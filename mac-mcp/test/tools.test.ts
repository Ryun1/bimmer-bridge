import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { NOTICE_ENTRIES_MAX } from "../src/approvalWatch.js";
import { createAppServer } from "../src/server.js";
import { loadConfig } from "../src/config.js";
import { wrongService } from "../src/errors.js";
import { PolicyGuard } from "../src/policy.js";
import type { ExecFileFn } from "../src/vmAddress.js";
import { AGENT_TOKEN, LOOPBACK_BRIDGE, healthBody, startFakeAgent, type FakeAgent } from "./fixtures.js";
import { agentErr, jobResult, phase2Health, stubHandler, type Responder } from "./stubAgent.js";

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
  now?: () => number;
  voltageSources?: { ecu: string; job: string }[];
}

const NVE_VOLTAGE = [{ ecu: "NVE_65", job: "STATUS_BORDNETZSPANNUNG" }];

async function connect(s: Setup = {}) {
  const auditPath = join(dir, "logs", "audit.log");
  const env = { EDIABAS_AGENT_URL: agent.url, EDIABAS_AGENT_TOKEN: AGENT_TOKEN, EDIABAS_AUDIT_LOG: auditPath, ...s.env };
  const { config: loaded } = loadConfig(env, dir);
  const config = { ...loaded, voltageSources: s.voltageSources ?? [] };
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
    now: s.now,
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

/** Requests other than discovery's /hello probe and /health. */
function toolRequests(): FakeAgent["requests"] {
  return agent.requests.filter((r) => r.path !== "/health" && !r.path.startsWith("/hello?"));
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
        "ediabas_jobs",
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
    expect(Object.keys(byName.ediabas_job!.inputSchema.properties ?? {}).sort()).toEqual(["args", "ecu", "job", "raw", "results", "timeoutSeconds"]);
    expect(Object.keys(byName.ediabas_read_faults!.inputSchema.properties ?? {}).sort()).toEqual(["ecus", "includeInfoMemory"]);
    expect(Object.keys(byName.ediabas_jobs!.inputSchema.properties ?? {}).sort()).toEqual(["jobs", "raw", "timeoutSeconds"]);
    expect(Object.keys(byName.ediabas_describe!.inputSchema.properties ?? {}).sort()).toEqual(["ecu", "filter", "job", "namesOnly"]);
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

  it("a wrong service names the host and port, never URL credentials", () => {
    const e = wrongService("http://u:p@1.2.3.4:8765");
    expect(e.code).toBe("wrong_service");
    expect(e.message).toContain("1.2.3.4:8765");
    expect(e.message).not.toContain("u:p");
  });

  it("gives a job request that may have run its own code", async () => {
    agent.setHandler((req, res) => {
      if (req.url === "/health" || req.url?.startsWith("/hello?")) stubHandler()(req, res);
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
    expect(r.text).toMatch(/Job policy: agent version 4; Mac version 4 \(sha256 [0-9a-f]{12}\)\./);
    expect(r.text).not.toMatch(/WARNING: the agent's job policy/);
  });

  it("starts with this server's version from package.json and its start time", async () => {
    const pkg = JSON.parse(await readFile(new URL("../package.json", import.meta.url), "utf8")) as { version: string };
    const { call } = await connect({ now: () => new Date(2026, 10, 22, 9, 5, 7).getTime() });
    const r = await call("vm_health");
    expect(r.text.split("\n")[0]).toBe(`Mac server ${pkg.version}, started 2026-11-22 09:05:07`);
    // The stub agent reports 0.1.0.
    expect(r.text).toContain("The Mac server and the agent are different versions; restart Claude or update the agent.");
  });

  it("warns when the agent's policy version differs", async () => {
    override("GET /health", () => ({ status: 200, body: healthBody({ policyVersion: 2, capabilities: ["ediabas"] }) }));
    const { call } = await connect();
    const r = await call("vm_health");
    expect(r.text).toMatch(/WARNING: the agent's job policy \(version 2\) differs from this Mac's \(version 4\)/);
    expect(r.text).toMatch(/Update line/);
  });

  it("warns about an agent still on policy version 1 (no description-screen ignore list)", async () => {
    override("GET /health", () => ({ status: 200, body: healthBody({ policyVersion: 1, capabilities: ["ediabas"] }) }));
    const { call } = await connect();
    const r = await call("vm_health");
    expect(r.text).toMatch(/WARNING: the agent's job policy \(version 1\) differs from this Mac's \(version 4\)/);
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
    ["window_minimized", 409, { handle: "0x1", title: "INPA" }, /"INPA" is minimised.*Ask the user to restore it/],
    ["desktop_unavailable", 503, null, /locked.*log in to or unlock/],
    ["capture_busy", 503, null, /^\[capture_busy\] (?![\s\S]*locked)Another screenshot is still being taken in the VM; try again in a moment\./],
    ["capture_failed", 500, null, /^\[capture_failed\] (?![\s\S]*locked)The window could not be captured/],
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
    ["job_not_found", 404, { ecu: "NVE_65", job: "NOPE" }, /^\[job_not_found\] NVE_65 has no job NOPE\. .*ediabas_describe/],
    ["cable_in_use", 409, { processes: [{ name: "INPALOAD", pid: 4312 }, { name: "NcsDummy", pid: 77 }] }, /in use by INPALOAD \(pid 4312\), NcsDummy \(pid 77\)\. Ask the user to close it/],
    ["ediabas_unavailable", 503, { reason: "api32.dll missing" }, /could not be started in the VM \(api32.dll missing\)/],
    // As seen at the desk on 2026-11-21: describing a real SGBD with the cable unplugged.
    ["cable_not_found", 502, { errorCode: 28, errorText: "IFH-0018: INITIALIZATION ERROR" }, /\(EDIABAS error 28: IFH-0018: INITIALIZATION ERROR\): the K\+DCAN cable is not connected to the VM\..*pass it through to the Windows VM in UTM\. Do not retry until the user says it is connected\./],
  ] as const)("maps %s", async (code, status, details, msg) => {
    override("GET /ediabas/describe", () => agentErr(status, code, "x", details));
    const { call } = await connect();
    const r = await call("ediabas_describe", { ecu: "NOPE", job: code === "job_not_found" ? "NOPE" : undefined });
    expect(r.isError).toBe(true);
    expect(r.text).toMatch(msg);
  });
});

describe("what an ediabas_error message sends the user to check, by code", () => {
  const status = "Check ediabas_vehicle_status";
  const hint = "If the job name may be wrong, check it with ediabas_describe.";
  // The cable and voltage codes are about the cable or the supply: ediabas_vehicle_status's
  // battery and KL30 readings come from the cable's lines (rehearsal, 2026-11-22), so they
  // cannot settle it. No IFH code comes from a wrong job name (that is SYS-0008).
  it.each([
    [12, "IFH-0002: NO RESPONSE FROM INTERFACE", false, false],
    [13, "IFH-0003: DATATRANSMISSION TO INTERFACE DISTURBED", false, false],
    [16, "IFH-0006: COMMAND NOT ACCEPTED", false, false],
    [48, "IFH-0038: INTERFACE COMMAND NOT IMPLEMENTED", false, false],
    [17, "IFH-0007: WRONG UBATT", false, false],
    [47, "IFH-0037: LOW UBATT", false, false],
    [42, "IFH-0032: IGNITION IS OFF", false, false],
    [19, "IFH-0009: NO RESPONSE FROM CONTROLUNIT", true, false],
    [20, "IFH-0010: DATATRANSMISSION TO CONTROLUNIT DISTURBED", true, false],
    [100, "SYS-0010: INITIALIZATION ERROR", true, true],
    [98, "SYS-0008: JOB NOT FOUND", true, true],
    [134, "SYS-0002: ECU OBJECT FILE NOT FOUND", true, true],
  ] as const)("error %i (%s)", async (errorCode, errorText, wantStatus, wantHint) => {
    override("POST /ediabas/job", () => agentErr(502, "ediabas_error", "x", { errorCode, errorText }));
    const { call } = await connect();
    const r = await call("ediabas_job", { ecu: "CAS", job: "C_FA_LESEN" });
    expect(r.text.includes(status)).toBe(wantStatus);
    expect(r.text.includes(hint)).toBe(wantHint);
    expect(r.text).toMatch(/ask the user before trying again|Do not retry|stop reading/i);
  });
});

describe("the job-name hint on ediabas_error", () => {
  const hint = "If the job name may be wrong, check it with ediabas_describe.";
  const details = { errorCode: 19, errorText: "SYS-0008: JOB NOT FOUND" };

  it("is added to a failed ediabas_job run", async () => {
    override("POST /ediabas/job", () => agentErr(502, "ediabas_error", "x", details));
    const { call } = await connect();
    const r = await call("ediabas_job", { ecu: "NVE_65", job: "STATUS_X" });
    expect(r.text).toContain(`error 19: SYS-0008: JOB NOT FOUND. `);
    expect(r.text.endsWith(hint)).toBe(true);
  });

  it("is added to an ediabas_jobs item", async () => {
    override("POST /ediabas/jobs", () => ({
      status: 200,
      body: { items: [{ ok: false, result: null, error: { code: "ediabas_error", message: "x", details } }] },
    }));
    const { call } = await connect();
    const r = await call("ediabas_jobs", { jobs: [{ ecu: "NVE_65", job: "STATUS_X" }] });
    expect(r.text).toContain(hint);
  });

  it("is not added to an ediabas_describe error", async () => {
    override("GET /ediabas/describe", () => agentErr(502, "ediabas_error", "x", details));
    const { call } = await connect();
    const r = await call("ediabas_describe", { ecu: "NVE_65" });
    expect(r.text).toMatch(/^\[ediabas_error\]/);
    expect(r.text).not.toContain(hint);
  });
});

describe("ediabas_describe text", () => {
  it("says it needs the cable, can be slow, and ignores filter with job", async () => {
    const { client } = await connect();
    const d = (await client.listTools()).tools.find((t) => t.name === "ediabas_describe")!.description!;
    expect(d).toMatch(/needs the cable attached to the VM/);
    expect(d).toMatch(/tens of seconds/);
    expect(d).toMatch(/`filter` is ignored when `job` is given/);
  });
});

describe("ediabas_vehicle_status", () => {
  it("sends one POST /ediabas/jobs and prints each reading's fields as returned, and per-reading errors", async () => {
    const { call, auditLines } = await connect({ voltageSources: NVE_VOLTAGE });
    const r = await call("ediabas_vehicle_status");
    expect(r.isError).toBeFalsy();
    expect(r.text).toContain("Ignition (UTILITY STATUS_ZUENDUNG): job status OKAY; STAT_ZUENDUNG=1");
    expect(r.text).toContain("Battery (UTILITY STATUS_UBATT): job status OKAY; STAT_UBATT=12480");
    expect(r.text).toContain("Voltage (NVE_65 STATUS_BORDNETZSPANNUNG): job status OKAY; STAT_SPANNUNG_WERT=12.4");
    expect(r.text).toContain("Cable KL30 (TMODE LESE_SPANNUNG_KL30): job status OKAY; SPANNUNG_KL30=12510");
    expect(r.text).toMatch(/Cable KL15 .*error: \[ediabas_error\] EDIABAS reported error 19: IFH-0009: NO RESPONSE FROM CONTROLUNIT/);
    const sent = toolRequests();
    expect(sent.map((q) => `${q.method} ${q.path}`)).toEqual(["POST /ediabas/jobs"]);
    expect(JSON.parse(sent[0]!.body).items).toEqual([
      { ecu: "UTILITY", job: "STATUS_ZUENDUNG" },
      { ecu: "UTILITY", job: "STATUS_UBATT" },
      { ecu: "NVE_65", job: "STATUS_BORDNETZSPANNUNG" },
      { ecu: "TMODE", job: "LESE_SPANNUNG_KL30" },
      { ecu: "TMODE", job: "LESE_SPANNUNG_KL15" },
    ]);
    expect((await auditLines())[0]).toMatch(/\tediabas_vehicle_status\tok\t\d+\tecu=UTILITY,UTILITY,NVE_65,TMODE,TMODE\tjob=STATUS_ZUENDUNG,STATUS_UBATT,STATUS_BORDNETZSPANNUNG,LESE_SPANNUNG_KL30,LESE_SPANNUNG_KL15$/);
  });

  it("without voltage sources sends the four fixed readings and says none are configured", async () => {
    const { call } = await connect();
    const r = await call("ediabas_vehicle_status");
    expect(r.text).toContain("Voltage sources: none configured (voltageSources in config.local.json on the Mac).");
    expect(JSON.parse(toolRequests()[0]!.body).items).toHaveLength(4);
  });

  it("a voltage source the policy refuses is logged at start-up, and the call is refused with nothing sent", async () => {
    const { call, logs } = await connect({ voltageSources: [{ ecu: "NVE_65", job: "STEUERN_X" }] });
    expect(logs).toContain(
      "config error: config.local.json voltageSources[0] (NVE_65 STEUERN_X) is refused by the job policy (rule deny.substring:STEUER). ediabas_vehicle_status refuses until it is fixed.",
    );
    const r = await call("ediabas_vehicle_status");
    expect(r.isError).toBe(true);
    expect(r.text).toMatch(/^\[job_not_allowed\] config\.local\.json voltageSources\[0\] \(NVE_65 STEUERN_X\) is refused by the job policy \(rule deny\.substring:STEUER\)\. Nothing was sent to the VM\./);
    expect(r.text).toMatch(/fix voltageSources in config\.local\.json on the Mac/);
    expect(agent.requests.filter((q) => q.path.startsWith("/ediabas/"))).toEqual([]);
  });

  it("a whole-request failure is the tool's error", async () => {
    override("POST /ediabas/jobs", () => agentErr(409, "cable_in_use", "INPA holds the cable.", { processes: ["INPALOAD"] }));
    const { call } = await connect();
    const r = await call("ediabas_vehicle_status");
    expect(r.isError).toBe(true);
    expect(r.text).toMatch(/^\[cable_in_use\]/);
  });
});

describe("approval changes", () => {
  const approvals = (entries: string[], hash: string) => ({ pinned: entries.length, ignored: 0, entries, hash });

  /** One ediabas_job call to set the baseline, then a changed list (and any other routes) for the next call. */
  const changeApprovals = async (entries: string[], routes: Record<string, Responder> = {}) => {
    override("GET /health", () => ({ status: 200, body: phase2Health({ approvals: approvals(["NVE_65:STATUS_X@b8ae0dd9d242"], "aaaaaaaaaaaa") }) }));
    const c = await connect();
    await c.call("ediabas_job", { ecu: "NVE_65", job: "IDENT" });
    agent.setHandler(stubHandler({ ...routes, "GET /health": () => ({ status: 200, body: phase2Health({ approvals: approvals(entries, "bbbbbbbbbbbb") }) }) }));
    return c;
  };

  it("a VM-controlled list of any length gives a capped notice and leaves the tool's text its budget", async () => {
    const many = Array.from({ length: 3000 }, (_, i) => `NVE_65:STATUS_${String(i).padStart(5, "0")}@b8ae0dd9d242`);
    const { call } = await changeApprovals(many);
    const r = await call("ediabas_job", { ecu: "NVE_65", job: "IDENT" });
    expect(r.text.startsWith("[approvals changed] reviewedJobs in the VM changed")).toBe(true);
    expect(r.text).toContain("NVE_65:STATUS_00000@b8ae0dd9d242");
    expect(r.text).toContain(`... and ${3000 - NOTICE_ENTRIES_MAX} more.`);
    expect(r.text).not.toContain("NVE_65:STATUS_02999@b8ae0dd9d242");
    expect(r.text).toContain("NVE_65 IDENT: job status OKAY");
    expect(r.text).not.toContain("… cut at");
  });

  it("on an error result the error's [code] comes first, then the notice, then the car-session line", async () => {
    const { call } = await changeApprovals(["D_MOTOR:IDENT@7b69322a754f"], {
      "POST /ediabas/job": () => agentErr(409, "cable_in_use", "INPA holds the cable.", { processes: [{ name: "INPALOAD", pid: 4312 }] }),
    });
    const r = await call("ediabas_job", { ecu: "NVE_65", job: "IDENT" });
    expect(r.isError).toBe(true);
    expect(r.text).toMatch(/^\[cable_in_use\] [^\n]+\n\n\[approvals changed\] reviewedJobs in the VM changed since this server started: now D_MOTOR:IDENT@7b69322a754f\. Only the owner should change it\.\n\nCar session: /);
  });

  it("on a success result the notice stays first", async () => {
    const { call } = await changeApprovals(["D_MOTOR:IDENT@7b69322a754f"]);
    const r = await call("ediabas_job", { ecu: "NVE_65", job: "IDENT" });
    expect(r.isError).toBeFalsy();
    expect(r.text).toMatch(/^\[approvals changed\] [^\n]+\n\nNVE_65 IDENT: job status OKAY[\s\S]*\n\nCar session: [^\n]+$/);
  });

  it("flags a change between two ediabas_job calls, and the audit line says so", async () => {
    override("GET /health", () => ({ status: 200, body: phase2Health({ approvals: approvals(["NVE_65:STATUS_X@b8ae0dd9d242"], "aaaaaaaaaaaa") }) }));
    const { call, auditLines } = await connect();
    const first = await call("ediabas_job", { ecu: "NVE_65", job: "IDENT" });
    expect(first.text).not.toContain("[approvals changed]");
    override("GET /health", () => ({
      status: 200,
      body: phase2Health({ approvals: approvals(["NVE_65:STATUS_X@b8ae0dd9d242", "D_MOTOR:IDENT@7b69322a754f"], "bbbbbbbbbbbb") }),
    }));
    const second = await call("ediabas_job", { ecu: "NVE_65", job: "IDENT" });
    expect(second.isError).toBeFalsy();
    expect(second.text.startsWith("[approvals changed] reviewedJobs in the VM changed")).toBe(true);
    expect(second.text).toContain("D_MOTOR:IDENT@7b69322a754f");
    const lines = await auditLines();
    expect(lines[0]).not.toContain("approvals_changed");
    expect(lines[1]).toMatch(/\tediabas_job\tok\t\d+\tecu=NVE_65\tjob=IDENT\tapprovals_changed=1$/);
    const third = await call("ediabas_job", { ecu: "NVE_65", job: "IDENT" });
    expect(third.text).not.toContain("[approvals changed]");
  });

  it("refuses every ediabas call with [policy_mismatch] when the agent's policy hash differs, and sends nothing to /ediabas/*", async () => {
    override("GET /health", () => ({ status: 200, body: phase2Health({ policySha256: "0".repeat(64) }) }));
    const { call, auditLines } = await connect();
    const r = await call("ediabas_job", { ecu: "NVE_65", job: "IDENT" });
    expect(r.isError).toBe(true);
    expect(r.text).toMatch(/^\[policy_mismatch\] The agent's job policy \(sha256 000000000000\) differs from this Mac's \([0-9a-f]{12}\)\. Update the agent or the Mac server so both carry the same policy\/policy\.json\./);
    for (const [tool, args] of [
      ["ediabas_jobs", { jobs: [{ ecu: "NVE_65", job: "IDENT" }] }],
      ["ediabas_read_faults", { ecus: ["NVE_65"] }],
      ["ediabas_list_ecus", {}],
      ["ediabas_describe", { ecu: "NVE_65" }],
      ["ediabas_vehicle_status", {}],
    ] as const) {
      expect((await call(tool, args)).text).toMatch(/^\[policy_mismatch\]/);
    }
    expect(agent.requests.filter((q) => q.path.startsWith("/ediabas/"))).toEqual([]);
    expect((await auditLines())[0]).toMatch(/\tediabas_job\tpolicy_mismatch\t/);
  });

  it("runs the job when the agent reports no policy hash, or the same one", async () => {
    const { call } = await connect();
    expect((await call("ediabas_job", { ecu: "NVE_65", job: "IDENT" })).isError).toBeFalsy();
    const same = PolicyGuard.load().sha256!;
    override("GET /health", () => ({ status: 200, body: phase2Health({ policySha256: same }) }));
    expect((await call("ediabas_job", { ecu: "NVE_65", job: "IDENT" })).isError).toBeFalsy();
  });

  it("vm_health shows both hashes", async () => {
    const same = PolicyGuard.load().sha256!;
    override("GET /health", () => ({ status: 200, body: phase2Health({ policySha256: "0".repeat(64) }) }));
    const { call } = await connect();
    const r = await call("vm_health");
    expect(r.text).toContain("sha256 000000000000");
    expect(r.text).toContain(`sha256 ${same.slice(0, 12)}`);
    expect(r.text).toContain("WARNING");
  });

  it("a health failure before an ediabas call gives the tool's usual unreachable error", async () => {
    agent.setHandler((req) => req.socket.destroy());
    const { call } = await connect();
    const r = await call("ediabas_job", { ecu: "NVE_65", job: "IDENT" });
    expect(r.isError).toBe(true);
    expect(r.text).toMatch(/^\[/);
  });
});

describe("ediabas_job", () => {
  it("runs an allowed job and formats the result sets, distances miles first", async () => {
    const { call, auditLines } = await connect();
    const r = await call("ediabas_job", { ecu: "nve_65", job: "ident", results: "ID_BMW_NR" });
    expect(r.isError).toBeFalsy();
    expect(r.text).toContain("NVE_65 IDENT: job status OKAY, variant NVE_65, 300 ms, 1 result set");
    expect(r.text).toContain("  KM_STAND=100,000 mi (160,935 km)");
    expect(JSON.parse(toolRequests()[0]!.body)).toEqual({ ecu: "nve_65", job: "ident", results: "ID_BMW_NR" });
    expect((await auditLines())[0]).toMatch(/\tediabas_job\tok\t\d+\tecu=nve_65\tjob=ident$/);
  });

  it("returns the agent's JSON with raw: true", async () => {
    const { call } = await connect();
    const r = await call("ediabas_job", { ecu: "NVE_65", job: "IDENT", raw: true });
    const json = r.text.split("\n\nCar session: ")[0]!;
    expect(JSON.parse(json)).toMatchObject({ ecu: "NVE_65", job: "IDENT", sets: [{ KM_STAND: 160935 }] });
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
    ["job_needs_review", 403, { ecu: "NVE_65", job: "STATUS_X", description: "Startet\u0007 den Test", matched: ["start"] }, /matched: start\)\. Description: "Startet\s+den Test"\. The user decides/],
    ["job_needs_review", 403, { ecu: "NVE_65", job: "STATUS_X", description: "", matched: [] }, /has no description/],
    ["job_needs_review", 403, { ecu: "NVE_65", job: "STATUS_X", description: "Jobbeschreibung", matched: [] }, /^(?![\s\S]*suggests)[\s\S]*description has nothing in it but a label \("Jobbeschreibung"\), so the agent cannot tell that it only reads/],
    [
      "ediabas_error",
      502,
      { errorCode: 19, errorText: "IFH-0009: NO RESPONSE FROM CONTROLUNIT" },
      /^\[ediabas_error\] (?![\s\S]*UTM)EDIABAS reported error 19: IFH-0009: NO RESPONSE FROM CONTROLUNIT\. The cable works but this ECU did not answer: the ignition is off, the module is not fitted or is asleep, or this SGBD does not match the module fitted\./,
    ],
    // The code in the text wins when the number is missing.
    ["ediabas_error", 502, { errorText: "IFH-0009: NO RESPONSE FROM CONTROLUNIT" }, /The cable works but this ECU did not answer/],
    [
      "ediabas_error",
      502,
      { errorCode: 12, errorText: "IFH-0002: NO RESPONSE FROM INTERFACE" },
      /^\[ediabas_error\] (?![\s\S]*ECU did not answer)EDIABAS reported error 12: IFH-0002: NO RESPONSE FROM INTERFACE\. The cable itself did not answer, so nothing reached the car: .*OBD port.*UTM's USB menu/,
    ],
    [
      "ediabas_error",
      502,
      { errorCode: 13, errorText: "IFH-0003: DATATRANSMISSION TO INTERFACE DISTURBED" },
      /^\[ediabas_error\] (?![\s\S]*(ECU did not answer|D_MOTOR|identification))EDIABAS reported error 13: IFH-0003: DATATRANSMISSION TO INTERFACE DISTURBED\. The exchange with the cable failed, which points at the cable before the ECU: at the desk every job on a real SGBD or group file gave this with the cable in the VM but not plugged into the car\. .*OBD port.*If both are fine and the ignition is on, this module may not have answered; say so rather than retrying\./,
    ],
    ...([
      [17, "IFH-0007: WRONG UBATT"],
      [25, "IFH-0015: UBATT ON/OFF ERROR"],
      [47, "IFH-0037: LOW UBATT"],
    ] as const).map(
      ([n, t]) =>
        [
          "ediabas_error",
          502,
          { errorCode: n, errorText: t },
          new RegExp(`^\\[ediabas_error\\] (?![\\s\\S]*ECU did not answer)EDIABAS reported error ${n}: [^.]*\\. The cable reports the battery voltage at the OBD port as wrong or low\\..*charger`),
        ] as const,
    ),
    [
      "ediabas_error",
      502,
      { errorCode: 20, errorText: "IFH-0010: DATATRANSMISSION TO CONTROLUNIT DISTURBED" },
      /EDIABAS reported error 20: IFH-0010: DATATRANSMISSION TO CONTROLUNIT DISTURBED\. The ECU's answer was disturbed: .*low battery voltage/,
    ],
    ...([
      [26, "IFH-0016: IGNITION ON/OFF ERROR"],
      [42, "IFH-0032: IGNITION IS OFF"],
    ] as const).map(
      ([n, t]) =>
        [
          "ediabas_error",
          502,
          { errorCode: n, errorText: t },
          new RegExp(`EDIABAS reported error ${n}: [^.]*\\. The cable sees the ignition \\(KL15\\) as off\\. Ask the user whether the ignition is on`),
        ] as const,
    ),
    [
      "ediabas_error",
      502,
      { errorCode: 100, errorText: "SYS-0010: INITIALIZATION ERROR" },
      /EDIABAS reported error 100: SYS-0010: INITIALIZATION ERROR\. The SGBD could not start\. Some files need the module to answer even to load or describe \(seen at the desk with the ignition off\)/,
    ],
    // A code with no advice of its own keeps the general wording.
    ["ediabas_error", 502, { errorCode: 134, errorText: "SYS-0002: ECU OBJECT FILE NOT FOUND" }, /EDIABAS reported error 134: SYS-0002: ECU OBJECT FILE NOT FOUND\. This often means the ECU did not answer/],
    [
      "ediabas_error",
      502,
      { errorCode: 16, errorText: "IFH-0006: COMMAND NOT ACCEPTED" },
      /^\[ediabas_error\] EDIABAS reported error 16: IFH-0006: COMMAND NOT ACCEPTED\. The cable may not be attached to the VM: after it is unplugged and plugged back in, UTM does not hand it back by itself \(the user ticks it in UTM's USB menu\)\. Otherwise the ECU did not answer/,
    ],
    [
      "ediabas_error",
      502,
      { errorCode: 48, errorText: "IFH-0038: INTERFACE COMMAND NOT IMPLEMENTED" },
      /^\[ediabas_error\] EDIABAS reported error 48: IFH-0038: INTERFACE COMMAND NOT IMPLEMENTED\. The cable may not be attached to the VM: after the Mac sleeps, UTM hands it back to the Mac \(the user ticks it in UTM's USB menu\)\. Otherwise the ECU did not answer/,
    ],
    [
      "ediabas_error",
      502,
      { errorCode: 134, errorText: "SYS-0002: SOMETHING", resultsUnread: true },
      /^\[ediabas_error\] The job finished but its results could not be read \(EDIABAS error 134: SYS-0002: SOMETHING\)\. Ask the user before trying again\.$/,
    ],
    ["ediabas_error", 502, { errorCode: 19, errorText: "IFH-0009", resultsUnread: false }, /^\[ediabas_error\] EDIABAS reported error 19: IFH-0009\. The cable works but this ECU did not answer/],
    ["job_timeout", 504, { timeoutSeconds: 30 }, /did not finish within 30 s/],
    ["job_not_found", 404, { ecu: "NVE_65", job: "STATUS_X" }, /^\[job_not_found\] NVE_65 has no job STATUS_X\./],
    ["job_not_found", 404, null, /^\[job_not_found\] The SGBD has no such job\./],
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
      if (req.url === "/health" || req.url?.startsWith("/hello?")) stubHandler()(req, res);
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
      if (req.url === "/health" || req.url?.startsWith("/hello?")) stubHandler()(req, res);
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
      if (req.url === "/health" || req.url?.startsWith("/hello?")) stubHandler()(req, res);
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

describe("timeoutSeconds and namesOnly", () => {
  it("ediabas_job passes timeoutSeconds, 1 to 60", async () => {
    const { call } = await connect();
    await call("ediabas_job", { ecu: "NVE_65", job: "IDENT", timeoutSeconds: 45 });
    expect(JSON.parse(toolRequests()[0]!.body)).toEqual({ ecu: "NVE_65", job: "IDENT", timeoutSeconds: 45 });
    expect((await call("ediabas_job", { ecu: "NVE_65", job: "IDENT", timeoutSeconds: 61 })).isError).toBe(true);
    expect((await call("ediabas_job", { ecu: "NVE_65", job: "IDENT", timeoutSeconds: 0 })).isError).toBe(true);
    expect(toolRequests()).toHaveLength(1);
  });

  it("ediabas_jobs passes timeoutSeconds for each item, as the agent's request field", async () => {
    const { call } = await connect();
    await call("ediabas_jobs", { jobs: [{ ecu: "NVE_65", job: "IDENT" }], timeoutSeconds: 20 });
    expect(JSON.parse(toolRequests()[0]!.body)).toEqual({ items: [{ ecu: "NVE_65", job: "IDENT" }], timeoutSeconds: 20 });
    expect((await call("ediabas_jobs", { jobs: [{ ecu: "NVE_65", job: "IDENT" }], timeoutSeconds: 61 })).isError).toBe(true);
  });

  it("ediabas_describe with namesOnly asks for no comments and says what that means", async () => {
    const { call } = await connect();
    const r = await call("ediabas_describe", { ecu: "NVE_65", namesOnly: true });
    expect(new URL(toolRequests()[0]!.path, "http://x").searchParams.get("comments")).toBe("false");
    expect(r.text.split("\n")[1]).toBe("(names only: not screened, verdicts by name)");
    await call("ediabas_describe", { ecu: "NVE_65" });
    expect(new URL(toolRequests()[1]!.path, "http://x").searchParams.has("comments")).toBe(false);
  });
});

describe("ediabas_jobs", () => {
  const items = (r: { body: string }) => JSON.parse(r.body).items;

  it("runs several jobs in one request and prints one block per item in order", async () => {
    const { call, auditLines } = await connect();
    const r = await call("ediabas_jobs", {
      jobs: [
        { ecu: "NVE_65", job: "IDENT" },
        { ecu: "D_MOTOR", job: "FS_LESEN", results: "F_ORT_NR" },
        { ecu: "NVE_65", job: "STATUS_X", args: "1;2" },
      ],
    });
    expect(r.isError).toBeFalsy();
    const reqs = toolRequests();
    expect(reqs).toHaveLength(1);
    expect(reqs[0]!.path).toBe("/ediabas/jobs");
    expect(items(reqs[0]!)).toEqual([
      { ecu: "NVE_65", job: "IDENT" },
      { ecu: "D_MOTOR", job: "FS_LESEN", results: "F_ORT_NR" },
      { ecu: "NVE_65", job: "STATUS_X", args: "1;2" },
    ]);
    expect(r.text).toMatch(/^3 of 3 ran\.\n\nNVE_65 IDENT: job status OKAY/);
    expect(r.text.indexOf("D_MOTOR FS_LESEN:")).toBeGreaterThan(r.text.indexOf("NVE_65 IDENT:"));
    expect(r.text.indexOf("NVE_65 STATUS_X:")).toBeGreaterThan(r.text.indexOf("D_MOTOR FS_LESEN:"));
    expect((await auditLines())[0]).toMatch(/\tediabas_jobs\tok\t\d+\tecu=NVE_65,D_MOTOR,NVE_65\tjob=IDENT,FS_LESEN,STATUS_X$/);
  });

  it("shows a failed item as its [code] line and counts what ran", async () => {
    override("POST /ediabas/jobs", (q) => ({
      status: 200,
      body: {
        items: (q.body as { items: { ecu: string; job: string }[] }).items.map((it) =>
          it.ecu === "BAD"
            ? { ok: false, result: null, error: { code: "job_not_found", message: "x", details: { ecu: "BAD", job: it.job } } }
            : { ok: true, result: jobResult(it.ecu, it.job, [{ A: 1 }]), error: null },
        ),
        durationMs: 5,
      },
    }));
    const { call } = await connect();
    const r = await call("ediabas_jobs", { jobs: [{ ecu: "NVE_65", job: "IDENT" }, { ecu: "BAD", job: "IDENT" }] });
    expect(r.isError).toBeFalsy();
    expect(r.text).toMatch(/^1 of 2 ran\./);
    expect(r.text).toContain("\n\nBAD IDENT: [job_not_found] BAD has no job IDENT.");
  });

  it("a batch item whose results could not be read is not blamed on the ECU", async () => {
    override("POST /ediabas/jobs", () => ({
      status: 200,
      body: {
        items: [{ ok: false, result: null, error: { code: "ediabas_error", message: "x", details: { errorCode: 134, errorText: "SYS-0002", resultsUnread: true } } }],
        durationMs: 5,
      },
    }));
    const { call } = await connect();
    const r = await call("ediabas_jobs", { jobs: [{ ecu: "NVE_65", job: "IDENT" }] });
    expect(r.text).toContain(
      "\n\nNVE_65 IDENT: [ediabas_error] The job finished but its results could not be read (EDIABAS error 134: SYS-0002). Ask the user before trying again.",
    );
    expect(r.text).not.toMatch(/did not answer|cable/i);
  });

  it("returns the agent's JSON per item with raw: true", async () => {
    const { call } = await connect();
    const r = await call("ediabas_jobs", { jobs: [{ ecu: "NVE_65", job: "IDENT" }], raw: true });
    const [, json] = r.text.split("\n\n");
    expect(JSON.parse(json!)).toMatchObject({ ecu: "NVE_65", job: "IDENT" });
  });

  it("refuses the whole call on the Mac if any item is refused, with zero requests", async () => {
    const fetchSpy = vi.fn<typeof fetch>((...a) => fetch(...a));
    const { call, auditLines } = await connect({ fetch: fetchSpy });
    const r = await call("ediabas_jobs", { jobs: [{ ecu: "NVE_65", job: "IDENT" }, { ecu: "NVE_65", job: "FS_LOESCHEN" }] });
    expect(r.isError).toBe(true);
    expect(r.text).toMatch(/^\[job_not_allowed\] Refused by the job policy: NVE_65 FS_LOESCHEN/);
    expect(r.text).toMatch(/Nothing was sent to the VM/);
    expect(fetchSpy).not.toHaveBeenCalled();
    expect((await auditLines())[0]).toMatch(/\tjob_not_allowed\t\d+\tecu=NVE_65,NVE_65\tjob=IDENT,FS_LOESCHEN\trule=deny\.substring:LOESCH$/);
  });

  it("refuses arguments that fail the policy's shape check, with zero requests", async () => {
    const { call } = await connect();
    const r = await call("ediabas_jobs", { jobs: [{ ecu: "NVE_65", job: "IDENT", args: "gr\u00fcn" }] });
    expect(r.isError).toBe(true);
    expect(r.text).toContain("rule shape.args");
    expect(agent.requests).toHaveLength(0);
  });

  it("rejects 21 items, and none, by the schema", async () => {
    const { call } = await connect();
    expect((await call("ediabas_jobs", { jobs: Array.from({ length: 21 }, () => ({ ecu: "NVE_65", job: "IDENT" })) })).isError).toBe(true);
    expect((await call("ediabas_jobs", { jobs: [] })).isError).toBe(true);
    expect(agent.requests).toHaveLength(0);
  });

  it("returns isError when the request fails as a whole", async () => {
    override("POST /ediabas/jobs", () => agentErr(409, "cable_in_use", "x", null));
    const { call } = await connect();
    const r = await call("ediabas_jobs", { jobs: [{ ecu: "NVE_65", job: "IDENT" }] });
    expect(r.isError).toBe(true);
    expect(r.text).toMatch(/^\[cable_in_use\]/);
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
    expect(r.text).toContain("present/stored: Fault currently present | frequency: 3 | logged at: 100,000 mi (160,935 km)");
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
    expect(r.text).toMatch(/BAD\n  Fault memory: error: \[ediabas_error\] EDIABAS reported error 19: IFH-0009/);
    expect(r.text).toMatch(/GONE\n  Fault memory: error: \[not_run\] Not run/);
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
    expect(r.text).toMatch(/D_MOTOR\n  Fault memory: error: \[not_run\] Not run: the agent said: "A BMW tool started during the request \(INPALOAD pid 4312\)/);
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
    expect(r.text).toMatch(/NVE_65\n  Fault memory: error: \[cable_not_found\] EDIABAS could not initialise the diagnostic interface/);
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
    expect(r.text).toMatch(/ECU_10\n  Fault memory: error: \[cable_in_use\] The diagnostic cable is in use by WinKFP \(pid 9\)/);
    expect(r.text).toMatch(/ECU_11\n  Fault memory: error: [^\n]+\n  Info memory: error: \[cable_in_use\] The diagnostic cable/);
  });

  it("says an info memory that did not read may be missing, not that the module failed", async () => {
    override("POST /ediabas/jobs", (q) => {
      const items = (q.body as { items: { ecu: string; job: string }[] }).items;
      return {
        status: 200,
        body: {
          items: items.map((it) =>
            it.job === "IS_LESEN" || it.ecu === "NOFS"
              ? { ok: false, result: null, error: { code: "ediabas_error", message: "EDIABAS job failed: SYS-0008: JOB NOT FOUND", details: { errorCode: 19, errorText: "SYS-0008: JOB NOT FOUND" } } }
              : { ok: true, result: jobResult(it.ecu, it.job, [{ JOB_STATUS: "OKAY" }]), error: null },
          ),
          durationMs: 5,
        },
      };
    });
    const { call } = await connect();
    const r = await call("ediabas_read_faults", { ecus: ["NVE_65", "NOFS"], includeInfoMemory: true });
    expect(r.text).toContain(
      "NVE_65\n  Fault memory: no faults stored.\n  Info memory: not read ([ediabas_error] EDIABAS job failed: SYS-0008: JOB NOT FOUND). Many modules have no info memory (no IS_LESEN job); ediabas_describe NVE_65 shows whether this one has it.",
    );
    expect(r.text).not.toContain("NVE_65 has no job");
    // A missing FS_LESEN is a plain ediabas_error with the run hint (no way to tell it from a silent ECU).
    expect(r.text).toMatch(/NOFS\n  Fault memory: error: \[ediabas_error\] EDIABAS reported error 19: SYS-0008: JOB NOT FOUND\..*If the job name may be wrong, check it with ediabas_describe\./);
  });

  it("reads a repeated ECU once", async () => {
    const { call } = await connect();
    await call("ediabas_read_faults", { ecus: ["NVE_65", "nve_65"] });
    expect(JSON.parse(toolRequests()[0]!.body).items).toHaveLength(1);
  });
});

describe("car session line", () => {
  const LINE = /\n\nCar session: (\d+ min )?\d+ s since the first read from the car \(aim to finish within 10 min\)\.$/;

  it("ends every car-facing result once a read from the car succeeded, and no other", async () => {
    const clock = { t: 0 };
    const { call } = await connect({ now: () => clock.t, voltageSources: NVE_VOLTAGE });
    const first = await call("ediabas_job", { ecu: "NVE_65", job: "IDENT" });
    expect(first.text).toMatch(/\n\nCar session: 0 s since the first read from the car \(aim to finish within 10 min\)\.$/);
    clock.t = 250_000;
    expect((await call("ediabas_read_faults", { ecus: ["NVE_65"] })).text).toMatch(/Car session: 4 min 10 s since/);
    expect((await call("ediabas_jobs", { jobs: [{ ecu: "NVE_65", job: "IDENT" }] })).text).toMatch(LINE);
    expect((await call("ediabas_vehicle_status")).text).toMatch(LINE);
    for (const [tool, args] of [
      ["ediabas_describe", { ecu: "NVE_65" }],
      ["ediabas_list_ecus", {}],
      ["vm_health", {}],
      ["vm_list_windows", {}],
    ] as const) {
      expect((await call(tool, args)).text, tool).not.toMatch(/Car session/);
    }
  });

  it("is not started by TMODE or UTILITY, or by a failed read", async () => {
    const { call } = await connect({ now: () => 0, voltageSources: NVE_VOLTAGE });
    expect((await call("ediabas_job", { ecu: "TMODE", job: "LESE_INTERFACE_TYP" })).text).not.toMatch(/Car session/);
    expect((await call("ediabas_job", { ecu: "UTILITY", job: "STATUS_UBATT" })).text).not.toMatch(/Car session/);
    override("POST /ediabas/job", () => agentErr(502, "ediabas_error", "x", { errorCode: 19, errorText: "IFH-0009" }));
    expect((await call("ediabas_job", { ecu: "NVE_65", job: "IDENT" })).text).not.toMatch(/Car session/);
    // The stub's vehicle status has one voltage read from NVE_65: that is a read from the car.
    expect((await call("ediabas_vehicle_status")).text).toMatch(/Car session: 0 s/);
  });
});
