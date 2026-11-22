import { mkdtemp, readFile, readdir, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createAppServer } from "../src/server.js";
import { loadConfig } from "../src/config.js";
import { PolicyGuard } from "../src/policy.js";
import type { ExecFileFn } from "../src/vmAddress.js";
import { AGENT_TOKEN, LOOPBACK_BRIDGE, healthBody, startFakeAgent, type FakeAgent } from "./fixtures.js";
import { agentErr, jobResult, stubHandler, type Responder } from "./stubAgent.js";

let dir: string;
let agent: FakeAgent;
beforeEach(async () => {
  dir = await mkdtemp(join(tmpdir(), "ediabas-tools-"));
  agent = await startFakeAgent(stubHandler());
});
afterEach(async () => {
  vi.restoreAllMocks();
  await agent.close();
  await rm(dir, { recursive: true, force: true });
});

interface Setup {
  env?: Record<string, string>;
  policy?: PolicyGuard;
  fetch?: typeof fetch;
  ediabasTimeoutMs?: number;
}

async function connect(s: Setup = {}) {
  const auditPath = join(dir, "logs", "audit.log");
  const env = { EDIABAS_AGENT_URL: agent.url, EDIABAS_AGENT_TOKEN: AGENT_TOKEN, EDIABAS_AUDIT_LOG: auditPath, ...s.env };
  const { config } = loadConfig(env, dir);
  const logs: string[] = [];
  const execFile = vi.fn<ExecFileFn>(async (file) => {
    if (file === "/usr/bin/security") return AGENT_TOKEN + "\n";
    throw new Error(`unexpected command ${file}`);
  });
  const server = createAppServer(config, {
    execFile,
    readFile: async () => "",
    networkInterfaces: () => LOOPBACK_BRIDGE as never,
    log: (m) => logs.push(m),
    policy: s.policy,
    fetch: s.fetch,
    ediabasTimeoutMs: s.ediabasTimeoutMs,
  });
  const [clientT, serverT] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: "test", version: "0.0.0" });
  await server.connect(serverT);
  await client.connect(clientT);
  const call = async (name: string, args: Record<string, unknown> = {}) => {
    const r = (await client.callTool({ name, arguments: args })) as {
      isError?: boolean;
      content: { type: string; text?: string; data?: string; mimeType?: string }[];
    };
    return { ...r, text: r.content.filter((c) => c.type === "text").map((c) => c.text).join("\n") };
  };
  const auditLines = async () => (await readFile(auditPath, "utf8")).trimEnd().split("\n");
  return { client, call, logs, auditLines };
}

function override(key: string, r: Responder): void {
  agent.setHandler(stubHandler({ [key]: r }));
}

/** Requests other than discovery's /health probe. */
function toolRequests(): FakeAgent["requests"] {
  return agent.requests.filter((r) => r.path !== "/health");
}

const brokenPolicy = () => PolicyGuard.load("/nonexistent/policy.json");

describe("tool list", () => {
  it("has exactly the tools in docs/tools.md, all read-only", async () => {
    const { client } = await connect();
    const { tools } = await client.listTools();
    expect(tools.map((t) => t.name).sort()).toEqual(
      [
        "ediabas_describe",
        "ediabas_job",
        "ediabas_list_ecus",
        "ediabas_read_faults",
        "ediabas_vehicle_status",
        "vm_health",
        "vm_list_windows",
        "vm_logs_list",
        "vm_logs_read",
        "vm_screenshot",
      ].sort(),
    );
    for (const t of tools) {
      expect(t.annotations).toMatchObject({ readOnlyHint: true, destructiveHint: false });
      expect(t.title).toBeTruthy();
    }
    const byName = Object.fromEntries(tools.map((t) => [t.name, t]));
    expect(Object.keys(byName.ediabas_job!.inputSchema.properties ?? {}).sort()).toEqual(["args", "ecu", "job", "raw", "results"]);
    expect(Object.keys(byName.ediabas_read_faults!.inputSchema.properties ?? {}).sort()).toEqual(["ecus", "includeInfoMemory"]);
    expect(Object.keys(byName.vm_logs_read!.inputSchema.properties ?? {}).sort()).toEqual(["grep", "length", "offset", "path", "tailBytes"]);
    expect(Object.keys(byName.vm_screenshot!.inputSchema.properties ?? {}).sort()).toEqual(["maxEdge", "window"]);
  });
});

describe("error results", () => {
  it("start with their code in brackets", async () => {
    const { call } = await connect();
    const refused = await call("ediabas_job", { ecu: "NVE_65", job: "FS_LOESCHEN" });
    expect(refused.text).toMatch(/^\[job_not_allowed\] Refused by the job policy/);
    override("POST /ediabas/job", () => agentErr(409, "cable_in_use", "x", null));
    const busy = await call("ediabas_job", { ecu: "NVE_65", job: "IDENT" });
    expect(busy.text).toMatch(/^\[cable_in_use\] The diagnostic cable is in use/);
    const bad = await call("vm_logs_read", { path: "C:\\x.log", offset: 5 });
    expect(bad.text).toMatch(/^\[invalid_arguments\] /);
  });

  it("gives a job request that may have run its own code", async () => {
    agent.setHandler((req, res) => {
      if (req.url === "/health") stubHandler()(req, res);
      else req.socket.destroy();
    });
    const { call, auditLines } = await connect();
    await call("vm_health");
    const r = await call("ediabas_job", { ecu: "NVE_65", job: "IDENT" });
    expect(r.text).toMatch(/^\[ediabas_request_lost\] The connection to the VM agent broke/);
    expect((await auditLines()).at(-1)).toContain("\tediabas_request_lost\t");
  });
});

describe("vm_health", () => {
  it("shows the agent's features and policy version against the Mac's", async () => {
    const { call } = await connect();
    const r = await call("vm_health");
    expect(r.isError).toBeFalsy();
    expect(r.text).toContain("Agent features: ediabas, capture, logs");
    expect(r.text).toContain("Job policy: agent version 2; Mac version 2.");
    expect(r.text).not.toMatch(/WARNING/);
  });

  it("warns when the agent's policy version differs", async () => {
    override("GET /health", () => ({ status: 200, body: healthBody({ policyVersion: 3, capabilities: ["ediabas"] }) }));
    const { call } = await connect();
    const r = await call("vm_health");
    expect(r.text).toMatch(/WARNING: the agent's job policy \(version 3\) differs from this Mac's \(version 2\)/);
    expect(r.text).toMatch(/Update line/);
  });

  it("warns about an agent still on policy version 1 (no description-screen ignore list)", async () => {
    override("GET /health", () => ({ status: 200, body: healthBody({ policyVersion: 1, capabilities: ["ediabas"] }) }));
    const { call } = await connect();
    const r = await call("vm_health");
    expect(r.text).toMatch(/WARNING: the agent's job policy \(version 1\) differs from this Mac's \(version 2\)/);
    expect(r.text).toMatch(/Update line/);
  });

  it("tolerates an older agent without the new fields", async () => {
    override("GET /health", () => ({ status: 200, body: healthBody() }));
    const { call } = await connect();
    const r = await call("vm_health");
    expect(r.isError).toBeFalsy();
    expect(r.text).toMatch(/Agent features: not reported/);
    expect(r.text).toMatch(/agent does not report one/);
  });

  it("says whether EDIABAS is loaded when the agent reports it", async () => {
    override("GET /health", () => ({ status: 200, body: healthBody({ ediabas: { dllPath: "C:\\EDIABAS\\BIN\\api32.dll", dllFound: true, loaded: false } }) }));
    const { call } = await connect();
    expect((await call("vm_health")).text).toMatch(/EDIABAS: not loaded .*about 10 s longer/);
  });

  it("says when the Mac's policy could not be loaded", async () => {
    const { call } = await connect({ policy: brokenPolicy() });
    expect((await call("vm_health")).text).toMatch(/Mac could not be loaded, so every EDIABAS tool refuses/);
  });
});

describe("vm_screenshot", () => {
  it("returns an image and a one-line caption", async () => {
    const { call } = await connect();
    const r = await call("vm_screenshot", { window: "INPA" });
    expect(r.isError).toBeFalsy();
    expect(r.content[0]).toMatchObject({ type: "image", mimeType: "image/jpeg" });
    expect(Buffer.from(r.content[0]!.data!, "base64")).toEqual(Buffer.from([0xff, 0xd8, 0xff, 0xe0, 1, 2, 3, 0xff, 0xd9]));
    expect(r.text).toBe('Window "INPA - BMW Group ü" (0x000A0B2C), 1280x800, captured with printwindow.');
    expect(r.text.split("\n")).toHaveLength(1);
    const req = toolRequests()[0]!;
    expect(req.path).toBe("/screen/capture?window=INPA&maxEdge=1600");
  });

  it("defaults to the desktop", async () => {
    const { call } = await connect();
    const r = await call("vm_screenshot", { maxEdge: 800 });
    expect(r.text).toMatch(/^Desktop, 1280x800/);
    expect(toolRequests()[0]!.path).toBe("/screen/capture?window=desktop&maxEdge=800");
  });

  it("rejects maxEdge out of range without a request", async () => {
    const { call } = await connect();
    const r = await call("vm_screenshot", { maxEdge: 100 });
    expect(r.isError).toBe(true);
    expect(toolRequests()).toHaveLength(0);
  });

  it.each([
    ["window_not_found", 404, null, /vm_list_windows/],
    ["window_minimized", 409, { handle: "0x1", title: "INPA" }, /"INPA" is minimised.*Ask Ryan to restore it/],
    ["desktop_unavailable", 503, null, /locked.*log in to or unlock/],
  ] as const)("maps %s", async (code, status, details, msg) => {
    override("GET /screen/capture", () => agentErr(status, code, "x", details));
    const { call } = await connect();
    const r = await call("vm_screenshot", { window: "INPA" });
    expect(r.isError).toBe(true);
    expect(r.text).toMatch(msg);
  });

  it("treats a non-image 200 as a bad response", async () => {
    override("GET /screen/capture", () => ({ status: 200, body: { not: "an image" } }));
    const { call } = await connect();
    const r = await call("vm_screenshot");
    expect(r.isError).toBe(true);
    expect(r.text).toMatch(/did not match the expected format/);
  });
});

describe("vm_logs_list / vm_logs_read", () => {
  it("lists roots and files", async () => {
    const { call } = await connect();
    const r = await call("vm_logs_list", { root: 0, glob: "*.trc", since: "2026-11-21T00:00:00Z", limit: 10 });
    expect(r.isError).toBeFalsy();
    expect(r.text).toContain("[0] C:\\EDIABAS\\TRACE");
    expect(r.text).toContain("[1] C:\\NCSEXPER\\WORK (does not exist)");
    expect(r.text).toContain("C:\\EDIABAS\\TRACE\\api.trc | 20.0 KB | 2026-11-21T16:40:02Z");
    const q = new URL(toolRequests()[0]!.path, "http://x").searchParams;
    expect(Object.fromEntries(q)).toEqual({ root: "0", glob: "*.trc", since: "2026-11-21T00:00:00Z", limit: "10" });
  });

  it("rejects a since that is not ISO 8601 and a limit out of range", async () => {
    const { call } = await connect();
    expect((await call("vm_logs_list", { since: "yesterday" })).isError).toBe(true);
    expect((await call("vm_logs_list", { limit: 500 })).isError).toBe(true);
    expect(toolRequests()).toHaveLength(0);
  });

  it("reads a log with a header line and cleaned text that keeps line breaks", async () => {
    const { call } = await connect();
    const r = await call("vm_logs_read", { path: "C:\\EDIABAS\\TRACE\\api.trc", tailBytes: 1000, grep: "line" });
    expect(r.isError).toBeFalsy();
    const [header, blank, ...rest] = r.text.split("\n");
    expect(header).toBe(
      'C:\\EDIABAS\\TRACE\\api.trc: bytes 0-20480 of 20480, encoding utf-8, modified 2026-11-21T16:40:02Z, truncated: no, grep "line": 2 matching lines',
    );
    expect(blank).toBe("");
    expect(rest).toEqual(["line one", "line\ttwo[31m", "line three"]);
  });

  it("refuses tailBytes together with offset/length, and offset without length, before any request", async () => {
    const { call } = await connect();
    const a = await call("vm_logs_read", { path: "C:\\x.log", tailBytes: 10, offset: 0, length: 5 });
    expect(a.isError).toBe(true);
    expect(a.text).toMatch(/either tailBytes, or offset and length/);
    const b = await call("vm_logs_read", { path: "C:\\x.log", offset: 5 });
    expect(b.isError).toBe(true);
    expect(toolRequests()).toHaveLength(0);
  });

  it.each([
    ["path_not_allowed", 403, { reason: "outside the log roots" }, /outside the folders the agent may read \(outside the log roots\).*vm_logs_list/],
    ["file_not_found", 404, null, /no such file.*vm_logs_list/i],
  ] as const)("maps %s", async (code, status, details, msg) => {
    override("GET /logs/read", () => agentErr(status, code, "x", details));
    const { call } = await connect();
    const r = await call("vm_logs_read", { path: "C:\\Windows\\win.ini" });
    expect(r.isError).toBe(true);
    expect(r.text).toMatch(msg);
    expect(r.text).not.toMatch(/firewall/);
  });
});

describe("text budget", () => {
  const BUDGET = 60_000;

  it("a 5000-name ECU list stays under the budget and ends with the hint", async () => {
    override("GET /ediabas/ecus", () => ({
      status: 200,
      body: { ecuPath: "C:\\EDIABAS\\ECU", count: 5000, truncated: false, files: Array.from({ length: 5000 }, (_, i) => ({ name: `SGBD_NAME_${i}`, type: "prg" })) },
    }));
    const { call } = await connect();
    const r = await call("ediabas_list_ecus");
    expect(r.text.length).toBeLessThanOrEqual(BUDGET);
    expect(r.text.split("\n").at(-1)).toBe("4500 more; use filter to narrow the list.");
  });

  it("a 400-job describe stays under the budget and ends with the hint", async () => {
    override("GET /ediabas/describe", () => ({
      status: 200,
      body: {
        ecu: "BIG",
        variant: "BIG",
        jobCount: 400,
        jobs: Array.from({ length: 400 }, (_, i) => ({
          name: `STATUS_JOB_${i}`,
          comment: `Description ${i} `.padEnd(400, "x"),
          verdict: { allowed: true, rule: "allow.pattern", review: "passed" },
        })),
      },
    }));
    const { call } = await connect();
    const r = await call("ediabas_describe", { ecu: "BIG" });
    expect(r.isError).toBeFalsy();
    expect(r.text.length).toBeLessThanOrEqual(BUDGET);
    expect(r.text.split("\n").at(-1)).toMatch(/^… cut at \d+ of \d+ characters\. .*filter.*job/);
  });

  it("a 65536-byte log stays under the budget and ends with the hint", async () => {
    override("GET /logs/read", (q) => ({
      status: 200,
      body: {
        path: q.query.get("path"),
        sizeBytes: 65536,
        modifiedAt: null,
        offset: 0,
        length: 65536,
        encoding: "windows-1252",
        truncated: false,
        text: Array.from({ length: 1024 }, (_, i) => `${String(i).padStart(5, "0")} `.padEnd(63, "t")).join("\r\n"),
      },
    }));
    const { call } = await connect();
    const r = await call("vm_logs_read", { path: "C:\\EDIABAS\\TRACE\\api.trc", tailBytes: 65536 });
    expect(r.text.length).toBeLessThanOrEqual(BUDGET);
    expect(r.text.split("\n").at(-1)).toMatch(/^… cut at \d+ of \d+ characters\. .*(tailBytes|offset and length).*grep/);
  });

  it("vm_logs_read reads 16384 bytes from the end by default and at most 65536", async () => {
    const { call } = await connect();
    await call("vm_logs_read", { path: "C:\\a.trc" });
    expect(new URL(toolRequests()[0]!.path, "http://x").searchParams.get("tailBytes")).toBe("16384");
    expect((await call("vm_logs_read", { path: "C:\\a.trc", tailBytes: 65537 })).isError).toBe(true);
    expect((await call("vm_logs_read", { path: "C:\\a.trc", offset: 0, length: 65537 })).isError).toBe(true);
    expect(toolRequests()).toHaveLength(1);
  });
});

describe("ediabas_list_ecus", () => {
  it("lists groups and SGBDs", async () => {
    const { call } = await connect();
    const r = await call("ediabas_list_ecus", { filter: "e" });
    expect(r.text).toContain("Groups (grp): D_MOTOR");
    expect(r.text).toContain("SGBDs (prg): MS450DS0, NVE_65");
    expect(toolRequests()[0]!.path).toBe("/ediabas/ecus?filter=e");
  });
});

describe("ediabas_describe", () => {
  it("lists jobs with whether each is runnable, combining the agent's and the Mac's verdicts", async () => {
    const { call } = await connect();
    const r = await call("ediabas_describe", { ecu: "NVE_65" });
    expect(r.isError).toBeFalsy();
    expect(r.text).toMatch(/^NVE_65: 4 jobs, 2 runnable through the bridge\./);
    expect(r.text).toContain("FS_LESEN [runnable] — Fehlerspeicher lesen");
    expect(r.text).toContain("STEUERN_X [not allowed (Mac policy rule deny.substring:STEUER)]");
    expect(r.text).toMatch(/STATUS_RUN \[needs review.*\] — Run something$/m);
    expect(r.text).toContain("STATUS_OK [runnable]");
  });

  it("describes one job, even a denied one (shape only)", async () => {
    const { call } = await connect();
    const r = await call("ediabas_describe", { ecu: "NVE_65", job: "FS_LOESCHEN" });
    expect(r.isError).toBeFalsy();
    expect(r.text).toMatch(/^NVE_65 FS_LOESCHEN: not allowed \(Mac policy rule deny.substring:LOESCH\)/);
    expect(r.text).toContain("Arguments: none");
    expect(r.text).toContain("F_ORT_NR (long) — Index des Fehlerorts");
  });

  it("refuses a malformed name without a request", async () => {
    const { call, auditLines } = await connect();
    const r = await call("ediabas_describe", { ecu: "../TMODE" });
    expect(r.isError).toBe(true);
    expect(r.text).toMatch(/shape\.ecu/);
    expect(agent.requests).toHaveLength(0);
    expect((await auditLines())[0]).toMatch(/\tediabas_describe\tjob_not_allowed\t\d+\tecu=\.\.\/TMODE\tjob=\(job list\)\trule=shape\.ecu$/);
  });

  it.each([
    ["ecu_not_found", 404, { ecu: "NOPE" }, /no SGBD or group file named NOPE.*ediabas_list_ecus/i],
    ["job_not_found", 404, { ecu: "NVE_65", job: "NOPE" }, /NVE_65 NOPE.*ediabas_describe/],
    ["cable_in_use", 409, { processes: [{ name: "INPALOAD", pid: 4312 }, { name: "NcsDummy", pid: 77 }] }, /in use by INPALOAD \(pid 4312\), NcsDummy \(pid 77\)\. Ask Ryan to close it/],
    ["ediabas_unavailable", 503, { reason: "api32.dll missing" }, /could not be started in the VM \(api32.dll missing\)/],
    // As seen at the desk on 2026-11-21: describing a real SGBD with the cable unplugged.
    ["cable_not_found", 502, { errorCode: 28, errorText: "IFH-0018: INITIALIZATION ERROR" }, /\(EDIABAS error 28: IFH-0018: INITIALIZATION ERROR\): the K\+DCAN cable is not connected to the VM\..*pass it through to the Windows VM in UTM\. Do not retry until he says it is connected\./],
  ] as const)("maps %s", async (code, status, details, msg) => {
    override("GET /ediabas/describe", () => agentErr(status, code, "x", details));
    const { call } = await connect();
    const r = await call("ediabas_describe", { ecu: "NOPE", job: code === "job_not_found" ? "NOPE" : undefined });
    expect(r.isError).toBe(true);
    expect(r.text).toMatch(msg);
  });
});

describe("ediabas_vehicle_status", () => {
  it("prints each reading's fields as returned, and per-reading errors", async () => {
    const { call } = await connect();
    const r = await call("ediabas_vehicle_status");
    expect(r.isError).toBeFalsy();
    expect(r.text).toContain("Ignition (UTILITY STATUS_ZUENDUNG): job status OKAY; STAT_ZUENDUNG=1");
    expect(r.text).toContain("Battery (UTILITY STATUS_UBATT): job status OKAY; STAT_UBATT=12480");
    expect(r.text).toContain("Voltage (NVE_65 STATUS_BORDNETZSPANNUNG): job status OKAY; STAT_SPANNUNG_WERT=12.4");
    expect(r.text).toContain("Cable KL30 (TMODE LESE_SPANNUNG_KL30): job status OKAY; SPANNUNG_KL30=12510");
    expect(r.text).toMatch(/Cable KL15 .*error: EDIABAS reported error 159: IFH-0009: NO RESPONSE FROM CONTROLUNIT/);
  });
});

describe("ediabas_job", () => {
  it("runs an allowed job and formats the result sets, distances miles first", async () => {
    const { call, auditLines } = await connect();
    const r = await call("ediabas_job", { ecu: "nve_65", job: "ident", results: "ID_BMW_NR" });
    expect(r.isError).toBeFalsy();
    expect(r.text).toContain("NVE_65 IDENT: job status OKAY, variant NVE_65, 300 ms, 1 result set");
    expect(r.text).toContain("  KM_STAND=121,609 mi (195,712 km)");
    expect(JSON.parse(toolRequests()[0]!.body)).toEqual({ ecu: "nve_65", job: "ident", results: "ID_BMW_NR" });
    expect((await auditLines())[0]).toMatch(/\tediabas_job\tok\t\d+\tecu=nve_65\tjob=ident$/);
  });

  it("returns the agent's JSON with raw: true", async () => {
    const { call } = await connect();
    const r = await call("ediabas_job", { ecu: "NVE_65", job: "IDENT", raw: true });
    expect(JSON.parse(r.text)).toMatchObject({ ecu: "NVE_65", job: "IDENT", sets: [{ KM_STAND: 195712 }] });
  });

  it.each([
    ["NVE_65", "FS_LOESCHEN", undefined, "deny.substring:LOESCH"],
    ["TMODE", "IDENT", undefined, "ecu_restricted"],
    ["NVE_65", "C_C_LESEN", undefined, "default_deny"],
    ["NVE_65", "IDENT", "gr\u00fcn", "shape.args"],
    ["NVE_65", "FS_LESEN;FS_LOESCHEN", undefined, "shape.job"],
  ])("refuses %s %s on the Mac with zero HTTP requests (%s, %s)", async (ecu, job, args, rule) => {
    const fetchSpy = vi.fn<typeof fetch>((...a) => fetch(...a));
    const { call, auditLines } = await connect({ fetch: fetchSpy });
    const r = await call("ediabas_job", { ecu, job, args });
    expect(r.isError).toBe(true);
    expect(r.text).toContain(`rule ${rule}`);
    expect(r.text).toMatch(/Nothing was sent to the VM/);
    expect(r.text).toMatch(/do not look for another job, ECU or tool/);
    expect(fetchSpy).not.toHaveBeenCalled();
    expect(agent.requests).toHaveLength(0);
    const line = (await auditLines())[0]!;
    expect(line).toContain("\tediabas_job\tjob_not_allowed\t");
    expect(line).toContain(`\tecu=${ecu}\t`);
    expect(line).toContain(`\trule=${rule}`);
  });

  it("refuses every ediabas_* tool with policy_unavailable when the policy cannot be loaded, with zero requests", async () => {
    const fetchSpy = vi.fn<typeof fetch>((...a) => fetch(...a));
    const { call, logs } = await connect({ policy: brokenPolicy(), fetch: fetchSpy });
    expect(logs.some((l) => /job policy not loaded/.test(l))).toBe(true);
    for (const [tool, args] of [
      ["ediabas_list_ecus", {}],
      ["ediabas_describe", { ecu: "NVE_65" }],
      ["ediabas_vehicle_status", {}],
      ["ediabas_job", { ecu: "NVE_65", job: "IDENT" }],
      ["ediabas_read_faults", { ecus: ["NVE_65"] }],
    ] as const) {
      const r = await call(tool, args);
      expect(r.isError, tool).toBe(true);
      expect(r.text).toMatch(/job policy file could not be loaded/);
    }
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it.each([
    ["job_not_allowed", 403, { ecu: "NVE_65", job: "STATUS_X", rule: "deny.token:START" }, /agent refused NVE_65 STATUS_X under its job policy \(rule deny.token:START\)/],
    ["job_needs_review", 403, { ecu: "NVE_65", job: "STATUS_X", description: "Startet\u0007 den Test", matched: ["start"] }, /matched: start\)\. Description: "Startet\s+den Test"\. Ryan decides/],
    ["job_needs_review", 403, { ecu: "NVE_65", job: "STATUS_X", description: "", matched: [] }, /has no description/],
    ["job_needs_review", 403, { ecu: "NVE_65", job: "STATUS_X", description: "Jobbeschreibung", matched: [] }, /^(?![\s\S]*suggests)[\s\S]*description has nothing in it but a label \("Jobbeschreibung"\), so the agent cannot tell that it only reads/],
    ["ediabas_error", 502, { errorCode: 159, errorText: "IFH-0009: NO RESPONSE" }, /^\[ediabas_error\] (?![\s\S]*UTM)EDIABAS reported error 159: IFH-0009: NO RESPONSE\. This often means the ECU did not answer/],
    [
      "ediabas_error",
      502,
      { errorCode: 16, errorText: "IFH-0006: COMMAND NOT ACCEPTED" },
      /^\[ediabas_error\] EDIABAS reported error 16: IFH-0006: COMMAND NOT ACCEPTED\. The cable may not be attached to the VM: after it is unplugged and plugged back in, UTM does not hand it back by itself \(Ryan ticks it in UTM's USB menu\)\. Otherwise the ECU did not answer/,
    ],
    ["job_timeout", 504, { timeoutSeconds: 30 }, /did not finish within 30 s/],
    ["cable_in_use", 409, null, /in use by a BMW tool in the VM/],
    ["cable_not_found", 502, { errorCode: 28, errorText: "IFH-0018: INITIALIZATION ERROR" }, /^\[cable_not_found\] (?![\s\S]*ECU did not answer)[\s\S]*cable is not connected to the VM/],
    ["cable_not_found", 502, null, /^\[cable_not_found\] EDIABAS could not initialise the diagnostic interface: the K\+DCAN cable/],
  ] as const)("maps the agent's %s", async (code, status, details, msg) => {
    override("POST /ediabas/job", () => agentErr(status, code, "agent message", details));
    const { call, auditLines } = await connect();
    const r = await call("ediabas_job", { ecu: "NVE_65", job: "STATUS_X" });
    expect(r.isError).toBe(true);
    expect(r.text).toMatch(msg);
    expect(r.text).not.toMatch(/firewall/);
    expect((await auditLines())[0]).toContain(`\t${code}\t`);
  });

  it.each([
    [
      "a BMW tool started during the request",
      "A BMW tool started during the request (INPALOAD pid 4312); no further jobs are started.",
      /^\[not_run\] Not run: the agent said: "A BMW tool started during the request \(INPALOAD pid 4312\); no further jobs are started\."/,
    ],
    [
      "the time limit",
      "The request reached its 60 s limit before IDENT started.",
      /^\[not_run\] Not run: the agent said: "The request reached its 60 s limit before IDENT started\."/,
    ],
    [
      "an earlier job that ignored apiBreak",
      "An earlier job did not stop after apiBreak; no further jobs are started.",
      /^\[not_run\] Not run: the agent said: "An earlier job did not stop after apiBreak/,
    ],
  ] as const)("passes the agent's reason through for a 503 not_run (%s)", async (_why, message, msg) => {
    override("POST /ediabas/job", () => agentErr(503, "not_run", message, null));
    const { call, auditLines } = await connect();
    const r = await call("ediabas_job", { ecu: "NVE_65", job: "IDENT" });
    expect(r.isError).toBe(true);
    expect(r.text).toMatch(msg);
    if (!/limit/.test(message)) expect(r.text).not.toMatch(/overall time limit/);
    expect((await auditLines())[0]).toContain("\tnot_run\t");
  });

  it("says not_run without a reason when the agent gave none", async () => {
    override("POST /ediabas/job", () => agentErr(503, "not_run", "", null));
    const { call } = await connect();
    const r = await call("ediabas_job", { ecu: "NVE_65", job: "IDENT" });
    expect(r.text).toMatch(/^\[not_run\] Not run: the agent did not start this job and gave no reason\./);
  });

  it("shows that the agent must be restarted when EDIABAS is stuck", async () => {
    override("POST /ediabas/job", () =>
      agentErr(503, "ediabas_unavailable", "EDIABAS cannot be used", {
        reason: "an earlier job did not stop after apiBreak; the agent must be restarted",
      }),
    );
    const { call } = await connect();
    const r = await call("ediabas_job", { ecu: "NVE_65", job: "IDENT" });
    expect(r.text).toMatch(/did not stop after apiBreak; the agent must be restarted/);
  });

  it("maps a 404 not_found (an older agent without the endpoint) to agent_outdated", async () => {
    agent.setHandler((req, res) => {
      if (req.url === "/health") stubHandler()(req, res);
      else res.writeHead(404, { "content-type": "application/json" }).end(JSON.stringify({ error: { code: "not_found", message: "No such path." } }));
    });
    const { call, auditLines } = await connect();
    const r = await call("ediabas_job", { ecu: "NVE_65", job: "IDENT" });
    expect(r.isError).toBe(true);
    expect(r.text).toMatch(/does not have this feature yet.*Update line/);
    expect((await auditLines())[0]).toContain("\tagent_outdated\t");
  });

  it("does not re-send an EDIABAS call that timed out", async () => {
    agent.setHandler((req, res) => {
      if (req.url === "/health") stubHandler()(req, res);
      // never answers
    });
    const { call } = await connect({ ediabasTimeoutMs: 200 });
    await call("vm_health"); // cache the address
    const r = await call("ediabas_job", { ecu: "NVE_65", job: "IDENT" });
    expect(r.isError).toBe(true);
    expect(r.text).toMatch(/may have run in the VM\. It was not sent again/);
    expect(agent.requests.filter((q) => q.path === "/ediabas/job")).toHaveLength(1);
  });

  it("does not re-send an EDIABAS job when the agent drops the connection after receiving it", async () => {
    agent.setHandler((req, res) => {
      if (req.url === "/health") stubHandler()(req, res);
      else req.socket.destroy(); // the agent died after reading the request
    });
    const { call } = await connect();
    await call("vm_health");
    for (const [tool, path] of [
      ["ediabas_job", "/ediabas/job"],
      ["ediabas_read_faults", "/ediabas/jobs"],
    ] as const) {
      const r = await call(tool, tool === "ediabas_job" ? { ecu: "NVE_65", job: "IDENT" } : { ecus: ["NVE_65"] });
      expect(r.isError).toBe(true);
      expect(r.text).toMatch(/may have run in the VM\. It was not sent again/);
      expect(agent.requests.filter((q) => q.path === path)).toHaveLength(1);
    }
  });
});

describe("ediabas_read_faults", () => {
  it("reads FS_LESEN per ECU in one request and prints one fault table per ECU", async () => {
    const { call, auditLines } = await connect();
    const r = await call("ediabas_read_faults", { ecus: ["NVE_65", "D_MOTOR"] });
    expect(r.isError).toBeFalsy();
    const reqs = toolRequests();
    expect(reqs).toHaveLength(1);
    expect(JSON.parse(reqs[0]!.body)).toEqual({ items: [{ ecu: "NVE_65", job: "FS_LESEN" }, { ecu: "D_MOTOR", job: "FS_LESEN" }] });
    expect(r.text).toMatch(/^Fault memory for 2 ECUs \(FS_LESEN\):/);
    expect(r.text).toContain("\nNVE_65\n  Fault memory: 2 entries\n  1. 0xA752  Synthetic fault A");
    expect(r.text).toContain("present/stored: Fault currently present | frequency: 3 | logged at: 121,609 mi (195,712 km)");
    expect(r.text).toContain("conditions: Engine speed = 820 1/min; Coolant temperature = 90 degC");
    expect(r.text).toContain("\n\nD_MOTOR\n  Fault memory: 2 entries");
    expect((await auditLines())[0]).toMatch(/\tediabas_read_faults\tok\t\d+\tecu=NVE_65,D_MOTOR\tjob=FS_LESEN$/);
  });

  it("chunks 20 ECUs with info memory into two requests of 20 items", async () => {
    const ecus = Array.from({ length: 20 }, (_, i) => `ECU_${i}`);
    const { call } = await connect();
    const r = await call("ediabas_read_faults", { ecus, includeInfoMemory: true });
    expect(r.isError).toBeFalsy();
    const reqs = toolRequests();
    expect(reqs).toHaveLength(2);
    const first = JSON.parse(reqs[0]!.body).items;
    const second = JSON.parse(reqs[1]!.body).items;
    expect(first).toHaveLength(20);
    expect(second).toHaveLength(20);
    expect(first.slice(0, 2)).toEqual([{ ecu: "ECU_0", job: "FS_LESEN" }, { ecu: "ECU_0", job: "IS_LESEN" }]);
    expect(second.at(-1)).toEqual({ ecu: "ECU_19", job: "IS_LESEN" });
    expect(r.text).toContain("Fault memory for 20 ECUs (FS_LESEN and IS_LESEN)");
    expect(r.text).toMatch(/ECU_19[^\n]*\n  Fault memory: 2 entries[\s\S]*Info memory: no faults stored\./);
  });

  it("rejects more than 20 ECUs without a request", async () => {
    const { call } = await connect();
    const r = await call("ediabas_read_faults", { ecus: Array.from({ length: 21 }, (_, i) => `E${i}`) });
    expect(r.isError).toBe(true);
    expect(toolRequests()).toHaveLength(0);
  });

  it("refuses the whole call on the Mac if any ECU is refused, with zero requests", async () => {
    const { call, auditLines } = await connect();
    const r = await call("ediabas_read_faults", { ecus: ["NVE_65", "TMODE"] });
    expect(r.isError).toBe(true);
    expect(r.text).toMatch(/TMODE FS_LESEN \(rule ecu_restricted/);
    expect(agent.requests).toHaveLength(0);
    expect((await auditLines())[0]).toMatch(/\tjob_not_allowed\t\d+\tecu=NVE_65,TMODE\tjob=FS_LESEN\trule=ecu_restricted$/);
  });

  it("shows a per-ECU error in that ECU's place and still prints the others", async () => {
    override("POST /ediabas/jobs", (q) => {
      const items = (q.body as { items: { ecu: string; job: string }[] }).items;
      return {
        status: 200,
        body: {
          items: items.map((it) =>
            it.ecu === "BAD"
              ? { ok: false, result: null, error: { code: "ediabas_error", message: "x", details: { errorCode: 19, errorText: "IFH-0009" } } }
              : it.ecu === "GONE"
                ? { ok: false, result: null, error: { code: "not_run", message: "x", details: null } }
                : { ok: true, result: jobResult(it.ecu, it.job, [{ JOB_STATUS: "OKAY" }]), error: null },
          ),
          durationMs: 5,
        },
      };
    });
    const { call } = await connect();
    const r = await call("ediabas_read_faults", { ecus: ["NVE_65", "BAD", "GONE"] });
    expect(r.isError).toBeFalsy();
    expect(r.text).toContain("NVE_65\n  Fault memory: no faults stored.");
    expect(r.text).toMatch(/BAD\n  Fault memory: error: EDIABAS reported error 19: IFH-0009/);
    expect(r.text).toMatch(/GONE\n  Fault memory: error: Not run/);
  });

  it("names the BMW tool that stopped a batch midway instead of blaming the time limit", async () => {
    override("POST /ediabas/jobs", (q) => {
      const items = (q.body as { items: { ecu: string; job: string }[] }).items;
      return {
        status: 200,
        body: {
          items: items.map((it, i) =>
            i === 0
              ? { ok: true, result: jobResult(it.ecu, it.job, [{ JOB_STATUS: "OKAY" }]), error: null }
              : {
                  ok: false,
                  result: null,
                  error: { code: "not_run", message: "A BMW tool started during the request (INPALOAD pid 4312); no further jobs are started.", details: null },
                },
          ),
          durationMs: 5,
        },
      };
    });
    const { call } = await connect();
    const r = await call("ediabas_read_faults", { ecus: ["NVE_65", "D_MOTOR"] });
    expect(r.text).toMatch(/D_MOTOR\n  Fault memory: error: Not run: the agent said: "A BMW tool started during the request \(INPALOAD pid 4312\)/);
    expect(r.text).not.toMatch(/overall time limit/);
  });

  it("shows cable_not_found as a per-ECU error, not as the ECU failing to answer", async () => {
    override("POST /ediabas/jobs", (q) => {
      const items = (q.body as { items: { ecu: string; job: string }[] }).items;
      return {
        status: 200,
        body: {
          items: items.map(() => ({
            ok: false,
            result: null,
            error: { code: "cable_not_found", message: "x", details: { errorCode: 28, errorText: "IFH-0018: INITIALIZATION ERROR" } },
          })),
          durationMs: 5,
        },
      };
    });
    const { call } = await connect();
    const r = await call("ediabas_read_faults", { ecus: ["NVE_65"] });
    expect(r.text).toMatch(/NVE_65\n  Fault memory: error: EDIABAS could not initialise the diagnostic interface/);
    expect(r.text).toMatch(/not connected to the VM/);
    expect(r.text).not.toMatch(/ECU did not answer/);
  });

  it("returns isError when the first request fails as a whole (cable in use)", async () => {
    override("POST /ediabas/jobs", () => agentErr(409, "cable_in_use", "x", { processes: [{ name: "INPALOAD", pid: 4312 }] }));
    const { call } = await connect();
    const r = await call("ediabas_read_faults", { ecus: ["NVE_65"] });
    expect(r.isError).toBe(true);
    expect(r.text).toMatch(/in use by INPALOAD \(pid 4312\)/);
    expect(toolRequests()).toHaveLength(1);
  });

  it("when the second request fails, prints the first and shows the error for the rest", async () => {
    let n = 0;
    const ok = stubHandler();
    agent.setHandler((req, res) => {
      if (req.url === "/ediabas/jobs" && ++n === 2) {
        res.writeHead(409, { "content-type": "application/json" }).end(JSON.stringify({ error: { code: "cable_in_use", message: "x", details: { processes: [{ name: "WinKFP", pid: 9 }] } } }));
        return;
      }
      ok(req, res);
    });
    const ecus = Array.from({ length: 12 }, (_, i) => `ECU_${i}`);
    const { call } = await connect();
    const r = await call("ediabas_read_faults", { ecus, includeInfoMemory: true });
    expect(r.isError).toBeFalsy();
    expect(r.text).toMatch(/ECU_9\n  Fault memory: 2 entries/);
    expect(r.text).toMatch(/ECU_10\n  Fault memory: error: The diagnostic cable is in use by WinKFP \(pid 9\)/);
    expect(r.text).toMatch(/ECU_11\n  Fault memory: error: [^\n]+\n  Info memory: error: The diagnostic cable/);
  });

  it("reads a repeated ECU once", async () => {
    const { call } = await connect();
    await call("ediabas_read_faults", { ecus: ["NVE_65", "nve_65"] });
    expect(JSON.parse(toolRequests()[0]!.body).items).toHaveLength(1);
  });
});

describe("recording", () => {
  it("writes one file per exchange, images separately, never the token", async () => {
    const rec = join(dir, "rec");
    const { call } = await connect({ env: { EDIABAS_RECORD_DIR: rec } });
    await call("vm_health");
    await call("ediabas_job", { ecu: "NVE_65", job: "IDENT" });
    await call("vm_screenshot", { window: "INPA" });
    override("GET /logs", () => agentErr(403, "path_not_allowed", "no", { reason: "x" }));
    await call("vm_logs_list");
    const files = (await readdir(rec)).sort();
    // discovery's /health probe, then vm_health, the job, the screenshot and the logs listing
    expect(files.filter((f) => f.endsWith(".json"))).toHaveLength(5);
    expect(files.filter((f) => f.endsWith(".jpg"))).toHaveLength(1);
    expect(files.some((f) => /^\d{8}T\d{9}Z-0003-POST-ediabas_job-NVE_65-IDENT\.json$/.test(f))).toBe(true);
    const all = await Promise.all(files.filter((f) => f.endsWith(".json")).map((f) => readFile(join(rec, f), "utf8")));
    for (const t of all) {
      expect(t).not.toContain(AGENT_TOKEN);
      expect(t.toLowerCase()).not.toContain("authorization");
    }
    const job = JSON.parse(all.find((t) => t.includes('"/ediabas/job"'))!);
    expect(job.request).toEqual({ method: "POST", path: "/ediabas/job", query: {}, body: { ecu: "NVE_65", job: "IDENT" } });
    expect(job.response.status).toBe(200);
    expect(job.response.body.ecu).toBe("NVE_65");
    const shot = JSON.parse(all.find((t) => t.includes('"/screen/capture"'))!);
    expect(shot.request.query).toEqual({ window: "INPA", maxEdge: "1600" });
    expect(shot.response.headers["x-capture-method"]).toBe("printwindow");
    expect(files).toContain(shot.response.imageFile);
    const err = JSON.parse(all.find((t) => t.includes('"/logs"'))!);
    expect(err.response).toMatchObject({ status: 403, body: { error: { code: "path_not_allowed" } } });
  });

  it("is off by default", async () => {
    const { call } = await connect();
    await call("vm_health");
    expect(await readdir(dir)).toEqual(["logs"]);
  });

  it("a recording failure does not fail the tool call", async () => {
    const { call, logs } = await connect({ env: { EDIABAS_RECORD_DIR: "/dev/null/cannot" } });
    const r = await call("vm_health");
    expect(r.isError).toBeFalsy();
    expect(logs.some((l) => /could not record/.test(l))).toBe(true);
  });
});
