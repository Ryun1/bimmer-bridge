import { appendFile, mkdir } from "node:fs/promises";
import { dirname } from "node:path";

export interface AuditEntry {
  time: Date;
  tool: string;
  /** `ok` or the error code. */
  outcome: string;
  durationMs: number;
}

// eslint-disable-next-line no-control-regex
const CONTROL = /[\u0000-\u001f\u007f-\u009f]/g;

function field(s: string): string {
  return s.replace(CONTROL, "");
}

/** One tab-separated line: UTC time, tool, outcome, duration in ms. */
export function formatAuditLine(e: AuditEntry): string {
  return [e.time.toISOString(), field(e.tool), field(e.outcome), String(Math.round(e.durationMs))].join("\t") + "\n";
}

export interface AuditSink {
  record(entry: AuditEntry): Promise<void>;
}

/** Appends to the Mac-side audit log. Never throws: failures are reported through `warn`. */
export class AuditLog implements AuditSink {
  constructor(
    private readonly path: string,
    private readonly opts: { warn: (message: string) => void },
  ) {}

  async record(entry: AuditEntry): Promise<void> {
    try {
      await mkdir(dirname(this.path), { recursive: true });
      await appendFile(this.path, formatAuditLine(entry), { encoding: "utf8", mode: 0o600 });
    } catch (e) {
      const reason = e instanceof Error ? e.message : String(e);
      this.opts.warn(`warning: could not write the audit log ${this.path}: ${reason}`);
    }
  }
}
