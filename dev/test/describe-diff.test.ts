import { describe, expect, it } from "vitest";
import { diffDescribe, type Side } from "../describe-diff-lib.ts";

const job = (name: string, comment: string | null, review = "not_required", allowed = true, rule = "allow.global") => ({
  name,
  comment,
  verdict: { allowed, rule, review },
});

const list = (jobs: unknown[], extra: Record<string, unknown> = {}) => ({
  ecu: "NVE_65",
  variant: "NVE_65",
  jobCount: jobs.length,
  jobs,
  durationMs: 100,
  ...extra,
});

const ok = (value: unknown): Side => ({ ok: true, value });
const err = (code: string): Side => ({ ok: false, code, message: code });

describe("diffDescribe", () => {
  it("identical lists match", () => {
    const a = list([job("IDENT", "reads ident"), job("FS_LESEN", "reads faults")]);
    expect(diffDescribe(ok(a), ok(structuredClone(a))).kind).toBe("match");
  });

  it("ignores durations", () => {
    const a = list([job("IDENT", "x")]);
    const b = list([job("IDENT", "x")], { durationMs: 99999 });
    expect(diffDescribe(ok(a), ok(b))).toEqual({ kind: "match", differences: [] });
  });

  it("reports a differing comment with its path", () => {
    const a = list([job("A", "a"), job("B", "b"), job("C", "c"), job("D", "d")]);
    const b = list([job("A", "a"), job("B", "b"), job("C", "c"), job("D", "changed")]);
    const d = diffDescribe(ok(a), ok(b));
    expect(d.kind).toBe("differ");
    expect(d.differences).toEqual([{ path: "jobs[3].comment", vm: "d", native: "changed" }]);
  });

  it("reports job lists of different length and order", () => {
    const a = list([job("A", "a"), job("B", "b")]);
    const b = list([job("B", "b"), job("A", "a"), job("C", "c")]);
    const d = diffDescribe(ok(a), ok(b));
    expect(d.kind).toBe("differ");
    expect(d.differences.map((x) => x.path)).toContain("jobCount");
    expect(d.differences.map((x) => x.path)).toContain("jobs[0].name");
    expect(d.differences.map((x) => x.path)).toContain("jobs[2]");
  });

  it("a VM error with a native answer is vm_error", () => {
    expect(diffDescribe(err("cable_not_found"), ok(list([]))).kind).toBe("vm_error");
  });

  it("a native error with a VM answer is native_error", () => {
    expect(diffDescribe(ok(list([])), err("needs_car")).kind).toBe("native_error");
  });

  it("both failing with the same code is both_error", () => {
    const d = diffDescribe(err("ecu_not_found"), err("ecu_not_found"));
    expect(d.kind).toBe("both_error");
    expect(d.differences).toEqual([]);
  });

  it("not_checked on one side compares only name, allowed and rule", () => {
    const a = list([job("STATUS_X", "first", "passed", true, "allow.pattern")]);
    const b = list([job("STATUS_X", "second", "not_checked", true, "allow.pattern")]);
    expect(diffDescribe(ok(a), ok(b)).kind).toBe("match");
  });

  it("not_checked still compares the rule", () => {
    const a = list([job("STATUS_X", "first", "passed", true, "allow.pattern")]);
    const b = list([job("STATUS_X", "second", "not_checked", false, "deny.substring")]);
    const d = diffDescribe(ok(a), ok(b));
    expect(d.differences.map((x) => x.path).sort()).toEqual(["jobs[0].verdict.allowed", "jobs[0].verdict.rule"]);
  });

  it("a differing variant is reported", () => {
    const d = diffDescribe(ok(list([], { variant: "A" })), ok(list([], { variant: "B" })));
    expect(d.differences).toEqual([{ path: "variant", vm: "A", native: "B" }]);
  });

  it("differences only in verdicts are verdict_only (policy drift), and still listed", () => {
    const a = list([job("STATUS_PAIRING", "same text", "needs_review", true, "allow.pattern")]);
    const b = list([job("STATUS_PAIRING", "same text", "passed", true, "allow.pattern")]);
    const d = diffDescribe(ok(a), ok(b));
    expect(d.kind).toBe("verdict_only");
    expect(d.differences).toEqual([{ path: "jobs[0].verdict.review", vm: "needs_review", native: "passed" }]);
  });

  it("a comment difference beside a verdict difference is differ", () => {
    const a = list([job("A", "x", "needs_review")]);
    const b = list([job("A", "y", "passed")]);
    expect(diffDescribe(ok(a), ok(b)).kind).toBe("differ");
  });
});

