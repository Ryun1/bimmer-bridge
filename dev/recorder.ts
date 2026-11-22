import { mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";

/**
 * Recording (docs/development.md "Recording and scrubbing"): dev/record-server.ts wraps the
 * MCP server's `fetch` so every agent exchange is written to a folder as one JSON file, images
 * as a separate file next to it. The token and request headers are never part of an exchange,
 * and `GET /hello` is never recorded (its proof is derived from the token; the replay agent
 * computes it live). dev/replay-agent.ts serves a scrubbed copy of such a folder. This is dev
 * tooling: the production server has no recording code.
 */

export interface Exchange {
  time: Date;
  method: string;
  /** Path without the query string, e.g. `/ediabas/job`. */
  path: string;
  /** Query parameters as sent. */
  query: Record<string, string>;
  /** The JSON request body, or null for a GET. */
  requestBody: unknown;
  status: number;
  /** `content-type` and the agent's `x-*` headers, lower-cased names. */
  responseHeaders: Record<string, string>;
  /** Response body bytes. */
  body: Uint8Array;
}

export interface ExchangeRecorder {
  /** Never throws. */
  record(x: Exchange): Promise<void>;
}

function stamp(d: Date): string {
  return d.toISOString().replace(/[-:.]/g, "");
}

function slug(s: string, max = 40): string {
  return s.replace(/[^A-Za-z0-9_]+/g, "_").replace(/^_+|_+$/g, "").slice(0, max);
}

function imageExtension(contentType: string | undefined): string | null {
  const t = (contentType ?? "").split(";")[0]?.trim().toLowerCase();
  if (t === "image/jpeg") return "jpg";
  if (t === "image/png") return "png";
  return null;
}

/** `ecu` and `job` from a request body or query, for a readable file name. */
function nameHints(x: Exchange): string[] {
  const out: string[] = [];
  const body = x.requestBody as { ecu?: unknown; job?: unknown; items?: unknown } | null;
  const pick = (v: unknown) => (typeof v === "string" && v ? out.push(slug(v, 32)) : 0);
  pick(body?.ecu ?? x.query.ecu);
  pick(body?.job ?? x.query.job);
  if (Array.isArray(body?.items)) out.push(`${body.items.length}items`);
  if (x.query.window) out.push(slug(x.query.window, 24));
  return out.filter(Boolean);
}

export class FolderRecorder implements ExchangeRecorder {
  private seq = 0;

  constructor(
    private readonly dir: string,
    private readonly opts: { warn: (message: string) => void; redact: (text: string) => string },
  ) {}

  /** Stable, readable base name: time, sequence, method, path, then ecu/job when present. */
  fileBase(x: Exchange, seq: number): string {
    const parts = [stamp(x.time), String(seq).padStart(4, "0"), x.method, slug(x.path) || "root", ...nameHints(x)];
    return parts.join("-");
  }

  async record(x: Exchange): Promise<void> {
    const base = this.fileBase(x, ++this.seq);
    try {
      await mkdir(this.dir, { recursive: true, mode: 0o700 });
      const contentType = x.responseHeaders["content-type"];
      const ext = x.status === 200 ? imageExtension(contentType) : null;
      let response: Record<string, unknown>;
      if (ext) {
        const imageFile = `${base}.${ext}`;
        await writeFile(join(this.dir, imageFile), x.body, { flag: "wx", mode: 0o600 });
        response = { status: x.status, headers: x.responseHeaders, imageFile };
      } else {
        const text = Buffer.from(x.body).toString("utf8");
        let json: unknown;
        try {
          json = JSON.parse(text);
        } catch {
          json = undefined;
        }
        response =
          json === undefined
            ? { status: x.status, headers: x.responseHeaders, bodyText: text }
            : { status: x.status, headers: x.responseHeaders, body: json };
      }
      const record = {
        recordedAt: x.time.toISOString(),
        request: { method: x.method, path: x.path, query: x.query, body: x.requestBody ?? null },
        response,
      };
      await writeFile(join(this.dir, `${base}.json`), this.opts.redact(JSON.stringify(record, null, 2)) + "\n", {
        flag: "wx",
        mode: 0o600,
      });
    } catch (e) {
      this.opts.warn(`warning: could not record agent exchange ${base}: ${e instanceof Error ? e.message : String(e)}`);
    }
  }
}

/** The text of a request body that is a string (what the MCP server sends), else null. */
function bodyText(body: RequestInit["body"]): string | null {
  return typeof body === "string" ? body : null;
}

function parseJson(text: string | null): unknown {
  if (!text) return null;
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

/**
 * A `fetch` that records every exchange of `inner` with FolderRecorder, except `GET /hello`.
 * Pass it as `AppDeps.fetch`. Recording never changes the call: the caller gets the response
 * (read from a clone) or the transport error as `inner` produced it. Besides `redact`, any
 * bearer token seen in a request's Authorization header is removed from the written text.
 */
export function recordingFetch(
  dir: string,
  redact: (text: string) => string,
  opts: { fetch?: typeof fetch; warn?: (message: string) => void } = {},
): typeof fetch {
  const inner = opts.fetch ?? fetch;
  const warn = opts.warn ?? ((m: string) => console.error(m));
  const seen = new Set<string>();
  const recorder = new FolderRecorder(dir, {
    warn,
    redact: (text) => {
      let t = redact(text);
      for (const token of seen) t = t.split(token).join("[redacted]");
      return t;
    },
  });
  return async (input, init) => {
    const res = await inner(input, init);
    try {
      const request = input instanceof Request ? input : null;
      const url = new URL(request ? request.url : String(input));
      const method = (init?.method ?? request?.method ?? "GET").toUpperCase();
      const auth = new Headers(init?.headers ?? request?.headers).get("authorization");
      const token = auth?.match(/^Bearer\s+(.+)$/i)?.[1];
      if (token) seen.add(token);
      if (url.pathname === "/hello") return res;
      const responseHeaders: Record<string, string> = {};
      res.headers.forEach((value, name) => {
        const n = name.toLowerCase();
        if (n === "content-type" || n.startsWith("x-")) responseHeaders[n] = value;
      });
      const reqText = init?.body !== undefined ? bodyText(init.body) : request ? await request.clone().text() : null;
      await recorder.record({
        time: new Date(),
        method,
        path: url.pathname,
        query: Object.fromEntries(url.searchParams),
        requestBody: parseJson(reqText),
        status: res.status,
        responseHeaders,
        body: new Uint8Array(await res.clone().arrayBuffer()),
      });
    } catch (e) {
      warn(`warning: could not record agent exchange: ${e instanceof Error ? e.message : String(e)}`);
    }
    return res;
  };
}
