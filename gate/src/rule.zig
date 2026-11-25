//! The telegram rule, version 1 (docs/api.md "Telegram rule"). Pure: no allocation, no I/O,
//! and total over every input (any concept, any frame of any length). `check` returns null
//! when the frame may be sent under the concept, else the refusal reason, named exactly as in
//! policy/telegram-cases.json.

const std = @import("std");

pub const Refusal = enum {
    no_concept,
    unknown_concept,
    ds2,
    malformed,
    adapter,
    service,
    /// allowed as the concept's framing reads it, but the same bytes read with the other
    /// framing (BMW-FAST vs KWP2000*) would not be: refused, so that a wrong idea of the
    /// concept can never turn an allowed read into a write.
    ambiguous,
};

/// One reviewed telegram: the target address (byte 1) and exactly its data bytes.
pub const Exact = struct {
    address: u8,
    data: []const u8,
};

/// policy/telegrams.json, as embedded at build time (see policy.zig).
pub const AllowList = struct {
    read_services: []const u8,
    default_session: u8,
    adapter_reads: []const u8,
    exact: []const Exact,
};

pub const concept_bmw_fast: u16 = 0x010F;
pub const concept_kwp2000_bmw: u16 = 0x010C;
pub const concept_dcan: u16 = 0x0110;
pub const concept_kwp2000_star: u16 = 0x010D;
pub const concept_ds2: u16 = 0x0006;

const tester: u8 = 0xF1;
const start_session: u8 = 0x10;

const Framing = enum { bmw_fast, kwp2000_star };

pub fn check(concept: ?u16, frame: []const u8, list: *const AllowList) ?Refusal {
    const c = concept orelse return .no_concept;
    const framing: Framing = switch (c) {
        concept_bmw_fast, concept_kwp2000_bmw, concept_dcan => .bmw_fast,
        concept_kwp2000_star => .kwp2000_star,
        concept_ds2 => return .ds2,
        else => return .unknown_concept,
    };
    const data = frameData(frame, framing) orelse return .malformed;
    if (judge(frame, data, list)) |r| return r;
    // Step 7: the other framing must not read the same bytes as something refused.
    const other: Framing = if (framing == .bmw_fast) .kwp2000_star else .bmw_fast;
    if (frameData(frame, other)) |alt| {
        if (judge(frame, alt, list) != null) return .ambiguous;
    }
    return null;
}

/// Steps 3 to 6 on one reading of the frame. frameData guarantees frame.len >= 4 and
/// data.len >= 1.
fn judge(frame: []const u8, data: []const u8, list: *const AllowList) ?Refusal {
    const target = frame[1];
    const source = frame[2];
    if (target == tester and source == tester) {
        return if (contains(list.adapter_reads, data[0])) null else .adapter;
    }
    if (contains(list.read_services, data[0])) return null;
    if (data.len == 2 and data[0] == start_session and data[1] == list.default_session) return null;
    for (list.exact) |e| {
        if (e.address == target and std.mem.eql(u8, e.data, data)) return null;
    }
    return .service;
}

/// The data bytes of a well-formed telegram (rule step 2), or null when it is malformed.
fn frameData(frame: []const u8, framing: Framing) ?[]const u8 {
    if (frame.len < 4) return null;
    const mode = frame[0] & 0xC0;
    if (mode != 0x80 and mode != 0xC0) return null;
    if (frame[2] != tester) return null;
    var start: usize = undefined;
    var len: usize = undefined;
    switch (framing) {
        .kwp2000_star => {
            len = frame[3];
            start = 4;
        },
        .bmw_fast => {
            len = frame[0] & 0x3F;
            start = 3;
            if (len == 0) {
                len = frame[3];
                start = 4;
                if (len == 0) {
                    if (frame.len < 6) return null;
                    len = (@as(usize, frame[4]) << 8) | frame[5];
                    start = 6;
                }
            }
        },
    }
    if (len == 0) return null;
    if (start > frame.len) return null;
    const rest = frame.len - start;
    if (rest != len and rest != len + 1) return null;
    return frame[start .. start + len];
}

fn contains(set: []const u8, b: u8) bool {
    return std.mem.indexOfScalar(u8, set, b) != null;
}

// ---- tests ---------------------------------------------------------------------------------

const testing = std.testing;

const test_list = AllowList{
    .read_services = &.{ 0x12, 0x13, 0x17, 0x18, 0x19, 0x1A, 0x21, 0x22, 0x23, 0x3E },
    .default_session = 0x81,
    .adapter_reads = &.{ 0xFA, 0xFD, 0xFE },
    .exact = &.{.{ .address = 0x57, .data = &.{ 0x30, 0x91, 0x01, 0x01 } }},
};

test "16-bit length form needs its two length bytes" {
    try testing.expectEqual(@as(?Refusal, .malformed), check(0x010F, &.{ 0x80, 0x12, 0xF1, 0x00, 0x00 }, &test_list));
    try testing.expectEqual(@as(?Refusal, null), check(0x010F, &.{ 0x80, 0x12, 0xF1, 0x00, 0x00, 0x01, 0x22 }, &test_list));
    try testing.expectEqual(@as(?Refusal, .malformed), check(0x010F, &.{ 0x80, 0x12, 0xF1, 0x00, 0x00, 0x00 }, &test_list));
}

test "a frame both framings can read is allowed only when both readings are" {
    // BMW-FAST reads $22 (35 data bytes); KWP2000* reads length 0x22 and service $2E.
    var a: [3 + 35]u8 = undefined;
    @memset(&a, 0x00);
    a[0] = 0xA3;
    a[1] = 0x12;
    a[2] = 0xF1;
    a[3] = 0x22;
    a[4] = 0x2E;
    try testing.expectEqual(@as(?Refusal, .ambiguous), check(0x010F, &a, &test_list));
    try testing.expectEqual(@as(?Refusal, .service), check(0x010D, &a, &test_list));
    // KWP2000* reads $22; BMW-FAST (0x2F = 47 data bytes) reads $2E.
    var b: [3 + 47]u8 = undefined;
    @memset(&b, 0x00);
    b[0] = 0xAF;
    b[1] = 0x12;
    b[2] = 0xF1;
    b[3] = 0x2E;
    b[4] = 0x22;
    try testing.expectEqual(@as(?Refusal, .ambiguous), check(0x010D, &b, &test_list));
    try testing.expectEqual(@as(?Refusal, .service), check(0x010F, &b, &test_list));
    // Both readings are reads: allowed under either concept.
    a[4] = 0x1A;
    try testing.expectEqual(@as(?Refusal, null), check(0x010F, &a, &test_list));
    try testing.expectEqual(@as(?Refusal, null), check(0x010D, &a, &test_list));
}

test "an empty allow-list refuses every service" {
    const empty = AllowList{ .read_services = &.{}, .default_session = 0x81, .adapter_reads = &.{}, .exact = &.{} };
    try testing.expectEqual(@as(?Refusal, .service), check(0x010F, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80 }, &empty));
    try testing.expectEqual(@as(?Refusal, .adapter), check(0x010F, &.{ 0x82, 0xF1, 0xF1, 0xFA, 0x00 }, &empty));
    // $10 81 is allowed by the rule itself (step 4), not by the list.
    try testing.expectEqual(@as(?Refusal, null), check(0x010F, &.{ 0x82, 0x12, 0xF1, 0x10, 0x81 }, &empty));
}

test "fuzz: never crashes, and anything allowed is a well-formed tester frame" {
    var prng = std.Random.DefaultPrng.init(0x5A17_E0F1);
    const r = prng.random();
    const concepts = [_]?u16{ null, 0x010F, 0x010C, 0x0110, 0x010D, 0x0006, 0x0000, 0xFFFF, 0x0001 };
    var buf: [70_000]u8 = undefined;
    var allowed: usize = 0;
    var i: usize = 0;
    while (i < 200_000) : (i += 1) {
        const n: usize = switch (r.uintLessThan(u8, 10)) {
            0 => buf.len,
            1 => r.uintLessThan(usize, buf.len),
            else => r.uintLessThan(usize, 12),
        };
        r.bytes(buf[0..n]);
        // Bias towards plausible headers so the deeper branches run.
        if (n > 0 and r.boolean()) buf[0] = (buf[0] & 0x3F) | 0x80;
        if (n > 2 and r.boolean()) buf[2] = 0xF1;
        if (n > 0 and r.uintLessThan(u8, 4) == 0) buf[0] = 0x80 | @as(u8, @intCast(@min(n -| 3, 0x3F)));
        if (n > 3 and r.uintLessThan(u8, 4) == 0) buf[3] = @intCast(@min(n -| 4, 0xFF));
        const c = concepts[r.uintLessThan(usize, concepts.len)];
        const frame = buf[0..n];
        if (check(c, frame, &test_list) == null) {
            allowed += 1;
            try testing.expect(frame.len >= 4);
            try testing.expect(frame[2] == 0xF1);
            try testing.expect(frame[0] & 0x80 != 0);
            try testing.expect(c != null and c.? != 0x0006);
        }
    }
    try testing.expect(allowed > 0);
}

test "oversized frames with a matching 16-bit length" {
    var buf: [65_535 + 8]u8 = undefined;
    @memset(&buf, 0x22);
    buf[0] = 0x80;
    buf[1] = 0x12;
    buf[2] = 0xF1;
    buf[3] = 0x00;
    buf[4] = 0xFF;
    buf[5] = 0xFF;
    try testing.expectEqual(@as(?Refusal, null), check(0x010F, buf[0 .. 6 + 65_535], &test_list));
    try testing.expectEqual(@as(?Refusal, null), check(0x010F, buf[0 .. 7 + 65_535], &test_list));
    try testing.expectEqual(@as(?Refusal, .malformed), check(0x010F, buf[0..], &test_list));
    try testing.expectEqual(@as(?Refusal, .malformed), check(0x010F, buf[0 .. 5 + 65_535], &test_list));
}
