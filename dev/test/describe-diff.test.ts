import { describe, expect, it } from "vitest";
import { diffDescribe, diffJob, spread, uncheckedJobs, type Side } from "../describe-diff-lib.ts";

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


describe("uncheckedJobs", () => {
  it("lists the job indexes where only the VM lacks a comment", () => {
    const row = {
      ecu: "AFS_60",
      kind: "differ",
      differences: [
        { path: "jobs[3].comment", vm: null, native: "x" },
        { path: "jobs[3].verdict.review", vm: "needs_review", native: "passed" },
        { path: "jobs[7].comment", vm: "y", native: null },
        { path: "jobs[12].comment", vm: null, native: "z" },
        { path: "jobs[20].comment", vm: "a", native: "b" },
      ],
    };
    expect(uncheckedJobs(row)).toEqual([3, 12]);
  });

  it("is empty for a row that is not differ", () => {
    expect(uncheckedJobs({ ecu: "X", kind: "vm_error", differences: [{ path: "error.code", vm: "ediabas_error", native: null }] })).toEqual([]);
  });
});

describe("spread", () => {
  it("takes the first, middle and last of many", () => {
    expect(spread([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 3)).toEqual([0, 5, 10]);
  });

  it("takes all when there are no more than n", () => {
    expect(spread([4, 9], 3)).toEqual([4, 9]);
  });

  it("takes the first for n = 1", () => {
    expect(spread([4, 9, 11], 1)).toEqual([4]);
  });
});

describe("diffJob", () => {
  const one = (comment: string | null, review = "passed", extra: Record<string, unknown> = {}) => ({
    ecu: "AFS_60",
    variant: "AFS_60",
    job: "STATUS_X",
    comment,
    descriptionHash: "abc",
    verdict: { allowed: true, rule: "allow.pattern", review },
    arguments: [{ name: "MODE", type: "string", comment: "m" }],
    results: [{ name: "STAT", type: "int", comment: "s" }],
    durationMs: 5,
    ...extra,
  });

  it("identical single-job describes match, durations ignored", () => {
    expect(diffJob(ok(one("c")), ok(one("c", "passed", { durationMs: 900 })))).toEqual({ kind: "match", differences: [] });
  });

  it("reports a differing comment, arguments and results", () => {
    const d = diffJob(ok(one("c")), ok(one("d", "passed", { arguments: [], results: [{ name: "STAT", type: "real", comment: "s" }] })));
    expect(d.kind).toBe("differ");
    expect(d.differences.map((x) => x.path).sort()).toEqual(["arguments", "comment", "results"]);
  });

  it("a VM side that still did not read the description is not_checked", () => {
    const d = diffJob(ok(one(null, "not_checked")), ok(one("c")));
    expect(d.kind).toBe("not_checked");
  });

  it("errors are classified as in diffDescribe", () => {
    expect(diffJob(err("ediabas_error"), ok(one("c"))).kind).toBe("vm_error");
  });
});
