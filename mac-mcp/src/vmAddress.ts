import type { NetworkInterfaceInfo } from "node:os";
import type { Config } from "./config.js";
import { BridgeError, configError, vmNotRunning } from "./errors.js";
import { isHttpUrl } from "./config.js";

// ---------------------------------------------------------------------------
// Injectable system access (tests replace all of these)
// ---------------------------------------------------------------------------

/** Run a program without a shell; resolve with stdout, reject on non-zero exit or timeout. */
export type ExecFileFn = (file: string, args: string[], opts: { timeoutMs: number }) => Promise<string>;
/** Read a UTF-8 text file; reject if it is missing. */
export type ReadFileFn = (path: string) => Promise<string>;
/** Same shape as `os.networkInterfaces()`. */
export type NetworkInterfacesFn = () => Partial<Record<string, NetworkInterfaceInfo[]>>;

export interface DiscoverySources {
  readFile: ReadFileFn;
  execFile: ExecFileFn;
  networkInterfaces: NetworkInterfacesFn;
}

export const LEASES_PATH = "/var/db/dhcpd_leases";
export const ARP_PATH = "/usr/sbin/arp";
const COMMAND_TIMEOUT_MS = 5_000;

// ---------------------------------------------------------------------------
// MAC addresses
// ---------------------------------------------------------------------------

/**
 * Parse a MAC address into six byte values. Accepts `:` or `-` separators and one or two
 * hex digits per byte (macOS drops leading zeros: `82:7` is `82:07`). Returns null for
 * anything else.
 */
export function parseMac(text: string): number[] | null {
  const parts = text.trim().split(/[:-]/);
  if (parts.length !== 6) return null;
  const bytes: number[] = [];
  for (const p of parts) {
    if (!/^[0-9a-fA-F]{1,2}$/.test(p)) return null;
    bytes.push(parseInt(p, 16));
  }
  return bytes;
}

/** Compare two MAC addresses numerically, byte by byte. Malformed input never matches. */
export function macEquals(a: string, b: string): boolean {
  const x = parseMac(a);
  const y = parseMac(b);
  if (!x || !y) return false;
  return x.every((v, i) => v === y[i]);
}

// ---------------------------------------------------------------------------
// IPv4 helpers
// ---------------------------------------------------------------------------

function ipv4ToInt(ip: string): number | null {
  const m = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(ip.trim());
  if (!m) return null;
  let n = 0;
  for (let i = 1; i <= 4; i++) {
    const octet = Number(m[i]);
    if (octet > 255) return null;
    n = n * 256 + octet;
  }
  return n >>> 0;
}

export function isIPv4(ip: string): boolean {
  return ipv4ToInt(ip) !== null;
}

function dedupe(list: string[]): string[] {
  return [...new Set(list)];
}

// ---------------------------------------------------------------------------
// Source 1: /var/db/dhcpd_leases
// ---------------------------------------------------------------------------

export interface Lease {
  ipAddress: string;
  /** The MAC part of `hw_address` (without the `1,` hardware-type prefix). */
  hwAddress: string;
  /** Expiry, epoch seconds. */
  lease: number;
}

/** Parse the `{ key=value ... }` blocks of macOS's bootpd lease file. Field order varies. */
export function parseLeases(text: string): Lease[] {
  const out: Lease[] = [];
  for (const match of text.matchAll(/\{([^{}]*)\}/g)) {
    const fields = new Map<string, string>();
    for (const line of (match[1] ?? "").split("\n")) {
      const eq = line.indexOf("=");
      if (eq < 0) continue;
      fields.set(line.slice(0, eq).trim(), line.slice(eq + 1).trim());
    }
    const ip = fields.get("ip_address");
    const hw = fields.get("hw_address");
    const lease = fields.get("lease");
    if (!ip || !hw || !lease || !isIPv4(ip)) continue;
    const comma = hw.indexOf(",");
    const hwAddress = comma >= 0 ? hw.slice(comma + 1) : hw;
    const expiry = /^0x[0-9a-fA-F]+$/.test(lease) ? parseInt(lease.slice(2), 16) : Number(lease);
    out.push({ ipAddress: ip, hwAddress, lease: Number.isFinite(expiry) ? expiry : 0 });
  }
  return out;
}

/** Addresses leased to `mac`, latest expiry first, de-duplicated. */
export function leaseCandidates(text: string, mac: string): string[] {
  const mine = parseLeases(text).filter((l) => macEquals(l.hwAddress, mac));
  mine.sort((a, b) => b.lease - a.lease);
  return dedupe(mine.map((l) => l.ipAddress));
}

// ---------------------------------------------------------------------------
// Source 2: arp -an
// ---------------------------------------------------------------------------

export interface ArpEntry {
  ipAddress: string;
  macAddress: string;
  iface: string;
}

/** Parse `arp -an` lines: `? (192.168.64.3) at f2:e5:81:1e:82:7 on bridge100 ifscope [bridge]`. */
export function parseArp(text: string): ArpEntry[] {
  const out: ArpEntry[] = [];
  for (const line of text.split("\n")) {
    const m = /^\S+ \(([\d.]+)\) at (\S+) on (\S+)/.exec(line.trim());
    if (!m) continue;
    const [, ip, macAddress, iface] = m;
    if (!ip || !macAddress || !iface || !isIPv4(ip) || parseMac(macAddress) === null) continue; // skips "(incomplete)"
    out.push({ ipAddress: ip, macAddress, iface });
  }
  return out;
}

export function arpCandidates(text: string, mac: string): string[] {
  return dedupe(parseArp(text).filter((e) => macEquals(e.macAddress, mac)).map((e) => e.ipAddress));
}

// ---------------------------------------------------------------------------
// Source 3: utmctl ip-address <name>
// ---------------------------------------------------------------------------

export function parseUtmctl(text: string): string[] {
  return dedupe(text.split("\n").map((l) => l.trim()).filter(isIPv4));
}

// ---------------------------------------------------------------------------
// The on-link rule
// ---------------------------------------------------------------------------

export interface Subnet {
  iface: string;
  address: string;
  netmask: string;
}

/** IPv4 subnets of local `bridge*` interfaces (UTM's shared network is one of these). */
export function bridgeSubnets(ifaces: Partial<Record<string, NetworkInterfaceInfo[]>>): Subnet[] {
  const out: Subnet[] = [];
  for (const [name, infos] of Object.entries(ifaces)) {
    if (!name.startsWith("bridge") || !infos) continue;
    for (const info of infos) {
      const family = info.family as unknown;
      if ((family === "IPv4" || family === 4) && !info.internal) {
        out.push({ iface: name, address: info.address, netmask: info.netmask });
      }
    }
  }
  return out;
}

/** True when `ip` is in the same IPv4 subnet as one of the given interface addresses. */
export function isOnLink(ip: string, subnets: Subnet[]): boolean {
  const target = ipv4ToInt(ip);
  if (target === null) return false;
  return subnets.some((s) => {
    const addr = ipv4ToInt(s.address);
    const mask = ipv4ToInt(s.netmask);
    if (addr === null || mask === null || mask === 0) return false;
    return ((addr & mask) >>> 0) === ((target & mask) >>> 0);
  });
}

/** `host:port` of a base URL, for log lines (never includes credentials or paths). */
export function hostPort(baseUrl: string): string {
  try {
    const u = new URL(baseUrl);
    return `${u.hostname}:${u.port || (u.protocol === "https:" ? "443" : "80")}`;
  } catch {
    return "(invalid url)";
  }
}

// ---------------------------------------------------------------------------
// Locator: picks and caches the agent's base URL
// ---------------------------------------------------------------------------

/** Per-tool-call state shared between discovery and the request (one token re-read per call). */
export interface CallContext {
  tokenRefreshed: boolean;
}

/**
 * Checks one candidate: resolves when `GET /health` there returns 200 with the right `agent`
 * value; otherwise rejects with a classified BridgeError.
 */
export type ProbeFn = (baseUrl: string, ctx: CallContext) => Promise<void>;

/** Which failure best explains why no candidate was accepted (higher wins). */
function rank(code: string): number {
  switch (code) {
    case "unauthorized":
      return 7;
    case "forbidden":
      return 6;
    case "local_network_blocked":
      return 5;
    case "agent_not_answering":
      return 4;
    case "vm_not_running":
    case "wrong_service":
      return 1;
    default:
      return 3; // agent errors, malformed responses
  }
}

export class VmLocator {
  private cached: string | undefined;
  private inflight: Promise<string> | undefined;

  constructor(
    private readonly config: Config,
    private readonly sources: DiscoverySources,
    private readonly probe: ProbeFn,
    private readonly log: (message: string) => void = () => {},
  ) {}

  /** The accepted base URL, from cache or by discovery. */
  async resolve(ctx: CallContext): Promise<{ baseUrl: string; fromCache: boolean }> {
    if (this.cached !== undefined) return { baseUrl: this.cached, fromCache: true };
    this.inflight ??= this.discover(ctx).finally(() => {
      this.inflight = undefined;
    });
    const baseUrl = await this.inflight;
    this.cached = baseUrl;
    return { baseUrl, fromCache: false };
  }

  invalidate(): void {
    this.cached = undefined;
  }

  private async discover(ctx: CallContext): Promise<string> {
    const explicit = this.config.agentUrl;
    if (explicit !== undefined) {
      if (!isHttpUrl(explicit)) {
        throw configError("EDIABAS_AGENT_URL is not a valid http(s) URL. Fix or unset it in the MCP server's environment.");
      }
      await this.probe(explicit, ctx);
      this.log(`discovery accepted ${hostPort(explicit)} (source: env)`);
      return explicit;
    }

    const subnets = bridgeSubnets(this.sources.networkInterfaces());
    if (subnets.length === 0) {
      throw vmNotRunning(this.config.vmName, "no UTM shared-network bridge interface is up on this Mac");
    }

    const tried = new Set<string>();
    let sawCandidate = false;
    let best: BridgeError | undefined;

    for (const [sourceName, source] of this.candidateSources()) {
      const candidates = await source();
      for (const ip of candidates) {
        if (tried.has(ip)) continue;
        tried.add(ip);
        sawCandidate = true;
        if (!isOnLink(ip, subnets)) continue; // never send the token off-link
        const baseUrl = `http://${ip}:${this.config.agentPort}`;
        try {
          await this.probe(baseUrl, ctx);
          this.log(`discovery accepted ${ip}:${this.config.agentPort} (source: ${sourceName})`);
          return baseUrl;
        } catch (e) {
          if (!(e instanceof BridgeError) || e.code === "no_token") throw e;
          if (!best || rank(e.code) > rank(best.code)) best = e;
        }
      }
    }

    if (best && rank(best.code) > 1) throw best;
    throw vmNotRunning(
      this.config.vmName,
      sawCandidate ? "no address for it answered as the agent on the UTM network" : "its MAC address is not in the DHCP leases or ARP table",
    );
  }

  /** Lazy sources in priority order; a later source only runs if earlier ones found nothing usable. */
  private candidateSources(): [string, () => Promise<string[]>][] {
    const { readFile, execFile } = this.sources;
    const mac = this.config.vmMac;
    return [
      ["leases", async () => leaseCandidates(await readFile(LEASES_PATH).catch(() => ""), mac)],
      ["arp", async () => arpCandidates(await execFile(ARP_PATH, ["-an"], { timeoutMs: COMMAND_TIMEOUT_MS }).catch(() => ""), mac)],
      ["utmctl", async () =>
        parseUtmctl(
          // Fails when the guest agent is absent, which is normal.
          await execFile(this.config.utmctlPath, ["ip-address", this.config.vmName], { timeoutMs: COMMAND_TIMEOUT_MS }).catch(() => ""),
        )],
    ];
  }
}
