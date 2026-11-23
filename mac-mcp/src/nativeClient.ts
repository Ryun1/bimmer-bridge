/**
 * The native backend: `ediabasd serve` as a child process, JSON lines over stdio
 * (docs/api.md, "`ediabasd` stdio"). One request at a time; a request is never resent. If the
 * child exits or stops answering during a request that runs a job, the call fails with
 * `ediabas_request_lost` (it may have run on the car) and the next call starts a fresh child.
 */
import { spawn as nodeSpawn, type ChildProcess, type SpawnOptions } from "node:child_process";
import { createInterface } from "node:readline";
import { z } from "zod";
import {
  DescribeJobSchema,
  DescribeListSchema,
  EcusSchema,
  JobResultSchema,
  JobsResponseSchema,
  type DescribeJob,
  type DescribeList,
  type EcuList,
  type JobResult,
  type JobsResponse,
} from "./agentClient.js";
import type { EdiabasBackend, JobRequest } from "./backend.js";
import { engineBadResponse, engineError, engineRequestLost, engineUnavailable } from "./errors.js";

export const NativeHealthSchema = z.object({
  engine: z.string(),
  version: z.string(),
  kline: z.string(),
  ecuPath: z.string(),
  cable: z.object({
    state: z.string(),
    port: z.string().nullable().optional(),
    latency: z.string().nullable().optional(),
  }),
  policyVersion: z.number().int().nullable().optional(),
  policySha256: z.string().nullable().optional(),
});
export type NativeHealth = z.infer<typeof NativeHealthSchema>;

export type SpawnFn = (command: string, args: string[], options: SpawnOptions & { env: NodeJS.ProcessEnv }) => ChildProcess;

export interface NativeClientOptions {
  /** The ediabasd executable. */
  binary: string;
  /** Passed as `--config`; absent means ediabasd's own XDG default. */
  configPath?: string | undefined;
  /** Added to this process's environment for the child (DOTNET_ROOT, for one). */
  env?: Record<string, string> | undefined;
  /** For tests. */
  spawn?: SpawnFn | undefined;
  /** How long one request may take before the child is killed; default 75 s, as the VM client's. */
  requestTimeoutMs?: number | undefined;
  log: (m: string) => void;
}

const ANSWER = z.object({
  id: z.unknown(),
  ok: z.boolean(),
  result: z.unknown().optional(),
  error: z.object({ code: z.string(), message: z.string(), details: z.unknown().optional() }).nullable().optional(),
});
type Answer = z.infer<typeof ANSWER>;

const STDERR_KEEP = 2048;

interface Child {
  proc: ChildProcess;
  /** The answer for the request in flight, by id. */
  waiting: Map<number, (a: Answer) => void>;
  /** Settles (never rejects) when the child exits or fails; says why. */
  gone: Promise<string>;
  stderr: string;
}

export class NativeClient implements EdiabasBackend {
  readonly kind = "native" as const;
  private readonly opts: NativeClientOptions;
  private readonly spawnFn: SpawnFn;
  private readonly requestTimeoutMs: number;
  private child: Child | null = null;
  private nextId = 1;
  /** Settles when the last queued request has finished. */
  private tail: Promise<void> = Promise.resolve();

  constructor(opts: NativeClientOptions) {
    this.opts = opts;
    this.spawnFn = opts.spawn ?? ((c, a, o) => nodeSpawn(c, a, o));
    this.requestTimeoutMs = opts.requestTimeoutMs ?? 75_000;
  }

  health(): Promise<NativeHealth> {
    return this.request("health", {}, NativeHealthSchema, false);
  }

  listEcus(filter?: string): Promise<EcuList> {
    return this.request("ecus", filter === undefined ? {} : { filter }, EcusSchema, false);
  }

  describeJobs(q: { ecu: string; filter?: string | undefined; comments?: boolean | undefined }): Promise<DescribeList> {
    return this.request("describe", defined(q), DescribeListSchema, false);
  }

  describeJob(q: { ecu: string; job: string }): Promise<DescribeJob> {
    return this.request("describe", defined(q), DescribeJobSchema, false);
  }

  runJob(body: { ecu: string; job: string; args?: string | undefined; results?: string | undefined; timeoutSeconds?: number | undefined }): Promise<JobResult> {
    return this.request("job", defined(body), JobResultSchema, true);
  }

  runJobs(items: JobRequest[], timeoutSeconds?: number): Promise<JobsResponse> {
    const params = timeoutSeconds === undefined ? { items: items.map(defined) } : { items: items.map(defined), timeoutSeconds };
    return this.request("jobs", params, JobsResponseSchema, true);
  }

  /** Stops the child, if any. A later request starts a new one. */
  close(): void {
    const c = this.child;
    this.child = null;
    if (c) c.proc.kill("SIGKILL");
  }

  /** FIFO: each request waits for every request queued before it. */
  private request<T>(method: string, params: unknown, schema: z.ZodType<T>, runsJob: boolean): Promise<T> {
    const before = this.tail;
    let release!: () => void;
    this.tail = new Promise<void>((r) => (release = r));
    return before.then(() => this.send(method, params, schema, runsJob)).finally(release);
  }

  private async send<T>(method: string, params: unknown, schema: z.ZodType<T>, runsJob: boolean): Promise<T> {
    const child = await this.ensureChild();
    const id = this.nextId++;
    const what = method === "describe" || method === "job" ? `${method} ${describeTarget(params)}` : method;

    let timer: NodeJS.Timeout | undefined;
    const answered = new Promise<Answer>((resolve) => child.waiting.set(id, resolve));
    const timedOut = new Promise<"timeout">((resolve) => (timer = setTimeout(() => resolve("timeout"), this.requestTimeoutMs)));
    try {
      child.proc.stdin?.write(`${JSON.stringify({ id, method, params })}\n`);
      const outcome = await Promise.race([answered, child.gone, timedOut]);
      if (outcome === "timeout" || typeof outcome === "string") {
        const why = outcome === "timeout" ? `no answer within ${Math.round(this.requestTimeoutMs / 1000)} s` : outcome;
        this.opts.log(`ediabasd ${what} failed: ${why}${child.stderr ? `; stderr: ${child.stderr.slice(-300)}` : ""}`);
        this.drop(child);
        if (runsJob) throw engineRequestLost();
        throw engineUnavailable(this.opts.binary, why);
      }
      if (!outcome.ok) {
        const err = outcome.error;
        throw engineError(err?.code, err?.message, err?.details, method === "job" || method === "jobs");
      }
      const parsed = schema.safeParse(outcome.result);
      if (!parsed.success) {
        this.opts.log(`ediabasd ${what} failed: response does not match the contract`);
        throw engineBadResponse(what);
      }
      return parsed.data;
    } finally {
      clearTimeout(timer);
      child.waiting.delete(id);
    }
  }

  /** The running child, or a new one once it has started. Nothing is written before then. */
  private ensureChild(): Promise<Child> {
    if (this.child) return Promise.resolve(this.child);
    const binary = this.opts.binary;
    const args = ["serve", ...(this.opts.configPath ? ["--config", this.opts.configPath] : [])];
    let proc: ChildProcess;
    try {
      proc = this.spawnFn(binary, args, { stdio: ["pipe", "pipe", "pipe"], env: { ...process.env, ...this.opts.env } });
    } catch (e) {
      return Promise.reject(engineUnavailable(binary, e instanceof Error ? e.message : String(e)));
    }

    let markGone!: (why: string) => void;
    const child: Child = { proc, waiting: new Map(), gone: new Promise<string>((r) => (markGone = r)), stderr: "" };

    createInterface({ input: proc.stdout! }).on("line", (line) => {
      let parsed: unknown;
      try {
        parsed = JSON.parse(line);
      } catch {
        this.opts.log(`ediabasd wrote a line that is not JSON: ${line.slice(0, 200)}`);
        return;
      }
      const a = ANSWER.safeParse(parsed);
      if (!a.success || typeof a.data.id !== "number") {
        this.opts.log(`ediabasd wrote a line that is not an answer: ${line.slice(0, 200)}`);
        return;
      }
      child.waiting.get(a.data.id)?.(a.data);
    });
    proc.stderr?.on("data", (d: Buffer) => {
      child.stderr = (child.stderr + d.toString("utf8")).slice(-STDERR_KEEP);
    });
    proc.stdin?.on("error", () => {
      // EPIPE when the child has gone; its exit settles the request.
    });
    proc.on("exit", (code, signal) => {
      markGone(signal ? `ediabasd was stopped (${signal})` : `ediabasd exited with code ${code}`);
      if (this.child === child) this.child = null;
    });

    return new Promise<Child>((resolve, reject) => {
      proc.once("spawn", () => {
        this.child = child;
        resolve(child);
      });
      proc.once("error", (e) => {
        markGone(`ediabasd failed: ${e.message}`);
        if (this.child === child) this.child = null;
        reject(engineUnavailable(binary, e.message));
      });
    });
  }

  private drop(child: Child): void {
    if (this.child === child) this.child = null;
    child.proc.kill("SIGKILL");
  }
}

/** The object without its undefined members, so they are not sent as nulls or at all. */
function defined<T extends object>(o: T): T {
  return Object.fromEntries(Object.entries(o).filter(([, v]) => v !== undefined)) as T;
}

function describeTarget(params: unknown): string {
  const p = params as { ecu?: unknown; job?: unknown };
  return [p.ecu, p.job].filter((x) => typeof x === "string").join(" ");
}
