import { execFile as execFileCb } from "node:child_process";
import { readFile } from "node:fs/promises";
import { networkInterfaces } from "node:os";
import { promisify } from "node:util";
import type { AppDeps } from "./server.js";
import { stderrLog } from "./server.js";
import type { ExecFileFn } from "./vmAddress.js";

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

/** The production system access for createAppServer (the real shell, files, network, stderr). */
export function defaultDeps(): AppDeps {
  return {
    execFile,
    readFile: (path) => readFile(path, "utf8"),
    networkInterfaces,
    log: stderrLog,
  };
}
