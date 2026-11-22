/** Strips control characters from entries that came from the VM. */
function clean(s: string): string {
  // eslint-disable-next-line no-control-regex
  return s.replace(/[\u0000-\u001f\u007f-\u009f]+/g, " ").trim();
}

/**
 * The notice's caps. The list comes from the VM (the notice exists because any program there can
 * edit it), so its length is not trusted: at most NOTICE_ENTRIES_MAX entries are shown, each cut
 * to NOTICE_ENTRY_MAX characters, then "... and N more". With the fixed words around them that
 * bounds the whole notice under NOTICE_MAX (about 1,800 characters at most).
 */
export const NOTICE_ENTRIES_MAX = 20;
export const NOTICE_ENTRY_MAX = 80;
export const NOTICE_MAX = 2000;

/** One entry, cleaned and cut to NOTICE_ENTRY_MAX characters ("..." marks a cut). */
function entry(s: string): string {
  const c = clean(s);
  return c.length <= NOTICE_ENTRY_MAX ? c : `${c.slice(0, NOTICE_ENTRY_MAX - 3)}...`;
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
    const shown = a.entries.slice(0, NOTICE_ENTRIES_MAX).map(entry).join(", ");
    const more = a.entries.length - NOTICE_ENTRIES_MAX;
    const now = a.entries.length ? `${shown}${more > 0 ? ` ... and ${more} more` : ""}` : "none";
    return `[approvals changed] reviewedJobs in the VM changed since this server started: now ${now}. Only the owner should change it.`;
  }
}
