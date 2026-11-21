/**
 * Typed errors and the user-facing messages for them. Every message here is shown to the
 * user (through Claude) as the text of a failed tool call, so each says what to do.
 */

export const TOKEN_FIX =
  "Store the VM's token in the Keychain with: security add-generic-password -s ediabas-bridge -a agent -w '<token>' -U " +
  "(the token is in the VM installer's output), or set EDIABAS_AGENT_TOKEN in the server's environment.";

export const MESSAGES = {
  vmNotRunning: (vmName: string) => `The VM does not appear to be running. Start the '${vmName}' VM in UTM and log in.`,
  agentNotAnswering:
    "The VM is up but the agent is not answering. Log in to Windows (the agent starts at log-on), or re-run the installer.",
  unauthorized: "The agent rejected the token. The Keychain token does not match the VM's.",
  forbidden: "The agent refused this Mac's address (firewall or gateway check).",
  localNetworkBlocked:
    "This Mac could not reach the VM's address even though UTM's network is up. macOS may be blocking it: open System Settings > Privacy & Security > Local Network and allow 'node' (or answer the 'node would like to find devices on your local network' prompt with Allow), then ask again. If that is already allowed, the VM may still be starting: log in to Windows and try again.",
  noToken: `No agent token found. ${TOKEN_FIX}`,
} as const;

/** Codes this server produces itself. Agent errors use the agent's own `error.code`. */
export type LocalErrorCode =
  | "vm_not_running"
  | "agent_not_answering"
  | "local_network_blocked"
  | "unauthorized"
  | "forbidden"
  | "no_token"
  | "wrong_service"
  | "bad_response"
  | "config_error"
  | "invalid_arguments"
  | "agent_outdated"
  | "policy_unavailable"
  | "job_not_allowed"
  | "internal_error";

export class BridgeError extends Error {
  override readonly name = "BridgeError";
  /** Short machine-readable code; also the audit outcome. */
  readonly code: string;
  /** For a policy refusal: the rule that refused it (goes to the audit log). */
  rule: string | undefined;

  constructor(code: LocalErrorCode | string, message: string) {
    super(message);
    this.code = code;
  }
}

export function vmNotRunning(vmName: string, detail?: string): BridgeError {
  return new BridgeError("vm_not_running", MESSAGES.vmNotRunning(vmName) + (detail ? ` (${detail})` : ""));
}

export function agentNotAnswering(): BridgeError {
  return new BridgeError("agent_not_answering", MESSAGES.agentNotAnswering);
}

export function localNetworkBlocked(): BridgeError {
  return new BridgeError("local_network_blocked", MESSAGES.localNetworkBlocked);
}

export function unauthorized(): BridgeError {
  return new BridgeError("unauthorized", `${MESSAGES.unauthorized} ${TOKEN_FIX}`);
}

export function forbidden(): BridgeError {
  return new BridgeError("forbidden", MESSAGES.forbidden);
}

export function noToken(): BridgeError {
  return new BridgeError("no_token", MESSAGES.noToken);
}

export function wrongService(where: string): BridgeError {
  return new BridgeError(
    "wrong_service",
    `Something answered at ${where}, but it is not the ediabas-bridge agent. Check EDIABAS_AGENT_URL.`,
  );
}

export function badResponse(what: string): BridgeError {
  return new BridgeError("bad_response", `The agent's response to ${what} did not match the expected format. The agent and this server may be different versions.`);
}

export function configError(message: string): BridgeError {
  return new BridgeError("config_error", message);
}

/** Remove control characters and cap the length of text that came from the network. */
export function cleanText(s: string, max = 300): string {
  // eslint-disable-next-line no-control-regex
  const t = s.replace(/[\u0000-\u001f\u007f-\u009f]+/g, " ").trim();
  return t.length > max ? `${t.slice(0, max - 1)}…` : t;
}

export function agentError(
  status: number,
  code: string | undefined,
  message: string | undefined,
  details?: unknown,
): BridgeError {
  const safeCode = code ? cleanText(code, 64).replace(/[^A-Za-z0-9_.-]/g, "_") : `http_${status}`;
  const known = safeCode ? describeAgentError(safeCode, message, details) : null;
  if (known) {
    const e = new BridgeError(safeCode, known);
    const rule = detailString(details, "rule");
    if (rule) e.rule = cleanText(rule, 80);
    return e;
  }
  const safeMessage = message ? cleanText(message) : "(no message)";
  return new BridgeError(safeCode || `http_${status}`, `The agent reported an error: ${safeCode} — ${safeMessage} (HTTP ${status}).`);
}

export const UPDATE_HINT =
  "Ryan needs to install the current agent build in the VM: run the Update line (start `python3 dev/serve.py` in the ediabas-bridge repo on the Mac and paste the Update line it prints into Windows PowerShell run as administrator in the VM; see README.md).";

/** The agent has no such endpoint: it is an older build. */
export function agentOutdated(path: string): BridgeError {
  return new BridgeError(
    "agent_outdated",
    `The agent in the VM does not have this feature yet (it does not know ${cleanText(path, 80)}). ${UPDATE_HINT} Do not retry until that is done.`,
  );
}

// ---------------------------------------------------------------------------
// Messages for the agent's error codes (docs/tools.md "Errors"). `details` may be absent
// (older agent) or null, so every field is optional and every message works without it.
// ---------------------------------------------------------------------------

function detailsObject(details: unknown): Record<string, unknown> | null {
  return details !== null && typeof details === "object" && !Array.isArray(details) ? (details as Record<string, unknown>) : null;
}

function detailString(details: unknown, key: string): string | null {
  const v = detailsObject(details)?.[key];
  return typeof v === "string" ? v : typeof v === "number" ? String(v) : null;
}

function detailNumber(details: unknown, key: string): number | null {
  const v = detailsObject(details)?.[key];
  return typeof v === "number" && Number.isFinite(v) ? v : null;
}

/** "NVE_65 FS_LESEN" from details, or a fallback. */
function ecuJob(details: unknown, fallback = "the job"): string {
  const ecu = detailString(details, "ecu");
  const job = detailString(details, "job");
  const parts = [ecu, job].filter((p): p is string => !!p).map((p) => cleanText(p, 80));
  return parts.length ? parts.join(" ") : fallback;
}

function processList(details: unknown): string | null {
  const ps = detailsObject(details)?.processes;
  if (!Array.isArray(ps) || ps.length === 0) return null;
  const names = ps.slice(0, 10).map((p) => {
    const o = detailsObject(p);
    const name = typeof o?.name === "string" ? cleanText(o.name, 60) : "?";
    return typeof o?.pid === "number" ? `${name} (pid ${o.pid})` : name;
  });
  return names.join(", ");
}

/**
 * The user-facing message for a known agent error code, or null for codes without one. None
 * suggests a way around a refusal.
 */
export function describeAgentError(code: string, message: string | undefined, details: unknown): string | null {
  const agentSaid = message ? ` The agent said: "${cleanText(message, 200)}".` : "";
  switch (code) {
    case "job_not_allowed": {
      const rule = detailString(details, "rule");
      return (
        `The agent refused ${ecuJob(details)} under its job policy${rule ? ` (rule ${cleanText(rule, 80)})` : ""}. ` +
        "The bridge is read-only; do not look for another job, ECU or tool to do the same thing. Tell Ryan it was refused."
      );
    }
    case "job_needs_review": {
      const description = detailString(details, "description");
      const matchedRaw = detailsObject(details)?.matched;
      const matched = Array.isArray(matchedRaw) ? matchedRaw.filter((m): m is string => typeof m === "string").map((m) => cleanText(m, 40)) : [];
      const why = !description?.trim()
        ? "the job has no description, so the agent cannot tell that it only reads"
        : `its description suggests it may do more than read${matched.length ? ` (matched: ${matched.join(", ")})` : ""}. Description: "${cleanText(description, 300)}"`;
      return (
        `The agent did not run ${ecuJob(details)}: ${why}. ` +
        "Ryan decides whether this job is safe to run; do not try to get the same reading another way."
      );
    }
    case "cable_in_use": {
      const list = processList(details);
      return (
        `The diagnostic cable is in use${list ? ` by ${list}` : " by a BMW tool in the VM"}. ` +
        "Ask Ryan to close it, then ask again once. Do not retry in a loop."
      );
    }
    case "ecu_not_found": {
      const ecu = detailString(details, "ecu");
      return `There is no SGBD or group file${ecu ? ` named ${cleanText(ecu, 80)}` : ""} in the VM's EDIABAS Ecu folder. Use ediabas_list_ecus to find the right name; never guess names.`;
    }
    case "job_not_found":
      return `The SGBD has no such job (${ecuJob(details)}). Use ediabas_describe on that ECU to see its jobs; never guess names.`;
    case "ediabas_error": {
      const n = detailNumber(details, "errorCode");
      const text = detailString(details, "errorText");
      const what = n !== null || text ? `${n !== null ? `error ${n}` : ""}${n !== null && text ? ": " : ""}${text ? cleanText(text, 200) : ""}` : cleanText(message ?? "no details", 200);
      return (
        `EDIABAS reported ${what}. This often means the ECU did not answer (ignition off, cable not connected, or that ECU is not fitted). ` +
        "Check ediabas_vehicle_status, and ask Ryan before trying again."
      );
    }
    case "ediabas_unavailable": {
      const reason = detailString(details, "reason");
      return `EDIABAS could not be started in the VM${reason ? ` (${cleanText(reason, 200)})` : ""}. Check vm_health (EDIABAS DLL); Ryan may need to check the EDIABAS installation in the VM.`;
    }
    case "job_timeout": {
      const s = detailNumber(details, "timeoutSeconds");
      return `The job did not finish${s !== null ? ` within ${s} s` : " in time"} and was stopped. The ECU may not be answering; check ediabas_vehicle_status and ask Ryan before trying again.`;
    }
    case "window_not_found":
      return "No visible window in the VM matches that. Use vm_list_windows to see the window titles and handles, or ask for the desktop.";
    case "window_minimized": {
      const title = detailString(details, "title");
      return `The window${title ? ` "${cleanText(title, 80)}"` : ""} is minimised, and the bridge never restores windows. Ask Ryan to restore it in the VM, then ask again.`;
    }
    case "desktop_unavailable":
      return "The VM's screen cannot be captured: Windows is locked, showing a secure desktop, or nobody is logged in. Ask Ryan to log in to or unlock Windows, then ask again.";
    case "path_not_allowed": {
      const reason = detailString(details, "reason");
      return `That path is outside the folders the agent may read${reason ? ` (${cleanText(reason, 120)})` : ""}. Use vm_logs_list to see the allowed folders and files; do not try other spellings of the path.`;
    }
    case "file_not_found":
      return "There is no such file in the VM. Use vm_logs_list to see which log files exist.";
    case "not_run":
      return "Not run: the request reached its overall time limit before this item started. Ask again for this ECU on its own.";
    case "bad_request": {
      const p = detailString(details, "parameter");
      return `The agent rejected the request as malformed${p ? ` (parameter ${cleanText(p, 60)})` : ""}.${agentSaid} Check the arguments.`;
    }
    default:
      return null;
  }
}

/** Anything thrown that is not a BridgeError becomes a generic internal error (no stack). */
export function toBridgeError(e: unknown): BridgeError {
  if (e instanceof BridgeError) return e;
  const msg = e instanceof Error ? e.message : String(e);
  return new BridgeError("internal_error", `Internal error in the ediabas-bridge MCP server: ${cleanText(msg)}`);
}
