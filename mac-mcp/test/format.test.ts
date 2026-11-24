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

  it("says when EDIABAS is held open after a failed apiEnd", () => {
    const dll = { dllPath: "C:\\EDIABAS\\BIN\\api32.dll", dllFound: true };
    const t = formatHealth(health({ ediabas: { ...dll, loaded: true, heldOpen: true } }));
    expect(t).toContain("EDIABAS: held open after a failed apiEnd; the interface stays in use until the agent restarts (restart Windows or the scheduled task).");
    expect(t).not.toMatch(/loaded now/);
    expect(formatHealth(health({ ediabas: { ...dll, loaded: true, heldOpen: false } }))).not.toMatch(/held open/);
  });

  describe("approvals", () => {
    const after = (approvals: unknown) => {
      const lines = formatHealth(health({ policyVersion: 3, capabilities: [], ...(approvals === undefined ? {} : { approvals }) }), 3).split("\n");
      return lines.slice(lines.findIndex((l) => l.startsWith("Job policy:")) + 1);
    };

    it("0 pinned, 0 ignored", () => {
      expect(after({ pinned: 0, ignored: 0 })).toEqual(["Approved jobs: 0 pinned, 0 ignored."]);
    });

    it("2 pinned, 0 ignored", () => {
      expect(after({ pinned: 2, ignored: 0 })).toEqual(["Approved jobs: 2 pinned, 0 ignored."]);
    });

    it("1 pinned, 5 ignored: says they approve nothing and where the pinned entry comes from", () => {
      expect(after({ pinned: 1, ignored: 5 })).toEqual([
        "Approved jobs: 1 pinned, 5 ignored.",
        "5 approval entries are ignored (unpinned, malformed or not upper case); ediabas_describe prints the pinned entry for each.",
      ]);
    });

    it("1 ignored: singular grammar", () => {
      expect(after({ pinned: 0, ignored: 1 })[1]).toBe(
        "1 approval entry is ignored (unpinned, malformed or not upper case); ediabas_describe prints the pinned entry for each.",
      );
    });

    it("lists each entry on its own line under 'Approved jobs:'", () => {
      expect(after({ pinned: 2, ignored: 0, entries: ["A:B@b8ae0dd9d242", "C:D@7b69322a754f"], hash: "0123456789ab" })).toEqual([
        "Approved jobs: 2 pinned, 0 ignored.",
        "  A:B@b8ae0dd9d242",
        "  C:D@7b69322a754f",
      ]);
    });

    it("a 0.4.0 agent's {pinned, unpinned}: still read, and says unpinned stops working in 0.5.0", () => {
      expect(after({ pinned: 0, unpinned: 0 })).toEqual(["Approved jobs: 0 pinned, 0 unpinned."]);
      expect(after({ pinned: 1, unpinned: 3 })).toEqual([
        "Approved jobs: 1 pinned, 3 unpinned.",
        "3 unpinned approvals skip the description screen on this older agent and approve nothing from 0.5.0; ediabas_describe prints the pinned entry for each.",
      ]);
    });

    it("an older agent without the field: nothing", () => {
      expect(after(undefined)).toEqual([]);
      expect(formatHealth(health())).not.toContain("Approved jobs");
    });
  });

  it("strips control characters from agent strings", () => {
    const t = formatHealth(health({ conflictingProcesses: [{ name: "EVIL\u001b[2J\nX", pid: 1 }] }));
    expect(t).not.toContain("\u001b");
    expect(t).toContain("EVIL [2J X");
  });

  describe("telegram rule and check", () => {
    const dll = { dllPath: "C:\\EDIABAS\\BIN\\api32.dll", dllFound: true };
    const sha = "ab".repeat(32);
    const tg = (o: Record<string, unknown> = {}, ediabas: Record<string, unknown> = { interface: null, requireGate: false }) =>
      health({ ediabas: { ...dll, ...ediabas }, telegramsVersion: 1, telegramsSha256: sha, telegramCheck: { mode: "enforce", latch: null }, ...o });

    it("says nothing for an agent that reports neither", () => {
      const t = formatHealth(health(), null, undefined, null, sha);
      expect(t).not.toMatch(/Telegram|EDIABAS interface/);
    });

    it("reports the version, the check and the interface", () => {
      const t = formatHealth(tg(), null, undefined, null, sha);
      expect(t).toContain(`Telegram rule: agent version 1 (sha256 ${sha.slice(0, 12)}); check: enforce.`);
      expect(t).toContain("EDIABAS interface: from EDIABAS.INI (apiInit).");
      expect(t).not.toMatch(/WARNING|LATCHED/);
      expect(formatHealth(tg({}, { interface: "GATE:OBD", requireGate: true }))).toContain(
        "EDIABAS interface: GATE:OBD (apiInitExt); only a GATE: interface is allowed.",
      );
    });

    it("warns when the Mac's policy/telegrams.json is not the agent's file", () => {
      const t = formatHealth(tg(), null, undefined, null, "cd".repeat(32));
      expect(t).toMatch(/^WARNING: the agent's telegram rule \(sha256 abababababab\) is not the same file as this Mac's policy\/telegrams\.json \(cdcdcdcdcdcd\)\. The agent enforces its own copy\. /m);
      expect(formatHealth(tg(), null, undefined, null, null)).not.toMatch(/WARNING/);
    });

    it("shows a latch prominently, with what was refused and what to do", () => {
      const latch = {
        at: "2026-11-24T10:00:00Z",
        code: "telegram_not_allowed",
        reason: "service",
        function: "SENDECUTEL",
        concept: "010F",
        frame: "83 12 F1 14 FF FF",
        message: "EDIABAS sent 83 12 F1 14 FF FF under concept 010F, which the telegram rule refuses (service).",
      };
      const t = formatHealth(tg({ telegramCheck: { mode: "enforce", latch } }));
      expect(t).toMatch(
        /^TELEGRAM CHECK LATCHED at 2026-11-24T10:00:00Z \[telegram_not_allowed\] \(SENDECUTEL, telegram 83 12 F1 14 FF FF, concept 010F, reason service\): EDIABAS sent .*Every EDIABAS tool is refused until the user restarts the agent.*never retry/m,
      );
    });

    it("warns about off, explains report, and flags requireGate without a gate", () => {
      expect(formatHealth(tg({ telegramCheck: { mode: "off", latch: null } }))).toMatch(/^WARNING: the agent's telegram check is off/m);
      expect(formatHealth(tg({ telegramCheck: { mode: "report", latch: null } }))).toMatch(/^The telegram check is in report mode/m);
      expect(formatHealth(tg({}, { interface: "STD:OBD", requireGate: true }))).toMatch(/^WARNING: requireGate is set but the interface is not a GATE: one/m);
    });

    it("says when the agent's rule did not load", () => {
      expect(formatHealth(tg({ telegramsVersion: null, telegramsSha256: null }))).toContain("Telegram rule: agent did not load one; check: enforce.");
    });
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
