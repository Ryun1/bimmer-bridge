import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createAppServer, INSTRUCTIONS, SERVER_VERSION } from "../src/server.js";
import { loadConfig } from "../src/config.js";
import type { ExecFileFn } from "../src/vmAddress.js";
import { AGENT_TOKEN, LOOPBACK_BRIDGE, NO_BRIDGE, contractHandler, json, leaseFile, startFakeAgent, type FakeAgent } from "./fixtures.js";

let dir: string;
let agent: FakeAgent;
beforeEach(async () => {
  dir = await mkdtemp(join(tmpdir(), "ediabas-server-"));
  agent = await startFakeAgent();
});
afterEach(async () => {
  vi.restoreAllMocks();
  await agent.close();
  await rm(dir, { recursive: true, force: true });
});

interface Setup {
  env?: Record<string, string>;
  keychain?: string | null;
  ifaces?: unknown;
  leases?: string;
}

async function connect(s: Setup = {}) {
  const auditPath = join(dir, "logs", "audit.log");
  const env = { EDIABAS_AGENT_URL: agent.url, EDIABAS_AUDIT_LOG: auditPath, ...s.env };
  const { config } = loadConfig(env, dir);
  const logs: string[] = [];
  const keychain = s.keychain === undefined ? AGENT_TOKEN : s.keychain;
  const execFile = vi.fn<ExecFileFn>(async (file) => {
    if (file === "/usr/bin/security") {
      if (keychain === null) throw Object.assign(new Error("not found"), { code: 44 });
      return keychain + "\n";
    }
    throw new Error(`unexpected command ${file}`);
  });
  const server = createAppServer(config, {
    execFile,
    readFile: async () => s.leases ?? "",
    networkInterfaces: () => (s.ifaces ?? LOOPBACK_BRIDGE) as never,
    log: (m) => logs.push(m),
  });
  const [clientT, serverT] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: "test", version: "0.0.0" });
  await server.connect(serverT);
  await client.connect(clientT);
  return { client, server, logs, auditPath, execFile };
}

function textOf(result: unknown): string {
  const r = result as { content: { type: string; text: string }[] };
  return r.content.map((c) => c.text).join("\n");
}

describe("MCP server", () => {
  it("identifies itself with instructions", async () => {
    const { client } = await connect();
    expect(client.getServerVersion()).toEqual({ name: "ediabas-bridge", version: SERVER_VERSION });
    expect(SERVER_VERSION).toBe("0.1.0");
    const instr = client.getInstructions() ?? "";
    expect(instr).toBe(INSTRUCTIONS);
    expect(instr.split(/\s+/).length).toBeLessThanOrEqual(180);
    for (const tool of ["vm_list_windows", "vm_screenshot", "vm_logs_list", "vm_logs_read", "ediabas_list_ecus", "ediabas_describe", "ediabas_read_faults", "ediabas_job", "ediabas_vehicle_status"]) {
      expect(instr).toContain(tool);
    }
    expect(instr).toMatch(/Call vm_health first/);
    expect(instr).toMatch(/agent_outdated is not installed in the VM yet/);
    expect(instr).toMatch(/Never retry a refusal/);
    expect(instr).toMatch(/never guess names/);
    expect(instr).toMatch(/read-only/i);
    expect(instr).toContain("vm_health");
    expect(instr).toMatch(/cannot click, type, clear faults, code or flash/i);
    expect(instr).toMatch(/rather than retrying|instead of retrying|do not retry/i);
  });

  it("lists the tools with read-only annotations, titles and descriptions", async () => {
    const { client } = await connect();
    const { tools } = await client.listTools();
    expect(tools).toHaveLength(10);
    for (const t of tools) {
      expect(t.title).toBeTruthy();
      expect(t.description && t.description.length).toBeGreaterThan(40);
      expect(t.description).not.toMatch(/needed for screenshots/);
      expect(t.annotations).toMatchObject({
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      });
      expect(t.inputSchema.type).toBe("object");
      if (["vm_health", "vm_list_windows", "ediabas_vehicle_status"].includes(t.name)) {
        expect(Object.keys(t.inputSchema.properties ?? {})).toEqual([]);
      }
    }
  });

  it("vm_health returns the formatted health text", async () => {
    const { client } = await connect();
    const r = await client.callTool({ name: "vm_health", arguments: {} });
    expect(r.isError).toBeFalsy();
    const t = textOf(r);
    expect(t).toContain("0.1.0");
    expect(t).toContain("INPALOAD (pid 4312)");
    expect(t).toMatch(/EDIABAS DLL: found/);
  });

  it("vm_list_windows returns the table", async () => {
    const { client } = await connect();
    const r = await client.callTool({ name: "vm_list_windows", arguments: {} });
    expect(r.isError).toBeFalsy();
    const t = textOf(r);
    expect(t).toContain("INPA - BMW Group");
    expect(t).toContain("1280x800");
  });

  it("works with discovery against a fake agent that counts as on-link", async () => {
    const { client } = await connect({
      env: { EDIABAS_AGENT_URL: "", EDIABAS_AGENT_PORT: String(agent.port) },
      leases: leaseFile({ ip: "127.0.0.1", lease: 0x6b01baf4 }),
    });
    const r = await client.callTool({ name: "vm_health", arguments: {} });
    expect(r.isError).toBeFalsy();
  });

  it("returns isError with the actionable message when the VM is off", async () => {
    const { client } = await connect({ env: { EDIABAS_AGENT_URL: "" }, ifaces: NO_BRIDGE });
    const r = await client.callTool({ name: "vm_list_windows", arguments: {} });
    expect(r.isError).toBe(true);
    const t = textOf(r);
    expect(t).toContain("The VM does not appear to be running. Start the 'Windows' VM in UTM and log in.");
    expect(t).not.toMatch(/\n\s+at /); // no stack trace
  });

  it("returns isError for a token mismatch, and the token never leaks", async () => {
    agent.setHandler(contractHandler("some-other-token"));
    const { client, auditPath, logs } = await connect();
    const r = await client.callTool({ name: "vm_health", arguments: {} });
    expect(r.isError).toBe(true);
    expect(textOf(r)).toContain("The agent rejected the token.");
    const audit = await readFile(auditPath, "utf8");
    expect(audit).toMatch(/\tvm_health\tunauthorized\t\d+\n$/);
    for (const s of [JSON.stringify(r), audit, logs.join("\n")]) expect(s).not.toContain(AGENT_TOKEN);
  });

  it("logs probe failures and acceptance to the injected stderr logger, without the token", async () => {
    const { client, logs } = await connect({
      env: { EDIABAS_AGENT_URL: "", EDIABAS_AGENT_PORT: String(agent.port) },
      leases: leaseFile({ ip: "127.0.0.1", lease: 0x6b01baf4 }),
    });
    await client.callTool({ name: "vm_health", arguments: {} });
    expect(logs).toContain(`discovery accepted 127.0.0.1:${agent.port} (source: leases)`);
    agent.setHandler(contractHandler("other"));
    const r = await client.callTool({ name: "vm_list_windows", arguments: {} });
    expect(r.isError).toBe(true);
    expect(logs).toContain(`request 127.0.0.1:${agent.port} GET /windows failed: HTTP 401`);
    expect(logs.join("\n")).not.toContain(AGENT_TOKEN);
  });

  it("reports a missing token with the fix, and the server still serves tools/list", async () => {
    const { client } = await connect({ keychain: null });
    expect((await client.listTools()).tools).toHaveLength(10);
    const r = await client.callTool({ name: "vm_health", arguments: {} });
    expect(r.isError).toBe(true);
    expect(textOf(r)).toContain("security add-generic-password -s ediabas-bridge -a agent -w");
    expect(agent.requests).toHaveLength(0);
  });

  it("uses the env token as a fallback with one stderr warning", async () => {
    const { client, logs } = await connect({ keychain: null, env: { EDIABAS_AGENT_TOKEN: AGENT_TOKEN } });
    expect((await client.callTool({ name: "vm_health", arguments: {} })).isError).toBeFalsy();
    expect((await client.callTool({ name: "vm_list_windows", arguments: {} })).isError).toBeFalsy();
    const warnings = logs.filter((l) => l.includes("EDIABAS_AGENT_TOKEN"));
    expect(warnings).toHaveLength(1);
    expect(logs.join("\n")).not.toContain(AGENT_TOKEN);
  });

  it("returns isError for agent errors and malformed bodies", async () => {
    const { client } = await connect();
    await client.callTool({ name: "vm_health", arguments: {} });
    agent.setHandler((req, res) => {
      if (req.url === "/windows") json(res, 200, { windows: "nope" });
      else contractHandler()(req, res);
    });
    const r = await client.callTool({ name: "vm_list_windows", arguments: {} });
    expect(r.isError).toBe(true);
    expect(textOf(r)).toMatch(/did not match/i);
  });

  it("writes one audit line per tool call", async () => {
    const { client, auditPath } = await connect();
    await client.callTool({ name: "vm_health", arguments: {} });
    await client.callTool({ name: "vm_list_windows", arguments: {} });
    const lines = (await readFile(auditPath, "utf8")).trimEnd().split("\n");
    expect(lines).toHaveLength(2);
    expect(lines[0]).toMatch(/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z\tvm_health\tok\t\d+$/);
    expect(lines[1]).toMatch(/\tvm_list_windows\tok\t\d+$/);
  });

  it("does not fail the call when the audit log cannot be written", async () => {
    const { client, logs } = await connect({ env: { EDIABAS_AUDIT_LOG: "/dev/null/cannot/audit.log" } });
    const r = await client.callTool({ name: "vm_health", arguments: {} });
    expect(r.isError).toBeFalsy();
    expect(logs.some((l) => /audit/i.test(l))).toBe(true);
  });

  it("writes nothing to stdout during tool calls or logging", async () => {
    const stdout = vi.spyOn(process.stdout, "write");
    const consoleLog = vi.spyOn(console, "log");
    const { client } = await connect({ keychain: null, env: { EDIABAS_AGENT_TOKEN: AGENT_TOKEN, EDIABAS_AUDIT_LOG: "/dev/null/x/audit.log" } });
    await client.callTool({ name: "vm_health", arguments: {} });
    await client.callTool({ name: "vm_list_windows", arguments: {} });
    agent.setHandler(contractHandler("other"));
    await client.callTool({ name: "vm_health", arguments: {} });
    expect(stdout).not.toHaveBeenCalled();
    expect(consoleLog).not.toHaveBeenCalled();
  });
});

describe("default stderr logger", () => {
  it("writes to stderr, never stdout", async () => {
    const { stderrLog } = await import("../src/server.js");
    const out = vi.spyOn(process.stdout, "write");
    const err = vi.spyOn(process.stderr, "write").mockImplementation(() => true);
    stderrLog("hello");
    expect(out).not.toHaveBeenCalled();
    expect(err).toHaveBeenCalledWith(expect.stringContaining("hello"));
  });
});
