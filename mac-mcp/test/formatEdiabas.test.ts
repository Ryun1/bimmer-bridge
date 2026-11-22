import { describe, expect, it } from "vitest";
import { DescribeJobSchema, EcusSchema, HealthSchema, JobResultSchema, LogReadSchema } from "../src/agentClient.js";
import { formatCaptureCaption, formatHealth, formatLogList, formatLogRead } from "../src/format.js";
import {
  formatDescribeJob,
  formatEcus,
  formatFaultReport,
  formatFaultTable,
  type FaultReport,
  formatJobRaw,
  formatJobResult,
  formatKm,
  formatVehicle,
  isKmName,
  itemErrorText,
  vehicleBatch,
} from "../src/formatEdiabas.js";
import { faultSets, jobResult, phase2Health } from "./stubAgent.js";

const result = (sets: Record<string, unknown>[], o: Record<string, unknown> = {}) =>
  JobResultSchema.parse(jobResult("NVE_65", "FS_LESEN", sets, o));

describe("isKmName", () => {
  it("a KM token is a distance, but not when PRO, H or STD follows it (km/h, km per hour, km standard)", () => {
    expect(isKmName("STAT_KM_STAND")).toBe(true);
    expect(isKmName("STAT_GESCHWINDIGKEIT_KM_PRO_H")).toBe(false);
    expect(isKmName("SPEED_KM_H")).toBe(false);
    expect(isKmName("X_KM_STD")).toBe(false);
  });
});

describe("formatKm", () => {
  it("puts miles first, km in brackets, with thousands separators", () => {
    expect(formatKm(160935)).toBe("100,000 mi (160,935 km)");
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
    expect(t).toContain("present/stored: Fault currently present | frequency: 3 | logged at: 100,000 mi (160,935 km)");
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
      F_UW_KM: "100,000 mi",
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
    expect(t).toContain("D_MOTOR\n  Fault memory: error: [ecu_not_found] There is no SGBD or group file named D_MOTOR");
    expect(t).toContain("X\n  Fault memory: error: The VM does not appear to be running.");
  });

  describe("info memory errors", () => {
    const noResponse = {
      ok: false as const,
      result: null,
      error: { code: "ediabas_error", message: "EDIABAS job failed", details: { errorCode: 19, errorText: "IFH-0009: NO RESPONSE FROM CONTROLUNIT" } },
    };
    const report = (faults: FaultReport["faults"], info: FaultReport["info"]) => formatFaultReport([{ ecu: "NVE_65", faults, info }], true);

    it("after a good fault-memory read, an ediabas_error on IS_LESEN says the module may have no info memory", () => {
      const t = report(
        { ok: true, result: result([]), error: null },
        { ok: false, result: null, error: { code: "ediabas_error", message: "EDIABAS job failed: SYS-0008: JOB NOT FOUND", details: { errorCode: 19 } } },
      );
      expect(t).toContain(
        "  Info memory: not read ([ediabas_error] EDIABAS job failed: SYS-0008: JOB NOT FOUND). Many modules have no info memory (no IS_LESEN job); ediabas_describe NVE_65 shows whether this one has it.",
      );
    });

    it("when the fault-memory read failed too, the info memory error gets the normal error text", () => {
      const t = report(noResponse, noResponse);
      const normal = `error: ${itemErrorText(noResponse.error)}`;
      expect(t).toContain(`  Fault memory: ${normal}\n  Info memory: ${normal}`);
      expect(t).toContain("IFH-0009");
      expect(t).not.toContain("Many modules have no info memory");
    });

    it("a fault-memory stand-in error ({ error }) also gives the info memory the normal error text", () => {
      const t = report({ error: "The VM does not appear to be running." }, noResponse);
      expect(t).toContain(`  Info memory: error: ${itemErrorText(noResponse.error)}`);
      expect(t).not.toContain("Many modules have no info memory");
    });

    it("results that could not be read mean IS_LESEN ran: normal error text even after a good fault-memory read", () => {
      const unread = { ok: false as const, result: null, error: { code: "ediabas_error", message: "x", details: { errorCode: 98, resultsUnread: true } } };
      const t = report({ ok: true, result: result([]), error: null }, unread);
      expect(t).toContain(`  Info memory: error: ${itemErrorText(unread.error)}`);
      expect(t).not.toContain("Many modules have no info memory");
    });

    it("an error other than ediabas_error on IS_LESEN is unchanged", () => {
      const busy = { ok: false as const, result: null, error: { code: "cable_in_use", message: "x", details: { processes: [{ name: "INPALOAD", pid: 1 }] } } };
      const t = report({ ok: true, result: result([]), error: null }, busy);
      expect(t).toContain(`  Info memory: error: ${itemErrorText(busy.error)}`);
      expect(t).toContain("[cable_in_use]");
    });
  });
});

describe("job results", () => {
  it("prints system results and each set, distances miles first", () => {
    const t = formatJobResult(result([{ STAT_KM_STAND: 160935, STAT_KILOMETERSTAND_WERT: 1000, X_WERT: 5, X_EINH: "km", SPEED_KM_H: 50 }]));
    expect(t).toContain("NVE_65 FS_LESEN: job status OKAY, variant NVE_65, 300 ms, 1 result set");
    expect(t).toContain("System results: VARIANTE=NVE_65 SAETZE=1");
    expect(t).toContain("  STAT_KM_STAND=100,000 mi (160,935 km)");
    expect(t).toContain("  STAT_KILOMETERSTAND_WERT=621 mi (1,000 km)");
    expect(t).toContain("  X_WERT=3 mi (5 km)");
    expect(t).toContain("  SPEED_KM_H=50");
  });

  it("raw is the agent's JSON with strings cleaned", () => {
    const t = formatJobRaw(result([{ A: "x\u0007y", B: null, C: "DEADBEEF" }]));
    expect(JSON.parse(t).sets[0]).toEqual({ A: "x y", B: null, C: "DEADBEEF" });
  });

  it("maps per-item errors, and unknown codes with their message", () => {
    expect(itemErrorText({ code: "weird_code", message: "something\nodd" })).toBe("[weird_code] something odd");
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

  describe("description hash", () => {
    const body = (review: string, hash?: string | null) => ({
      ecu: "NVE_65",
      variant: "NVE_65",
      job: "STATUS_X",
      comment: "Liest Status",
      ...(hash === undefined ? {} : { descriptionHash: hash }),
      verdict: { allowed: true, rule: "allow.pattern", review },
      arguments: [],
      results: [],
      durationMs: 5,
    });
    const mac = { allowed: true, rule: "allow.pattern", screen: true };
    const approve =
      'To approve this job as it is described now, the user adds "NVE_65:STATUS_X@0123456789ab" to reviewedJobs in C:\\ProgramData\\EdiabasBridge\\config.json in the VM (no restart needed).';

    it("needs_review: the hash and the line to paste, directly under the description", () => {
      const lines = formatDescribeJob(DescribeJobSchema.parse(body("needs_review", "0123456789ab")), mac).split("\n");
      const at = lines.indexOf("Description: Liest Status");
      expect(lines[at + 1]).toBe("Description hash: 0123456789ab");
      expect(lines[at + 2]).toBe(approve);
    });

    it("reviewed: only the hash", () => {
      const t = formatDescribeJob(DescribeJobSchema.parse(body("reviewed", "0123456789ab")), mac);
      expect(t).toContain("\nDescription hash: 0123456789ab\n");
      expect(t).not.toContain("To approve");
    });

    it("not_required: neither", () => {
      const t = formatDescribeJob(DescribeJobSchema.parse(body("not_required", "0123456789ab")), mac);
      expect(t).not.toContain("Description hash");
      expect(t).not.toContain("To approve");
    });

    it("a hash that is not 12 lower-case hex digits: neither (the line would approve nothing)", () => {
      for (const hash of ["0123456789AB", "0123456789a", "0123456789abc", 'x" , "CAS:Y']) {
        const t = formatDescribeJob(DescribeJobSchema.parse(body("needs_review", hash)), mac);
        expect(t).not.toContain("Description hash");
        expect(t).not.toContain("To approve");
      }
    });

    it("null hash, or an older agent without the field: neither", () => {
      for (const hash of [null, undefined]) {
        const t = formatDescribeJob(DescribeJobSchema.parse(body("needs_review", hash)), mac);
        expect(t).not.toContain("Description hash");
        expect(t).not.toContain("To approve");
      }
    });
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

describe("results that could not be read", () => {
  const text = (details: unknown) => itemErrorText({ code: "ediabas_error", message: "The job finished but its results could not be read.", details });
  const head = "[ediabas_error] The job finished but its results could not be read";
  const tail = " Ask the user before trying again.";

  it("shows the number and text EDIABAS gave, or whichever one it gave, or neither", () => {
    expect(text({ errorCode: 92, errorText: "SYS-0002", resultsUnread: true })).toBe(`${head} (EDIABAS error 92: SYS-0002).${tail}`);
    expect(text({ errorCode: 92, resultsUnread: true })).toBe(`${head} (EDIABAS error 92).${tail}`);
    expect(text({ errorText: "SYS-0002", resultsUnread: true })).toBe(`${head} (EDIABAS SYS-0002).${tail}`);
    expect(text({ resultsUnread: true })).toBe(`${head}.${tail}`);
  });

  it("is only used when the agent sets resultsUnread to true", () => {
    for (const details of [{ errorCode: 92, errorText: "SYS-0002" }, { errorCode: 92, errorText: "SYS-0002", resultsUnread: "true" }]) {
      expect(text(details)).toMatch(/^\[ediabas_error\] EDIABAS reported error 92: SYS-0002\. This often means the ECU did not answer/);
    }
  });
});

describe("vehicle status", () => {
  const FIXED = [
    { ecu: "UTILITY", job: "STATUS_ZUENDUNG" },
    { ecu: "UTILITY", job: "STATUS_UBATT" },
    { ecu: "TMODE", job: "LESE_SPANNUNG_KL30" },
    { ecu: "TMODE", job: "LESE_SPANNUNG_KL15" },
  ];

  it("vehicleBatch: four readings in D10 order without voltage sources", () => {
    expect(vehicleBatch([])).toEqual(FIXED);
  });

  it("vehicleBatch: a voltage source goes third, after ignition and battery", () => {
    const b = vehicleBatch([{ ecu: "NVE_65", job: "STATUS_BORDNETZSPANNUNG" }]);
    expect(b).toHaveLength(5);
    expect(b[2]).toEqual({ ecu: "NVE_65", job: "STATUS_BORDNETZSPANNUNG" });
    expect([b[0], b[1], b[3], b[4]]).toEqual(FIXED);
  });

  // Rehearsal, 2026-11-22, cable in the VM and not in the car: STAT_UBATT=1 and KL30 E02E0000.
  const VEHICLE_NOTE =
    "Note: STAT_UBATT and the cable's KL30 come from the cable's own lines, not from the car: with no car connected (desk, 2026-11-22) " +
    "they read STAT_UBATT=1 and KL30 E02E0000 (12000 read little-endian). They do not show that the car is awake or how charged " +
    "the battery is; only a reading from a module (a voltage source) shows that.";

  it("formatVehicle reads the items in that order", () => {
    const ok = (ecu: string, job: string, set: Record<string, unknown>) => ({ ok: true, result: JobResultSchema.parse(jobResult(ecu, job, [set])) });
    const sources = [{ ecu: "NVE_65", job: "STATUS_BORDNETZSPANNUNG" }];
    const items = [
      ok("UTILITY", "STATUS_ZUENDUNG", { STAT_ZUENDUNG: 1, JOB_STATUS: "OKAY" }),
      ok("UTILITY", "STATUS_UBATT", { STAT_UBATT: 12480 }),
      ok("NVE_65", "STATUS_BORDNETZSPANNUNG", { STAT_SPANNUNG_WERT: 12.4 }),
      ok("TMODE", "LESE_SPANNUNG_KL30", { SPANNUNG_KL30: 12510 }),
      { ok: false, result: null, error: { code: "ediabas_error", message: "failed", details: { errorCode: 19, errorText: "IFH-0009: NO RESPONSE FROM CONTROLUNIT" } } },
    ];
    const t = formatVehicle({ items }, sources).split("\n");
    expect(t[0]).toBe("Vehicle status (each reading's result fields as returned; their meaning is not yet confirmed at the car):");
    expect(t[1]).toBe("Ignition (UTILITY STATUS_ZUENDUNG): job status OKAY; STAT_ZUENDUNG=1");
    expect(t[2]).toBe("Battery (UTILITY STATUS_UBATT): job status OKAY; STAT_UBATT=12480");
    expect(t[3]).toBe("Voltage (NVE_65 STATUS_BORDNETZSPANNUNG): job status OKAY; STAT_SPANNUNG_WERT=12.4");
    expect(t[4]).toBe("Cable KL30 (TMODE LESE_SPANNUNG_KL30): job status OKAY; SPANNUNG_KL30=12510");
    expect(t[5]).toMatch(/^Cable KL15 \(TMODE LESE_SPANNUNG_KL15\): error: \[ediabas_error\] EDIABAS reported error 19: IFH-0009: NO RESPONSE FROM CONTROLUNIT/);
    expect(t[6]).toBe(VEHICLE_NOTE);
    expect(t).toHaveLength(7);
  });

  it("formatVehicle says where voltage sources are configured when there are none", () => {
    const item = { ok: true, result: JobResultSchema.parse(jobResult("TMODE", "X", [])) };
    expect(formatVehicle({ items: [item, item, item, item] }, [])).toContain("Voltage sources: none configured (voltageSources in config.local.json on the Mac).");
  });
});
