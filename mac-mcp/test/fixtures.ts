import { createHash, createHmac } from "node:crypto";
import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";

export const AGENT_TOKEN = "TESTtoken_0123456789abcdefghijklmnopqrstuvwx"; // 43 chars, fake
/** A made-up VM MAC (locally administered), as configured and as macOS prints it. */
export const TEST_VM_MAC = "02:00:00:00:00:01";
export const TEST_VM_MAC_SHORT = "2:0:0:0:0:1";

export function healthBody(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    agent: "ediabas-bridge-agent",
    version: "0.1.0",
    startedAt: "2026-11-21T15:04:05Z",
    uptimeSeconds: 512,
    os: {
      description: "Microsoft Windows 10.0.26100",
      osArchitecture: "Arm64",
      processArchitecture: "X86",
    },
    user: "user",
    elevated: false,
    sessionId: 1,
    bindAddress: "192.168.64.3",
    port: 8765,
    gateway: "192.168.64.1",
    conflictingProcesses: [{ name: "INPALOAD", pid: 4312 }],
    ediabas: { dllPath: "C:\\EDIABAS\\BIN\\api32.dll", dllFound: true },
    lastCableUseAt: null,
    ...overrides,
  };
}

export function windowsBody(): Record<string, unknown> {
  return {
    windows: [
      {
        handle: "0x000A0B2C",
        title: "INPA - BMW Group",
        processName: "INPALOAD",
        pid: 4312,
        bounds: { x: 0, y: 0, width: 1280, height: 800 },
        minimized: false,
      },
      {
        handle: "0x00010010",
        title: "Program Manager",
        processName: null,
        pid: 812,
        bounds: { x: -32000, y: -32000, width: 160, height: 28 },
        minimized: true,
      },
    ],
  };
}

export type Handler = (req: IncomingMessage, res: ServerResponse) => void;

export interface FakeAgent {
  url: string;
  port: number;
  requests: { method: string; path: string; authorization: string | undefined; body: string }[];
  setHandler(h: Handler): void;
  close(): Promise<void>;
}

export function json(res: ServerResponse, status: number, body: unknown, headers: Record<string, string> = {}): void {
  res.writeHead(status, { "content-type": "application/json; charset=utf-8", ...headers });
  res.end(JSON.stringify(body));
}

/** The /hello proof (docs/api.md "GET /hello"), computed independently of mac-mcp/src. */
export function helloProof(token: string, nonce: string): string {
  const key = createHash("sha256").update(token, "utf8").digest();
  return createHmac("sha256", key).update(`ediabas-bridge-hello-v1:${nonce}`, "utf8").digest("hex");
}

/** Answers `GET /hello?nonce=` like the agent (no token needed). False when it is not that request. */
export function answerHello(req: IncomingMessage, res: ServerResponse, token: string): boolean {
  const url = new URL(req.url ?? "/", "http://fake");
  if (req.method !== "GET" || url.pathname !== "/hello") return false;
  const nonce = url.searchParams.get("nonce") ?? "";
  if (!/^[0-9a-f]{32}$/.test(nonce) || [...url.searchParams.keys()].length !== 1) {
    json(res, 400, { error: { code: "bad_request", message: "nonce must be 32 lowercase hex characters.", details: { parameter: "nonce" } } });
  } else {
    json(res, 200, { agent: "ediabas-bridge-agent", proof: helloProof(token, nonce) });
  }
  return true;
}

/** The real agent's behaviour per docs/api.md, for a given token. */
export function contractHandler(token: string = AGENT_TOKEN, health = healthBody(), windows = windowsBody()): Handler {
  return (req, res) => {
    if (answerHello(req, res, token)) return;
    if (req.headers.authorization !== `Bearer ${token}`) {
      json(res, 401, { error: { code: "unauthorized", message: "Missing or invalid bearer token." } }, { "www-authenticate": "Bearer" });
      return;
    }
    if (req.url === "/health" || req.url === "/windows") {
      if (req.method !== "GET") {
        json(res, 405, { error: { code: "method_not_allowed", message: "Use GET." } });
        return;
      }
      json(res, 200, req.url === "/health" ? health : windows);
      return;
    }
    json(res, 404, { error: { code: "not_found", message: "No such path." } });
  };
}

export async function startFakeAgent(handler: Handler = contractHandler()): Promise<FakeAgent> {
  let current = handler;
  const requests: FakeAgent["requests"] = [];
  const server: Server = createServer((req, res) => {
    const chunks: Buffer[] = [];
    req.on("data", (c: Buffer) => chunks.push(c));
    req.on("end", () => {
      const body = Buffer.concat(chunks).toString("utf8");
      (req as IncomingMessage & { bodyText?: string }).bodyText = body;
      requests.push({ method: req.method ?? "", path: req.url ?? "", authorization: req.headers.authorization, body });
      current(req, res);
    });
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address() as AddressInfo;
  return {
    url: `http://127.0.0.1:${port}`,
    port,
    requests,
    setHandler(h) {
      current = h;
    },
    async close() {
      server.closeAllConnections();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    },
  };
}

/** A port on 127.0.0.1 with nothing listening. */
export async function closedPort(): Promise<number> {
  const s = createServer();
  await new Promise<void>((resolve) => s.listen(0, "127.0.0.1", resolve));
  const { port } = s.address() as AddressInfo;
  await new Promise<void>((resolve) => s.close(() => resolve()));
  return port;
}

/** Interfaces where bridge100 covers 127.0.0.0/8, so a local fake agent counts as on-link. */
export const LOOPBACK_BRIDGE = {
  bridge100: [{ address: "127.0.0.1", netmask: "255.0.0.0", family: "IPv4", mac: "f2:00:00:00:00:64", internal: false, cidr: "127.0.0.1/8" }],
};

export const VM_BRIDGE = {
  en0: [{ address: "192.168.1.20", netmask: "255.255.255.0", family: "IPv4", mac: "aa:aa:aa:aa:aa:aa", internal: false, cidr: "192.168.1.20/24" }],
  bridge100: [{ address: "192.168.64.1", netmask: "255.255.255.0", family: "IPv4", mac: "f2:00:00:00:00:64", internal: false, cidr: "192.168.64.1/24" }],
};

export const NO_BRIDGE = {
  lo0: [{ address: "127.0.0.1", netmask: "255.0.0.0", family: "IPv4", mac: "00:00:00:00:00:00", internal: true, cidr: "127.0.0.1/8" }],
  en0: [{ address: "192.168.1.20", netmask: "255.255.255.0", family: "IPv4", mac: "aa:aa:aa:aa:aa:aa", internal: false, cidr: "192.168.1.20/24" }],
};

export function leaseFile(...entries: { ip: string; mac?: string; lease: number }[]): string {
  return entries
    .map((e) => `{\n\tname=host\n\tip_address=${e.ip}\n\thw_address=1,${e.mac ?? TEST_VM_MAC_SHORT}\n\tidentifier=1,${e.mac ?? TEST_VM_MAC_SHORT}\n\tlease=0x${e.lease.toString(16)}\n}\n`)
    .join("");
}
