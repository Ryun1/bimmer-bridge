import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuditLog, formatAuditLine } from "../src/audit.js";

let dir: string;
beforeEach(async () => {
  dir = await mkdtemp(join(tmpdir(), "ediabas-audit-"));
});
afterEach(async () => {
  await rm(dir, { recursive: true, force: true });
});

describe("audit log", () => {
  it("formats one tab-separated line: UTC time, tool, outcome, duration", () => {
    const line = formatAuditLine({
      time: new Date(Date.UTC(2026, 10, 21, 15, 4, 5, 123)),
      tool: "vm_health",
      outcome: "ok",
      durationMs: 12.7,
    });
    expect(line).toBe("2026-11-21T15:04:05.123Z\tvm_health\tok\t13\n");
  });

  it("strips control characters (including tabs and newlines) from fields", () => {
    const line = formatAuditLine({
      time: new Date(0),
      tool: "vm\t_hea\nlth",
      outcome: "bad\r\ncode\u0007\u001b[31m",
      durationMs: 1,
    });
    const fields = line.slice(0, -1).split("\t");
    expect(fields).toHaveLength(4);
    for (const f of fields) expect(f).not.toMatch(/[\u0000-\u001f\u007f]/);
    expect(line).toContain("vm_health");
  });

  it("adds ecu, job and rule fields when given, cleaned", () => {
    const line = formatAuditLine({
      time: new Date(0),
      tool: "ediabas_job",
      outcome: "job_not_allowed",
      durationMs: 2,
      ecu: "NVE_65",
      job: "FS_LOESCHEN\t\nX",
      rule: "deny.substring:LOESCH",
    });
    expect(line).toBe("1970-01-01T00:00:00.000Z\tediabas_job\tjob_not_allowed\t2\tecu=NVE_65\tjob=FS_LOESCHENX\trule=deny.substring:LOESCH\n");
  });

  it("creates the directory and appends", async () => {
    const path = join(dir, "nested", "deeper", "audit.log");
    const log = new AuditLog(path, { warn: vi.fn() });
    await log.record({ time: new Date(0), tool: "vm_health", outcome: "ok", durationMs: 5 });
    await log.record({ time: new Date(1000), tool: "vm_list_windows", outcome: "vm_not_running", durationMs: 7 });
    const text = await readFile(path, "utf8");
    expect(text).toBe(
      "1970-01-01T00:00:00.000Z\tvm_health\tok\t5\n1970-01-01T00:00:01.000Z\tvm_list_windows\tvm_not_running\t7\n",
    );
  });

  it("reports a write failure on stderr (via warn) without throwing", async () => {
    const blocker = join(dir, "file");
    await writeFile(blocker, "x");
    const warn = vi.fn();
    const log = new AuditLog(join(blocker, "audit.log"), { warn });
    await expect(log.record({ time: new Date(0), tool: "t", outcome: "ok", durationMs: 1 })).resolves.toBeUndefined();
    expect(warn).toHaveBeenCalledTimes(1);
    expect(String(warn.mock.calls[0]?.[0])).toMatch(/audit/i);
  });
});
