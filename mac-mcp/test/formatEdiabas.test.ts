import { describe, expect, it } from "vitest";
import { EcusSchema, HealthSchema, JobResultSchema, LogReadSchema } from "../src/agentClient.js";
import { formatCaptureCaption, formatHealth, formatLogList, formatLogRead } from "../src/format.js";
import {
  formatDescribeJob,
  formatEcus,
  formatFaultReport,
  formatFaultTable,
  formatJobRaw,
  formatJobResult,
  formatKm,
  itemErrorText,
} from "../src/formatEdiabas.js";
import { faultSets, jobResult, phase2Health } from "./stubAgent.js";

const result = (sets: Record<string, unknown>[], o: Record<string, unknown> = {}) =>
  JobResultSchema.parse(jobResult("NVE_65", "FS_LESEN", sets, o));

describe("formatKm", () => {
  it("puts miles first, km in brackets, with thousands separators", () => {
    expect(formatKm(195712)).toBe("121,609 mi (195,712 km)");
    expect(formatKm(0)).toBe("0 mi (0 km)");
    expect(formatKm(1)).toBe("0 mi (1 km)");
    expect(formatKm(1609.344)).toBe("1,000 mi (1,609 km)");
    expect(formatKm(524280)).toBe("325,772 mi (524,280 km)");
  });
});

describe("fault tables", () => {
  it("maps known fields, converts km, and passes every other field through", () => {
    const t = formatFaultTable(result(faultSets()), "Fault memory").join("\n");
    expect(t).toContain("  Fault memory: 2 entries");
    expect(t).toContain("  1. 0xA752  Synthetic fault A");
    expect(t).toContain("present/stored: Fault currently present | frequency: 3 | logged at: 121,609 mi (195,712 km)");
    // F_Uw2_WERT: one SGBD spells it that way
    expect(t).toContain("conditions: Engine speed = 820 1/min; Coolant temperature = 90 degC");
    expect(t).toContain('other: F_VERSION=2 F_SYMPTOM_NR=2 F_SYMPTOM_TEXT="Signal too low" F_VORHANDEN_NR=1 F_UW_ANZ=2 F_UW1_NR=1 JOB_STATUS=OKAY');
    expect(t).toContain("  2. 0x002F  Synthetic fault B");
    expect(t).toContain("present/stored: Fault stored | frequency: not counted");
    expect(t).toContain("other: MYSTERY_FIELD=kept");
  });

  it("never hides a field: every key of every set appears in the output", () => {
    const sets = faultSets();
    const t = formatFaultTable(result(sets), "Fault memory").join("\n");
    const shown: Record<string, string> = {
      F_ORT_NR: "0xA752",
      F_ORT_TEXT: "Synthetic fault A",
      F_VORHANDEN_TEXT: "Fault currently present",
      F_HFK: "frequency: 3",
      F_UW_KM: "121,609 mi",
      F_UW1_TEXT: "Engine speed",
      F_UW1_WERT: "820",
      F_UW1_EINH: "1/min",
      F_Uw2_WERT: "90",
      F_UW2_TEXT: "Coolant temperature",
      F_UW2_EINH: "degC",
    };
    for (const k of Object.keys(sets[0]!)) expect(t).toContain(shown[k] ?? `${k}=`);
  });

  it("passes through fields of an SGBD that uses other names", () => {
    const t = formatFaultTable(result([{ FEHLER_CODE: 12, FEHLER_TEXT: "Other naming" }]), "Fault memory").join("\n");
    expect(t).toContain("no faults stored");
    expect(t).toContain('(set 1) FEHLER_CODE=12 FEHLER_TEXT="Other naming"');
  });

  it("says when an ECU has no faults", () => {
    expect(formatFaultTable(result([{ JOB_STATUS: "OKAY" }]), "Fault memory")).toEqual(["  Fault memory: no faults stored."]);
    expect(formatFaultTable(result([]), "Info memory")).toEqual(["  Info memory: no faults stored."]);
  });

  it("does not claim 'no faults' when the job status is not OKAY", () => {
    const t = formatFaultTable(result([{ JOB_STATUS: "ERROR_ECU_NO_RESPONSE" }], { jobStatus: "ERROR_ECU_NO_RESPONSE" }), "Fault memory");
    expect(t[0]).toBe("  Fault memory: no fault entries returned (job status ERROR_ECU_NO_RESPONSE).");
  });

  it("cleans control characters and caps text from the VM", () => {
    const t = formatFaultTable(result([{ F_ORT_NR: 1, F_ORT_TEXT: "Bad\u001b[2J\ntext" + "x".repeat(1000) }]), "Fault memory").join("\n");
    expect(t).not.toMatch(/[\u0000-\u0009\u000b-\u001f]/);
    expect(t).toContain("Bad [2J text");
    expect(t.length).toBeLessThan(500);
  });

  it("prints per-ECU errors in place", () => {
    const t = formatFaultReport(
      [
        { ecu: "NVE_65", faults: { ok: true, result: result([]), error: null } },
        { ecu: "D_MOTOR", faults: { ok: false, result: null, error: { code: "ecu_not_found", message: "x", details: { ecu: "D_MOTOR" } } } },
        { ecu: "X", faults: { error: "The VM does not appear to be running." } },
      ],
      false,
    );
    expect(t).toContain("NVE_65\n  Fault memory: no faults stored.");
    expect(t).toContain("D_MOTOR\n  Fault memory: error: There is no SGBD or group file named D_MOTOR");
    expect(t).toContain("X\n  Fault memory: error: The VM does not appear to be running.");
  });
});

describe("job results", () => {
  it("prints system results and each set, distances miles first", () => {
    const t = formatJobResult(result([{ STAT_KM_STAND: 195712, STAT_KILOMETERSTAND_WERT: 1000, X_WERT: 5, X_EINH: "km", SPEED_KM_H: 50 }]));
    expect(t).toContain("NVE_65 FS_LESEN: job status OKAY, variant NVE_65, 300 ms, 1 result set");
    expect(t).toContain("System results: VARIANTE=NVE_65 SAETZE=1");
    expect(t).toContain("  STAT_KM_STAND=121,609 mi (195,712 km)");
    expect(t).toContain("  STAT_KILOMETERSTAND_WERT=621 mi (1,000 km)");
    expect(t).toContain("  X_WERT=3 mi (5 km)");
    expect(t).toContain("  SPEED_KM_H=50");
  });

  it("raw is the agent's JSON with strings cleaned", () => {
    const t = formatJobRaw(result([{ A: "x\u0007y", B: null, C: "DEADBEEF" }]));
    expect(JSON.parse(t).sets[0]).toEqual({ A: "x y", B: null, C: "DEADBEEF" });
  });

  it("maps per-item errors, and unknown codes with their message", () => {
    expect(itemErrorText({ code: "weird_code", message: "something\nodd" })).toBe("weird_code: something odd");
    expect(itemErrorText(null)).toMatch(/no error/);
  });
});

describe("describe / ecus", () => {
  it("one job: verdict, description, arguments and results", () => {
    const t = formatDescribeJob(
      {
        ecu: "NVE_65",
        variant: "NVE_65",
        job: "STATUS_X",
        comment: "Liest\u0000 Status",
        verdict: { allowed: true, rule: "allow.pattern", review: "needs_review" },
        arguments: [{ name: "ARG", type: "int", comment: "Nummer" }],
        results: [],
      },
      { allowed: true, rule: "allow.pattern", screen: true },
    );
    expect(t).toMatch(/^NVE_65 STATUS_X: needs review/);
    expect(t).toMatch(/Description: Liest\s+Status/);
    expect(t).toContain("  ARG (int) — Nummer");
    expect(t).toContain("Results: none listed");
  });

  it("ECU list, with truncation", () => {
    const t = formatEcus(
      EcusSchema.parse({ ecuPath: "C:\\EDIABAS\\ECU", count: 5000, truncated: true, files: [{ name: "A", type: "prg" }, { name: "G", type: "grp" }] }),
      undefined,
    );
    expect(t).toMatch(/^2 files in C:\\EDIABAS\\ECU \(truncated: 5000 matched; narrow the filter\):/);
    expect(formatEcus(EcusSchema.parse({ ecuPath: null, count: 0, truncated: false, files: [] }), "zz")).toBe('No SGBD or group files matching "zz".');
  });

  it("ECU list prints at most 500 names, then how many more", () => {
    const files = [
      ...Array.from({ length: 10 }, (_, i) => ({ name: `G${i}`, type: "grp" })),
      ...Array.from({ length: 4990 }, (_, i) => ({ name: `P${String(i).padStart(4, "0")}`, type: "prg" })),
    ];
    const t = formatEcus(EcusSchema.parse({ ecuPath: null, count: 5000, truncated: false, files }), undefined);
    expect(t).toMatch(/^5000 files:/);
    expect(t).toContain("Groups (grp): G0, G1");
    expect(t).toContain("P0489");
    expect(t).not.toContain("P0490");
    expect(t.split("\n").at(-1)).toBe("4500 more; use filter to narrow the list.");
  });
});

describe("health, capture, logs", () => {
  it("health shows features and policy versions", () => {
    const t = formatHealth(HealthSchema.parse(phase2Health({ policyVersion: 3 })), 1);
    expect(t).toContain("Agent features: ediabas, capture, logs");
    expect(t).toMatch(/WARNING: the agent's job policy \(version 3\) differs from this Mac's \(version 1\)/);
  });

  it("caption is one line", () => {
    expect(formatCaptureCaption({ mimeType: "image/png", data: new Uint8Array(), title: "T\nX", handle: "0x1", method: "bitblt", width: 10, height: 20 })).toBe(
      'Window "T X" (0x1), 10x20, captured with bitblt.',
    );
    expect(formatCaptureCaption({ mimeType: "image/png", data: new Uint8Array(), title: null, handle: null, method: null, width: null, height: null })).toBe(
      "Desktop, size unknown, captured with an unknown method.",
    );
  });

  it("log list with no files", () => {
    expect(formatLogList({ roots: [], files: [], truncated: false })).toBe("Log folders:\n  none configured in the agent\n\nNo matching files.");
  });

  it("log text is not capped here (the tool's text budget applies)", () => {
    const r = LogReadSchema.parse({
      path: "C:\\a.log",
      sizeBytes: 1,
      modifiedAt: null,
      offset: 0,
      length: 1,
      encoding: "windows-1252",
      truncated: true,
      text: "y".repeat(400_000),
    });
    const t = formatLogRead(r);
    expect(t).toMatch(/^C:\\a\.log: bytes 0-1 of 1, encoding windows-1252, modified unknown, truncated: yes\n\n/);
    expect(t).not.toMatch(/cut at/);
  });
});
