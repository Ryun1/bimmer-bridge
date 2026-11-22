import type { Capture, Health, LogList, LogRead, WindowList } from "./agentClient.js";
import { UPDATE_HINT } from "./errors.js";

/** Strip control characters from text that came from the VM (window titles, process names). */
function clean(s: string | null | undefined, fallback = "unknown"): string {
  if (s === null || s === undefined) return fallback;
  // eslint-disable-next-line no-control-regex
  const t = s.replace(/[\u0000-\u001f\u007f-\u009f]+/g, " ").trim();
  return t || fallback;
}

function truncate(s: string, max: number): string {
  return s.length > max ? `${s.slice(0, max - 1)}…` : s;
}

export function formatDuration(totalSeconds: number): string {
  const s = Math.max(0, Math.floor(totalSeconds));
  const d = Math.floor(s / 86400);
  const h = Math.floor((s % 86400) / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  if (d > 0) return `${d}d ${h}h ${m}m`;
  if (h > 0) return `${h}h ${m}m`;
  if (m > 0) return `${m}m ${sec}s`;
  return `${sec}s`;
}

/** The agent's policy version against the Mac's (null when the Mac's file did not load). */
function policyLines(h: Health, macPolicyVersion: number | null): string[] {
  const lines: string[] = [];
  const agentV = h.policyVersion ?? null;
  const caps = h.capabilities ?? null;
  if (caps === null) {
    lines.push(`Agent features: not reported — an older agent build that has only vm_health and vm_list_windows. ${UPDATE_HINT}`);
  } else {
    lines.push(`Agent features: ${caps.length ? caps.map((c) => clean(c)).join(", ") : "none"}`);
  }
  const mac = macPolicyVersion === null ? "could not be loaded, so every EDIABAS tool refuses" : `version ${macPolicyVersion}`;
  lines.push(`Job policy: agent ${agentV === null ? "does not report one" : `version ${agentV}`}; Mac ${mac}.`);
  if (agentV !== null && macPolicyVersion !== null && agentV !== macPolicyVersion) {
    lines.push(
      `WARNING: the agent's job policy (version ${agentV}) differs from this Mac's (version ${macPolicyVersion}). Each side refuses what its own copy refuses. ${UPDATE_HINT}`,
    );
  }
  return lines;
}

export function formatHealth(h: Health, macPolicyVersion: number | null = null): string {
  const lines: string[] = [];
  const since = h.startedAt ? ` (since ${clean(h.startedAt)})` : "";
  lines.push(`Agent: ediabas-bridge-agent ${clean(h.version)}, up ${formatDuration(h.uptimeSeconds)}${since}`);
  lines.push(
    `Windows: ${clean(h.os.description)} (OS ${clean(h.os.osArchitecture)}, agent process ${clean(h.os.processArchitecture)})`,
  );
  if (h.sessionId === 0) {
    lines.push(
      "Windows session 0 — NOT an interactive session; screenshots will not work. Log in to Windows so the agent runs on the desktop.",
    );
  } else {
    lines.push(`Windows session ${h.sessionId} (interactive)`);
  }
  lines.push(`User: ${clean(h.user)}; elevated: ${h.elevated ? "yes" : "no"}`);
  const bind = h.bindAddress ? `${clean(h.bindAddress)}${h.port !== null ? `:${h.port}` : ""}` : "unknown";
  lines.push(`Listening on: ${bind}${h.gateway ? ` (gateway ${clean(h.gateway)})` : ""}`);
  if (h.conflictingProcesses.length === 0) {
    lines.push("BMW tools running: none.");
  } else {
    const list = h.conflictingProcesses.map((p) => `${clean(p.name)} (pid ${p.pid})`).join(", ");
    lines.push(`BMW tools running: ${list} — the diagnostic cable is unavailable to the bridge while they run.`);
  }
  const dll = h.ediabas.dllPath ? clean(h.ediabas.dllPath) : "unknown path";
  lines.push(h.ediabas.dllFound ? `EDIABAS DLL: found at ${dll}` : `EDIABAS DLL: NOT found (looked for ${dll})`);
  // Absent on agents without idle release: say nothing rather than guess.
  if (h.ediabas.stuck === true) {
    lines.push(
      "EDIABAS: STUCK: a job did not stop after apiBreak, so the agent no longer touches EDIABAS and every EDIABAS call fails. " +
        "Ryan needs to restart the agent (restart Windows in the VM).",
    );
  } else if (h.ediabas.loaded === true) {
    lines.push("EDIABAS: loaded now, so the next EDIABAS call starts straight away.");
  } else if (h.ediabas.loaded === false) {
    lines.push("EDIABAS: not loaded (released after idle); the next EDIABAS call loads it and takes about 10 s longer. That is normal, not a fault.");
  }
  lines.push(`Last cable use: ${h.lastCableUseAt ? clean(h.lastCableUseAt) : "never (since the agent started)"}`);
  lines.push(...policyLines(h, macPolicyVersion));
  return lines.join("\n");
}

const TITLE_MAX = 60;
const PROCESS_MAX = 24;

export function formatWindows(list: WindowList): string {
  const ws = list.windows;
  if (ws.length === 0) {
    return "No visible windows with a title. (Is anyone logged in to Windows?)";
  }
  const header = ["Title", "Process", "PID", "Size", "Minimised"];
  const rows = ws.map((w) => [
    truncate(clean(w.title, "(untitled)"), TITLE_MAX),
    truncate(clean(w.processName, "?"), PROCESS_MAX),
    String(w.pid),
    `${Math.round(w.bounds.width)}x${Math.round(w.bounds.height)}`,
    w.minimized ? "yes" : "no",
  ]);
  const widths = header.map((h, i) => Math.max(h.length, ...rows.map((r) => (r[i] ?? "").length)));
  const fmt = (cells: string[]) => cells.map((c, i) => c.padEnd(widths[i] ?? 0)).join(" | ").trimEnd();
  const n = ws.length;
  return [
    `${n} window${n === 1 ? "" : "s"} (front-most first):`,
    fmt(header),
    widths.map((w) => "-".repeat(w)).join("-+-"),
    ...rows.map(fmt),
  ].join("\n");
}

// ---------------------------------------------------------------------------
// Screenshots and logs
// ---------------------------------------------------------------------------

export function formatCaptureCaption(c: Capture): string {
  const title = c.title === null ? null : truncate(clean(c.title, ""), TITLE_MAX);
  const what = title === null || title.toLowerCase() === "desktop" ? "Desktop" : `Window "${title}"${c.handle ? ` (${truncate(clean(c.handle), 20)})` : ""}`;
  const size = c.width !== null && c.height !== null ? `${c.width}x${c.height}` : "size unknown";
  return `${what}, ${size}, captured with ${c.method ? truncate(clean(c.method), 20) : "an unknown method"}.`;
}

function bytes(n: number): string {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / (1024 * 1024)).toFixed(1)} MB`;
}

const PATH_MAX = 260;

export function formatLogList(l: LogList): string {
  const out = ["Log folders:"];
  if (l.roots.length === 0) out.push("  none configured in the agent");
  for (const r of l.roots) out.push(`  [${r.index}] ${truncate(clean(r.path), PATH_MAX)}${r.exists ? "" : " (does not exist)"}`);
  const n = l.files.length;
  out.push("");
  if (n === 0) {
    out.push("No matching files.");
  } else {
    out.push(`${n} file${n === 1 ? "" : "s"}, newest first${l.truncated ? " (more matched: narrow with root, glob, since or limit)" : ""}:`);
    for (const f of l.files) {
      out.push(`  ${truncate(clean(f.path), PATH_MAX)} | ${bytes(f.sizeBytes)} | ${f.modifiedAt ? clean(f.modifiedAt) : "unknown"}`);
    }
  }
  return out.join("\n");
}

const LOG_TEXT_MAX = 300_000;

/** Log text keeps its line breaks and tabs; every other control character is removed. */
function cleanLogText(s: string): string {
  // eslint-disable-next-line no-control-regex
  const t = s.replace(/\r\n?/g, "\n").replace(/[\u0000-\u0008\u000b-\u001f\u007f-\u009f]/g, "");
  return t.length > LOG_TEXT_MAX ? `${t.slice(0, LOG_TEXT_MAX)}\n… (cut at ${LOG_TEXT_MAX} characters)` : t;
}

export function formatLogRead(r: LogRead): string {
  const end = r.offset + r.length;
  const parts = [
    `${truncate(clean(r.path), PATH_MAX)}: bytes ${r.offset}-${end} of ${r.sizeBytes}`,
    `encoding ${clean(r.encoding)}`,
    `modified ${r.modifiedAt ? clean(r.modifiedAt) : "unknown"}`,
    `truncated: ${r.truncated ? "yes" : "no"}`,
  ];
  if (r.grep) parts.push(`grep "${truncate(clean(r.grep.pattern, ""), 200)}": ${r.grep.matchedLines} matching line${r.grep.matchedLines === 1 ? "" : "s"}`);
  const text = cleanLogText(r.text);
  return `${parts.join(", ")}\n\n${text || "(no text)"}`;
}
