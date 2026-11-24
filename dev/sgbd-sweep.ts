#!/usr/bin/env node
/**
 * SGBD sweep: which jobs that the job policy allows send something other than a read?
 *
 *   node --import ./dev/ts-resolve.mjs dev/sgbd-sweep.ts <folder of .prg and .grp files> [--json <out.json>]
 *
 * The policy decides by name, and the agent's description screen by what the SGBD says about
 * the job. Neither sees the telegrams a job sends. This reads the SGBD files themselves (copied
 * from C:\EDIABAS\ECU in the VM, both *.prg/*.PRG and *.grp/*.GRP; no cable, no EDIABAS),
 * decodes each job's code and names the KWP2000/UDS service of every request it sends or holds.
 * A job is reported under the ECU name the bridge would be given: the SGBD itself, and every
 * group (.grp) that can resolve to it, since EDIABAS runs a group name as the variant it
 * identifies. Run it after a policy change or a new SGBD set, and put every `write` it reports
 * in `deny.ecuJobs` (the suggestion it prints lists the SGBD and its groups).
 *
 * BEST2 object files: the header is plain; offset 0x88 holds the job table's offset; the table
 * is an int32 count, then 0x44-byte entries (name, int32 code offset), every byte XOR 0xF7, as
 * is the code and the info section (JOBNAME:/JOBCOMMENT: strings). A job's code runs from its
 * offset to the next job's (or the table). Instructions are an opcode, a mode byte (two 4-bit
 * operand modes) and two operands whose sizes follow from their modes, as EdiabasLib's
 * EdiabasNet.GetOpArg reads them; register numbers are EdiabasLib's register table.
 *
 * What it cannot see, so a clean result is not a proof: jumps are not followed (the replay runs
 * the code in order), so a request chosen by branching, or built from a table, a result or the
 * job's arguments, may be traced inexactly. To make up for it a job's kind is the worst of what
 * it sends and of every request-shaped constant in its code; when the worst comes only from a
 * constant the finding says so (check it by hand). DS2 telegrams (older modules) are not
 * classified. `withoutTelegram` lists the allowed jobs where no request was found at all.
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

/** A request seen at a send (`exact`: its format byte and service byte came from what the replay could follow). */
export interface SentTelegram extends Telegram {
  exact: boolean;
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
  return classifyAt(t).telegram;
}

/**
 * classifyService, and the index the service byte was read from. With `known` (one flag per
 * byte, from the replay), a byte the replay could not follow is never taken as the service.
 */
function classifyAt(t: ArrayLike<number>, known?: boolean[]): { telegram: Telegram; at: number } {
  const bytes = Array.from(t);
  const isKnown = (i: number) => known === undefined || known[i] === true;
  // Short form: the format byte's low six bits are the length. Otherwise a length byte follows
  // the header (always when those bits are 0; 0xB8 always; some SGBDs set both).
  const shortLen = bytes[0]! & 0x3f;
  const shortOk = shortLen !== 0 && shortLen === bytes.length - 3;
  const longOk = bytes[3] === bytes.length - 4;
  const lengthByteForm = !shortOk && (shortLen === 0 || bytes[0] === 0xb8 || longOk);
  const read = (at: number, lengthOk: boolean) => {
    const service = bytes[at] ?? -1;
    const svc = SERVICES[service];
    let kind: ServiceKind = svc?.kind ?? "other";
    // The default session (KWP 0x81, UDS 0x01) changes nothing.
    if (service === 0x10 && (bytes[at + 1] === 0x81 || bytes[at + 1] === 0x01)) kind = "read";
    return { telegram: { hex: hexOf(bytes), service, name: svc?.name ?? "unknown", kind, lengthOk }, at };
  };
  if (!lengthByteForm) return read(3, shortOk);
  if (longOk && isKnown(3)) return read(4, true);
  // A length byte that does not fit: either it is inserted later and the service follows the
  // header (80 12 F1 2C 10), or it is there and the job appends its bytes (B8 21 F1 03 17).
  // A length byte can equal a service ID (0x12, 0x21, ...), so take the worse known reading.
  const readings = [read(3, false), read(4, false)].filter((r) => isKnown(r.at) && SERVICES[r.telegram.service] !== undefined);
  if (readings.length === 0) return read(4, false);
  return readings.sort((a, b) => SEVERITY.indexOf(a.telegram.kind) - SEVERITY.indexOf(b.telegram.kind))[0]!;
}

/** A request from the tester: format byte with the top bit set, source 0xF1. */
const looksLikeRequest = (d: ArrayLike<number>) => d.length >= 4 && (d[0]! & 0x80) !== 0 && d[2] === 0xf1;

// ---- BEST2 code: operands, registers and a straight-line replay ----

/** Operand modes (EdiabasNet.OpAddrMode). */
const M = { None: 0, RegS: 1, RegAb: 2, RegI: 3, RegL: 4, Imm8: 5, Imm16: 6, Imm32: 7, ImmStr: 8, IdxImm: 9, IdxReg: 10, IdxRegImm: 11, IdxImmLenImm: 12, IdxImmLenReg: 13, IdxRegLenImm: 14, IdxRegLenReg: 15 } as const;

interface Operand {
  mode: number;
  /** Register (Reg* and the string of Idx* modes). */
  r?: number;
  /** Index register (IdxReg, IdxRegImm, IdxRegLen*). */
  ri?: number;
  /** Length register (Idx*LenReg). */
  rl?: number;
  /** Immediate value, index (IdxImm*), increment (IdxRegImm). */
  imm?: number;
  /** Length (Idx*LenImm). */
  len?: number;
  str?: number[];
}

/** One operand at `pos`, as EdiabasNet.GetOpArg reads it; null past the end of the code. */
function readOperand(code: Buffer, pos: number, mode: number): [Operand, number] | null {
  const need = (n: number) => pos + n <= code.length;
  switch (mode) {
    case M.None:
      return [{ mode }, pos];
    case M.RegS:
    case M.RegAb:
    case M.RegI:
    case M.RegL:
      return need(1) ? [{ mode, r: code[pos]! }, pos + 1] : null;
    case M.Imm8:
      return need(1) ? [{ mode, imm: code[pos]! }, pos + 1] : null;
    case M.Imm16:
      return need(2) ? [{ mode, imm: code.readUInt16LE(pos) }, pos + 2] : null;
    case M.Imm32:
      return need(4) ? [{ mode, imm: code.readUInt32LE(pos) }, pos + 4] : null;
    case M.ImmStr: {
      if (!need(2)) return null;
      const n = code.readInt16LE(pos);
      if (n < 0 || !need(2 + n)) return null;
      return [{ mode, str: Array.from(code.subarray(pos + 2, pos + 2 + n)) }, pos + 2 + n];
    }
    case M.IdxImm:
      return need(3) ? [{ mode, r: code[pos]!, imm: code.readUInt16LE(pos + 1) }, pos + 3] : null;
    case M.IdxReg:
      return need(2) ? [{ mode, r: code[pos]!, ri: code[pos + 1]! }, pos + 2] : null;
    case M.IdxRegImm:
      return need(4) ? [{ mode, r: code[pos]!, ri: code[pos + 1]!, imm: code.readUInt16LE(pos + 2) }, pos + 4] : null;
    case M.IdxImmLenImm:
      return need(5) ? [{ mode, r: code[pos]!, imm: code.readUInt16LE(pos + 1), len: code.readUInt16LE(pos + 3) }, pos + 5] : null;
    case M.IdxImmLenReg:
      return need(4) ? [{ mode, r: code[pos]!, imm: code.readUInt16LE(pos + 1), rl: code[pos + 3]! }, pos + 4] : null;
    case M.IdxRegLenImm:
      return need(4) ? [{ mode, r: code[pos]!, ri: code[pos + 1]!, len: code.readUInt16LE(pos + 2) }, pos + 4] : null;
    case M.IdxRegLenReg:
      return need(3) ? [{ mode, r: code[pos]!, ri: code[pos + 1]!, rl: code[pos + 2]! }, pos + 3] : null;
    default:
      return null;
  }
}

/** Opcodes 0x00-0x94 exist (EdiabasNet.OcList); anything above is not code. */
const LAST_OPCODE = 0x94;
const OP = { move: 0x00, clear: 0x01, subb: 0x03, adds: 0x04, push: 0x1e, pop: 0x1f, scat: 0x21, scut: 0x22, spaste: 0x24, serase: 0x25, xsend: 0x2a, xsendf: 0x2b, xraw: 0x75, xsendr: 0x84 } as const;
/** Opcodes that never change their first operand: compares, jumps, results, push, sends that only read it. */
const KEEPS_ARG0 = new Set([
  0x02, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x1c, 0x1d, 0x1e, 0x20,
  0x2b, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3f, 0x41, 0x47, 0x48, 0x4b, 0x4c, 0x4d, 0x5a, 0x5b, 0x5c, 0x5d, 0x5e,
  0x5f, 0x6a, 0x6b, 0x81, 0x82,
]);

/** String registers S0-S7 (0x1C-0x23) and S8-SF (0x2C-0x33). */
const isStr = (r: number) => (r >= 0x1c && r <= 0x23) || (r >= 0x2c && r <= 0x33);
/**
 * Byte offset and width of a byte/int/long register in the one 32-byte file they share (EdiabasLib):
 * B0-BF 00-0F, B10-B1F 80-8F (bytes 16-31); I0-I7 10-17, I8-IF 90-97; L0-L3 18-1B, L4-L7 98-9B.
 */
function numReg(r: number): { at: number; width: number } | null {
  if (r <= 0x0f) return { at: r, width: 1 };
  if (r >= 0x80 && r <= 0x8f) return { at: 16 + (r - 0x80), width: 1 };
  if (r >= 0x10 && r <= 0x17) return { at: (r - 0x10) * 2, width: 2 };
  if (r >= 0x90 && r <= 0x97) return { at: (8 + r - 0x90) * 2, width: 2 };
  if (r >= 0x18 && r <= 0x1b) return { at: (r - 0x18) * 4, width: 4 };
  if (r >= 0x98 && r <= 0x9b) return { at: (4 + r - 0x98) * 4, width: 4 };
  return null;
}

/** A string register's bytes, each with whether the replay could follow where it came from. */
interface Str {
  bytes: number[];
  known: boolean[];
}
const allKnown = (bytes: number[]): Str => ({ bytes, known: bytes.map(() => true) });
const noneKnown = (bytes: number[]): Str => ({ bytes, known: bytes.map(() => false) });

export interface JobAnalysis {
  /** The request at each send, in code order. */
  sent: SentTelegram[];
  /** Every request-shaped immediate string in the code, sent or not. */
  constants: Telegram[];
  /** Sends whose request is not request-shaped (DS2) or whose service byte the replay could not follow. */
  unresolved: number;
}

/**
 * Replays a job's code in order (jumps not followed) to see the request at each send: string
 * registers (each byte known or not), the shared byte/int/long register file, the stack,
 * `move`/`scat`/`clear` into whole or indexed strings, `spaste`/`serase`/`scut`, `adds`/`subb`
 * on known numbers, and the sends `xsend`/`xraw` (request in the second operand) and `xsendf`
 * (first). Anything else that may write a register makes it unknown. A send is `exact` when its
 * format byte and its service byte are known.
 */
export function analyseJob(code: Buffer): JobAnalysis {
  const file = new Uint8Array(32);
  const known = new Uint8Array(32);
  const S = new Map<number, Str>();
  const stack: (number | null)[] = [];
  const out: JobAnalysis = { sent: [], constants: [], unresolved: 0 };

  const getNum = (r: number): number | null => {
    const n = numReg(r);
    if (!n) return null;
    let v = 0;
    for (let k = n.width - 1; k >= 0; k--) {
      if (!known[n.at + k]) return null;
      v = v * 256 + file[n.at + k]!;
    }
    return v;
  };
  const setNum = (r: number, v: number | null) => {
    const n = numReg(r);
    if (!n) return;
    for (let k = 0; k < n.width; k++) {
      file[n.at + k] = v === null ? 0 : Math.floor(v / 256 ** k) & 0xff;
      known[n.at + k] = v === null ? 0 : 1;
    }
  };
  const getStr = (r: number): Str => S.get(r) ?? { bytes: [], known: [] };
  const copy = (x: Str): Str => ({ bytes: [...x.bytes], known: [...x.known] });
  const taint = (r: number) => {
    if (isStr(r)) S.set(r, noneKnown(getStr(r).bytes));
    else setNum(r, null);
  };
  /** Index and length of an indexed operand; null when a register they come from is unknown. */
  const span = (o: Operand): { idx: number; len: number | null } | null => {
    let idx: number | null = o.imm ?? 0;
    if (o.ri !== undefined) {
      const base = getNum(o.ri);
      idx = base === null ? null : base + (o.mode === M.IdxRegImm ? (o.imm ?? 0) : 0);
    }
    let len: number | null = o.len ?? null;
    if (o.rl !== undefined) len = getNum(o.rl);
    if (idx === null || (o.rl !== undefined && len === null)) return null;
    return { idx, len: o.mode === M.IdxImm || o.mode === M.IdxReg || o.mode === M.IdxRegImm ? 1 : len };
  };
  /** The bytes an operand gives as a source. */
  const value = (o: Operand): Str => {
    switch (o.mode) {
      case M.ImmStr:
        return allKnown(o.str!);
      case M.RegS:
        return copy(getStr(o.r!));
      case M.RegAb:
      case M.RegI:
      case M.RegL: {
        const n = numReg(o.r!);
        if (!n) return { bytes: [], known: [] };
        const v = getNum(o.r!);
        const bytes = Array.from({ length: n.width }, (_, k) => (v === null ? 0 : Math.floor(v / 256 ** k) & 0xff));
        return v === null ? noneKnown(bytes) : allKnown(bytes);
      }
      case M.Imm8:
        return allKnown([o.imm! & 0xff]);
      case M.Imm16:
        return allKnown([o.imm! & 0xff, (o.imm! >> 8) & 0xff]);
      case M.Imm32:
        return allKnown([0, 1, 2, 3].map((k) => Math.floor(o.imm! / 256 ** k) & 0xff));
      default: {
        if (o.r === undefined || !isStr(o.r)) return { bytes: [], known: [] };
        const src = getStr(o.r);
        const sp = span(o);
        if (!sp) return noneKnown([0]);
        const end = sp.len === null ? src.bytes.length : sp.idx + sp.len;
        const bytes: number[] = [];
        const kn: boolean[] = [];
        for (let k = sp.idx; k < end; k++) {
          bytes.push(src.bytes[k] ?? 0);
          kn.push(src.known[k] ?? false);
        }
        return { bytes, known: kn };
      }
    }
  };
  const num = (o: Operand): number | null => {
    if (o.mode === M.RegAb || o.mode === M.RegI || o.mode === M.RegL) return getNum(o.r!);
    if (o.mode === M.Imm8 || o.mode === M.Imm16 || o.mode === M.Imm32) return o.imm!;
    const v = value(o);
    if (v.bytes.length === 0 || v.bytes.length > 4 || v.known.some((k) => !k)) return null;
    return v.bytes.reduceRight((acc, b) => acc * 256 + b, 0);
  };
  /** Writes `v` into the first operand: a whole register or bytes of an indexed string. */
  const store = (dst: Operand, v: Str) => {
    if (dst.mode === M.RegS) {
      S.set(dst.r!, copy(v));
    } else if (dst.mode === M.RegAb || dst.mode === M.RegI || dst.mode === M.RegL) {
      const ok = v.bytes.length > 0 && v.known.slice(0, 4).every((k) => k);
      setNum(dst.r!, ok ? v.bytes.slice(0, 4).reduceRight((acc, b) => acc * 256 + b, 0) : null);
    } else if (dst.mode >= M.IdxImm && dst.r !== undefined && isStr(dst.r)) {
      const t = copy(getStr(dst.r));
      const sp = span(dst);
      if (!sp || sp.idx > 4096) {
        S.set(dst.r, noneKnown(t.bytes));
        return;
      }
      const n = sp.len === null ? v.bytes.length : Math.min(sp.len, v.bytes.length);
      while (t.bytes.length < sp.idx) {
        t.bytes.push(0);
        t.known.push(false);
      }
      for (let k = 0; k < n; k++) {
        t.bytes[sp.idx + k] = v.bytes[k]!;
        t.known[sp.idx + k] = v.known[k]!;
      }
      S.set(dst.r, t);
    }
  };
  /** spaste / serase: insert at or remove from the index of an indexed string operand. */
  const edit = (dst: Operand, f: (t: Str, idx: number) => Str) => {
    if (dst.r === undefined || !isStr(dst.r)) return;
    const t = copy(getStr(dst.r));
    const sp = span(dst);
    S.set(dst.r, sp ? f(t, Math.min(sp.idx, t.bytes.length)) : noneKnown(t.bytes));
  };
  const send = (req: Operand) => {
    const v = value(req);
    if (!looksLikeRequest(v.bytes)) {
      out.unresolved++;
      return;
    }
    const { telegram, at } = classifyAt(v.bytes, v.known);
    // A service byte the replay could not follow says nothing about the request: count it as
    // unresolved and leave the job to its constants (or to the unresolved list).
    if (v.known[at] !== true) {
      out.unresolved++;
      return;
    }
    out.sent.push({ ...telegram, exact: v.known[0] === true });
  };

  for (let pc = 0; pc + 2 <= code.length; ) {
    const op = code[pc]!;
    if (op > LAST_OPCODE) break;
    const a0 = readOperand(code, pc + 2, code[pc + 1]! >> 4);
    if (!a0) break;
    const a1 = readOperand(code, a0[1], code[pc + 1]! & 0x0f);
    if (!a1) break;
    pc = a1[1];
    const [x, y] = [a0[0], a1[0]];
    for (const o of [x, y]) if (o.mode === M.ImmStr && looksLikeRequest(o.str!)) out.constants.push(classifyService(o.str!));

    if (op === OP.move) store(x, value(y));
    else if (op === OP.clear) {
      if (x.mode === M.RegS) S.set(x.r!, { bytes: [], known: [] });
      else if (x.r !== undefined) setNum(x.r, 0);
    } else if (op === OP.scat && x.mode === M.RegS) {
      const t = getStr(x.r!);
      const v = value(y);
      S.set(x.r!, { bytes: [...t.bytes, ...v.bytes], known: [...t.known, ...v.known] });
    } else if (op === OP.spaste) {
      const v = value(y);
      edit(x, (t, i) => ({ bytes: [...t.bytes.slice(0, i), ...v.bytes, ...t.bytes.slice(i)], known: [...t.known.slice(0, i), ...v.known, ...t.known.slice(i)] }));
    } else if (op === OP.serase) {
      const n = num(y);
      edit(x, (t, i) => (n === null ? noneKnown(t.bytes) : { bytes: [...t.bytes.slice(0, i), ...t.bytes.slice(i + n)], known: [...t.known.slice(0, i), ...t.known.slice(i + n)] }));
    } else if (op === OP.scut && x.mode === M.RegS) {
      const n = num(y);
      const t = getStr(x.r!);
      S.set(x.r!, n === null ? noneKnown(t.bytes) : { bytes: t.bytes.slice(0, Math.max(0, t.bytes.length - n)), known: t.known.slice(0, Math.max(0, t.bytes.length - n)) });
    } else if ((op === OP.adds || op === OP.subb) && x.r !== undefined && numReg(x.r) && x.mode !== M.RegS) {
      const a = getNum(x.r);
      const b = num(y);
      const size = 256 ** numReg(x.r)!.width;
      setNum(x.r, a === null || b === null ? null : ((((op === OP.adds ? a + b : a - b) % size) + size) % size));
    } else if (op === OP.push) stack.push(num(x));
    else if (op === OP.pop && x.r !== undefined) setNum(x.r, stack.pop() ?? null);
    else if (op === OP.xsend || op === OP.xraw) {
      send(y);
      if (x.r !== undefined) taint(x.r);
    } else if (op === OP.xsendf) send(x);
    else if (op === OP.xsendr) out.unresolved++;
    else if (!KEEPS_ARG0.has(op) && x.r !== undefined) taint(x.r);
  }
  return out;
}

/** The requests at each send (see analyseJob). */
export function traceSends(code: Buffer): { sent: SentTelegram[]; unresolved: number } {
  const a = analyseJob(code);
  return { sent: a.sent, unresolved: a.unresolved };
}

/** Every request-shaped immediate string in the code (see analyseJob). */
export function findTelegrams(code: Buffer): Telegram[] {
  return analyseJob(code).constants;
}

// ---- groups ----

const NAME_TOKEN = /[A-Za-z0-9_]{2,64}/g;

/**
 * For each SGBD (upper case), the group files (upper case, without extension) that can resolve
 * to it: its group in T_GRTB's ZuordnungsTabelle (rows `ADR ---- VARDIAG`, SGBD, GRUPPE), the
 * address group d_00<ADR> for each address it sits at, and any group file whose own tables name
 * it. Ers on the side of more groups.
 */
export function groupsBySgbd(folder: string): Map<string, Set<string>> {
  const files = readdirSync(folder);
  const groups = new Map(files.filter((f) => /\.grp$/i.test(f)).map((f) => [basename(f, f.slice(-4)).toUpperCase(), f]));
  const out = new Map<string, Set<string>>();
  const add = (sgbd: string, group: string) => {
    if (!groups.has(group)) return;
    const set = out.get(sgbd) ?? new Set<string>();
    set.add(group);
    out.set(sgbd, set);
  };
  const grtb = files.find((f) => /^t_grtb\.prg$/i.test(f));
  if (grtb) {
    const text = unxor(readFileSync(join(folder, grtb))).toString("latin1");
    for (const m of text.matchAll(/([0-9A-F]{2}) ---- [0-9A-F]{4}\0([^\0]+)\0([^\0]+)\0/g)) {
      const sgbd = m[2]!.toUpperCase();
      add(sgbd, m[3]!.toUpperCase());
      add(sgbd, `D_00${m[1]!}`);
    }
  }
  for (const [group, f] of groups) {
    const text = unxor(readFileSync(join(folder, f))).toString("latin1");
    for (const m of text.matchAll(NAME_TOKEN)) add(m[0].toUpperCase(), group);
  }
  // A group's tables also name groups and other words; only SGBD names are looked up later.
  return out;
}

// ---- the sweep ----

export interface Finding {
  /** The name the bridge would be given: the SGBD, or a group that can resolve to it. */
  ecu: string;
  /** The SGBD whose code runs (the same as `ecu` unless `ecu` is a group). */
  sgbd: string;
  job: string;
  rule: string;
  /** The worst kind among the requests it sends and the request-shaped constants in its code. */
  kind: ServiceKind;
  /** True when a request of that kind was seen at a send, exactly; false: check by hand. */
  confirmed: boolean;
  comment: string | null;
  sent: SentTelegram[];
  constants: Telegram[];
  unresolved: number;
}

export interface SweepReport {
  files: number;
  groups: number;
  allowedJobs: number;
  findings: Finding[];
  withoutTelegram: { ecu: string; job: string; rule: string }[];
  /** Allowed jobs that send something the replay could not follow and hold no request constant: check by hand. */
  unresolvedOnly: { ecu: string; job: string; rule: string; unresolved: number }[];
  unreadable: { file: string; reason: string }[];
}

const worst = (ts: Telegram[]): ServiceKind | null => SEVERITY.find((k) => ts.some((t) => t.kind === k)) ?? null;

export function sweepFolder(folder: string, guard: PolicyGuard): SweepReport {
  const groups = groupsBySgbd(folder);
  const report: SweepReport = { files: 0, groups: new Set([...groups.values()].flatMap((s) => [...s])).size, allowedJobs: 0, findings: [], withoutTelegram: [], unresolvedOnly: [], unreadable: [] };
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
    const sgbd = basename(file, file.slice(-4)).toUpperCase();
    const names = [sgbd, ...[...(groups.get(sgbd) ?? [])].sort()];
    for (const j of jobs) {
      const job = j.name.toUpperCase();
      const verdicts = names.map((ecu) => ({ ecu, v: guard.evaluate({ ecu, job: j.name }) })).filter((x) => x.v.allowed);
      if (verdicts.length === 0) continue;
      if (verdicts[0]!.ecu === sgbd) report.allowedJobs++;
      const a = analyseJob(j.code);
      const kind = worst([...a.sent, ...a.constants]);
      if (kind === null) {
        if (verdicts[0]!.ecu !== sgbd) continue;
        if (a.unresolved > 0) report.unresolvedOnly.push({ ecu: sgbd, job, rule: verdicts[0]!.v.rule, unresolved: a.unresolved });
        else report.withoutTelegram.push({ ecu: sgbd, job, rule: verdicts[0]!.v.rule });
        continue;
      }
      if (kind === "read") continue;
      const confirmed = a.sent.some((t) => t.kind === kind && t.exact);
      for (const { ecu, v } of verdicts) {
        report.findings.push({ ecu, sgbd, job, rule: v.rule, kind, confirmed, comment: j.comment, sent: a.sent, constants: a.constants, unresolved: a.unresolved });
      }
    }
  }
  return report;
}

function printReport(r: SweepReport): void {
  console.log(`${r.files} SGBD files, ${r.groups} group files that resolve to them, ${r.allowedJobs} policy-allowed jobs.`);
  for (const kind of SEVERITY.filter((k) => k !== "read")) {
    const list = r.findings.filter((f) => f.kind === kind);
    console.log(`\n## ${kind}: ${list.length} job(s)`);
    for (const f of list) {
      const via = f.ecu === f.sgbd ? "" : ` (group, runs ${f.sgbd})`;
      const check = f.confirmed ? "" : " NOT CONFIRMED AT A SEND: check by hand";
      const sends = f.sent.map((t) => `${t.hex} (${t.name})`).join("; ") || "(none traced)";
      console.log(`- ${f.ecu}${via} ${f.job} [${f.rule}]${check} "${f.comment ?? "(no description)"}"\n    sends: ${sends}`);
      if (!f.confirmed) console.log(`    constants: ${f.constants.map((t) => `${t.hex} (${t.name})`).join("; ")}`);
    }
  }
  const writes = r.findings.filter((f) => f.kind === "write");
  if (writes.length > 0) {
    const map: Record<string, string[]> = {};
    for (const f of writes) map[f.ecu] = [...new Set([...(map[f.ecu] ?? []), f.job])].sort();
    console.log(`\nSuggested deny.ecuJobs entries (check the unconfirmed ones first):\n${JSON.stringify(map, null, 1)}`);
  }
  console.log(`\n${r.unresolvedOnly.length} allowed job(s) send requests the replay could not follow and hold no request constant: check by hand (see --json).`);
  console.log(`${r.withoutTelegram.length} allowed job(s) send nothing the sweep can see (interface, info and DS2 jobs; see --json).`);
  for (const u of r.unreadable) console.log(`unreadable: ${u.file}: ${u.reason}`);
}

if (import.meta.main) {
  const [folder, flag, out] = process.argv.slice(2);
  if (!folder || (flag !== undefined && (flag !== "--json" || !out))) {
    console.error("usage: node --import ./dev/ts-resolve.mjs dev/sgbd-sweep.ts <folder of .prg and .grp files> [--json <out.json>]");
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
