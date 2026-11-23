/**
 * Compares one SGBD's describe (list mode, with comments) from the VM agent and from ediabasd.
 * Pure: no I/O. Used by dev/describe-diff.ts (M1 acceptance: describe matches for every .prg).
 */

/** One side's answer: the describe body, or the error code it failed with. */
export type Side = { ok: true; value: unknown } | { ok: false; code: string; message?: string };

export interface Difference {
  path: string;
  vm: unknown;
  native: unknown;
}

/** verdict_only: names, comments, variant and count agree, only verdicts differ. Verdicts come
 * from the same Core code on both sides, so that is a policy difference (e.g. the VM agent
 * built from a newer policy.json), not the engine. */
export type DiffKind = "match" | "differ" | "verdict_only" | "vm_error" | "native_error" | "both_error";

export interface DescribeDiff {
  kind: DiffKind;
  differences: Difference[];
}

type Obj = Record<string, unknown>;

const asObj = (v: unknown): Obj => (v !== null && typeof v === "object" ? (v as Obj) : {});
const asArr = (v: unknown): unknown[] => (Array.isArray(v) ? v : []);

/**
 * Rules: durations are ignored; ecu, variant, jobCount, job names in order, and each job's
 * comment and verdict are compared. When either side did not read a job's description
 * (review "not_checked", e.g. the request's time budget ran out), only its name and the
 * verdict's allowed and rule are compared.
 */
export function diffDescribe(vm: Side, native: Side): DescribeDiff {
  if (!vm.ok && !native.ok) {
    return {
      kind: "both_error",
      differences: vm.code === native.code ? [] : [{ path: "error.code", vm: vm.code, native: native.code }],
    };
  }
  if (!vm.ok) return { kind: "vm_error", differences: [{ path: "error.code", vm: vm.code, native: null }] };
  if (!native.ok) return { kind: "native_error", differences: [{ path: "error.code", vm: null, native: native.code }] };

  const a = asObj(vm.value);
  const b = asObj(native.value);
  const out: Difference[] = [];
  const cmp = (path: string, x: unknown, y: unknown) => {
    if ((x ?? null) !== (y ?? null)) out.push({ path, vm: x ?? null, native: y ?? null });
  };

  cmp("ecu", a.ecu, b.ecu);
  cmp("variant", a.variant, b.variant);
  cmp("jobCount", a.jobCount, b.jobCount);

  const ja = asArr(a.jobs);
  const jb = asArr(b.jobs);
  for (let i = 0; i < Math.max(ja.length, jb.length); i++) {
    if (i >= ja.length || i >= jb.length) {
      const extra = asObj(i >= ja.length ? jb[i] : ja[i]);
      out.push({ path: `jobs[${i}]`, vm: i < ja.length ? extra.name : null, native: i < jb.length ? extra.name : null });
      continue;
    }
    const x = asObj(ja[i]);
    const y = asObj(jb[i]);
    const vx = asObj(x.verdict);
    const vy = asObj(y.verdict);
    cmp(`jobs[${i}].name`, x.name, y.name);
    cmp(`jobs[${i}].verdict.allowed`, vx.allowed, vy.allowed);
    cmp(`jobs[${i}].verdict.rule`, vx.rule, vy.rule);
    if (vx.review === "not_checked" || vy.review === "not_checked") continue;
    cmp(`jobs[${i}].comment`, x.comment, y.comment);
    cmp(`jobs[${i}].verdict.review`, vx.review, vy.review);
  }

  const kind: DiffKind =
    out.length === 0 ? "match" : out.every((d) => /\.verdict\.(allowed|rule|review)$/.test(d.path)) ? "verdict_only" : "differ";
  return { kind, differences: out };
}

/** A row of a list-mode run's output (describe-diff.ts --out). */
export interface RunRow {
  ecu: string;
  kind: string;
  differences: Difference[];
}

/** The job indexes of a differ row where the VM returned no comment and ediabasd did: the jobs
 * whose description the list run could not check (the VM agent's time budget ran out). */
export function uncheckedJobs(row: RunRow): number[] {
  if (row.kind !== "differ") return [];
  const out: number[] = [];
  for (const d of row.differences) {
    const m = /^jobs\[(\d+)\]\.comment$/.exec(d.path);
    if (m && d.vm === null && d.native !== null) out.push(Number(m[1]));
  }
  return out;
}

/** n items spread evenly over the list, first and last included; all of them when n >= length. */
export function spread<T>(items: readonly T[], n: number): T[] {
  if (items.length <= n) return [...items];
  if (n <= 1) return items.slice(0, Math.max(n, 0));
  return Array.from({ length: n }, (_, i) => items[Math.round((i * (items.length - 1)) / (n - 1))] as T);
}

/** not_checked: either side still did not read the job's description, so only name, allowed
 * and rule were compared. */
export type JobDiffKind = DiffKind | "not_checked";

/**
 * Compares one job's describe (GET /ediabas/describe?ecu&job) from both sides: ecu, variant,
 * job, verdict allowed and rule; and, when both read the description, comment, description
 * hash, review, arguments and results. Durations are ignored.
 */
export function diffJob(vm: Side, native: Side): { kind: JobDiffKind; differences: Difference[] } {
  if (!vm.ok || !native.ok) return diffDescribe(vm, native);
  const a = asObj(vm.value);
  const b = asObj(native.value);
  const va = asObj(a.verdict);
  const vb = asObj(b.verdict);
  const out: Difference[] = [];
  const cmp = (path: string, x: unknown, y: unknown) => {
    if (JSON.stringify(x ?? null) !== JSON.stringify(y ?? null)) out.push({ path, vm: x ?? null, native: y ?? null });
  };
  cmp("ecu", a.ecu, b.ecu);
  cmp("variant", a.variant, b.variant);
  cmp("job", a.job, b.job);
  cmp("verdict.allowed", va.allowed, vb.allowed);
  cmp("verdict.rule", va.rule, vb.rule);
  const unread = va.review === "not_checked" || vb.review === "not_checked";
  if (!unread) {
    cmp("comment", a.comment, b.comment);
    cmp("descriptionHash", a.descriptionHash, b.descriptionHash);
    cmp("verdict.review", va.review, vb.review);
    cmp("arguments", a.arguments, b.arguments);
    cmp("results", a.results, b.results);
  }
  if (out.length > 0) {
    return { kind: out.every((d) => d.path.startsWith("verdict.")) ? "verdict_only" : "differ", differences: out };
  }
  return { kind: unread ? "not_checked" : "match", differences: out };
}
