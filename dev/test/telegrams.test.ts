// The telegram tool's parsing and comparison. Synthetic trace text in the format of the VM's
// IFH.trc (docs/native/m0-results.md section 4; the VM's IFH.trc.m0run1), and synthetic
// recordings in libkline's format (kline/src/recording.zig). No car data.
import { describe, expect, it } from "vitest";
import { compareExchanges, parseIfhTrace, parseRecording, stripChecksum, timing, type Exchange } from "../telegrams-lib.ts";

const CRLF = "\r\n";
const trace = (lines: string[]) => lines.join(CRLF) + CRLF;
const marker = (n: number) => `\t\t\t\t\t\t\t\t#@${n};P00002600h;#`;

describe("parseIfhTrace", () => {
  it("parses a SENDECUTEL block with no reply", () => {
    const t = trace([
      marker(116),
      "_STD     [        16] SENDECUTEL",
      marker(117),
      "_STD     [        16]  INPUT: 82 12 F1 1A 80 ",
      "_STD     [        16]   I -> D: 14 06 00 00 00 00 ",
      "_STD     [        16]   D -> I: 01 03 00 ",
      "_STD     [        16]   I -> D: 06 08 00 82 12 F1 1A 80 ",
      "_STD     [        32]   D -> I: 71 03 00 ",
      "_STD     [        32]  ERROR: IFH_0003",
      "_STD     [        32]  OUTPUT: --",
    ]);
    expect(parseIfhTrace(t)).toEqual([{ request: [0x82, 0x12, 0xf1, 0x1a, 0x80], response: null, error: "IFH_0003" }]);
  });

  it("parses a reply, and NO ERROR is no error", () => {
    const t = trace([
      "_STD     [         0] SENDECUTEL",
      "_STD     [         0]  INPUT: 82 12 F1 1A 80 ",
      "_STD     [        15]  ERROR: NO ERROR",
      "_STD     [        15]  OUTPUT: 82 F1 12 5A 80 5F ",
    ]);
    expect(parseIfhTrace(t)).toEqual([{ request: [0x82, 0x12, 0xf1, 0x1a, 0x80], response: [0x82, 0xf1, 0x12, 0x5a, 0x80, 0x5f], error: null }]);
  });

  it("joins INPUT continuation lines", () => {
    const t = trace([
      "_STD     [        47] SENDECUTEL",
      "_STD     [        47]  INPUT: 0D 01 00 00 80 25 00 00 F4 01 00 00 19 00 00 00 14 00 00 00 ",
      "_STD                          00 00 00 00 02 00 00 00 88 13 00 00 ",
      "_STD     [        47]   I -> D: 05 23 00 0D 01 00 00 80 25 00 00 F4 01 00 00 19 00 00 00 14 ",
      "_STD                            00 00 00 00 00 00 00 02 00 00 00 88 13 00 00 ",
      "_STD     [        48]  ERROR: NO ERROR",
      "_STD     [        48]  OUTPUT: 01 ",
      "_STD                           02 03 ",
    ]);
    const [x] = parseIfhTrace(t);
    expect(x!.request).toHaveLength(32);
    expect(x!.request.slice(-4)).toEqual([0x88, 0x13, 0x00, 0x00]);
    expect(x!.response).toEqual([1, 2, 3]);
  });

  it("skips other calls", () => {
    const t = trace([
      "_STD     [        47] SETECUCOMM",
      "_STD     [        47]  INPUT: 0F 01 00 00 ",
      "_STD     [        47]  ERROR: NO ERROR",
      "_STD     [        47]  OUTPUT: 06 ",
      "_STD     [        48] SENDECUTEL",
      "_STD     [        48]  INPUT: 82 12 F1 1A 80 ",
      "_STD     [        49]  ERROR: IFH_0003",
      "_STD     [        49]  OUTPUT: --",
      "_STD     [        50] STOPECUTELFREQUENT",
      "_STD     [        50]   I -> D: 0E 03 00 ",
    ]);
    expect(parseIfhTrace(t)).toHaveLength(1);
  });
});

describe("parseRecording", () => {
  const rec = (events: object[]) => events.map((e) => JSON.stringify(e)).join("\n") + "\n";

  it("recording: echo dropped, response kept, times measured", () => {
    const r = parseRecording(
      rec([
        { t_us: 0, op: "open", path: "/dev/cu.usbserial-X", latency: "1ms" },
        { t_us: 500, op: "concept", params: [271, 115200, 800, 20, 10, 2, 5000] },
        { t_us: 1000, op: "send", hex: "8212F11A801F" },
        { t_us: 2100, op: "recv", hex: "8212F11A801F" },
        { t_us: 9100, op: "recv", hex: "82F1125A" },
        { t_us: 9300, op: "recv", hex: "805F" },
        { t_us: 9900, op: "close" },
      ]),
    );
    expect(r.latency).toBe("1ms");
    expect(r.exchanges).toEqual([
      { request: [0x82, 0x12, 0xf1, 0x1a, 0x80, 0x1f], response: [0x82, 0xf1, 0x12, 0x5a, 0x80, 0x5f], error: null, tUs: 1000, echoUs: 1100, replyUs: 7000 },
    ]);
    expect(r.shortReads).toBe(0);
    expect(r.refused).toBe(0);
  });

  it("an echo split over two receives is still the echo", () => {
    const r = parseRecording(
      rec([
        { t_us: 1000, op: "send", hex: "8212F11A801F" },
        { t_us: 1500, op: "recv", hex: "8212F1" },
        { t_us: 2000, op: "recv", hex: "1A801F" },
        { t_us: 5000, op: "recv", hex: "82F1125A805F" },
      ]),
    );
    expect(r.exchanges[0]).toMatchObject({ response: [0x82, 0xf1, 0x12, 0x5a, 0x80, 0x5f], echoUs: 1000, replyUs: 3000 });
  });

  it("a send with no reply has a null response and no reply time", () => {
    const r = parseRecording(rec([{ t_us: 0, op: "open", path: "x", latency: "16ms" }, { t_us: 1000, op: "send", hex: "8212F11A801F" }, { t_us: 9000, op: "close" }]));
    expect(r.latency).toBe("16ms");
    expect(r.exchanges).toEqual([{ request: [0x82, 0x12, 0xf1, 0x1a, 0x80, 0x1f], response: null, error: null, tUs: 1000 }]);
  });

  it("a recv with a reason counts as a short read", () => {
    const r = parseRecording(
      rec([
        { t_us: 1000, op: "send", hex: "8212F11A801F" },
        { t_us: 2000, op: "recv", hex: "8212F11A801F" },
        { t_us: 60000, op: "recv", hex: "82F1", reason: "timeout" },
        { t_us: 61000, op: "refused", hex: "8312F114FFFF", reason: "gate" },
      ]),
    );
    expect(r.shortReads).toBe(1);
    expect(r.refused).toBe(1);
    expect(r.exchanges[0]!.response).toEqual([0x82, 0xf1]);
  });

  it("a line that is not an event is an error naming the line", () => {
    expect(() => parseRecording('{"t_us":0,"op":"open"}\nnot json\n')).toThrow(/line 2/);
  });
});

describe("stripChecksum", () => {
  it("drops a trailing 8-bit sum and leaves anything else", () => {
    expect(stripChecksum([0x82, 0x12, 0xf1, 0x1a, 0x80, 0x1f])).toEqual([0x82, 0x12, 0xf1, 0x1a, 0x80]);
    expect(stripChecksum([0x82, 0x12, 0xf1, 0x1a, 0x80])).toEqual([0x82, 0x12, 0xf1, 0x1a, 0x80]);
    expect(stripChecksum([])).toEqual([]);
  });
});

describe("compareExchanges", () => {
  const ex = (request: number[], response: number[] | null): Exchange => ({ request, response, error: null });
  const ident = [0x82, 0x12, 0xf1, 0x1a, 0x80];
  const reply = [0x82, 0xf1, 0x12, 0x5a, 0x80, 0x5f];

  it("checksum on one side only still matches", () => {
    const r = compareExchanges([ex(ident, reply)], [ex([...ident, 0x1f], reply)]);
    expect(r).toEqual({ matched: 1, firstDivergence: null });
  });

  it("first divergence names the index and both sides", () => {
    const extra = ex([0xb8, 0x12, 0xf1, 0x02, 0x1a, 0x80], null);
    const r = compareExchanges([ex(ident, reply), extra, ex(ident, reply)], [ex(ident, reply), ex(ident, reply)]);
    expect(r.matched).toBe(1);
    expect(r.firstDivergence).toEqual({ index: 1, vm: extra, native: ex(ident, reply) });
  });

  it("one side running out is a divergence with the other side null", () => {
    const r = compareExchanges([ex(ident, reply)], []);
    expect(r.firstDivergence).toEqual({ index: 0, vm: ex(ident, reply), native: null });
  });

  it("a differing reply diverges", () => {
    const r = compareExchanges([ex(ident, reply)], [ex(ident, null)]);
    expect(r.matched).toBe(0);
    expect(r.firstDivergence?.index).toBe(0);
  });
});

describe("timing", () => {
  it("timing stats: min, median, max", () => {
    const xs: Exchange[] = [1000, 3000, 2000].map((e, i) => ({ request: [1], response: [2], error: null, echoUs: e, replyUs: (i + 1) * 10_000 }));
    xs.push({ request: [1], response: null, error: null });
    expect(timing(xs)).toEqual({
      echo: { n: 3, minMs: 1, medianMs: 2, maxMs: 3 },
      reply: { n: 3, minMs: 10, medianMs: 20, maxMs: 30 },
    });
  });

  it("an even count takes the mean of the middle two, and none is n 0", () => {
    const xs: Exchange[] = [1000, 2000].map((e) => ({ request: [1], response: null, error: null, echoUs: e }));
    expect(timing(xs).echo).toEqual({ n: 2, minMs: 1, medianMs: 1.5, maxMs: 2 });
    expect(timing([]).reply).toEqual({ n: 0, minMs: 0, medianMs: 0, maxMs: 0 });
  });
});
