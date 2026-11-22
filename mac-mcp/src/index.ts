#!/usr/bin/env node
/**
 * ediabas-bridge MCP server (stdio). stdout is the MCP channel: nothing else may write to it.
 * All diagnostics go to stderr.
 */
import { execFile as execFileCb } from "node:child_process";
import { readFile } from "node:fs/promises";
import { networkInterfaces } from "node:os";
import { promisify } from "node:util";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { DEFAULT_LOCAL_CONFIG_PATH, loadConfig } from "./config.js";
import { createAppServer, stderrLog } from "./server.js";
import type { ExecFileFn } from "./vmAddress.js";

// Belt and braces: anything that calls console.log must not corrupt the protocol stream.
console.log = (...args: unknown[]) => console.error(...args);
console.info = (...args: unknown[]) => console.error(...args);

const execFileP = promisify(execFileCb);

/** Runs a program directly (no shell). */
const execFile: ExecFileFn = async (file, args, opts) => {
  const { stdout } = await execFileP(file, args, {
    timeout: opts.timeoutMs,
    encoding: "utf8",
    maxBuffer: 1024 * 1024,
    windowsHide: true,
  });
  return stdout;
};

async function main(): Promise<void> {
  const { config, warnings } = loadConfig(process.env, undefined, DEFAULT_LOCAL_CONFIG_PATH);
  for (const w of warnings) stderrLog(`warning: ${w}`);

  const server = createAppServer(config, {
    execFile,
    readFile: (path) => readFile(path, "utf8"),
    networkInterfaces,
    log: stderrLog,
  });

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
