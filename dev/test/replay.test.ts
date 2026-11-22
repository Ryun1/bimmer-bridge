import { spawn } from "node:child_process";
import { mkdtempSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import {
  BIND_HOST,
  DEFAULT_FIXTURES,
  ReplayConfigError,
  findRecording,
  jsonEqual,
  loadScenario,
  parseArgs,
  startReplayAgent,
  type ReplayAgent,
} from "../replay-agent.ts";
import { TOKEN, agentError, synthHealth, synthJobResult, writeRecording, writeScenarioFile } from "./synthetic.ts";

const DEV = resolve(dirname(fileURLToPath(import.meta.url)), "..");

let root: string;
let agent: ReplayAgent | undefined;
let logs: string[];

beforeEach(() => {
  root = mkdtempSync(join(tmpdir(), "replay-agent-test-"));
  logs = [];
});
afterEach(async () => {
  await agent?.close();
  agent = undefined;
  rmSync(root, { recursive: true, force: true });
});

async function start(scenario: string): Promise<ReplayAgent> {
  agent = await startReplayAgent({ scenario: loadScenario(root, scenario), token: TOKEN, port: 0, log: (l) => logs.push(l) });
  return agent;
}

async function call(path: string, init: { method?: string; body?: string; token?: string | null } = {}) {
  const headers: Record<string, string> = {};
  if (init.token !== null) headers.authorization = `Bearer ${init.token ?? TOKEN}`;
  if (init.body !== undefined) headers["content-type"] = "application/json; charset=utf-8";
  const res = await fetch(agent!.url + path, { method: init.method ?? "GET", headers, body: init.body });
  const bytes = Buffer.from(await res.arrayBuffer());
  return { status: res.status, headers: res.headers, bytes, json: () => JSON.parse(bytes.toString("utf8")) as any };
}

/** A small "normal" scenario: health, describe with a query, a job with a JSON body. */
function normalScenario(name = "normal"): string {
  const dir = join(root, name);
  writeRecording(dir, { method: "GET", path: "/health", response: synthHealth() });
  writeRecording(dir, {
    method: "GET",
    path: "/ediabas/describe",
    query: { ecu: "SYNTH_ECU", filter: "LESEN", comments: "true" },
    response: { ecu: "SYNTH_ECU", variant: "SYNTH_ECU", jobCount: 0, jobs: [], durationMs: 1 },
  });
  writeRecording(dir, {
    method: "POST",
    path: "/ediabas/job",
    body: { ecu: "SYNTH_ECU", job: "STATUS_SYNTH", args: "", results: "" },
    response: synthJobResult("SYNTH_ECU", "STATUS_SYNTH", [{ SYNTH_VALUE: 1 }]),
  });
  return dir;
}

describe("loading scenarios", () => {
  it("loads recordings and ignores images and scenario.json as recordings", () => {
    normalScenario();
    const s = loadScenario(root, "normal");
    expect(s.recordings).toHaveLength(3);
    expect(s.base).toBeNull();
  });

  it("refuses names that could leave the fixtures folder", () => {
    for (const bad of ["../etc", "a/b", ".hidden", "", "x".repeat(65)]) {
      expect(() => loadScenario(root, bad)).toThrow(ReplayConfigError);
    }
  });

  it("fails loudly on a missing folder, a broken recording, an empty scenario", () => {
    expect(() => loadScenario(root, "nope")).toThrow(/no scenario folder/);
    const dir = join(root, "broken");
    writeRecording(dir, { method: "GET", path: "/health", response: synthHealth() });
    writeFileSync(join(dir, "bad.json"), JSON.stringify({ request: { method: "DELETE", path: "/x" }, response: { status: 200, body: {} } }));
    expect(() => loadScenario(root, "broken")).toThrow(/bad\.json: request\.method must be GET or POST/);
    writeScenarioFile(join(root, "empty"), { description: "nothing" });
    expect(() => loadScenario(root, "empty")).toThrow(/has no recordings/);
  });

  it("rejects a base loop and a route to a recording that is not there", () => {
    writeScenarioFile(join(root, "a"), { base: "b" });
    writeScenarioFile(join(root, "b"), { base: "a" });
    expect(() => loadScenario(root, "a")).toThrow(/loops: a -> b -> a/);
    normalScenario("c");
    writeScenarioFile(join(root, "c"), { routes: [{ pathPrefix: "/ediabas/", recording: "missing.json" }] });
    expect(() => loadScenario(root, "c")).toThrow(/route recording missing\.json/);
    writeScenarioFile(join(root, "c"), { routes: [{ pathPrefix: "/ediabas/", recording: "../x.json" }] });
    expect(() => loadScenario(root, "c")).toThrow(/file name of a recording in this folder/);
  });

  it("every scenario committed in dev/fixtures loads", () => {
    const names = readdirSync(DEFAULT_FIXTURES, { withFileTypes: true }).filter((d) => d.isDirectory()).map((d) => d.name);
    for (const n of names) expect(() => loadScenario(DEFAULT_FIXTURES, n), n).not.toThrow();
  });

  it("no committed recording asks for the removed GET /ediabas/vehicle; inpa-open has the vehicle batch as POST /ediabas/jobs", () => {
    const names = readdirSync(DEFAULT_FIXTURES, { withFileTypes: true }).filter((d) => d.isDirectory()).map((d) => d.name);
    for (const n of names) expect(loadScenario(DEFAULT_FIXTURES, n).recordings.filter((r) => r.path === "/ediabas/vehicle"), n).toEqual([]);
    const items = ["UTILITY STATUS_ZUENDUNG", "UTILITY STATUS_UBATT", "TMODE LESE_SPANNUNG_KL30", "TMODE LESE_SPANNUNG_KL15"].map((x) => {
      const [ecu, job] = x.split(" ");
      return { ecu, job };
    });
    const m = findRecording(loadScenario(DEFAULT_FIXTURES, "inpa-open"), { method: "POST", path: "/ediabas/jobs", query: [], body: { items } });
    expect(m?.how).toBe("exact");
    expect(m?.recording.response.status).toBe(409);
    expect((m?.recording.response.json as { error: { code: string } }).error.code).toBe("cable_in_use");
  });
});

describe("matching", () => {
  it("JSON equality ignores key order but not array order or extra keys", () => {
    expect(jsonEqual({ a: 1, b: { c: [1, 2] } }, { b: { c: [1, 2] }, a: 1 })).toBe(true);
    expect(jsonEqual({ a: [1, 2] }, { a: [2, 1] })).toBe(false);
    expect(jsonEqual({ a: 1 }, { a: 1, b: null })).toBe(false);
    expect(jsonEqual(null, {})).toBe(false);
  });

  it("matches method, path, query in any order and the JSON body in any key order", async () => {
    normalScenario();
    await start("normal");
    expect((await call("/health")).json().agent).toBe("ediabas-bridge-agent");
    const d = await call("/ediabas/describe?comments=true&filter=LESEN&ecu=SYNTH_ECU");
    expect(d.status).toBe(200);
    expect(d.json().ecu).toBe("SYNTH_ECU");
    const j = await call("/ediabas/job", { method: "POST", body: JSON.stringify({ results: "", args: "", job: "STATUS_SYNTH", ecu: "SYNTH_ECU" }) });
    expect(j.status).toBe(200);
    expect(j.json().sets).toEqual([{ SYNTH_VALUE: 1 }]);
    expect(j.headers.get("content-type")).toBe("application/json; charset=utf-8");
  });

  it("does not match a different method, a missing or extra query parameter, or another body", async () => {
    normalScenario();
    await start("normal");
    expect((await call("/health", { method: "POST", body: "{}" })).status).toBe(404);
    expect((await call("/ediabas/describe?ecu=SYNTH_ECU&filter=LESEN")).status).toBe(404);
    expect((await call("/ediabas/describe?ecu=SYNTH_ECU&filter=LESEN&comments=true&job=X")).status).toBe(404);
    const other = await call("/ediabas/job", { method: "POST", body: JSON.stringify({ ecu: "SYNTH_ECU", job: "STATUS_SYNTH" }) });
    expect(other.status).toBe(404);
  });

  it("with several matching recordings, serves the one recorded last", async () => {
    const dir = join(root, "dup");
    writeRecording(dir, { method: "GET", path: "/health", response: synthHealth({ uptimeSeconds: 1 }), recordedAt: "2026-11-21T12:00:00.000Z" });
    writeRecording(dir, { method: "GET", path: "/health", response: synthHealth({ uptimeSeconds: 2 }), recordedAt: "2026-11-21T12:05:00.000Z" });
    await start("dup");
    expect((await call("/health")).json().uptimeSeconds).toBe(2);
    expect(logs.at(-1)).toMatch(/2 recordings matched; serving the latest/);
  });
});

describe("no recording", () => {
  it("answers 404 no_recording naming what was asked, never an invented answer", async () => {
    normalScenario();
    await start("normal");
    const r = await call("/ediabas/job", { method: "POST", body: JSON.stringify({ ecu: "SYNTH_ECU", job: "FS_LESEN" }) });
    expect(r.status).toBe(404);
    const e = r.json().error;
    expect(e.code).toBe("no_recording");
    expect(e.message).toContain("scenario normal");
    expect(e.message).toContain('POST /ediabas/job with body {"ecu":"SYNTH_ECU","job":"FS_LESEN"}');
    expect(e.message).toMatch(/never makes one up/);
    expect(e.details).toEqual({ scenario: "normal", method: "POST", path: "/ediabas/job", query: {}, body: { ecu: "SYNTH_ECU", job: "FS_LESEN" } });
    const q = await call("/logs?glob=*.trc&limit=5");
    expect(q.json().error.message).toContain("GET /logs?glob=*.trc&limit=5");
  });

  it("answers a body that is not JSON with bad_request", async () => {
    normalScenario();
    await start("normal");
    const r = await call("/ediabas/job", { method: "POST", body: "{not json" });
    expect(r.status).toBe(400);
    expect(r.json().error.code).toBe("bad_request");
  });
});

describe("auth and methods", () => {
  it("binds to loopback only", async () => {
    normalScenario();
    await start("normal");
    expect(BIND_HOST).toBe("127.0.0.1");
    expect(agent!.url.startsWith("http://127.0.0.1:")).toBe(true);
    expect((agent!.server.address() as { address: string }).address).toBe("127.0.0.1");
  });

  it("401 without or with a wrong token, before any routing, with WWW-Authenticate", async () => {
    normalScenario();
    await start("normal");
    for (const token of [null, "wrong-token", ""] as const) {
      for (const path of ["/health", "/no/such/path"]) {
        const r = await call(path, { token });
        expect(r.status).toBe(401);
        expect(r.headers.get("www-authenticate")).toBe("Bearer");
        expect(r.json().error.code).toBe("unauthorized");
      }
    }
  });

  it("serves GET and POST only", async () => {
    normalScenario();
    await start("normal");
    for (const method of ["PUT", "DELETE", "PATCH"]) {
      const r = await call("/health", { method });
      expect(r.status).toBe(405);
      expect(r.json().error.code).toBe("method_not_allowed");
    }
  });

  it("refuses to start without a token", async () => {
    normalScenario();
    await expect(startReplayAgent({ scenario: loadScenario(root, "normal"), token: "", port: 0 })).rejects.toThrow(/token is required/);
  });
});

describe("response kinds", () => {
  it("serves a recorded image with its content type and x-* headers", async () => {
    const dir = join(root, "screen");
    const png = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3, 4]);
    writeRecording(dir, {
      method: "GET",
      path: "/screen/capture",
      query: { window: "desktop", maxEdge: "1600" },
      headers: { "content-type": "image/png", "x-window-title": "desktop", "x-capture-method": "screen", "x-image-width": "4", "x-image-height": "3" },
      image: { file: "synthetic-capture.png", bytes: png },
    });
    await start("screen");
    const r = await call("/screen/capture?maxEdge=1600&window=desktop");
    expect(r.status).toBe(200);
    expect(r.headers.get("content-type")).toBe("image/png");
    expect(r.headers.get("x-capture-method")).toBe("screen");
    expect(r.headers.get("x-image-width")).toBe("4");
    expect(r.bytes.equals(png)).toBe(true);
  });

  it("refuses an image file name with a path in it", () => {
    const dir = join(root, "evil");
    writeRecording(dir, { method: "GET", path: "/health", response: synthHealth() });
    writeFileSync(
      join(dir, "x.json"),
      JSON.stringify({ request: { method: "GET", path: "/screen/capture", query: {}, body: null }, response: { status: 200, headers: {}, imageFile: "../secret.png" } }),
    );
    expect(() => loadScenario(root, "evil")).toThrow(/imageFile must be a \.jpg or \.png file name/);
  });

  it("serves bodyText and a recorded error status as recorded", async () => {
    const dir = join(root, "text");
    writeRecording(dir, { method: "GET", path: "/windows", headers: { "content-type": "text/plain" }, bodyText: "not json" });
    writeRecording(dir, {
      method: "POST",
      path: "/ediabas/job",
      body: { ecu: "SYNTH_ECU", job: "STATUS_SYNTH" },
      status: 403,
      response: agentError("job_needs_review", "SYNTH needs review", { ecu: "SYNTH_ECU", job: "STATUS_SYNTH", description: "synthetic description", matched: [] }),
    });
    await start("text");
    const t = await call("/windows");
    expect(t.headers.get("content-type")).toBe("text/plain");
    expect(t.bytes.toString()).toBe("not json");
    const e = await call("/ediabas/job", { method: "POST", body: JSON.stringify({ job: "STATUS_SYNTH", ecu: "SYNTH_ECU" }) });
    expect(e.status).toBe(403);
    expect(e.json().error.code).toBe("job_needs_review");
  });
});

describe("overlays", () => {
  /** A "tool open" overlay: its own /health, and one recorded cable_in_use for every /ediabas/* request. */
  function toolOpenOverlay(): void {
    normalScenario("normal");
    const dir = join(root, "tool-open");
    writeRecording(dir, { method: "GET", path: "/health", response: synthHealth({ conflictingProcesses: [{ name: "SYNTH_TOOL", pid: 1 }] }) });
    const busy = writeRecording(dir, {
      method: "POST",
      path: "/ediabas/job",
      body: { ecu: "SYNTH_ECU", job: "STATUS_SYNTH" },
      status: 409,
      response: agentError("cable_in_use", "synthetic: cable in use", { processes: [{ name: "SYNTH_TOOL", pid: 1 }] }),
    });
    writeScenarioFile(dir, { description: "synthetic tool-open overlay", base: "normal", routes: [{ method: "*", pathPrefix: "/ediabas/", recording: busy }] });
  }

  it("serves the overlay's own recordings first, then its routes, then the base", async () => {
    toolOpenOverlay();
    const s = loadScenario(root, "tool-open");
    expect(s.base?.name).toBe("normal");
    await start("tool-open");
    expect((await call("/health")).json().conflictingProcesses).toEqual([{ name: "SYNTH_TOOL", pid: 1 }]);
    // The base has an exact recording for this describe, but the overlay's route wins.
    const d = await call("/ediabas/describe?ecu=SYNTH_ECU&filter=LESEN&comments=true");
    expect(d.status).toBe(409);
    expect(d.json().error.code).toBe("cable_in_use");
    const j = await call("/ediabas/jobs", { method: "POST", body: JSON.stringify({ items: [] }) });
    expect(j.json().error.code).toBe("cable_in_use");
    expect(logs.at(-1)).toMatch(/tool-open\/.*\[route\]/);
  });

  it("falls through to the base for anything the overlay does not cover", async () => {
    toolOpenOverlay();
    const dir = join(root, "normal");
    writeRecording(dir, { method: "GET", path: "/windows", response: { windows: [] } });
    await start("tool-open");
    const w = await call("/windows");
    expect(w.status).toBe(200);
    expect(w.json()).toEqual({ windows: [] });
    expect(logs.at(-1)).toMatch(/from normal\/.*\[exact\]/);
  });

  it("findRecording reports the layer and how it matched", () => {
    toolOpenOverlay();
    const s = loadScenario(root, "tool-open");
    const m = findRecording(s, { method: "GET", path: "/ediabas/ecus", query: [], body: null });
    expect(m?.scenario).toBe("tool-open");
    expect(m?.how).toBe("route");
    expect(findRecording(s, { method: "GET", path: "/logs", query: [], body: null })).toBeNull();
  });
});

describe("command line", () => {
  it("takes the token from the flag or EDIABAS_AGENT_TOKEN and defaults the port to 8765", () => {
    expect(parseArgs(["--scenario", "x"], { EDIABAS_AGENT_TOKEN: "env-token" })).toMatchObject({ scenario: "x", port: 8765, token: "env-token" });
    expect(parseArgs(["--scenario", "x", "--token", "flag", "--port", "9000"], { EDIABAS_AGENT_TOKEN: "env" })).toMatchObject({ token: "flag", port: 9000 });
    expect(() => parseArgs(["--scenario", "x"], {})).toThrow(/no token/);
    expect(() => parseArgs(["--token", "t"], {})).toThrow(/--scenario is required/);
    expect(() => parseArgs(["--scenario", "x", "--token", "t", "--port", "0"], {})).toThrow(/--port/);
    expect(() => parseArgs(["--scenario", "x", "--token", "t", "--bind", "0.0.0.0"], {})).toThrow(/unknown argument --bind/);
  });

  it("runs under plain node (type stripping) and answers with the token", async () => {
    normalScenario();
    const port = await freePort();
    const child = spawn(process.execPath, [join(DEV, "replay-agent.ts"), "--scenario", "normal", "--fixtures", root, "--port", String(port)], {
      env: { ...process.env, EDIABAS_AGENT_TOKEN: TOKEN },
      stdio: ["ignore", "ignore", "pipe"],
    });
    try {
      let err = "";
      await new Promise<void>((ok, bad) => {
        const timer = setTimeout(() => bad(new Error(`replay agent did not start: ${err}`)), 10_000);
        child.stderr!.on("data", (c: Buffer) => {
          err += c.toString();
          if (err.includes("listening on")) {
            clearTimeout(timer);
            ok();
          }
        });
        child.on("exit", (code) => bad(new Error(`exited ${code}: ${err}`)));
      });
      expect(err).toContain(`listening on http://127.0.0.1:${port}`);
      const ok = await fetch(`http://127.0.0.1:${port}/health`, { headers: { authorization: `Bearer ${TOKEN}` } });
      expect(ok.status).toBe(200);
      const no = await fetch(`http://127.0.0.1:${port}/health`);
      expect(no.status).toBe(401);
    } finally {
      child.kill("SIGTERM");
    }
  });

  it("exits 2 with usage when there is no token", async () => {
    normalScenario();
    const env = { ...process.env };
    delete env.EDIABAS_AGENT_TOKEN;
    const child = spawn(process.execPath, [join(DEV, "replay-agent.ts"), "--scenario", "normal", "--fixtures", root], { env, stdio: ["ignore", "ignore", "pipe"] });
    let err = "";
    child.stderr!.on("data", (c: Buffer) => (err += c.toString()));
    const code = await new Promise<number | null>((ok) => child.on("exit", ok));
    expect(code).toBe(2);
    expect(err).toMatch(/no token/);
  });
});

function freePort(): Promise<number> {
  return new Promise((ok, bad) => {
    const s = createServer();
    s.once("error", bad);
    s.listen(0, "127.0.0.1", () => {
      const p = (s.address() as { port: number }).port;
      s.close(() => ok(p));
    });
  });
}
