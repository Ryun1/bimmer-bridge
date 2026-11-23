import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
// The real MCP server, from source (not dist).
import { loadConfig } from "../../mac-mcp/src/config.ts";
import { createAppServer } from "../../mac-mcp/src/server.ts";
import { loadScenario, startReplayAgent, type ReplayAgent } from "../replay-agent.ts";
import { TOKEN, agentError, synthHealth, synthJobResult, writeRecording, writeScenarioFile } from "./synthetic.ts";

/**
 * End to end: the real MCP server (mac-mcp/src) pointed with EDIABAS_AGENT_URL at the replay
 * agent, which serves synthetic recordings from a temporary folder.
 */

let root: string;
let agent: ReplayAgent;
const transcript: string[] = [];

beforeEach(() => {
  root = mkdtempSync(join(tmpdir(), "replay-session-"));
});
afterEach(async () => {
  await agent?.close();
  rmSync(root, { recursive: true, force: true });
});

function synthScenario(): void {
  const normal = join(root, "synth-normal");
  writeRecording(normal, { method: "GET", path: "/health", response: synthHealth() });
  writeRecording(normal, {
    method: "POST",
    path: "/ediabas/jobs",
    body: { items: [{ ecu: "SYNTH_ECU", job: "FS_LESEN" }] },
    response: {
      items: [
        {
          ok: true,
          result: synthJobResult("SYNTH_ECU", "FS_LESEN", [
            { F_ORT_NR: 0xabcd, F_ORT_TEXT: "Synthetic test fault", F_VORHANDEN_TEXT: "stored", F_HFK: 1, F_UW_KM: 160935 },
          ]),
          error: null,
        },
      ],
      durationMs: 20,
    },
  });
  const busy = join(root, "synth-tool-open");
  writeRecording(busy, { method: "GET", path: "/health", response: synthHealth({ conflictingProcesses: [{ name: "SYNTH_TOOL", pid: 7 }] }) });
  const f = writeRecording(busy, {
    method: "POST",
    path: "/ediabas/jobs",
    body: { items: [{ ecu: "SYNTH_ECU", job: "FS_LESEN" }] },
    status: 409,
    response: agentError("cable_in_use", "synthetic: cable in use", { processes: [{ name: "SYNTH_TOOL", pid: 7 }] }),
  });
  writeScenarioFile(busy, { base: "synth-normal", routes: [{ pathPrefix: "/ediabas/", recording: f }] });
}

const keychainReads: string[] = [];

async function session(scenario: string, fixturesDir: string = root) {
  agent = await startReplayAgent({ scenario: loadScenario(fixturesDir, scenario), token: TOKEN, port: 0, log: () => {} });
  const env = { EDIABAS_AGENT_URL: agent.url, EDIABAS_AGENT_TOKEN: TOKEN, EDIABAS_AUDIT_LOG: join(root, "audit.log") };
  const { config } = loadConfig(env, root);
  const server = createAppServer(config, {
    // With EDIABAS_AGENT_URL set the server must use EDIABAS_AGENT_TOKEN and never read the
    // Keychain (whose token is the real VM agent's secret): any execFile call is a failure.
    execFile: async (file) => {
      keychainReads.push(file);
      return "real-vm-agent-token-must-not-be-sent-xxxxxxxx\n";
    },
    readFile: async () => "",
    networkInterfaces: () => ({}),
    log: () => {},
  });
  const [clientT, serverT] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: "replay-session-test", version: "0.0.0" });
  await server.connect(serverT);
  await client.connect(clientT);
  return client;
}

async function tool(client: Client, name: string, args: Record<string, unknown> = {}) {
  const r = (await client.callTool({ name, arguments: args })) as { isError?: boolean; content: { type: string; text?: string }[] };
  const text = r.content.map((c) => c.text ?? `[${c.type}]`).join("\n");
  transcript.push(`> ${name} ${JSON.stringify(args)}${r.isError ? "  (isError)" : ""}\n${text}\n`);
  return { isError: r.isError === true, text };
}

describe("real MCP server against the replay agent", () => {
  it("answers vm_health and ediabas_read_faults from recordings, and says so when there is none", async () => {
    synthScenario();
    const client = await session("synth-normal");

    const h = await tool(client, "vm_health");
    expect(h.isError).toBe(false);
    expect(h.text).toContain("0.2.0");

    const f = await tool(client, "ediabas_read_faults", { ecus: ["SYNTH_ECU"] });
    expect(f.isError).toBe(false);
    expect(f.text).toContain("Synthetic test fault");
    expect(f.text).toMatch(/100,000 mi \(160,935 km\)/);

    expect(keychainReads).toEqual([]);

    const n = await tool(client, "ediabas_job", { ecu: "SYNTH_ECU", job: "IDENT" });
    expect(n.isError).toBe(true);
    expect(n.text).toContain("no_recording");
    expect(n.text).toContain("POST /ediabas/job");
  });

  it("an overlay with a tool open: health names it and EDIABAS calls get cable_in_use", async () => {
    synthScenario();
    const client = await session("synth-tool-open");
    const h = await tool(client, "vm_health");
    expect(h.text).toContain("SYNTH_TOOL");
    const f = await tool(client, "ediabas_read_faults", { ecus: ["SYNTH_ECU"] });
    expect(f.isError).toBe(true);
    expect(f.text).toMatch(/cable is in use by SYNTH_TOOL \(pid 7\)/);
    expect(f.text).toMatch(/Do not retry in a loop/);
    // The vehicle status is one /ediabas/jobs batch, so the overlay's route answers it too.
    const v = await tool(client, "ediabas_vehicle_status");
    expect(v.isError).toBe(true);
    expect(v.text).toMatch(/^\[cable_in_use\] The diagnostic cable is in use by SYNTH_TOOL \(pid 7\)/);
  });

  it("the real recordings from the car: whole-car views and the battery batch fit the text budget", async () => {
    const fixtures = join(import.meta.dirname, "..", "fixtures");
    const client = await session("battery", fixtures);

    const ident = await tool(client, "ediabas_job", { ecu: "E60", job: "IDENT_FUNKTIONAL", timeoutSeconds: 60 });
    expect(ident.isError).toBe(false);
    expect(ident.text).toContain("43 modules answered");
    expect(ident.text).toContain("PGS (address 0x27): PGS_65_2");
    expect(ident.text).not.toContain("… cut at");

    const faults = await tool(client, "ediabas_job", { ecu: "E60", job: "FS_LESEN_FUNKTIONAL", timeoutSeconds: 60 });
    expect(faults.isError).toBe(false);
    expect(faults.text).toContain("15 modules with faults (24 codes)");
    expect(faults.text).toContain("PGS (address 0x27): 0xA0B7 present now (F_ART 0x60)");
    expect(faults.text).not.toContain("… cut at");

    const rec = JSON.parse(readFileSync(join(fixtures, "battery", "20261123T194644919Z-0016-POST-ediabas_jobs-12items.json"), "utf8"));
    const items = rec.request.body.items;
    expect(items).toHaveLength(12);
    const batch = await tool(client, "ediabas_jobs", { jobs: items });
    expect(batch.isError).toBe(false);
    expect(batch.text).toContain("STAT_ANZAHL_FAHRTEN_0_5_KM_1=4");
    expect(batch.text).toContain("CAS STATUS_WAKEUP_HISTORY: job status ERROR_ECU_SUBFUNCTION_NOT_SUPPORTED__INVALID_FORMAT (the module answered that it does not support this job");
  });

  it("with nothing listening, the server reports that the agent is not answering", async () => {
    synthScenario();
    const client = await session("synth-normal");
    await agent.close();
    const h = await tool(client, "vm_health");
    expect(h.isError).toBe(true);
    // Print the session so it can be quoted in the task report.
    process.stdout.write(`\n--- MCP session transcript (synthetic recordings) ---\n${transcript.join("\n")}--- end ---\n`);
  });
});
