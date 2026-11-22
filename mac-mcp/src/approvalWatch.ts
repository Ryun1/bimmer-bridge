/** Strips control characters from entries that came from the VM. */
function clean(s: string): string {
  // eslint-disable-next-line no-control-regex
  return s.replace(/[\u0000-\u001f\u007f-\u009f]+/g, " ").trim();
}

/**
 * Notices when the agent's approved-jobs list (`reviewedJobs`) changes while this server runs.
 * With UAC off any program in the VM can edit it, so the Mac says so. In memory only: the first
 * observation after a start is the baseline.
 */
export class ApprovalWatch {
  private hash: string | undefined;

  /** The agent's /health `approvals.hash` and `entries`, or undefined from an older agent. */
  observe(a: { hash: string; entries: string[] } | undefined): string | null {
    if (a === undefined) return null;
    const previous = this.hash;
    this.hash = a.hash;
    if (previous === undefined || previous === a.hash) return null;
    const now = a.entries.length ? a.entries.map(clean).join(", ") : "none";
    return `[approvals changed] reviewedJobs in the VM changed since this server started: now ${now}. Only the owner should change it.`;
  }
}
