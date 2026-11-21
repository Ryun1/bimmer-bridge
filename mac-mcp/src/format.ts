import type { Health, WindowList } from "./agentClient.js";

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

export function formatHealth(h: Health): string {
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
    lines.push("BMW tools running: none — the diagnostic cable is free for the bridge.");
  } else {
    const list = h.conflictingProcesses.map((p) => `${clean(p.name)} (pid ${p.pid})`).join(", ");
    lines.push(`BMW tools running: ${list} — the diagnostic cable is unavailable to the bridge while they run.`);
  }
  const dll = h.ediabas.dllPath ? clean(h.ediabas.dllPath) : "unknown path";
  lines.push(h.ediabas.dllFound ? `EDIABAS DLL: found at ${dll}` : `EDIABAS DLL: NOT found (looked for ${dll})`);
  lines.push(`Last cable use: ${h.lastCableUseAt ? clean(h.lastCableUseAt) : "never (since the agent started)"}`);
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
