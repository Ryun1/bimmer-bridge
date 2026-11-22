import { homedir } from "node:os";
import { join } from "node:path";

/** All runtime configuration, read once from the environment. */
export interface Config {
  /** Full base URL of the agent (no trailing slash). Skips discovery when set. */
  agentUrl: string | undefined;
  /** Port used with a discovered VM address. */
  agentPort: number;
  /** The VM's MAC address, as configured (compared numerically, byte by byte). */
  vmMac: string;
  /** UTM VM name, for `utmctl` and messages. */
  vmName: string;
  /** Token fallback when the Keychain has none. */
  envToken: string | undefined;
  /** Absolute path of the Mac-side audit log. */
  auditLogPath: string;
  /** Keychain generic-password item. */
  keychainService: string;
  keychainAccount: string;
  /** Path of UTM's command-line tool. */
  utmctlPath: string;
  /** Folder to record every agent exchange into (fixtures). Off when undefined. */
  recordDir: string | undefined;
}

export const DEFAULTS = {
  agentPort: 8765,
  vmMac: "F2:E5:81:1E:82:07",
  vmName: "Windows",
  keychainService: "ediabas-bridge",
  keychainAccount: "agent",
  utmctlPath: "/Applications/UTM.app/Contents/MacOS/utmctl",
} as const;

type Env = Record<string, string | undefined>;

function nonEmpty(v: string | undefined): string | undefined {
  const t = v?.trim();
  return t ? t : undefined;
}

function expandHome(p: string, home: string): string {
  if (p === "~") return home;
  if (p.startsWith("~/")) return join(home, p.slice(2));
  return p;
}

/** True when `url` is an absolute http(s) URL. */
export function isHttpUrl(url: string): boolean {
  try {
    const u = new URL(url);
    return u.protocol === "http:" || u.protocol === "https:";
  } catch {
    return false;
  }
}

export function loadConfig(env: Env = process.env, home: string = homedir()): { config: Config; warnings: string[] } {
  const warnings: string[] = [];

  let agentUrl = nonEmpty(env.EDIABAS_AGENT_URL);
  if (agentUrl !== undefined) {
    agentUrl = agentUrl.replace(/\/+$/, "");
    if (!isHttpUrl(agentUrl)) {
      warnings.push("EDIABAS_AGENT_URL is not a valid http(s) URL; every tool will report this until it is fixed.");
    }
  }

  let agentPort: number = DEFAULTS.agentPort;
  const portText = nonEmpty(env.EDIABAS_AGENT_PORT);
  if (portText !== undefined) {
    const n = Number(portText);
    if (/^\d+$/.test(portText) && n >= 1 && n <= 65535) agentPort = n;
    else warnings.push(`EDIABAS_AGENT_PORT is not a valid port; using ${DEFAULTS.agentPort}.`);
  }

  const auditLogPath = expandHome(
    nonEmpty(env.EDIABAS_AUDIT_LOG) ?? join(home, "Library", "Logs", "ediabas-bridge", "audit.log"),
    home,
  );

  const recordText = nonEmpty(env.EDIABAS_RECORD_DIR);
  const recordDir = recordText === undefined ? undefined : expandHome(recordText, home);

  return {
    config: {
      agentUrl,
      agentPort,
      vmMac: nonEmpty(env.EDIABAS_VM_MAC) ?? DEFAULTS.vmMac,
      vmName: nonEmpty(env.EDIABAS_VM_NAME) ?? DEFAULTS.vmName,
      envToken: nonEmpty(env.EDIABAS_AGENT_TOKEN),
      auditLogPath,
      keychainService: DEFAULTS.keychainService,
      keychainAccount: DEFAULTS.keychainAccount,
      utmctlPath: DEFAULTS.utmctlPath,
      recordDir,
    },
    warnings,
  };
}
