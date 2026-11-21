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
  noToken: `No agent token found. ${TOKEN_FIX}`,
} as const;

/** Codes this server produces itself. Agent errors use the agent's own `error.code`. */
export type LocalErrorCode =
  | "vm_not_running"
  | "agent_not_answering"
  | "unauthorized"
  | "forbidden"
  | "no_token"
  | "wrong_service"
  | "bad_response"
  | "config_error"
  | "internal_error";

export class BridgeError extends Error {
  override readonly name = "BridgeError";
  /** Short machine-readable code; also the audit outcome. */
  readonly code: string;

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

export function agentError(status: number, code: string | undefined, message: string | undefined): BridgeError {
  const safeCode = code ? cleanText(code, 64).replace(/[^A-Za-z0-9_.-]/g, "_") : `http_${status}`;
  const safeMessage = message ? cleanText(message) : "(no message)";
  return new BridgeError(safeCode || `http_${status}`, `The agent reported an error: ${safeCode} — ${safeMessage} (HTTP ${status}).`);
}

/** Anything thrown that is not a BridgeError becomes a generic internal error (no stack). */
export function toBridgeError(e: unknown): BridgeError {
  if (e instanceof BridgeError) return e;
  const msg = e instanceof Error ? e.message : String(e);
  return new BridgeError("internal_error", `Internal error in the ediabas-bridge MCP server: ${cleanText(msg)}`);
}
