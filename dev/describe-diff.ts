#!/usr/bin/env node
/**
 * Describe comparison (M1 acceptance): for every .prg in an Ecu folder, describe it (list mode,
 * with comments) through the VM agent and through ediabasd, and compare (describe-diff-lib.ts).
 *
 * THIS RUNS EDIABAS IN THE VM (_JOBS, _JOBCOMMENTS), and real EDIABAS runs each SGBD's
 * INITIALISIERUNG first. Run it only with the cable unplugged from the car (it may stay in the
 * VM), at a desk session Ryan has started. Group files are not compared: natively they give
 * needs_car by design.
 *
 *   export DOTNET_ROOT=/opt/homebrew/opt/dotnet@10/libexec
 *   /opt/homebrew/opt/dotnet@10/bin/dotnet build engine/ediabasd-net
 *   node dev/describe-diff.ts --ecu-path ~/ediabas-ecu/Ecu --out describe-diff.jsonl [--only NVE_65 …] [--limit N]
 *
 * One JSON line per ECU goes to --out; ECUs already in it are skipped, so a run can be resumed.
 * The VM agent is found as the MCP server finds it (config.local.json, Keychain token).
 */
import { execFile as execFileCb, spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { appendFileSync, existsSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { networkInterfaces, tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { createInterface } from "node:readline";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { AgentClient } from "../mac-mcp/src/agentClient.ts";
import { DEFAULT_LOCAL_CONFIG_PATH, loadConfig } from "../mac-mcp/src/config.ts";
import { BridgeError } from "../mac-mcp/src/errors.ts";
import { TokenProvider } from "../mac-mcp/src/token.ts";
import type { ExecFileFn } from "../mac-mcp/src/vmAddress.ts";
import { diffDescribe, type DiffKind, type Side } from "./describe-diff-lib.ts";

const repo = resolve(fileURLToPath(new URL("..", import.meta.url)));

interface Args {
  ecuPath: string;
  out: string;
  only: string[];
  limit: number | undefined;
  ediabasd: string;
}

function parseArgs(argv: string[]): Args {
  const a: Partial<Args> & { only: string[] } = { only: [] };
  for (let i = 0; i < argv.length; i++) {
    const k = argv[i];
    const v = () => {
      const x = argv[++i];
      if (x === undefined) throw new Error(`${k} needs a value`);
      return x;
    };
    if (k === "--ecu-path") a.ecuPath = v();
    else if (k === "--out") a.out = v();
    else if (k === "--only") a.only.push(v().toUpperCase());
    else if (k === "--limit") a.limit = Number.parseInt(v(), 10);
    else if (k === "--ediabasd") a.ediabasd = v();
    else throw new Error(`unknown argument ${k}`);
  }
  if (!a.ecuPath || !a.out) throw new Error("usage: describe-diff.ts --ecu-path DIR --out FILE.jsonl [--only NAME]… [--limit N] [--ediabasd PATH]");
  return {
    ecuPath: a.ecuPath,
    out: a.out,
    only: a.only,
    limit: a.limit,
    ediabasd: a.ediabasd ?? join(repo, "engine/ediabasd-net/bin/Debug/net10.0/ediabasd"),
  };
}

/** ediabasd serve as a child, one request at a time. */
class Ediabasd {
  private readonly child: ChildProcessWithoutNullStreams;
  private readonly waiting = new Map<number, (line: Record<string, unknown>) => void>();
  private next = 1;

  constructor(binary: string, ecuPath: string) {
    const dir = mkdtempSync(join(tmpdir(), "describe-diff-"));
    const config = join(dir, "config.json");
    writeFileSync(config, JSON.stringify({ ecuPath }));
    this.child = spawn(binary, ["serve", "--config", config], {
      env: { ...process.env, XDG_STATE_HOME: join(dir, "state") },
      stdio: ["pipe", "pipe", "pipe"],
    });
    this.child.stderr.on("data", () => {});
    createInterface({ input: this.child.stdout }).on("line", (line) => {
      const msg = JSON.parse(line) as Record<string, unknown>;
      const done = this.waiting.get(msg.id as number);
      this.waiting.delete(msg.id as number);
      done?.(msg);
    });
  }

  request(method: string, params: unknown): Promise<Side> {
    const id = this.next++;
    return new Promise((res) => {
      this.waiting.set(id, (msg) =>
        res(msg.ok ? { ok: true, value: msg.result } : { ok: false, code: String((msg.error as Record<string, unknown>)?.code), message: String((msg.error as Record<string, unknown>)?.message) }),
      );
      this.child.stdin.write(JSON.stringify({ id, method, params }) + "\n");
    });
  }

  close(): void {
    this.child.stdin.end();
  }
}

const execFileP = promisify(execFileCb);
const execFile: ExecFileFn = async (file, args, opts) => {
  const { stdout } = await execFileP(file, args, { timeout: opts.timeoutMs, encoding: "utf8", maxBuffer: 1024 * 1024 });
  return stdout;
};

function vmClient(): AgentClient {
  const { config, warnings } = loadConfig(process.env, undefined, DEFAULT_LOCAL_CONFIG_PATH);
  for (const w of warnings) console.error(`warning: ${w}`);
  const log = (m: string) => console.error(m);
  const tokens = new TokenProvider({
    execFile,
    envToken: config.envToken,
    envOnly: config.agentUrl !== undefined,
    warn: log,
    service: config.keychainService,
    account: config.keychainAccount,
  });
  return new AgentClient({
    config,
    tokens,
    sources: { readFile: (p) => readFile(p, "utf8"), execFile, networkInterfaces },
    log: (m) => log(tokens.redact(m)),
  });
}

async function vmDescribe(client: AgentClient, ecu: string): Promise<Side> {
  try {
    return { ok: true, value: await client.describeJobs({ ecu, comments: true }) };
  } catch (e) {
    if (e instanceof BridgeError) return { ok: false, code: e.code, message: e.message };
    throw e;
  }
}

async function main(): Promise<void> {
  const args = parseArgs(process.argv.slice(2));
  if (!existsSync(args.ediabasd)) throw new Error(`${args.ediabasd} not found; build engine/ediabasd-net first`);
  const done = new Set<string>();
  if (existsSync(args.out)) {
    for (const line of readFileSync(args.out, "utf8").split("\n")) {
      if (line.trim()) done.add(String((JSON.parse(line) as Record<string, unknown>).ecu));
    }
  }

  const native = new Ediabasd(args.ediabasd, args.ecuPath);
  const client = vmClient();
  const listed = await native.request("ecus", {});
  if (!listed.ok) throw new Error(`ediabasd ecus failed: ${listed.code}`);
  const files = ((listed.value as { files: { name: string; type: string }[] }).files ?? [])
    .filter((f) => f.type === "prg")
    .map((f) => f.name.toUpperCase())
    .filter((n) => args.only.length === 0 || args.only.includes(n));

  let count = 0;
  for (const ecu of files) {
    if (done.has(ecu)) continue;
    if (args.limit !== undefined && count >= args.limit) break;
    count++;
    const vm = await vmDescribe(client, ecu);
    const nat = await native.request("describe", { ecu, comments: true });
    const d = diffDescribe(vm, nat);
    appendFileSync(args.out, JSON.stringify({ ecu, kind: d.kind, differences: d.differences, vmError: vm.ok ? null : vm.code, nativeError: nat.ok ? null : nat.code }) + "\n");
    console.error(`${ecu}: ${d.kind}`);
  }
  native.close();

  const tally = new Map<DiffKind, number>();
  for (const line of readFileSync(args.out, "utf8").split("\n")) {
    if (!line.trim()) continue;
    const k = (JSON.parse(line) as { kind: DiffKind }).kind;
    tally.set(k, (tally.get(k) ?? 0) + 1);
  }
  for (const [k, n] of tally) console.log(`${k.padEnd(14)} ${n}`);
}

main().catch((e: unknown) => {
  console.error(e instanceof Error ? e.message : String(e));
  process.exit(1);
});
