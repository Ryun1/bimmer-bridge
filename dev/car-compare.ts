#!/usr/bin/env node
/**
 * Car comparison (M2): run a job list through the VM agent, then (with the cable moved from UTM
 * to the Mac) through ediabasd, and compare the two runs.
 *
 * `run` TALKS TO THE CAR. Run it only at the car, with Ryan there and his go for that session
 * (M2 plan, tasks 8 and 9), a charger on and the ignition-on time watched. Each job is sent
 * once, on its own, so one failure never hides the rest; nothing is ever resent. Leave the 24
 * gate-refused reads (m1-results section 2) out of every native job list.
 *
 *   (cd mac-mcp && npm run build)
 *   DOTNET_ROOT=/opt/homebrew/opt/dotnet@10/libexec /opt/homebrew/opt/dotnet@10/bin/dotnet build -c Release engine/ediabasd-net
 *   node dev/car-compare.ts run --side vm --jobs dev/m2/session-c.json --out ~/m2/c-vm.jsonl
 *   node dev/car-compare.ts run --side native --ediabasd-config ~/m2/ediabasd-c.json --jobs dev/m2/session-c.json --out ~/m2/c-native.jsonl
 *   node dev/car-compare.ts compare ~/m2/c-vm.jsonl ~/m2/c-native.jsonl
 *
 * The VM side finds the agent as the MCP server does (config.local.json, Keychain token). The
 * native side starts ediabasd serve (default: this repo's Release build) with DOTNET_ROOT from
 * the environment, else Homebrew's dotnet@10. Outputs stay local under ~/m2/: a C_FA_LESEN
 * reply holds the VIN. An existing --out is never overwritten.
 */
import { execFile as execFileCb } from "node:child_process";
import { appendFileSync, existsSync, readFileSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { networkInterfaces } from "node:os";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
// Types from mac-mcp's sources; the code itself from its build (mac-mcp/dist), as describe-diff.ts does.
import type * as ClientModule from "../mac-mcp/src/agentClient.ts";
import type { EdiabasBackend } from "../mac-mcp/src/backend.ts";
import type * as ConfigModule from "../mac-mcp/src/config.ts";
import type * as ErrorsModule from "../mac-mcp/src/errors.ts";
import type * as NativeModule from "../mac-mcp/src/nativeClient.ts";
import type * as TokenModule from "../mac-mcp/src/token.ts";
import type { ExecFileFn } from "../mac-mcp/src/vmAddress.ts";
import { compareJobResults, countIfh0003, readJobList, type Response, type RunLine } from "./car-compare-lib.ts";

const repo = resolve(fileURLToPath(new URL("..", import.meta.url)));
const DEFAULT_DOTNET_ROOT = "/opt/homebrew/opt/dotnet@10/libexec";
const USAGE =
  "usage: car-compare.ts run --side vm|native --jobs FILE.json --out FILE.jsonl [--ediabasd PATH] [--ediabasd-config PATH]\n" +
  "       car-compare.ts compare VM.jsonl NATIVE.jsonl";

interface RunArgs {
  side: "vm" | "native";
  jobs: string;
  out: string;
  ediabasd: string;
  ediabasdConfig: string | undefined;
}

function parseRunArgs(argv: string[]): RunArgs {
  const a: Partial<RunArgs> = {};
  for (let i = 0; i < argv.length; i++) {
    const k = argv[i];
    const v = () => {
      const x = argv[++i];
      if (x === undefined) throw new Error(`${k} needs a value`);
      return x;
    };
    if (k === "--side") {
      const s = v();
      if (s !== "vm" && s !== "native") throw new Error("--side is vm or native");
      a.side = s;
    } else if (k === "--jobs") a.jobs = v();
    else if (k === "--out") a.out = v();
    else if (k === "--ediabasd") a.ediabasd = v();
    else if (k === "--ediabasd-config") a.ediabasdConfig = v();
    else throw new Error(`unknown argument ${k}`);
  }
  if (!a.side || !a.jobs || !a.out) throw new Error(USAGE);
  return {
    side: a.side,
    jobs: a.jobs,
    out: a.out,
    ediabasd: a.ediabasd ?? join(repo, "engine/ediabasd-net/bin/Release/net10.0/ediabasd"),
    ediabasdConfig: a.ediabasdConfig,
  };
}

const execFileP = promisify(execFileCb);
const execFile: ExecFileFn = async (file, args, opts) => {
  const { stdout } = await execFileP(file, args, { timeout: opts.timeoutMs, encoding: "utf8" });
  return stdout;
};

const dist = (name: string) => new URL(`../mac-mcp/dist/${name}.js`, import.meta.url).href;

interface Side {
  backend: EdiabasBackend;
  check: () => Promise<string>;
  close?: () => void;
}

async function vmBackend(): Promise<Side> {
  const { AgentClient } = (await import(dist("agentClient"))) as typeof ClientModule;
  const { DEFAULT_LOCAL_CONFIG_PATH, loadConfig } = (await import(dist("config"))) as typeof ConfigModule;
  const { TokenProvider } = (await import(dist("token"))) as typeof TokenModule;
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
  const client = new AgentClient({ config, tokens, sources: { readFile: (p) => readFile(p, "utf8"), execFile, networkInterfaces }, log: (m) => log(tokens.redact(m)) });
  return {
    backend: client,
    check: async () => {
      const h = await client.health();
      if (h.conflictingProcesses.length) throw new Error(`a BMW tool holds the cable in the VM: ${h.conflictingProcesses.map((p) => p.name).join(", ")}. Close it first.`);
      return `VM agent ${h.version}, policy version ${h.policyVersion ?? "?"}`;
    },
  };
}

async function nativeBackend(args: RunArgs): Promise<Side> {
  const { NativeClient } = (await import(dist("nativeClient"))) as typeof NativeModule;
  if (!existsSync(args.ediabasd)) throw new Error(`${args.ediabasd} not found; build engine/ediabasd-net (Release) first`);
  const client = new NativeClient({
    binary: args.ediabasd,
    configPath: args.ediabasdConfig,
    env: { DOTNET_ROOT: process.env.DOTNET_ROOT ?? DEFAULT_DOTNET_ROOT },
    log: (m) => console.error(m),
  });
  return {
    backend: client,
    close: () => client.close(),
    check: async () => {
      const h = await client.health();
      if (h.cable.state !== "free") throw new Error(`the cable is ${h.cable.state}${h.cable.port ? ` (${h.cable.port})` : ""}, not free; untick it in UTM's USB menu if the VM has it`);
      return `ediabasd ${h.version}, libkline ${h.kline}, cable free at ${h.cable.port ?? "?"}, policy version ${h.policyVersion ?? "?"}`;
    },
  };
}

async function run(args: RunArgs): Promise<number> {
  if (existsSync(args.out)) throw new Error(`${args.out} exists; choose another --out (a run is never repeated into the same file)`);
  const jobs = readJobList(readFileSync(args.jobs, "utf8"));
  const { BridgeError } = (await import(dist("errors"))) as typeof ErrorsModule;
  const side = args.side === "vm" ? await vmBackend() : await nativeBackend(args);
  try {
    // Before any job: the backend answers and the cable is where this side needs it.
    console.log(`${args.side}: ${await side.check()}`);
    const started = Date.now();
    for (const j of jobs) {
      const t0 = Date.now();
      let response: Response;
      try {
        response = { ok: true, result: await side.backend.runJob({ ecu: j.ecu, job: j.job, args: j.args, results: j.results, timeoutSeconds: j.timeoutSeconds }) };
      } catch (e) {
        if (!(e instanceof BridgeError)) throw e;
        response = { ok: false, code: e.code, message: e.message };
      }
      const line: RunLine = { ecu: j.ecu, job: j.job, response, at: new Date().toISOString() };
      appendFileSync(args.out, `${JSON.stringify(line)}\n`);
      console.log(`${j.ecu} ${j.job}: ${response.ok ? "ok" : `[${response.code}]`} (${Date.now() - t0} ms)`);
    }
    console.log(`${jobs.length} jobs in ${Math.round((Date.now() - started) / 1000)} s; written to ${args.out}`);
    return 0;
  } finally {
    side.close?.();
  }
}

function readRun(path: string): RunLine[] {
  return readFileSync(path, "utf8")
    .split("\n")
    .filter((l) => l.trim())
    .map((l) => JSON.parse(l) as RunLine);
}

function show(v: unknown): string {
  const s = JSON.stringify(v);
  return s === undefined ? "(absent)" : s.length > 120 ? `${s.slice(0, 119)}…` : s;
}

function compare(vmPath: string, nativePath: string): number {
  const vm = readRun(vmPath);
  const native = readRun(nativePath);
  if (vm.length !== native.length) console.log(`The runs differ in length: VM ${vm.length} jobs, native ${native.length}; comparing the first ${Math.min(vm.length, native.length)}.`);
  const tally = new Map<string, number>();
  for (let i = 0; i < Math.min(vm.length, native.length); i++) {
    const a = vm[i]!;
    const b = native[i]!;
    if (a.ecu !== b.ecu || a.job !== b.job) throw new Error(`job ${i} is ${a.ecu} ${a.job} in the VM run but ${b.ecu} ${b.job} natively: not the same job list`);
    const c = compareJobResults(a.response, b.response);
    tally.set(c.kind, (tally.get(c.kind) ?? 0) + 1);
    console.log(`${a.ecu} ${a.job}: ${c.kind}`);
    if (!a.response.ok) console.log(`  VM: [${a.response.code}] ${a.response.message}`);
    if (!b.response.ok) console.log(`  native: [${b.response.code}] ${b.response.message}`);
    for (const d of c.differences) console.log(`  ${d.path}: VM ${show(d.vm)}, native ${show(d.native)}`);
    if (c.note) console.log(`  note: ${c.note}`);
  }
  console.log([...tally].map(([k, n]) => `${k} ${n}`).join(", "));
  console.log(`IFH-0003: VM ${countIfh0003(vm)}, native ${countIfh0003(native)}`);
  return [...tally.keys()].every((k) => k === "match") ? 0 : 1;
}

async function main(argv: string[]): Promise<number> {
  const [cmd, ...rest] = argv;
  if (cmd === "run") return run(parseRunArgs(rest));
  if (cmd === "compare" && rest.length === 2) return compare(rest[0]!, rest[1]!);
  console.error(USAGE);
  return 2;
}

main(process.argv.slice(2)).then(
  (code) => (process.exitCode = code),
  (e: unknown) => {
    console.error(e instanceof Error ? e.message : String(e));
    process.exitCode = 1;
  },
);
