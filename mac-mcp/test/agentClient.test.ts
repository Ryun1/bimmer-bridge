import { createHash } from "node:crypto";
import { afterEach, describe, expect, it, vi } from "vitest";
import { AgentClient, helloProof as clientHelloProof, type TokenSource } from "../src/agentClient.js";
import { BridgeError, MESSAGES } from "../src/errors.js";
import { loadConfig, type Config } from "../src/config.js";
import type { ExecFileFn, ReadFileFn, NetworkInterfacesFn } from "../src/vmAddress.js";
import {
  AGENT_TOKEN,
  LOOPBACK_BRIDGE,
  NO_BRIDGE,
  TEST_VM_MAC,
  VM_BRIDGE,
  answerHello,
  closedPort,
  contractHandler,
  healthBody,
  helloProof,
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

function sha256(s: string): Buffer {
  return createHash("sha256").update(s, "utf8").digest();
}

function staticTokens(token: string | null = AGENT_TOKEN): TokenSource & { refresh: ReturnType<typeof vi.fn> } {
  const read = async () => {
    if (token === null) throw new BridgeError("no_token", MESSAGES.noToken);
    return token;
  };
  return { get: vi.fn(read), refresh: vi.fn(read), helloKey: async () => sha256(await read()) };
}

/** A token source whose value can change (a token installed while the server runs). */
function changingTokens(initial: string, next: string): TokenSource & { refresh: ReturnType<typeof vi.fn> } {
  let current = initial;
  return {
    get: vi.fn(async () => current),
    refresh: vi.fn(async () => {
      current = next;
      return current;
    }),
    helloKey: async () => sha256(current),
  };
}

function config(env: Record<string, string> = {}): Config {
  // The VM MAC comes from the environment so discovery never runs plutil (see vmMac tests below).
  return loadConfig({ EDIABAS_VM_MAC: TEST_VM_MAC, ...env }, "/nonexistent-home").config;
}

interface Opts {
  env?: Record<string, string>;
  tokens?: TokenSource;
  fetch?: typeof fetch;
  leases?: string | null;
  arp?: string | null;
  utm?: string | null;
  /** `plutil -convert json` output for UTM's config.plist; null: plutil fails (no such file). */
  plutil?: string | null;
  ifaces?: unknown;
  requestTimeoutMs?: number;
  probeTimeoutMs?: number;
  ediabasTimeoutMs?: number;
  ediabasQueueWaitMs?: number;
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
    if (file === "/usr/bin/plutil") {
      if (o.plutil == null) throw new Error("plutil: file does not exist");
      return o.plutil;
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
    ediabasTimeoutMs: o.ediabasTimeoutMs,
    ediabasQueueWaitMs: o.ediabasQueueWaitMs,
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

/** URLs fetched, with the random /hello nonce dropped. */
function calledUrls(f: ReturnType<typeof vi.fn>): string[] {
  return f.mock.calls.map((c) => String(c[0]).replace(/\/hello\?nonce=[0-9a-f]{32}$/, "/hello"));
}

/** The agent's answer at `url`: its /hello proof for `token`, else 200 with `body`. */
function agentResponse(url: string, body: unknown = healthBody(), token = AGENT_TOKEN): Response {
  const u = new URL(url);
  if (u.pathname === "/hello") return okResponse({ agent: "ediabas-bridge-agent", proof: helloProof(token, u.searchParams.get("nonce") ?? "") });
  return okResponse(body);
}

/** A fetch that answers GET /hello like the agent holding `token`, and passes the rest to `inner`. */
function withHello(inner: (url: string, init?: RequestInit) => Promise<Response>, token = AGENT_TOKEN): typeof fetch {
  return async (input, init) => (new URL(String(input)).pathname === "/hello" ? agentResponse(String(input), undefined, token) : inner(String(input), init));
}

/** The Authorization header of every fetch the client made, in order. */
function authHeaders(f: ReturnType<typeof vi.fn>): (string | undefined)[] {
  return f.mock.calls.map((c) => (c[1] as RequestInit | undefined)?.headers as Record<string, string> | undefined).map((h) => h?.authorization);
}

function okResponse(body: unknown = healthBody()): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });
}

function connError(code: string, syscall?: string): Error {
  return Object.assign(new TypeError("fetch failed"), { cause: Object.assign(new Error(code), { code, ...(syscall ? { syscall } : {}) }) });
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
    // /hello goes without the token; everything after it carries it
    expect(a.requests[0]).toMatchObject({ authorization: undefined });
    expect(a.requests.slice(1).every((r) => r.authorization === `Bearer ${AGENT_TOKEN}`)).toBe(true);
    // discovery skipped
    expect(readFile).not.toHaveBeenCalled();
    expect(execFile).not.toHaveBeenCalled();
  });

  it("caches the accepted base URL", async () => {
    const a = await fake();
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url } });
    await client.listWindows();
    await client.listWindows();
    expect(a.requests.map((r) => r.path.split("?")[0])).toEqual(["/hello", "/windows", "/windows"]);
  });

  it("still requires the exact agent value", async () => {
    const a = await fake((_req, res) => json(res, 200, { ...healthBody(), agent: "ediabas-bridge-agent-imposter" }));
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url } });
    const e = await errorOf(client.listWindows());
    expect(e.code).toBe("wrong_service");
    expect(a.requests.map((r) => r.path.split("?")[0])).toEqual(["/hello"]);
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
    // Proved with this Mac's token at /hello, then the VM's token changed (re-installed).
    const a = await fake((req, res) => (answerHello(req, res, AGENT_TOKEN) ? undefined : contractHandler("the-vm-has-a-different-token")(req, res)));
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
    // /hello still proves the old token; the VM's token changed right after.
    const a = await fake((req, res) => (answerHello(req, res, "stale-token") ? undefined : contractHandler("fresh-token")(req, res)));
    const tokens = changingTokens("stale-token", "fresh-token");
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

  it("a non-JSON error body is not the agent's: wrong_service", async () => {
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
    expect(e.code).toBe("wrong_service");
  });

  it("treats a malformed 200 body as an error, not a crash", async () => {
    const a = await fake((req, res) => {
      if (answerHello(req, res, AGENT_TOKEN)) return;
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

describe("identity before token (D5)", () => {
  it("the client's proof matches the documented vector", () => {
    const vector = "496e963110d8963be46b16878c70fbd5003960c525362880c4f7514a06a608bd";
    expect(clientHelloProof(sha256("test-token"), "00112233445566778899aabbccddeeff")).toBe(vector);
    expect(helloProof("test-token", "00112233445566778899aabbccddeeff")).toBe(vector);
  });

  it("probe: sends GET /hello with a fresh 32-hex nonce and no Authorization, then the authenticated request", async () => {
    const a = await fake();
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url } });
    await client.listWindows();
    await errorOf(makeClient({ env: { EDIABAS_AGENT_URL: a.url } }).client.capture()); // a second probe: another nonce
    const hellos = a.requests.filter((r) => r.path.startsWith("/hello"));
    expect(hellos).toHaveLength(2);
    for (const h of hellos) {
      expect(h.method).toBe("GET");
      expect(h.path).toMatch(/^\/hello\?nonce=[0-9a-f]{32}$/);
      expect(h.authorization).toBeUndefined();
    }
    expect(hellos[0]!.path).not.toBe(hellos[1]!.path);
    expect(a.requests[1]).toMatchObject({ path: "/windows", authorization: `Bearer ${AGENT_TOKEN}` });
  });

  it("probe: an impostor answering /hello with a wrong proof never receives an Authorization header", async () => {
    const a = await fake((req, res) => {
      if (req.url?.startsWith("/hello")) json(res, 200, { agent: "ediabas-bridge-agent", proof: "0".repeat(64) });
      else json(res, 200, req.url === "/windows" ? windowsBody() : healthBody());
    });
    const tokens = staticTokens();
    const { client, fetchFn, logs } = makeClient({ env: { EDIABAS_AGENT_URL: a.url }, tokens });
    const e = await errorOf(client.listWindows());
    expect(e.code).toBe("wrong_service");
    expect(e.message).toMatch(/could not prove it holds this Mac's token/);
    expect(e.message).not.toContain(AGENT_TOKEN);
    // the token is re-read once in case it was just installed, then the check fails again
    expect(tokens.refresh).toHaveBeenCalledTimes(1);
    expect(a.requests.map((r) => r.path.split("?")[0])).toEqual(["/hello"]);
    expect(a.requests.every((r) => r.authorization === undefined)).toBe(true);
    expect(authHeaders(fetchFn).every((h) => h === undefined)).toBe(true);
    expect(logs).toContain(`probe 127.0.0.1:${a.port} failed: the proof does not match this Mac's token`);
  });

  it("probe: a proof in the wrong format (not 64 lowercase hex) is wrong_service", async () => {
    for (const proof of ["", "0".repeat(63), "A".repeat(64), 42, null]) {
      const a = await fake((req, res) => json(res, 200, { agent: "ediabas-bridge-agent", proof }));
      const e = await errorOf(makeClient({ env: { EDIABAS_AGENT_URL: a.url } }).client.health());
      expect(e.code).toBe("wrong_service");
      expect(a.requests.every((r) => r.authorization === undefined)).toBe(true);
    }
  });

  it("probe: an agent with another token is wrong_service, and is never sent this Mac's token", async () => {
    const a = await fake(contractHandler("the-vm-has-a-different-token"));
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url } });
    const e = await errorOf(client.health());
    expect(e.code).toBe("wrong_service");
    expect(e.message).toContain("EDIABAS_AGENT_TOKEN"); // the override's fix
    expect(a.requests.every((r) => r.authorization === undefined)).toBe(true);
  });

  it("probe: a token installed while the server runs is picked up by the proof check", async () => {
    const a = await fake(contractHandler("fresh-token"));
    const tokens = changingTokens("stale-token", "fresh-token");
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url }, tokens });
    expect((await client.listWindows()).windows).toHaveLength(2);
    expect(tokens.refresh).toHaveBeenCalledTimes(1);
    expect(a.requests.map((r) => r.authorization)).toEqual([undefined, "Bearer fresh-token"]);
  });

  it("probe: an impostor answering 404 to /hello -> wrong_service, no Authorization sent", async () => {
    const a = await fake((_req, res) => {
      res.writeHead(404, { "content-type": "text/html" });
      res.end("<html>Not Found</html>");
    });
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url } });
    expect((await errorOf(client.listWindows())).code).toBe("wrong_service");
    expect(a.requests).toHaveLength(1);
    expect(a.requests.every((r) => r.authorization === undefined)).toBe(true);
  });

  it("probe: an agent older than /hello (401 in the agent's shape) is agent_outdated, no Authorization sent", async () => {
    const old = (req: import("node:http").IncomingMessage, res: import("node:http").ServerResponse) => {
      if (req.headers.authorization !== `Bearer ${AGENT_TOKEN}`) {
        json(res, 401, { error: { code: "unauthorized", message: "Missing or invalid bearer token." } }, { "www-authenticate": "Bearer" });
      } else json(res, 200, healthBody());
    };
    const a = await fake(old);
    const tokens = staticTokens();
    const e = await errorOf(makeClient({ env: { EDIABAS_AGENT_URL: a.url }, tokens }).client.health());
    expect(e.code).toBe("agent_outdated");
    expect(e.message).toContain("/hello");
    expect(a.requests).toHaveLength(1);
    expect(a.requests[0]!.authorization).toBeUndefined();
    expect(tokens.refresh).not.toHaveBeenCalled();
  });

  it("probe: the gateway's 403 at /hello is still forbidden", async () => {
    const a = await fake((_req, res) => json(res, 403, { error: { code: "forbidden", message: "Caller is not the gateway." } }));
    expect((await errorOf(makeClient({ env: { EDIABAS_AGENT_URL: a.url } }).client.health())).code).toBe("forbidden");
  });

  describe("a cached address that stops being the agent (F7)", () => {
    /** Discovery via a lease to a local fake agent, which then gets replaced by `impostor`. */
    async function cachedThenReplaced(impostor: Parameters<FakeAgent["setHandler"]>[0]) {
      const a = await fake(stubHandler());
      const c = makeClient({ ifaces: LOOPBACK_BRIDGE, env: { EDIABAS_AGENT_PORT: String(a.port) }, leases: leaseFile({ ip: "127.0.0.1", lease: 0x6b01baf4 }) });
      await c.client.listWindows();
      expect(c.readFile).toHaveBeenCalledTimes(1);
      a.setHandler(impostor);
      a.requests.length = 0;
      return { a, ...c };
    }

    it("cached address now answers 404 without the agent's error shape -> wrong_service, cache invalidated, next call rediscovers", async () => {
      const { a, client, readFile } = await cachedThenReplaced((_req, res) => {
        res.writeHead(404, { "content-type": "text/plain" });
        res.end("not found");
      });
      expect((await errorOf(client.listWindows())).code).toBe("wrong_service");
      const second = await errorOf(client.listWindows());
      expect(readFile).toHaveBeenCalledTimes(2); // discovery ran again
      expect(second.code).toBe("vm_not_running"); // the only candidate is not the agent
      expect(a.requests.map((r) => r.path.split("?")[0])).toEqual(["/windows", "/hello"]);
    });

    it("cached address answers 200 with JSON that fails the schema -> bad_response, cache invalidated", async () => {
      const { a, client, readFile } = await cachedThenReplaced((_req, res) => json(res, 200, { hello: "from another service" }));
      expect((await errorOf(client.listWindows())).code).toBe("bad_response");
      await errorOf(client.listWindows());
      expect(readFile).toHaveBeenCalledTimes(2);
      expect(a.requests.map((r) => r.path.split("?")[0])).toEqual(["/windows", "/hello"]);
    });

    it("the second call to an impostor at the cached address sends no Authorization", async () => {
      for (const impostor of [
        (_req: import("node:http").IncomingMessage, res: import("node:http").ServerResponse) => {
          res.writeHead(404);
          res.end();
        },
        (_req: import("node:http").IncomingMessage, res: import("node:http").ServerResponse) => json(res, 200, { status: "ok" }),
      ]) {
        const { a, client } = await cachedThenReplaced(impostor);
        await errorOf(client.listWindows()); // still cached: this one carried the token
        const firstCall = a.requests.length;
        await errorOf(client.listWindows());
        await errorOf(client.health());
        const later = a.requests.slice(firstCall);
        expect(later.length).toBeGreaterThan(0);
        expect(later.every((r) => r.authorization === undefined && r.path.startsWith("/hello"))).toBe(true);
      }
    });

    it("an agent error in its own shape keeps the cache", async () => {
      const { client, readFile } = await cachedThenReplaced(
        stubHandler({ "GET /windows": () => agentErr(500, "internal_error", "Something failed; see the agent log.") }),
      );
      expect((await errorOf(client.listWindows())).code).toBe("internal_error");
      await errorOf(client.listWindows());
      expect(readFile).toHaveBeenCalledTimes(1);
    });

    it("a 401 that is not in the agent's shape is not answered with a re-read token", async () => {
      const tokens = staticTokens();
      const a = await fake();
      const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url }, tokens });
      await client.listWindows();
      a.setHandler((_req, res) => {
        res.writeHead(401);
        res.end("who are you");
      });
      expect((await errorOf(client.listWindows())).code).toBe("wrong_service");
      expect(tokens.refresh).not.toHaveBeenCalled();
    });
  });
});

describe("AgentClient discovery", () => {
  it("says the VM is not running and contacts nothing when no bridge interface is up", async () => {
    const { client, fetchFn } = makeClient({
      ifaces: NO_BRIDGE,
      leases: leaseFile({ ip: "192.168.64.3", lease: 0x6b01baf4 }),
      arp: "? (192.168.64.3) at 2:0:0:0:0:1 on bridge100 ifscope [bridge]",
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
    const fetchMock = vi.fn<typeof fetch>(withHello(async () => okResponse()));
    const { client, fetchFn } = makeClient({
      fetch: fetchMock,
      // old lease (192.168.65.3) has the LATER expiry here, so it would be tried first if allowed
      leases: leaseFile({ ip: "192.168.65.3", lease: 0x7f000000 }, { ip: "192.168.64.3", lease: 0x6b01baf4 }),
    });
    await client.health();
    const urls = calledUrls(fetchFn);
    expect(urls.length).toBeGreaterThan(0);
    expect(urls.some((u) => u.includes("192.168.65.3"))).toBe(false);
    expect(urls[0]).toBe("http://192.168.64.3:8765/hello");
  });

  it("says the VM is not running when the only candidates are off-link", async () => {
    const { client, fetchFn } = makeClient({
      fetch: vi.fn(withHello(async () => okResponse())),
      leases: leaseFile({ ip: "192.168.65.3", lease: 0x7f000000 }),
      arp: "? (10.1.2.3) at 2:0:0:0:0:1 on en0 ifscope [ethernet]",
      utm: "172.16.0.9\n",
    });
    expect((await errorOf(client.health())).code).toBe("vm_not_running");
    expect(fetchFn).not.toHaveBeenCalled();
  });

  it("tries leases by latest expiry first", async () => {
    const fetchMock = vi.fn<typeof fetch>(withHello(async () => okResponse()));
    const { client, fetchFn } = makeClient({
      fetch: fetchMock,
      leases: leaseFile({ ip: "192.168.64.9", lease: 0x60000000 }, { ip: "192.168.64.3", lease: 0x6b01baf4 }),
    });
    await client.health();
    expect(calledUrls(fetchFn)[0]).toBe("http://192.168.64.3:8765/hello");
  });

  it("accepts only the exact agent value and moves on to the next candidate", async () => {
    const fetchMock = vi.fn<typeof fetch>(async (url) =>
      String(url).includes("192.168.64.3")
        ? okResponse({ status: "ok", agent: "some-other-agent" })
        : agentResponse(String(url)),
    );
    const { client, fetchFn } = makeClient({
      fetch: fetchMock,
      leases: leaseFile({ ip: "192.168.64.3", lease: 0x6b01baf4 }, { ip: "192.168.64.4", lease: 0x6b01ba00 }),
    });
    await client.listWindows().catch(() => undefined);
    const urls = calledUrls(fetchFn);
    expect(urls[0]).toBe("http://192.168.64.3:8765/hello");
    expect(urls[1]).toBe("http://192.168.64.4:8765/hello");
    expect(urls[2]).toBe("http://192.168.64.4:8765/windows");
    expect(authHeaders(fetchFn).slice(0, 2)).toEqual([undefined, undefined]);
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
          throw connError(code, "connect");
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

  it("says agent_outdated when the candidate is an agent without /hello (it answers 401)", async () => {
    const { client, fetchFn } = makeClient({
      fetch: vi.fn(async () => new Response(JSON.stringify({ error: { code: "unauthorized", message: "Missing or invalid bearer token." } }), { status: 401 })),
      leases: leaseFile({ ip: "192.168.64.3", lease: 0x6b01baf4 }),
    });
    const e = await errorOf(client.health());
    expect(e.code).toBe("agent_outdated");
    expect(authHeaders(fetchFn).every((h) => h === undefined)).toBe(true);
  });

  it("a wrong proof at the only candidate ends as vm_not_running (wrong_service ranks lowest in discovery)", async () => {
    const { client } = makeClient({
      fetch: vi.fn(async () => okResponse({ agent: "ediabas-bridge-agent", proof: "0".repeat(64) })),
      leases: leaseFile({ ip: "192.168.64.3", lease: 0x6b01baf4 }),
    });
    const e = await errorOf(client.health());
    expect(e.code).toBe("vm_not_running");
  });

  it("re-reads the token at most once per call across candidates", async () => {
    const tokens = staticTokens();
    const { client } = makeClient({
      tokens,
      fetch: vi.fn(async () => okResponse({ agent: "ediabas-bridge-agent", proof: "0".repeat(64) })),
      leases: leaseFile({ ip: "192.168.64.3", lease: 3 }, { ip: "192.168.64.4", lease: 2 }, { ip: "192.168.64.5", lease: 1 }),
    });
    await errorOf(client.health());
    expect(tokens.refresh).toHaveBeenCalledTimes(1);
    await errorOf(client.health());
    expect(tokens.refresh).toHaveBeenCalledTimes(2);
  });

  it("falls back to arp, then utmctl, and ignores utmctl failure", async () => {
    const fetchMock = vi.fn<typeof fetch>(withHello(async () => okResponse()));
    const viaArp = makeClient({
      fetch: fetchMock,
      leases: null,
      arp: "? (192.168.64.7) at (incomplete) on bridge100 ifscope [bridge]\n? (192.168.64.5) at 2:0:0:0:0:1 on bridge100 ifscope [bridge]\n",
    });
    await viaArp.client.health();
    expect(calledUrls(viaArp.fetchFn)[0]).toBe("http://192.168.64.5:8765/hello");
    expect(viaArp.execFile.mock.calls.map((c) => c[0])).toEqual(["/usr/sbin/arp"]); // utmctl not needed

    const viaUtm = makeClient({ fetch: vi.fn(withHello(async () => okResponse())), leases: null, arp: "", utm: "192.168.64.6\n" });
    await viaUtm.client.health();
    expect(calledUrls(viaUtm.fetchFn)[0]).toBe("http://192.168.64.6:8765/hello");
    expect(viaUtm.execFile).toHaveBeenCalledWith(
      "/Applications/UTM.app/Contents/MacOS/utmctl",
      ["ip-address", "Windows"],
      expect.any(Object),
    );

    const nothing = makeClient({ fetch: vi.fn(withHello(async () => okResponse())), leases: null, arp: null, utm: null });
    const e = await errorOf(nothing.client.health());
    expect(e.code).toBe("vm_not_running");
    expect(nothing.fetchFn).not.toHaveBeenCalled();
  });

  it("does not run utmctl when a lease candidate is accepted", async () => {
    const { client, execFile } = makeClient({
      fetch: vi.fn(withHello(async () => okResponse())),
      leases: leaseFile({ ip: "192.168.64.3", lease: 0x6b01baf4 }),
    });
    await client.health();
    expect(execFile).not.toHaveBeenCalled();
  });

  describe("VM MAC from UTM's config", () => {
    const NO_MAC = { EDIABAS_VM_MAC: "" };
    const UTM_CONFIG = JSON.stringify({ Backend: "QEMU", Network: [{ Hardware: "virtio-net-pci", MacAddress: "02:00:00:00:00:01", Mode: "Shared" }] });
    const PLIST = "/nonexistent-home/Library/Containers/com.utmapp.UTM/Data/Documents/Windows.utm/config.plist";

    it("reads the MAC from UTM's config.plist for the VM when EDIABAS_VM_MAC is not set", async () => {
      const { client, execFile, fetchFn } = makeClient({
        env: NO_MAC,
        fetch: vi.fn(withHello(async () => okResponse())),
        plutil: UTM_CONFIG,
        leases: leaseFile({ ip: "192.168.64.3", lease: 0x6b01baf4 }),
      });
      await client.health();
      expect(calledUrls(fetchFn)[0]).toBe("http://192.168.64.3:8765/hello");
      expect(execFile).toHaveBeenCalledWith("/usr/bin/plutil", ["-convert", "json", "-o", "-", PLIST], expect.any(Object));
    });

    it("looks in the config of the VM named by EDIABAS_VM_NAME", async () => {
      const { client, execFile } = makeClient({
        env: { ...NO_MAC, EDIABAS_VM_NAME: "Win11" },
        fetch: vi.fn(withHello(async () => okResponse())),
        plutil: UTM_CONFIG,
        leases: leaseFile({ ip: "192.168.64.3", lease: 0x6b01baf4 }),
      });
      await client.health();
      expect(execFile.mock.calls[0]?.[1]).toEqual(["-convert", "json", "-o", "-", PLIST.replace("Windows.utm", "Win11.utm")]);
    });

    it("EDIABAS_VM_MAC wins: UTM's config is not read", async () => {
      const { client, execFile } = makeClient({
        fetch: vi.fn(withHello(async () => okResponse())),
        plutil: JSON.stringify({ Network: [{ MacAddress: "02:00:00:00:00:99" }] }),
        leases: leaseFile({ ip: "192.168.64.3", lease: 0x6b01baf4 }),
      });
      await client.health();
      expect(execFile).not.toHaveBeenCalled();
    });

    it("without any MAC: skips leases and ARP, still tries utmctl, and says how to set it", async () => {
      const { client, readFile, execFile } = makeClient({
        env: NO_MAC,
        fetch: vi.fn(withHello(async () => okResponse())),
        plutil: null,
        leases: leaseFile({ ip: "192.168.64.3", lease: 0x6b01baf4 }),
        arp: "? (192.168.64.3) at 2:0:0:0:0:1 on bridge100 ifscope [bridge]",
        utm: null,
      });
      const e = await errorOf(client.health());
      expect(e.code).toBe("vm_not_running");
      expect(e.message).toContain(`its MAC address is not set and could not be read from ${PLIST}`);
      expect(e.message).toContain("set EDIABAS_VM_MAC to the VM's MAC address, or EDIABAS_VM_NAME if the VM in UTM is not called 'Windows'");
      expect(readFile).not.toHaveBeenCalled();
      expect(execFile.mock.calls.map((c) => c[0])).toEqual(["/usr/bin/plutil", "/Applications/UTM.app/Contents/MacOS/utmctl"]);
    });

    it("a MAC with no lease names it and the settings to check", async () => {
      const { client } = makeClient({ fetch: vi.fn(withHello(async () => okResponse())), leases: "", arp: "", utm: null });
      const e = await errorOf(client.health());
      expect(e.code).toBe("vm_not_running");
      expect(e.message).toContain("its MAC address 02:00:00:00:00:01 is not in the DHCP leases or ARP table; if that is not the VM's MAC, set EDIABAS_VM_MAC");
    });
  });

  it("de-duplicates candidates across sources", async () => {
    const { client, fetchFn } = makeClient({
      fetch: vi.fn(async () => {
        throw connError("ECONNREFUSED");
      }),
      leases: leaseFile({ ip: "192.168.64.3", lease: 0x6b01baf4 }),
      arp: "? (192.168.64.3) at 2:0:0:0:0:1 on bridge100 ifscope [bridge]",
      utm: "192.168.64.3\n",
    });
    await errorOf(client.health());
    expect(calledUrls(fetchFn)).toEqual(["http://192.168.64.3:8765/hello"]);
  });

  it("on a connection failure clears the cache and resolves once more", async () => {
    let vmIp = "192.168.64.3";
    let leases = leaseFile({ ip: vmIp, lease: 0x6b01baf4 });
    const fetchMock = vi.fn<typeof fetch>(async (url) => {
      if (!String(url).includes(`${vmIp}:`)) throw connError("ECONNREFUSED");
      return String(url).endsWith("/windows") ? okResponse(windowsBody()) : agentResponse(String(url));
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
      "http://192.168.64.3:8765/hello",
      "http://192.168.64.3:8765/windows",
      "http://192.168.64.3:8765/windows", // fails
      "http://192.168.64.4:8765/hello",
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
        throw connError("EHOSTUNREACH", "connect");
      }),
      leases: leaseFile({ ip: "192.168.65.3", lease: 0x7f000000 }, { ip: "192.168.64.3", lease: 0x6b01baf4 }),
    });
    expect((await errorOf(client.health())).code).toBe("local_network_blocked");
    expect(calledUrls(fetchFn).some((u) => u.includes("192.168.65.3"))).toBe(false);
  });

  it("maps EHOSTUNREACH on a request right after discovery", async () => {
    const { client, logs } = makeClient({
      fetch: vi.fn(async (url) => {
        if (String(url).includes("/hello")) return agentResponse(String(url));
        throw connError("EHOSTUNREACH", "connect");
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
        if (blocked && String(url).endsWith("/windows")) throw connError("EHOSTUNREACH", "connect");
        return String(url).endsWith("/windows") ? okResponse(windowsBody()) : agentResponse(String(url));
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
      "http://192.168.64.3:8765/hello",
      "http://192.168.64.3:8765/windows",
    ]);
  });

  it("maps EHOSTUNREACH when the cached address and the re-probe are both blocked", async () => {
    let blocked = false;
    const { client } = makeClient({
      fetch: vi.fn(async (url) => {
        if (blocked) throw connError("EHOSTUNREACH", "connect");
        return String(url).endsWith("/windows") ? okResponse(windowsBody()) : agentResponse(String(url));
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
        throw connError("EHOSTUNREACH", "connect");
      }),
    });
    expect((await errorOf(client.health())).code).toBe("vm_not_running");
  });

  it("uses local_network_blocked for an unreachable explicit URL on a bridge subnet", async () => {
    const { client } = makeClient({
      env: { EDIABAS_AGENT_URL: "http://192.168.64.3:8765" },
      fetch: vi.fn(async () => {
        throw connError("EHOSTUNREACH", "connect");
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
      throw connError("EHOSTUNREACH", "connect");
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

    it("forbidden beats local_network_blocked, in either order", async () => {
      expect((await errorOf(makeClient({ fetch: by(unreachable, status(403)), leases: TWO }).client.health())).code).toBe("forbidden");
      expect((await errorOf(makeClient({ fetch: by(status(403), unreachable), leases: TWO }).client.health())).code).toBe("forbidden");
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
        return u.endsWith("/windows") ? okResponse(windowsBody()) : agentResponse(u);
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
        return u.endsWith("/windows") ? okResponse(windowsBody()) : agentResponse(u);
      }),
      leases: LEASES,
    });
    await c.listWindows();
    refuse = true;
    const r = await c.runJob({ ecu: "NVE_65", job: "IDENT" });
    expect(r.job).toBe("IDENT");
    expect(calledUrls(fetchFn).slice(2)).toEqual([
      "http://192.168.64.3:8765/ediabas/job", // refused
      "http://192.168.64.3:8765/hello",
      "http://192.168.64.3:8765/ediabas/job",
    ]);
  });

  it("runJob: EHOSTUNREACH with syscall 'write' is not re-sent and says it may have run", async () => {
    let fail = false;
    const { client: c, fetchFn } = makeClient({
      fetch: vi.fn(async (url) => {
        const u = String(url);
        if (u.endsWith("/ediabas/job") && fail) throw connError("EHOSTUNREACH", "write");
        return u.endsWith("/windows") ? okResponse(windowsBody()) : agentResponse(u);
      }),
      leases: LEASES,
    });
    await c.listWindows();
    fail = true;
    const e = await errorOf(c.runJob({ ecu: "NVE_65", job: "IDENT" }));
    expect(e.code).toBe("ediabas_request_lost");
    expect(calledUrls(fetchFn).filter((u) => u.endsWith("/ediabas/job"))).toHaveLength(1);
  });

  it("runJob: EHOSTUNREACH with syscall 'connect' rediscovers and sends once more", async () => {
    let fail = false;
    const { client: c, fetchFn } = makeClient({
      fetch: vi.fn(async (url) => {
        const u = String(url);
        if (u.endsWith("/ediabas/job") && fail) {
          fail = false;
          throw connError("EHOSTUNREACH", "connect");
        }
        if (u.endsWith("/ediabas/job")) return okResponse(JOB);
        return u.endsWith("/windows") ? okResponse(windowsBody()) : agentResponse(u);
      }),
      leases: LEASES,
    });
    await c.listWindows();
    fail = true;
    expect((await c.runJob({ ecu: "NVE_65", job: "IDENT" })).job).toBe("IDENT");
    expect(calledUrls(fetchFn).filter((u) => u.endsWith("/ediabas/job"))).toHaveLength(2);
  });

  describe("connect timeout (the TCP handshake never completed) against ETIMEDOUT", () => {
    /** `path` fails once with `code`, then answers. */
    function failOnce(path: string, code: string, body: unknown) {
      let fail = false;
      const c = makeClient({
        fetch: vi.fn(async (url) => {
          const u = String(url);
          if (fail && u.endsWith(path)) {
            fail = false;
            throw connError(code);
          }
          if (u.endsWith(path)) return okResponse(body);
          return u.endsWith("/windows") ? okResponse(windowsBody()) : agentResponse(u);
        }),
        leases: LEASES,
      });
      return { ...c, arm: () => (fail = true) };
    }

    it("POST /ediabas/job after UND_ERR_CONNECT_TIMEOUT: the address is resolved again and the job sent once more", async () => {
      const { client: c, fetchFn, arm } = failOnce("/ediabas/job", "UND_ERR_CONNECT_TIMEOUT", JOB);
      await c.listWindows();
      arm();
      const r = await c.runJob({ ecu: "NVE_65", job: "IDENT" });
      expect(r.job).toBe("IDENT");
      expect(calledUrls(fetchFn).slice(2)).toEqual([
        "http://192.168.64.3:8765/ediabas/job", // never connected
        "http://192.168.64.3:8765/hello",
        "http://192.168.64.3:8765/ediabas/job",
      ]);
    });

    it("POST /ediabas/job after ETIMEDOUT: not re-sent, may have run", async () => {
      const { client: c, fetchFn, arm } = failOnce("/ediabas/job", "ETIMEDOUT", JOB);
      await c.listWindows();
      arm();
      const e = await errorOf(c.runJob({ ecu: "NVE_65", job: "IDENT" }));
      expect(e.code).toBe("ediabas_request_lost");
      expect(calledUrls(fetchFn).slice(2)).toEqual(["http://192.168.64.3:8765/ediabas/job"]);
    });

    it("a connect timeout right after discovery is not reported as possibly run", async () => {
      const { client: c, arm } = failOnce("/ediabas/job", "UND_ERR_CONNECT_TIMEOUT", JOB);
      arm();
      const e = await errorOf(c.runJob({ ecu: "NVE_65", job: "IDENT" }));
      expect(e.code).not.toBe("ediabas_request_lost");
      expect(e.message).not.toMatch(/may have run/);
    });

    it.each(["UND_ERR_CONNECT_TIMEOUT", "ETIMEDOUT"])("GET /windows after %s: resolved again and re-sent", async (code) => {
      const { client: c, fetchFn, arm } = failOnce("/windows", code, windowsBody());
      await c.listWindows();
      arm();
      expect((await c.listWindows()).windows).toHaveLength(2);
      expect(calledUrls(fetchFn).slice(2)).toEqual([
        "http://192.168.64.3:8765/windows",
        "http://192.168.64.3:8765/hello",
        "http://192.168.64.3:8765/windows",
      ]);
    });

    it("the VM came back on a new address: the next job call finds it", async () => {
      let vmIp = "192.168.64.3";
      const fetchMock = vi.fn<typeof fetch>(async (url) => {
        if (!String(url).includes(`${vmIp}:`)) throw connError("UND_ERR_CONNECT_TIMEOUT");
        if (String(url).endsWith("/ediabas/job")) return okResponse(JOB);
        return String(url).endsWith("/windows") ? okResponse(windowsBody()) : agentResponse(String(url));
      });
      const c = makeClient({ fetch: fetchMock, leases: LEASES });
      await c.client.listWindows();
      vmIp = "192.168.64.4";
      c.readFile.mockImplementation(async () => leaseFile({ ip: vmIp, lease: 0x6b01baf5 }));
      expect((await c.client.runJob({ ecu: "NVE_65", job: "IDENT" })).job).toBe("IDENT");
      expect(calledUrls(c.fetchFn).filter((u) => u.endsWith("/ediabas/job"))).toEqual([
        "http://192.168.64.3:8765/ediabas/job",
        "http://192.168.64.4:8765/ediabas/job",
      ]);
    });
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
          return u.endsWith("/windows") ? okResponse(windowsBody()) : agentResponse(u);
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

describe("one EDIABAS request at a time", () => {
  /**
   * Like the agent: /ediabas/* requests are served one at a time, each taking `workMs`; a
   * request that arrives while another runs waits in the agent's queue.
   */
  async function oneAtATime(workMs: number) {
    const state = { running: 0, maxRunning: 0, arrivedWhileBusy: 0 };
    let tail = Promise.resolve();
    const ok = stubHandler();
    const a = await fake(ok);
    a.setHandler((req, res) => {
      if (!req.url?.startsWith("/ediabas/")) return ok(req, res);
      if (state.running > 0) state.arrivedWhileBusy++;
      state.running++;
      state.maxRunning = Math.max(state.maxRunning, state.running);
      tail = tail.then(
        () =>
          new Promise<void>((done) =>
            setTimeout(() => {
              ok(req, res);
              state.running--;
              done();
            }, workMs),
          ),
      );
    });
    return { a, state };
  }

  it("sends two concurrent job calls one after the other; both succeed, neither is sent twice", async () => {
    const { a, state } = await oneAtATime(100);
    // Each job takes 100 ms; had both been sent at once, the second would wait 200 ms in the
    // agent's queue and pass the 150 ms limit. The limit counts from sending, not queueing.
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url }, ediabasTimeoutMs: 150 });
    await client.health();
    const [r1, r2] = await Promise.all([client.runJob({ ecu: "NVE_65", job: "IDENT" }), client.runJobs([{ ecu: "NVE_65", job: "FS_LESEN" }])]);
    expect(r1.job).toBe("IDENT");
    expect(r2.items).toHaveLength(1);
    expect(state.arrivedWhileBusy).toBe(0);
    expect(a.requests.filter((r) => r.path === "/ediabas/job")).toHaveLength(1);
    expect(a.requests.filter((r) => r.path === "/ediabas/jobs")).toHaveLength(1);
  });

  it("keeps order: calls are sent in the order they were made", async () => {
    const { a } = await oneAtATime(20);
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url } });
    await client.health();
    await Promise.all([
      client.runJob({ ecu: "A", job: "IDENT" }),
      client.describeJob({ ecu: "B", job: "IDENT" }),
      client.listEcus("C"),
    ]);
    expect(a.requests.filter((r) => r.path.startsWith("/ediabas/")).map((r) => r.path.split("?")[0])).toEqual([
      "/ediabas/job",
      "/ediabas/describe",
      "/ediabas/ecus",
    ]);
  });

  it("answers ediabas_busy without sending when the wait passes the limit", async () => {
    const { a } = await oneAtATime(300);
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url }, ediabasQueueWaitMs: 100 });
    await client.health();
    const first = client.runJob({ ecu: "NVE_65", job: "IDENT" });
    const e = await errorOf(client.runJob({ ecu: "NVE_65", job: "FS_LESEN" }));
    expect(e.code).toBe("ediabas_busy");
    expect(e.message).toMatch(/another EDIABAS request is still running; this one was not sent/i);
    expect((await first).job).toBe("IDENT");
    expect(a.requests.filter((r) => r.path === "/ediabas/job").map((r) => JSON.parse(r.body).job)).toEqual(["IDENT"]);
    // The queue is free again afterwards.
    expect((await client.runJob({ ecu: "NVE_65", job: "FS_LESEN" })).job).toBe("FS_LESEN");
  });

  it("does not hold other calls behind an EDIABAS request", async () => {
    const { a } = await oneAtATime(300);
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url } });
    await client.health();
    const job = client.runJob({ ecu: "NVE_65", job: "IDENT" });
    const started = performance.now();
    await client.listWindows();
    expect(performance.now() - started).toBeLessThan(250);
    await job;
  });

  it("frees the queue after a failed request", async () => {
    const { a } = await oneAtATime(10);
    const { client } = makeClient({ env: { EDIABAS_AGENT_URL: a.url } });
    await client.health();
    const ok = stubHandler();
    let fail = true;
    a.setHandler((req, res) => {
      if (req.url === "/ediabas/job" && fail) {
        fail = false;
        json(res, 409, { error: { code: "cable_in_use", message: "x", details: null } });
      } else ok(req, res);
    });
    expect((await errorOf(client.runJob({ ecu: "NVE_65", job: "IDENT" }))).code).toBe("cable_in_use");
    expect((await client.runJob({ ecu: "NVE_65", job: "IDENT" })).job).toBe("IDENT");
  });
});

describe("stderr diagnostics", () => {
  it("logs the accepted address and its source", async () => {
    const ok = vi.fn(withHello(async () => okResponse()));
    const viaLeases = makeClient({ fetch: ok, leases: leaseFile({ ip: "192.168.64.3", lease: 1 }) });
    await viaLeases.client.health();
    expect(viaLeases.logs).toContain("discovery accepted 192.168.64.3:8765 (source: leases)");

    const viaArp = makeClient({ fetch: ok, leases: null, arp: "? (192.168.64.5) at 2:0:0:0:0:1 on bridge100 ifscope [bridge]" });
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
    const a = await fake((_req, res) => json(res, 403, { error: { code: "forbidden", message: "Caller is not the gateway." } }));
    const http = makeClient({ env: { EDIABAS_AGENT_URL: a.url } });
    await errorOf(http.client.health());
    expect(http.logs).toContain(`probe 127.0.0.1:${a.port} failed: HTTP 403`);

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
    await client.runJob({ ecu: "NVE_65", job: "IDENT", args: "1;2" });
    await client.runJobs([{ ecu: "NVE_65", job: "FS_LESEN" }]);
    const seen = a.requests.filter((r) => !r.path.startsWith("/hello")).map((r) => `${r.method} ${r.path}${r.body ? ` ${r.body}` : ""}`);
    expect(seen).toEqual([
      "GET /screen/capture?window=INPA+-+BMW&maxEdge=800",
      "GET /logs?root=1&glob=*.trc",
      "GET /logs/read?path=C%3A%5CEDIABAS%5CTRACE%5Capi.trc&offset=10&length=20&grep=ERR",
      "GET /ediabas/ecus?filter=nve",
      "GET /ediabas/describe?ecu=NVE_65&filter=LESEN",
      "GET /ediabas/describe?ecu=NVE_65&job=FS_LESEN",
      'POST /ediabas/job {"ecu":"NVE_65","job":"IDENT","args":"1;2"}',
      'POST /ediabas/jobs {"items":[{"ecu":"NVE_65","job":"FS_LESEN"}]}',
    ]);
    expect(a.requests.filter((r) => !r.path.startsWith("/hello")).every((r) => r.authorization === `Bearer ${AGENT_TOKEN}`)).toBe(true);
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
    await client.runJob({ ecu: "NVE_65", job: "IDENT" });
    await client.runJobs([{ ecu: "NVE_65", job: "IDENT" }]);
    expect(spy.mock.calls.map((c) => c[0])).toEqual([75_000, 75_000, 75_000]);
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
    expect(e.message).toMatch(/cable is in use by a BMW tool in the VM\. Ask the user to close it/);
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
    for (const p of [() => c2.listEcus(), () => c2.runJobs([{ ecu: "A", job: "IDENT" }]), () => c2.listLogs(), () => c2.readLog({ path: "C:\\a" })]) {
      expect((await errorOf(p())).code).toBe("agent_outdated");
    }
    expect((await errorOf(client.describeJobs({ ecu: "NOPE" }))).code).toBe("ecu_not_found");
  });

  it("rejects a malformed job result as bad_response", async () => {
    const { client } = await stubbed({ "POST /ediabas/job": () => ({ status: 200, body: { ecu: "X", sets: "nope" } }) });
    expect((await errorOf(client.runJob({ ecu: "X", job: "IDENT" }))).code).toBe("bad_response");
  });
});
