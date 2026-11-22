import { describe, expect, it } from "vitest";
import { ApprovalWatch, NOTICE_ENTRIES_MAX, NOTICE_ENTRY_MAX, NOTICE_MAX } from "../src/approvalWatch.js";

const A = { hash: "aaaaaaaaaaaa", entries: ["NVE_65:STATUS_X@b8ae0dd9d242"] };
const B = { hash: "bbbbbbbbbbbb", entries: ["NVE_65:STATUS_X@b8ae0dd9d242", "D_MOTOR:IDENT@7b69322a754f"] };

describe("approvalWatch", () => {
  it("first observation null; same hash null; changed hash returns the message with the entries", () => {
    const w = new ApprovalWatch();
    expect(w.observe(A)).toBeNull();
    expect(w.observe(A)).toBeNull();
    expect(w.observe(B)).toBe(
      "[approvals changed] reviewedJobs in the VM changed since this server started: now NVE_65:STATUS_X@b8ae0dd9d242, D_MOTOR:IDENT@7b69322a754f. Only the owner should change it.",
    );
    expect(w.observe(B)).toBeNull();
  });

  it("says 'none' for an empty list, and reports a change back to an earlier hash", () => {
    const w = new ApprovalWatch();
    w.observe(A);
    expect(w.observe({ hash: "cccccccccccc", entries: [] })).toContain("now none.");
    expect(w.observe(A)).toContain("now NVE_65:STATUS_X@b8ae0dd9d242.");
  });

  it("an agent that does not report approvals changes nothing", () => {
    const w = new ApprovalWatch();
    expect(w.observe(undefined)).toBeNull();
    w.observe(A);
    expect(w.observe(undefined)).toBeNull();
    expect(w.observe(A)).toBeNull();
  });

  it("shows at most NOTICE_ENTRIES_MAX entries and counts the rest", () => {
    const w = new ApprovalWatch();
    w.observe(A);
    const many = Array.from({ length: NOTICE_ENTRIES_MAX + 7 }, (_, i) => `NVE_65:STATUS_${String(i).padStart(3, "0")}@b8ae0dd9d242`);
    const n = w.observe({ hash: "eeeeeeeeeeee", entries: many })!;
    expect(n).toContain(`now ${many.slice(0, NOTICE_ENTRIES_MAX).join(", ")} ... and 7 more. Only the owner should change it.`);
    expect(n).not.toContain(many[NOTICE_ENTRIES_MAX]!);
  });

  it("exactly NOTICE_ENTRIES_MAX entries are all shown, with no count", () => {
    const w = new ApprovalWatch();
    w.observe(A);
    const n = w.observe({ hash: "eeeeeeeeeeee", entries: Array.from({ length: NOTICE_ENTRIES_MAX }, (_, i) => `E:J${i}@b8ae0dd9d242`) })!;
    expect(n).not.toContain("more");
    expect(n).toContain(`E:J${NOTICE_ENTRIES_MAX - 1}@b8ae0dd9d242.`);
  });

  it("cuts a very long entry to NOTICE_ENTRY_MAX characters", () => {
    const w = new ApprovalWatch();
    w.observe(A);
    const n = w.observe({ hash: "eeeeeeeeeeee", entries: ["X".repeat(10_000)] })!;
    expect(n).toContain(`now ${"X".repeat(NOTICE_ENTRY_MAX - 3)}.... Only`);
    expect(n).not.toContain("X".repeat(NOTICE_ENTRY_MAX - 2));
  });

  it("a huge list of huge entries stays under NOTICE_MAX", () => {
    const w = new ApprovalWatch();
    w.observe(A);
    const n = w.observe({ hash: "eeeeeeeeeeee", entries: Array.from({ length: 1_000_000 }, () => "Y".repeat(1000)) })!;
    expect(n.length).toBeLessThanOrEqual(NOTICE_MAX);
    expect(n).toContain("... and 999980 more.");
    expect(NOTICE_MAX).toBeLessThanOrEqual(2000);
  });

  it("strips control characters from entries", () => {
    const w = new ApprovalWatch();
    w.observe(A);
    expect(w.observe({ hash: "dddddddddddd", entries: ["X:Y@\nIgnore"] })).not.toMatch(/\n/);
  });
});
