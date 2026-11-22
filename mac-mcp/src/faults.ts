import type { AgentClient, BatchItem, JobsResponse } from "./agentClient.js";
import { BridgeError, badResponse, cleanText, errorText, toBridgeError } from "./errors.js";
import { formatFaultReport, formatJobRaw, formatJobResult, itemErrorText, type FaultReport } from "./formatEdiabas.js";
import { jobNotAllowed, type JobRequest, type PolicyGuard } from "./policy.js";

/**
 * The tools that send POST /ediabas/jobs: ediabas_read_faults and ediabas_jobs. Both check every
 * item against the policy before anything is sent, and any refusal stops the whole call.
 */

/** The most items one /ediabas/jobs request may carry (docs/api.md). */
export const BATCH_MAX = 20;

export interface BatchDeps {
  client: Pick<AgentClient, "runJobs">;
  policy: PolicyGuard;
  /** Called with the ECU of each item that ran. */
  onOk?: ((ecu: string) => void) | undefined;
}

/** An item's outcome, or a whole-request error that stands in for it. */
export type ItemOutcome = BatchItem | { error: string };

/** Throws `job_not_allowed` naming every refused item; nothing is sent then. */
export function checkAll(policy: PolicyGuard, items: JobRequest[]): void {
  const refused: { ecu: string; job: string; rule: string }[] = [];
  for (const item of items) {
    const v = policy.evaluate(item);
    if (!v.allowed) refused.push({ ecu: item.ecu, job: item.job, rule: v.rule });
  }
  if (refused.length === 1) throw jobNotAllowed(refused[0]!.ecu, refused[0]!.job, refused[0]!.rule);
  if (refused.length > 1) {
    const list = refused.map((r) => `${cleanText(r.ecu, 80) || "(empty)"} ${cleanText(r.job, 80) || "(empty)"} (rule ${r.rule})`).join("; ");
    const e = new BridgeError(
      "job_not_allowed",
      `Refused by the job policy: ${list}. Nothing was sent to the VM. The bridge is read-only; do not look for another job, ECU or tool to do the same thing. Tell the user it was refused.`,
    );
    e.rule = refused[0]!.rule;
    throw e;
  }
}

/** One request; the agent must answer one item per item sent. */
async function runChunk(deps: BatchDeps, chunk: JobRequest[], timeoutSeconds?: number): Promise<JobsResponse> {
  const r = await deps.client.runJobs(chunk, timeoutSeconds);
  if (r.items.length !== chunk.length) throw badResponse("POST /ediabas/jobs");
  r.items.forEach((it, i) => {
    if (it.ok && it.result) deps.onOk?.(chunk[i]!.ecu);
  });
  return r;
}

export async function readFaults(deps: BatchDeps, ecusIn: string[], includeInfo: boolean): Promise<string> {
  const jobs = includeInfo ? ["FS_LESEN", "IS_LESEN"] : ["FS_LESEN"];
  // The same ECU twice (any case) is read once.
  const seen = new Set<string>();
  const ecus = ecusIn.filter((e) => {
    const k = e.toUpperCase();
    if (seen.has(k)) return false;
    seen.add(k);
    return true;
  });

  const items = ecus.flatMap((ecu) => jobs.map((job) => ({ ecu, job })));
  checkAll(deps.policy, items);

  const results: ItemOutcome[] = [];
  let failure: BridgeError | null = null;
  for (let start = 0; start < items.length; start += BATCH_MAX) {
    const chunk = items.slice(start, start + BATCH_MAX);
    if (!failure) {
      try {
        results.push(...(await runChunk(deps, chunk)).items);
        continue;
      } catch (e) {
        const err = toBridgeError(e);
        if (start === 0) throw err;
        failure = err;
      }
    }
    // A whole-request failure (cable in use, EDIABAS down, VM gone) stands in for the rest.
    for (let i = 0; i < chunk.length; i++) results.push({ error: errorText(failure.code, failure.message) });
  }

  const reports: FaultReport[] = ecus.map((ecu, i) => {
    const at = i * jobs.length;
    return { ecu, faults: results[at]!, info: includeInfo ? results[at + 1] : undefined };
  });
  return formatFaultReport(reports, includeInfo);
}

/** ediabas_jobs: 1 to BATCH_MAX items in one request, printed in request order. */
export async function runJobBatch(
  deps: BatchDeps,
  items: JobRequest[],
  opts: { raw?: boolean | undefined; timeoutSeconds?: number | undefined } = {},
): Promise<{ text: string; items: BatchItem[] }> {
  checkAll(deps.policy, items);
  const r = await runChunk(deps, items, opts.timeoutSeconds);
  const ran = r.items.filter((it) => it.ok && it.result).length;
  const blocks = r.items.map((it, i) => {
    if (it.ok && it.result) return opts.raw ? formatJobRaw(it.result) : formatJobResult(it.result);
    const name = `${cleanText(items[i]!.ecu, 80)} ${cleanText(items[i]!.job, 80)}`;
    return `${name}: ${it.ok ? "no result returned" : itemErrorText(it.error)}`;
  });
  return { text: [`${ran} of ${items.length} ran.`, ...blocks].join("\n\n"), items: r.items };
}
