/**
 * Typed errors and the user-facing messages for them. Every message here is shown to the
 * user (through Claude) as the text of a failed tool call, so each says what to do.
 */

import { hostPort } from "./vmAddress.js";

export const TOKEN_FIX =
  "Hand the VM's token to this Mac again: in the repo run python3 dev/serve.py --accept-token and paste the Install line it prints " +
  "into an Administrator Windows PowerShell in the VM (the installer never shows the token). Or set EDIABAS_AGENT_TOKEN in the server's environment.";

export const MESSAGES = {
  vmNotRunning: (vmName: string) => `The VM does not appear to be running. Start the '${vmName}' VM in UTM and log in.`,
  agentNotAnswering:
    "The VM is up but the agent is not answering. Log in to Windows (the agent starts at log-on), or re-run the installer.",
  unauthorized: "The agent rejected the token. The Keychain token does not match the VM's.",
  // With EDIABAS_AGENT_URL set the Keychain is never read (token.ts), so it cannot be to blame.
  unauthorizedOverride:
    "The agent at EDIABAS_AGENT_URL rejected the token. With EDIABAS_AGENT_URL set, the token comes from EDIABAS_AGENT_TOKEN " +
    "(the Keychain is never used for an address set by hand), and it does not match that agent's. " +
    "Set EDIABAS_AGENT_TOKEN to the token of the agent at that address, or unset EDIABAS_AGENT_URL to use the VM.",
  forbidden: "The agent refused this Mac's address (firewall or gateway check).",
  localNetworkBlocked:
    "This Mac could not reach the VM's address even though UTM's network is up. macOS may be blocking it: open System Settings > Privacy & Security > Local Network and allow 'node' (or answer the 'node would like to find devices on your local network' prompt with Allow), then ask again. If that is already allowed, the VM may still be starting: log in to Windows and try again.",
  noToken: `No agent token found. ${TOKEN_FIX}`,
  ediabasRequestLost:
    "The connection to the VM agent broke or timed out after this EDIABAS request may already have reached it, so the job may have run in the VM. " +
    "It was not sent again. Run vm_health to see whether the agent is still up; ask again only if the result is still needed.",
  ediabasBusy:
    "Another EDIABAS request is still running; this one was not sent. Wait for the other call's answer, then ask again once if the result is still needed.",
} as const;

/**
 * Codes this server produces itself. Agent errors use the agent's own `error.code`. Each is
 * listed in docs/tools.md "Errors" (test/errorCodesDoc.test.ts checks).
 */
export const LOCAL_ERROR_CODES = [
  "vm_not_running",
  "agent_not_answering",
  "ediabas_request_lost",
  "ediabas_busy",
  "local_network_blocked",
  "unauthorized",
  "forbidden",
  "no_token",
  "wrong_service",
  "bad_response",
  "config_error",
  "invalid_arguments",
  "agent_outdated",
  "policy_unavailable",
  "policy_mismatch",
  "job_not_allowed",
  "internal_error",
] as const;

export type LocalErrorCode = (typeof LOCAL_ERROR_CODES)[number];

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

/** The agent's job policy file differs byte for byte from this Mac's. Nothing was sent to /ediabas/*. */
export function policyMismatch(agentSha256: string, macSha256: string): BridgeError {
  return new BridgeError(
    "policy_mismatch",
    `The agent's job policy (sha256 ${cleanText(agentSha256.slice(0, 12), 40)}) differs from this Mac's (${macSha256.slice(0, 12)}). ` +
      "Update the agent or the Mac server so both carry the same policy/policy.json.",
  );
}

/**
 * A request that runs an EDIABAS job failed after it may have reached the agent. With `http`:
 * the address (which proved it is the agent in this call) answered a non-200 without the
 * agent's error shape, so whether the job ran is unknown.
 */
export function ediabasRequestLost(http?: { baseUrl: string; status: number }): BridgeError {
  if (http === undefined) return new BridgeError("ediabas_request_lost", MESSAGES.ediabasRequestLost);
  return new BridgeError(
    "ediabas_request_lost",
    `The agent's address (${hostPort(http.baseUrl)}) answered this EDIABAS request with HTTP ${http.status} but without the agent's error format, ` +
      "so it is not known whether the agent handled it: the job may have run in the VM. It was not sent again. " +
      "Run vm_health to see whether the agent is still up; ask again only if the result is still needed.",
  );
}

/** An EDIABAS request waited too long behind another one on this Mac and was never sent. */
export function ediabasBusy(): BridgeError {
  return new BridgeError("ediabas_busy", MESSAGES.ediabasBusy);
}

export function localNetworkBlocked(): BridgeError {
  return new BridgeError("local_network_blocked", MESSAGES.localNetworkBlocked);
}

/** `agentUrlOverride`: EDIABAS_AGENT_URL is set, so the token came from EDIABAS_AGENT_TOKEN. */
export function unauthorized(agentUrlOverride = false): BridgeError {
  return new BridgeError("unauthorized", agentUrlOverride ? MESSAGES.unauthorizedOverride : `${MESSAGES.unauthorized} ${TOKEN_FIX}`);
}

export function forbidden(): BridgeError {
  return new BridgeError("forbidden", MESSAGES.forbidden);
}

export function noToken(): BridgeError {
  return new BridgeError("no_token", MESSAGES.noToken);
}

export function wrongService(baseUrl: string): BridgeError {
  return new BridgeError(
    "wrong_service",
    `Something answered at ${hostPort(baseUrl)}, but it is not the bimmer-bridge agent. Check EDIABAS_AGENT_URL.`,
  );
}

/**
 * An address answered `/hello` as the agent but its proof did not match this Mac's token, so the
 * token was not sent there. `agentUrlOverride`: the token came from EDIABAS_AGENT_TOKEN.
 */
export function unprovenAgent(where: string, agentUrlOverride = false): BridgeError {
  const fix = agentUrlOverride
    ? "Either EDIABAS_AGENT_TOKEN is not the token of the agent at EDIABAS_AGENT_URL, or something else answers there. Set the right token, or unset EDIABAS_AGENT_URL to use the VM."
    : `Either the VM's token no longer matches the one on this Mac, or something else answers there. ${TOKEN_FIX}`;
  return new BridgeError("wrong_service", `The address ${where} answered as the bimmer-bridge agent but could not prove it holds this Mac's token, so the token was not sent there. ${fix}`);
}

export function badResponse(what: string): BridgeError {
  return new BridgeError("bad_response", `The agent's response to ${what} did not match the expected format. The agent and this server may be different versions.`);
}

/**
 * The address, which proved it is the agent in this call, answered `what` with a non-200 that
 * is not in the agent's error shape: most likely the agent's web server refused the request
 * before the agent's code ran. For a request that runs no EDIABAS job.
 */
export function unexpectedStatus(what: string, baseUrl: string, status: number): BridgeError {
  return new BridgeError(
    "bad_response",
    `The agent's address (${hostPort(baseUrl)}) answered ${what} with HTTP ${status} but without the agent's error format: ` +
      "the agent's web server may have refused the request before the agent handled it, or another service may answer there now. " +
      "Run vm_health to check the agent; ask again once if the result is still needed.",
  );
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
  run = false,
): BridgeError {
  const safeCode = (code ? cleanText(code, 64).replace(/[^A-Za-z0-9_.-]/g, "_") : "") || `http_${status}`;
  const known = describeAgentError(safeCode, message, details, run);
  if (known) {
    const e = new BridgeError(safeCode, known);
    const rule = detailString(details, "rule");
    if (rule) e.rule = cleanText(rule, 80);
    return e;
  }
  const safeMessage = message ? cleanText(message) : "(no message)";
  return new BridgeError(safeCode, `The agent reported an error: ${safeCode} — ${safeMessage} (HTTP ${status}).`);
}

export const UPDATE_HINT =
  "The user needs to install the current agent build in the VM: run the Update line (start `python3 dev/serve.py` in the bimmer-bridge repo on the Mac and paste the Update line it prints into Windows PowerShell run as administrator in the VM; see docs/install.md).";

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
 * EDIABAS's own name for an error ("IFH-0009"): from the text when it carries one, else from
 * the number. Numbering from EdiabasLib's ErrorCodes (IFH from 10, BIP 60, SYS 90, API 120,
 * NET 150), which matches every code seen at the desk (16 IFH-0006, 28 IFH-0018, 48 IFH-0038,
 * 13 IFH-0003, 100 SYS-0010).
 */
export function ediabasErrorId(n: number | null, text: string | null | undefined): string | null {
  const m = text ? /\b(IFH|BIP|SYS|API|NET)-(\d{4})\b/.exec(text) : null;
  if (m) return `${m[1]}-${m[2]}`;
  if (n === null) return null;
  const ranges: [string, number][] = [["NET", 150], ["API", 120], ["SYS", 90], ["BIP", 60], ["IFH", 10]];
  for (const [name, base] of ranges) {
    if (n >= base && n < base + 30) return `${name}-${String(n - base).padStart(4, "0")}`;
  }
  return null;
}

/** Codes about the cable, the supply or the ignition rather than one ECU's answer. */
const CABLE_SIDE_CODES = new Set(["IFH-0002", "IFH-0003", "IFH-0006", "IFH-0038", "IFH-0007", "IFH-0015", "IFH-0037", "IFH-0016", "IFH-0032"]);

const ECU_OR_CAR = "Otherwise the ECU did not answer (ignition off, cable not plugged into the car, or that ECU is not fitted). ";

/**
 * Why an EDIABAS error happened and what to check, by EDIABAS's name for it. Codes marked
 * "desk" were seen on this setup; the others take EDIABAS's own meaning of the code, so the
 * advice stays with what the code says.
 */
function ediabasErrorAdvice(id: string | null): string {
  switch (id) {
    // Desk, 2026-11-21/22: every real SGBD after the cable was replugged into the Mac.
    case "IFH-0006":
      return (
        "The cable may not be attached to the VM: after it is unplugged and plugged back in, UTM does not hand it back by itself " +
        "(the user ticks it in UTM's USB menu). " + ECU_OR_CAR
      );
    // Desk, 2026-11-22: after the Mac slept, which hands USB devices back to the Mac.
    case "IFH-0038":
      return "The cable may not be attached to the VM: after the Mac sleeps, UTM hands it back to the Mac (the user ticks it in UTM's USB menu). " + ECU_OR_CAR;
    case "IFH-0002":
      return (
        "The cable itself did not answer, so nothing reached the car: check that it is firmly in the car's OBD port (it is powered from there) " +
        "and still ticked for the VM in UTM's USB menu. "
      );
    // Rehearsal, 2026-11-22: every job on E60, CAS, NVE_65 and D_MOTOR, cable in the VM, not in the car.
    case "IFH-0003":
      return (
        "The exchange with the cable failed, which points at the cable before the ECU: at the desk every job on a real SGBD or group file " +
        "gave this with the cable in the VM but not plugged into the car. Check that it is firmly in the car's OBD port and still ticked " +
        "for the VM in UTM's USB menu. If both are fine and the ignition is on, this module may not have answered; say so rather than retrying. "
      );
    case "IFH-0007":
    case "IFH-0015":
    case "IFH-0037":
      return (
        "The cable reports the battery voltage at the OBD port as wrong or low. Ask the user to check that a charger or support unit is on " +
        "and connected; reads on a low battery are unreliable, so stop reading the car until it is. "
      );
    case "IFH-0009":
      return (
        "The cable works but this ECU did not answer: the ignition is off, the module is not fitted or is asleep, or this SGBD does not match " +
        "the module fitted. Do not try other SGBDs at random; find the right one with ediabas_list_ecus. "
      );
    case "IFH-0010":
      return "The ECU's answer was disturbed: low battery voltage, a loose cable at the OBD port, or another tester on the bus. ";
    case "IFH-0016":
    case "IFH-0032":
      return "The cable sees the ignition (KL15) as off. Ask the user whether the ignition is on; every minute with it on counts toward the car session. ";
    // Desk, 2026-11-21: N62_TUE2, ME9N62_2, GS19D and SZL_60 with the ignition off.
    case "SYS-0010":
      return (
        "The SGBD could not start. Some files need the module to answer even to load or describe (seen at the desk with the ignition off); " +
        "with the ignition on, the module may not be fitted or may not match this file. "
      );
    default:
      return "This often means the ECU did not answer (ignition off, cable not plugged into the car, or that ECU is not fitted). ";
  }
}

/**
 * The user-facing message for a known agent error code, or null for codes without one. None
 * suggests a way around a refusal.
 */
export function describeAgentError(code: string, message: string | undefined, details: unknown, run = false): string | null {
  const agentSaid = message ? ` The agent said: "${cleanText(message, 200)}".` : "";
  switch (code) {
    case "job_not_allowed": {
      const rule = detailString(details, "rule");
      return (
        `The agent refused ${ecuJob(details)} under its job policy${rule ? ` (rule ${cleanText(rule, 80)})` : ""}. ` +
        "The bridge is read-only; do not look for another job, ECU or tool to do the same thing. Tell the user it was refused."
      );
    }
    case "job_needs_review": {
      const description = detailString(details, "description");
      const matchedRaw = detailsObject(details)?.matched;
      const matched = Array.isArray(matchedRaw) ? matchedRaw.filter((m): m is string => typeof m === "string").map((m) => cleanText(m, 40)) : [];
      const why = !description?.trim()
        ? "the job has no description, so the agent cannot tell that it only reads"
        : matched.length === 0
          ? // Blank once descriptionScreen.ignore is applied, e.g. only "Jobbeschreibung".
            `its description has nothing in it but a label ("${cleanText(description, 300)}"), so the agent cannot tell that it only reads`
          : `its description suggests it may do more than read (matched: ${matched.join(", ")}). Description: "${cleanText(description, 300)}"`;
      return (
        `The agent did not run ${ecuJob(details)}: ${why}. ` +
        "The user decides whether this job is safe to run; do not try to get the same reading another way."
      );
    }
    case "cable_in_use": {
      const list = processList(details);
      return (
        `The diagnostic cable is in use${list ? ` by ${list}` : " by a BMW tool in the VM"}. ` +
        "Ask the user to close it, then ask again once. Do not retry in a loop."
      );
    }
    case "ecu_not_found": {
      const ecu = detailString(details, "ecu");
      return `There is no SGBD or group file${ecu ? ` named ${cleanText(ecu, 80)}` : ""} in the VM's EDIABAS Ecu folder. Use ediabas_list_ecus to find the right name; never guess names.`;
    }
    case "job_not_found": {
      const ecu = detailString(details, "ecu");
      const job = detailString(details, "job");
      const what = ecu && job ? `${cleanText(ecu, 80)} has no job ${cleanText(job, 80)}.` : "The SGBD has no such job.";
      return `${what} Use ediabas_describe on that ECU to see its jobs; never guess names.`;
    }
    case "ediabas_error": {
      const n = detailNumber(details, "errorCode");
      const text = detailString(details, "errorText");
      const said = `${n !== null ? `error ${n}` : ""}${n !== null && text ? ": " : ""}${text ? cleanText(text, 200) : ""}`;
      // The job ran and ended normally; only reading its results failed. Nothing points at the ECU or cable.
      if (detailsObject(details)?.resultsUnread === true) {
        return `The job finished but its results could not be read${said ? ` (EDIABAS ${said})` : ""}. Ask the user before trying again.`;
      }
      const what = n !== null || text ? said : cleanText(message ?? "no details", 200);
      const id = ediabasErrorId(n, text);
      // The cable, voltage and ignition codes are not settled by ediabas_vehicle_status: its battery
      // and KL30 readings come from the cable's own lines (rehearsal, 2026-11-22).
      const checkStatus = !(id !== null && CABLE_SIDE_CODES.has(id));
      // No IFH code comes from a wrong job name (EDIABAS gives SYS-0008 for that).
      const hint = run && !id?.startsWith("IFH-") ? " If the job name may be wrong, check it with ediabas_describe." : "";
      const next = checkStatus ? "Check ediabas_vehicle_status, and ask the user before trying again." : "Ask the user before trying again.";
      return `EDIABAS reported ${what}. ${ediabasErrorAdvice(id)}${next}${hint}`;
    }
    case "cable_not_found": {
      // EDIABAS error 28 (IFH-0018): seen at the desk on 2026-11-21 with the cable unplugged.
      // Not ediabas_error's "ECU did not answer": nothing reached the car at all.
      const n = detailNumber(details, "errorCode");
      const text = detailString(details, "errorText");
      const said = n !== null || text ? ` (EDIABAS ${n !== null ? `error ${n}` : ""}${n !== null && text ? ": " : ""}${text ? cleanText(text, 200) : ""})` : "";
      return (
        `EDIABAS could not initialise the diagnostic interface${said}: the K+DCAN cable is not connected to the VM. ` +
        "Even ediabas_describe needs it for a real SGBD. Ask the user to plug the cable into the Mac and pass it through to the " +
        "Windows VM in UTM. Do not retry until the user says it is connected."
      );
    }
    case "ediabas_unavailable": {
      const reason = detailString(details, "reason");
      return `EDIABAS could not be started in the VM${reason ? ` (${cleanText(reason, 200)})` : ""}. Check vm_health (EDIABAS DLL); the user may need to check the EDIABAS installation in the VM.`;
    }
    case "job_timeout": {
      const s = detailNumber(details, "timeoutSeconds");
      return `The job did not finish${s !== null ? ` within ${s} s` : " in time"} and was stopped. The ECU may not be answering; check ediabas_vehicle_status and ask the user before trying again.`;
    }
    case "window_not_found":
      return "No visible window in the VM matches that. Use vm_list_windows to see the window titles and handles, or ask for the desktop.";
    case "window_minimized": {
      const title = detailString(details, "title");
      return `The window${title ? ` "${cleanText(title, 80)}"` : ""} is minimised, and the bridge never restores windows. Ask the user to restore it in the VM, then ask again.`;
    }
    case "desktop_unavailable":
      return "The VM's screen cannot be captured: Windows is locked, showing a secure desktop, or nobody is logged in. Ask the user to log in to or unlock Windows, then ask again.";
    case "capture_busy":
      return "Another screenshot is still being taken in the VM; try again in a moment.";
    case "capture_failed":
      return "The window could not be captured (Windows failed to copy its image; this is not the lock screen). Try again once, or capture the desktop instead.";
    case "path_not_allowed": {
      const reason = detailString(details, "reason");
      return `That path is outside the folders the agent may read${reason ? ` (${cleanText(reason, 120)})` : ""}. Use vm_logs_list to see the allowed folders and files; do not try other spellings of the path.`;
    }
    case "file_not_found":
      return "There is no such file in the VM. Use vm_logs_list to see which log files exist.";
    case "not_run": {
      // The agent says why: its time budget, a BMW tool that started during the request, an
      // earlier job that ignored apiBreak, or an abandoned request. Pass that through.
      const why = message?.trim() ? `the agent said: "${cleanText(message, 200)}"` : "the agent did not start this job and gave no reason.";
      return (
        `Not run: ${why} ` +
        "If a BMW tool was opened in the VM, ask the user to close it before asking again. If the time limit was reached, ask again for this ECU on its own. Do not retry in a loop."
      );
    }
    case "bad_request": {
      const p = detailString(details, "parameter");
      return `The agent rejected the request as malformed${p ? ` (parameter ${cleanText(p, 60)})` : ""}.${agentSaid} Check the arguments.`;
    }
    default:
      return null;
  }
}

/** The text of a failed tool call: the code in brackets, then the message. */
export function errorText(code: string, message: string): string {
  return `[${code}] ${message}`;
}

/** Anything thrown that is not a BridgeError becomes a generic internal error (no stack). */
export function toBridgeError(e: unknown): BridgeError {
  if (e instanceof BridgeError) return e;
  const msg = e instanceof Error ? e.message : String(e);
  return new BridgeError("internal_error", `Internal error in the bimmer-bridge MCP server: ${cleanText(msg)}`);
}
