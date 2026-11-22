import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { z } from "zod";
import { BridgeError, cleanText } from "./errors.js";

/**
 * The EDIABAS job policy (policy/policy.json), evaluated exactly as docs/api.md "Job policy"
 * steps 1–7. The Mac runs it before any request leaves, so a refused job never reaches the VM.
 * The description screen is the agent's job; the Mac only reports `screen: true` (it still
 * validates `descriptionScreen`, so both sides accept the same files).
 */

/** policy/policy.json at the repo root: two levels above both src/ and dist/. */
export const DEFAULT_POLICY_PATH = fileURLToPath(new URL("../../policy/policy.json", import.meta.url));

// As strict as the agent's parser (JobPolicy.cs), checked by policy/policy-parse-cases.json:
// unknown keys, duplicate keys, empty names and out-of-range numbers make the policy invalid,
// so the Mac never silently ignores a rule the agent would refuse to load.
const INT32_MAX = 2147483647;
const Names = z.array(z.string().min(1));
/**
 * Upper-case names (allow and deny lists, restricted ECU keys). Evaluation compares them with
 * the upper-cased ECU and job as written, so anything else could never match. Unchanged by
 * upper-casing and no lower-case letter (ß has no single upper-case form): the agent applies
 * the same two tests.
 */
const isUpper = (s: string) => s === s.toUpperCase() && !/\p{Ll}/u.test(s);
const UpperName = z.string().min(1).refine(isUpper, "must be upper-case");
const UpperNames = z.array(UpperName);
/**
 * allow.patterns: compiled here by JavaScript and in the VM by .NET. The dialects differ on
 * escapes (\d, \w, \p{..}, \_) and on non-ASCII, and agree without them.
 */
const Patterns = z.array(
  z.string().min(1).refine((s) => !/[\\\u0080-\uffff]/.test(s), "must not contain a backslash or a non-ASCII character"),
);
/** Lower-case, non-empty and each listed once (descriptionScreen.ignore). */
const LowerNames = z
  .array(z.string().min(1).refine((s) => s === s.toLowerCase(), "must be lower-case"))
  .refine((a) => new Set(a).size === a.length, "must not list an entry twice");

const PolicySchema = z.strictObject({
  version: z.number().int().min(1).max(INT32_MAX),
  namePattern: z.string().min(1),
  maxArgLength: z.number().int().nonnegative().max(INT32_MAX),
  allow: z.strictObject({
    globalExact: UpperNames,
    patterns: Patterns,
    restrictedEcus: z.record(UpperName, UpperNames),
  }),
  deny: z.strictObject({ substrings: UpperNames, tokens: UpperNames }),
  descriptionScreen: z.strictObject({ terms: Names.min(1), ignore: LowerNames }),
});

/**
 * The first key that appears twice in one object of `text`, else null. `JSON.parse` keeps the
 * last of duplicate keys; the agent refuses them. `text` must already be valid JSON.
 */
export function findDuplicateKey(text: string): string | null {
  // One entry per open object (its keys so far) or array (null).
  const stack: (Set<string> | null)[] = [];
  let expectKey = false;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (ch === '"') {
      let j = i + 1;
      while (text[j] !== '"') j += text[j] === "\\" ? 2 : 1;
      const top = stack[stack.length - 1];
      if (expectKey && top) {
        const key = JSON.parse(text.slice(i, j + 1)) as string;
        if (top.has(key)) return key;
        top.add(key);
        expectKey = false;
      }
      i = j;
    } else if (ch === "{") {
      stack.push(new Set());
      expectKey = true;
    } else if (ch === "[") {
      stack.push(null);
      expectKey = false;
    } else if (ch === "}" || ch === "]") {
      stack.pop();
      expectKey = false;
    } else if (ch === ",") {
      expectKey = !!stack[stack.length - 1];
    }
  }
  return null;
}

/** Keys whose value must be written as an integer literal: `JSON.parse` reads `3.0` and `3e0`
 * as 3, the agent refuses them. */
const INTEGER_KEYS = new Set(["version", "maxArgLength"]);

/**
 * The first top-level key in INTEGER_KEYS whose number is not written as a plain integer
 * (`-?digits`), else null. `text` must already be valid JSON.
 */
export function findNonIntegerLiteral(text: string): string | null {
  let depth = 0;
  let expectKey = false;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (ch === '"') {
      let j = i + 1;
      while (text[j] !== '"') j += text[j] === "\\" ? 2 : 1;
      if (expectKey && depth === 1) {
        const key = JSON.parse(text.slice(i, j + 1)) as string;
        expectKey = false;
        if (INTEGER_KEYS.has(key)) {
          const m = /^\s*:\s*(-?[0-9][0-9.eE+-]*)/.exec(text.slice(j + 1));
          if (m && !/^-?(0|[1-9][0-9]*)$/.test(m[1]!)) return key;
        }
      }
      i = j;
    } else if (ch === "{" || ch === "[") {
      depth++;
      expectKey = ch === "{";
    } else if (ch === "}" || ch === "]") {
      depth--;
      expectKey = false;
    } else if (ch === ",") {
      // Only objects are nested at depth 1 (the file is an object), and keys there follow a comma.
      expectKey = depth === 1;
    }
  }
  return null;
}

/** A UTF-16 surrogate that is not half of a pair: the agent cannot read such a string. */
const LONE_SURROGATE = /[\uD800-\uDFFF]/u;

export interface Policy {
  version: number;
  maxArgLength: number;
  namePattern: RegExp;
  globalExact: Set<string>;
  patterns: RegExp[];
  restrictedEcus: Map<string, Set<string>>;
  denySubstrings: string[];
  denyTokens: string[];
  /** descriptionScreen.ignore: validated here, used only by the agent's screen. */
  screenIgnore: string[];
}

export interface Verdict {
  allowed: boolean;
  rule: string;
  /** The agent must screen the job's description before running it. */
  screen: boolean;
}

export interface JobRequest {
  ecu: string;
  job: string;
  args?: string | undefined;
  results?: string | undefined;
}

/** Parse and compile a policy. Throws on anything malformed (the caller fails closed). */
export function parsePolicy(text: string): Policy {
  let loneSurrogate = false;
  const json: unknown = JSON.parse(text, (k: string, v: unknown) => {
    if (LONE_SURROGATE.test(k) || (typeof v === "string" && LONE_SURROGATE.test(v))) loneSurrogate = true;
    return v;
  });
  if (loneSurrogate) throw new Error("it has text that is not valid Unicode (a lone surrogate)");
  const duplicate = findDuplicateKey(text);
  if (duplicate !== null) throw new Error(`it has the key "${duplicate}" twice in one object`);
  const nonInteger = findNonIntegerLiteral(text);
  if (nonInteger !== null) throw new Error(`"${nonInteger}" must be written as a whole number`);
  const raw = PolicySchema.parse(json);
  const compile = (p: string) => new RegExp(p);
  return {
    version: raw.version,
    maxArgLength: raw.maxArgLength,
    namePattern: compile(raw.namePattern),
    globalExact: new Set(raw.allow.globalExact),
    patterns: raw.allow.patterns.map(compile),
    restrictedEcus: new Map(Object.entries(raw.allow.restrictedEcus).map(([k, v]) => [k, new Set(v)])),
    denySubstrings: raw.deny.substrings,
    denyTokens: raw.deny.tokens,
    screenIgnore: raw.descriptionScreen.ignore,
  };
}

/** The whole string must match: a match that leaves a trailing newline (or anything) does not count. */
function fullMatch(re: RegExp, s: string): boolean {
  const m = re.exec(s);
  return m !== null && m.index === 0 && m[0].length === s.length;
}

function printableAscii(s: string, max: number): boolean {
  return s.length <= max && /^[ -~]*$/.test(s);
}

export function evaluate(policy: Policy, req: JobRequest): Verdict {
  const deny = (rule: string): Verdict => ({ allowed: false, rule, screen: false });

  // 1. Shape
  if (!fullMatch(policy.namePattern, req.ecu)) return deny("shape.ecu");
  if (!fullMatch(policy.namePattern, req.job)) return deny("shape.job");
  if (req.args !== undefined && !printableAscii(req.args, policy.maxArgLength)) return deny("shape.args");
  if (req.results !== undefined && !printableAscii(req.results, policy.maxArgLength)) return deny("shape.results");

  // 2. Upper-case (shape already limits names to what namePattern allows)
  const ecu = req.ecu.toUpperCase();
  const job = req.job.toUpperCase();

  // 3. Deny. Entries are upper-case (the parser refuses others) and compared as written, as the agent does.
  for (const s of policy.denySubstrings) if (job.includes(s)) return deny(`deny.substring:${s}`);
  const tokens = job.split("_").filter((t) => t.length > 0);
  for (const t of policy.denyTokens) if (tokens.includes(t)) return deny(`deny.token:${t}`);

  // 4. Restricted ECU: nothing else applies to it
  const restricted = policy.restrictedEcus.get(ecu);
  if (restricted) return restricted.has(job) ? { allowed: true, rule: "allow.ecu", screen: false } : deny("ecu_restricted");

  // 5–7
  if (policy.globalExact.has(job)) return { allowed: true, rule: "allow.global", screen: false };
  if (policy.patterns.some((p) => fullMatch(p, job))) return { allowed: true, rule: "allow.pattern", screen: true };
  return deny("default_deny");
}

/** Step 1 only, for `ediabas_describe`: the job name passes shape rules only. */
export function checkShape(policy: Policy, ecu: string, job?: string): string | null {
  if (!fullMatch(policy.namePattern, ecu)) return "shape.ecu";
  if (job !== undefined && !fullMatch(policy.namePattern, job)) return "shape.job";
  return null;
}

// ---------------------------------------------------------------------------

export function policyUnavailable(reason: string): BridgeError {
  return new BridgeError(
    "policy_unavailable",
    `The job policy file could not be loaded on this Mac (${cleanText(reason, 200)}), so every EDIABAS tool refuses and nothing was sent to the VM. ` +
      "Ryan needs to restore a valid policy/policy.json in the ediabas-bridge repo and restart the MCP server (restart Claude).",
  );
}

const RULE_MEANING: Record<string, string> = {
  "shape.ecu": "the ECU name has characters or a length the bridge does not accept",
  "shape.job": "the job name has characters or a length the bridge does not accept",
  "shape.args": "the arguments are too long or contain characters other than printable ASCII",
  "shape.results": "the result filter is too long or contains characters other than printable ASCII",
  ecu_restricted: "this ECU only allows a fixed list of jobs",
  default_deny: "the job is not on the read-only allow list",
};

function ruleMeaning(rule: string): string {
  if (rule.startsWith("deny.")) return "the job name marks it as something other than a read";
  return RULE_MEANING[rule] ?? "refused by the job policy";
}

/** `job_not_allowed`, raised on the Mac. Says which rule refused it and not to look for another way. */
export function jobNotAllowed(ecu: string, job: string, rule: string): BridgeError {
  const e = new BridgeError(
    "job_not_allowed",
    `Refused by the job policy: ${cleanText(ecu, 80) || "(empty)"} ${cleanText(job, 80) || "(empty)"} (rule ${cleanText(rule, 80)}: ${ruleMeaning(rule)}). ` +
      "Nothing was sent to the VM. The bridge is read-only; do not look for another job, ECU or tool to do the same thing. Tell Ryan it was refused.",
  );
  e.rule = rule;
  return e;
}

/**
 * Holds the loaded policy, or the reason it could not be loaded. Loaded once; every check
 * fails closed with `policy_unavailable` when loading failed.
 */
export class PolicyGuard {
  private constructor(
    private readonly policy: Policy | null,
    private readonly loadError: string | null,
  ) {}

  static load(path: string = DEFAULT_POLICY_PATH, read: (p: string) => string = (p) => readFileSync(p, "utf8")): PolicyGuard {
    try {
      return new PolicyGuard(parsePolicy(read(path)), null);
    } catch (e) {
      const reason = e instanceof z.ZodError ? "it does not have the expected fields" : e instanceof Error ? e.message : String(e);
      return new PolicyGuard(null, `${path}: ${reason}`);
    }
  }

  static fromPolicy(policy: Policy): PolicyGuard {
    return new PolicyGuard(policy, null);
  }

  /** Version of the loaded policy, or null when it could not be loaded. */
  get version(): number | null {
    return this.policy?.version ?? null;
  }

  get error(): string | null {
    return this.loadError;
  }

  /** Throws `policy_unavailable` unless the policy loaded. */
  require(): Policy {
    if (!this.policy) throw policyUnavailable(this.loadError ?? "unknown reason");
    return this.policy;
  }

  /** Full evaluation; throws `job_not_allowed` (or `policy_unavailable`). */
  check(req: JobRequest): Verdict {
    const v = evaluate(this.require(), req);
    if (!v.allowed) throw jobNotAllowed(req.ecu, req.job, v.rule);
    return v;
  }

  /** Verdict without throwing on a refusal (still throws `policy_unavailable`). */
  evaluate(req: JobRequest): Verdict {
    return evaluate(this.require(), req);
  }

  /** Shape-only check for describe; throws `job_not_allowed`. */
  checkShape(ecu: string, job?: string): void {
    const rule = checkShape(this.require(), ecu, job);
    if (rule) throw jobNotAllowed(ecu, job ?? "", rule);
  }
}
