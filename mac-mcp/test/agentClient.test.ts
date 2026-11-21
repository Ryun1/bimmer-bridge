import { afterEach, describe, expect, it, vi } from "vitest";
import { AgentClient, type TokenSource } from "../src/agentClient.js";
import { BridgeError, MESSAGES } from "../src/errors.js";
import { loadConfig, type Config } from "../src/config.js";
import type { ExecFileFn, ReadFileFn, NetworkInterfacesFn } from "../src/vmAddress.js";
import {
  AGENT_TOKEN,
  LOOPBACK_BRIDGE,
  NO_BRIDGE,
  VM_BRIDGE,
  closedPort,
  contractHandler,
  healthBody,
  json,
  leaseFile,
  startFakeAgent,
  windowsBody,
  type FakeAgent,
} from "./fixtures.js";

const agents: FakeAgent[] = [];
afterEach(async () => {
  while (agents.length) await agents.pop()!.close();
});
async function fake(...args: Parameters<typeof startFakeAgent>): Promise<FakeAgent> {
  const a = await startFakeAgent(...args);
  agents.push(a);
  return a;
}

function staticTokens(token: string | null = AGENT_TOKEN): TokenSource & { refresh: ReturnType<typeof vi.fn> } {
  const read = async () => {
    if (token === null) throw new BridgeError("no_token", MESSAGES.noToken);
    return token;
  };
  return { get: vi.fn(read), refresh: vi.fn(read) };
}

function config(env: Record<string, string> = {}): Config {
  return loadConfig(env, "/nonexistent-home").config;
}

interface Opts {
  env?: Record<string, string>;
  tokens?: TokenSource;
  fetch?: typeof fetch;
  leases?: string | null;
  arp?: string | null;
  utm?: string | null;
  ifaces?: unknown;
  requestTimeoutMs?: number;
  probeTimeoutMs?: number;
}

function makeClient(o: Opts = {}) {
  const readFile = vi.fn<ReadFileFn>(async () => {
    if (o.leases == null) throw Object.assign(new Error("ENOENT"), { code: "ENOENT" });
    return o.leases;
  });
  const execFile = vi.fn<ExecFileFn>(async (file) => {
    if (file === "/usr/sbin/arp") {
      if (o.arp == null) throw new Error("arp failed");
      return o.arp;
    }
    if (o.utm == null) throw new Error("utmctl: guest agent not running");
    return o.utm;
  });
  const networkInterfaces = vi.fn<NetworkInterfacesFn>(() => (o.ifaces ?? VM_BRIDGE) as never);
  const fetchFn = vi.fn<typeof fetch>(o.fetch ?? ((...a) => fetch(...a)));
  const client = new AgentClient({
    config: config(o.env),
    tokens: o.tokens ?? staticTokens(),
    fetch: fetchFn,
    sources: { readFile, execFile, networkInterfaces },
    requestTimeoutMs: o.requestTimeoutMs,
    probeTimeoutMs: o.probeTimeoutMs,
  });
  return { client, readFile, execFile, networkInterfaces, fetchFn };
}

async function errorOf(p: Promise<unknown>): Promise<BridgeError> {
  const e = await p.then(
    () => {
      throw new Error("expected a rejection");
    },
    (err: unknown) => err,
  );
  expect(e).toBeInstanceOf(BridgeError);
  return e as BridgeError;
}

function calledUrls(f: ReturnType<typeof vi.fn>): string[] {
  return f.mock.calls.map((c) => String(c[0]));
}

function okResponse(body: unknown = healthBody()): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });
}

function connError(code: string): Error {
  return Object.assign(new TypeError("fetch failed"), { cause: Object.assign(new Error(code), { code }) });
}

describe("AgentClient with EDIABAS_AGENT_URL", () => {
  it("returns typed health and windows from the real contract", async () => {
    const a = await fake();
    const { client, readFile, execFile } = makeClient({ env: { EDIABAS_AGENT_URL: a.url } });
    const h = await client.health();
    expect(h.agent).toBe("ediabas-bridge-agent");
    expect(h.conflictingProcesses).toEqual([{ name: "INPALOAD", pid: 4312 }]);
    const w = await client.listWindows();
    expect(w.windows).toHaveLength(2);
    expect(a.requests.every((r) => r.authorization === `Bearer ${AGENT_TOKEN}`)).toBe(true);
    // discovery skipped
    expect(readFile).not.toHaveBeenCalled();
    expect(execFile).not.toHaveBeenCalled();
  });

  it("caches the accepted base URL", async () => {
    const a = await fake();
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url } });
    await client.listWindows();
    await client.listWindows();
    expect(a.requests.map((r) => r.path)).toEqual(["/health", "/windows", "/windows"]);
  });

  it("still requires the exact agent value", async () => {
    const a = await fake((_req, res) => json(res, 200, { ...healthBody(), agent: "ediabas-bridge-agent-imposter" }));
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url } });
    const e = await errorOf(client.listWindows());
    expect(e.code).toBe("wrong_service");
    expect(a.requests.map((r) => r.path)).toEqual(["/health"]);
  });

  it("rejects a 200 from some other service", async () => {
    const a = await fake((_req, res) => {
      res.writeHead(200, { "content-type": "text/html" });
      res.end("<html>router admin</html>");
    });
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url } });
    expect((await errorOf(client.health())).code).toBe("wrong_service");
  });

  it("maps connection refused to 'agent not answering'", async () => {
    const port = await closedPort();
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: `http://127.0.0.1:${port}` } });
    const e = await errorOf(client.health());
    expect(e.code).toBe("agent_not_answering");
    expect(e.message).toBe(MESSAGES.agentNotAnswering);
    expect(e.message).toBe(
      "The VM is up but the agent is not answering. Log in to Windows (the agent starts at log-on), or re-run the installer.",
    );
  });

  it("maps a timeout to 'agent not answering'", async () => {
    const a = await fake(() => {
      /* never respond */
    });
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url }, probeTimeoutMs: 150 });
    expect((await errorOf(client.health())).code).toBe("agent_not_answering");
  });

  it("applies the request timeout to calls after discovery", async () => {
    const a = await fake();
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url }, requestTimeoutMs: 150 });
    await client.health();
    a.setHandler((req, res) => {
      if (req.url === "/health") contractHandler()(req, res);
      // /windows never answers
    });
    expect((await errorOf(client.listWindows())).code).toBe("agent_not_answering");
  });

  it("maps 401 to the token message and re-reads the token exactly once", async () => {
    const a = await fake(contractHandler("the-vm-has-a-different-token"));
    const tokens = staticTokens();
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url }, tokens });
    const e = await errorOf(client.health());
    expect(e.code).toBe("unauthorized");
    expect(e.message).toContain("The agent rejected the token. The Keychain token does not match the VM's.");
    expect(tokens.refresh).toHaveBeenCalledTimes(1);
    expect(e.message).not.toContain(AGENT_TOKEN);
  });

  it("retries with a freshly installed token after a 401", async () => {
    const a = await fake(contractHandler("fresh-token"));
    let current = "stale-token";
    const tokens: TokenSource = {
      get: vi.fn(async () => current),
      refresh: vi.fn(async () => {
        current = "fresh-token";
        return current;
      }),
    };
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url }, tokens });
    const w = await client.listWindows();
    expect(w.windows).toHaveLength(2);
    expect(tokens.refresh).toHaveBeenCalledTimes(1);
  });

  it("maps 403 to the firewall message", async () => {
    const a = await fake((_req, res) => json(res, 403, { error: { code: "forbidden", message: "Caller is not the gateway." } }));
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url } });
    const e = await errorOf(client.health());
    expect(e.code).toBe("forbidden");
    expect(e.message).toBe("The agent refused this Mac's address (firewall or gateway check).");
  });

  it("passes other agent errors through with code and message", async () => {
    const a = await fake();
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url } });
    await client.health();
    a.setHandler((req, res) => {
      if (req.url === "/windows") json(res, 500, { error: { code: "internal_error", message: "Something failed; see the agent log." } });
      else contractHandler()(req, res);
    });
    const e = await errorOf(client.listWindows());
    expect(e.code).toBe("internal_error");
    expect(e.message).toContain("internal_error");
    expect(e.message).toContain("Something failed; see the agent log.");
  });

  it("handles a non-JSON error body", async () => {
    const a = await fake();
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url } });
    await client.health();
    a.setHandler((req, res) => {
      if (req.url === "/windows") {
        res.writeHead(502);
        res.end("Bad gateway");
      } else contractHandler()(req, res);
    });
    const e = await errorOf(client.listWindows());
    expect(e.code).toBe("http_502");
  });

  it("treats a malformed 200 body as an error, not a crash", async () => {
    const a = await fake((req, res) => {
      if (req.url === "/health") json(res, 200, { ...healthBody(), uptimeSeconds: "a while", os: null });
      else json(res, 200, { windows: [{ title: 5 }] });
    });
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url } });
    const e = await errorOf(client.health());
    expect(e.code).toBe("bad_response");
    const e2 = await errorOf(client.listWindows());
    expect(e2.code).toBe("bad_response");
  });

  it("treats invalid JSON in a 200 from the agent as bad_response", async () => {
    const a = await fake();
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url } });
    await client.health();
    a.setHandler((req, res) => {
      if (req.url === "/windows") {
        res.writeHead(200, { "content-type": "application/json" });
        res.end("{not json");
      } else contractHandler()(req, res);
    });
    expect((await errorOf(client.listWindows())).code).toBe("bad_response");
  });

  it("reports no token without contacting anything", async () => {
    const { client, fetchFn } = makeClient({ env: { EDIABAS_AGENT_URL: "http://127.0.0.1:9" }, tokens: staticTokens(null) });
    const e = await errorOf(client.health());
    expect(e.code).toBe("no_token");
    expect(fetchFn).not.toHaveBeenCalled();
  });

  it("reports an invalid EDIABAS_AGENT_URL", async () => {
    const { client, fetchFn } = makeClient({ env: { EDIABAS_AGENT_URL: "ftp://x" } });
    expect((await errorOf(client.health())).code).toBe("config_error");
    expect(fetchFn).not.toHaveBeenCalled();
  });
});

describe("AgentClient discovery", () => {
  it("says the VM is not running and contacts nothing when no bridge interface is up", async () => {
    const { client, fetchFn } = makeClient({
      ifaces: NO_BRIDGE,
      leases: leaseFile({ ip: "192.168.64.3", lease: 0x6b01baf4 }),
      arp: "? (192.168.64.3) at f2:e5:81:1e:82:7 on bridge100 ifscope [bridge]",
    });
    const e = await errorOf(client.health());
    expect(e.code).toBe("vm_not_running");
    expect(e.message).toContain("The VM does not appear to be running. Start the 'Windows' VM in UTM and log in.");
    expect(fetchFn).not.toHaveBeenCalled();
  });

  it("uses the configured VM name in the message", async () => {
    const { client } = makeClient({ ifaces: NO_BRIDGE, env: { EDIABAS_VM_NAME: "Win11" } });
    expect((await errorOf(client.health())).message).toContain("Start the 'Win11' VM in UTM");
  });

  it("never contacts a candidate outside every bridge subnet", async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => okResponse());
    const { client, fetchFn } = makeClient({
      fetch: fetchMock,
      // old lease (192.168.65.3) has the LATER expiry here, so it would be tried first if allowed
      leases: leaseFile({ ip: "192.168.65.3", lease: 0x7f000000 }, { ip: "192.168.64.3", lease: 0x6b01baf4 }),
    });
    await client.health();
    const urls = calledUrls(fetchFn);
    expect(urls.length).toBeGreaterThan(0);
    expect(urls.some((u) => u.includes("192.168.65.3"))).toBe(false);
    expect(urls[0]).toBe("http://192.168.64.3:8765/health");
  });

  it("says the VM is not running when the only candidates are off-link", async () => {
    const { client, fetchFn } = makeClient({
      fetch: vi.fn(async () => okResponse()),
      leases: leaseFile({ ip: "192.168.65.3", lease: 0x7f000000 }),
      arp: "? (10.1.2.3) at f2:e5:81:1e:82:7 on en0 ifscope [ethernet]",
      utm: "172.16.0.9\n",
    });
    expect((await errorOf(client.health())).code).toBe("vm_not_running");
    expect(fetchFn).not.toHaveBeenCalled();
  });

  it("tries leases by latest expiry first", async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => okResponse());
    const { client, fetchFn } = makeClient({
      fetch: fetchMock,
      leases: leaseFile({ ip: "192.168.64.9", lease: 0x60000000 }, { ip: "192.168.64.3", lease: 0x6b01baf4 }),
    });
    await client.health();
    expect(calledUrls(fetchFn)[0]).toBe("http://192.168.64.3:8765/health");
  });

  it("accepts only the exact agent value and moves on to the next candidate", async () => {
    const fetchMock = vi.fn<typeof fetch>(async (url) =>
      String(url).includes("192.168.64.3")
        ? okResponse({ status: "ok", agent: "some-other-agent" })
        : okResponse(),
    );
    const { client, fetchFn } = makeClient({
      fetch: fetchMock,
      leases: leaseFile({ ip: "192.168.64.3", lease: 0x6b01baf4 }, { ip: "192.168.64.4", lease: 0x6b01ba00 }),
    });
    await client.listWindows().catch(() => undefined);
    const urls = calledUrls(fetchFn);
    expect(urls[0]).toBe("http://192.168.64.3:8765/health");
    expect(urls[1]).toBe("http://192.168.64.4:8765/health");
    expect(urls[2]).toBe("http://192.168.64.4:8765/windows");
  });

  it("says the VM is not running when something else answers at the only address", async () => {
    const { client } = makeClient({
      fetch: vi.fn(async () => new Response("<html></html>", { status: 200 })),
      leases: leaseFile({ ip: "192.168.64.3", lease: 0x6b01baf4 }),
    });
    expect((await errorOf(client.health())).code).toBe("vm_not_running");
  });

  it("maps connection refused at an on-link address to 'agent not answering'", async () => {
    const { client } = makeClient({
      fetch: vi.fn(async () => {
        throw connError("ECONNREFUSED");
      }),
      leases: leaseFile({ ip: "192.168.64.3", lease: 0x6b01baf4 }),
    });
    const e = await errorOf(client.health());
    expect(e.code).toBe("agent_not_answering");
  });

  it("maps host unreachable at an on-link address to 'VM not running'", async () => {
    const { client } = makeClient({
      fetch: vi.fn(async () => {
        throw connError("EHOSTDOWN");
      }),
      leases: leaseFile({ ip: "192.168.64.3", lease: 0x6b01baf4 }),
    });
    expect((await errorOf(client.health())).code).toBe("vm_not_running");
  });

  it("prefers the 401 message over other candidate failures", async () => {
    const { client } = makeClient({
      fetch: vi.fn(async (url) =>
        String(url).includes(".3:")
          ? new Response(JSON.stringify({ error: { code: "unauthorized", message: "Missing or invalid bearer token." } }), { status: 401 })
          : Promise.reject(connError("ECONNREFUSED")),
      ),
      leases: leaseFile({ ip: "192.168.64.3", lease: 0x6b01baf4 }, { ip: "192.168.64.4", lease: 0x6b01ba00 }),
    });
    expect((await errorOf(client.health())).code).toBe("unauthorized");
  });

  it("re-reads the token at most once per call across candidates", async () => {
    const tokens = staticTokens();
    const { client } = makeClient({
      tokens,
      fetch: vi.fn(async () => new Response(JSON.stringify({ error: { code: "unauthorized", message: "x" } }), { status: 401 })),
      leases: leaseFile({ ip: "192.168.64.3", lease: 3 }, { ip: "192.168.64.4", lease: 2 }, { ip: "192.168.64.5", lease: 1 }),
    });
    await errorOf(client.health());
    expect(tokens.refresh).toHaveBeenCalledTimes(1);
    await errorOf(client.health());
    expect(tokens.refresh).toHaveBeenCalledTimes(2);
  });

  it("falls back to arp, then utmctl, and ignores utmctl failure", async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => okResponse());
    const viaArp = makeClient({
      fetch: fetchMock,
      leases: null,
      arp: "? (192.168.64.7) at (incomplete) on bridge100 ifscope [bridge]\n? (192.168.64.5) at f2:e5:81:1e:82:7 on bridge100 ifscope [bridge]\n",
    });
    await viaArp.client.health();
    expect(calledUrls(viaArp.fetchFn)[0]).toBe("http://192.168.64.5:8765/health");
    expect(viaArp.execFile.mock.calls.map((c) => c[0])).toEqual(["/usr/sbin/arp"]); // utmctl not needed

    const viaUtm = makeClient({ fetch: vi.fn(async () => okResponse()), leases: null, arp: "", utm: "192.168.64.6\n" });
    await viaUtm.client.health();
    expect(calledUrls(viaUtm.fetchFn)[0]).toBe("http://192.168.64.6:8765/health");
    expect(viaUtm.execFile).toHaveBeenCalledWith(
      "/Applications/UTM.app/Contents/MacOS/utmctl",
      ["ip-address", "Windows"],
      expect.any(Object),
    );

    const nothing = makeClient({ fetch: vi.fn(async () => okResponse()), leases: null, arp: null, utm: null });
    const e = await errorOf(nothing.client.health());
    expect(e.code).toBe("vm_not_running");
    expect(nothing.fetchFn).not.toHaveBeenCalled();
  });

  it("does not run utmctl when a lease candidate is accepted", async () => {
    const { client, execFile } = makeClient({
      fetch: vi.fn(async () => okResponse()),
      leases: leaseFile({ ip: "192.168.64.3", lease: 0x6b01baf4 }),
    });
    await client.health();
    expect(execFile).not.toHaveBeenCalled();
  });

  it("de-duplicates candidates across sources", async () => {
    const { client, fetchFn } = makeClient({
      fetch: vi.fn(async () => {
        throw connError("ECONNREFUSED");
      }),
      leases: leaseFile({ ip: "192.168.64.3", lease: 0x6b01baf4 }),
      arp: "? (192.168.64.3) at f2:e5:81:1e:82:7 on bridge100 ifscope [bridge]",
      utm: "192.168.64.3\n",
    });
    await errorOf(client.health());
    expect(calledUrls(fetchFn)).toEqual(["http://192.168.64.3:8765/health"]);
  });

  it("on a connection failure clears the cache and resolves once more", async () => {
    let vmIp = "192.168.64.3";
    let leases = leaseFile({ ip: vmIp, lease: 0x6b01baf4 });
    const fetchMock = vi.fn<typeof fetch>(async (url) => {
      if (!String(url).includes(`${vmIp}:`)) throw connError("ECONNREFUSED");
      return String(url).endsWith("/windows") ? okResponse(windowsBody()) : okResponse();
    });
    const c = makeClient({ fetch: fetchMock, leases });
    await c.client.listWindows();
    // the VM gets a new address
    vmIp = "192.168.64.4";
    leases = leaseFile({ ip: vmIp, lease: 0x6b01baf5 });
    c.readFile.mockImplementation(async () => leases);
    const w = await c.client.listWindows();
    expect(w.windows).toHaveLength(2);
    expect(calledUrls(c.fetchFn)).toEqual([
      "http://192.168.64.3:8765/health",
      "http://192.168.64.3:8765/windows",
      "http://192.168.64.3:8765/windows", // fails
      "http://192.168.64.4:8765/health",
      "http://192.168.64.4:8765/windows",
    ]);
  });

  it("works end to end against a real local agent treated as on-link", async () => {
    const a = await fake();
    const { client } = makeClient({
      ifaces: LOOPBACK_BRIDGE,
      env: { EDIABAS_AGENT_PORT: String(a.port) },
      leases: leaseFile({ ip: "127.0.0.1", lease: 0x6b01baf4 }),
    });
    const h = await client.health();
    expect(h.version).toBe("0.1.0");
  });

  it("never puts the token in an error message", async () => {
    const msgs: string[] = [];
    const cases: Opts[] = [
      { ifaces: NO_BRIDGE },
      { fetch: vi.fn(async () => new Response(`{"error":{"code":"bad","message":"echo ${AGENT_TOKEN}"}}`, { status: 500 })), env: { EDIABAS_AGENT_URL: "http://127.0.0.1:1" } },
      { fetch: vi.fn(async () => new Response("x", { status: 401 })), env: { EDIABAS_AGENT_URL: "http://127.0.0.1:1" } },
      { fetch: vi.fn(async () => okResponse({ agent: AGENT_TOKEN })), env: { EDIABAS_AGENT_URL: "http://127.0.0.1:1" } },
    ];
    for (const o of cases) msgs.push((await errorOf(makeClient(o).client.health())).message);
    for (const m of msgs) expect(m).not.toContain(AGENT_TOKEN);
  });
});
