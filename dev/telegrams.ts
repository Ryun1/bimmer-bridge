#!/usr/bin/env node
/**
 * Telegrams (M2): compare EDIABAS's IFH trace from the VM with a libkline recording of the same
 * read, and measure echo and reply times. Reads files only; talks to nothing.
 *
 *   node dev/telegrams.ts compare ~/m2/b-ifh.trc ~/m2/b/kline-….jsonl
 *   node dev/telegrams.ts timing ~/m2/b/*.jsonl
 *
 * Telegrams are compared without checksums (the trace's INPUT has none) and without the
 * K-line echo (the recording has it). Recordings and traces stay local under ~/m2/: a
 * C_FA_LESEN reply holds the VIN.
 */
import { readFileSync } from "node:fs";
import { basename } from "node:path";
import { compareExchanges, hex, parseIfhTrace, parseRecording, timing, type Exchange, type Stats } from "./telegrams-lib.ts";

const USAGE = "usage: telegrams.ts compare IFH.trc RECORDING.jsonl | telegrams.ts timing RECORDING.jsonl…";

function side(name: string, x: Exchange | null): string[] {
  if (!x) return [`  ${name}: (none: this side ended)`];
  const out = [`  ${name} request:  ${hex(x.request)}`, `  ${name} response: ${hex(x.response)}`];
  if (x.error) out.push(`  ${name} error:    ${x.error}`);
  return out;
}

function compare(tracePath: string, recordingPath: string): number {
  const vm = parseIfhTrace(readFileSync(tracePath, "latin1"));
  const rec = parseRecording(readFileSync(recordingPath, "utf8"));
  console.log(`VM trace: ${vm.length} SENDECUTEL exchanges; recording: ${rec.exchanges.length} sends (latency ${rec.latency ?? "not recorded"}).`);
  const r = compareExchanges(vm, rec.exchanges);
  if (!r.firstDivergence) {
    console.log(`All ${r.matched} exchanges match (checksums and echoes aside).`);
    return 0;
  }
  console.log(`${r.matched} exchanges match, then they diverge at exchange ${r.firstDivergence.index}:`);
  for (const l of [...side("VM    ", r.firstDivergence.vm), ...side("native", r.firstDivergence.native)]) console.log(l);
  return 1;
}

const ms = (s: Stats) => (s.n === 0 ? "-" : `${s.minMs.toFixed(1)} / ${s.medianMs.toFixed(1)} / ${s.maxMs.toFixed(1)}`);

function timingTable(paths: string[]): number {
  console.log(["recording", "latency", "echoes", "echo ms min/median/max", "replies", "reply ms min/median/max", "short reads", "refused"].join("\t"));
  for (const p of paths) {
    const r = parseRecording(readFileSync(p, "utf8"));
    const t = timing(r.exchanges);
    console.log([basename(p), r.latency ?? "-", t.echo.n, ms(t.echo), t.reply.n, ms(t.reply), r.shortReads, r.refused].join("\t"));
  }
  return 0;
}

function main(argv: string[]): number {
  const [cmd, ...rest] = argv;
  if (cmd === "compare" && rest.length === 2) return compare(rest[0]!, rest[1]!);
  if (cmd === "timing" && rest.length >= 1) return timingTable(rest);
  console.error(USAGE);
  return 2;
}

process.exitCode = main(process.argv.slice(2));
