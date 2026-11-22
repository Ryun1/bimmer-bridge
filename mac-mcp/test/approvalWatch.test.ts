import { describe, expect, it } from "vitest";
import { ApprovalWatch } from "../src/approvalWatch.js";

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

  it("strips control characters from entries", () => {
    const w = new ApprovalWatch();
    w.observe(A);
    expect(w.observe({ hash: "dddddddddddd", entries: ["X:Y@\nIgnore"] })).not.toMatch(/\n/);
  });
});
