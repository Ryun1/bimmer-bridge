#!/usr/bin/env node
/**
 * Recording MCP server (stdio): the production server from mac-mcp/src, with a `fetch` that
 * writes every agent exchange (except GET /hello, never request headers) into a folder.
 * Register it as the MCP command for a recording session (docs/development.md); the production
 * server has no recording code. stdout is the MCP channel: diagnostics go to stderr.
 *
 *   EDIABAS_RECORD_DIR=<folder outside the repo> \
 *     node --import ./dev/ts-resolve.mjs dev/record-server.ts
 *
 * (Node strips the types without a flag: erasableSyntaxOnly keeps mac-mcp/src strippable, and
 * ts-resolve.mjs handles its "./x.js" imports; no build and no extra dependency.)
 */
import { homedir } from "node:os";
import { join, resolve } from "node:path";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { DEFAULT_LOCAL_CONFIG_PATH, loadConfig } from "../mac-mcp/src/config.ts";
import { defaultDeps } from "../mac-mcp/src/deps.ts";
import { createAppServer, stderrLog } from "../mac-mcp/src/server.ts";
import { recordingFetch } from "./recorder.ts";

console.log = (...args: unknown[]) => console.error(...args);
console.info = (...args: unknown[]) => console.error(...args);

async function main(): Promise<void> {
  const text = process.env.EDIABAS_RECORD_DIR?.trim();
  if (!text) throw new Error("set EDIABAS_RECORD_DIR to the folder to record into (outside the repo)");
  const dir = resolve(text === "~" ? homedir() : text.startsWith("~/") ? join(homedir(), text.slice(2)) : text);

  const { config, warnings } = loadConfig(process.env, undefined, DEFAULT_LOCAL_CONFIG_PATH);
  for (const w of warnings) stderrLog(`warning: ${w}`);

  const envToken = config.envToken;
  const redact = (t: string) => (envToken ? t.split(envToken).join("[redacted]") : t);
  const server = createAppServer(config, { ...defaultDeps(), fetch: recordingFetch(dir, redact, { warn: stderrLog }) });

  await server.connect(new StdioServerTransport());
  stderrLog(`started (stdio), recording every agent exchange to ${dir}`);

  const shutdown = () => {
    void server.close().finally(() => process.exit(0));
  };
  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
}

main().catch((e: unknown) => {
  stderrLog(`fatal: ${e instanceof Error ? e.message : String(e)}`);
  process.exit(1);
});
