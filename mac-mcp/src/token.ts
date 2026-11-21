import { noToken } from "./errors.js";
import type { ExecFileFn } from "./vmAddress.js";

export interface TokenProviderOptions {
  /** Runs a command without a shell; resolves with stdout. */
  execFile: ExecFileFn;
  /** `EDIABAS_AGENT_TOKEN`, used only when the Keychain has none. */
  envToken?: string | undefined;
  /** Writes one line to stderr. */
  warn: (message: string) => void;
  service: string;
  account: string;
}

const SECURITY = "/usr/bin/security";

/**
 * The agent token, read lazily from the Keychain with an env fallback. The token itself is
 * never logged or returned anywhere except as the value of `get()`/`refresh()`.
 */
export class TokenProvider {
  private cached: string | undefined;
  private readonly seen = new Set<string>();
  private warnedEnv = false;

  constructor(private readonly opts: TokenProviderOptions) {}

  /** The token, reading it on first use. Throws `no_token` (absence is not cached). */
  async get(): Promise<string> {
    if (this.cached !== undefined) return this.cached;
    return this.refresh();
  }

  /** Re-read the token (e.g. after a 401: it may have just been installed). */
  async refresh(): Promise<string> {
    this.cached = undefined;
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
