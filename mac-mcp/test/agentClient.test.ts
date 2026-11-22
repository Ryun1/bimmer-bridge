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
import { agentErr, jobResult, stubHandler } from "./stubAgent.js";

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
  const logs: string[] = [];
  const client = new AgentClient({
    log: (m) => logs.push(m),
    config: config(o.env),
    tokens: o.tokens ?? staticTokens(),
    fetch: fetchFn,
    sources: { readFile, execFile, networkInterfaces },
    requestTimeoutMs: o.requestTimeoutMs,
    probeTimeoutMs: o.probeTimeoutMs,
  });
  return { client, readFile, execFile, networkInterfaces, fetchFn, logs };
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
    // EDIABAS_AGENT_URL is set, so the token came from EDIABAS_AGENT_TOKEN: the Keychain is not to blame.
    expect(e.message).toContain("The agent at EDIABAS_AGENT_URL rejected the token.");
    expect(e.message).toContain("EDIABAS_AGENT_TOKEN");
    expect(e.message).not.toMatch(/Keychain token|security add-generic-password/);
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

  it("maps unreachable-class errors at an on-link address to 'local_network_blocked'", async () => {
    for (const code of ["EHOSTUNREACH", "EHOSTDOWN", "ENETUNREACH", "ENETDOWN", "EADDRNOTAVAIL"]) {
      const { client, logs } = makeClient({
        fetch: vi.fn(async () => {
          throw connError(code);
        }),
        leases: leaseFile({ ip: "192.168.64.3", lease: 0x6b01baf4 }),
      });
      const e = await errorOf(client.health());
      expect(e.code).toBe("local_network_blocked");
      expect(e.message).toBe(MESSAGES.localNetworkBlocked);
      expect(e.message).toContain("System Settings > Privacy & Security > Local Network");
      expect(logs).toContain(`probe 192.168.64.3:8765 failed: unreachable (${code})`);
    }
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
    const e = await errorOf(client.health());
    expect(e.code).toBe("unauthorized");
    // Discovered VM, no override: the token came from the Keychain (or its env fallback).
    expect(e.message).toContain("The agent rejected the token. The Keychain token does not match the VM's.");
    expect(e.message).not.toContain("EDIABAS_AGENT_URL");
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

describe("local network blocked (macOS Local Network privacy)", () => {
  const LEASES = leaseFile({ ip: "192.168.64.3", lease: 0x6b01baf4 });

  it("never contacts an off-link candidate, and blames the local network for the on-link one", async () => {
    const { client, fetchFn } = makeClient({
      fetch: vi.fn(async () => {
        throw connError("EHOSTUNREACH");
      }),
      leases: leaseFile({ ip: "192.168.65.3", lease: 0x7f000000 }, { ip: "192.168.64.3", lease: 0x6b01baf4 }),
    });
    expect((await errorOf(client.health())).code).toBe("local_network_blocked");
    expect(calledUrls(fetchFn).some((u) => u.includes("192.168.65.3"))).toBe(false);
  });

  it("maps EHOSTUNREACH on a request right after discovery", async () => {
    const { client, logs } = makeClient({
      fetch: vi.fn(async (url) => {
        if (String(url).endsWith("/health")) return okResponse();
        throw connError("EHOSTUNREACH");
      }),
      leases: LEASES,
    });
    expect((await errorOf(client.listWindows())).code).toBe("local_network_blocked");
    expect(logs).toContain("request 192.168.64.3:8765 GET /windows failed: unreachable (EHOSTUNREACH)");
  });

  it("maps EHOSTUNREACH on the cached-address path", async () => {
    let blocked = false;
    const { client, fetchFn } = makeClient({
      fetch: vi.fn(async (url) => {
        if (blocked && String(url).endsWith("/windows")) throw connError("EHOSTUNREACH");
        return String(url).endsWith("/windows") ? okResponse(windowsBody()) : okResponse();
      }),
      leases: LEASES,
    });
    await client.listWindows();
    blocked = true;
    const e = await errorOf(client.listWindows());
    expect(e.code).toBe("local_network_blocked");
    // cached request failed, cache cleared, re-resolved once, retried once
    expect(calledUrls(fetchFn).slice(2)).toEqual([
      "http://192.168.64.3:8765/windows",
      "http://192.168.64.3:8765/health",
      "http://192.168.64.3:8765/windows",
    ]);
  });

  it("maps EHOSTUNREACH when the cached address and the re-probe are both blocked", async () => {
    let blocked = false;
    const { client } = makeClient({
      fetch: vi.fn(async (url) => {
        if (blocked) throw connError("EHOSTUNREACH");
        return String(url).endsWith("/windows") ? okResponse(windowsBody()) : okResponse();
      }),
      leases: LEASES,
    });
    await client.listWindows();
    blocked = true;
    expect((await errorOf(client.listWindows())).code).toBe("local_network_blocked");
  });

  it("still says 'VM not running' with no bridge interface", async () => {
    const { client, fetchFn } = makeClient({ ifaces: NO_BRIDGE, leases: LEASES });
    expect((await errorOf(client.health())).code).toBe("vm_not_running");
    expect(fetchFn).not.toHaveBeenCalled();
  });

  it("still says 'VM not running' for an unreachable explicit URL that is not on a bridge subnet", async () => {
    const { client } = makeClient({
      env: { EDIABAS_AGENT_URL: "http://10.9.9.9:8765" },
      fetch: vi.fn(async () => {
        throw connError("EHOSTUNREACH");
      }),
    });
    expect((await errorOf(client.health())).code).toBe("vm_not_running");
  });

  it("uses local_network_blocked for an unreachable explicit URL on a bridge subnet", async () => {
    const { client } = makeClient({
      env: { EDIABAS_AGENT_URL: "http://192.168.64.3:8765" },
      fetch: vi.fn(async () => {
        throw connError("EHOSTUNREACH");
      }),
    });
    expect((await errorOf(client.health())).code).toBe("local_network_blocked");
  });

  it("keeps timeouts as 'agent not answering'", async () => {
    const { client, logs } = makeClient({
      fetch: vi.fn(async () => {
        throw new DOMException("The operation was aborted due to timeout", "TimeoutError");
      }),
      leases: LEASES,
    });
    expect((await errorOf(client.health())).code).toBe("agent_not_answering");
    expect(logs).toContain("probe 192.168.64.3:8765 failed: timeout (TimeoutError)");
  });

  describe("ranking among candidates", () => {
    const TWO = leaseFile({ ip: "192.168.64.3", lease: 2 }, { ip: "192.168.64.4", lease: 1 });
    const by = (a: () => Promise<Response>, b: () => Promise<Response>) =>
      vi.fn<typeof fetch>(async (url) => (String(url).includes(".3:") ? a() : b()));
    const unreachable = async (): Promise<Response> => {
      throw connError("EHOSTUNREACH");
    };
    const refused = async (): Promise<Response> => {
      throw connError("ECONNREFUSED");
    };
    const status = (n: number) => async () =>
      new Response(JSON.stringify({ error: { code: n === 401 ? "unauthorized" : "forbidden", message: "m" } }), { status: n });

    it("local_network_blocked beats agent_not_answering, in either order", async () => {
      expect((await errorOf(makeClient({ fetch: by(unreachable, refused), leases: TWO }).client.health())).code).toBe("local_network_blocked");
      expect((await errorOf(makeClient({ fetch: by(refused, unreachable), leases: TWO }).client.health())).code).toBe("local_network_blocked");
    });

    it("forbidden and unauthorized beat local_network_blocked", async () => {
      expect((await errorOf(makeClient({ fetch: by(unreachable, status(403)), leases: TWO }).client.health())).code).toBe("forbidden");
      expect((await errorOf(makeClient({ fetch: by(status(401), unreachable), leases: TWO }).client.health())).code).toBe("unauthorized");
    });
  });
});

describe("a request that runs an EDIABAS job is never sent twice", () => {
  const LEASES = leaseFile({ ip: "192.168.64.3", lease: 0x6b01baf4 });
  const JOB = jobResult("NVE_65", "IDENT", [{ A: 1 }]);
  const JOBS = { items: [{ ok: true, result: JOB }] };

  type Call = { name: string; run: (c: AgentClient) => Promise<unknown>; path: string; ok: unknown };
  const CALLS: Call[] = [
    { name: "POST /ediabas/job", run: (c) => c.runJob({ ecu: "NVE_65", job: "IDENT" }), path: "/ediabas/job", ok: JOB },
    { name: "POST /ediabas/jobs", run: (c) => c.runJobs([{ ecu: "NVE_65", job: "IDENT" }]), path: "/ediabas/jobs", ok: JOBS },
    { name: "GET /ediabas/vehicle", run: (c) => c.vehicle(), path: "/ediabas/vehicle", ok: {} },
    { name: "GET /ediabas/describe", run: (c) => c.describeJob({ ecu: "NVE_65", job: "IDENT" }), path: "/ediabas/describe", ok: {} },
  ];

  /** Health and windows answer; `path` fails with `fail()` while `failing` is set. */
  function client(path: string, fail: () => never) {
    const state = { failing: false, sent: 0 };
    const c = makeClient({
      fetch: vi.fn(async (url) => {
        const u = String(url);
        if (u.includes(path)) {
          state.sent++;
          if (state.failing) fail();
        }
        return u.endsWith("/windows") ? okResponse(windowsBody()) : okResponse();
      }),
      leases: LEASES,
    });
    return { ...c, state };
  }

  for (const call of CALLS) {
    for (const code of ["ECONNRESET", "UND_ERR_SOCKET", "EPIPE"]) {
      it(`${call.name}: after ${code} on the cached address it is not re-sent and says it may have run`, async () => {
        const { client: c, state, fetchFn } = client(call.path, () => {
          throw connError(code);
        });
        await c.listWindows(); // address cached
        state.failing = true;
        const e = await errorOf(call.run(c));
        expect(state.sent).toBe(1);
        expect(e.code).toBe("ediabas_request_lost");
        expect(e.message).toBe(MESSAGES.ediabasRequestLost);
        // nothing after the failed request: no re-probe, no second send
        expect(calledUrls(fetchFn).at(-1)).toContain(call.path);
      });
    }

    it(`${call.name}: a timeout on the cached address is not re-sent and says it may have run`, async () => {
      const { client: c, state } = client(call.path, () => {
        throw new DOMException("The operation was aborted due to timeout", "TimeoutError");
      });
      await c.listWindows();
      state.failing = true;
      const e = await errorOf(call.run(c));
      expect(state.sent).toBe(1);
      expect(e.message).toBe(MESSAGES.ediabasRequestLost);
    });

    it(`${call.name}: a reset right after discovery also says it may have run`, async () => {
      const { client: c, state } = client(call.path, () => {
        throw connError("ECONNRESET");
      });
      state.failing = true;
      const e = await errorOf(call.run(c));
      expect(state.sent).toBe(1);
      expect(e.message).toBe(MESSAGES.ediabasRequestLost);
    });
  }

  it("POST /ediabas/job is re-sent once after ECONNREFUSED (the request never left)", async () => {
    let refuse = false;
    const { client: c, fetchFn } = makeClient({
      fetch: vi.fn(async (url) => {
        const u = String(url);
        if (u.endsWith("/ediabas/job") && refuse) {
          refuse = false;
          throw connError("ECONNREFUSED");
        }
        if (u.endsWith("/ediabas/job")) return okResponse(JOB);
        return u.endsWith("/windows") ? okResponse(windowsBody()) : okResponse();
      }),
      leases: LEASES,
    });
    await c.listWindows();
    refuse = true;
    const r = await c.runJob({ ecu: "NVE_65", job: "IDENT" });
    expect(r.job).toBe("IDENT");
    expect(calledUrls(fetchFn).slice(2)).toEqual([
      "http://192.168.64.3:8765/ediabas/job", // refused
      "http://192.168.64.3:8765/health",
      "http://192.168.64.3:8765/ediabas/job",
    ]);
  });

  it("plain GETs that do not touch the cable are still re-sent after a reset", async () => {
    for (const [path, run] of [
      ["/windows", (c: AgentClient) => c.listWindows()],
      ["/logs", (c: AgentClient) => c.listLogs()],
      ["/health", (c: AgentClient) => c.health()],
    ] as const) {
      let reset = false;
      const { client: c, fetchFn } = makeClient({
        fetch: vi.fn(async (url) => {
          const u = String(url);
          if (reset && u.endsWith(path)) {
            reset = false;
            throw connError("ECONNRESET");
          }
          if (u.endsWith("/logs")) return okResponse({ roots: [], files: [], truncated: false });
          return u.endsWith("/windows") ? okResponse(windowsBody()) : okResponse();
        }),
        leases: LEASES,
      });
      await c.listWindows();
      reset = true;
      await run(c).catch(() => undefined); // body shape is not the point here
      expect(calledUrls(fetchFn).slice(2).filter((u) => u.endsWith(path) || u.endsWith("/health")).length).toBeGreaterThanOrEqual(2);
    }
  });
});

describe("stderr diagnostics", () => {
  it("logs the accepted address and its source", async () => {
    const ok = vi.fn(async () => okResponse());
    const viaLeases = makeClient({ fetch: ok, leases: leaseFile({ ip: "192.168.64.3", lease: 1 }) });
    await viaLeases.client.health();
    expect(viaLeases.logs).toContain("discovery accepted 192.168.64.3:8765 (source: leases)");

    const viaArp = makeClient({ fetch: ok, leases: null, arp: "? (192.168.64.5) at f2:e5:81:1e:82:7 on bridge100 ifscope [bridge]" });
    await viaArp.client.health();
    expect(viaArp.logs).toContain("discovery accepted 192.168.64.5:8765 (source: arp)");

    const viaUtm = makeClient({ fetch: ok, leases: null, arp: "", utm: "192.168.64.6\n" });
    await viaUtm.client.health();
    expect(viaUtm.logs).toContain("discovery accepted 192.168.64.6:8765 (source: utmctl)");

    const viaEnv = makeClient({ fetch: ok, env: { EDIABAS_AGENT_URL: "http://127.0.0.1:4567" } });
    await viaEnv.client.health();
    expect(viaEnv.logs).toContain("discovery accepted 127.0.0.1:4567 (source: env)");
  });

  it("logs refused connections and HTTP statuses, never the token", async () => {
    const a = await fake(contractHandler("a-different-token"));
    const http = makeClient({ env: { EDIABAS_AGENT_URL: a.url } });
    await errorOf(http.client.health());
    expect(http.logs).toContain(`probe 127.0.0.1:${a.port} failed: HTTP 401`);

    const port = await closedPort();
    const refused = makeClient({ env: { EDIABAS_AGENT_URL: `http://127.0.0.1:${port}` } });
    await errorOf(refused.client.health());
    expect(refused.logs).toContain(`probe 127.0.0.1:${port} failed: refused (ECONNREFUSED)`);

    const b = await fake();
    const req = makeClient({ env: { EDIABAS_AGENT_URL: b.url } });
    await req.client.health();
    b.setHandler((r, res) => (r.url === "/windows" ? json(res, 500, { error: { code: "internal_error", message: AGENT_TOKEN } }) : contractHandler()(r, res)));
    await errorOf(req.client.listWindows());
    expect(req.logs).toContain(`request 127.0.0.1:${b.port} GET /windows failed: HTTP 500`);

    const wrong = makeClient({ env: { EDIABAS_AGENT_URL: "http://127.0.0.1:1" }, fetch: vi.fn(async () => okResponse({ agent: "x" })) });
    await errorOf(wrong.client.health());
    expect(wrong.logs.join("\n")).toMatch(/probe 127\.0\.0\.1:1 failed: not the agent/);

    for (const l of [...http.logs, ...refused.logs, ...req.logs, ...wrong.logs]) {
      expect(l).not.toContain(AGENT_TOKEN);
      expect(l.toLowerCase()).not.toContain("bearer");
      expect(l.toLowerCase()).not.toContain("authorization");
    }
  });
});

describe("AgentClient, Phase 2/4/5/6 endpoints", () => {
  async function stubbed(overrides: Parameters<typeof stubHandler>[0] = {}) {
    const a = await fake(stubHandler(overrides));
    return { a, ...makeClient({ env: { EDIABAS_AGENT_URL: a.url } }) };
  }

  it("calls every endpoint with the documented method, path, query and body", async () => {
    const { a, client } = await stubbed();
    await client.capture({ window: "INPA - BMW", maxEdge: 800 });
    await client.listLogs({ root: 1, glob: "*.trc" });
    await client.readLog({ path: "C:\\EDIABAS\\TRACE\\api.trc", offset: 10, length: 20, grep: "ERR" });
    await client.listEcus("nve");
    await client.describeJobs({ ecu: "NVE_65", filter: "LESEN" });
    await client.describeJob({ ecu: "NVE_65", job: "FS_LESEN" });
    await client.vehicle();
    await client.runJob({ ecu: "NVE_65", job: "IDENT", args: "1;2" });
    await client.runJobs([{ ecu: "NVE_65", job: "FS_LESEN" }]);
    const seen = a.requests.filter((r) => r.path !== "/health").map((r) => `${r.method} ${r.path}${r.body ? ` ${r.body}` : ""}`);
    expect(seen).toEqual([
      "GET /screen/capture?window=INPA+-+BMW&maxEdge=800",
      "GET /logs?root=1&glob=*.trc",
      "GET /logs/read?path=C%3A%5CEDIABAS%5CTRACE%5Capi.trc&offset=10&length=20&grep=ERR",
      "GET /ediabas/ecus?filter=nve",
      "GET /ediabas/describe?ecu=NVE_65&filter=LESEN",
      "GET /ediabas/describe?ecu=NVE_65&job=FS_LESEN",
      "GET /ediabas/vehicle",
      'POST /ediabas/job {"ecu":"NVE_65","job":"IDENT","args":"1;2"}',
      'POST /ediabas/jobs {"items":[{"ecu":"NVE_65","job":"FS_LESEN"}]}',
    ]);
    expect(a.requests.every((r) => r.authorization === `Bearer ${AGENT_TOKEN}`)).toBe(true);
  });

  it("returns capture bytes and decoded headers", async () => {
    const { client } = await stubbed();
    const c = await client.capture({ window: "INPA" });
    expect(c.mimeType).toBe("image/jpeg");
    expect(Buffer.from(c.data)).toEqual(Buffer.from([0xff, 0xd8, 0xff, 0xe0, 1, 2, 3, 0xff, 0xd9]));
    expect(c).toMatchObject({ title: "INPA - BMW Group ü", handle: "0x000A0B2C", method: "printwindow", width: 1280, height: 800 });
  });

  it("uses 75 s for /ediabas/* and 15 s for everything else", async () => {
    const { client } = await stubbed();
    const spy = vi.spyOn(AbortSignal, "timeout");
    await client.health(); // probe (3 s) + request
    spy.mockClear();
    await client.listWindows();
    await client.capture();
    await client.listLogs();
    expect(spy.mock.calls.map((c) => c[0])).toEqual([15_000, 15_000, 15_000]);
    spy.mockClear();
    await client.listEcus();
    await client.vehicle();
    await client.runJob({ ecu: "NVE_65", job: "IDENT" });
    await client.runJobs([{ ecu: "NVE_65", job: "IDENT" }]);
    expect(spy.mock.calls.map((c) => c[0])).toEqual([75_000, 75_000, 75_000, 75_000]);
  });

  it("tolerates unknown extra fields everywhere", async () => {
    const { client } = await stubbed({
      "POST /ediabas/job": () => ({ status: 200, body: { ...jobResult("NVE_65", "IDENT", [{ A: 1 }]), newField: { x: 1 } } }),
      "GET /health": () => ({ status: 200, body: { ...healthBody(), policyVersion: 1, capabilities: ["ediabas"], futureThing: true } }),
    });
    expect((await client.health()).capabilities).toEqual(["ediabas"]);
    expect((await client.runJob({ ecu: "NVE_65", job: "IDENT" })).sets).toEqual([{ A: 1 }]);
  });

  it("accepts /health from an older agent without the new fields", async () => {
    const { client } = await stubbed({ "GET /health": () => ({ status: 200, body: healthBody() }) });
    const h = await client.health();
    expect(h.policyVersion).toBeUndefined();
    expect(h.capabilities).toBeUndefined();
  });

  it.each([
    ["absent", undefined],
    ["null", null],
  ])("handles error details %s", async (_n, details) => {
    const { client } = await stubbed({ "POST /ediabas/job": () => agentErr(409, "cable_in_use", "busy", details) });
    const e = await errorOf(client.runJob({ ecu: "NVE_65", job: "IDENT" }));
    expect(e.code).toBe("cable_in_use");
    expect(e.message).toMatch(/cable is in use by a BMW tool in the VM\. Ask Ryan to close it/);
  });

  it("tells the gateway 403 apart from the agent's policy 403s", async () => {
    const { client } = await stubbed({
      "POST /ediabas/job": () => agentErr(403, "job_not_allowed", "no", { ecu: "NVE_65", job: "X", rule: "default_deny" }),
      "GET /logs/read": () => agentErr(403, "path_not_allowed", "no", { reason: "UNC path" }),
      "GET /logs": () => agentErr(403, "forbidden", "Caller is not the gateway."),
    });
    const job = await errorOf(client.runJob({ ecu: "NVE_65", job: "X" }));
    expect(job.code).toBe("job_not_allowed");
    expect(job.rule).toBe("default_deny");
    expect((await errorOf(client.readLog({ path: "\\\\x\\y" }))).code).toBe("path_not_allowed");
    const fw = await errorOf(client.listLogs());
    expect(fw.code).toBe("forbidden");
    expect(fw.message).toMatch(/firewall/);
  });

  it("maps 404 not_found to agent_outdated, but keeps ecu_not_found", async () => {
    const { client } = await stubbed({
      "GET /ediabas/describe": () => agentErr(404, "ecu_not_found", "no", { ecu: "NOPE" }),
    });
    // contractHandler is the Phase 1 agent: it has only /health and /windows.
    const a2 = await fake(contractHandler());
    const c2 = makeClient({ env: { EDIABAS_AGENT_URL: a2.url } }).client;
    const old = await errorOf(c2.capture());
    expect(old.code).toBe("agent_outdated");
    expect(old.message).toMatch(/does not know \/screen\/capture.*Update line/);
    for (const p of [() => c2.listEcus(), () => c2.vehicle(), () => c2.runJobs([{ ecu: "A", job: "IDENT" }]), () => c2.listLogs(), () => c2.readLog({ path: "C:\\a" })]) {
      expect((await errorOf(p())).code).toBe("agent_outdated");
    }
    expect((await errorOf(client.describeJobs({ ecu: "NOPE" }))).code).toBe("ecu_not_found");
  });

  it("rejects a malformed job result as bad_response", async () => {
    const { client } = await stubbed({ "POST /ediabas/job": () => ({ status: 200, body: { ecu: "X", sets: "nope" } }) });
    expect((await errorOf(client.runJob({ ecu: "X", job: "IDENT" }))).code).toBe("bad_response");
  });
});
