import { mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";

/**
 * Recording mode (docs/tools.md "Recording"): with EDIABAS_RECORD_DIR set, every agent
 * exchange is written to that folder as one JSON file, images as a separate file next to it.
 * The token and the Authorization header are never part of an exchange. dev/replay-agent.ts
 * serves a scrubbed copy of such a folder.
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
