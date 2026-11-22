#!/usr/bin/env node
/**
 * bimmer-bridge MCP server (stdio). stdout is the MCP channel: nothing else may write to it.
 * All diagnostics go to stderr.
 */
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { DEFAULT_LOCAL_CONFIG_PATH, loadConfig } from "./config.js";
import { createAppServer, stderrLog } from "./server.js";
import { defaultDeps } from "./deps.js";

// Belt and braces: anything that calls console.log must not corrupt the protocol stream.
console.log = (...args: unknown[]) => console.error(...args);
console.info = (...args: unknown[]) => console.error(...args);

async function main(): Promise<void> {
  const { config, warnings } = loadConfig(process.env, undefined, DEFAULT_LOCAL_CONFIG_PATH);
  for (const w of warnings) stderrLog(`warning: ${w}`);

  const server = createAppServer(config, defaultDeps());

  const transport = new StdioServerTransport();
  await server.connect(transport);
  stderrLog("started (stdio)");

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
