/**
 * A scripted stand-in for `ediabasd serve`, for NativeClient's tests. Run with Node (which
 * strips the types). It reads JSON lines on stdin and acts on one script step per request.
 *
 * FAKE_EDIABASD_SCRIPT: a JSON array; step i answers the i-th request across every child that
 * shares the log, so a script can span a child that exits and the one started after it:
 *   { "answer": {...} }                 write {"id": <request id>, ...answer}
 *   { "delayMs": n, "answer": {...} }   the same, n ms later
 *   { "exit": n }                       exit with code n without answering
 *   { "hang": true }                    never answer
 *   { "raw": "text" }                   write this line as it is
 * FAKE_EDIABASD_LOG: a file; each request is appended as {"kind":"req",...} before it is acted
 * on, and each answer as {"kind":"ans",...} just before it is written. Every response here is synthetic.
 */
import { appendFileSync, existsSync, readFileSync } from "node:fs";
import { createInterface } from "node:readline";

interface Step {
  answer?: Record<string, unknown>;
  delayMs?: number;
  exit?: number;
  hang?: boolean;
  raw?: string;
}

const script = JSON.parse(process.env.FAKE_EDIABASD_SCRIPT ?? "[]") as Step[];
const log = process.env.FAKE_EDIABASD_LOG ?? "";

function requestsSoFar(): number {
  if (!existsSync(log)) return 0;
  return readFileSync(log, "utf8")
    .split("\n")
    .filter((l) => l.includes('"kind":"req"')).length;
}

function record(entry: Record<string, unknown>): void {
  if (log) appendFileSync(log, `${JSON.stringify(entry)}\n`);
}

const rl = createInterface({ input: process.stdin });
rl.on("line", (line) => {
  const index = requestsSoFar();
  let request: { id?: unknown } = {};
  try {
    request = JSON.parse(line) as { id?: unknown };
  } catch {
    // Logged as it came; the step decides what happens.
  }
  record({ kind: "req", index, line, argv: process.argv.slice(2), dotnetRoot: process.env.DOTNET_ROOT ?? null, pid: process.pid });
  const step = script[index] ?? { hang: true };
  if (step.exit !== undefined) process.exit(step.exit);
  if (step.hang) return;
  const write = () => {
    const out = step.raw ?? JSON.stringify({ id: request.id, ...step.answer });
    record({ kind: "ans", index });
    process.stdout.write(`${out}\n`);
  };
  if (step.delayMs) setTimeout(write, step.delayMs);
  else write();
});
