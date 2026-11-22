//! The frame gate: sees each whole outgoing telegram and the declared protocol concept, and
//! refuses anything that could write to a car or rewrite the cable. It fails closed: no
//! concept, or a concept not listed here, refuses every send.

const std = @import("std");
const expectEqual = std.testing.expectEqual;

/// The protocol concepts the gate lets through (CommParameter[0] as the SGBD sets it).
pub const Concept = enum(u32) {
    bmw_fast = 0x010F,
    kwp2000_star = 0x010D,
    kwp2000_bmw = 0x010C,
    ds2 = 0x0006,
    dcan = 0x0110,
    _,
};

pub const Refusal = enum { no_concept, unknown_concept, refused_sid, adapter_write, malformed };

/// Service IDs refused under the header concepts, physically or functionally addressed. From
/// the design (a secondary source; ISO 14230-3 is paywalled). 0x10 is handled separately: only
/// the default session (next byte 0x81) is allowed. Changing this list is Ryan's decision.
const refused_sids = [_]u8{ 0x11, 0x14, 0x27, 0x28, 0x29, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x3B, 0x3D, 0x85 };

const start_session: u8 = 0x10;
const default_session: u8 = 0x81;

/// The tester/adapter address: a frame from F1 to F1 is for the cable itself.
const adapter_address: u8 = 0xF1;

/// Adapter commands that only read (type, version, ignition). Every other F1→F1 command
/// rewrites the cable's CAN mode, PLD mode or resets it (EdCustomAdapterCommon.cs:659-702,
/// EdInterfaceObd.cs:1171).
const adapter_reads = [_]u8{ 0xFA, 0xFD, 0xFE };

/// DS2 commands are manufacturer-specific. None is positively identified as a write yet, so
/// nothing is refused by command; DS2 relies on the job policy above the interpreter.
const ds2_refused_commands: []const u8 = &.{};

/// Null when the frame may be sent under the declared concept.
pub fn check(concept: ?u32, frame: []const u8) ?Refusal {
    const c = concept orelse return .no_concept;
    return switch (@as(Concept, @enumFromInt(c))) {
        .bmw_fast, .kwp2000_star, .kwp2000_bmw, .dcan => checkHeader(frame),
        .ds2 => checkDs2(frame),
        _ => .unknown_concept,
    };
}

fn checkHeader(frame: []const u8) ?Refusal {
    if (frame.len < 3) return .malformed;
    var data_start: usize = 3;
    var len: usize = frame[0] & 0x3F;
    if (len == 0) {
        if (frame.len < 4) return .malformed;
        len = frame[3];
        data_start = 4;
        if (len == 0) {
            if (frame.len < 6) return .malformed;
            len = (@as(usize, frame[4]) << 8) | frame[5];
            data_start = 6;
        }
    }
    if (len == 0 or frame.len <= data_start) return .malformed;
    const sid = frame[data_start];

    if (frame[1] == adapter_address and frame[2] == adapter_address) {
        return if (std.mem.indexOfScalar(u8, &adapter_reads, sid) == null) .adapter_write else null;
    }
    if (sid == start_session) {
        if (frame.len <= data_start + 1 or frame[data_start + 1] != default_session) return .refused_sid;
        return null;
    }
    if (std.mem.indexOfScalar(u8, &refused_sids, sid) != null) return .refused_sid;
    return null;
}

fn checkDs2(frame: []const u8) ?Refusal {
    // DS2: address, length, command…, checksum.
    if (frame.len < 3) return .malformed;
    if (std.mem.indexOfScalar(u8, ds2_refused_commands, frame[2]) != null) return .refused_sid;
    return null;
}

test "no concept refuses" {
    try expectEqual(Refusal.no_concept, check(null, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80 }).?);
}

test "unknown concept refuses" {
    try expectEqual(Refusal.unknown_concept, check(0x0001, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80 }).?);
}

test "bmw-fast read ident allowed" {
    try expectEqual(@as(?Refusal, null), check(0x010F, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80, 0x9F }));
}

test "bmw-fast clear dtc refused physical and functional" {
    try expectEqual(Refusal.refused_sid, check(0x010F, &.{ 0x83, 0x12, 0xF1, 0x14, 0xFF, 0xFF }).?);
    try expectEqual(Refusal.refused_sid, check(0x010F, &.{ 0xC3, 0xEF, 0xF1, 0x14, 0xFF, 0xFF }).?);
}

test "default session allowed, other sessions refused" {
    try expectEqual(@as(?Refusal, null), check(0x010F, &.{ 0x82, 0x12, 0xF1, 0x10, 0x81 }));
    try expectEqual(Refusal.refused_sid, check(0x010F, &.{ 0x82, 0x12, 0xF1, 0x10, 0x85 }).?);
    try expectEqual(Refusal.refused_sid, check(0x010F, &.{ 0x81, 0x12, 0xF1, 0x10 }).?);
}

test "every listed sid refused" {
    const sids = [_]u8{ 0x11, 0x14, 0x27, 0x28, 0x29, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x3B, 0x3D, 0x85 };
    for (sids) |sid| {
        try expectEqual(Refusal.refused_sid, check(0x010F, &.{ 0x82, 0x12, 0xF1, sid, 0x01 }).?);
        try expectEqual(Refusal.refused_sid, check(0x010F, &.{ 0xC2, 0xEF, 0xF1, sid, 0x01 }).?);
    }
}

test "read sids allowed" {
    for ([_]u8{ 0x17, 0x18, 0x1A, 0x21, 0x22, 0x3E }) |sid| {
        try expectEqual(@as(?Refusal, null), check(0x010F, &.{ 0x82, 0x12, 0xF1, sid, 0x01 }));
    }
}

test "length byte form (fmt len 0) decodes sid at offset 4" {
    try expectEqual(Refusal.refused_sid, check(0x010F, &.{ 0x80, 0x12, 0xF1, 0x02, 0x2E, 0x01 }).?);
    try expectEqual(@as(?Refusal, null), check(0x010F, &.{ 0x80, 0x12, 0xF1, 0x02, 0x1A, 0x80 }));
}

test "16-bit length form decodes sid at offset 6" {
    try expectEqual(Refusal.refused_sid, check(0x010F, &.{ 0x80, 0x12, 0xF1, 0x00, 0x00, 0x02, 0x31, 0x01 }).?);
    try expectEqual(@as(?Refusal, null), check(0x010F, &.{ 0x80, 0x12, 0xF1, 0x00, 0x00, 0x02, 0x22, 0x01 }));
}

test "adapter reads allowed, adapter writes refused" {
    for ([_]u8{ 0xFA, 0xFD, 0xFE }) |c| try expectEqual(@as(?Refusal, null), check(0x010F, &.{ 0x82, 0xF1, 0xF1, c, c }));
    for ([_]u8{ 0xFB, 0xFC, 0x06 }) |c| try expectEqual(Refusal.adapter_write, check(0x010F, &.{ 0x82, 0xF1, 0xF1, c, c }).?);
    try expectEqual(Refusal.adapter_write, check(0x010F, &.{ 0x84, 0xF1, 0xF1, 0x06, 0x00, 0x00, 0x00 }).?);
}

test "truncated header refuses as malformed" {
    try expectEqual(Refusal.malformed, check(0x010F, &.{ 0x82, 0x12 }).?);
    try expectEqual(Refusal.malformed, check(0x010F, &.{}).?);
    try expectEqual(Refusal.malformed, check(0x010F, &.{ 0x82, 0x12, 0xF1 }).?);
    try expectEqual(Refusal.malformed, check(0x010F, &.{ 0x80, 0x12, 0xF1 }).?);
    try expectEqual(Refusal.malformed, check(0x010F, &.{ 0x80, 0x12, 0xF1, 0x00, 0x00 }).?);
    try expectEqual(Refusal.malformed, check(0x010F, &.{ 0x80, 0x12, 0xF1, 0x00, 0x00, 0x01 }).?);
}

test "zero-length data is malformed" {
    try expectEqual(Refusal.malformed, check(0x010F, &.{ 0x80, 0x12, 0xF1, 0x00, 0x00, 0x00 }).?);
}

test "ds2 frames allowed" {
    try expectEqual(@as(?Refusal, null), check(0x0006, &.{ 0x12, 0x04, 0x00, 0x16 }));
}

test "ds2 still needs a frame" {
    try expectEqual(Refusal.malformed, check(0x0006, &.{}).?);
}

test "same rules for kwp2000*, kwp2000 bmw and d-can" {
    for ([_]u32{ 0x010D, 0x010C, 0x0110 }) |c| {
        try expectEqual(Refusal.refused_sid, check(c, &.{ 0x83, 0x12, 0xF1, 0x14, 0xFF, 0xFF }).?);
        try expectEqual(@as(?Refusal, null), check(c, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80 }));
        try expectEqual(Refusal.adapter_write, check(c, &.{ 0x82, 0xF1, 0xF1, 0xFB, 0xFB }).?);
    }
}
