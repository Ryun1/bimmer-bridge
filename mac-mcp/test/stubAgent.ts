import type { IncomingMessage, ServerResponse } from "node:http";
import { AGENT_TOKEN, healthBody, json, windowsBody, type Handler } from "./fixtures.js";

/**
 * A stub of the VM agent for the Phase 2/4/5/6 endpoints, following docs/api.md. Every
 * response here is synthetic test data, not a recording of a real ECU.
 */

export interface StubRequest {
  method: string;
  path: string;
  query: URLSearchParams;
  body: unknown;
}

export type Reply =
  | { status: number; body: unknown; headers?: Record<string, string> }
  | { status: number; raw: Buffer; headers: Record<string, string> };

export type Responder = (r: StubRequest) => Reply;

export function agentErr(status: number, code: string, message: string, details?: unknown): Reply {
  const error: Record<string, unknown> = { code, message };
  if (details !== undefined) error.details = details;
  return { status, body: { error } };
}

export function phase2Health(o: Record<string, unknown> = {}): Record<string, unknown> {
  return healthBody({ policyVersion: 4, capabilities: ["ediabas", "capture", "logs"], conflictingProcesses: [], ...o });
}

export function jobResult(ecu: string, job: string, sets: Record<string, unknown>[], o: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    ecu: ecu.toUpperCase(),
    job: job.toUpperCase(),
    variant: ecu.toUpperCase(),
    jobStatus: "OKAY",
    systemResults: { VARIANTE: ecu.toUpperCase(), SAETZE: sets.length },
    sets,
    durationMs: 300,
    ...o,
  };
}

/** Two synthetic FS_LESEN fault sets with standard field names, then the status set. */
export function faultSets(): Record<string, unknown>[] {
  return [
    {
      F_VERSION: 2,
      F_ORT_NR: 42834,
      F_ORT_TEXT: "Synthetic fault A",
      F_SYMPTOM_NR: 2,
      F_SYMPTOM_TEXT: "Signal too low",
      F_VORHANDEN_NR: 1,
      F_VORHANDEN_TEXT: "Fault currently present",
      F_HFK: 3,
      F_UW_KM: 160935,
      F_UW_ANZ: 2,
      F_UW1_NR: 1,
      F_UW1_TEXT: "Engine speed",
      F_UW1_WERT: 820,
      F_UW1_EINH: "1/min",
      F_Uw2_WERT: 90,
      F_UW2_TEXT: "Coolant temperature",
      F_UW2_EINH: "degC",
      JOB_STATUS: "OKAY",
    },
    { F_ORT_NR: 0x2f, F_ORT_TEXT: "Synthetic fault B", F_VORHANDEN_TEXT: "Fault stored", F_HFK: -1, MYSTERY_FIELD: "kept" },
  ];
}

export function ecuOf(body: unknown): string {
  return String((body as { ecu?: unknown }).ecu ?? "");
}

export function defaultResponders(): Record<string, Responder> {
  return {
    "GET /health": () => ({ status: 200, body: phase2Health() }),
    "GET /windows": () => ({ status: 200, body: windowsBody() }),
    "GET /screen/capture": (r) => ({
      status: 200,
      raw: Buffer.from([0xff, 0xd8, 0xff, 0xe0, 1, 2, 3, 0xff, 0xd9]),
      headers: {
        "content-type": "image/jpeg",
        "x-window-title": r.query.get("window") === "desktop" ? "desktop" : encodeURIComponent("INPA - BMW Group ü"),
        "x-window-handle": "0x000A0B2C",
        "x-capture-method": "printwindow",
        "x-image-width": "1280",
        "x-image-height": "800",
      },
    }),
    "GET /logs": () => ({
      status: 200,
      body: {
        roots: [
          { index: 0, path: "C:\\EDIABAS\\TRACE", exists: true },
          { index: 1, path: "C:\\NCSEXPER\\WORK", exists: false },
        ],
        files: [{ path: "C:\\EDIABAS\\TRACE\\api.trc", root: 0, sizeBytes: 20480, modifiedAt: "2026-11-21T16:40:02Z" }],
        truncated: false,
      },
    }),
    "GET /logs/read": (r) => ({
      status: 200,
      body: {
        path: r.query.get("path"),
        sizeBytes: 20480,
        modifiedAt: "2026-11-21T16:40:02Z",
        offset: 0,
        length: 20480,
        encoding: "utf-8",
        truncated: false,
        grep: r.query.get("grep") ? { pattern: r.query.get("grep"), matchedLines: 2 } : null,
        text: "line one\r\nline\ttwo\u001b[31m\nline three",
      },
    }),
    "GET /ediabas/ecus": () => ({
      status: 200,
      body: {
        ecuPath: "C:\\EDIABAS\\ECU",
        count: 3,
        truncated: false,
        files: [
          { name: "D_MOTOR", type: "grp" },
          { name: "MS450DS0", type: "prg" },
          { name: "NVE_65", type: "prg" },
        ],
      },
    }),
    "GET /ediabas/describe": (r) => {
      const ecu = (r.query.get("ecu") ?? "").toUpperCase();
      const job = r.query.get("job");
      if (job) {
        return {
          status: 200,
          body: {
            ecu,
            variant: ecu,
            job: job.toUpperCase(),
            comment: "Fehlerspeicher lesen",
            verdict: { allowed: true, rule: "allow.global", review: "not_required" },
            arguments: [],
            results: [
              { name: "F_ORT_NR", type: "long", comment: "Index des Fehlerorts" },
              { name: "F_ORT_TEXT", type: "string", comment: null },
            ],
            durationMs: 95,
          },
        };
      }
      return {
        status: 200,
        body: {
          ecu,
          variant: ecu,
          jobCount: 4,
          jobs: [
            { name: "FS_LESEN", comment: "Fehlerspeicher lesen", verdict: { allowed: true, rule: "allow.global", review: "not_required" } },
            { name: "STEUERN_X", comment: "Stellglied ansteuern", verdict: { allowed: false, rule: "deny.substring:STEUER", review: "not_required" } },
            { name: "STATUS_RUN", comment: "Run something\u0007", verdict: { allowed: true, rule: "allow.pattern", review: "needs_review" } },
            { name: "STATUS_OK", comment: null, verdict: { allowed: true, rule: "allow.pattern", review: "passed" } },
          ],
          durationMs: 412,
        },
      };
    },
    "GET /ediabas/vehicle": () => ({
      status: 200,
      body: {
        ignition: { ok: true, result: jobResult("UTILITY", "STATUS_ZUENDUNG", [{ STAT_ZUENDUNG: 1, JOB_STATUS: "OKAY" }]), error: null },
        ubatt: { ok: true, result: jobResult("UTILITY", "STATUS_UBATT", [{ STAT_UBATT: 12480 }]), error: null },
        voltages: [{ ecu: "NVE_65", job: "STATUS_BORDNETZSPANNUNG", ok: true, result: jobResult("NVE_65", "STATUS_BORDNETZSPANNUNG", [{ STAT_SPANNUNG_WERT: 12.4 }]), error: null }],
        cableKl30: { ok: true, result: jobResult("TMODE", "LESE_SPANNUNG_KL30", [{ SPANNUNG_KL30: 12510 }]), error: null },
        cableKl15: { ok: false, result: null, error: { code: "ediabas_error", message: "failed", details: { errorCode: 159, errorText: "IFH-0009: NO RESPONSE FROM CONTROLUNIT" } } },
        durationMs: 2100,
      },
    }),
    "POST /ediabas/job": (r) => {
      const b = r.body as { ecu: string; job: string };
      return { status: 200, body: jobResult(b.ecu, b.job, [{ ID_BMW_NR: "1234567", KM_STAND: 160935, JOB_STATUS: "OKAY" }]) };
    },
    "POST /ediabas/jobs": (r) => {
      const items = (r.body as { items: { ecu: string; job: string }[] }).items;
      return {
        status: 200,
        body: {
          items: items.map((it) => ({
            ok: true,
            result: jobResult(it.ecu, it.job, it.job === "FS_LESEN" ? faultSets() : [{ JOB_STATUS: "OKAY" }]),
            error: null,
          })),
          durationMs: 1290,
        },
      };
    },
  };
}

const KNOWN_PATHS = ["/health", "/windows", "/screen/capture", "/logs", "/logs/read", "/ediabas/ecus", "/ediabas/describe", "/ediabas/vehicle", "/ediabas/job", "/ediabas/jobs"];

/** Routes like the agent: token (401), then path (404 not_found), then method (405). */
export function stubHandler(overrides: Record<string, Responder> = {}, token: string = AGENT_TOKEN): Handler {
  const responders = { ...defaultResponders(), ...overrides };
  return (req: IncomingMessage, res: ServerResponse) => {
    if (req.headers.authorization !== `Bearer ${token}`) {
      json(res, 401, { error: { code: "unauthorized", message: "Missing or invalid bearer token.", details: null } }, { "www-authenticate": "Bearer" });
      return;
    }
    const url = new URL(req.url ?? "/", "http://stub");
    const key = `${req.method} ${url.pathname}`;
    const responder = responders[key];
    if (!responder) {
      if (KNOWN_PATHS.includes(url.pathname)) json(res, 405, { error: { code: "method_not_allowed", message: "Wrong method.", details: null } });
      else json(res, 404, { error: { code: "not_found", message: "No such path.", details: null } });
      return;
    }
    const text = (req as IncomingMessage & { bodyText?: string }).bodyText ?? "";
    let body: unknown = null;
    if (text) body = JSON.parse(text);
    const reply = responder({ method: req.method ?? "", path: url.pathname, query: url.searchParams, body });
    if ("raw" in reply) {
      res.writeHead(reply.status, reply.headers);
      res.end(reply.raw);
    } else {
      json(res, reply.status, reply.body, reply.headers);
    }
  };
}
