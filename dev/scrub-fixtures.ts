#!/usr/bin/env node
/**
 * Fixture scrubber: copies a raw recording folder (written by dev/record-server.ts with
 * EDIABAS_RECORD_DIR; never committed) into dev/fixtures/<scenario>/ with identifiers removed.
 *
 *   node dev/scrub-fixtures.ts <raw-folder> <scenario> [--fixtures dev/fixtures] [--dry-run]
 *
 * Replaced, in every string and object of each recording (request and response):
 *   - anything shaped like a 17-character VIN; a BMW VIN (WB...) in any case and whatever
 *     touches it; in hex-encoded binary results, any 17-character VIN-alphabet run with a
 *     letter and a digit; percent-encoded strings are also checked decoded;
 *   - the value of any field whose name says it holds a chassis number, VIN or serial number
 *     (German and English names);
 *   - in /health: the Windows user name and any host name field;
 *   - everywhere: the folder name after `\Users\` in Windows paths.
 * Placeholders are fixed and keep the shape of the value (letters -> X, digits -> 0, hex -> 0).
 * Every replacement is printed (file, field and placeholder, never the original) for review.
 * Screenshots are copied unchanged and flagged: look at each one before committing.
 *
 * Nothing is written until every file has been read and scrubbed, and an existing fixture
 * file is never overwritten. Node 24+ runs this file directly: erasable TypeScript only.
 */
import { copyFileSync, existsSync, mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

export const DEFAULT_FIXTURES = resolve(dirname(fileURLToPath(import.meta.url)), "fixtures");
const SCENARIO_NAME = /^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/;
const IMAGE_FILE = /^[A-Za-z0-9][A-Za-z0-9._-]{0,200}\.(jpg|png)$/;

export const USER_PLACEHOLDER = "user";
export const HOST_PLACEHOLDER = "HOST";

export interface Replacement {
  file: string;
  /** JSON path of the value, e.g. `response.body.sets[0].FG_NR`. */
  field: string;
  rule: string;
  placeholder: string;
}

// ---------------------------------------------------------------------------------------------
// Rules
// ---------------------------------------------------------------------------------------------

/** VIN alphabet (no I, O, Q), 17 characters, not part of a longer letter/digit run. */
const VIN_RE = /(?<![A-Za-z0-9])[A-HJ-NPR-Z0-9]{17}(?![A-Za-z0-9])/g;
/** A BMW VIN (WB + 15), in any case and whatever touches it: field text can run into it. */
const BMW_VIN_RE = /WB[A-HJ-NPR-Z0-9]{15}/gi;
/** In binary results any run of the VIN alphabet is a candidate; bytes around it mean nothing. */
const VIN_RUN_RE = /[A-HJ-NPR-Z0-9]{17,}/g;
const PERCENT_ESCAPE = /%[0-9A-Fa-f]{2}/;

function looksLikeVin(s: string): boolean {
  // A VIN has both letters and digits; this keeps 17-digit numbers and all-letter words out.
  return /[A-Z]/.test(s) && /[0-9]/.test(s) && !isPlaceholder(s);
}

/**
 * The characters of a VIN-alphabet run that lie in some 17-character window with a letter
 * and a digit, as placeholders; the rest of the run is kept.
 */
function scrubVinRun(run: string): string {
  const hit = new Array<boolean>(run.length).fill(false);
  for (let i = 0; i + 17 <= run.length; i++) {
    if (looksLikeVin(run.slice(i, i + 17))) hit.fill(true, i, i + 17);
  }
  return [...run].map((c, i) => (hit[i] ? shapePlaceholder(c) : c)).join("");
}

/** The agent's X-Window-Title encoding: UTF-8, every byte outside A-Z a-z 0-9 - . _ ~ as %XX. */
function percentEncode(s: string): string {
  let out = "";
  for (const b of Buffer.from(s, "utf8")) {
    const c = String.fromCharCode(b);
    out += /[A-Za-z0-9._~-]/.test(c) ? c : "%" + b.toString(16).toUpperCase().padStart(2, "0");
  }
  return out;
}

/** Letters -> X, digits -> 0, everything else kept. */
export function shapePlaceholder(s: string): string {
  return s.replace(/[A-Z]/g, "X").replace(/[a-z]/g, "x").replace(/[0-9]/g, "0");
}

function isPlaceholder(s: string): boolean {
  return /^[X0]+$/.test(s);
}

function isHex(s: string): boolean {
  return s.length >= 2 && s.length % 2 === 0 && /^[0-9A-Fa-f]+$/.test(s);
}

/** Name tokens: split on separators and camelCase, upper-cased. */
function nameTokens(key: string): string[] {
  return key
    .replace(/([a-z0-9])([A-Z])/g, "$1_$2")
    .split(/[^A-Za-z0-9]+/)
    .filter(Boolean)
    .map((t) => t.toUpperCase());
}

/** Squashed upper-case name: `FG_NR` -> `FGNR`, `serialNumber` -> `SERIALNUMBER`. */
const ID_SUBSTRINGS = ["FAHRGESTELL", "CHASSIS", "FAHRZEUGIDENT", "SERIENNUMMER", "SERIENNR", "SERNR", "SERIAL", "FGNR"];

/** True when a field name says it holds a chassis number, VIN or serial number. */
export function isIdentifierField(key: string): boolean {
  const tokens = nameTokens(key);
  if (tokens.some((t) => /^VIN\d*$/.test(t))) return true;
  // FG = Fahrgestell (chassis); on its own it names the chassis-number field.
  if (tokens.length === 1 && tokens[0] === "FG") return true;
  const squashed = tokens.join("");
  return ID_SUBSTRINGS.some((s) => squashed.includes(s));
}

const HOST_FIELDS = new Set(["HOST", "HOSTNAME", "MACHINENAME", "COMPUTERNAME"]);

// ---------------------------------------------------------------------------------------------
// Scrubbing
// ---------------------------------------------------------------------------------------------

function isObject(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function childPath(path: string, key: string | number): string {
  if (typeof key === "number") return `${path}[${key}]`;
  return /^[A-Za-z_$][A-Za-z0-9_$]*$/.test(key) ? `${path}.${key}` : `${path}[${JSON.stringify(key)}]`;
}

class Scrubber {
  readonly replacements: Replacement[] = [];
  private readonly file: string;

  constructor(file: string) {
    this.file = file;
  }

  private note(field: string, rule: string, placeholder: string): void {
    this.replacements.push({ file: this.file, field, rule, placeholder });
  }

  /** The whole value of an identifier field, keeping its type and shape. */
  wholeValue(v: unknown, path: string, rule: string): unknown {
    if (typeof v === "string") {
      if (v === "" || isPlaceholder(v)) return v;
      const p = isHex(v) ? "0".repeat(v.length) : shapePlaceholder(v);
      this.note(path, rule, p);
      return p;
    }
    if (typeof v === "number") {
      const digits = String(Math.trunc(Math.abs(v))).length;
      const p = Number.isInteger(v) ? (digits > 1 ? 10 ** (digits - 1) : 0) : 0;
      if (p === v) return v;
      this.note(path, rule, String(p));
      return p;
    }
    if (Array.isArray(v)) return v.map((x, i) => this.wholeValue(x, childPath(path, i), rule));
    if (isObject(v)) {
      const out: Record<string, unknown> = {};
      for (const [k, x] of Object.entries(v)) out[k] = this.wholeValue(x, childPath(path, k), rule);
      return out;
    }
    return v;
  }

  /**
   * A string as it is and, when it has percent-escapes (the agent encodes X-Window-Title that
   * way), decoded: an identifier found only in the decoded text is scrubbed there and the string
   * re-encoded as the agent does.
   */
  text(s: string, path: string): string {
    const out = this.plainText(s, path);
    if (!PERCENT_ESCAPE.test(out)) return out;
    let decoded: string;
    try {
      decoded = decodeURIComponent(out);
    } catch {
      return out; // a lone "%" in ordinary text: nothing encoded here
    }
    const scrubbed = this.plainText(decoded, path);
    return scrubbed === decoded ? out : percentEncode(scrubbed);
  }

  /** VIN-shaped text and Windows profile folders inside a string. */
  private plainText(s: string, path: string): string {
    let out = s.replace(VIN_RE, (m) => {
      if (!looksLikeVin(m)) return m;
      const p = shapePlaceholder(m);
      this.note(path, "vin-shaped", p);
      return p;
    });
    out = out.replace(BMW_VIN_RE, (m) => {
      if (!looksLikeVin(m.toUpperCase())) return m;
      const p = shapePlaceholder(m);
      this.note(path, "bmw-vin", p);
      return p;
    });
    if (isHex(out) && out.length >= 34) out = this.hexText(out, path);
    out = out.replace(/([A-Za-z]:(?:\\\\|\\|\/)Users(?:\\\\|\\|\/))([^\\/"\r\n\t]+)/gi, (m, prefix: string, name: string) => {
      if (/^(Public|Default|Default User|All Users)$/i.test(name) || name === USER_PLACEHOLDER) return m;
      this.note(path, "windows-user-folder", USER_PLACEHOLDER);
      return prefix + USER_PLACEHOLDER;
    });
    return out;
  }

  /** A VIN in ASCII inside a hex string (binary results): replace those bytes in place. */
  private hexText(hex: string, path: string): string {
    const upper = hex === hex.toUpperCase();
    const ascii = Buffer.from(hex, "hex").toString("latin1");
    const scrubbed = ascii
      .replace(VIN_RUN_RE, scrubVinRun)
      .replace(BMW_VIN_RE, (m) => (looksLikeVin(m.toUpperCase()) ? shapePlaceholder(m) : m));
    if (scrubbed === ascii) return hex;
    const out = Buffer.from(scrubbed, "latin1").toString("hex");
    const p = upper ? out.toUpperCase() : out;
    this.note(path, "vin-in-hex", "(hex with the VIN bytes as X/0)");
    return p;
  }

  /** Walk any JSON value. */
  value(v: unknown, path: string): unknown {
    if (typeof v === "string") return this.text(v, path);
    if (Array.isArray(v)) return v.map((x, i) => this.value(x, childPath(path, i)));
    if (isObject(v)) {
      const out: Record<string, unknown> = {};
      for (const [k, x] of Object.entries(v)) {
        const p = childPath(path, k);
        out[k] = isIdentifierField(k) ? this.wholeValue(x, p, `identifier-field:${k}`) : this.value(x, p);
      }
      return out;
    }
    return v;
  }

  /** /health: the user name and any host name field. */
  health(body: Record<string, unknown>, path: string): void {
    for (const k of Object.keys(body)) {
      const v = body[k];
      if (typeof v !== "string" || v === "") continue;
      const upper = k.toUpperCase();
      if (upper === "USER" || upper === "USERNAME") {
        if (v !== USER_PLACEHOLDER) {
          body[k] = USER_PLACEHOLDER;
          this.note(childPath(path, k), "health-user", USER_PLACEHOLDER);
        }
      } else if (HOST_FIELDS.has(upper) && v !== HOST_PLACEHOLDER) {
        body[k] = HOST_PLACEHOLDER;
        this.note(childPath(path, k), "health-host", HOST_PLACEHOLDER);
      }
    }
  }
}

export interface ScrubResult {
  recording: Record<string, unknown>;
  replacements: Replacement[];
  imageFile: string | null;
}

/** Scrub one parsed recording. Throws if it is not a recording. */
export function scrubRecording(file: string, raw: unknown): ScrubResult {
  if (!isObject(raw) || !isObject(raw.request) || !isObject(raw.response)) {
    throw new Error(`${file}: not a recording (needs "request" and "response")`);
  }
  const s = new Scrubber(file);
  const request = s.value(raw.request, "request") as Record<string, unknown>;
  const response = s.value(raw.response, "response") as Record<string, unknown>;
  if (request.path === "/health" && isObject(response.body)) s.health(response.body, "response.body");
  let imageFile: string | null = null;
  if ("imageFile" in raw.response) {
    const img = raw.response.imageFile;
    if (typeof img !== "string" || !IMAGE_FILE.test(img) || img.includes("..")) {
      throw new Error(`${file}: response.imageFile must be a .jpg or .png file name in the same folder`);
    }
    imageFile = img;
    response.imageFile = img;
  }
  const recording: Record<string, unknown> = { ...raw, request, response };
  for (const [k, v] of Object.entries(raw)) {
    if (k !== "request" && k !== "response") recording[k] = s.value(v, k);
  }
  return { recording, replacements: s.replacements, imageFile };
}

export interface ScrubFolderResult {
  written: string[];
  images: string[];
  replacements: Replacement[];
}

/** Scrub every recording in `rawDir` into `destDir`. Writes nothing unless all succeed. */
export function scrubFolder(rawDir: string, destDir: string, opts: { dryRun?: boolean } = {}): ScrubFolderResult {
  if (!existsSync(rawDir) || !statSync(rawDir).isDirectory()) throw new Error(`no folder ${rawDir}`);
  if (resolve(rawDir) === resolve(destDir)) throw new Error("the raw folder and the destination are the same");
  const files = readdirSync(rawDir).filter((f) => f.endsWith(".json")).sort();
  if (files.length === 0) throw new Error(`no recordings (*.json) in ${rawDir}`);

  const outputs: { name: string; text: string }[] = [];
  const images: string[] = [];
  const replacements: Replacement[] = [];
  for (const f of files) {
    let raw: unknown;
    try {
      raw = JSON.parse(readFileSync(join(rawDir, f), "utf8"));
    } catch (e) {
      throw new Error(`${f}: not valid JSON (${e instanceof Error ? e.message : String(e)})`);
    }
    const r = scrubRecording(f, raw);
    replacements.push(...r.replacements);
    outputs.push({ name: f, text: JSON.stringify(r.recording, null, 2) + "\n" });
    if (r.imageFile) {
      if (!existsSync(join(rawDir, r.imageFile))) throw new Error(`${f}: image file ${r.imageFile} is missing`);
      images.push(r.imageFile);
    }
  }

  const clashes = [...outputs.map((o) => o.name), ...images].filter((n) => existsSync(join(destDir, n)));
  if (clashes.length) throw new Error(`refusing to overwrite existing fixture files in ${destDir}: ${clashes.join(", ")}`);

  if (!opts.dryRun) {
    mkdirSync(destDir, { recursive: true });
    for (const o of outputs) writeFileSync(join(destDir, o.name), o.text, { flag: "wx", mode: 0o644 });
    for (const img of images) copyFileSync(join(rawDir, img), join(destDir, img));
  }
  return { written: outputs.map((o) => o.name), images, replacements };
}

// ---------------------------------------------------------------------------------------------
// Command line
// ---------------------------------------------------------------------------------------------

const USAGE = "usage: node dev/scrub-fixtures.ts <raw-folder> <scenario> [--fixtures <dir>] [--dry-run]";

async function main(): Promise<void> {
  const args = process.argv.slice(2);
  const positional: string[] = [];
  let fixtures = DEFAULT_FIXTURES;
  let dryRun = false;
  for (let i = 0; i < args.length; i++) {
    const a = args[i]!;
    if (a === "--dry-run") dryRun = true;
    else if (a === "--fixtures") {
      const v = args[++i];
      if (!v) throw new Error("--fixtures needs a value");
      fixtures = resolve(v);
    } else if (a.startsWith("--")) throw new Error(`unknown argument ${a}`);
    else positional.push(a);
  }
  if (positional.length !== 2) throw new Error("need a raw folder and a scenario name");
  const [rawDir, scenario] = positional as [string, string];
  if (!SCENARIO_NAME.test(scenario)) throw new Error(`scenario name "${scenario}" is not allowed (letters, digits, - and _ only)`);
  const dest = join(fixtures, scenario);

  const r = scrubFolder(resolve(rawDir), dest, { dryRun });
  for (const x of r.replacements) process.stdout.write(`${x.file}  ${x.field}  [${x.rule}]  -> ${x.placeholder}\n`);
  for (const img of r.images) {
    process.stdout.write(`IMAGE NOT SCRUBBED: ${img} is copied as it is; look at it and delete it if it shows a VIN or other identifier.\n`);
  }
  process.stdout.write(
    `${dryRun ? "dry run: would write" : "wrote"} ${r.written.length} recordings and ${r.images.length} images to ${dest}; ` +
      `${r.replacements.length} replacements.\n` +
      "Review every file (git diff) before committing: the rules catch the usual identifiers, not every one.\n",
  );
}

if (import.meta.main) {
  main().catch((e: unknown) => {
    process.stderr.write(`scrub-fixtures: ${e instanceof Error ? e.message : String(e)}\n${USAGE}\n`);
    process.exit(2);
  });
}

