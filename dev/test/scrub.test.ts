import { spawnSync } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { loadScenario, findRecording } from "../replay-agent.ts";
import { isIdentifierField, scrubFolder, scrubRecording, shapePlaceholder } from "../scrub-fixtures.ts";
import { FAKE_VIN, agentError, synthHealth, synthJobResult, writeRecording } from "./synthetic.ts";

const DEV = resolve(dirname(fileURLToPath(import.meta.url)), "..");
/** Shaped like a BMW VIN (WB...) but made up: SYNTH and TEST in the middle. */
const BMW_VIN = "WBASYNTH0TEST1234";

let root: string;
beforeEach(() => {
  root = mkdtempSync(join(tmpdir(), "scrub-test-"));
});
afterEach(() => rmSync(root, { recursive: true, force: true }));

function recording(path: string, responseBody: unknown, request: Record<string, unknown> = {}) {
  return {
    recordedAt: "2026-11-21T12:00:00.000Z",
    request: { method: "POST", path, query: {}, body: null, ...request },
    response: { status: 200, headers: { "content-type": "application/json; charset=utf-8" }, body: responseBody },
  };
}

describe("field names", () => {
  it("knows German and English names for chassis numbers, VINs and serial numbers", () => {
    for (const k of [
      "VIN",
      "VIN17",
      "vin",
      "FG_NR",
      "FG_NR_LANG",
      "FGNR",
      "FG",
      "FAHRGESTELLNUMMER",
      "FAHRGESTELL_NR",
      "FAHRZEUGIDENTNUMMER",
      "CHASSIS_NUMBER",
      "chassisNo",
      "SERIENNUMMER",
      "ID_SERIEN_NR",
      "SERIAL_NUMBER",
      "serialNumber",
      "ID_SER_NR",
    ]) {
      expect(isIdentifierField(k), k).toBe(true);
    }
  });

  it("leaves ordinary result names alone", () => {
    for (const k of ["F_ORT_NR", "F_ORT_TEXT", "F_UW_KM", "JOB_STATUS", "VARIANTE", "PROVINZ", "job", "ecu", "user", "STATUS_UBATT", "F_HFK", "ID_BMW_NR", "comment"]) {
      expect(isIdentifierField(k), k).toBe(false);
    }
  });

  it("placeholders keep the shape", () => {
    expect(shapePlaceholder(FAKE_VIN)).toBe("XXXXX00000XXXX000");
    expect(shapePlaceholder("ab-12")).toBe("xx-00");
  });
});

describe("scrubRecording", () => {
  it("replaces VIN-shaped text anywhere and identifier fields by name, keeping type and shape", () => {
    const raw = recording(
      "/ediabas/job",
      synthJobResult("SYNTH_ECU", "SYNTH_JOB", [
        { FG_NR: "SYNTH12", SERIENNUMMER: 12345678, ID_SERIAL_HEX: "0A1B2C3D", F_ORT_NR: 42, F_ORT_TEXT: `stored for ${FAKE_VIN} today` },
        { NESTED: { VIN: FAKE_VIN }, LIST: [FAKE_VIN] },
      ]),
      { body: { ecu: "SYNTH_ECU", job: "SYNTH_JOB" } },
    );
    const { recording: out, replacements } = scrubRecording("r.json", raw);
    const sets = (out.response as any).body.sets;
    expect(sets[0].FG_NR).toBe("XXXXX00");
    expect(sets[0].SERIENNUMMER).toBe(10000000);
    expect(sets[0].ID_SERIAL_HEX).toBe("00000000");
    expect(sets[0].F_ORT_NR).toBe(42);
    expect(sets[0].F_ORT_TEXT).toBe("stored for XXXXX00000XXXX000 today");
    expect(sets[1].NESTED.VIN).toBe("XXXXX00000XXXX000");
    expect(sets[1].LIST[0]).toBe("XXXXX00000XXXX000");
    expect((out.request as any).body).toEqual({ ecu: "SYNTH_ECU", job: "SYNTH_JOB" });

    const fields = replacements.map((r) => `${r.field} ${r.rule} ${r.placeholder}`);
    expect(fields).toContain("response.body.sets[0].FG_NR identifier-field:FG_NR XXXXX00");
    expect(fields).toContain("response.body.sets[0].SERIENNUMMER identifier-field:SERIENNUMMER 10000000");
    expect(fields).toContain("response.body.sets[0].F_ORT_TEXT vin-shaped XXXXX00000XXXX000");
    // The report never carries the original value.
    expect(JSON.stringify(replacements)).not.toContain(FAKE_VIN);
    expect(JSON.stringify(replacements)).not.toContain("SYNTH12");
    expect(JSON.stringify(replacements)).not.toContain("12345678");
  });

  it("does not touch 17-character runs that are not VIN-shaped", () => {
    const keep = ["12345678901234567", "ABCDEFGHJKLMNPRST", "SYNTH00000TEST1234", "0123456789ABCDEF01", "STATUS_LESEN_ABCDE"];
    const { recording: out, replacements } = scrubRecording("r.json", recording("/ediabas/job", { values: keep }));
    expect((out.response as any).body.values).toEqual(keep);
    expect(replacements).toEqual([]);
  });

  it("finds a VIN in ASCII inside a hex-encoded binary result", () => {
    const hex = Buffer.from(`\x01\x02${FAKE_VIN}\x03`, "latin1").toString("hex").toUpperCase();
    const { recording: out, replacements } = scrubRecording("r.json", recording("/ediabas/job", { sets: [{ BINARY_DATA: hex }] }));
    const got = Buffer.from((out.response as any).body.sets[0].BINARY_DATA, "hex").toString("latin1");
    expect(got).toBe("\x01\x02XXXXX00000XXXX000\x03");
    expect(replacements[0]?.rule).toBe("vin-in-hex");
  });

  it("replaces the user and host name in /health and Windows profile folders anywhere", () => {
    const raw = recording("/health", synthHealth({ hostName: "SYNTH-PC", ediabas: { dllPath: "C:\\Users\\synthuser\\EDIABAS\\api32.dll", dllFound: true } }), { method: "GET" });
    const { recording: out, replacements } = scrubRecording("h.json", raw);
    const body = (out.response as any).body;
    expect(body.user).toBe("user");
    expect(body.hostName).toBe("HOST");
    expect(body.ediabas.dllPath).toBe("C:\\Users\\user\\EDIABAS\\api32.dll");
    expect(body.agent).toBe("ediabas-bridge-agent");
    expect(replacements.map((r) => r.rule).sort()).toEqual(["health-host", "health-user", "windows-user-folder"]);
    expect(JSON.stringify(replacements)).not.toContain("synthuser");
  });

  it("scrubs a window title in a screenshot's headers and keeps the image reference", () => {
    const raw = {
      recordedAt: "2026-11-21T12:00:00.000Z",
      request: { method: "GET", path: "/screen/capture", query: { window: "desktop" }, body: null },
      response: { status: 200, headers: { "content-type": "image/png", "x-window-title": `SYNTH ${FAKE_VIN}` }, imageFile: "cap.png" },
    };
    const r = scrubRecording("c.json", raw);
    expect((r.recording.response as any).headers["x-window-title"]).toBe("SYNTH XXXXX00000XXXX000");
    expect(r.imageFile).toBe("cap.png");
  });

  it("scrubs a percent-encoded window title (as the agent sends it) and re-encodes it", () => {
    // The agent percent-encodes every byte outside A-Z a-z 0-9 - . _ ~, so "%20" sits right
    // against the VIN and the user folder is "C%3A%5CUsers%5C...".
    const title = `SYNTH%20${FAKE_VIN}%20-%20C%3A%5CUsers%5Csynthuser%5Cnotes.txt`;
    const raw = {
      recordedAt: "2026-11-21T12:00:00.000Z",
      request: { method: "GET", path: "/screen/capture", query: { window: "desktop" }, body: null },
      response: { status: 200, headers: { "content-type": "image/png", "x-window-title": title }, imageFile: "cap.png" },
    };
    const r = scrubRecording("c.json", raw);
    const got = (r.recording.response as any).headers["x-window-title"];
    expect(got).toBe("SYNTH%20XXXXX00000XXXX000%20-%20C%3A%5CUsers%5Cuser%5Cnotes.txt");
    expect(r.replacements.map((x) => x.rule).sort()).toEqual(["vin-shaped", "windows-user-folder"]);
    expect(JSON.stringify(r.replacements)).not.toContain("synthuser");
    // An encoded string with nothing to scrub is left exactly as it was.
    const plain = scrubRecording("p.json", recording("/ediabas/job", { t: "INPA%20-%20Loader%3A%20x" }));
    expect((plain.recording.response as any).body.t).toBe("INPA%20-%20Loader%3A%20x");
    expect(plain.replacements).toEqual([]);
  });

  it("in hex results, scrubs a VIN-alphabet run whatever bytes touch it", () => {
    for (const ascii of [`1${FAKE_VIN}\0`, `FG${FAKE_VIN}`, `\x01${BMW_VIN}9\x02`]) {
      const hex = Buffer.from(ascii, "latin1").toString("hex");
      const { recording: out, replacements } = scrubRecording("r.json", recording("/ediabas/job", { sets: [{ RAW: hex }] }));
      const got = Buffer.from((out.response as any).body.sets[0].RAW, "hex").toString("latin1");
      expect(got, JSON.stringify(ascii)).not.toContain(FAKE_VIN);
      expect(got, JSON.stringify(ascii)).not.toContain(BMW_VIN);
      expect(got.length).toBe(ascii.length);
      expect(replacements.map((x) => x.rule)).toEqual(["vin-in-hex"]);
    }
  });

  it("in text, scrubs a BMW VIN in any case and with any neighbours", () => {
    const values = [`FG${BMW_VIN}`, BMW_VIN.toLowerCase(), `x${BMW_VIN}1`];
    const { recording: out, replacements } = scrubRecording("r.json", recording("/ediabas/job", { values }));
    expect((out.response as any).body.values).toEqual(["FGXXXXXXXX0XXXX0000", "xxxxxxxx0xxxx0000", "xXXXXXXXX0XXXX00001"]);
    expect(replacements.map((x) => x.rule)).toEqual(["bmw-vin", "bmw-vin", "bmw-vin"]);
  });

  it("is idempotent: scrubbing a scrubbed recording changes nothing", () => {
    const hex = Buffer.from(`1${FAKE_VIN}\0`, "latin1").toString("hex");
    const first = scrubRecording(
      "r.json",
      recording("/ediabas/job", {
        sets: [{ VIN: FAKE_VIN, SERIENNUMMER: 987, RAW: hex, T: `FG${BMW_VIN.toLowerCase()}`, E: `x%20${FAKE_VIN}%20C%3A%5CUsers%5Csynthuser` }],
      }),
    );
    expect(first.replacements.length).toBeGreaterThan(0);
    const second = scrubRecording("r.json", first.recording);
    expect(second.recording).toEqual(first.recording);
    expect(second.replacements).toEqual([]);
  });

  it("refuses something that is not a recording", () => {
    expect(() => scrubRecording("x.json", { hello: 1 })).toThrow(/not a recording/);
  });
});

describe("scrubFolder", () => {
  function rawFolder(): string {
    const raw = join(root, "raw");
    writeRecording(raw, { method: "GET", path: "/health", response: synthHealth() });
    writeRecording(raw, {
      method: "POST",
      path: "/ediabas/job",
      body: { ecu: "SYNTH_ECU", job: "SYNTH_IDENT" },
      response: synthJobResult("SYNTH_ECU", "SYNTH_IDENT", [{ FAHRGESTELL_NR: FAKE_VIN }]),
    });
    writeRecording(raw, {
      method: "GET",
      path: "/screen/capture",
      query: { window: "desktop", maxEdge: "1600" },
      headers: { "content-type": "image/png" },
      image: { file: "synthetic.png", bytes: Buffer.from([0x89, 0x50, 0x4e, 0x47]) },
    });
    writeRecording(raw, { method: "POST", path: "/ediabas/jobs", body: { items: [] }, status: 409, response: agentError("cable_in_use", "synthetic", { processes: [] }) });
    return raw;
  }

  it("copies scrubbed recordings and images into the scenario folder; the replay agent can serve them", () => {
    const raw = rawFolder();
    const dest = join(root, "fixtures", "synth");
    const r = scrubFolder(raw, dest);
    expect(r.written).toHaveLength(4);
    expect(r.images).toEqual(["synthetic.png"]);
    expect(readdirSync(dest).sort()).toEqual([...r.written, "synthetic.png"].sort());
    const all = readdirSync(dest).filter((f) => f.endsWith(".json")).map((f) => readFileSync(join(dest, f), "utf8")).join("\n");
    expect(all).not.toContain(FAKE_VIN);
    expect(all).not.toContain("synthuser");

    const s = loadScenario(join(root, "fixtures"), "synth");
    expect(s.recordings).toHaveLength(4);
    const m = findRecording(s, { method: "POST", path: "/ediabas/job", query: [], body: { job: "SYNTH_IDENT", ecu: "SYNTH_ECU" } });
    expect((m?.recording.response.json as any).sets[0].FAHRGESTELL_NR).toBe("XXXXX00000XXXX000");
  });

  it("dry run writes nothing; an existing fixture is never overwritten; a bad file stops everything", () => {
    const raw = rawFolder();
    const dest = join(root, "fixtures", "synth");
    scrubFolder(raw, dest, { dryRun: true });
    expect(existsSync(dest)).toBe(false);
    scrubFolder(raw, dest);
    expect(() => scrubFolder(raw, dest)).toThrow(/refusing to overwrite/);

    const raw2 = join(root, "raw2");
    writeRecording(raw2, { method: "GET", path: "/health", response: synthHealth() });
    writeFileSync(join(raw2, "zz-broken.json"), "{");
    const dest2 = join(root, "fixtures", "other");
    expect(() => scrubFolder(raw2, dest2)).toThrow(/zz-broken\.json: not valid JSON/);
    expect(existsSync(dest2)).toBe(false);
  });

  it("the command prints every replacement without the original, and flags images for review", () => {
    const raw = rawFolder();
    const fixtures = join(root, "fixtures");
    const r = spawnSync(process.execPath, [join(DEV, "scrub-fixtures.ts"), raw, "synth", "--fixtures", fixtures], { encoding: "utf8" });
    expect(r.status, r.stderr).toBe(0);
    expect(r.stdout).toMatch(/response\.body\.sets\[0\]\.FAHRGESTELL_NR {2}\[identifier-field:FAHRGESTELL_NR\] {2}-> XXXXX00000XXXX000/);
    expect(r.stdout).toMatch(/response\.body\.user {2}\[health-user\] {2}-> user/);
    expect(r.stdout).toMatch(/IMAGE NOT SCRUBBED: synthetic\.png/);
    expect(r.stdout).toMatch(/wrote 4 recordings and 1 images/);
    expect(r.stdout).not.toContain(FAKE_VIN);
    expect(r.stdout).not.toContain("synthuser");

    const bad = spawnSync(process.execPath, [join(DEV, "scrub-fixtures.ts"), raw, "../escape", "--fixtures", fixtures], { encoding: "utf8" });
    expect(bad.status).toBe(2);
    expect(bad.stderr).toMatch(/scenario name/);
  });
});
