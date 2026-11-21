import { readFileSync } from "node:fs";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import type { CallToolResult, ToolAnnotations } from "@modelcontextprotocol/sdk/types.js";
import { AgentClient } from "./agentClient.js";
import { AuditLog, type AuditSink } from "./audit.js";
import type { Config } from "./config.js";
import { toBridgeError } from "./errors.js";
import { formatHealth, formatWindows } from "./format.js";
import { TokenProvider } from "./token.js";
import type { DiscoverySources } from "./vmAddress.js";

export const SERVER_NAME = "ediabas-bridge";

/** Version from package.json (one level above both src/ and dist/). */
export const SERVER_VERSION: string = (() => {
  try {
    const pkg = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8")) as { version?: unknown };
    return typeof pkg.version === "string" ? pkg.version : "0.0.0";
  } catch {
    return "0.0.0";
  }
})();

export const INSTRUCTIONS = [
  "ediabas-bridge reads BMW diagnostic information from a Windows VM (UTM) on this Mac that runs EDIABAS, INPA and related tools.",
  "Tools: vm_health (agent status, which BMW tools are running and so hold the cable, EDIABAS DLL)",
  "and vm_list_windows (windows open in the VM).",
  "Everything is read-only: you cannot click, type, clear faults, code or flash anything in the VM or the car.",
  "Only vm_health and vm_list_windows exist so far: reading faults or live values from the car, screenshots and log reading are not built yet, so if the user asks for those, say they are not available yet rather than offering them.",
  "Call vm_health first.",
  "If a tool reports that the VM is not running or the agent is not answering, tell the user what to do (the message says how) rather than retrying.",
].join(" ");

const READ_ONLY: ToolAnnotations = {
  readOnlyHint: true,
  destructiveHint: false,
  idempotentHint: true,
  openWorldHint: false,
};

/** The only logger: stderr. stdout carries MCP protocol messages and nothing else. */
export function stderrLog(message: string): void {
  process.stderr.write(`[ediabas-bridge] ${message}\n`);
}

export interface ServerParts {
  client: Pick<AgentClient, "health" | "listWindows">;
  audit: AuditSink;
  /** Remove secrets from text before it leaves a tool handler. */
  redact: (text: string) => string;
  log: (message: string) => void;
}

/** Builds the McpServer and registers the tools. No transport is attached. */
export function buildServer(parts: ServerParts): McpServer {
  const server = new McpServer({ name: SERVER_NAME, version: SERVER_VERSION }, { instructions: INSTRUCTIONS });

  async function run(tool: string, body: () => Promise<string>): Promise<CallToolResult> {
    const started = performance.now();
    let outcome = "ok";
    let result: CallToolResult;
    try {
      const text = await body();
      result = { content: [{ type: "text", text: parts.redact(text) }] };
    } catch (e) {
      const err = toBridgeError(e);
      outcome = err.code;
      if (err.code === "internal_error") parts.log(parts.redact(`${tool}: ${err.message}`));
      result = { isError: true, content: [{ type: "text", text: parts.redact(err.message) }] };
    }
    try {
      await parts.audit.record({ time: new Date(), tool, outcome, durationMs: performance.now() - started });
    } catch (e) {
      parts.log(`warning: audit failed: ${e instanceof Error ? e.message : String(e)}`);
    }
    return result;
  }

  server.registerTool(
    "vm_health",
    {
      title: "VM agent health",
      description:
        "Check the Windows VM's ediabas-bridge agent: agent version and uptime, Windows version, whether it runs in an " +
        "interactive session, elevation, bind address, which BMW tools (INPA, Tool32, NCS Expert, " +
        "WinKFP, ...) are running and therefore hold the diagnostic cable, whether the EDIABAS DLL was found, and last cable use. " +
        "Call this first in a session, and again when another tool fails. Read-only.",
      annotations: { title: "VM agent health", ...READ_ONLY },
    },
    async () => run("vm_health", async () => formatHealth(await parts.client.health())),
  );

  server.registerTool(
    "vm_list_windows",
    {
      title: "List VM windows",
      description:
        "List the visible top-level windows in the Windows VM as a table (title, process, pid, size, minimised), " +
        "front-most first. Use it to see what the user has open there, e.g. which INPA or Tool32 screen is showing. " +
        "Read-only: it cannot click, type or change anything.",
      annotations: { title: "List VM windows", ...READ_ONLY },
    },
    async () => run("vm_list_windows", async () => formatWindows(await parts.client.listWindows())),
  );

  return server;
}

export interface AppDeps extends DiscoverySources {
  log: (message: string) => void;
  fetch?: typeof fetch;
  requestTimeoutMs?: number;
  probeTimeoutMs?: number;
}

/** Wires config + system access into a ready server. */
export function createAppServer(config: Config, deps: AppDeps): McpServer {
  const tokens = new TokenProvider({
    execFile: deps.execFile,
    envToken: config.envToken,
    warn: deps.log,
    service: config.keychainService,
    account: config.keychainAccount,
  });
  const client = new AgentClient({
    config,
    tokens,
    sources: { readFile: deps.readFile, execFile: deps.execFile, networkInterfaces: deps.networkInterfaces },
    fetch: deps.fetch,
    requestTimeoutMs: deps.requestTimeoutMs,
    probeTimeoutMs: deps.probeTimeoutMs,
    log: (m) => deps.log(tokens.redact(m)),
  });
  const audit = new AuditLog(config.auditLogPath, { warn: deps.log });
  return buildServer({ client, audit, redact: (t) => tokens.redact(t), log: deps.log });
}
