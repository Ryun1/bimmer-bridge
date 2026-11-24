import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { PolicyGuard } from "../../mac-mcp/src/policy.ts";
import { classifyService, findTelegrams, parsePrg, sweepFolder, traceSends } from "../sgbd-sweep.ts";

/** `move S1d, "<bytes>"`: opcode 00, mode 0x18 (string register, immediate string). */
const moveImm = (bytes: number[]) => [0x00, 0x18, 0x1d, bytes.length & 0xff, bytes.length >> 8, ...bytes];
/** `scat S1d, "<bytes>"`: opcode 21, same operand modes. */
const scatImm = (bytes: number[]) => [0x21, 0x18, 0x1d, bytes.length & 0xff, bytes.length >> 8, ...bytes];
/** `eoj` and some padding that is not a telegram. */
const filler = [0x1d, 0x00, 0x01, 0x10, 0x1d, 0x00];

/**
 * A synthetic BEST2 object file: the plain header (job table offset at 0x88), the code of each
 * job, then the job table (plain count, XOR-ed 0x44-byte entries), then the info section
 * (JOBNAME/JOBCOMMENT strings, XOR-ed). Only what parsePrg reads.
 */
function buildPrg(jobs: { name: string; code: number[]; comment?: string }[]): Buffer {
  const header = Buffer.alloc(0xa0);
  header.write("@EDIABAS OBJECT", 0, "latin1");
  const codes: Buffer[] = [];
  const starts: number[] = [];
  let at = header.length;
  for (const j of jobs) {
    starts.push(at);
    const c = Buffer.from([...j.code, ...filler]);
    codes.push(c);
    at += c.length;
  }
  const tableOffset = at;
  header.writeInt32LE(tableOffset, 0x88);
  const table = Buffer.alloc(4 + 0x44 * jobs.length);
  table.writeInt32LE(jobs.length, 0);
  jobs.forEach((j, i) => {
    const e = Buffer.alloc(0x44);
    e.write(j.name, 0, "latin1");
    e.writeInt32LE(starts[i]!, 0x40);
    for (let k = 0; k < e.length; k++) e[k] = e[k]! ^ 0xf7;
    e.copy(table, 4 + i * 0x44);
  });
  // As in real files: one item per line, a job's several JOBCOMMENT lines in a row.
  const info = Buffer.from(
    jobs.map((j) => `JOBNAME:${j.name}\n` + (j.comment === undefined ? "" : j.comment.split(" / ").map((c) => `JOBCOMMENT:${c}\n`).join(""))).join(""),
    "latin1",
  );
  const xored = (b: Buffer) => Buffer.from(b.map((v) => v ^ 0xf7));
  return Buffer.concat([header, ...codes.map(xored), table, xored(info)]);
}

describe("parsePrg", () => {
  it("reads the job table, each job's code range and its description", () => {
    const prg = buildPrg([
      { name: "INFO", code: [0x1d, 0x00], comment: "Information" },
      { name: "STATUS_X", code: moveImm([0x83, 0x01, 0xf1, 0x22, 0x99, 0x04]), comment: "Status X lesen / KWP2000: $22 ReadDataByCommonIdentifier" },
    ]);
    const p = parsePrg(prg);
    expect(p.jobs.map((j) => j.name)).toEqual(["INFO", "STATUS_X"]);
    expect(p.jobs[1]!.comment).toBe("Status X lesen / KWP2000: $22 ReadDataByCommonIdentifier");
    expect(findTelegrams(p.jobs[1]!.code).map((t) => t.hex)).toEqual(["83 01 F1 22 99 04"]);
  });

  it("refuses a file that is not a BEST2 object", () => {
    expect(() => parsePrg(Buffer.from("not an sgbd"))).toThrow(/not an EDIABAS object/);
  });
});

describe("findTelegrams", () => {
  it("finds every KWP request in a job, not only the first", () => {
    const code = [
      ...moveImm([0x85, 0xff, 0xf1, 0x2e, 0x99, 0x02, 0xff, 0xff]),
      ...moveImm([0x83, 0x01, 0xf1, 0x22, 0x99, 0x04]),
      ...moveImm([0x84, 0x01, 0xf1, 0x2e, 0x99, 0x07, 0xff]),
    ];
    expect(findTelegrams(Buffer.from(code)).map((t) => t.service)).toEqual([0x2e, 0x22, 0x2e]);
  });

  it("reads the long form (format 0x80, length in the fourth byte) and functional addressing", () => {
    const code = [...moveImm([0x80, 0x12, 0xf1, 0x02, 0x3b, 0xfa]), ...scatImm([0xc2, 0xef, 0xf1, 0x18, 0x02])];
    expect(findTelegrams(Buffer.from(code)).map((t) => t.service)).toEqual([0x3b, 0x18]);
  });

  it("reads an explicit length byte after the header when the format byte's length does not fit", () => {
    // DDE41KR0 STATUS_DIGITAL: format B8 (56) but 21 bytes follow; 0x14 = 20 is their length, and $2C the service.
    const body = [0x2c, 0x10, 0x0f, 0x70, 0x1c, 0x00, 0x2f, 0x74, 0xdf, 0x01, 0xdf, 0x02, 0xdf, 0x08, 0x1f, 0x9a, 0x1f, 0xb0, 0x1f, 0xb1];
    const [t] = findTelegrams(Buffer.from(moveImm([0xb8, 0x12, 0xf1, body.length, ...body])));
    expect(t).toMatchObject({ service: 0x2c, kind: "define", lengthOk: true });
  });

  it("reads a long-form template whose length byte the job inserts later (service right after the header)", () => {
    // D40M57A1 STATUS_DIGITAL and MS450DS0: 80 12 F1 2C 10 is $2C, not a session change ($10).
    const [t] = findTelegrams(Buffer.from(moveImm([0x80, 0x12, 0xf1, 0x2c, 0x10])));
    expect(t).toMatchObject({ service: 0x2c, kind: "define", lengthOk: false });
  });

  it("reads a length byte whose bytes the job appends (service after the length)", () => {
    // ACC FS_LESEN: B8 21 F1 03 17 is $17 with three bytes to come.
    const [t] = findTelegrams(Buffer.from(moveImm([0xb8, 0x21, 0xf1, 0x03, 0x17])));
    expect(t).toMatchObject({ service: 0x17, kind: "read", lengthOk: false });
  });

  it("marks a request shorter than its format byte says (the job appends the rest)", () => {
    const [t] = findTelegrams(Buffer.from(moveImm([0x85, 0x01, 0xf1, 0x3b, 0xfa])));
    expect(t).toMatchObject({ service: 0x3b, kind: "write", lengthOk: false });
  });

  it("ignores immediate strings that are not requests from the tester (source F1)", () => {
    const code = [...moveImm([0xb5, 0x46, 0x00]), ...moveImm([0x83, 0x01, 0x12, 0x2e, 0x00, 0x00]), ...moveImm([0x4f, 0x4b, 0x41, 0x59])];
    expect(findTelegrams(Buffer.from(code))).toEqual([]);
  });
});

describe("classifyService", () => {
  it("calls the KWP2000 and UDS write, reset, security and download services writes", () => {
    for (const sid of [0x14, 0x2e, 0x3b, 0x3d, 0x11, 0x27, 0x34, 0x36, 0x37, 0x85, 0x28]) {
      expect(classifyService([0x80 | 3, 0x01, 0xf1, sid, 0x00, 0x00]).kind, sid.toString(16)).toBe("write");
    }
  });

  it("puts a dynamic read definition ($2C) in its own kind: it defines what a later $21 reads", () => {
    expect(classifyService([0x84, 0x12, 0xf1, 0x2c, 0xf0, 0x04, 0x01]).kind).toBe("define");
  });

  it("keeps input/output control and routines apart from writes (they were reviewed separately)", () => {
    expect(classifyService([0x84, 0x01, 0xf1, 0x30, 0x91, 0x01, 0x01]).kind).toBe("control");
    expect(classifyService([0x83, 0x01, 0xf1, 0x31, 0x10, 0x00]).kind).toBe("control");
  });

  it("calls the read services reads", () => {
    for (const sid of [0x12, 0x13, 0x17, 0x18, 0x19, 0x1a, 0x21, 0x22, 0x23, 0x3e]) {
      expect(classifyService([0x83, 0x01, 0xf1, sid, 0x00, 0x00]).kind, sid.toString(16)).toBe("read");
    }
  });

  it("treats a session change as a read only when it asks for the default session", () => {
    expect(classifyService([0x82, 0x01, 0xf1, 0x10, 0x81]).kind).toBe("read");
    expect(classifyService([0x82, 0x01, 0xf1, 0x10, 0x85]).kind).toBe("session");
  });
});

/** The patch idiom: move L0, index; push; move L0, value; push; pop L0; pop L1; move Sreg[L1], B0. */
const patch = (reg: number, index: number, value: number) => [
  0x00, 0x47, 0x18, index, 0, 0, 0, 0x1e, 0x40, 0x18,
  0x00, 0x47, 0x18, value, 0, 0, 0, 0x1e, 0x40, 0x18,
  0x1f, 0x40, 0x18, 0x1f, 0x40, 0x19,
  0x00, 0xa2, reg, 0x19, 0x00,
];
/** xsend S21, Sreq */
const xsend = (req: number) => [0x2a, 0x11, 0x21, req];

describe("traceSends", () => {
  it("sends the template as the job patched it, not the constant (EHC2RR STATUS_CALIBRATION)", () => {
    const code = [
      ...moveImm([0x80, 0xff, 0xf1, 0x3d, 0x00]),
      0x00, 0x11, 0x1e, 0x1d, // move S1e, S1d
      ...patch(0x1e, 0, 0x82), ...patch(0x1e, 1, 0x38), ...patch(0x1e, 2, 0xf1), ...patch(0x1e, 3, 0x31), ...patch(0x1e, 4, 0xfc),
      ...xsend(0x1e),
    ];
    const r = traceSends(Buffer.from(code));
    expect(r.sent.map((t) => t.hex)).toEqual(["82 38 F1 31 FC"]);
    expect(r.sent[0]!.kind).toBe("control");
  });

  it("follows copies between string registers to each send, in order (ACSM60 STATUS_ROC)", () => {
    const code = [
      ...moveImm([0x85, 0xff, 0xf1, 0x2e, 0x99, 0x02, 0xff, 0xff]), 0x00, 0x11, 0x1e, 0x1d,
      ...moveImm([0x83, 0x01, 0xf1, 0x22, 0x99, 0x04]), 0x00, 0x11, 0x1f, 0x1d,
      ...patch(0x1e, 1, 0x01), ...xsend(0x1e),
      0x00, 0x11, 0x1e, 0x1f, ...xsend(0x1e),
    ];
    expect(traceSends(Buffer.from(code)).sent.map((t) => t.hex)).toEqual(["85 01 F1 2E 99 02 FF FF", "83 01 F1 22 99 04"]);
  });

  it("counts a send whose request it could not follow", () => {
    expect(traceSends(Buffer.from(xsend(0x1e)))).toEqual({ sent: [], unresolved: 1 });
  });
});

describe("sweepFolder", () => {
  let dir: string;
  beforeEach(() => {
    dir = mkdtempSync(join(tmpdir(), "sgbd-sweep-"));
  });
  afterEach(() => rmSync(dir, { recursive: true, force: true }));

  it("reports only jobs the policy allows, with every service they send", () => {
    writeFileSync(
      join(dir, "SYNTH_SG.prg"),
      buildPrg([
        {
          name: "STATUS_ROC",
          comment: "Status des Rollover Controller lesen / KWP2000: $22 ReadDataByCommonIdentifier",
          code: [
            ...moveImm([0x85, 0xff, 0xf1, 0x2e, 0x99, 0x02, 0xff, 0xff]),
            ...moveImm([0x83, 0x01, 0xf1, 0x22, 0x99, 0x04]),
          ],
        },
        // Refused by name (deny.substring:LOESCH): not the sweep's concern.
        { name: "FS_LOESCHEN", code: moveImm([0x83, 0x01, 0xf1, 0x14, 0xff, 0x00]) },
        { name: "STATUS_GUT", code: moveImm([0x83, 0x01, 0xf1, 0x22, 0x10, 0x0a]) },
        { name: "STATUS_LEER", code: [0x1d, 0x00] },
      ]),
    );
    const report = sweepFolder(dir, PolicyGuard.load());
    expect(report.files).toBe(1);
    expect(report.allowedJobs).toBe(3);
    expect(report.withoutTelegram).toEqual([{ ecu: "SYNTH_SG", job: "STATUS_LEER", rule: "allow.pattern" }]);
    expect(report.findings).toEqual([
      {
        ecu: "SYNTH_SG",
        job: "STATUS_ROC",
        rule: "allow.pattern",
        kind: "write",
        traced: false,
        comment: "Status des Rollover Controller lesen / KWP2000: $22 ReadDataByCommonIdentifier",
        telegrams: [
          { hex: "85 FF F1 2E 99 02 FF FF", service: 0x2e, name: "WriteDataByCommonIdentifier", kind: "write", lengthOk: true },
          { hex: "83 01 F1 22 99 04", service: 0x22, name: "ReadDataByCommonIdentifier", kind: "read", lengthOk: true },
        ],
      },
    ]);
  });

  it("leaves out a job the policy already refuses by deny.ecuJobs (ACSM60 STATUS_ROC)", () => {
    writeFileSync(
      join(dir, "ACSM60.prg"),
      buildPrg([{ name: "STATUS_ROC", code: moveImm([0x85, 0xff, 0xf1, 0x2e, 0x99, 0x02, 0xff, 0xff]) }]),
    );
    const report = sweepFolder(dir, PolicyGuard.load());
    expect(report.allowedJobs).toBe(0);
    expect(report.findings).toEqual([]);
  });

  it("names a file it cannot read instead of stopping", () => {
    writeFileSync(join(dir, "KAPUTT.prg"), "junk");
    const report = sweepFolder(dir, PolicyGuard.load());
    expect(report.unreadable).toEqual([{ file: "KAPUTT.prg", reason: expect.stringMatching(/not an EDIABAS object/) }]);
  });
});
