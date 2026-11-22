import { readFileSync } from "node:fs";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import type { CallToolResult, ToolAnnotations } from "@modelcontextprotocol/sdk/types.js";
import { z } from "zod";
import { AgentClient, type Health } from "./agentClient.js";
import { ApprovalWatch } from "./approvalWatch.js";
import { AuditLog, type AuditSink } from "./audit.js";
import type { Config } from "./config.js";
import { BridgeError, errorText, policyMismatch, toBridgeError } from "./errors.js";
import { BATCH_MAX, readFaults, runJobBatch } from "./faults.js";
import { formatCaptureCaption, formatHealth, formatLogList, formatLogRead, formatWindows } from "./format.js";
import { formatDescribeJob, formatDescribeList, formatEcus, formatJobRaw, formatJobResult, formatVehicle } from "./formatEdiabas.js";
import { PolicyGuard } from "./policy.js";
import { FolderRecorder } from "./recorder.js";
import { CarSession } from "./carSession.js";
import { TEXT_BUDGET, fitText } from "./textBudget.js";
import { TokenProvider } from "./token.js";
import type { DiscoverySources } from "./vmAddress.js";

export const SERVER_NAME = "ediabas-bridge";

/** Version from package.json (one level above both src/ and dist/). */
export const SERVER_VERSION: string = (() => {
  try {
    const pkg = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8")) as { version?: unknown };
    return typeof pkg.version === "string" ? pkg.version : "0.0.0";
  } catch {
    return "0.0.0";
  }
})();

export const INSTRUCTIONS = [
  "ediabas-bridge reads BMW diagnostics through EDIABAS in a Windows VM (UTM) on this Mac, cabled to the user's car.",
  "Call vm_health first and on its own; read its answer before any other tool: it says whether the VM is reachable and a BMW tool holds the cable.",
  "vm_list_windows and vm_screenshot show what is open in the VM; vm_logs_list and vm_logs_read read EDIABAS and tool logs.",
  "ediabas_list_ecus and ediabas_describe find ECU and job names: use them, never guess names.",
  "ediabas_read_faults reads fault memory, ediabas_job runs one allowed read job and ediabas_jobs several, ediabas_vehicle_status reads ignition and voltages.",
  "Everything is read-only: you cannot click, type, clear faults, code or flash anything in the VM or the car.",
  "Errors start with a code in brackets. Never retry a refusal ([job_not_allowed], [job_needs_review]) or look for another way to do it.",
  "[agent_outdated]: that tool is not installed in the VM yet; say so.",
  "[ediabas_request_lost]: the job may have run; do not repeat it unless the user asks.",
  "If the VM or agent is unreachable, tell the user the fix the message gives rather than retrying.",
].join(" ");

const READ_ONLY: ToolAnnotations = {
  readOnlyHint: true,
  destructiveHint: false,
  idempotentHint: true,
  openWorldHint: false,
};

/** The only logger: stderr. stdout carries MCP protocol messages and nothing else. */
export function stderrLog(message: string): void {
  process.stderr.write(`[ediabas-bridge] ${message}\n`);
}

export const MAX_FAULT_ECUS = 20;
/** vm_logs_read: the largest range, and the tail read when no range is given. */
const LOG_READ_MAX = 65_536;
const LOG_TAIL_DEFAULT = 16_384;

type Client = Pick<
  AgentClient,
  | "health"
  | "listWindows"
  | "capture"
  | "listLogs"
  | "readLog"
  | "listEcus"
  | "describeJobs"
  | "describeJob"
  | "vehicle"
  | "runJob"
  | "runJobs"
>;

export interface ServerParts {
  client: Client;
  policy: PolicyGuard;
  audit: AuditSink;
  /** Remove secrets from text before it leaves a tool handler. */
  redact: (text: string) => string;
  log: (message: string) => void;
  /** The clock (ms since the epoch); default Date.now. */
  now?: (() => number) | undefined;
}

/** Per call: what run() adds to the result besides the tool's own text. */
interface CallContext {
  /** The approvals watch's message, shown first in the result. */
  notice: string | null;
}

type ToolOutput = string | { text: string; image: { data: string; mimeType: string } };

/** What the audit line records about an ediabas_* call. */
interface AuditInfo {
  ecu?: string;
  job?: string;
}

/** The closing line of a result cut to the text budget: how to narrow that tool's call. */
const NARROW_HINT: Record<string, string> = {
  vm_logs_read: "Read a smaller range (a smaller tailBytes, or offset and length) or use grep.",
  vm_logs_list: "Narrow with root, glob, since or limit.",
  ediabas_list_ecus: "Use filter.",
  ediabas_describe: "Use filter, or job to describe one job.",
  ediabas_job: "Use results to ask for fewer result fields.",
  ediabas_jobs: "Use results on the items, or ask for fewer items.",
  ediabas_read_faults: "Ask for fewer ECUs.",
};
const DEFAULT_HINT = "Ask for less.";

/** Tools whose results end with the car-session line. */
const CAR_TOOLS = new Set(["ediabas_job", "ediabas_jobs", "ediabas_read_faults", "ediabas_vehicle_status"]);

function invalidArguments(message: string): BridgeError {
  return new BridgeError("invalid_arguments", message);
}

// Argument shapes. Names are only length-limited here: the job policy decides, so that a bad
// name gets the policy's refusal and an audit line.
const Name = z.string().max(256);
const Filter = z.string().min(1).max(64);
/** Per job; the agent clamps it to its own maximum. */
const JobTimeout = z.number().int().min(1).max(60).optional().describe("Seconds each job may run, 1-60 (default: the agent's setting).");
const ISO_8601 = /^\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:?\d{2})?)?$/;

/** Builds the McpServer and registers the tools. No transport is attached. */
export function buildServer(parts: ServerParts): McpServer {
  const server = new McpServer({ name: SERVER_NAME, version: SERVER_VERSION }, { instructions: INSTRUCTIONS });
  const { client, policy } = parts;
  const car = new CarSession(parts.now);
  const mac = { version: SERVER_VERSION, startedAt: new Date((parts.now ?? Date.now)()) };
  const onOk = (ecu: string) => car.record(ecu);
  const watch = new ApprovalWatch();

  const noticeOf = (h: Health): string | null => {
    const a = h.approvals;
    return watch.observe(a && "entries" in a && a.entries !== undefined && a.hash !== undefined ? { hash: a.hash, entries: a.entries } : undefined);
  };

  /**
   * Called by the ediabas_* handlers before their own request: one /health round trip (it never
   * loads the DLL) that feeds the approvals watch. A /health failure throws, as the tool's own
   * request would have: the VM is unreachable either way.
   */
  async function preflight(): Promise<{ notice: string | null }> {
    const h = await client.health();
    // Compared whole, not by version: two files with the same version can still differ.
    const macSha = policy.sha256;
    if (h.policySha256 && macSha && h.policySha256.toLowerCase() !== macSha) throw policyMismatch(h.policySha256.toLowerCase(), macSha);
    return { notice: noticeOf(h) };
  }

  async function run(tool: string, body: (ctx: CallContext) => Promise<ToolOutput>, info?: AuditInfo): Promise<CallToolResult> {
    const started = performance.now();
    let outcome = "ok";
    let rule: string | undefined;
    let result: CallToolResult;
    let footer: string | null = null;
    const ctx: CallContext = { notice: null };
    // The car-session line is added after the cut, so it is always the last line.
    const fit = (text: string) => {
      const notice = ctx.notice ? parts.redact(ctx.notice) : null;
      const budget = TEXT_BUDGET - (footer ? footer.length + 2 : 0) - (notice ? notice.length + 2 : 0);
      const t = fitText(parts.redact(text), NARROW_HINT[tool] ?? DEFAULT_HINT, budget);
      return [notice, t, footer].filter((x) => x !== null).join("\n\n");
    };
    try {
      const out = await body(ctx);
      if (CAR_TOOLS.has(tool)) footer = car.line();
      if (typeof out === "string") {
        result = { content: [{ type: "text", text: fit(out) }] };
      } else {
        result = {
          content: [
            { type: "image", data: out.image.data, mimeType: out.image.mimeType },
            { type: "text", text: fit(out.text) },
          ],
        };
      }
    } catch (e) {
      const err = toBridgeError(e);
      outcome = err.code;
      rule = err.rule;
      if (err.code === "internal_error") parts.log(parts.redact(`${tool}: ${err.message}`));
      if (CAR_TOOLS.has(tool)) footer = car.line();
      result = { isError: true, content: [{ type: "text", text: fit(errorText(err.code, err.message)) }] };
    }
    try {
      await parts.audit.record({ time: new Date(), tool, outcome, durationMs: performance.now() - started, ...info, rule, approvalsChanged: ctx.notice !== null });
    } catch (e) {
      parts.log(`warning: audit failed: ${e instanceof Error ? e.message : String(e)}`);
    }
    return result;
  }

  // -------------------------------------------------------------------------
  // VM tools
  // -------------------------------------------------------------------------

  server.registerTool(
    "vm_health",
    {
      title: "VM agent health",
      description:
        "Check this Mac server's version and start time and the Windows VM's ediabas-bridge agent: agent version and uptime, Windows version, whether it runs in an " +
        "interactive session, elevation, bind address, which BMW tools (INPA, Tool32, NCS Expert, " +
        "WinKFP, ...) are running and therefore hold the diagnostic cable, whether the EDIABAS DLL was found and is loaded now, last cable use, " +
        "which features the agent has, and its job policy version against this Mac's. " +
        "Call this first in a session, and again when another tool fails. Read-only.",
      annotations: { title: "VM agent health", ...READ_ONLY },
    },
    async () =>
      run("vm_health", async (ctx) => {
        const h = await client.health();
        ctx.notice = noticeOf(h);
        return formatHealth(h, policy.version, mac, policy.sha256);
      }),
  );

  server.registerTool(
    "vm_list_windows",
    {
      title: "List VM windows",
      description:
        "List the visible top-level windows in the Windows VM as a table (title, process, pid, size, minimised), " +
        "front-most first. Use it to see what the user has open there, e.g. which INPA or Tool32 screen is showing. " +
        "Read-only: it cannot click, type or change anything.",
      annotations: { title: "List VM windows", ...READ_ONLY },
    },
    async () => run("vm_list_windows", async () => formatWindows(await client.listWindows())),
  );

  server.registerTool(
    "vm_screenshot",
    {
      title: "Screenshot of the VM",
      description:
        "Capture the VM's screen, or one window, as an image, followed by one line naming the window, its size and the capture method. " +
        "`window` is a handle from vm_list_windows (0x...), part of a window title, or `desktop` (default). " +
        "A minimised window cannot be captured: the bridge never restores, focuses or moves windows. Read-only.",
      inputSchema: {
        window: z.string().min(1).max(256).optional().describe("Window handle (0x...), title substring, or 'desktop' (default)."),
        maxEdge: z.number().int().min(200).max(4000).optional().describe("Longest image edge in pixels, 200-4000 (default 1600)."),
      },
      annotations: { title: "Screenshot of the VM", ...READ_ONLY },
    },
    async (a) =>
      run("vm_screenshot", async () => {
        const c = await client.capture({ window: a.window ?? "desktop", maxEdge: a.maxEdge ?? 1600 });
        return { text: formatCaptureCaption(c), image: { data: Buffer.from(c.data).toString("base64"), mimeType: c.mimeType } };
      }),
  );

  server.registerTool(
    "vm_logs_list",
    {
      title: "List VM log files",
      description:
        "List log files in the VM's allowed log folders (EDIABAS traces and similar): first the folders with their index, " +
        "then path, size and modified time, newest first. Filter by folder index (`root`), file-name pattern (`glob`, * and ?), " +
        "modified since (`since`, ISO 8601) and `limit` (1-200). Read-only.",
      inputSchema: {
        root: z.number().int().min(0).max(1000).optional().describe("Index of one log folder, from an earlier listing."),
        glob: z.string().min(1).max(200).optional().describe("File-name pattern with * and ?, case-insensitive."),
        since: z.string().max(40).regex(ISO_8601, "an ISO 8601 date or time").optional().describe("Only files modified at or after this ISO 8601 time."),
        limit: z.number().int().min(1).max(200).optional().describe("Most files to list, 1-200."),
      },
      annotations: { title: "List VM log files", ...READ_ONLY },
    },
    async (a) => run("vm_logs_list", async () => formatLogList(await client.listLogs(a))),
  );

  server.registerTool(
    "vm_logs_read",
    {
      title: "Read a VM log file",
      description:
        "Read part of a log file in the VM's allowed log folders (absolute Windows path from vm_logs_list). Give either " +
        "`tailBytes` (the end of the file; default 16384) or `offset` and `length`; at most 65536 bytes. `grep` (a regular " +
        "expression, up to 200 characters) keeps only matching lines. Returns a header line (path, byte range, encoding, " +
        "whether more exists) and the text. Read-only.",
      inputSchema: {
        path: z.string().min(1).max(1024).describe("Absolute path of the log file in the VM."),
        tailBytes: z.number().int().min(1).max(LOG_READ_MAX).optional().describe("Read this many bytes from the end."),
        offset: z.number().int().min(0).optional().describe("Start of the range in bytes (with length)."),
        length: z.number().int().min(1).max(LOG_READ_MAX).optional().describe("Length of the range in bytes (with offset)."),
        grep: z.string().min(1).max(200).optional().describe("Regular expression; only matching lines are returned."),
      },
      annotations: { title: "Read a VM log file", ...READ_ONLY },
    },
    async (a) =>
      run("vm_logs_read", async () => {
        const ranged = a.offset !== undefined || a.length !== undefined;
        if (ranged && a.tailBytes !== undefined) throw invalidArguments("Give either tailBytes, or offset and length, not both.");
        if (ranged && (a.offset === undefined || a.length === undefined)) throw invalidArguments("offset and length go together: give both.");
        return formatLogRead(await client.readLog(ranged || a.tailBytes !== undefined ? a : { ...a, tailBytes: LOG_TAIL_DEFAULT }));
      }),
  );

  // -------------------------------------------------------------------------
  // EDIABAS tools. Policy first: a refusal never leaves the Mac.
  // -------------------------------------------------------------------------

  server.registerTool(
    "ediabas_list_ecus",
    {
      title: "List EDIABAS ECU files",
      description:
        "List the SGBD (.prg) and group (.grp) names in the VM's EDIABAS Ecu folder, optionally filtered by a " +
        "case-insensitive name substring. Use it to find the exact ECU name before describing or reading it; never guess " +
        "names. Reads files only; does not talk to the car.",
      inputSchema: { filter: Filter.optional().describe("Case-insensitive substring of the name.") },
      annotations: { title: "List EDIABAS ECU files", ...READ_ONLY },
    },
    async (a) =>
      run("ediabas_list_ecus", async (ctx) => {
        policy.require();
        ctx.notice = (await preflight()).notice;
        return formatEcus(await client.listEcus(a.filter), a.filter);
      }),
  );

  server.registerTool(
    "ediabas_describe",
    {
      title: "Describe an SGBD's jobs",
      description:
        "Without `job`: list an ECU's (SGBD's) jobs with their descriptions and whether each is runnable through the " +
        "bridge (optionally filtered by name substring). With `job`: that job's description, arguments and results. " +
        "`filter` is ignored when `job` is given. `namesOnly` lists names without reading descriptions: faster, but " +
        "nothing is screened, so verdicts go by name only. Use it to find job names and arguments; never guess them. Reads the SGBD " +
        "file through EDIABAS, so it needs the cable attached to the VM, and can take tens of seconds on a large SGBD; read-only.",
      inputSchema: {
        ecu: Name.describe("SGBD or group name, e.g. from ediabas_list_ecus."),
        job: Name.optional().describe("One job to describe."),
        filter: Filter.optional().describe("Case-insensitive substring of job names (job list only)."),
        namesOnly: z.boolean().optional().describe("Job list only: names without descriptions (faster; not screened)."),
      },
      annotations: { title: "Describe an SGBD's jobs", ...READ_ONLY },
    },
    async (a) =>
      run(
        "ediabas_describe",
        async (ctx) => {
          policy.checkShape(a.ecu, a.job);
          ctx.notice = (await preflight()).notice;
          if (a.job !== undefined) {
            const d = await client.describeJob({ ecu: a.ecu, job: a.job });
            return formatDescribeJob(d, policy.evaluate({ ecu: a.ecu, job: a.job }));
          }
          const namesOnly = a.namesOnly === true;
          const d = await client.describeJobs({ ecu: a.ecu, filter: a.filter, comments: namesOnly ? false : undefined });
          const text = formatDescribeList(d, a.filter, (job) => policy.evaluate({ ecu: a.ecu, job }));
          if (!namesOnly) return text;
          const [head, ...rest] = text.split("\n");
          return [head, "(names only: not screened, verdicts by name)", ...rest].join("\n");
        },
        { ecu: a.ecu, job: a.job ?? "(job list)" },
      ),
  );

  server.registerTool(
    "ediabas_vehicle_status",
    {
      title: "Vehicle status",
      description:
        "Read ignition state, battery voltage readings and the cable's own KL30/KL15 readings, in one EDIABAS cycle. " +
        "Each reading's result fields are shown as returned. Use it to check the car is awake before reading faults. Read-only.",
      annotations: { title: "Vehicle status", ...READ_ONLY },
    },
    async () =>
      run(
        "ediabas_vehicle_status",
        async (ctx) => {
          policy.require();
          ctx.notice = (await preflight()).notice;
          const v = await client.vehicle();
          // Only the voltage sources are modules in the car; the rest read the cable.
          for (const s of v.voltages) if (s.ok && s.result && s.ecu) onOk(s.ecu);
          return formatVehicle(v);
        },
        { ecu: "(vehicle)", job: "STATUS_ZUENDUNG,STATUS_UBATT,voltages,LESE_SPANNUNG_KL30,LESE_SPANNUNG_KL15" },
      ),
  );

  server.registerTool(
    "ediabas_job",
    {
      title: "Run one EDIABAS read job",
      description:
        "Run one read-only EDIABAS job on an ECU and return its result sets as text (or the agent's JSON with raw: true). " +
        "Only jobs the job policy allows run; anything that could write, clear, activate or code is refused, and a refusal " +
        "is final. Find names and arguments with ediabas_describe first. `args` and `results` are EDIABAS's " +
        "semicolon-separated strings.",
      inputSchema: {
        ecu: Name.describe("SGBD or group name."),
        job: Name.describe("Job name, from ediabas_describe."),
        args: z.string().max(1000).optional().describe("Job arguments, semicolon-separated."),
        results: z.string().max(1000).optional().describe("Result names to return, semicolon-separated."),
        raw: z.boolean().optional().describe("Return the agent's JSON instead of formatted text."),
        timeoutSeconds: JobTimeout,
      },
      annotations: { title: "Run one EDIABAS read job", ...READ_ONLY },
    },
    async (a) =>
      run(
        "ediabas_job",
        async (ctx) => {
          const req = { ecu: a.ecu, job: a.job, args: a.args, results: a.results };
          policy.check(req);
          ctx.notice = (await preflight()).notice;
          const r = await client.runJob({ ...req, timeoutSeconds: a.timeoutSeconds });
          onOk(a.ecu);
          return a.raw ? formatJobRaw(r) : formatJobResult(r);
        },
        { ecu: a.ecu, job: a.job },
      ),
  );

  server.registerTool(
    "ediabas_jobs",
    {
      title: "Run several EDIABAS read jobs",
      description:
        "Run 1-20 read-only EDIABAS jobs in one request, each `{ecu, job, args?, results?}` as for ediabas_job, and " +
        "show one block per job in the order given, a failed job as its [code] line, under a header saying how many ran. " +
        "Every job is checked against the job policy first: if one is refused, nothing is sent. Find names and arguments " +
        "with ediabas_describe first. Read-only.",
      inputSchema: {
        jobs: z
          .array(
            z.object({
              ecu: Name.describe("SGBD or group name."),
              job: Name.describe("Job name, from ediabas_describe."),
              args: z.string().max(1000).optional().describe("Job arguments, semicolon-separated."),
              results: z.string().max(1000).optional().describe("Result names to return, semicolon-separated."),
            }),
          )
          .min(1)
          .max(BATCH_MAX)
          .describe("The jobs to run, 1-20."),
        raw: z.boolean().optional().describe("Return the agent's JSON for each job instead of formatted text."),
        timeoutSeconds: JobTimeout,
      },
      annotations: { title: "Run several EDIABAS read jobs", ...READ_ONLY },
    },
    async (a) =>
      run(
        "ediabas_jobs",
        async (ctx) => {
          const beforeSend = async () => void (ctx.notice = (await preflight()).notice);
          const items = a.jobs.map((j) => ({ ecu: j.ecu, job: j.job, args: j.args, results: j.results }));
          return (await runJobBatch({ client, policy, onOk, beforeSend }, items, { raw: a.raw, timeoutSeconds: a.timeoutSeconds })).text;
        },
        { ecu: a.jobs.map((j) => j.ecu).join(","), job: a.jobs.map((j) => j.job).join(",") },
      ),
  );

  server.registerTool(
    "ediabas_read_faults",
    {
      title: "Read fault memory",
      description:
        "Read the fault memory (FS_LESEN) of 1-20 ECUs, plus the info memory (IS_LESEN) with includeInfoMemory, and show " +
        "one fault table per ECU: code, text, present or stored, frequency, logged conditions, mileage in miles and km. " +
        "An error for one ECU is shown in its place. Read-only: it cannot clear faults.",
      inputSchema: {
        ecus: z.array(Name).min(1).max(MAX_FAULT_ECUS).describe("ECU (SGBD or group) names, 1-20."),
        includeInfoMemory: z.boolean().optional().describe("Also read the info memory (IS_LESEN)."),
      },
      annotations: { title: "Read fault memory", ...READ_ONLY },
    },
    async (a) => {
      const includeInfo = a.includeInfoMemory === true;
      const jobs = includeInfo ? "FS_LESEN,IS_LESEN" : "FS_LESEN";
      return run(
        "ediabas_read_faults",
        (ctx) => {
          const beforeSend = async () => void (ctx.notice = (await preflight()).notice);
          return readFaults({ client, policy, onOk, beforeSend }, a.ecus, includeInfo);
        },
        { ecu: a.ecus.join(","), job: jobs },
      );
    },
  );

  return server;
}

export interface AppDeps extends DiscoverySources {
  log: (message: string) => void;
  fetch?: typeof fetch;
  requestTimeoutMs?: number;
  ediabasTimeoutMs?: number;
  probeTimeoutMs?: number;
  /** The job policy; default policy/policy.json at the repo root. */
  policy?: PolicyGuard;
  /** The clock (ms since the epoch); default Date.now. */
  now?: () => number;
}

/** Wires config + system access into a ready server. */
export function createAppServer(config: Config, deps: AppDeps): McpServer {
  const tokens = new TokenProvider({
    execFile: deps.execFile,
    envToken: config.envToken,
    // An overridden agent URL gets EDIABAS_AGENT_TOKEN only, never the Keychain's token.
    envOnly: config.agentUrl !== undefined,
    warn: deps.log,
    service: config.keychainService,
    account: config.keychainAccount,
  });
  const log = (m: string) => deps.log(tokens.redact(m));
  const policy = deps.policy ?? PolicyGuard.load();
  if (policy.error) log(`warning: job policy not loaded (${policy.error}); every ediabas_* tool will refuse with policy_unavailable.`);
  const recorder = config.recordDir ? new FolderRecorder(config.recordDir, { warn: log, redact: (t) => tokens.redact(t) }) : undefined;
  if (recorder) log(`recording every agent exchange to ${config.recordDir}`);
  const client = new AgentClient({
    config,
    tokens,
    sources: { readFile: deps.readFile, execFile: deps.execFile, networkInterfaces: deps.networkInterfaces },
    fetch: deps.fetch,
    requestTimeoutMs: deps.requestTimeoutMs,
    ediabasTimeoutMs: deps.ediabasTimeoutMs,
    probeTimeoutMs: deps.probeTimeoutMs,
    log,
    recorder,
  });
  const audit = new AuditLog(config.auditLogPath, { warn: log });
  return buildServer({ client, policy, audit, redact: (t) => tokens.redact(t), log, now: deps.now });
}
