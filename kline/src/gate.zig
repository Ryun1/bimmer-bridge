//! The frame gate: sees each whole outgoing telegram and the declared protocol concept, and
//! allows it only under the shared telegram rule (docs/api.md "Telegram rule"): read services
//! by service ID, anything else only as an exact reviewed telegram to its address, from
//! policy/telegrams.json. It is the same rule and the same embedded list as the VM's
//! XGate32.dll (gate/), so both backends enforce one rule. It fails closed: no concept, an
//! unknown concept, DS2 (version 1 allows none), or a malformed frame refuses.

const std = @import("std");
const telegram = @import("telegram_policy");

pub const Refusal = telegram.rule.Refusal;

/// Null when the frame may be sent under the declared concept. The concept is the SGBD's
/// CommParameter[0]; concepts are 16-bit, so a larger value is an unknown concept.
pub fn check(concept: ?u32, frame: []const u8) ?Refusal {
    const c = concept orelse return .no_concept;
    if (c > 0xFFFF) return .unknown_concept;
    return telegram.rule.check(@intCast(c), frame, &telegram.list);
}

const expectEqual = std.testing.expectEqual;

test "every shared case in policy/telegram-cases.json, through this gate" {
    const Case = struct { concept: ?[]const u8, frame: []const u8, allowed: bool, reason: ?[]const u8, note: []const u8 };
    const File = struct { comment: []const u8, cases: []const Case };
    const parsed = try std.json.parseFromSlice(File, std.testing.allocator, @embedFile("telegram_cases"), .{});
    defer parsed.deinit();
    try std.testing.expect(parsed.value.cases.len > 0);
    for (parsed.value.cases) |case| {
        const concept: ?u32 = if (case.concept) |hex| try std.fmt.parseInt(u32, hex, 16) else null;
        var buf: [256]u8 = undefined;
        var n: usize = 0;
        var it = std.mem.tokenizeScalar(u8, case.frame, ' ');
        while (it.next()) |byte| : (n += 1) buf[n] = try std.fmt.parseInt(u8, byte, 16);
        const got = check(concept, buf[0..n]);
        const want: ?Refusal = if (case.allowed) null else std.meta.stringToEnum(Refusal, case.reason.?).?;
        expectEqual(want, got) catch |e| {
            std.debug.print("case '{s}' ({s})\n", .{ case.frame, case.note });
            return e;
        };
    }
}

test "a concept above 16 bits is unknown, not truncated into a known one" {
    try expectEqual(@as(?Refusal, .unknown_concept), check(0x1_010F, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80 }));
}

test "what the old deny-list let through is now refused" {
    // BMW-specific $A6, OBD-II mode $04 (clear emission faults) and DS2 all passed before.
    try expectEqual(@as(?Refusal, .service), check(0x010F, &.{ 0x86, 0x22, 0xF1, 0xA6, 0x00, 0x03, 0x30, 0x40, 0x01 }));
    try expectEqual(@as(?Refusal, .service), check(0x010F, &.{ 0xC1, 0x33, 0xF1, 0x04 }));
    try expectEqual(@as(?Refusal, .ds2), check(0x0006, &.{ 0x12, 0x05, 0x0B, 0x03, 0x1F }));
}

test "the voltage source (NVE_65 STATUS_BORDNETZSPANNUNG) passes as its exact telegram only" {
    try expectEqual(@as(?Refusal, null), check(0x010F, &.{ 0x84, 0x57, 0xF1, 0x30, 0x91, 0x01, 0x01 }));
    try expectEqual(@as(?Refusal, .service), check(0x010F, &.{ 0x84, 0x12, 0xF1, 0x30, 0x91, 0x01, 0x01 }));
}
