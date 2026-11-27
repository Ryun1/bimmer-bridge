// The car comparison's result comparison and job lists. Synthetic results only; nothing here
// reaches a VM, the engine or a car.
import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { compareJobResults, countIfh0003, gateRefusedIn, readGateRefused, readJobList } from "../car-compare-lib.ts";

const ok = (sets: Record<string, unknown>[], o: Record<string, unknown> = {}) => ({
  ok: true as const,
  result: { ecu: "NVE_65", job: "IDENT", variant: "NVE_65", jobStatus: "OKAY", systemResults: { SAETZE: sets.length }, sets, durationMs: 120, ...o },
});
const err = (code: string, message = "m") => ({ ok: false as const, code, message });

describe("compareJobResults", () => {
  it("same results in a different order match", () => {
    const r = compareJobResults(ok([{ A: 1, B: "x" }]), ok([{ B: "x", A: 1 }], { durationMs: 900, systemResults: { SAETZE: 1, OTHER: 2 } }));
    expect(r).toEqual({ kind: "match", differences: [] });
  });

  it("a differing value is reported with set and name", () => {
    const r = compareJobResults(ok([{ A: 1 }, { ID_SG_ADR: 18 }]), ok([{ A: 1 }, { ID_SG_ADR: 19 }]));
    expect(r.kind).toBe("differ");
    expect(r.differences).toEqual([{ path: "sets[1].ID_SG_ADR", vm: 18, native: 19 }]);
  });

  it("a differing type is a difference", () => {
    expect(compareJobResults(ok([{ A: 1 }]), ok([{ A: "1" }])).differences).toEqual([{ path: "sets[0].A", vm: 1, native: "1" }]);
  });

  it("a field on one side only, and a different set count, are differences", () => {
    expect(compareJobResults(ok([{ A: 1 }]), ok([{ A: 1, B: 2 }])).differences).toEqual([{ path: "sets[0].B", vm: undefined, native: 2 }]);
    expect(compareJobResults(ok([{ A: 1 }]), ok([{ A: 1 }, { A: 1 }])).differences).toContainEqual({ path: "sets.length", vm: 1, native: 2 });
  });

  it("GRUPPE and FAMILIE are ignored", () => {
    expect(compareJobResults(ok([{ A: 1 }]), ok([{ A: 1, GRUPPE: "D_0012", FAMILIE: "NVE" }])).kind).toBe("match");
  });

  it("job status and variant are compared", () => {
    expect(compareJobResults(ok([{ A: 1 }]), ok([{ A: 1 }], { jobStatus: "ERROR_ECU" })).differences).toEqual([{ path: "jobStatus", vm: "OKAY", native: "ERROR_ECU" }]);
  });

  it("error on one side is vm_error / native_error", () => {
    expect(compareJobResults(err("ediabas_error"), ok([{ A: 1 }])).kind).toBe("vm_error");
    expect(compareJobResults(ok([{ A: 1 }]), err("cable_not_found")).kind).toBe("native_error");
    expect(compareJobResults(err("ediabas_error"), err("ediabas_error")).kind).toBe("both_error");
  });

  it("errors with different codes on both sides say so", () => {
    expect(compareJobResults(err("ediabas_error"), err("job_timeout")).differences).toEqual([{ path: "code", vm: "ediabas_error", native: "job_timeout" }]);
  });

  it("a native BIP-0007 or BIP-0011 is noted as the EDIABAS 7.7.3 emulation difference", () => {
    const r = compareJobResults(ok([{ A: 1 }]), err("ediabas_error", "EDIABAS reported error 67: BIP-0007: DIVISION BY ZERO"));
    expect(r.kind).toBe("native_error");
    expect(r.note).toMatch(/7\.7\.3/);
  });
});

describe("countIfh0003", () => {
  it("counts IFH-0003 errors in a run's lines", () => {
    const lines = [
      { ecu: "A", job: "J", response: err("ediabas_error", "EDIABAS reported error 13: IFH-0003: DATATRANSMISSION TO CONTROLUNIT DISTURBED") },
      { ecu: "B", job: "J", response: ok([{ A: 1 }]) },
      { ecu: "C", job: "J", response: err("ediabas_error", "EDIABAS reported error 19: IFH-0009") },
    ];
    expect(countIfh0003(lines)).toBe(1);
  });
});

describe("job lists", () => {
  const files = ["session-b.json", "session-c.json"].map((f) => new URL(`../m2/${f}`, import.meta.url));

  it.each(files.map((f) => [f.pathname.split("/").pop()!, f] as const))("%s reads and has no gate-refused read", (_name, file) => {
    const jobs = readJobList(readFileSync(file, "utf8"));
    expect(jobs.length).toBeGreaterThan(0);
    // M1 section 2 names LM_60's 17 refused STATUS_ reads only as a count, and the gate screen
    // (gate-refused.json) did not cover LM_60 (not this car's light module): no LM_60 STATUS_ read.
    for (const j of jobs) expect(j.ecu.toUpperCase() === "LM_60" && j.job.toUpperCase().startsWith("STATUS_")).toBe(false);
  });

  it.each(files.map((f) => [f.pathname.split("/").pop()!, f] as const))("%s has no read the gate screen refused (gate-refused.json)", (_name, file) => {
    const refused = readGateRefused(readFileSync(new URL("../m2/gate-refused.json", import.meta.url), "utf8"));
    expect(gateRefusedIn(readJobList(readFileSync(file, "utf8")), refused)).toEqual([]);
  });

  it("session B is NVE_65 IDENT alone", () => {
    expect(readJobList(readFileSync(files[0]!, "utf8"))).toEqual([{ ecu: "NVE_65", job: "IDENT" }]);
  });

  it("gateRefusedIn finds a refused read whatever the case", () => {
    const refused = readGateRefused('{"refused":[{"ecu":"NVE_65","job":"STATUS_TASTER","service":"30"}],"unscreened":[]}');
    expect(gateRefusedIn([{ ecu: "nve_65", job: "status_taster" }, { ecu: "NVE_65", job: "FS_LESEN" }], refused)).toEqual(["nve_65 status_taster"]);
  });

  it("readGateRefused rejects a file without a refused array", () => {
    expect(() => readGateRefused("{}")).toThrow(/refused/);
  });

  it("a malformed list is an error naming the entry", () => {
    expect(() => readJobList('[{"ecu":"A"}]')).toThrow(/\[0\]/);
    expect(() => readJobList('[{"ecu":"A","job":"B","extra":1}]')).toThrow(/extra/);
    expect(() => readJobList("{}")).toThrow(/array/);
  });
});
