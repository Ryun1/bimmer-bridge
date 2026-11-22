import { z } from "zod";
import type { Config } from "./config.js";
import {
  BridgeError,
  agentError,
  agentNotAnswering,
  ediabasBusy,
  ediabasRequestLost,
  agentOutdated,
  badResponse,
  forbidden,
  localNetworkBlocked,
  unauthorized,
  vmNotRunning,
  wrongService,
} from "./errors.js";
import type { ExchangeRecorder } from "./recorder.js";
import { VmLocator, bridgeSubnets, hostPort, isOnLink, type CallContext, type DiscoverySources } from "./vmAddress.js";

export const AGENT_ID = "ediabas-bridge-agent";

// ---------------------------------------------------------------------------
// Response schemas (docs/api.md). Unknown extra properties are ignored so the agent can
// add fields without breaking this client.
// ---------------------------------------------------------------------------

export const HealthSchema = z.object({
  agent: z.literal(AGENT_ID),
  version: z.string(),
  startedAt: z.string().nullable(),
  uptimeSeconds: z.number().nonnegative(),
  os: z.object({
    description: z.string().nullable(),
    osArchitecture: z.string().nullable(),
    processArchitecture: z.string().nullable(),
  }),
  user: z.string().nullable(),
  elevated: z.boolean(),
  sessionId: z.number().int(),
  bindAddress: z.string().nullable(),
  port: z.number().int().nullable(),
  gateway: z.string().nullable(),
  conflictingProcesses: z.array(z.object({ name: z.string(), pid: z.number().int() })),
  // `loaded`: whether the agent has the DLL loaded right now (idle release); older agents omit it.
  // `stuck`: a job did not stop after apiBreak and the agent must be restarted; older agents omit it.
  // `heldOpen`: apiEnd threw, so EDIABAS stays loaded until the agent restarts; older agents omit it.
  ediabas: z.object({
    dllPath: z.string().nullable(),
    dllFound: z.boolean(),
    loaded: z.boolean().nullable().optional(),
    stuck: z.boolean().nullable().optional(),
    heldOpen: z.boolean().nullable().optional(),
  }),
  lastCableUseAt: z.string().nullable(),
  // Phase 2 additions; an older agent omits them.
  policyVersion: z.number().int().nullable().optional(),
  // SHA-256 (hex) of the agent's embedded policy.json; absent from an older agent.
  policySha256: z.string().nullable().optional(),
  capabilities: z.array(z.string()).nullable().optional(),
  // Distinct reviewedJobs entries: pinned ones, and ignored ones (unpinned or malformed; they
  // approve nothing). A 0.4.0 agent sends `unpinned` instead, which it still honours; older
  // agents omit the field.
  approvals: z
    .union([
      z.object({
        pinned: z.number().int().nonnegative(),
        ignored: z.number().int().nonnegative(),
        // Absent from an agent that predates the Mac's change watch.
        entries: z.array(z.string()).optional(),
        hash: z.string().optional(),
      }),
      z.object({ pinned: z.number().int().nonnegative(), unpinned: z.number().int().nonnegative() }),
    ])
    .nullable()
    .optional(),
});
export type Health = z.infer<typeof HealthSchema>;

export const WindowInfoSchema = z.object({
  handle: z.string(),
  title: z.string(),
  processName: z.string().nullable(),
  pid: z.number().int(),
  bounds: z.object({ x: z.number(), y: z.number(), width: z.number(), height: z.number() }),
  minimized: z.boolean(),
});
export const WindowsSchema = z.object({ windows: z.array(WindowInfoSchema) });
export type WindowList = z.infer<typeof WindowsSchema>;

// `details` may be absent (older agent) or null.
const ErrorInfoSchema = z.object({ code: z.string(), message: z.string(), details: z.unknown().optional() });
const ErrorBodySchema = z.object({ error: ErrorInfoSchema });
export type AgentErrorInfo = z.infer<typeof ErrorInfoSchema>;

/** A result set: field name to value (string, number, hex string or null). */
const ResultSet = z.record(z.string(), z.unknown());

export const JobResultSchema = z.object({
  ecu: z.string(),
  job: z.string(),
  variant: z.string().nullable().optional(),
  jobStatus: z.string().nullable().optional(),
  systemResults: ResultSet.nullable().optional(),
  sets: z.array(ResultSet),
  durationMs: z.number().nullable().optional(),
});
export type JobResult = z.infer<typeof JobResultSchema>;

export const BatchItemSchema = z.object({
  ok: z.boolean(),
  result: JobResultSchema.nullable().optional(),
  error: ErrorInfoSchema.nullable().optional(),
});
export type BatchItem = z.infer<typeof BatchItemSchema>;

export const JobsResponseSchema = z.object({ items: z.array(BatchItemSchema), durationMs: z.number().nullable().optional() });
export type JobsResponse = z.infer<typeof JobsResponseSchema>;

export const EcusSchema = z.object({
  ecuPath: z.string().nullable(),
  count: z.number().int(),
  truncated: z.boolean(),
  files: z.array(z.object({ name: z.string(), type: z.string() })),
});
export type EcuList = z.infer<typeof EcusSchema>;

const VerdictSchema = z.object({ allowed: z.boolean(), rule: z.string(), review: z.string() });
export type AgentVerdict = z.infer<typeof VerdictSchema>;

export const DescribeListSchema = z.object({
  ecu: z.string(),
  variant: z.string().nullable().optional(),
  jobCount: z.number().int().nullable().optional(),
  jobs: z.array(z.object({ name: z.string(), comment: z.string().nullable().optional(), verdict: VerdictSchema })),
  durationMs: z.number().nullable().optional(),
});
export type DescribeList = z.infer<typeof DescribeListSchema>;

const ParamSchema = z.object({ name: z.string(), type: z.string().nullable().optional(), comment: z.string().nullable().optional() });

export const DescribeJobSchema = z.object({
  ecu: z.string(),
  variant: z.string().nullable().optional(),
  job: z.string(),
  comment: z.string().nullable().optional(),
  // 12 lower-case hex digits, what a pinned reviewedJobs entry holds; older agents omit it.
  descriptionHash: z.string().nullable().optional(),
  verdict: VerdictSchema,
  arguments: z.array(ParamSchema),
  results: z.array(ParamSchema),
  durationMs: z.number().nullable().optional(),
});
export type DescribeJob = z.infer<typeof DescribeJobSchema>;

export const LogListSchema = z.object({
  roots: z.array(z.object({ index: z.number().int(), path: z.string(), exists: z.boolean() })),
  files: z.array(
    z.object({ path: z.string(), root: z.number().int().nullable().optional(), sizeBytes: z.number(), modifiedAt: z.string().nullable() }),
  ),
  truncated: z.boolean(),
});
export type LogList = z.infer<typeof LogListSchema>;

export const LogReadSchema = z.object({
  path: z.string(),
  sizeBytes: z.number(),
  modifiedAt: z.string().nullable(),
  offset: z.number(),
  length: z.number(),
  encoding: z.string().nullable(),
  truncated: z.boolean(),
  grep: z.object({ pattern: z.string(), matchedLines: z.number().int() }).nullable().optional(),
  text: z.string(),
});
export type LogRead = z.infer<typeof LogReadSchema>;

export interface Capture {
  mimeType: "image/jpeg" | "image/png";
  data: Uint8Array;
  title: string | null;
  handle: string | null;
  method: string | null;
  width: number | null;
  height: number | null;
}
const AgentIdSchema = z.object({ agent: z.string() });

function isAgentErrorBody(body: string): boolean {
  try {
    return ErrorBodySchema.safeParse(JSON.parse(body)).success;
  } catch {
    return false;
  }
}

// ---------------------------------------------------------------------------

export interface TokenSource {
  get(): Promise<string>;
  refresh(): Promise<string>;
}

export interface AgentClientOptions {
  config: Config;
  tokens: TokenSource;
  sources: DiscoverySources;
  fetch?: typeof fetch | undefined;
  /** Per-request timeout. Default 15 s. */
  requestTimeoutMs?: number | undefined;
  /** Per-request timeout for /ediabas/* calls. Default 75 s. */
  ediabasTimeoutMs?: number | undefined;
  /**
   * How long an /ediabas/* call may wait behind another one on this Mac before it is answered
   * `ediabas_busy` without being sent. Default 120 s.
   */
  ediabasQueueWaitMs?: number | undefined;
  /** Timeout for discovery probes. Default 3 s. */
  probeTimeoutMs?: number | undefined;
  /** Diagnostics (stderr). Never receives the token. */
  log?: ((message: string) => void) | undefined;
  /** Recording mode (EDIABAS_RECORD_DIR). */
  recorder?: ExchangeRecorder | undefined;
}

type FailureKind = "unreachable" | "refused" | "timeout";

/** A transport-level failure (no HTTP response). */
class ConnectionFailure extends Error {
  constructor(
    readonly kind: FailureKind,
    /** Underlying error code or name, e.g. EHOSTUNREACH, TimeoutError. */
    readonly detail: string,
    /** The connection was never made, so the request cannot have reached the agent. */
    readonly neverSent = false,
  ) {
    super(`connection failure: ${kind}`);
  }
}

const UNREACHABLE = new Set(["EHOSTDOWN", "EHOSTUNREACH", "ENETUNREACH", "ENETDOWN", "EADDRNOTAVAIL"]);

function classify(e: unknown): ConnectionFailure {
  const err = e as { name?: string; code?: string; cause?: { code?: string; name?: string; syscall?: string } } | undefined;
  const name = err?.name ?? "";
  if (name === "TimeoutError" || name === "AbortError") return new ConnectionFailure("timeout", name);
  if (err?.cause?.name === "TimeoutError") return new ConnectionFailure("timeout", "TimeoutError");
  const code = String(err?.cause?.code ?? err?.code ?? "");
  const detail = code || err?.cause?.name || name || "unknown";
  // A connect timeout means the TCP handshake never completed: nothing was sent, and the VM may
  // be on another address now. ETIMEDOUT can also come after the request was written.
  if (code === "UND_ERR_CONNECT_TIMEOUT") return new ConnectionFailure("unreachable", detail, true);
  if (code === "ETIMEDOUT") return new ConnectionFailure("timeout", detail);
  // EHOSTUNREACH and friends can also come after the body was written (syscall "write"), so
  // they prove nothing was sent only when the connect itself failed.
  if (UNREACHABLE.has(code)) return new ConnectionFailure("unreachable", detail, err?.cause?.syscall === "connect");
  // Only ECONNREFUSED proves nothing was sent. A reset or "other side closed" (ECONNRESET,
  // UND_ERR_SOCKET, EPIPE) can come after the agent received the request.
  return new ConnectionFailure("refused", detail, code === "ECONNREFUSED");
}

type QueryValue = string | number | boolean | undefined;

/** One agent request. */
interface RequestSpec {
  method: "GET" | "POST";
  path: string;
  query?: Record<string, QueryValue>;
  body?: unknown;
  /**
   * /ediabas/*: sent one at a time (the agent runs them one at a time, and a request waiting
   * in its queue would use up this side's timeout), the long timeout, and never re-sent after
   * a timeout (the job may have run).
   */
  ediabas?: boolean;
  /**
   * Runs an EDIABAS job in the VM (job, jobs, describe): re-sent only when the
   * connection was never made, never after a failure that may have followed delivery.
   */
  runsJob?: boolean;
}

interface RawResponse {
  status: number;
  bytes: Uint8Array;
  headers: Record<string, string>;
  token: string;
}

const HEALTH: RequestSpec = { method: "GET", path: "/health" };

/** 403 codes that are agent decisions, not the gateway check. */
const AGENT_403_CODES = new Set(["job_not_allowed", "job_needs_review", "path_not_allowed"]);

function definedQuery(q: Record<string, QueryValue> | undefined): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(q ?? {})) if (v !== undefined) out[k] = String(v);
  return out;
}

function describeRequest(spec: RequestSpec): string {
  return `${spec.method} ${spec.path}`;
}

function intHeader(v: string | undefined): number | null {
  if (v === undefined || !/^\d+$/.test(v.trim())) return null;
  return Number(v.trim());
}

function decodeTitle(v: string | undefined): string | null {
  if (v === undefined) return null;
  try {
    return decodeURIComponent(v);
  } catch {
    return v;
  }
}

/** HTTP client for the VM agent. Every method throws BridgeError with an actionable message. */
export class AgentClient {
  private readonly locator: VmLocator;
  private readonly fetchFn: typeof fetch;
  private readonly requestTimeoutMs: number;
  private readonly ediabasTimeoutMs: number;
  private readonly probeTimeoutMs: number;
  private readonly ediabasQueueWaitMs: number;
  private readonly log: (message: string) => void;
  /** Settles when the last queued /ediabas/* call has finished. */
  private ediabasTail: Promise<void> = Promise.resolve();

  constructor(private readonly opts: AgentClientOptions) {
    this.fetchFn = opts.fetch ?? globalThis.fetch;
    this.requestTimeoutMs = opts.requestTimeoutMs ?? 15_000;
    this.ediabasTimeoutMs = opts.ediabasTimeoutMs ?? 75_000;
    this.probeTimeoutMs = opts.probeTimeoutMs ?? 3_000;
    this.ediabasQueueWaitMs = opts.ediabasQueueWaitMs ?? 120_000;
    this.log = opts.log ?? (() => {});
    this.locator = new VmLocator(opts.config, opts.sources, (baseUrl, ctx) => this.probe(baseUrl, ctx), this.log);
  }

  health(): Promise<Health> {
    return this.json(HEALTH, HealthSchema);
  }

  listWindows(): Promise<WindowList> {
    return this.json({ method: "GET", path: "/windows" }, WindowsSchema);
  }

  capture(o: { window?: string | undefined; maxEdge?: number | undefined } = {}): Promise<Capture> {
    const spec: RequestSpec = { method: "GET", path: "/screen/capture", query: { window: o.window, maxEdge: o.maxEdge } };
    return this.call(spec, (res) => {
      const type = (res.headers["content-type"] ?? "").split(";")[0]?.trim().toLowerCase();
      if ((type !== "image/jpeg" && type !== "image/png") || res.bytes.length === 0) {
        this.log(`request GET /screen/capture failed: not an image (${type || "no content-type"})`);
        throw badResponse("GET /screen/capture");
      }
      return {
        mimeType: type,
        data: res.bytes,
        title: decodeTitle(res.headers["x-window-title"]),
        handle: res.headers["x-window-handle"] ?? null,
        method: res.headers["x-capture-method"] ?? null,
        width: intHeader(res.headers["x-image-width"]),
        height: intHeader(res.headers["x-image-height"]),
      };
    });
  }

  listLogs(q: { root?: number | undefined; glob?: string | undefined; since?: string | undefined; limit?: number | undefined } = {}): Promise<LogList> {
    return this.json({ method: "GET", path: "/logs", query: q }, LogListSchema);
  }

  readLog(q: {
    path: string;
    tailBytes?: number | undefined;
    offset?: number | undefined;
    length?: number | undefined;
    grep?: string | undefined;
  }): Promise<LogRead> {
    return this.json({ method: "GET", path: "/logs/read", query: q }, LogReadSchema);
  }

  listEcus(filter?: string): Promise<EcuList> {
    return this.json({ method: "GET", path: "/ediabas/ecus", query: { filter }, ediabas: true }, EcusSchema);
  }

  /** `comments: false`: names only, no `_JOBCOMMENTS` per job (verdicts then say `not_checked`). */
  describeJobs(q: { ecu: string; filter?: string | undefined; comments?: boolean | undefined }): Promise<DescribeList> {
    return this.json({ method: "GET", path: "/ediabas/describe", query: q, ediabas: true, runsJob: true }, DescribeListSchema);
  }

  describeJob(q: { ecu: string; job: string }): Promise<DescribeJob> {
    return this.json({ method: "GET", path: "/ediabas/describe", query: q, ediabas: true, runsJob: true }, DescribeJobSchema);
  }

  runJob(body: {
    ecu: string;
    job: string;
    args?: string | undefined;
    results?: string | undefined;
    timeoutSeconds?: number | undefined;
  }): Promise<JobResult> {
    return this.json({ method: "POST", path: "/ediabas/job", body, ediabas: true, runsJob: true }, JobResultSchema);
  }

  /** 1..20 items; the caller chunks. `timeoutSeconds` applies to each item. */
  runJobs(
    items: { ecu: string; job: string; args?: string | undefined; results?: string | undefined }[],
    timeoutSeconds?: number,
  ): Promise<JobsResponse> {
    return this.json({ method: "POST", path: "/ediabas/jobs", body: timeoutSeconds === undefined ? { items } : { items, timeoutSeconds }, ediabas: true, runsJob: true }, JobsResponseSchema);
  }

  private json<T>(spec: RequestSpec, schema: z.ZodType<T>): Promise<T> {
    return this.call(spec, (res) => {
      const what = describeRequest(spec);
      let parsed: unknown;
      try {
        parsed = JSON.parse(Buffer.from(res.bytes).toString("utf8"));
      } catch {
        this.log(`request ${what} failed: response is not JSON`);
        throw badResponse(what);
      }
      const result = schema.safeParse(parsed);
      if (!result.success) {
        this.log(`request ${what} failed: response does not match the contract`);
        throw badResponse(what);
      }
      return result.data;
    });
  }

  private call<T>(spec: RequestSpec, parse: (res: RawResponse) => T): Promise<T> {
    return spec.ediabas ? this.queued(() => this.callNow(spec, parse)) : this.callNow(spec, parse);
  }

  /**
   * Runs `fn` after every /ediabas/* call queued before it (FIFO). A call that waits longer
   * than the limit is answered `ediabas_busy` and never sent. The request timeout starts
   * inside `fn`, so time spent waiting here does not count against it.
   */
  private async queued<T>(fn: () => Promise<T>): Promise<T> {
    const before = this.ediabasTail;
    let release!: () => void;
    const mine = new Promise<void>((resolve) => (release = resolve));
    this.ediabasTail = before.then(() => mine);
    let timer: ReturnType<typeof setTimeout> | undefined;
    const turn = await Promise.race([
      before.then(() => true),
      new Promise<boolean>((resolve) => (timer = setTimeout(() => resolve(false), this.ediabasQueueWaitMs))),
    ]);
    clearTimeout(timer);
    if (!turn) {
      release();
      this.log("an EDIABAS request waited too long behind another one and was not sent");
      throw ediabasBusy();
    }
    try {
      return await fn();
    } finally {
      release();
    }
  }

  private async callNow<T>(spec: RequestSpec, parse: (res: RawResponse) => T): Promise<T> {
    const ctx: CallContext = { tokenRefreshed: false };
    const first = await this.locator.resolve(ctx);
    try {
      return await this.request(first.baseUrl, spec, ctx, parse);
    } catch (e) {
      if (!(e instanceof ConnectionFailure)) throw e;
      this.locator.invalidate();
      // A job request that may have reached the agent may have run: never send it twice.
      if (spec.runsJob && !e.neverSent) throw ediabasRequestLost();
      // An EDIABAS call that timed out may still be running in the VM: never send it twice.
      if (!first.fromCache || (spec.ediabas && e.kind === "timeout")) throw this.connectionError(e, first.baseUrl);
    }
    // The cached address stopped answering: resolve once more, then give up.
    const second = await this.locator.resolve(ctx);
    try {
      return await this.request(second.baseUrl, spec, ctx, parse);
    } catch (e) {
      if (!(e instanceof ConnectionFailure)) throw e;
      this.locator.invalidate();
      if (spec.runsJob && !e.neverSent) throw ediabasRequestLost();
      throw this.connectionError(e, second.baseUrl);
    }
  }

  /**
   * Map a transport failure. "Unreachable" at an address that is on-link on an up bridge
   * interface means UTM's network is up (a VM is running) yet the connect was refused locally:
   * typically macOS Local Network privacy blocking this process, or the VM still starting.
   */
  private connectionError(e: ConnectionFailure, baseUrl: string): BridgeError {
    if (e.kind !== "unreachable") return agentNotAnswering();
    let host = "";
    try {
      host = new URL(baseUrl).hostname;
    } catch {
      /* invalid URL: treat as off-link */
    }
    let onLink = false;
    try {
      onLink = isOnLink(host, bridgeSubnets(this.opts.sources.networkInterfaces()));
    } catch {
      /* interface lookup failed: fall back */
    }
    return onLink ? localNetworkBlocked() : vmNotRunning(this.opts.config.vmName);
  }

  /** Discovery check: 200 from /health with exactly the agent's id. */
  private async probe(baseUrl: string, ctx: CallContext): Promise<void> {
    let res: RawResponse;
    try {
      res = await this.send(baseUrl, HEALTH, ctx, this.probeTimeoutMs, "probe");
    } catch (e) {
      if (e instanceof ConnectionFailure) throw this.connectionError(e, baseUrl);
      throw e;
    }
    const body = Buffer.from(res.bytes).toString("utf8");
    if (res.status !== 200) {
      this.log(`probe ${hostPort(baseUrl)} failed: HTTP ${res.status}`);
      // A non-2xx without the agent's error shape came from some other service.
      if (res.status !== 401 && res.status !== 403 && !isAgentErrorBody(body)) throw wrongService(baseUrl);
      throw this.httpError(res.status, body, res.token, HEALTH);
    }
    let parsed: unknown;
    try {
      parsed = JSON.parse(body);
    } catch {
      this.log(`probe ${hostPort(baseUrl)} failed: not the agent (200 without JSON)`);
      throw wrongService(baseUrl);
    }
    const id = AgentIdSchema.safeParse(parsed);
    if (!id.success || id.data.agent !== AGENT_ID) {
      this.log(`probe ${hostPort(baseUrl)} failed: not the agent (wrong or missing "agent" field)`);
      throw wrongService(baseUrl);
    }
  }

  private async request<T>(baseUrl: string, spec: RequestSpec, ctx: CallContext, parse: (res: RawResponse) => T): Promise<T> {
    const timeout = spec.ediabas ? this.ediabasTimeoutMs : this.requestTimeoutMs;
    const res = await this.send(baseUrl, spec, ctx, timeout, "request");
    if (res.status !== 200) {
      this.log(`request ${hostPort(baseUrl)} ${describeRequest(spec)} failed: HTTP ${res.status}`);
      throw this.httpError(res.status, Buffer.from(res.bytes).toString("utf8"), res.token, spec);
    }
    return parse(res);
  }

  /**
   * One authenticated request. After a 401 the token is re-read (it may have just been
   * installed) and the request repeated, at most once per tool call.
   */
  private async send(baseUrl: string, spec: RequestSpec, ctx: CallContext, timeoutMs: number, label: "probe" | "request"): Promise<RawResponse> {
    let token = await this.opts.tokens.get();
    const attempt = async (t: string) => {
      try {
        return await this.fetchOnce(baseUrl, spec, t, timeoutMs);
      } catch (e) {
        if (e instanceof ConnectionFailure) {
          const what = label === "request" ? `request ${hostPort(baseUrl)} ${describeRequest(spec)}` : `probe ${hostPort(baseUrl)}`;
          this.log(`${what} failed: ${e.kind} (${e.detail})`);
        }
        throw e;
      }
    };
    let res = await attempt(token);
    if (res.status === 401 && !ctx.tokenRefreshed) {
      ctx.tokenRefreshed = true;
      const fresh = await this.opts.tokens.refresh();
      if (fresh !== token) {
        token = fresh;
        res = await attempt(token);
      }
    }
    return { ...res, token };
  }

  private async fetchOnce(
    baseUrl: string,
    spec: RequestSpec,
    token: string,
    timeoutMs: number,
  ): Promise<{ status: number; bytes: Uint8Array; headers: Record<string, string> }> {
    const query = definedQuery(spec.query);
    const qs = new URLSearchParams(query).toString();
    const url = baseUrl + spec.path + (qs ? `?${qs}` : "");
    const headers: Record<string, string> = { authorization: `Bearer ${token}`, accept: "application/json" };
    let body: string | undefined;
    if (spec.body !== undefined) {
      headers["content-type"] = "application/json; charset=utf-8";
      body = JSON.stringify(spec.body);
    }
    let out: { status: number; bytes: Uint8Array; headers: Record<string, string> };
    try {
      const res = await this.fetchFn(url, {
        method: spec.method,
        headers,
        body,
        redirect: "manual",
        signal: AbortSignal.timeout(timeoutMs),
      });
      const bytes = new Uint8Array(await res.arrayBuffer());
      const kept: Record<string, string> = {};
      res.headers.forEach((value, name) => {
        const n = name.toLowerCase();
        if (n === "content-type" || n.startsWith("x-")) kept[n] = value;
      });
      out = { status: res.status, bytes, headers: kept };
    } catch (e) {
      throw classify(e);
    }
    if (this.opts.recorder) {
      // The token is not part of the exchange: no request headers are recorded.
      await this.opts.recorder.record({
        time: new Date(),
        method: spec.method,
        path: spec.path,
        query,
        requestBody: spec.body ?? null,
        status: out.status,
        responseHeaders: out.headers,
        body: out.bytes,
      });
    }
    return out;
  }

  private httpError(status: number, body: string, token: string, spec: RequestSpec): BridgeError {
    if (status === 401) return unauthorized(this.opts.config.agentUrl !== undefined);
    let info: AgentErrorInfo | undefined;
    try {
      const parsed = ErrorBodySchema.safeParse(JSON.parse(body));
      if (parsed.success) info = parsed.data.error;
    } catch {
      /* not JSON: report the status only */
    }
    if (status === 403 && !(info && AGENT_403_CODES.has(info.code))) return forbidden();
    // The agent knows nothing at this path: it is an older build than this server.
    if (status === 404 && info?.code === "not_found") return agentOutdated(spec.path);
    const redact = (s: string | undefined) => (s && token ? s.split(token).join("[redacted]") : s);
    return agentError(status, redact(info?.code), redact(info?.message), info?.details, spec.path !== "/ediabas/describe");
  }
}
