import { describe, expect, it } from "vitest";
import { formatDuration, formatHealth, formatWindows } from "../src/format.js";
import { HealthSchema, WindowsSchema } from "../src/agentClient.js";
import { healthBody, windowsBody } from "./fixtures.js";

const health = (o: Record<string, unknown> = {}) => HealthSchema.parse(healthBody(o));

describe("formatDuration", () => {
  it("renders compact durations", () => {
    expect(formatDuration(0)).toBe("0s");
    expect(formatDuration(512)).toBe("8m 32s");
    expect(formatDuration(3 * 86400 + 4 * 3600 + 5 * 60 + 6)).toBe("3d 4h 5m");
    expect(formatDuration(7200)).toBe("2h 0m");
  });
});

describe("formatHealth", () => {
  it("covers every field the tool promises", () => {
    const t = formatHealth(health());
    expect(t).toContain("0.1.0");
    expect(t).toContain("8m 32s");
    expect(t).toContain("Microsoft Windows 10.0.26100");
    expect(t).toContain("Arm64");
    expect(t).toContain("X86");
    expect(t).toMatch(/session 1/i);
    expect(t).toMatch(/elevated: no/i);
    expect(t).toContain("192.168.64.3:8765");
    expect(t).toContain("INPALOAD (pid 4312)");
    expect(t).toMatch(/cable is unavailable to the bridge/i);
    expect(t).toMatch(/EDIABAS DLL: found/);
    expect(t).toContain("C:\\EDIABAS\\BIN\\api32.dll");
    expect(t).toMatch(/last cable use: never/i);
  });

  it("starts with the Mac server's version and start time, then the agent's", () => {
    const startedAt = new Date(2026, 10, 22, 9, 5, 7); // local time
    const lines = formatHealth(health({ version: "0.3.0" }), null, { version: "0.3.0", startedAt }).split("\n");
    expect(lines[0]).toBe("Mac server 0.3.0, started 2026-11-22 09:05:07");
    expect(lines[1]).toMatch(/^Agent: ediabas-bridge-agent 0\.3\.0, up /);
    expect(lines.join("\n")).not.toMatch(/different versions/);
  });

  it("warns when the Mac server and the agent are different versions", () => {
    const t = formatHealth(health({ version: "0.2.2" }), null, { version: "0.3.0", startedAt: new Date() });
    expect(t.split("\n")[2]).toBe("WARNING: The Mac server and the agent are different versions; restart Claude or update the agent.");
  });

  it("warns that screenshots will not work in session 0", () => {
    const t = formatHealth(health({ sessionId: 0 }));
    expect(t).toMatch(/not .*interactive/i);
    expect(t).toMatch(/screenshots will not work/i);
    expect(formatHealth(health())).not.toMatch(/screenshot/i);
  });

  it("says the cable is free when no BMW tools are running", () => {
    const t = formatHealth(health({ conflictingProcesses: [] }));
    expect(t).toContain("BMW tools running: none.");
    expect(t).not.toMatch(/unavailable|free for the bridge/);
  });

  it("reports a missing DLL, elevation and a last cable use", () => {
    const t = formatHealth(
      health({ elevated: true, ediabas: { dllPath: "C:\\EDIABAS\\BIN\\api32.dll", dllFound: false }, lastCableUseAt: "2026-11-21T16:00:00Z" }),
    );
    expect(t).toMatch(/elevated: yes/i);
    expect(t).toMatch(/EDIABAS DLL: NOT found/);
    expect(t).toContain("2026-11-21T16:00:00Z");
  });

  it("says whether EDIABAS is loaded, and nothing when an older agent does not report it", () => {
    const dll = { dllPath: "C:\\EDIABAS\\BIN\\api32.dll", dllFound: true };
    expect(formatHealth(health({ ediabas: { ...dll, loaded: true } }))).toMatch(/^EDIABAS: loaded now/m);
    const off = formatHealth(health({ ediabas: { ...dll, loaded: false } }));
    expect(off).toMatch(/^EDIABAS: not loaded \(released after idle\).*about 10 s longer.*normal/m);
    const old = formatHealth(health());
    expect(old).not.toMatch(/^EDIABAS: /m);
    expect(old).not.toMatch(/EDIABAS: (not )?loaded/);
  });

  it("says when EDIABAS is stuck after a job ignored apiBreak, and that the agent must be restarted", () => {
    const dll = { dllPath: "C:\\EDIABAS\\BIN\\api32.dll", dllFound: true };
    const t = formatHealth(health({ ediabas: { ...dll, loaded: true, stuck: true } }));
    expect(t).toMatch(/^EDIABAS: STUCK: .*did not stop after apiBreak.*restart/m);
    expect(t).not.toMatch(/loaded now/);
    expect(formatHealth(health({ ediabas: { ...dll, loaded: true, stuck: false } }))).not.toMatch(/STUCK/);
  });

  it("strips control characters from agent strings", () => {
    const t = formatHealth(health({ conflictingProcesses: [{ name: "EVIL\u001b[2J\nX", pid: 1 }] }));
    expect(t).not.toContain("\u001b");
    expect(t).toContain("EVIL [2J X");
  });
});

describe("formatWindows", () => {
  it("renders a compact table, front-most first", () => {
    const t = formatWindows(WindowsSchema.parse(windowsBody()));
    const lines = t.split("\n");
    expect(lines[0]).toMatch(/2 windows.*front-most first/i);
    expect(t).toMatch(/title\s+\|?\s*process/i);
    const inpa = lines.findIndex((l) => l.includes("INPA - BMW Group"));
    const pm = lines.findIndex((l) => l.includes("Program Manager"));
    expect(inpa).toBeGreaterThan(0);
    expect(pm).toBeGreaterThan(inpa);
    expect(lines[inpa]).toContain("INPALOAD");
    expect(lines[inpa]).toContain("4312");
    expect(lines[inpa]).toContain("1280x800");
    expect(lines[inpa]).toMatch(/\bno\b/);
    expect(lines[pm]).toMatch(/\byes\b/);
    expect(lines[pm]).toContain("?"); // unknown process
  });

  it("says so when there are none", () => {
    expect(formatWindows({ windows: [] })).toMatch(/no visible windows/i);
  });

  it("truncates very long titles and strips control characters", () => {
    const t = formatWindows({
      windows: [{ handle: "0x1", title: "A".repeat(200) + "\tB\n", processName: "p", pid: 1, bounds: { x: 0, y: 0, width: 1, height: 1 }, minimized: false }],
    });
    expect(t).not.toContain("A".repeat(100));
    expect(t.split("\n").every((l) => !/[\u0000-\u0008\u000b-\u001f]/.test(l))).toBe(true);
  });
});
