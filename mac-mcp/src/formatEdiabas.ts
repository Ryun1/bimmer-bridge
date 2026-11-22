import type { AgentErrorInfo, AgentVerdict, BatchItem, DescribeJob, DescribeList, EcuList, JobResult, Vehicle } from "./agentClient.js";
import { cleanText, describeAgentError, errorText } from "./errors.js";
import type { Verdict } from "./policy.js";

/**
 * Text for the EDIABAS tools (docs/tools.md "Output"). Everything from the VM is cleaned and
 * length-capped. Field names differ between SGBDs, so fault fields are mapped only when
 * present and every field that is not shown under a mapped label is passed through as
 * key=value: a wrong assumption about a name can never hide data.
 */

type ResultSet = Record<string, unknown>;

const VALUE_MAX = 300;

// ---------------------------------------------------------------------------
// Values and distances
// ---------------------------------------------------------------------------

function group(n: number): string {
  const s = String(Math.abs(Math.trunc(n))).replace(/\B(?=(\d{3})+(?!\d))/g, ",");
  return n < 0 ? `-${s}` : s;
}

/** `121,609 mi (195,712 km)`. Miles are whole miles covered, as an odometer shows them. */
export function formatKm(km: number): string {
  return `${group(Math.floor(km / 1.609344))} mi (${group(Math.round(km))} km)`;
}

/** A result value as text: numbers as is, strings cleaned and capped, null as `null`. */
export function valueText(v: unknown, max = VALUE_MAX): string {
  if (v === null || v === undefined) return "null";
  if (typeof v === "number") return Number.isFinite(v) ? String(v) : "null";
  if (typeof v === "boolean") return String(v);
  if (typeof v === "string") return cleanText(v, max);
  return cleanText(JSON.stringify(v) ?? "null", max);
}

/** For a line of several key=value pairs: quote strings that contain spaces or are empty. */
function inlineValue(v: unknown): string {
  const t = valueText(v);
  return typeof v === "string" && (t === "" || /\s|=/.test(t)) ? `"${t.replace(/"/g, "'")}"` : t;
}

function cleanName(s: string, max = 64): string {
  return cleanText(s, max) || "?";
}

/** Field names that hold a distance in km: a `KM` name token (not `KM_H`), or "kilometer". */
function isKmName(name: string): boolean {
  if (/(^|_)KM_H(_|$)/i.test(name)) return false;
  return /(^|_)KM(_|$)/i.test(name) || /KILOMETER/i.test(name);
}

/** Keys of a set whose numeric value is a distance in km: by name, or `X_WERT` with `X_EINH` = km. */
function kmKeys(set: ResultSet): Set<string> {
  const out = new Set<string>();
  for (const [k, v] of Object.entries(set)) {
    if (typeof v !== "number") continue;
    if (isKmName(k)) out.add(k);
    const m = /^(.*)_WERT$/i.exec(k);
    if (m) {
      const unitKey = Object.keys(set).find((u) => u.toUpperCase() === `${m[1]!.toUpperCase()}_EINH`);
      const unit = unitKey ? set[unitKey] : undefined;
      if (typeof unit === "string" && unit.trim().toLowerCase() === "km") out.add(k);
    }
  }
  return out;
}

/** One key=value per field, distances miles first. */
function fieldLines(set: ResultSet, indent: string): string[] {
  const km = kmKeys(set);
  return Object.entries(set).map(([k, v]) => `${indent}${cleanName(k)}=${km.has(k) && typeof v === "number" ? formatKm(v) : valueText(v)}`);
}

/** All fields on one line. */
function fieldsInline(set: ResultSet, keys: string[] = Object.keys(set)): string {
  const km = kmKeys(set);
  return keys.map((k) => `${cleanName(k)}=${km.has(k) && typeof set[k] === "number" ? formatKm(set[k] as number) : inlineValue(set[k])}`).join(" ");
}

function plural(n: number, one: string, many = `${one}s`): string {
  return `${n} ${n === 1 ? one : many}`;
}

/** Error text for a per-item error: the known message, else code and the agent's message. */
export function itemErrorText(err: AgentErrorInfo | null | undefined): string {
  if (!err) return "failed (the agent gave no error)";
  const code = cleanText(err.code, 64).replace(/[^A-Za-z0-9_.-]/g, "_") || "error";
  return errorText(code, describeAgentError(code, err.message, err.details) ?? cleanText(err.message || "(no message)"));
}

// ---------------------------------------------------------------------------
// Fault tables
// ---------------------------------------------------------------------------

function faultCode(v: unknown): string {
  if (typeof v === "number" && Number.isInteger(v) && v >= 0) return `0x${v.toString(16).toUpperCase().padStart(4, "0")}`;
  return valueText(v, 40);
}

function findKey(set: ResultSet, name: string): string | undefined {
  return Object.keys(set).find((k) => k.toUpperCase() === name);
}

function isFaultSet(set: ResultSet): boolean {
  return findKey(set, "F_ORT_NR") !== undefined || findKey(set, "F_ORT_TEXT") !== undefined;
}

/** One fault row. Mapped fields are consumed only when they are shown. */
function faultRow(set: ResultSet, n: number): string[] {
  const used = new Set<string>();
  const take = (name: string): { key: string; value: unknown } | undefined => {
    const key = findKey(set, name);
    if (key === undefined) return undefined;
    used.add(key);
    return { key, value: set[key] };
  };

  const code = take("F_ORT_NR");
  const text = take("F_ORT_TEXT");
  const head = `  ${n}. ${code ? faultCode(code.value) : "(no code)"}  ${text ? valueText(text.value) : "(no text)"}`;

  const details: string[] = [];
  const present = take("F_VORHANDEN_TEXT");
  if (present) details.push(`present/stored: ${valueText(present.value)}`);
  const freq = take("F_HFK");
  if (freq) details.push(freq.value === -1 ? "frequency: not counted" : `frequency: ${valueText(freq.value)}`);
  const km = findKey(set, "F_UW_KM");
  if (km !== undefined && typeof set[km] === "number") {
    used.add(km);
    details.push(`logged at: ${formatKm(set[km] as number)}`);
  }

  // Logged conditions F_UWi_TEXT / _WERT / _EINH (one SGBD spells it F_Uwi_WERT).
  const conds = new Map<number, { text?: string; wert?: string; einh?: string }>();
  for (const k of Object.keys(set)) {
    const m = /^F_UW(\d+)_(TEXT|WERT|EINH)$/i.exec(k);
    if (!m) continue;
    const i = Number(m[1]);
    const part = m[2]!.toUpperCase();
    const c = conds.get(i) ?? {};
    if (part === "TEXT") c.text = k;
    else if (part === "WERT") c.wert = k;
    else c.einh = k;
    conds.set(i, c);
  }
  const condTexts: string[] = [];
  for (const i of [...conds.keys()].sort((a, b) => a - b)) {
    const c = conds.get(i)!;
    if (c.text === undefined || c.wert === undefined) continue; // incomplete: leave to pass-through
    used.add(c.text);
    used.add(c.wert);
    const unit = c.einh !== undefined ? valueText(set[c.einh], 20) : "";
    if (c.einh !== undefined) used.add(c.einh);
    const value = unit.toLowerCase() === "km" && typeof set[c.wert] === "number" ? formatKm(set[c.wert] as number) : `${valueText(set[c.wert], 60)}${unit ? ` ${unit}` : ""}`;
    condTexts.push(`${valueText(set[c.text], 80)} = ${value}`);
  }

  const lines = [head];
  if (details.length) lines.push(`     ${details.join(" | ")}`);
  if (condTexts.length) lines.push(`     conditions: ${condTexts.join("; ")}`);
  const rest = Object.keys(set).filter((k) => !used.has(k));
  if (rest.length) lines.push(`     other: ${fieldsInline(set, rest)}`);
  return lines;
}

/** The fault table for one job result (FS_LESEN or IS_LESEN). */
export function formatFaultTable(result: JobResult, label: string): string[] {
  const sets = result.sets;
  const faults = sets.filter(isFaultSet);
  const status = result.jobStatus ? valueText(result.jobStatus, 60) : null;
  const ok = status === "OKAY";
  const lines: string[] = [];
  if (faults.length === 0) {
    lines.push(ok ? `  ${label}: no faults stored.` : `  ${label}: no fault entries returned (job status ${status ?? "not reported"}).`);
  } else {
    lines.push(`  ${label}: ${plural(faults.length, "entry", "entries")}${ok ? "" : ` (job status ${status ?? "not reported"})`}`);
  }
  let n = 0;
  sets.forEach((set, i) => {
    if (isFaultSet(set)) {
      lines.push(...faultRow(set, ++n));
      return;
    }
    // A set without a fault location: show anything beyond its job status.
    const extra = Object.keys(set).filter((k) => k.toUpperCase() !== "JOB_STATUS");
    if (extra.length) lines.push(`  (set ${i + 1}) ${fieldsInline(set, extra)}`);
  });
  return lines;
}

export interface FaultReport {
  ecu: string;
  /** FS_LESEN item, or an error that stands in the ECU's place. */
  faults: BatchItem | { error: string };
  info?: BatchItem | { error: string } | undefined;
}

function itemSection(item: BatchItem | { error: string }, label: string, info = false): string[] {
  if ("ok" in item) {
    if (item.ok && item.result) return formatFaultTable(item.result, label);
    // Not every module has an info memory; its SGBD then has no IS_LESEN. That is not a failure.
    if (info && item.error?.code === "job_not_found") return [`  ${label}: no info memory on this module.`];
    return [`  ${label}: ${item.ok ? "no result returned" : `error: ${itemErrorText(item.error)}`}`];
  }
  return [`  ${label}: error: ${item.error}`];
}

export function formatFaultReport(reports: FaultReport[], includeInfo: boolean): string {
  const jobs = includeInfo ? "FS_LESEN and IS_LESEN" : "FS_LESEN";
  const out = [`Fault memory for ${plural(reports.length, "ECU")} (${jobs}):`];
  for (const r of reports) {
    const variant = "ok" in r.faults && r.faults.result?.variant ? valueText(r.faults.result.variant, 64) : null;
    out.push("");
    out.push(`${cleanName(r.ecu)}${variant && variant !== r.ecu.toUpperCase() ? ` (variant ${variant})` : ""}`);
    out.push(...itemSection(r.faults, "Fault memory"));
    if (includeInfo && r.info) out.push(...itemSection(r.info, "Info memory", true));
  }
  return out.join("\n");
}

// ---------------------------------------------------------------------------
// Job results
// ---------------------------------------------------------------------------

export function formatJobResult(r: JobResult): string {
  const facts = [
    `job status ${r.jobStatus ? valueText(r.jobStatus, 60) : "not reported"}`,
    r.variant ? `variant ${valueText(r.variant, 64)}` : null,
    typeof r.durationMs === "number" ? `${Math.round(r.durationMs)} ms` : null,
    plural(r.sets.length, "result set"),
  ].filter(Boolean);
  const out = [`${cleanName(r.ecu)} ${cleanName(r.job)}: ${facts.join(", ")}`];
  if (r.systemResults && Object.keys(r.systemResults).length) out.push(`System results: ${fieldsInline(r.systemResults)}`);
  r.sets.forEach((set, i) => {
    out.push(`Set ${i + 1}:`);
    const lines = fieldLines(set, "  ");
    out.push(...(lines.length ? lines : ["  (empty)"]));
  });
  return out.join("\n");
}

/** Deep copy with every string cleaned, for `raw: true`. */
function cleanDeep(v: unknown): unknown {
  if (typeof v === "string") return cleanText(v, 2000);
  if (Array.isArray(v)) return v.map(cleanDeep);
  if (v && typeof v === "object") return Object.fromEntries(Object.entries(v).map(([k, x]) => [cleanText(k, 64), cleanDeep(x)]));
  return v;
}

export function formatJobRaw(r: JobResult): string {
  return JSON.stringify(cleanDeep(r), null, 2);
}

// ---------------------------------------------------------------------------
// Describe
// ---------------------------------------------------------------------------

const RUNNABLE_REVIEW = new Set(["not_required", "passed", "reviewed"]);

/** Combine the agent's verdict with the Mac's own: a job is runnable only if both allow it. */
function verdictText(agent: AgentVerdict, mac: Verdict | null): { runnable: boolean; text: string } {
  if (mac && !mac.allowed) return { runnable: false, text: `not allowed (Mac policy rule ${mac.rule})` };
  if (!agent.allowed) return { runnable: false, text: `not allowed (${cleanText(agent.rule, 60)})` };
  const review = cleanText(agent.review, 30);
  if (RUNNABLE_REVIEW.has(review)) return { runnable: true, text: "runnable" };
  if (review === "needs_review") return { runnable: false, text: "needs review (its description suggests more than a read; Ryan decides)" };
  if (review === "not_checked") return { runnable: false, text: "description not screened" };
  return { runnable: false, text: `not runnable (review ${review || "unknown"})` };
}

export function formatDescribeList(d: DescribeList, filter: string | undefined, mac: (job: string) => Verdict | null): string {
  const n = d.jobs.length;
  const runnable = d.jobs.filter((j) => verdictText(j.verdict, mac(j.name)).runnable).length;
  const variant = d.variant && d.variant.toUpperCase() !== d.ecu.toUpperCase() ? ` (variant ${valueText(d.variant, 64)})` : "";
  const out = [
    `${cleanName(d.ecu)}${variant}: ${plural(n, "job")}${filter ? ` matching "${cleanText(filter, 64)}"` : ""}, ${runnable} runnable through the bridge.`,
  ];
  for (const j of d.jobs) {
    const v = verdictText(j.verdict, mac(j.name));
    const comment = j.comment ? ` — ${cleanText(j.comment, 160)}` : "";
    out.push(`${cleanName(j.name)} [${v.text}]${comment}`);
  }
  return out.join("\n");
}

function paramLines(ps: DescribeJob["arguments"]): string[] {
  return ps.map((p) => `  ${cleanName(p.name)}${p.type ? ` (${cleanText(p.type, 30)})` : ""}${p.comment ? ` — ${cleanText(p.comment, 200)}` : ""}`);
}

/** The description hash, and for a job that needs review the reviewedJobs entry that pins it. */
function hashLines(d: DescribeJob): string[] {
  const review = cleanText(d.verdict.review, 30);
  // Only the shape the agent's config accepts: anything else would make a line that approves nothing.
  const hash = d.descriptionHash && /^[0-9a-f]{12}$/.test(d.descriptionHash) ? d.descriptionHash : "";
  if (review === "not_required" || !hash) return [];
  const out = [`Description hash: ${hash}`];
  if (review === "needs_review") {
    out.push(
      `To approve this job as it is described now, Ryan adds "${cleanName(d.ecu)}:${cleanName(d.job)}@${hash}" to reviewedJobs in C:\\ProgramData\\EdiabasBridge\\config.json in the VM (no restart needed).`,
    );
  }
  return out;
}

export function formatDescribeJob(d: DescribeJob, mac: Verdict | null): string {
  const v = verdictText(d.verdict, mac);
  const variant = d.variant && d.variant.toUpperCase() !== d.ecu.toUpperCase() ? ` (variant ${valueText(d.variant, 64)})` : "";
  const out = [`${cleanName(d.ecu)}${variant} ${cleanName(d.job)}: ${v.text}`];
  out.push(`Description: ${d.comment ? cleanText(d.comment, 600) : "(none)"}`);
  out.push(...hashLines(d));
  out.push(d.arguments.length ? "Arguments:" : "Arguments: none");
  out.push(...paramLines(d.arguments));
  out.push(d.results.length ? "Results:" : "Results: none listed");
  out.push(...paramLines(d.results));
  return out.join("\n");
}

// ---------------------------------------------------------------------------
// Vehicle status and ECU list
// ---------------------------------------------------------------------------

function readingText(item: BatchItem): string {
  if (!item.ok || !item.result) return `error: ${itemErrorText(item.error)}`;
  const r = item.result;
  const parts = [`job status ${r.jobStatus ? valueText(r.jobStatus, 60) : "not reported"}`];
  for (const set of r.sets) {
    const keys = Object.keys(set).filter((k) => k.toUpperCase() !== "JOB_STATUS");
    if (keys.length) parts.push(fieldsInline(set, keys));
  }
  return parts.join("; ");
}

export function formatVehicle(v: Vehicle): string {
  const out = ["Vehicle status (each reading's result fields as returned; their meaning is confirmed at the car in Phase 6):"];
  out.push(`Ignition (UTILITY STATUS_ZUENDUNG): ${readingText(v.ignition)}`);
  out.push(`Battery (UTILITY STATUS_UBATT): ${readingText(v.ubatt)}`);
  for (const s of v.voltages) {
    const name = [s.ecu, s.job].filter((x): x is string => !!x).map((x) => cleanName(x)).join(" ") || "voltage source";
    out.push(`Voltage (${name}): ${readingText(s)}`);
  }
  if (v.voltages.length === 0) out.push("Voltage sources: none configured in the agent.");
  out.push(`Cable KL30 (TMODE LESE_SPANNUNG_KL30): ${readingText(v.cableKl30)}`);
  out.push(`Cable KL15 (TMODE LESE_SPANNUNG_KL15): ${readingText(v.cableKl15)}`);
  return out.join("\n");
}

/** The most ECU names one listing prints. */
const ECU_NAMES_MAX = 500;

export function formatEcus(e: EcuList, filter: string | undefined): string {
  const where = e.ecuPath ? ` in ${cleanText(e.ecuPath, 200)}` : "";
  const match = filter ? ` matching "${cleanText(filter, 64)}"` : "";
  if (e.files.length === 0) return `No SGBD or group files${match}${where}.`;
  const by = (type: string) => e.files.filter((f) => f.type.toLowerCase() === type).map((f) => cleanName(f.name));
  const prg = by("prg");
  const grp = by("grp");
  const other = e.files.filter((f) => !["prg", "grp"].includes(f.type.toLowerCase())).map((f) => `${cleanName(f.name)} (${cleanText(f.type, 10)})`);
  const out = [`${plural(e.files.length, "file")}${match}${where}${e.truncated ? ` (truncated: ${e.count > e.files.length ? `${e.count} matched; ` : ""}narrow the filter)` : ""}:`];
  let left = ECU_NAMES_MAX;
  const line = (label: string, names: string[]) => {
    const shown = names.slice(0, left);
    left -= shown.length;
    if (shown.length) out.push(`${label}: ${shown.join(", ")}`);
  };
  line("Groups (grp)", grp);
  line("SGBDs (prg)", prg);
  line("Other", other);
  const more = grp.length + prg.length + other.length - ECU_NAMES_MAX;
  if (more > 0) out.push(`${more} more; use filter to narrow the list.`);
  return out.join("\n");
}
