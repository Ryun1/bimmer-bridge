// NativeClient against a scripted stand-in for `ediabasd serve` (test/fakeEdiabasd.ts). Never
// starts the real engine or opens a device. Every response is synthetic.
import { spawn as nodeSpawn } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { BridgeError } from "../src/errors.js";
import { NativeClient, type SpawnFn } from "../src/nativeClient.js";

const FAKE = fileURLToPath(new URL("./fakeEdiabasd.ts", import.meta.url));

let dir: string;
let logPath: string;
const clients: NativeClient[] = [];

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), "native-client-"));
  logPath = join(dir, "log.jsonl");
});

afterEach(() => {
  for (const c of clients.splice(0)) c.close();
  rmSync(dir, { recursive: true, force: true });
});

interface LogEntry {
  kind: "req" | "ans";
  index: number;
  line?: string;
  argv?: string[];
  dotnetRoot?: string | null;
  pid?: number;
}

function readLog(): LogEntry[] {
  if (!existsSync(logPath)) return [];
  return readFileSync(logPath, "utf8")
    .split("\n")
    .filter((l) => l.trim())
    .map((l) => JSON.parse(l) as LogEntry);
}

/** Runs the fake with Node in place of the binary; the binary's arguments are passed on. */
function fakeSpawn(script: unknown[]): SpawnFn {
  return (_command, args, options) =>
    nodeSpawn(process.execPath, [FAKE, ...args], {
      ...options,
      env: { ...options.env, FAKE_EDIABASD_SCRIPT: JSON.stringify(script), FAKE_EDIABASD_LOG: logPath },
    });
}

function client(script: unknown[], extra: Partial<ConstructorParameters<typeof NativeClient>[0]> = {}): NativeClient {
  const c = new NativeClient({ binary: "/fake/ediabasd", spawn: fakeSpawn(script), log: () => {}, ...extra });
  clients.push(c);
  return c;
}

const describeList = { ecu: "NVE_65", variant: "NVE_65", jobCount: 1, jobs: [{ name: "IDENT", comment: "Identdaten", verdict: { allowed: true, rule: "read", review: "none" } }] };
const ecus = { ecuPath: "/ecu", count: 1, truncated: false, files: [{ name: "NVE_65", type: "prg" }] };
const jobResult = { ecu: "NVE_65", job: "IDENT", variant: "NVE_65", jobStatus: "OKAY", systemResults: null, sets: [{ JOB_STATUS: "OKAY" }] };

async function rejection(p: Promise<unknown>): Promise<BridgeError> {
  try {
    await p;
  } catch (e) {
    expect(e).toBeInstanceOf(BridgeError);
    return e as BridgeError;
  }
  throw new Error("expected a rejection");
}

describe("NativeClient", () => {
  it("sends one JSON line per request and parses the result", async () => {
    const c = client([{ answer: { ok: true, result: describeList } }]);
    const r = await c.describeJobs({ ecu: "NVE_65" });
    expect(r.jobs[0]?.name).toBe("IDENT");
    const reqs = readLog().filter((e) => e.kind === "req");
    expect(reqs).toHaveLength(1);
    expect(JSON.parse(reqs[0]!.line!)).toEqual({ id: 1, method: "describe", params: { ecu: "NVE_65" } });
    expect(reqs[0]!.argv).toEqual(["serve"]);
  });

  it("passes --config when a config path is set", async () => {
    const c = client([{ answer: { ok: true, result: ecus } }], { configPath: "/cfg/ediabasd.json" });
    await c.listEcus();
    expect(readLog()[0]!.argv).toEqual(["serve", "--config", "/cfg/ediabasd.json"]);
  });

  it("an engine error becomes a BridgeError with its code", async () => {
    const c = client([{ answer: { ok: false, error: { code: "cable_not_found", message: "m" } } }]);
    const e = await rejection(c.runJob({ ecu: "NVE_65", job: "IDENT" }));
    expect(e.code).toBe("cable_not_found");
    expect(e.message).toContain("UTM");
  });

  it("two calls at once reach the child one after the other", async () => {
    const c = client([
      { delayMs: 150, answer: { ok: true, result: jobResult } },
      { answer: { ok: true, result: ecus } },
    ]);
    const [job, list] = await Promise.all([c.runJob({ ecu: "NVE_65", job: "IDENT" }), c.listEcus()]);
    expect(job.job).toBe("IDENT");
    expect(list.count).toBe(1);
    expect(readLog().map((e) => `${e.kind}${e.index}`)).toEqual(["req0", "ans0", "req1", "ans1"]);
  });

  it("the child exiting during a job is ediabas_request_lost and is not resent", async () => {
    const c = client([{ exit: 3 }, { answer: { ok: true, result: ecus } }]);
    const e = await rejection(c.runJob({ ecu: "NVE_65", job: "IDENT" }));
    expect(e.code).toBe("ediabas_request_lost");
    expect(e.message).toContain("bridge_health");
    const list = await c.listEcus();
    expect(list.count).toBe(1);
    const reqs = readLog().filter((x) => x.kind === "req");
    expect(reqs.map((r) => (JSON.parse(r.line!) as { method: string }).method)).toEqual(["job", "ecus"]);
    expect(reqs[0]!.pid).not.toBe(reqs[1]!.pid);
  });

  it("the child exiting during a jobs batch is ediabas_request_lost", async () => {
    const c = client([{ exit: 1 }]);
    const e = await rejection(c.runJobs([{ ecu: "NVE_65", job: "IDENT" }]));
    expect(e.code).toBe("ediabas_request_lost");
  });

  it("no answer within the timeout during a job is ediabas_request_lost, and the child is killed", async () => {
    const c = client([{ hang: true }, { answer: { ok: true, result: ecus } }], { requestTimeoutMs: 200 });
    const e = await rejection(c.runJob({ ecu: "NVE_65", job: "IDENT" }));
    expect(e.code).toBe("ediabas_request_lost");
    const hungPid = readLog()[0]!.pid!;
    await new Promise((r) => setTimeout(r, 50));
    expect(alive(hungPid)).toBe(false);
    expect((await c.listEcus()).count).toBe(1);
  });

  it("the child exiting during describe is engine_unavailable", async () => {
    const c = client([{ exit: 3 }]);
    const e = await rejection(c.describeJob({ ecu: "NVE_65", job: "IDENT" }));
    expect(e.code).toBe("engine_unavailable");
  });

  it("the child exiting during health is engine_unavailable", async () => {
    const c = client([{ exit: 78 }]);
    const e = await rejection(c.health());
    expect(e.code).toBe("engine_unavailable");
  });

  it("a missing binary is engine_unavailable with the path in the message", async () => {
    const c = new NativeClient({ binary: join(dir, "no-such-ediabasd"), log: () => {} });
    clients.push(c);
    const e = await rejection(c.runJob({ ecu: "NVE_65", job: "IDENT" }));
    expect(e.code).toBe("engine_unavailable");
    expect(e.message).toContain(join(dir, "no-such-ediabasd"));
  });

  it("an answer that fails its schema is bad_response", async () => {
    const c = client([{ answer: { ok: true, result: { nonsense: true } } }]);
    const e = await rejection(c.listEcus());
    expect(e.code).toBe("bad_response");
  });

  it("a line that is not JSON is skipped while the answer is awaited", async () => {
    const c = client([{ raw: "not json" }], { requestTimeoutMs: 200 });
    const e = await rejection(c.listEcus());
    expect(e.code).toBe("engine_unavailable");
  });

  it("env is passed to the child", async () => {
    const c = client([{ answer: { ok: true, result: ecus } }], { env: { DOTNET_ROOT: "/opt/dotnet-test" } });
    await c.listEcus();
    expect(readLog()[0]!.dotnetRoot).toBe("/opt/dotnet-test");
  });

  it("health parses the engine's health", async () => {
    const health = {
      engine: "ediabasd-net",
      version: "0.1.0",
      kline: "0.1.0",
      ecuPath: "/ecu",
      cable: { state: "free", port: "/dev/cu.usbserial-X", latency: null },
      policyVersion: 4,
      policySha256: "a".repeat(64),
    };
    const c = client([{ answer: { ok: true, result: health } }]);
    const h = await c.health();
    expect(h.cable.state).toBe("free");
    expect(h.policySha256).toBe("a".repeat(64));
  });

  it("kind is native", () => {
    expect(client([]).kind).toBe("native");
  });
});

function alive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}
