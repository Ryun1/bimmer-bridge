/**
 * The car comparison (M2): the same job list run through the VM agent and then natively, one
 * JSON line per job, compared here. The CLI is car-compare.ts.
 */

/** One job as the job lists (dev/m2/*.json) give it. */
export interface JobSpec {
  ecu: string;
  job: string;
  args?: string;
  results?: string;
  timeoutSeconds?: number;
}

/** One job's outcome as a run writes it. */
export type Response = { ok: true; result: unknown } | { ok: false; code: string; message: string };

export interface RunLine {
  ecu: string;
  job: string;
  response: Response;
  at?: string;
}

export interface Difference {
  path: string;
  vm: unknown;
  native: unknown;
}

export interface Comparison {
  kind: "match" | "differ" | "vm_error" | "native_error" | "both_error";
  differences: Difference[];
  /** A known cause of the difference, when there is one. */
  note?: string;
}

const JOB_KEYS = new Set(["ecu", "job", "args", "results", "timeoutSeconds"]);

/** Parses and checks a job list: an array of {ecu, job, args?, results?, timeoutSeconds?}. */
export function readJobList(text: string): JobSpec[] {
  const v: unknown = JSON.parse(text);
  if (!Array.isArray(v)) throw new Error("a job list must be a JSON array");
  return v.map((e: unknown, i) => {
    const o = e as Record<string, unknown>;
    if (typeof e !== "object" || e === null || Array.isArray(e)) throw new Error(`job list [${i}] must be an object`);
    for (const k of Object.keys(o)) if (!JOB_KEYS.has(k)) throw new Error(`job list [${i}] has an unknown key "${k}"`);
    if (typeof o.ecu !== "string" || !o.ecu.trim() || typeof o.job !== "string" || !o.job.trim()) throw new Error(`job list [${i}] needs a non-empty "ecu" and "job"`);
    if (o.args !== undefined && typeof o.args !== "string") throw new Error(`job list [${i}] args must be a string`);
    if (o.results !== undefined && typeof o.results !== "string") throw new Error(`job list [${i}] results must be a string`);
    if (o.timeoutSeconds !== undefined && !(Number.isInteger(o.timeoutSeconds) && (o.timeoutSeconds as number) >= 1 && (o.timeoutSeconds as number) <= 60)) {
      throw new Error(`job list [${i}] timeoutSeconds must be a whole number from 1 to 60`);
    }
    return o as unknown as JobSpec;
  });
}

// EdiabasLib's results carry these where real EDIABAS 7.3 does not (M1 results, section 4).
const IGNORED_FIELDS = new Set(["GRUPPE", "FAMILIE"]);

// EdiabasLib emulates EDIABAS 7.7.3; the VM runs 7.3 (M1 results, sections 4 and 5).
const EMULATION_NOTE = "EdiabasLib emulates EDIABAS 7.7.3 and the VM runs 7.3: a BIP-0007/BIP-0011 natively is that difference, not a transport fault (m1-results section 4).";

interface ResultShape {
  variant?: unknown;
  jobStatus?: unknown;
  sets?: unknown;
}

function asResponse(x: unknown): Response {
  const r = x as Response;
  if (typeof x === "object" && x !== null && typeof r.ok === "boolean") return r;
  return { ok: false, code: "no_response", message: "the run wrote no response for this job" };
}

function sameValue(a: unknown, b: unknown): boolean {
  return typeof a === typeof b && JSON.stringify(a) === JSON.stringify(b);
}

/** One job's two outcomes: result sets compared per set by field name (type and value). */
export function compareJobResults(vmIn: unknown, nativeIn: unknown): Comparison {
  const vm = asResponse(vmIn);
  const native = asResponse(nativeIn);
  if (!vm.ok || !native.ok) {
    const kind = !vm.ok && !native.ok ? "both_error" : !vm.ok ? "vm_error" : "native_error";
    const differences = !vm.ok && !native.ok && vm.code !== native.code ? [{ path: "code", vm: vm.code, native: native.code }] : [];
    const out: Comparison = { kind, differences };
    if (!native.ok && /BIP[-_]00(07|11)/.test(native.message) && (vm.ok || !/BIP[-_]00(07|11)/.test(vm.message))) out.note = EMULATION_NOTE;
    return out;
  }

  const a = vm.result as ResultShape;
  const b = native.result as ResultShape;
  const differences: Difference[] = [];
  for (const key of ["variant", "jobStatus"] as const) {
    if (!sameValue(a[key] ?? null, b[key] ?? null)) differences.push({ path: key, vm: a[key], native: b[key] });
  }
  const setsA = Array.isArray(a.sets) ? (a.sets as Record<string, unknown>[]) : [];
  const setsB = Array.isArray(b.sets) ? (b.sets as Record<string, unknown>[]) : [];
  if (setsA.length !== setsB.length) differences.push({ path: "sets.length", vm: setsA.length, native: setsB.length });
  for (let i = 0; i < Math.min(setsA.length, setsB.length); i++) {
    const sa = setsA[i] ?? {};
    const sb = setsB[i] ?? {};
    const names = [...new Set([...Object.keys(sa), ...Object.keys(sb)])].filter((n) => !IGNORED_FIELDS.has(n.toUpperCase()));
    for (const name of names) {
      if (!sameValue(sa[name], sb[name])) differences.push({ path: `sets[${i}].${name}`, vm: sa[name], native: sb[name] });
    }
  }
  return { kind: differences.length ? "differ" : "match", differences };
}

/** How many jobs in a run failed with IFH-0003 (no valid answer on the line). */
export function countIfh0003(lines: RunLine[]): number {
  return lines.filter((l) => !l.response.ok && /IFH[-_]0003/.test(l.response.message)).length;
}

/** One read the gate screen (M2 task 7b, dev/gate-screen) found refused natively. */
export interface GateRefusal {
  ecu: string;
  job: string;
  /** The refused frame's service byte, hex. */
  service?: string;
}

export interface GateRefusedFile {
  refused: GateRefusal[];
  /** SGBDs the screen could not run (they need the module to initialise). */
  unscreened?: { ecu: string; reason: string }[];
}

/** Parses dev/m2/gate-refused.json. */
export function readGateRefused(text: string): GateRefusedFile {
  const v = JSON.parse(text) as Partial<GateRefusedFile>;
  if (!Array.isArray(v.refused)) throw new Error('gate-refused.json needs a "refused" array');
  for (const [i, r] of v.refused.entries()) {
    if (typeof r?.ecu !== "string" || typeof r.job !== "string") throw new Error(`gate-refused.json refused[${i}] needs "ecu" and "job"`);
  }
  return v as GateRefusedFile;
}

/** The job-list entries the gate screen refused, as "ECU JOB" in the list's own spelling. */
export function gateRefusedIn(jobs: JobSpec[], file: GateRefusedFile): string[] {
  const key = (ecu: string, job: string) => `${ecu.toUpperCase()} ${job.toUpperCase()}`;
  const refused = new Set(file.refused.map((r) => key(r.ecu, r.job)));
  return jobs.filter((j) => refused.has(key(j.ecu, j.job))).map((j) => `${j.ecu} ${j.job}`);
}
