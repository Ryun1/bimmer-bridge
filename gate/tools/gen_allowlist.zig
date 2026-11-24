//! Build-time generator: validates policy/telegrams.json (embedded by build.zig as
//! "telegrams_json") with src/allowlist_json.zig and prints the Zig source that src/policy.zig
//! imports as "allowlist". Any problem with the file fails the build.

const std = @import("std");
const allowlist_json = @import("allowlist_json");

pub fn main(init: std.process.Init) !void {
    var arena = std.heap.ArenaAllocator.init(init.gpa);
    defer arena.deinit();
    const list = allowlist_json.parse(arena.allocator(), @embedFile("telegrams_json")) catch |err| {
        std.debug.print("policy/telegrams.json is not a valid version 1 telegram allow-list: {s}\n", .{@errorName(err)});
        std.process.exit(1);
    };
    var buf: [4096]u8 = undefined;
    var fw = std.Io.File.stdout().writer(init.io, &buf);
    try allowlist_json.writeZig(&fw.interface, list);
    try fw.interface.flush();
}
