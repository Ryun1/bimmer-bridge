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
