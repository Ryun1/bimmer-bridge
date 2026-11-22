import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";

/**
 * Synthetic recordings for the tests, written only into the tests' temporary folders. None of
 * this is an EDIABAS response from a real car or a real SGBD: names and values are made up and
 * say so (SYNTH_*), and nothing here may be copied into dev/fixtures/.
 */

export const TOKEN = "synthetic-test-token-0123456789abcdefghijk";
/** VIN-shaped but obviously not a real one. */
export const FAKE_VIN = "SYNTH00000TEST123";

export interface SynthExchange {
  method: "GET" | "POST";
  path: string;
  query?: Record<string, string>;
  body?: unknown;
  status?: number;
  headers?: Record<string, string>;
  response?: unknown;
  bodyText?: string;
  image?: { file: string; bytes: Buffer };
  recordedAt?: string;
}

let seq = 0;

/** Write one recording in the format dev/recorder.ts uses. Returns its file name. */
export function writeRecording(dir: string, x: SynthExchange, name?: string): string {
  mkdirSync(dir, { recursive: true });
  seq++;
  const file = name ?? `20261121T120000${String(seq).padStart(3, "0")}Z-${String(seq).padStart(4, "0")}-${x.method}-${x.path.replace(/\W+/g, "_")}.json`;
  const response: Record<string, unknown> = {
    status: x.status ?? 200,
    headers: x.headers ?? { "content-type": "application/json; charset=utf-8" },
  };
  if (x.image) {
    writeFileSync(join(dir, x.image.file), x.image.bytes);
    response.imageFile = x.image.file;
  } else if (x.bodyText !== undefined) {
    response.bodyText = x.bodyText;
  } else {
    response.body = x.response ?? null;
  }
  const record = {
    recordedAt: x.recordedAt ?? `2026-11-21T12:00:00.${String(seq % 1000).padStart(3, "0")}Z`,
    request: { method: x.method, path: x.path, query: x.query ?? {}, body: x.body ?? null },
    response,
  };
  writeFileSync(join(dir, file), JSON.stringify(record, null, 2) + "\n");
  return file;
}

export function writeScenarioFile(dir: string, spec: unknown): void {
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, "scenario.json"), JSON.stringify(spec, null, 2) + "\n");
}

export function synthHealth(o: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    agent: "ediabas-bridge-agent",
    version: "0.2.0",
    startedAt: "2026-11-21T12:00:00Z",
    uptimeSeconds: 60,
    os: { description: "Synthetic OS", osArchitecture: "Arm64", processArchitecture: "X86" },
    user: "synthuser",
    elevated: false,
    sessionId: 1,
    bindAddress: "127.0.0.1",
    port: 8765,
    gateway: "127.0.0.1",
    conflictingProcesses: [],
    ediabas: { dllPath: "C:\\SYNTH\\api32.dll", dllFound: true },
    lastCableUseAt: null,
    policyVersion: 3,
    capabilities: ["ediabas", "capture", "logs"],
    ...o,
  };
}

export function synthJobResult(ecu: string, job: string, sets: Record<string, unknown>[]): Record<string, unknown> {
  return {
    ecu,
    job,
    variant: ecu,
    jobStatus: "OKAY",
    systemResults: { VARIANTE: ecu, SAETZE: sets.length },
    sets,
    durationMs: 10,
  };
}

export function agentError(code: string, message: string, details: unknown = null): unknown {
  return { error: { code, message, details } };
}
