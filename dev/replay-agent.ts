#!/usr/bin/env node
/**
 * Replay agent: a stand-in for the VM agent (docs/api.md) that answers only from recorded
 * exchanges, so the MCP server and the skills can be exercised with no VM and no car.
 *
 *   node dev/replay-agent.ts --scenario <name> [--port 8765] [--fixtures dev/fixtures] [--token <t>]
 *
 * The token comes from --token or EDIABAS_AGENT_TOKEN; a request without it gets 401 like the
 * real agent, except `GET /hello`, whose proof is computed live from that token (recordings
 * never hold it). Binds to 127.0.0.1 only. A request with no matching recording gets a 404
 * `no_recording` naming what was asked: this file never makes up an answer.
 *
 * A scenario is a folder of recordings in the format dev/recorder.ts writes, plus an
 * optional scenario.json (see dev/fixtures/README.md) that can name a base scenario to overlay
 * and routes that serve one recorded response for a whole path prefix.
 *
 * Node 24+ runs this file directly (type stripping): keep it to erasable TypeScript and node:
 * built-ins only.
 */
import { createHash, createHmac, timingSafeEqual } from "node:crypto";
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

export const DEFAULT_PORT = 8765;
export const DEFAULT_FIXTURES = resolve(dirname(fileURLToPath(import.meta.url)), "fixtures");
/** The real agent only ever listens on one address; this one only on loopback. */
export const BIND_HOST = "127.0.0.1";
const BODY_LIMIT = 64 * 1024;
const SCENARIO_FILE = "scenario.json";
const SCENARIO_NAME = /^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/;
const IMAGE_FILE = /^[A-Za-z0-9][A-Za-z0-9._-]{0,200}\.(jpg|png)$/;
const MAX_BASE_DEPTH = 8;

// ---------------------------------------------------------------------------------------------
// Recordings and scenarios
// ---------------------------------------------------------------------------------------------

export interface RecordedResponse {
  status: number;
  headers: Record<string, string>;
  /** Exactly one of these three is set. `hasJson` tells a JSON `null` body from "no body". */
  hasJson: boolean;
  json?: unknown;
  bodyText?: string;
  image?: Buffer;
}

export interface Recording {
  file: string;
  recordedAt: string;
  method: string;
  path: string;
  /** Query pairs sorted by key, for order-insensitive comparison. */
  query: [string, string][];
  body: unknown;
  response: RecordedResponse;
}

export interface Route {
  method: string;
  path?: string;
  pathPrefix?: string;
  recording: Recording;
}

/** One scenario folder; `base` is the scenario it overlays, if any. */
export interface ScenarioLayer {
  name: string;
  dir: string;
  description: string | null;
  recordings: Recording[];
  routes: Route[];
  base: ScenarioLayer | null;
}

export class ReplayConfigError extends Error {}

function fail(message: string): never {
  throw new ReplayConfigError(message);
}

function isObject(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function stringRecord(v: unknown, what: string): Record<string, string> {
  if (!isObject(v)) fail(`${what} must be an object of strings`);
  for (const [k, x] of Object.entries(v)) if (typeof x !== "string") fail(`${what}.${k} must be a string`);
  return v as Record<string, string>;
}

function sortedPairs(q: Record<string, string>): [string, string][] {
  return Object.entries(q).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0));
}

/** Parse one recording file (dev/recorder.ts format). Throws ReplayConfigError. */
export function parseRecording(dir: string, file: string): Recording {
  const where = join(dir, file);
  let raw: unknown;
  try {
    raw = JSON.parse(readFileSync(where, "utf8"));
  } catch (e) {
    fail(`${where}: not valid JSON (${e instanceof Error ? e.message : String(e)})`);
  }
  if (!isObject(raw)) fail(`${where}: not a recording (expected an object)`);
  const req = raw.request;
  const res = raw.response;
  if (!isObject(req) || !isObject(res)) fail(`${where}: not a recording (needs "request" and "response")`);
  const method = req.method;
  if (method !== "GET" && method !== "POST") fail(`${where}: request.method must be GET or POST`);
  if (typeof req.path !== "string" || !req.path.startsWith("/")) fail(`${where}: request.path must start with /`);
  const query = stringRecord(req.query ?? {}, `${where}: request.query`);
  const status = res.status;
  if (typeof status !== "number" || !Number.isInteger(status) || status < 100 || status > 599) {
    fail(`${where}: response.status must be an HTTP status`);
  }
  const headers = stringRecord(res.headers ?? {}, `${where}: response.headers`);
  const kinds = ["body", "bodyText", "imageFile"].filter((k) => k in res);
  if (kinds.length !== 1) fail(`${where}: response needs exactly one of body, bodyText, imageFile`);
  const response: RecordedResponse = { status, headers: lowerKeys(headers), hasJson: false };
  if ("body" in res) {
    response.hasJson = true;
    response.json = res.body;
  } else if ("bodyText" in res) {
    if (typeof res.bodyText !== "string") fail(`${where}: response.bodyText must be a string`);
    response.bodyText = res.bodyText;
  } else {
    const img = res.imageFile;
    if (typeof img !== "string" || !IMAGE_FILE.test(img) || img.includes("..")) {
      fail(`${where}: response.imageFile must be a .jpg or .png file name in the same folder`);
    }
    const imgPath = join(dir, img);
    if (!existsSync(imgPath)) fail(`${where}: image file ${img} is missing`);
    response.image = readFileSync(imgPath);
  }
  return {
    file,
    recordedAt: typeof raw.recordedAt === "string" ? raw.recordedAt : "",
    method,
    path: req.path,
    query: sortedPairs(query),
    body: req.body ?? null,
    response,
  };
}

function lowerKeys(h: Record<string, string>): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(h)) out[k.toLowerCase()] = v;
  return out;
}

interface ScenarioFile {
  description: string | null;
  base: string | null;
  routes: { method: string; path?: string; pathPrefix?: string; recording: string }[];
}

function readScenarioFile(dir: string): ScenarioFile {
  const where = join(dir, SCENARIO_FILE);
  if (!existsSync(where)) return { description: null, base: null, routes: [] };
  let raw: unknown;
  try {
    raw = JSON.parse(readFileSync(where, "utf8"));
  } catch (e) {
    fail(`${where}: not valid JSON (${e instanceof Error ? e.message : String(e)})`);
  }
  if (!isObject(raw)) fail(`${where}: must be an object`);
  const known = new Set(["description", "base", "routes"]);
  for (const k of Object.keys(raw)) if (!known.has(k)) fail(`${where}: unknown field "${k}"`);
  const description = raw.description === undefined ? null : raw.description;
  if (description !== null && typeof description !== "string") fail(`${where}: description must be a string`);
  const base = raw.base === undefined ? null : raw.base;
  if (base !== null && (typeof base !== "string" || !SCENARIO_NAME.test(base))) fail(`${where}: base must be a scenario name`);
  const routesRaw = raw.routes === undefined ? [] : raw.routes;
  if (!Array.isArray(routesRaw)) fail(`${where}: routes must be an array`);
  const routes = routesRaw.map((r: unknown, i: number) => {
    const at = `${where}: routes[${i}]`;
    if (!isObject(r)) fail(`${at} must be an object`);
    for (const k of Object.keys(r)) if (!["method", "path", "pathPrefix", "recording"].includes(k)) fail(`${at}: unknown field "${k}"`);
    const method = r.method ?? "*";
    if (method !== "GET" && method !== "POST" && method !== "*") fail(`${at}.method must be GET, POST or *`);
    const hasPath = typeof r.path === "string";
    const hasPrefix = typeof r.pathPrefix === "string";
    if (hasPath === hasPrefix) fail(`${at} needs exactly one of path, pathPrefix`);
    const p = (hasPath ? r.path : r.pathPrefix) as string;
    if (!p.startsWith("/")) fail(`${at}: path must start with /`);
    if (typeof r.recording !== "string" || basename(r.recording) !== r.recording || !r.recording.endsWith(".json") || r.recording === SCENARIO_FILE) {
      fail(`${at}.recording must be the file name of a recording in this folder`);
    }
    return { method, path: hasPath ? p : undefined, pathPrefix: hasPrefix ? p : undefined, recording: r.recording };
  });
  return { description, base, routes };
}

/**
 * Load a scenario and, through scenario.json `base`, every scenario under it. Fails loudly on
 * anything malformed, so a broken fixture is found at start-up and not as a wrong answer.
 */
export function loadScenario(fixturesDir: string, name: string, chain: string[] = []): ScenarioLayer {
  if (!SCENARIO_NAME.test(name)) fail(`scenario name "${name}" is not allowed (letters, digits, - and _ only)`);
  if (chain.includes(name)) fail(`scenario base chain loops: ${[...chain, name].join(" -> ")}`);
  if (chain.length >= MAX_BASE_DEPTH) fail(`scenario base chain is too deep: ${[...chain, name].join(" -> ")}`);
  const dir = join(fixturesDir, name);
  if (!existsSync(dir) || !statSync(dir).isDirectory()) fail(`no scenario folder ${dir}`);
  const spec = readScenarioFile(dir);
  const recordings = readdirSync(dir)
    .filter((f) => f.endsWith(".json") && f !== SCENARIO_FILE)
    .sort()
    .map((f) => parseRecording(dir, f));
  const byFile = new Map(recordings.map((r) => [r.file, r]));
  const routes: Route[] = spec.routes.map((r) => {
    const rec = byFile.get(r.recording);
    if (!rec) fail(`${join(dir, SCENARIO_FILE)}: route recording ${r.recording} is not a recording in ${dir}`);
    return { method: r.method, path: r.path, pathPrefix: r.pathPrefix, recording: rec };
  });
  const base = spec.base === null ? null : loadScenario(fixturesDir, spec.base, [...chain, name]);
  if (recordings.length === 0 && routes.length === 0 && base === null) fail(`scenario ${name} has no recordings`);
  return { name, dir, description: spec.description, recordings, routes, base };
}

// ---------------------------------------------------------------------------------------------
// Matching
// ---------------------------------------------------------------------------------------------

export interface IncomingRequest {
  method: string;
  path: string;
  /** Query pairs as received, any order. */
  query: [string, string][];
  /** Parsed JSON body, or null when there is none. */
  body: unknown;
}

/** JSON equality with object keys in any order; arrays in order. */
export function jsonEqual(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (Array.isArray(a) || Array.isArray(b)) {
    if (!Array.isArray(a) || !Array.isArray(b) || a.length !== b.length) return false;
    return a.every((x, i) => jsonEqual(x, b[i]));
  }
  if (isObject(a) && isObject(b)) {
    const ka = Object.keys(a);
    if (ka.length !== Object.keys(b).length) return false;
    return ka.every((k) => Object.hasOwn(b, k) && jsonEqual(a[k], b[k]));
  }
  return false;
}

function queryEqual(recorded: [string, string][], incoming: [string, string][]): boolean {
  if (recorded.length !== incoming.length) return false;
  const sorted = [...incoming].sort(([a, x], [b, y]) => (a < b ? -1 : a > b ? 1 : x < y ? -1 : x > y ? 1 : 0));
  return recorded.every(([k, v], i) => sorted[i]![0] === k && sorted[i]![1] === v);
}

function exactMatch(r: Recording, q: IncomingRequest): boolean {
  return r.method === q.method && r.path === q.path && queryEqual(r.query, q.query) && jsonEqual(r.body, q.body);
}

function routeMatch(r: Route, q: IncomingRequest): boolean {
  if (r.method !== "*" && r.method !== q.method) return false;
  return r.path !== undefined ? q.path === r.path : q.path.startsWith(r.pathPrefix!);
}

export interface Match {
  recording: Recording;
  scenario: string;
  how: "exact" | "route";
  /** How many recordings matched exactly in that layer (more than one: the latest is served). */
  candidates: number;
}

/**
 * Find the recording for a request. Per layer, top scenario first: exact recordings (several
 * matches: the one recorded last wins), then that layer's routes in file order; then the base.
 */
export function findRecording(layer: ScenarioLayer, q: IncomingRequest): Match | null {
  for (let l: ScenarioLayer | null = layer; l; l = l.base) {
    const hits = l.recordings.filter((r) => exactMatch(r, q));
    if (hits.length > 0) {
      const latest = hits.reduce((a, b) => (`${b.recordedAt}\u0000${b.file}` > `${a.recordedAt}\u0000${a.file}` ? b : a));
      return { recording: latest, scenario: l.name, how: "exact", candidates: hits.length };
    }
    const route = l.routes.find((r) => routeMatch(r, q));
    if (route) return { recording: route.recording, scenario: l.name, how: "route", candidates: 1 };
  }
  return null;
}

// ---------------------------------------------------------------------------------------------
// HTTP
// ---------------------------------------------------------------------------------------------

const JSON_TYPE = "application/json; charset=utf-8";

function sendJson(res: ServerResponse, status: number, body: unknown, extra: Record<string, string> = {}): void {
  const bytes = Buffer.from(JSON.stringify(body), "utf8");
  res.writeHead(status, { "content-type": JSON_TYPE, "content-length": String(bytes.length), ...extra });
  res.end(bytes);
}

function sendError(res: ServerResponse, status: number, code: string, message: string, details: unknown = null, extra: Record<string, string> = {}): void {
  sendJson(res, status, { error: { code, message, details } }, extra);
}

function digest(s: string): Buffer {
  return createHash("sha256").update(s, "utf8").digest();
}

function tokenOk(header: string | undefined, expected: Buffer): boolean {
  const m = /^Bearer ([^\s]+)$/.exec(header ?? "");
  return m !== null && timingSafeEqual(digest(m[1]!), expected);
}

/** `GET /hello` like the agent (docs/api.md): the proof is HMAC-SHA256 keyed with SHA-256(token). */
function answerHello(res: ServerResponse, url: URL, tokenHash: Buffer, log: (line: string) => void): void {
  const keys = [...url.searchParams.keys()];
  const nonce = url.searchParams.get("nonce") ?? "";
  if (keys.length !== 1 || keys[0] !== "nonce" || !/^[0-9a-f]{32}$/.test(nonce)) {
    log("GET /hello -> 400 (nonce must be 32 lowercase hex characters, given once)");
    sendError(res, 400, "bad_request", "nonce must be 32 lowercase hex characters, given once.", { parameter: "nonce" });
    return;
  }
  log("GET /hello -> 200 (proof computed from the replay token)");
  const proof = createHmac("sha256", tokenHash).update(`ediabas-bridge-hello-v1:${nonce}`, "utf8").digest("hex");
  sendJson(res, 200, { agent: "ediabas-bridge-agent", proof });
}

function readBody(req: IncomingMessage): Promise<Buffer | null> {
  return new Promise((resolveBody, reject) => {
    const chunks: Buffer[] = [];
    let size = 0;
    let over = false;
    req.on("data", (c: Buffer) => {
      size += c.length;
      if (size > BODY_LIMIT) over = true;
      else chunks.push(c);
    });
    req.on("end", () => resolveBody(over ? null : Buffer.concat(chunks)));
    req.on("error", reject);
  });
}

function describeAsk(q: IncomingRequest): string {
  const qs = q.query.length ? "?" + new URLSearchParams(q.query).toString() : "";
  const body = q.body === null ? "" : ` with body ${JSON.stringify(q.body)}`;
  const text = `${q.method} ${q.path}${qs}${body}`;
  return text.length > 600 ? text.slice(0, 600) + "…" : text;
}

function serveRecording(res: ServerResponse, r: RecordedResponse): void {
  const headers: Record<string, string> = {};
  for (const [k, v] of Object.entries(r.headers)) if (k === "content-type" || k.startsWith("x-")) headers[k] = v;
  let bytes: Buffer;
  if (r.image) {
    bytes = r.image;
  } else if (r.hasJson) {
    bytes = Buffer.from(JSON.stringify(r.json), "utf8");
    headers["content-type"] ??= JSON_TYPE;
  } else {
    bytes = Buffer.from(r.bodyText ?? "", "utf8");
  }
  if (r.status === 401) headers["www-authenticate"] = "Bearer";
  headers["content-length"] = String(bytes.length);
  res.writeHead(r.status, headers);
  res.end(bytes);
}

export interface ReplayOptions {
  scenario: ScenarioLayer;
  token: string;
  /** 0 picks a free port (tests). */
  port: number;
  log?: (line: string) => void;
}

export interface ReplayAgent {
  url: string;
  port: number;
  server: Server;
  close(): Promise<void>;
}

export async function startReplayAgent(opts: ReplayOptions): Promise<ReplayAgent> {
  if (!opts.token) throw new ReplayConfigError("a bearer token is required (--token or EDIABAS_AGENT_TOKEN)");
  const expected = digest(opts.token);
  const log = opts.log ?? ((line: string) => process.stderr.write(`[replay-agent] ${line}\n`));
  const top = opts.scenario;

  const handle = async (req: IncomingMessage, res: ServerResponse): Promise<void> => {
    const url = new URL(req.url ?? "/", "http://replay.invalid");
    const method = req.method ?? "";
    // Same order as the agent: token before anything else, so unknown paths stay hidden. Only
    // GET /hello (that exact path) goes without it.
    if (method === "GET" && url.pathname === "/hello") {
      answerHello(res, url, expected, log);
      return;
    }
    if (!tokenOk(req.headers.authorization, expected)) {
      log(`${method} ${url.pathname} -> 401 unauthorized`);
      sendError(res, 401, "unauthorized", "Missing or invalid bearer token.", null, { "www-authenticate": "Bearer" });
      return;
    }
    if (method !== "GET" && method !== "POST") {
      log(`${method} ${url.pathname} -> 405 (the replay agent serves GET and POST only)`);
      sendError(res, 405, "method_not_allowed", "The replay agent serves GET and POST only.");
      return;
    }
    const raw = await readBody(req);
    if (raw === null) {
      sendError(res, 400, "bad_request", "Request body over 64 KB.");
      return;
    }
    let body: unknown = null;
    if (method === "POST" && raw.length > 0) {
      try {
        body = JSON.parse(raw.toString("utf8"));
      } catch {
        log(`${method} ${url.pathname} -> 400 (body is not JSON)`);
        sendError(res, 400, "bad_request", "The request body is not valid JSON.");
        return;
      }
    }
    const q: IncomingRequest = { method, path: url.pathname, query: [...url.searchParams.entries()], body };
    const m = findRecording(top, q);
    if (!m) {
      const ask = describeAsk(q);
      log(`${ask} -> 404 no_recording`);
      sendError(
        res,
        404,
        "no_recording",
        `Replay agent (scenario ${top.name}): no recording for ${ask}. The replay agent only answers from recordings and never makes one up; record this exchange with EDIABAS_RECORD_DIR, scrub it and add it to the scenario.`,
        { scenario: top.name, method: q.method, path: q.path, query: Object.fromEntries(q.query), body: q.body },
      );
      return;
    }
    const note = m.candidates > 1 ? ` (${m.candidates} recordings matched; serving the latest)` : "";
    log(`${method} ${url.pathname} -> ${m.recording.response.status} from ${m.scenario}/${m.recording.file} [${m.how}]${note}`);
    serveRecording(res, m.recording.response);
  };

  const server = createServer((req, res) => {
    handle(req, res).catch((e: unknown) => {
      log(`internal error: ${e instanceof Error ? e.message : String(e)}`);
      if (!res.headersSent) sendError(res, 500, "internal_error", "Internal error in the replay agent.");
      else res.destroy();
    });
  });
  return new Promise((resolveStart, reject) => {
    server.once("error", reject);
    server.listen(opts.port, BIND_HOST, () => {
      const port = (server.address() as AddressInfo).port;
      resolveStart({
        url: `http://${BIND_HOST}:${port}`,
        port,
        server,
        close: () => new Promise<void>((done) => server.close(() => done())),
      });
    });
  });
}

// ---------------------------------------------------------------------------------------------
// Command line
// ---------------------------------------------------------------------------------------------

const USAGE = `usage: node dev/replay-agent.ts --scenario <name> [--port ${DEFAULT_PORT}] [--fixtures <dir>] [--token <token>]
  The token is --token, else EDIABAS_AGENT_TOKEN. Binds to ${BIND_HOST} only.
  Then run the MCP server with EDIABAS_AGENT_URL=http://${BIND_HOST}:<port> and the same token.`;

export interface CliOptions {
  scenario: string;
  port: number;
  fixtures: string;
  token: string;
}

export function parseArgs(argv: string[], env: Record<string, string | undefined>): CliOptions {
  const o: Partial<CliOptions> = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i]!;
    const value = (): string => {
      const v = argv[++i];
      if (v === undefined) fail(`${a} needs a value`);
      return v;
    };
    if (a === "--scenario") o.scenario = value();
    else if (a === "--fixtures") o.fixtures = resolve(value());
    else if (a === "--token") o.token = value();
    else if (a === "--port") {
      const p = value();
      if (!/^\d{1,5}$/.test(p) || Number(p) < 1 || Number(p) > 65535) fail(`--port must be 1-65535, got ${p}`);
      o.port = Number(p);
    } else fail(`unknown argument ${a}`);
  }
  if (!o.scenario) fail("--scenario is required");
  const token = o.token ?? env.EDIABAS_AGENT_TOKEN?.trim();
  if (!token) fail("no token: pass --token or set EDIABAS_AGENT_TOKEN");
  return { scenario: o.scenario, port: o.port ?? DEFAULT_PORT, fixtures: o.fixtures ?? DEFAULT_FIXTURES, token };
}

function countLayers(l: ScenarioLayer): string {
  const parts: string[] = [];
  for (let x: ScenarioLayer | null = l; x; x = x.base) parts.push(`${x.name} (${x.recordings.length} recordings, ${x.routes.length} routes)`);
  return parts.join(" over ");
}

async function main(): Promise<void> {
  let opts: CliOptions;
  let scenario: ScenarioLayer;
  try {
    opts = parseArgs(process.argv.slice(2), process.env);
    scenario = loadScenario(opts.fixtures, opts.scenario);
  } catch (e) {
    process.stderr.write(`replay-agent: ${e instanceof Error ? e.message : String(e)}\n${USAGE}\n`);
    process.exit(2);
  }
  const agent = await startReplayAgent({ scenario, token: opts.token, port: opts.port });
  process.stderr.write(`[replay-agent] scenario ${countLayers(scenario)}\n`);
  if (scenario.description) process.stderr.write(`[replay-agent] ${scenario.description}\n`);
  process.stderr.write(`[replay-agent] listening on ${agent.url} (Ctrl-C to stop)\n`);
  const stop = () => void agent.close().finally(() => process.exit(0));
  process.on("SIGINT", stop);
  process.on("SIGTERM", stop);
}

if (import.meta.main) {
  main().catch((e: unknown) => {
    process.stderr.write(`replay-agent: ${e instanceof Error ? e.message : String(e)}\n`);
    process.exit(1);
  });
}
