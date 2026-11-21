import { z } from "zod";
import type { Config } from "./config.js";
import {
  BridgeError,
  agentError,
  agentNotAnswering,
  badResponse,
  forbidden,
  unauthorized,
  vmNotRunning,
  wrongService,
} from "./errors.js";
import { VmLocator, type CallContext, type DiscoverySources } from "./vmAddress.js";

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
  ediabas: z.object({ dllPath: z.string().nullable(), dllFound: z.boolean() }),
  lastCableUseAt: z.string().nullable(),
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

const ErrorBodySchema = z.object({ error: z.object({ code: z.string(), message: z.string() }) });
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
  /** Timeout for discovery probes. Default 3 s. */
  probeTimeoutMs?: number | undefined;
}

type FailureKind = "unreachable" | "refused" | "timeout";

/** A transport-level failure (no HTTP response). */
class ConnectionFailure extends Error {
  constructor(readonly kind: FailureKind) {
    super(`connection failure: ${kind}`);
  }
}

const UNREACHABLE = new Set(["EHOSTDOWN", "EHOSTUNREACH", "ENETUNREACH", "ENETDOWN", "EADDRNOTAVAIL"]);

function classify(e: unknown): ConnectionFailure {
  const err = e as { name?: string; code?: string; cause?: { code?: string; name?: string } } | undefined;
  const name = err?.name ?? "";
  if (name === "TimeoutError" || name === "AbortError" || err?.cause?.name === "TimeoutError") return new ConnectionFailure("timeout");
  const code = err?.cause?.code ?? err?.code ?? "";
  if (code === "UND_ERR_CONNECT_TIMEOUT" || code === "ETIMEDOUT") return new ConnectionFailure("timeout");
  if (UNREACHABLE.has(code)) return new ConnectionFailure("unreachable");
  return new ConnectionFailure("refused");
}

/** HTTP client for the VM agent. Every method throws BridgeError with an actionable message. */
export class AgentClient {
  private readonly locator: VmLocator;
  private readonly fetchFn: typeof fetch;
  private readonly requestTimeoutMs: number;
  private readonly probeTimeoutMs: number;

  constructor(private readonly opts: AgentClientOptions) {
    this.fetchFn = opts.fetch ?? globalThis.fetch;
    this.requestTimeoutMs = opts.requestTimeoutMs ?? 15_000;
    this.probeTimeoutMs = opts.probeTimeoutMs ?? 3_000;
    this.locator = new VmLocator(opts.config, opts.sources, (baseUrl, ctx) => this.probe(baseUrl, ctx));
  }

  health(): Promise<Health> {
    return this.call("/health", HealthSchema);
  }

  listWindows(): Promise<WindowList> {
    return this.call("/windows", WindowsSchema);
  }

  private async call<T>(path: string, schema: z.ZodType<T>): Promise<T> {
    const ctx: CallContext = { tokenRefreshed: false };
    const first = await this.locator.resolve(ctx);
    try {
      return await this.request(first.baseUrl, path, schema, ctx);
    } catch (e) {
      if (!(e instanceof ConnectionFailure)) throw e;
      this.locator.invalidate();
      if (!first.fromCache) throw this.connectionError(e);
    }
    // The cached address stopped answering: resolve once more, then give up.
    const second = await this.locator.resolve(ctx);
    try {
      return await this.request(second.baseUrl, path, schema, ctx);
    } catch (e) {
      if (!(e instanceof ConnectionFailure)) throw e;
      this.locator.invalidate();
      throw this.connectionError(e);
    }
  }

  private connectionError(e: ConnectionFailure): BridgeError {
    return e.kind === "unreachable" ? vmNotRunning(this.opts.config.vmName) : agentNotAnswering();
  }

  /** Discovery check: 200 from /health with exactly the agent's id. */
  private async probe(baseUrl: string, ctx: CallContext): Promise<void> {
    let res: { status: number; body: string; token: string };
    try {
      res = await this.send(baseUrl, "/health", ctx, this.probeTimeoutMs);
    } catch (e) {
      if (e instanceof ConnectionFailure) throw this.connectionError(e);
      throw e;
    }
    if (res.status !== 200) {
      // A non-2xx without the agent's error shape came from some other service.
      if (res.status !== 401 && res.status !== 403 && !isAgentErrorBody(res.body)) throw wrongService(baseUrl);
      throw this.httpError(res.status, res.body, res.token);
    }
    let parsed: unknown;
    try {
      parsed = JSON.parse(res.body);
    } catch {
      throw wrongService(baseUrl);
    }
    const id = AgentIdSchema.safeParse(parsed);
    if (!id.success || id.data.agent !== AGENT_ID) throw wrongService(baseUrl);
  }

  private async request<T>(baseUrl: string, path: string, schema: z.ZodType<T>, ctx: CallContext): Promise<T> {
    const res = await this.send(baseUrl, path, ctx, this.requestTimeoutMs);
    if (res.status !== 200) throw this.httpError(res.status, res.body, res.token);
    let parsed: unknown;
    try {
      parsed = JSON.parse(res.body);
    } catch {
      throw badResponse(`GET ${path}`);
    }
    const result = schema.safeParse(parsed);
    if (!result.success) throw badResponse(`GET ${path}`);
    return result.data;
  }

  /**
   * One authenticated GET. After a 401 the token is re-read (it may have just been installed)
   * and the request repeated, at most once per tool call.
   */
  private async send(
    baseUrl: string,
    path: string,
    ctx: CallContext,
    timeoutMs: number,
  ): Promise<{ status: number; body: string; token: string }> {
    let token = await this.opts.tokens.get();
    let res = await this.fetchOnce(baseUrl + path, token, timeoutMs);
    if (res.status === 401 && !ctx.tokenRefreshed) {
      ctx.tokenRefreshed = true;
      const fresh = await this.opts.tokens.refresh();
      if (fresh !== token) {
        token = fresh;
        res = await this.fetchOnce(baseUrl + path, token, timeoutMs);
      }
    }
    return { ...res, token };
  }

  private async fetchOnce(url: string, token: string, timeoutMs: number): Promise<{ status: number; body: string }> {
    try {
      const res = await this.fetchFn(url, {
        method: "GET",
        headers: { authorization: `Bearer ${token}`, accept: "application/json" },
        redirect: "manual",
        signal: AbortSignal.timeout(timeoutMs),
      });
      const body = await res.text();
      return { status: res.status, body };
    } catch (e) {
      throw classify(e);
    }
  }

  private httpError(status: number, body: string, token: string): BridgeError {
    if (status === 401) return unauthorized();
    if (status === 403) return forbidden();
    let code: string | undefined;
    let message: string | undefined;
    try {
      const parsed = ErrorBodySchema.safeParse(JSON.parse(body));
      if (parsed.success) ({ code, message } = parsed.data.error);
    } catch {
      /* not JSON: report the status only */
    }
    const redact = (s: string | undefined) => (s && token ? s.split(token).join("[redacted]") : s);
    return agentError(status, redact(code), redact(message));
  }
}
