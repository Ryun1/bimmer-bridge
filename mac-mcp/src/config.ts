import { readFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { cleanText } from "./errors.js";
import type { PolicyGuard } from "./policy.js";

/** config.local.json at the repo root (git-ignored): two levels above both src/ and dist/. */
export const DEFAULT_LOCAL_CONFIG_PATH = fileURLToPath(new URL("../../config.local.json", import.meta.url));

/** Keys config.local.json may set. Keys starting with "_" are comments and ignored. */
const LOCAL_KEYS = ["vmName", "vmMac", "agentPort", "voltageSources"] as const;

/** One extra voltage reading for ediabas_vehicle_status (config.local.json `voltageSources`). */
export interface VoltageSource {
  ecu: string;
  job: string;
}

/** The most voltage sources: with the four fixed readings they fill one /ediabas/jobs batch (20). */
export const VOLTAGE_SOURCES_MAX = 16;

/** All runtime configuration, read once from the environment. */
export interface Config {
  /** Full base URL of the agent (no trailing slash). Skips discovery when set. */
  agentUrl: string | undefined;
  /** Port used with a discovered VM address. */
  agentPort: number;
  /**
   * The VM's MAC address from EDIABAS_VM_MAC or config.local.json (compared numerically, byte
   * by byte). When undefined, discovery reads it from UTM's config for the VM (`utmConfigPath`).
   */
  vmMac: string | undefined;
  /** UTM VM name, for messages and finding the VM's UTM config. */
  vmName: string;
  /** UTM's config.plist for the VM named `vmName`: where the VM's MAC is read from. */
  utmConfigPath: string;
  /** Token fallback when the Keychain has none. */
  envToken: string | undefined;
  /** Absolute path of the Mac-side audit log. */
  auditLogPath: string;
  /** Keychain generic-password item. */
  keychainService: string;
  keychainAccount: string;
  /** Extra voltage readings ediabas_vehicle_status runs, in order (config.local.json only). */
  voltageSources: VoltageSource[];
}

export const DEFAULTS = {
  agentPort: 8765,
  vmName: "Windows",
  keychainService: "ediabas-bridge",
  keychainAccount: "agent",
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

/** UTM's config.plist for the VM called `vmName` (UTM's sandboxed Documents folder). */
export function utmConfigPath(home: string, vmName: string): string {
  return join(home, "Library", "Containers", "com.utmapp.UTM", "Data", "Documents", `${vmName}.utm`, "config.plist");
}

interface LocalConfig {
  vmName?: string;
  vmMac?: string;
  agentPort?: number;
  voltageSources?: VoltageSource[];
}

function readVoltageSources(path: string, value: unknown, warnings: string[]): VoltageSource[] {
  if (!Array.isArray(value) || value.length > VOLTAGE_SOURCES_MAX) {
    warnings.push(`${path}: voltageSources must be an array of at most ${VOLTAGE_SOURCES_MAX} {"ecu", "job"} entries; it is ignored.`);
    return [];
  }
  const out: VoltageSource[] = [];
  value.forEach((entry: unknown, i) => {
    const e = entry as Record<string, unknown>;
    const ok =
      typeof entry === "object" &&
      entry !== null &&
      !Array.isArray(entry) &&
      Object.keys(e).every((k) => k === "ecu" || k === "job") &&
      typeof e.ecu === "string" &&
      e.ecu.trim() !== "" &&
      typeof e.job === "string" &&
      e.job.trim() !== "";
    if (ok) out.push({ ecu: (e.ecu as string).trim(), job: (e.job as string).trim() });
    else warnings.push(`${path}: voltageSources[${i}] must be an object with only a non-empty "ecu" and "job"; it is ignored.`);
  });
  return out;
}

/**
 * The voltageSources entries the job policy refuses, one message each. These are config errors:
 * ediabas_vehicle_status refuses while any is left. Empty when the policy did not load (that is
 * reported on its own, and every ediabas_* tool refuses then).
 */
export function voltageSourceErrors(sources: VoltageSource[], policy: PolicyGuard): string[] {
  if (policy.error) return [];
  const out: string[] = [];
  sources.forEach((s, i) => {
    const v = policy.evaluate(s);
    if (!v.allowed) out.push(`config.local.json voltageSources[${i}] (${cleanText(s.ecu, 80)} ${cleanText(s.job, 80)}) is refused by the job policy (rule ${v.rule}).`);
  });
  return out;
}

/**
 * Read config.local.json. A missing file is normal (empty result); anything wrong with it is a
 * warning, never a crash, and only the valid keys are used.
 */
export function readLocalConfig(path: string, warnings: string[]): LocalConfig {
  let text: string;
  try {
    text = readFileSync(path, "utf8");
  } catch (e) {
    if ((e as NodeJS.ErrnoException).code !== "ENOENT") {
      warnings.push(`${path} could not be read (${(e as Error).message}); it is ignored.`);
    }
    return {};
  }
  let json: unknown;
  try {
    json = JSON.parse(text);
  } catch (e) {
    warnings.push(`${path} is not valid JSON (${(e as Error).message}); it is ignored.`);
    return {};
  }
  if (typeof json !== "object" || json === null || Array.isArray(json)) {
    warnings.push(`${path} must hold one JSON object; it is ignored.`);
    return {};
  }
  const out: LocalConfig = {};
  for (const [key, value] of Object.entries(json as Record<string, unknown>)) {
    if (key.startsWith("_")) continue;
    if (!(LOCAL_KEYS as readonly string[]).includes(key)) {
      warnings.push(`${path}: unknown key "${key}" is ignored (known keys: ${LOCAL_KEYS.join(", ")}).`);
      continue;
    }
    if (key === "agentPort") {
      if (typeof value === "number" && Number.isInteger(value) && value >= 1 && value <= 65535) out.agentPort = value;
      else warnings.push(`${path}: agentPort must be a whole number from 1 to 65535; it is ignored.`);
      continue;
    }
    if (key === "voltageSources") {
      out.voltageSources = readVoltageSources(path, value, warnings);
      continue;
    }
    const text = typeof value === "string" ? value.trim() : "";
    if (!text) {
      warnings.push(`${path}: ${key} must be a non-empty string; it is ignored.`);
      continue;
    }
    if (key === "vmName") out.vmName = text;
    else out.vmMac = text;
  }
  return out;
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

/**
 * Build the configuration. Each setting comes from its environment variable, else from
 * config.local.json (`localConfigPath`; not read when undefined), else its default. The VM's
 * MAC has no default: without one, discovery reads it from UTM's config for the VM.
 */
export function loadConfig(
  env: Env = process.env,
  home: string = homedir(),
  localConfigPath?: string,
): { config: Config; warnings: string[] } {
  const warnings: string[] = [];
  const local = localConfigPath === undefined ? {} : readLocalConfig(localConfigPath, warnings);

  let agentUrl = nonEmpty(env.EDIABAS_AGENT_URL);
  if (agentUrl !== undefined) {
    agentUrl = agentUrl.replace(/\/+$/, "");
    if (!isHttpUrl(agentUrl)) {
      warnings.push("EDIABAS_AGENT_URL is not a valid http(s) URL; every tool will report this until it is fixed.");
    }
  }

  let agentPort: number = local.agentPort ?? DEFAULTS.agentPort;
  const portText = nonEmpty(env.EDIABAS_AGENT_PORT);
  if (portText !== undefined) {
    const n = Number(portText);
    if (/^\d+$/.test(portText) && n >= 1 && n <= 65535) agentPort = n;
    else warnings.push(`EDIABAS_AGENT_PORT is not a valid port; using ${agentPort}.`);
  }

  const auditLogPath = expandHome(
    nonEmpty(env.EDIABAS_AUDIT_LOG) ?? join(home, "Library", "Logs", "ediabas-bridge", "audit.log"),
    home,
  );

  const vmName = nonEmpty(env.EDIABAS_VM_NAME) ?? local.vmName ?? DEFAULTS.vmName;

  return {
    config: {
      agentUrl,
      agentPort,
      vmMac: nonEmpty(env.EDIABAS_VM_MAC) ?? local.vmMac,
      vmName,
      utmConfigPath: utmConfigPath(home, vmName),
      envToken: nonEmpty(env.EDIABAS_AGENT_TOKEN),
      auditLogPath,
      keychainService: DEFAULTS.keychainService,
      keychainAccount: DEFAULTS.keychainAccount,
      voltageSources: local.voltageSources ?? [],
    },
    warnings,
  };
}
