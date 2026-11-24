//! Runs every case in policy/telegram-cases.json (embedded by build.zig as
//! "telegram_cases_json") against the rule with the compiled-in policy/telegrams.json.

const std = @import("std");
const rule = @import("rule.zig");
const policy = @import("policy.zig");

const Case = struct {
    concept: ?[]const u8,
    frame: []const u8,
    allowed: bool,
    reason: ?[]const u8,
    note: []const u8,
};
const File = struct { comment: []const u8, cases: []const Case };

fn parseHex(out: []u8, s: []const u8) ![]u8 {
    var n: usize = 0;
    var it = std.mem.tokenizeScalar(u8, s, ' ');
    while (it.next()) |tok| : (n += 1) {
        if (tok.len != 2) return error.BadHex;
        out[n] = try std.fmt.parseInt(u8, tok, 16);
    }
    return out[0..n];
}

test "policy/telegram-cases.json" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const file = try std.json.parseFromSliceLeaky(File, arena.allocator(), @embedFile("telegram_cases_json"), .{});
    try std.testing.expect(file.cases.len > 0);

    var failures: usize = 0;
    for (file.cases, 0..) |c, i| {
        const concept: ?u16 = if (c.concept) |s| try std.fmt.parseInt(u16, s, 16) else null;
        var buf: [512]u8 = undefined;
        const frame = try parseHex(&buf, c.frame);
        const got = rule.check(concept, frame, &policy.list);
        const got_name: ?[]const u8 = if (got) |r| @tagName(r) else null;
        const ok = (got == null) == c.allowed and
            ((got_name == null and c.reason == null) or
                (got_name != null and c.reason != null and std.mem.eql(u8, got_name.?, c.reason.?)));
        if (!ok) {
            failures += 1;
            std.debug.print("case {d} ({s} / {s}: {s}): expected {s}, got {s}\n", .{
                i,                         c.concept orelse "null",   c.frame, c.note,
                c.reason orelse "allowed", got_name orelse "allowed",
            });
        }
    }
    if (failures > 0) std.debug.print("telegram-cases.json: {d} cases, {d} failed\n", .{ file.cases.len, failures });
    try std.testing.expectEqual(@as(usize, 0), failures);
}
