import type { NetworkInterfaceInfo } from "node:os";
import type { Config } from "./config.js";
import { configError, vmNotRunning } from "./errors.js";
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
export const PLUTIL_PATH = "/usr/bin/plutil";
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

/**
 * The VM's MAC address from UTM's config.plist, given as JSON (`plutil -convert json`): the
 * first `Network` entry's `MacAddress`. Undefined when there is none or it is not a MAC.
 */
export function macFromUtmConfig(jsonText: string): string | undefined {
  let json: unknown;
  try {
    json = JSON.parse(jsonText);
  } catch {
    return undefined;
  }
  const network = (json as { Network?: unknown } | null)?.Network;
  if (!Array.isArray(network) || network.length === 0) return undefined;
  const mac = (network[0] as { MacAddress?: unknown } | null)?.MacAddress;
  return typeof mac === "string" && parseMac(mac) !== null ? mac.trim() : undefined;
}

/**
 * The VM's MAC: `config.vmMac` (EDIABAS_VM_MAC or config.local.json) when set, else read from
 * UTM's config.plist for the VM. Undefined when neither gives one.
 */
export async function resolveVmMac(config: Config, execFile: ExecFileFn): Promise<string | undefined> {
  if (config.vmMac !== undefined) return config.vmMac;
  const text = await execFile(PLUTIL_PATH, ["-convert", "json", "-o", "-", config.utmConfigPath], { timeoutMs: COMMAND_TIMEOUT_MS }).catch(
    () => "",
  );
  return macFromUtmConfig(text);
}

/** How to tell the bridge the VM's MAC, for messages. */
export function vmMacHint(config: Config): string {
  return `set EDIABAS_VM_MAC to the VM's MAC address, or EDIABAS_VM_NAME if the VM in UTM is not called '${config.vmName}'`;
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

// ---------------------------------------------------------------------------
// The VM's address: the newest on-link lease in /var/db/dhcpd_leases
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
    if (!Number.isFinite(expiry)) continue; // dev/serve.py skips a lease whose expiry is not a number
    out.push({ ipAddress: ip, hwAddress, lease: expiry });
  }
  return out;
}

/**
 * The addresses of every lease for `mac`, newest first: the largest `lease` (expiry) first, and
 * on a tie the earlier entry in the file first, as in `vm_lease_address` in dev/serve.py. The
 * caller contacts only one of them: the first that is on-link (`VmLocator.discover`).
 */
export function leaseCandidates(text: string, mac: string): string[] {
  return parseLeases(text)
    .filter((l) => macEquals(l.hwAddress, mac))
    .sort((a, b) => b.lease - a.lease) // stable: ties keep file order
    .map((l) => l.ipAddress);
}

// ---------------------------------------------------------------------------
// The on-link rule
// ---------------------------------------------------------------------------

export interface Subnet {
  iface: string;
  address: string;
  netmask: string;
}

/** IPv4 subnets of local `bridge1NN` interfaces (UTM's shared network is `bridge100`, `bridge101`, ...). */
export function bridgeSubnets(ifaces: Partial<Record<string, NetworkInterfaceInfo[]>>): Subnet[] {
  const out: Subnet[] = [];
  for (const [name, infos] of Object.entries(ifaces)) {
    if (!/^bridge1\d\d$/.test(name) || !infos) continue;
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
  /**
   * The address that proved itself in this call, and the exact token whose key its `/hello`
   * proof matched: the only token this call sends, and only to that address.
   */
  proven?: { baseUrl: string; token: string } | undefined;
}

/**
 * Checks one candidate: resolves when `GET /hello` there (fresh nonce, no token) returns 200
 * with the right `agent` value and a proof that matches this Mac's token, and records the
 * address and token in `ctx.proven`; otherwise rejects with a classified BridgeError.
 */
export type ProbeFn = (baseUrl: string, ctx: CallContext) => Promise<void>;

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

    const mac = await resolveVmMac(this.config, this.sources.execFile);
    if (mac === undefined) {
      throw vmNotRunning(
        this.config.vmName,
        `its MAC address is not set and could not be read from ${this.config.utmConfigPath}: ${vmMacHint(this.config)}`,
      );
    }
    const leases = leaseCandidates(await this.sources.readFile(LEASES_PATH).catch(() => ""), mac);
    if (leases.length === 0) {
      throw vmNotRunning(this.config.vmName, `its MAC address ${mac} is not in the DHCP leases; if that is not the VM's MAC, ${vmMacHint(this.config)}`);
    }
    // The newest lease on UTM's network. A newer one off-link (from a former UTM subnet) can
    // never be contacted, so skipping it adds no source; only this one address is probed, and
    // if it fails no older lease is tried.
    const ip = leases.find((l) => isOnLink(l, subnets));
    if (ip === undefined) {
      // never send anything off-link
      throw vmNotRunning(this.config.vmName, `none of its DHCP leases is on the UTM network (the newest is ${leases[0]})`);
    }
    const baseUrl = `http://${ip}:${this.config.agentPort}`;
    await this.probe(baseUrl, ctx); // its own error is the answer
    this.log(`discovery accepted ${ip}:${this.config.agentPort} (source: leases)`);
    return baseUrl;
  }
}
