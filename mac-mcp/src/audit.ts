import { appendFile, mkdir } from "node:fs/promises";
import { dirname } from "node:path";

export interface AuditEntry {
  time: Date;
  tool: string;
  /** `ok` or the error code. */
  outcome: string;
  durationMs: number;
  /** ediabas_* tools: the ECU(s) and job(s) asked for, refused ones included. */
  ecu?: string | undefined;
  job?: string | undefined;
  /** For a policy refusal: the rule. */
  rule?: string | undefined;
}

// eslint-disable-next-line no-control-regex
const CONTROL = /[\u0000-\u001f\u007f-\u009f]/g;

function field(s: string): string {
  return s.replace(CONTROL, "");
}

const VALUE_MAX = 200;

function value(s: string): string {
  const t = field(s).replace(/\s+/g, " ");
  return t.length > VALUE_MAX ? `${t.slice(0, VALUE_MAX - 1)}…` : t;
}

/**
 * One tab-separated line: UTC time, tool, outcome, duration in ms; then, when present,
 * `ecu=`, `job=` and `rule=` fields.
 */
export function formatAuditLine(e: AuditEntry): string {
  const fields = [e.time.toISOString(), field(e.tool), field(e.outcome), String(Math.round(e.durationMs))];
  if (e.ecu !== undefined) fields.push(`ecu=${value(e.ecu)}`);
  if (e.job !== undefined) fields.push(`job=${value(e.job)}`);
  if (e.rule !== undefined) fields.push(`rule=${value(e.rule)}`);
  return fields.join("\t") + "\n";
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
