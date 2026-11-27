/**
 * Telegrams from EDIABAS's IFH trace (the VM, IfhTrace = 3) and from libkline's recordings (the
 * native engine), compared as telegrams, and the echo and reply times a recording shows.
 * The CLI is telegrams.ts.
 *
 * IFH trace (C:\EDIABAS\TRACE\ifh.trc): CRLF lines like `_STD     [   16] SENDECUTEL` (a call),
 * `_STD     [   16]  INPUT: 82 12 F1 1A 80 ` (its input; no checksum), `…  ERROR: IFH_0003` or
 * `ERROR: NO ERROR`, then `…  OUTPUT: --` (no reply) or the reply's bytes. Driver lines
 * (`   I -> D:`, `   D -> I:`) are indented one more. A value that wraps continues on lines with
 * no `[n]`. Tab-indented `#@n;…#` lines are markers and are skipped.
 *
 * Recording (kline/src/recording.zig): JSON lines `{"t_us","op",…}`; a `send` starts an
 * exchange, the K-line echoes it back (the first bytes received after it), and what follows up
 * to the next send is the reply.
 */

export interface Exchange {
  request: number[];
  response: number[] | null;
  error: string | null;
  /** Recording only: the send's time (µs since open). */
  tUs?: number;
  /** Recording only: from the send to the end of its echo. */
  echoUs?: number;
  /** Recording only: from the end of the echo to the first reply byte. */
  replyUs?: number;
}

export interface Stats {
  n: number;
  minMs: number;
  medianMs: number;
  maxMs: number;
}

const ENTRY = /^\S+\s+\[\s*\d+\]( +)(.*)$/;
const CONTINUATION = /^\S+\s+((?:[0-9A-Fa-f]{2}\s*)+)$/;

function hexBytes(text: string): number[] {
  const t = text.trim();
  if (t === "" || t === "--") return [];
  return t.split(/\s+/).map((b) => {
    if (!/^[0-9A-Fa-f]{2}$/.test(b)) throw new Error(`not a hex byte: ${b}`);
    return Number.parseInt(b, 16);
  });
}

/** One exchange per SENDECUTEL block, in trace order. */
export function parseIfhTrace(text: string): Exchange[] {
  const out: Exchange[] = [];
  let current: { request: number[]; output: number[] | null; error: string | null } | null = null;
  // Which field a continuation line extends.
  let field: "input" | "output" | "other" = "other";
  const flush = () => {
    if (current) out.push({ request: current.request, response: current.output && current.output.length ? current.output : null, error: current.error });
    current = null;
  };

  for (const raw of text.split(/\r?\n/)) {
    if (raw.trim() === "" || /^\t+#@/.test(raw)) continue;
    const m = ENTRY.exec(raw);
    if (!m) {
      const c = CONTINUATION.exec(raw);
      if (c && current) {
        if (field === "input") current.request.push(...hexBytes(c[1]!));
        else if (field === "output" && current.output) current.output.push(...hexBytes(c[1]!));
      }
      continue;
    }
    const indent = m[1]!.length;
    const body = m[2]!.trimEnd();
    if (indent === 1) {
      // A call. Only SENDECUTEL is a telegram exchange.
      flush();
      field = "other";
      if (body === "SENDECUTEL") current = { request: [], output: null, error: null };
      continue;
    }
    if (!current || indent !== 2) {
      field = "other";
      continue;
    }
    const [key, ...rest] = body.split(":");
    const value = rest.join(":").trim();
    if (key === "INPUT") {
      current.request.push(...hexBytes(value));
      field = "input";
    } else if (key === "OUTPUT") {
      current.output = value === "--" ? null : hexBytes(value);
      field = "output";
    } else if (key === "ERROR") {
      current.error = value === "NO ERROR" ? null : value;
      field = "other";
    } else {
      field = "other";
    }
  }
  flush();
  return out;
}

interface RecEvent {
  t_us: number;
  op: string;
  hex?: string;
  reason?: string;
  latency?: string;
}

function fromHex(hex: string): number[] {
  const out: number[] = [];
  for (let i = 0; i + 1 < hex.length; i += 2) out.push(Number.parseInt(hex.slice(i, i + 2), 16));
  return out;
}

function startsWith(a: number[], prefix: number[]): boolean {
  return prefix.length <= a.length && prefix.every((b, i) => a[i] === b);
}

/** The exchanges in a recording, with echo and reply times; plus its latency, short reads and refusals. */
export function parseRecording(jsonl: string): { exchanges: Exchange[]; latency: string | null; shortReads: number; refused: number } {
  const events: RecEvent[] = [];
  jsonl.split("\n").forEach((line, i) => {
    if (!line.trim()) return;
    let e: unknown;
    try {
      e = JSON.parse(line);
    } catch {
      throw new Error(`recording line ${i + 1} is not JSON`);
    }
    const ev = e as RecEvent;
    if (typeof ev !== "object" || ev === null || typeof ev.t_us !== "number" || typeof ev.op !== "string") throw new Error(`recording line ${i + 1} is not an event`);
    events.push(ev);
  });

  let latency: string | null = null;
  let shortReads = 0;
  let refused = 0;
  const exchanges: Exchange[] = [];
  let send: { bytes: number[]; t: number } | null = null;
  let received: number[] = [];
  let echoEnd: number | null = null;
  let firstReply: number | null = null;

  const flush = () => {
    if (!send) return;
    const echoed = echoEnd !== null;
    const response = echoed ? received.slice(send.bytes.length) : received;
    const x: Exchange = { request: send.bytes, response: response.length ? response : null, error: null, tUs: send.t };
    if (echoEnd !== null) x.echoUs = echoEnd - send.t;
    if (echoEnd !== null && firstReply !== null) x.replyUs = firstReply - echoEnd;
    exchanges.push(x);
    send = null;
  };

  for (const ev of events) {
    if (ev.op === "open" && typeof ev.latency === "string") latency = ev.latency;
    else if (ev.op === "refused") refused++;
    else if (ev.op === "send") {
      flush();
      send = { bytes: fromHex(ev.hex ?? ""), t: ev.t_us };
      received = [];
      echoEnd = null;
      firstReply = null;
    } else if (ev.op === "recv") {
      if (ev.reason) shortReads++;
      if (!send) continue;
      const bytes = fromHex(ev.hex ?? "");
      if (bytes.length === 0) continue;
      const before = received.length;
      received.push(...bytes);
      if (echoEnd === null) {
        // The echo is the send's bytes, first, in one receive or several.
        if (received.length >= send.bytes.length && startsWith(received, send.bytes)) {
          echoEnd = ev.t_us;
          if (received.length > send.bytes.length) firstReply = ev.t_us;
        } else if (!startsWith(send.bytes, received)) {
          // Not an echo after all: everything received is the reply.
          if (firstReply === null) firstReply = ev.t_us;
        }
      } else if (firstReply === null && received.length > before) {
        firstReply = ev.t_us;
      }
    }
  }
  flush();
  return { exchanges, latency, shortReads, refused };
}

/** Drops the last byte when it is the 8-bit sum of the others (the telegram's checksum). */
export function stripChecksum(t: number[]): number[] {
  if (t.length < 2) return t;
  const sum = t.slice(0, -1).reduce((a, b) => (a + b) & 0xff, 0);
  return sum === t[t.length - 1] ? t.slice(0, -1) : t;
}

function same(a: number[] | null, b: number[] | null): boolean {
  if (a === null || b === null) return a === b;
  const x = stripChecksum(a);
  const y = stripChecksum(b);
  return x.length === y.length && x.every((v, i) => v === y[i]);
}

/** Pairwise, checksums aside; the first exchange where requests or replies differ, or one side ends. */
export function compareExchanges(vm: Exchange[], native: Exchange[]): { matched: number; firstDivergence: null | { index: number; vm: Exchange | null; native: Exchange | null } } {
  const n = Math.max(vm.length, native.length);
  for (let i = 0; i < n; i++) {
    const a = vm[i] ?? null;
    const b = native[i] ?? null;
    if (!a || !b || !same(a.request, b.request) || !same(a.response, b.response)) return { matched: i, firstDivergence: { index: i, vm: a, native: b } };
  }
  return { matched: n, firstDivergence: null };
}

function stats(us: number[]): Stats {
  if (us.length === 0) return { n: 0, minMs: 0, medianMs: 0, maxMs: 0 };
  const s = [...us].sort((a, b) => a - b);
  const mid = Math.floor(s.length / 2);
  const median = s.length % 2 ? s[mid]! : (s[mid - 1]! + s[mid]!) / 2;
  return { n: s.length, minMs: s[0]! / 1000, medianMs: median / 1000, maxMs: s[s.length - 1]! / 1000 };
}

/** Echo and reply times over the exchanges that have them. */
export function timing(exchanges: Exchange[]): { echo: Stats; reply: Stats } {
  return {
    echo: stats(exchanges.flatMap((x) => (x.echoUs === undefined ? [] : [x.echoUs]))),
    reply: stats(exchanges.flatMap((x) => (x.replyUs === undefined ? [] : [x.replyUs]))),
  };
}

/** Bytes as `82 12 F1`. */
export function hex(bytes: number[] | null): string {
  return bytes === null ? "--" : bytes.map((b) => b.toString(16).toUpperCase().padStart(2, "0")).join(" ");
}
