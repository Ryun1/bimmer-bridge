//! Runs every text in policy/telegrams-parse-cases.json (embedded by build.zig as
//! "telegrams_parse_cases_json") through the list parser: the agent's TelegramRule runs the same
//! texts, so a list loads in both or in neither.

const std = @import("std");
const allowlist_json = @import("allowlist_json.zig");

const Case = struct { name: []const u8, valid: bool, text: []const u8 };
const File = struct { comment: []const u8, cases: []const Case };

test "policy/telegrams-parse-cases.json" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const file = try std.json.parseFromSliceLeaky(File, arena.allocator(), @embedFile("telegrams_parse_cases_json"), .{});
    try std.testing.expect(file.cases.len > 0);
    for (file.cases) |c| {
        var case_arena = std.heap.ArenaAllocator.init(std.testing.allocator);
        defer case_arena.deinit();
        const loaded = if (allowlist_json.parse(case_arena.allocator(), c.text)) |_| true else |_| false;
        if (loaded != c.valid) {
            std.debug.print("parse case '{s}': expected valid={}, got {}\n", .{ c.name, c.valid, loaded });
            return error.TestUnexpectedResult;
        }
    }
}
