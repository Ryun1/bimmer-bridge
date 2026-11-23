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
 *   (cd mac-mcp && npm ci && npm run build)
 *   node dev/describe-diff.ts --ecu-path ~/ediabas-ecu/Ecu --out describe-diff.jsonl [--only NVE_65 …] [--limit N]
 *
 * One JSON line per ECU goes to --out; ECUs already in it are skipped, so a run can be resumed.
 *
 * Sample mode, for jobs a list run could not check (the VM agent's 60 s budget ran out before
 * their comments): --from takes earlier --out files (a later file's row for an ECU replaces an
 * earlier one's), and for each differ row --sample N single-job describes go to both sides, for
 * N of the jobs whose comment only the VM lacked, spread over the SGBD. Each single-job request
 * has its own budget. One JSON line per job; (ecu, job) pairs already in --out are skipped.
 *
 *   node dev/describe-diff.ts --ecu-path ~/ediabas-ecu/Ecu --out sample.jsonl --sample 3 --from full.jsonl --from recheck.jsonl
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
// Types from mac-mcp's sources; the code itself from its build (mac-mcp/dist), because its
// sources import ".js" specifiers that only resolve once compiled.
import type { AgentClient } from "../mac-mcp/src/agentClient.ts";
import type * as ConfigModule from "../mac-mcp/src/config.ts";
import type * as ErrorsModule from "../mac-mcp/src/errors.ts";
import type * as TokenModule from "../mac-mcp/src/token.ts";
import type * as ClientModule from "../mac-mcp/src/agentClient.ts";
import type { ExecFileFn } from "../mac-mcp/src/vmAddress.ts";
import { diffDescribe, diffJob, spread, uncheckedJobs, type RunRow, type Side } from "./describe-diff-lib.ts";

const repo = resolve(fileURLToPath(new URL("..", import.meta.url)));

interface Args {
  ecuPath: string;
  out: string;
  only: string[];
  limit: number | undefined;
  ediabasd: string;
  sample: number | undefined;
  from: string[];
}

function parseArgs(argv: string[]): Args {
  const a: Partial<Args> & { only: string[]; from: string[] } = { only: [], from: [] };
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
    else if (k === "--sample") a.sample = Number.parseInt(v(), 10);
    else if (k === "--from") a.from.push(v());
    else throw new Error(`unknown argument ${k}`);
  }
  if (!a.ecuPath || !a.out) throw new Error("usage: describe-diff.ts --ecu-path DIR --out FILE.jsonl [--only NAME]… [--limit N] [--ediabasd PATH] [--sample N --from FILE.jsonl…]");
  if ((a.sample === undefined) !== (a.from.length === 0) || (a.sample !== undefined && !(a.sample >= 1))) {
    throw new Error("--sample N (N >= 1) and --from FILE go together");
  }
  return {
    ecuPath: a.ecuPath,
    out: a.out,
    only: a.only,
    limit: a.limit,
    ediabasd: a.ediabasd ?? join(repo, "engine/ediabasd-net/bin/Debug/net10.0/ediabasd"),
    sample: a.sample,
    from: a.from,
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
    let stderr = "";
    this.child.stderr.on("data", (d: Buffer) => {
      stderr = (stderr + d.toString()).slice(-2000);
    });
    // A child that dies (no DOTNET_ROOT for dotnet 10, a config error, a stuck job's exit 3)
    // fails every pending request loudly instead of leaving the run to end silently.
    this.child.on("exit", (code) => {
      this.dead = `ediabasd exited with code ${code}: ${stderr.trim()}`;
      for (const [, done] of this.waiting) {
        done({ ok: false, error: { code: "ediabasd_exited", message: this.dead } });
      }
      this.waiting.clear();
    });
    createInterface({ input: this.child.stdout }).on("line", (line) => {
      const msg = JSON.parse(line) as Record<string, unknown>;
      const done = this.waiting.get(msg.id as number);
      this.waiting.delete(msg.id as number);
      done?.(msg);
    });
  }

  private dead: string | undefined;

  request(method: string, params: unknown): Promise<Side> {
    if (this.dead !== undefined) return Promise.reject(new Error(this.dead));
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

const dist = (name: string) => new URL(`../mac-mcp/dist/${name}.js`, import.meta.url).href;
const { AgentClient: AgentClientImpl } = (await import(dist("agentClient"))) as typeof ClientModule;
const { DEFAULT_LOCAL_CONFIG_PATH, loadConfig } = (await import(dist("config"))) as typeof ConfigModule;
const { BridgeError } = (await import(dist("errors"))) as typeof ErrorsModule;
const { TokenProvider } = (await import(dist("token"))) as typeof TokenModule;

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
  return new AgentClientImpl({
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

async function vmDescribeJob(client: AgentClient, ecu: string, job: string): Promise<Side> {
  try {
    return { ok: true, value: await client.describeJob({ ecu, job }) };
  } catch (e) {
    if (e instanceof BridgeError) return { ok: false, code: e.code, message: e.message };
    throw e;
  }
}

const readRows = (path: string): Record<string, unknown>[] =>
  readFileSync(path, "utf8")
    .split("\n")
    .filter((l) => l.trim())
    .map((l) => JSON.parse(l) as Record<string, unknown>);

async function sampleMain(args: Args): Promise<void> {
  const rows = new Map<string, RunRow>();
  for (const f of args.from) for (const r of readRows(f)) rows.set(String(r.ecu), r as unknown as RunRow);
  const done = new Set(existsSync(args.out) ? readRows(args.out).map((r) => `${String(r.ecu)}#${String(r.job)}`) : []);

  const native = new Ediabasd(args.ediabasd, args.ecuPath);
  const client = vmClient();
  let count = 0;
  for (const [ecu, row] of rows) {
    if (args.only.length > 0 && !args.only.includes(ecu)) continue;
    const picks = spread(uncheckedJobs(row), args.sample ?? 1);
    if (picks.length === 0) continue;
    if (args.limit !== undefined && count >= args.limit) break;
    count++;
    // Names by index from ediabasd's list (offline, no comments needed).
    const list = await native.request("describe", { ecu, comments: false });
    if (!list.ok) throw new Error(`ediabasd describe ${ecu} failed: ${list.code}: ${list.message ?? ""}`);
    const names = ((list.value as { jobs?: { name: string }[] }).jobs ?? []).map((j) => j.name);
    for (const i of picks) {
      const job = names[i];
      if (job === undefined || done.has(`${ecu}#${job}`)) continue;
      const vm = await vmDescribeJob(client, ecu, job);
      const nat = await native.request("describe", { ecu, job });
      if (!nat.ok && nat.code === "ediabasd_exited") throw new Error(nat.message);
      const d = diffJob(vm, nat);
      appendFileSync(args.out, JSON.stringify({ ecu, job, kind: d.kind, differences: d.differences, vmError: vm.ok ? null : vm.code, nativeError: nat.ok ? null : nat.code }) + "\n");
      console.error(`${ecu} ${job}: ${d.kind}`);
    }
  }
  native.close();
}

async function main(): Promise<void> {
  const args = parseArgs(process.argv.slice(2));
  if (!existsSync(args.ediabasd)) throw new Error(`${args.ediabasd} not found; build engine/ediabasd-net first`);
  if (args.sample !== undefined) {
    await sampleMain(args);
    printTally(args.out);
    return;
  }
  const done = new Set<string>();
  if (existsSync(args.out)) {
    for (const line of readFileSync(args.out, "utf8").split("\n")) {
      if (line.trim()) done.add(String((JSON.parse(line) as Record<string, unknown>).ecu));
    }
  }

  const native = new Ediabasd(args.ediabasd, args.ecuPath);
  const client = vmClient();
  const listed = await native.request("ecus", {});
  if (!listed.ok) throw new Error(`ediabasd ecus failed: ${listed.code}: ${listed.message ?? ""}`);
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
    if (!nat.ok && nat.code === "ediabasd_exited") throw new Error(nat.message);
    const d = diffDescribe(vm, nat);
    appendFileSync(args.out, JSON.stringify({ ecu, kind: d.kind, differences: d.differences, vmError: vm.ok ? null : vm.code, nativeError: nat.ok ? null : nat.code }) + "\n");
    console.error(`${ecu}: ${d.kind}`);
  }
  native.close();
  printTally(args.out);
}

function printTally(out: string): void {
  const tally = new Map<string, number>();
  for (const r of readRows(out)) tally.set(String(r.kind), (tally.get(String(r.kind)) ?? 0) + 1);
  for (const [k, n] of tally) console.log(`${k.padEnd(14)} ${n}`);
}

main().catch((e: unknown) => {
  console.error(e instanceof Error ? e.message : String(e));
  process.exit(1);
});
