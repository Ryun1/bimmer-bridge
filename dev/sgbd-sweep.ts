#!/usr/bin/env node
/**
 * SGBD sweep: which jobs that the job policy allows send something other than a read?
 *
 *   node --import ./dev/ts-resolve.mjs dev/sgbd-sweep.ts <folder of .prg files> [--json <out.json>]
 *
 * The policy decides by name, and the agent's description screen by what the SGBD says about
 * the job. Neither sees the telegrams a job sends. This reads the SGBD files themselves (copied
 * from C:\EDIABAS\ECU in the VM; no cable, no EDIABAS), finds every KWP2000/UDS request each
 * policy-allowed job holds as a constant, and names its service. Run it after a policy change
 * or a new SGBD set, and put every job it reports as `write` in `deny.ecuJobs`.
 *
 * BEST2 object files: the header is plain; offset 0x88 holds the job table's offset; the table
 * is an int32 count, then 0x44-byte entries (name, int32 code offset), every byte XOR 0xF7, as
 * is the code and the info section (JOBNAME:/JOBCOMMENT: strings). A job's code runs from its
 * offset to the next job's (or the table). Requests are found as immediate strings moved or
 * concatenated into a string register (`move`/`scat` Sx, "<bytes>") that look like a request
 * from the tester: format byte 0x80-0xFF, source 0xF1.
 *
 * What it cannot see, so a clean result is not a proof: a request assembled byte by byte or
 * from a table or the job's arguments, and DS2 telegrams (older modules). `withoutTelegram`
 * lists the allowed jobs where no request was found at all; most are interface or info jobs.
 * Node 24+ runs this file directly: erasable TypeScript only.
 */
import { readdirSync, readFileSync, writeFileSync } from "node:fs";
import { basename, join } from "node:path";
import { PolicyGuard } from "../mac-mcp/src/policy.ts";

/**
 * write: changes stored data or the ECU's state (write, clear, reset, security, download).
 * control: input/output control and routines. define: a dynamic read definition ($2C), which
 * sets what a later $21 reads and lasts until the session ends. session: a diagnostic session
 * other than the default.
 */
export type ServiceKind = "read" | "write" | "control" | "define" | "session" | "other";

/** KWP2000 (ISO 14230-3, BMW) and UDS (ISO 14229) request services. */
const SERVICES: Record<number, { name: string; kind: ServiceKind }> = {
  0x10: { name: "StartDiagnosticSession", kind: "session" },
  0x11: { name: "ECUReset", kind: "write" },
  0x12: { name: "ReadFreezeFrameData", kind: "read" },
  0x13: { name: "ReadDiagnosticTroubleCodes", kind: "read" },
  0x14: { name: "ClearDiagnosticInformation", kind: "write" },
  0x17: { name: "ReadStatusOfDiagnosticTroubleCodes", kind: "read" },
  0x18: { name: "ReadDiagnosticTroubleCodesByStatus", kind: "read" },
  0x19: { name: "ReadDTCInformation", kind: "read" },
  0x1a: { name: "ReadECUIdentification", kind: "read" },
  0x20: { name: "StopDiagnosticSession", kind: "session" },
  0x21: { name: "ReadDataByLocalIdentifier", kind: "read" },
  0x22: { name: "ReadDataByCommonIdentifier", kind: "read" },
  0x23: { name: "ReadMemoryByAddress", kind: "read" },
  0x27: { name: "SecurityAccess", kind: "write" },
  0x28: { name: "DisableNormalMessageTransmission", kind: "write" },
  0x29: { name: "EnableNormalMessageTransmission", kind: "write" },
  0x2c: { name: "DynamicallyDefineLocalIdentifier", kind: "define" },
  0x2e: { name: "WriteDataByCommonIdentifier", kind: "write" },
  0x2f: { name: "InputOutputControlByCommonIdentifier", kind: "control" },
  0x30: { name: "InputOutputControlByLocalIdentifier", kind: "control" },
  0x31: { name: "StartRoutineByLocalIdentifier", kind: "control" },
  0x32: { name: "StopRoutineByLocalIdentifier", kind: "control" },
  0x33: { name: "RequestRoutineResultsByLocalIdentifier", kind: "control" },
  0x34: { name: "RequestDownload", kind: "write" },
  0x35: { name: "RequestUpload", kind: "write" },
  0x36: { name: "TransferData", kind: "write" },
  0x37: { name: "RequestTransferExit", kind: "write" },
  0x38: { name: "StartRoutineByAddress", kind: "control" },
  0x39: { name: "StopRoutineByAddress", kind: "control" },
  0x3a: { name: "RequestRoutineResultsByAddress", kind: "control" },
  0x3b: { name: "WriteDataByLocalIdentifier", kind: "write" },
  0x3d: { name: "WriteMemoryByAddress", kind: "write" },
  0x3e: { name: "TesterPresent", kind: "read" },
  0x83: { name: "AccessTimingParameters", kind: "session" },
  0x85: { name: "ControlDTCSetting", kind: "write" },
  0x86: { name: "ResponseOnEvent", kind: "write" },
  0x87: { name: "LinkControl", kind: "session" },
};

/** Worst first: a job's kind is the worst kind among its requests. */
const SEVERITY: ServiceKind[] = ["write", "control", "session", "define", "other", "read"];

export interface Telegram {
  hex: string;
  service: number;
  name: string;
  kind: ServiceKind;
  /** The length in the header matches the bytes present; false when the job appends the rest. */
  lengthOk: boolean;
}

export interface PrgJob {
  name: string;
  code: Buffer;
  comment: string | null;
}

const XOR = 0xf7;
const ENTRY = 0x44;

const unxor = (b: Buffer) => Buffer.from(b.map((v) => v ^ XOR));
const hexOf = (bytes: ArrayLike<number>) =>
  Array.from(bytes, (v) => v.toString(16).toUpperCase().padStart(2, "0")).join(" ");

/** The jobs of one SGBD file, with their code and description. Throws on anything else. */
export function parsePrg(buf: Buffer): { jobs: PrgJob[] } {
  if (buf.length < 0xa0 || buf.toString("latin1", 0, 15) !== "@EDIABAS OBJECT") {
    throw new Error("not an EDIABAS object file");
  }
  const tableOffset = buf.readInt32LE(0x88);
  if (tableOffset < 0xa0 || tableOffset + 4 > buf.length) throw new Error(`job table offset ${tableOffset} is outside the file`);
  const count = buf.readInt32LE(tableOffset);
  if (count < 0 || tableOffset + 4 + count * ENTRY > buf.length) throw new Error(`job count ${count} does not fit the file`);

  const entries: { name: string; start: number }[] = [];
  for (let i = 0; i < count; i++) {
    const e = unxor(buf.subarray(tableOffset + 4 + i * ENTRY, tableOffset + 4 + (i + 1) * ENTRY));
    const nul = e.indexOf(0);
    const name = e.toString("latin1", 0, nul < 0 || nul > 0x40 ? 0x40 : nul);
    const start = e.readInt32LE(0x40);
    if (!/^[A-Za-z0-9_]{1,64}$/.test(name) || start < 0xa0 || start >= tableOffset) {
      throw new Error(`job table entry ${i} is not a job (name '${name}', offset ${start})`);
    }
    entries.push({ name, start });
  }

  const starts = [...new Set(entries.map((e) => e.start))].sort((a, b) => a - b);
  const info = unxor(buf.subarray(tableOffset + 4 + count * ENTRY)).toString("latin1");
  const comments = readComments(info);

  return {
    jobs: entries.map(({ name, start }) => {
      const end = starts.find((s) => s > start) ?? tableOffset;
      return { name, code: unxor(buf.subarray(start, end)), comment: comments.get(name.toUpperCase()) ?? null };
    }),
  };
}

/** JOBNAME:X followed by its JOBCOMMENT: lines (one item per line; several are joined with " / "). */
function readComments(info: string): Map<string, string> {
  const out = new Map<string, string>();
  let job: string | null = null;
  for (const s of info.split(/[\0\r\n]/)) {
    if (s.startsWith("JOBNAME:")) {
      job = s.slice(8).toUpperCase();
    } else if (s.startsWith("JOBCOMMENT:") && job !== null) {
      const prev = out.get(job);
      out.set(job, prev === undefined ? s.slice(11) : `${prev} / ${s.slice(11)}`);
    }
  }
  return out;
}

/** The service of one request, and what kind of service it is. */
export function classifyService(t: ArrayLike<number>): Telegram {
  const bytes = Array.from(t);
  // Short form: the format byte's low six bits are the length. Otherwise a length byte follows
  // the header (always when those bits are 0; some SGBDs set both).
  const shortLen = bytes[0]! & 0x3f;
  const shortOk = shortLen !== 0 && shortLen === bytes.length - 3;
  const longOk = bytes[3] === bytes.length - 4;
  // 0xB8 always has a length byte. A length byte that does not fit means the job builds the
  // rest: if a known service follows the header, the length byte is inserted later (80 12 F1
  // 2C 10); else the length byte is there and its bytes are appended (B8 21 F1 03 17).
  const lengthByteForm = !shortOk && (shortLen === 0 || bytes[0] === 0xb8 || longOk);
  const sidAt = !lengthByteForm ? 3 : longOk || SERVICES[bytes[3]!] === undefined ? 4 : 3;
  const lengthOk = sidAt === 3 ? shortOk : longOk;
  const service = bytes[sidAt] ?? -1;
  const known = SERVICES[service];
  let kind: ServiceKind = known?.kind ?? "other";
  // The default session (KWP 0x81, UDS 0x01) changes nothing.
  if (service === 0x10 && (bytes[sidAt + 1] === 0x81 || bytes[sidAt + 1] === 0x01)) kind = "read";
  return { hex: hexOf(bytes), service, name: known?.name ?? "unknown", kind, lengthOk };
}

/**
 * Every request a job's code holds as an immediate string: `move` (00) or `scat` (21) with an
 * immediate string as the second operand (mode low nibble 8), shaped as a request from the
 * tester (format byte with the top bit set, source 0xF1).
 */
export function findTelegrams(code: Buffer): Telegram[] {
  const out: Telegram[] = [];
  for (let i = 0; i + 5 <= code.length; i++) {
    const op = code[i]!;
    if ((op !== 0x00 && op !== 0x21) || (code[i + 1]! & 0x0f) !== 0x08) continue;
    const len = code.readUInt16LE(i + 3);
    if (len < 4 || len > 255 || i + 5 + len > code.length) continue;
    const data = code.subarray(i + 5, i + 5 + len);
    if ((data[0]! & 0x80) === 0 || data[2] !== 0xf1) continue;
    if ((data[0]! & 0x3f) === 0 && len < 5) continue;
    out.push(classifyService(data));
    i += 4 + len;
  }
  return out;
}

/** A request seen at a send, in code order. */
const looksLikeRequest = (d: ArrayLike<number>) => d.length >= 4 && (d[0]! & 0x80) !== 0 && d[2] === 0xf1;

/**
 * The requests a job sends, by replaying its code in order without following jumps: string
 * registers set from constants (`move`/`scat` Sx, "<bytes>" or Sy), the byte register file
 * (B0-BF, overlapped by I0-I7 and L0-L3) set from constants and the stack, bytes written into a
 * string register by index (`move Sx[L1], B0`: how jobs put the ECU's address into a template),
 * and at each `xsend` (2A, mode 11: response, request) the request register as it then is.
 * `unresolved` counts sends whose request was not built from what the replay understood.
 * A job that picks its request by branching or from a table or its arguments is not seen
 * exactly; the constants (findTelegrams) remain for those.
 */
export function traceSends(code: Buffer): { sent: Telegram[]; unresolved: number } {
  const S = new Map<number, number[]>();
  const B = new Uint8Array(16);
  const stack: number[] = [];
  const sent: Telegram[] = [];
  let unresolved = 0;
  // Register codes: 00-0F B0-BF, 10-17 I0-I7, 18-1B L0-L3 (one 16-byte file); string registers above.
  const width = (r: number) => (r < 0x10 ? 1 : r < 0x18 ? 2 : r < 0x1c ? 4 : 0);
  const at = (r: number) => (r < 0x10 ? r : r < 0x18 ? (r - 0x10) * 2 : (r - 0x18) * 4);
  const get = (r: number) => {
    let v = 0;
    for (let k = width(r) - 1; k >= 0; k--) v = v * 256 + B[at(r) + k]!;
    return v;
  };
  const set = (r: number, v: number) => {
    for (let k = 0; k < width(r); k++) B[at(r) + k] = Math.floor(v / 256 ** k) & 0xff;
  };
  const num = (r: number) => width(r) > 0;
  const str = (r: number) => r >= 0x1c;

  for (let i = 0; i < code.length; ) {
    const op = code[i]!;
    const mode = code[i + 1] ?? 0;
    const a = code[i + 2] ?? 0;
    const hi = mode >> 4;
    const lo = mode & 0x0f;
    // move/scat Sx, "<bytes>"
    if ((op === 0x00 || op === 0x21) && mode === 0x18 && str(a) && i + 5 <= code.length) {
      const len = code.readUInt16LE(i + 3);
      if (i + 5 + len <= code.length) {
        const data = Array.from(code.subarray(i + 5, i + 5 + len));
        S.set(a, op === 0x00 ? data : [...(S.get(a) ?? []), ...data]);
        i += 5 + len;
        continue;
      }
    }
    // move/scat Sx, Sy
    if ((op === 0x00 || op === 0x21) && mode === 0x11 && str(a) && str(code[i + 3] ?? 0)) {
      const from = S.get(code[i + 3]!) ?? [];
      S.set(a, op === 0x00 ? [...from] : [...(S.get(a) ?? []), ...from]);
      i += 4;
      continue;
    }
    // move <B/I/L>, imm8/16/32
    if (op === 0x00 && (hi === 2 || hi === 3 || hi === 4) && (lo === 5 || lo === 6 || lo === 7) && num(a)) {
      const n = lo === 5 ? 1 : lo === 6 ? 2 : 4;
      if (i + 3 + n <= code.length) {
        set(a, n === 1 ? code[i + 3]! : n === 2 ? code.readUInt16LE(i + 3) : code.readUInt32LE(i + 3));
        i += 3 + n;
        continue;
      }
    }
    // push / pop <B/I/L>
    if ((op === 0x1e || op === 0x1f) && (hi === 2 || hi === 3 || hi === 4) && lo === 0 && num(a)) {
      if (op === 0x1e) stack.push(get(a));
      else set(a, stack.pop() ?? 0);
      i += 3;
      continue;
    }
    // move Sx[<index register>], <byte register or imm8>
    if (op === 0x00 && hi === 0xa && (lo === 2 || lo === 5) && str(a) && num(code[i + 3] ?? 0)) {
      const idx = get(code[i + 3]!);
      const value = lo === 5 ? code[i + 4]! : get(code[i + 4]!) & 0xff;
      const t = S.get(a) ?? [];
      while (t.length <= idx && idx < 4096) t.push(0);
      if (idx < 4096) t[idx] = value;
      S.set(a, t);
      i += 5;
      continue;
    }
    // xsend Sresponse, Srequest
    if (op === 0x2a && mode === 0x11 && str(a) && str(code[i + 3] ?? 0)) {
      const req = S.get(code[i + 3]!);
      if (req && looksLikeRequest(req)) sent.push(classifyService(req));
      else unresolved++;
      i += 4;
      continue;
    }
    i++;
  }
  return { sent, unresolved };
}

export interface Finding {
  ecu: string;
  job: string;
  rule: string;
  kind: ServiceKind;
  /** True when `telegrams` are the requests traced at each send; false when they are only the
   * constants in the job's code (no send could be traced): check those by hand. */
  traced: boolean;
  comment: string | null;
  telegrams: Telegram[];
}

export interface SweepReport {
  files: number;
  allowedJobs: number;
  findings: Finding[];
  withoutTelegram: { ecu: string; job: string; rule: string }[];
  unreadable: { file: string; reason: string }[];
}

export function sweepFolder(folder: string, guard: PolicyGuard): SweepReport {
  const report: SweepReport = { files: 0, allowedJobs: 0, findings: [], withoutTelegram: [], unreadable: [] };
  const files = readdirSync(folder)
    .filter((f) => /\.prg$/i.test(f))
    .sort();
  for (const file of files) {
    let jobs: PrgJob[];
    try {
      jobs = parsePrg(readFileSync(join(folder, file))).jobs;
    } catch (e) {
      report.unreadable.push({ file, reason: e instanceof Error ? e.message : String(e) });
      continue;
    }
    report.files++;
    const ecu = basename(file, file.slice(-4)).toUpperCase();
    for (const j of jobs) {
      const v = guard.evaluate({ ecu, job: j.name });
      if (!v.allowed) continue;
      report.allowedJobs++;
      const job = j.name.toUpperCase();
      const trace = traceSends(j.code);
      const traced = trace.sent.length > 0;
      const telegrams = traced ? trace.sent : findTelegrams(j.code);
      if (telegrams.length === 0) {
        report.withoutTelegram.push({ ecu, job, rule: v.rule });
        continue;
      }
      const kind = SEVERITY.find((k) => telegrams.some((t) => t.kind === k))!;
      if (kind !== "read") report.findings.push({ ecu, job, rule: v.rule, kind, traced, comment: j.comment, telegrams });
    }
  }
  return report;
}

function printReport(r: SweepReport): void {
  console.log(`${r.files} SGBD files, ${r.allowedJobs} policy-allowed jobs.`);
  for (const kind of SEVERITY.filter((k) => k !== "read")) {
    const list = r.findings.filter((f) => f.kind === kind);
    console.log(`\n## ${kind}: ${list.length} job(s)`);
    for (const f of list) {
      const sends = f.telegrams.map((t) => `${t.hex} (${t.name})`).join("; ");
      const traced = f.traced ? "" : " (constants only, no send traced: check by hand)";
      console.log(`- ${f.ecu} ${f.job} [${f.rule}]${traced} "${f.comment ?? "(no description)"}"\n    sends: ${sends}`);
    }
  }
  console.log(`\n${r.withoutTelegram.length} allowed job(s) hold no request the sweep can read (see --json).`);
  for (const u of r.unreadable) console.log(`unreadable: ${u.file}: ${u.reason}`);
}

if (import.meta.main) {
  const [folder, flag, out] = process.argv.slice(2);
  if (!folder || (flag !== undefined && (flag !== "--json" || !out))) {
    console.error("usage: node --import ./dev/ts-resolve.mjs dev/sgbd-sweep.ts <folder of .prg files> [--json <out.json>]");
    process.exit(2);
  }
  const guard = PolicyGuard.load();
  if (guard.error) {
    console.error(`policy: ${guard.error}`);
    process.exit(1);
  }
  const report = sweepFolder(folder, guard);
  printReport(report);
  if (out) writeFileSync(out, JSON.stringify(report, null, 1) + "\n");
}
