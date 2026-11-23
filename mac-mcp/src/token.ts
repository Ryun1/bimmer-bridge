import { createHash } from "node:crypto";
import { BridgeError, noToken } from "./errors.js";
import type { ExecFileFn } from "./vmAddress.js";

export interface TokenProviderOptions {
  /** Runs a command without a shell; resolves with stdout. */
  execFile: ExecFileFn;
  /** `EDIABAS_AGENT_TOKEN`, used only when the Keychain has none (or alone, with `envOnly`). */
  envToken?: string | undefined;
  /**
   * True when EDIABAS_AGENT_URL overrides the agent's address: then only `envToken` is used and
   * the Keychain is never read, so the VM agent's secret never goes to an address someone set
   * by hand (a replay agent, or a mistake).
   */
  envOnly?: boolean | undefined;
  /** Writes one line to stderr. */
  warn: (message: string) => void;
  service: string;
  account: string;
}

const SECURITY = "/usr/bin/security";

/**
 * The key of the agent's `/hello` proof (docs/api.md): SHA-256 of the token's UTF-8 bytes.
 * Taken from one exact token, so the token that passed the proof is the one that is sent.
 */
export function helloKey(token: string): Buffer {
  return createHash("sha256").update(token, "utf8").digest();
}

/** no_token when EDIABAS_AGENT_URL is set without EDIABAS_AGENT_TOKEN. */
export function noTokenForOverride(): BridgeError {
  return new BridgeError(
    "no_token",
    "EDIABAS_AGENT_URL is set, so this server needs EDIABAS_AGENT_TOKEN in its environment: the token of the agent at that address. " +
      "The Keychain token (the VM agent's) is deliberately never sent to an overridden address. " +
      "Set EDIABAS_AGENT_TOKEN, or unset EDIABAS_AGENT_URL to use the VM.",
  );
}

/**
 * The agent token, read lazily from the Keychain with an env fallback. The token itself is
 * never logged or returned anywhere except as the value of `get()`/`refresh()`.
 */
export class TokenProvider {
  private cached: string | undefined;
  private readonly seen = new Set<string>();
  private warnedEnv = false;

  private readonly opts: TokenProviderOptions;

  constructor(opts: TokenProviderOptions) {
    this.opts = opts;
  }

  /** The token, reading it on first use. Throws `no_token` (absence is not cached). */
  async get(): Promise<string> {
    if (this.cached !== undefined) return this.cached;
    return this.refresh();
  }

  /** Re-read the token (e.g. after a 401: it may have just been installed). */
  async refresh(): Promise<string> {
    this.cached = undefined;
    if (this.opts.envOnly) {
      if (!this.opts.envToken) throw noTokenForOverride();
      this.seen.add(this.opts.envToken);
      this.cached = this.opts.envToken;
      return this.cached;
    }
    const fromKeychain = await this.readKeychain();
    let token = fromKeychain;
    if (token === undefined && this.opts.envToken) {
      token = this.opts.envToken;
      if (!this.warnedEnv) {
        this.warnedEnv = true;
        this.opts.warn(
          "warning: no agent token in the Keychain (service 'ediabas-bridge', account 'agent'); using EDIABAS_AGENT_TOKEN from the environment. The Keychain is the safer place for it.",
        );
      }
    }
    if (token === undefined) throw noToken();
    this.seen.add(token);
    this.cached = token;
    return token;
  }

  /** Replace every token value this provider has handed out with "[redacted]". */
  redact(text: string): string {
    let out = text;
    const all = new Set(this.seen);
    if (this.opts.envToken) all.add(this.opts.envToken);
    for (const t of all) if (t.length >= 8) out = out.split(t).join("[redacted]");
    return out;
  }

  private async readKeychain(): Promise<string | undefined> {
    try {
      const stdout = await this.opts.execFile(
        SECURITY,
        ["find-generic-password", "-s", this.opts.service, "-a", this.opts.account, "-w"],
        { timeoutMs: 10_000 },
      );
      const t = stdout.trim();
      return t ? t : undefined;
    } catch {
      // Not found (exit 44), Keychain locked, or `security` missing: treat as "none".
      return undefined;
    }
  }
}
