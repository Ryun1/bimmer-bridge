// The tool set and behaviour of each backend, through buildServer with stub backends. Nothing
// here starts ediabasd or reaches a VM. Every response is synthetic.
import { spawn as nodeSpawn } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { describe, expect, it } from "vitest";
import type { JobResult, JobsResponse } from "../src/agentClient.js";
import type { EdiabasBackend, JobRequest } from "../src/backend.js";
import { BridgeError } from "../src/errors.js";
import { loadConfig } from "../src/config.js";
import type { NativeHealth, SpawnFn } from "../src/nativeClient.js";
import { PolicyGuard } from "../src/policy.js";
import { NATIVE_INSTRUCTIONS, buildServer, createAppServer, type ServerParts } from "../src/server.js";
import { jobResult, phase2Health } from "./stubAgent.js";

const policy = PolicyGuard.load();

function nativeHealth(o: Partial<NativeHealth> = {}, cable: Partial<NativeHealth["cable"]> = {}): NativeHealth {
  return {
    engine: "ediabasd-net",
    version: "0.1.0",
    kline: "0.1.0",
    ecuPath: "/Users/test/ediabas-ecu/Ecu",
    cable: { state: "free", port: "/dev/cu.usbserial-X", latency: "1ms", ...cable },
    policyVersion: policy.version,
    policySha256: policy.sha256,
    ...o,
  };
}

/** A backend that answers every item OKAY, or as `answer` says, and records each runJobs call. */
class StubBackend implements EdiabasBackend {
  readonly kind: "vm" | "native";
  constructor(kind: "vm" | "native" = "native") {
    this.kind = kind;
  }
  readonly batches: JobRequest[][] = [];
  readonly jobs: string[] = [];
  answer: (items: JobRequest[], call: number) => JobsResponse = (items) => ({
    items: items.map((i) => ({ ok: true, result: jobResult(i.ecu, i.job, [{ STAT_WERT: 12.4 }]) as JobResult })),
  });

  async listEcus() {
    return { ecuPath: "/ecu", count: 1, truncated: false, files: [{ name: "NVE_65", type: "prg" }] };
  }
  async describeJobs(q: { ecu: string }) {
    return { ecu: q.ecu, variant: q.ecu, jobCount: 0, jobs: [] };
  }
  async describeJob(q: { ecu: string; job: string }) {
    return { ecu: q.ecu, job: q.job, verdict: { allowed: true, rule: "read", review: "none" }, arguments: [], results: [] };
  }
  async runJob(body: { ecu: string; job: string }) {
    this.jobs.push(`${body.ecu} ${body.job}`);
    return jobResult(body.ecu, body.job, [{ STAT_WERT: 1 }]) as JobResult;
  }
  async runJobs(items: JobRequest[]) {
    this.batches.push(items.map((i) => ({ ecu: i.ecu, job: i.job })));
    return this.answer(items, this.batches.length);
  }
}

async function connect(parts: Partial<ServerParts> & Pick<ServerParts, "ediabas">) {
  const server = buildServer({ policy, audit: { record: async () => {} }, redact: (t) => t, log: () => {}, ...parts });
  const [clientT, serverT] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: "test", version: "0.0.0" });
  await server.connect(serverT);
  await client.connect(clientT);
  const call = async (name: string, args: Record<string, unknown> = {}) => {
    const r = (await client.callTool({ name, arguments: args })) as { isError?: boolean; content: { type: string; text?: string }[] };
    return { ...r, text: r.content.map((c) => c.text ?? "").join("\n") };
  };
  return { client, call, instructions: client.getInstructions() };
}

const EDIABAS_TOOLS = ["ediabas_describe", "ediabas_job", "ediabas_jobs", "ediabas_list_ecus", "ediabas_read_faults", "ediabas_vehicle_status"];

describe("tool set by backend", () => {
  it("native registers bridge_health and the ediabas tools, no vm tools", async () => {
    const { client, instructions } = await connect({ ediabas: new StubBackend(), nativeHealth: async () => nativeHealth() });
    const names = (await client.listTools()).tools.map((t) => t.name).sort();
    expect(names).toEqual(["bridge_health", ...EDIABAS_TOOLS].sort());
    expect(instructions).toBe(NATIVE_INSTRUCTIONS);
    expect(instructions).toContain("bridge_health");
    expect(instructions).not.toContain("vm_");
  });

  it("vm registers today's tools and no bridge_health", async () => {
    const vm = new StubBackend("vm");
    const client = { ...stubVmClient(), ...bind(vm) };
    const { client: mcp } = await connect({ ediabas: vm, client });
    const names = (await mcp.listTools()).tools.map((t) => t.name).sort();
    expect(names).toEqual([...EDIABAS_TOOLS, "vm_health", "vm_list_windows", "vm_logs_list", "vm_logs_read", "vm_screenshot"].sort());
  });

  it("no tool description on native says the VM", async () => {
    const { client } = await connect({ ediabas: new StubBackend(), nativeHealth: async () => nativeHealth() });
    for (const t of (await client.listTools()).tools) expect(t.description ?? "").not.toMatch(/\bVM\b/);
  });
});

describe("bridge_health", () => {
  it("shows engine, version, ecuPath, cable state and latency, and policy version", async () => {
    const { call } = await connect({ ediabas: new StubBackend(), nativeHealth: async () => nativeHealth() });
    const r = await call("bridge_health");
    expect(r.isError).toBeFalsy();
    expect(r.text).toContain("ediabasd-net 0.1.0");
    expect(r.text).toContain("/Users/test/ediabas-ecu/Ecu");
    expect(r.text).toContain("cable: free (/dev/cu.usbserial-X, latency 1ms)");
    expect(r.text).toContain(`version ${policy.version}`);
  });

  it("on a cable passed to the VM says absent and how to fix it", async () => {
    const { call } = await connect({ ediabas: new StubBackend(), nativeHealth: async () => nativeHealth({}, { state: "absent", port: null, latency: null }) });
    const r = await call("bridge_health");
    expect(r.text).toContain("cable: absent");
    expect(r.text).toMatch(/not on this Mac/);
    expect(r.text).toMatch(/untick it in UTM's USB menu/);
  });

  it("an engine that will not start is its error", async () => {
    const { call } = await connect({
      ediabas: new StubBackend(),
      nativeHealth: async () => {
        throw new BridgeError("engine_unavailable", "no engine");
      },
    });
    const r = await call("bridge_health");
    expect(r.isError).toBe(true);
    expect(r.text).toMatch(/^\[engine_unavailable\]/);
  });
});

describe("native preflight", () => {
  it("a different policy SHA refuses with policy_mismatch and sends nothing", async () => {
    const backend = new StubBackend();
    const { call } = await connect({ ediabas: backend, nativeHealth: async () => nativeHealth({ policySha256: "0".repeat(64) }) });
    const r = await call("ediabas_job", { ecu: "NVE_65", job: "IDENT" });
    expect(r.text).toMatch(/^\[policy_mismatch\]/);
    expect(backend.jobs).toEqual([]);
  });

  it("a matching policy runs the job", async () => {
    const backend = new StubBackend();
    const { call } = await connect({ ediabas: backend, nativeHealth: async () => nativeHealth() });
    const r = await call("ediabas_job", { ecu: "NVE_65", job: "IDENT" });
    expect(r.isError).toBeFalsy();
    expect(backend.jobs).toEqual(["NVE_65 IDENT"]);
  });
});

describe("vehicle status", () => {
  const sources = [{ ecu: "NVE_65", job: "STATUS_BORDNETZSPANNUNG" }];

  it("native sends voltage sources in a second request", async () => {
    const backend = new StubBackend();
    backend.answer = (items, n) => {
      if (n === 2) throw new BridgeError("frame_refused", "refused");
      return { items: items.map((i) => ({ ok: true, result: jobResult(i.ecu, i.job, [{ STAT_WERT: 12.4 }]) as JobResult })) };
    };
    const { call } = await connect({ ediabas: backend, nativeHealth: async () => nativeHealth(), voltageSources: sources });
    const r = await call("ediabas_vehicle_status");
    expect(backend.batches).toEqual([
      [
        { ecu: "UTILITY", job: "STATUS_ZUENDUNG" },
        { ecu: "UTILITY", job: "STATUS_UBATT" },
        { ecu: "TMODE", job: "LESE_SPANNUNG_KL30" },
        { ecu: "TMODE", job: "LESE_SPANNUNG_KL15" },
      ],
      sources,
    ]);
    expect(r.isError).toBeFalsy();
    expect(r.text).toMatch(/Ignition \(UTILITY STATUS_ZUENDUNG\): job status OKAY/);
    expect(r.text).toMatch(/Battery \(UTILITY STATUS_UBATT\): job status OKAY/);
    expect(r.text).toMatch(/Voltage \(NVE_65 STATUS_BORDNETZSPANNUNG\): error: \[frame_refused\]/);
    expect(r.text).toMatch(/Cable KL15 \(TMODE LESE_SPANNUNG_KL15\): job status OKAY/);
  });

  it("native with no sources sends one request", async () => {
    const backend = new StubBackend();
    const { call } = await connect({ ediabas: backend, nativeHealth: async () => nativeHealth() });
    await call("ediabas_vehicle_status");
    expect(backend.batches).toHaveLength(1);
  });

  it("vm vehicle status is unchanged: one request", async () => {
    const vm = new StubBackend("vm");
    const { call } = await connect({ ediabas: vm, client: { ...stubVmClient(), ...bind(vm) }, voltageSources: sources });
    const r = await call("ediabas_vehicle_status");
    expect(r.isError).toBeFalsy();
    expect(vm.batches).toEqual([
      [
        { ecu: "UTILITY", job: "STATUS_ZUENDUNG" },
        { ecu: "UTILITY", job: "STATUS_UBATT" },
        ...sources,
        { ecu: "TMODE", job: "LESE_SPANNUNG_KL30" },
        { ecu: "TMODE", job: "LESE_SPANNUNG_KL15" },
      ],
    ]);
  });
});

/** The VM client's non-EDIABAS calls, for a vm-backend server. */
function stubVmClient() {
  return {
    health: async () => phase2Health() as never,
    listWindows: async () => ({ windows: [] }),
    capture: async () => {
      throw new Error("no capture in this test");
    },
    listLogs: async () => ({ roots: [], files: [] }) as never,
    readLog: async () => {
      throw new Error("no logs in this test");
    },
  };
}

function bind(b: StubBackend) {
  return {
    listEcus: b.listEcus.bind(b),
    describeJobs: b.describeJobs.bind(b),
    describeJob: b.describeJob.bind(b),
    runJob: b.runJob.bind(b),
    runJobs: b.runJobs.bind(b),
  };
}

describe("createAppServer", () => {
  const FAKE = fileURLToPath(new URL("./fakeEdiabasd.ts", import.meta.url));

  async function app(env: Record<string, string>, local: Record<string, unknown>, script: unknown[]) {
    const dir = mkdtempSync(join(tmpdir(), "native-app-"));
    const logPath = join(dir, "log.jsonl");
    const localPath = join(dir, "config.local.json");
    writeFileSync(localPath, JSON.stringify(local));
    const { config } = loadConfig({ EDIABAS_AUDIT_LOG: join(dir, "audit.log"), ...env }, dir, localPath);
    const spawnEdiabasd: SpawnFn = (_c, args, options) =>
      nodeSpawn(process.execPath, [FAKE, ...args], {
        ...options,
        env: { ...options.env, FAKE_EDIABASD_SCRIPT: JSON.stringify(script), FAKE_EDIABASD_LOG: logPath },
      });
    const server = createAppServer(config, {
      execFile: async () => {
        throw new Error("no commands in this test");
      },
      readFile: async () => "",
      networkInterfaces: () => ({}) as never,
      log: () => {},
      spawnEdiabasd,
    });
    const [clientT, serverT] = InMemoryTransport.createLinkedPair();
    const client = new Client({ name: "test", version: "0.0.0" });
    await server.connect(serverT);
    await client.connect(clientT);
    const call = async (name: string) => {
      const r = (await client.callTool({ name, arguments: {} })) as { isError?: boolean; content: { text?: string }[] };
      return { ...r, text: r.content.map((c) => c.text ?? "").join("\n") };
    };
    const log = () =>
      readFileSync(logPath, "utf8")
        .split("\n")
        .filter((l) => l.trim())
        .map((l) => JSON.parse(l) as { argv: string[]; dotnetRoot: string | null });
    return { client, call, log, close: () => rmSync(dir, { recursive: true, force: true }) };
  }

  it("EDIABAS_BACKEND=native starts ediabasd with its config and DOTNET_ROOT, and bridge_health answers", async () => {
    const a = await app({ EDIABAS_BACKEND: "native" }, { ediabasdConfig: "/m2/e.json", dotnetRoot: "/opt/dotnet10" }, [
      { answer: { ok: true, result: nativeHealth() } },
    ]);
    try {
      const names = (await a.client.listTools()).tools.map((t) => t.name);
      expect(names).toContain("bridge_health");
      expect(names).not.toContain("vm_health");
      const r = await a.call("bridge_health");
      expect(r.text).toContain("cable: free");
      expect(a.log()[0]).toMatchObject({ argv: ["serve", "--config", "/m2/e.json"], dotnetRoot: "/opt/dotnet10" });
    } finally {
      a.close();
    }
  });

  it("an unknown backend keeps the VM tools, and every ediabas tool says config_error before sending anything", async () => {
    const a = await app({ EDIABAS_BACKEND: "wine", EDIABAS_AGENT_URL: "http://127.0.0.1:9", EDIABAS_AGENT_TOKEN: "t" }, {}, []);
    try {
      const names = (await a.client.listTools()).tools.map((t) => t.name);
      expect(names).toContain("vm_health");
      const r = await a.call("ediabas_list_ecus");
      expect(r.text).toMatch(/^\[config_error\] EDIABAS_BACKEND is "wine"/);
    } finally {
      a.close();
    }
  });
});
