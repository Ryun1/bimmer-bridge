//! Strict reader for policy/telegrams.json (version 1). Used at build time by
//! tools/gen_allowlist.zig, which turns the file into Zig source that is compiled into the
//! DLL, so a bad file fails the build instead of reaching the VM. Unknown keys, missing keys,
//! duplicate keys, a wrong version, bad hex, or a service the rule forbids allowing by ID are
//! all errors.

const std = @import("std");

pub const Error = error{
    WrongVersion,
    BadHexByte,
    BadHexData,
    EmptyData,
    EmptyNote,
    ForbiddenReadService,
    Duplicate,
} || std.json.ParseError(std.json.Scanner) || std.mem.Allocator.Error;

pub const Exact = struct { address: u8, data: []const u8, note: []const u8 };

pub const List = struct {
    version: u32,
    read_services: []const u8,
    default_session: u8,
    adapter_reads: []const u8,
    exact: []const Exact,
};

const RawExact = struct { address: []const u8, data: []const u8, note: []const u8 };
/// A JSON number, strictly (std.json would also accept the string "1" for an integer).
const Version = struct {
    value: u32,

    pub fn jsonParse(_: std.mem.Allocator, source: anytype, _: std.json.ParseOptions) !Version {
        const digits = switch (try source.next()) {
            .number => |d| d,
            else => return error.UnexpectedToken,
        };
        return .{ .value = std.fmt.parseInt(u32, digits, 10) catch return error.InvalidNumber };
    }
};

const Raw = struct {
    version: Version,
    readServices: []const []const u8,
    defaultSession: []const u8,
    adapterReads: []const []const u8,
    exact: []const RawExact,
};

/// Services the rule says are never allowed by service ID: $10 only as `10 <defaultSession>`,
/// $30 and $31 only as exact telegrams (docs/api.md "Telegram rule").
const never_by_service = [_]u8{ 0x10, 0x30, 0x31 };

/// Parses and validates. Everything returned is allocated in `arena`.
pub fn parse(arena: std.mem.Allocator, json: []const u8) Error!List {
    const raw = try std.json.parseFromSliceLeaky(Raw, arena, json, .{
        .ignore_unknown_fields = false,
        .duplicate_field_behavior = .@"error",
    });
    if (raw.version.value != 1) return error.WrongVersion;

    const reads = try arena.alloc(u8, raw.readServices.len);
    for (raw.readServices, reads, 0..) |s, *b, i| {
        b.* = try hexByte(s);
        if (std.mem.indexOfScalar(u8, &never_by_service, b.*) != null) return error.ForbiddenReadService;
        if (std.mem.indexOfScalar(u8, reads[0..i], b.*) != null) return error.Duplicate;
    }
    const adapter = try arena.alloc(u8, raw.adapterReads.len);
    for (raw.adapterReads, adapter, 0..) |s, *b, i| {
        b.* = try hexByte(s);
        if (std.mem.indexOfScalar(u8, adapter[0..i], b.*) != null) return error.Duplicate;
    }

    const exact = try arena.alloc(Exact, raw.exact.len);
    for (raw.exact, exact, 0..) |e, *out, i| {
        if (e.note.len == 0) return error.EmptyNote;
        out.* = .{ .address = try hexByte(e.address), .data = try hexData(arena, e.data), .note = e.note };
        // The same telegram to the same address twice (as the agent's parser refuses it).
        for (exact[0..i]) |prev| {
            if (prev.address == out.address and std.mem.eql(u8, prev.data, out.data)) return error.Duplicate;
        }
    }
    return .{
        .version = raw.version.value,
        .read_services = reads,
        .default_session = try hexByte(raw.defaultSession),
        .adapter_reads = adapter,
        .exact = exact,
    };
}

/// Exactly two upper-case hex digits (as the agent's parser requires).
fn hexByte(s: []const u8) Error!u8 {
    if (s.len != 2 or !isUpperHex(s[0]) or !isUpperHex(s[1])) return error.BadHexByte;
    return std.fmt.parseInt(u8, s, 16) catch error.BadHexByte;
}

fn isUpperHex(c: u8) bool {
    return (c >= '0' and c <= '9') or (c >= 'A' and c <= 'F');
}

/// Two-digit hex bytes separated by single spaces, at least one byte.
fn hexData(arena: std.mem.Allocator, s: []const u8) Error![]const u8 {
    if (s.len == 0) return error.EmptyData;
    if ((s.len + 1) % 3 != 0) return error.BadHexData;
    const n = (s.len + 1) / 3;
    const out = try arena.alloc(u8, n);
    for (out, 0..) |*b, i| {
        if (i > 0 and s[i * 3 - 1] != ' ') return error.BadHexData;
        b.* = hexByte(s[i * 3 .. i * 3 + 2]) catch return error.BadHexData;
    }
    return out;
}

/// The Zig source that tools/gen_allowlist.zig emits; src/policy.zig imports it as "allowlist".
pub fn writeZig(w: *std.Io.Writer, list: List) std.Io.Writer.Error!void {
    try w.writeAll("// Generated at build time from policy/telegrams.json by tools/gen_allowlist.zig. Do not edit.\n");
    try w.print("pub const version: u32 = {d};\n", .{list.version});
    try w.writeAll("pub const read_services = [_]u8{");
    try writeBytes(w, list.read_services);
    try w.writeAll("};\n");
    try w.print("pub const default_session: u8 = 0x{X:0>2};\n", .{list.default_session});
    try w.writeAll("pub const adapter_reads = [_]u8{");
    try writeBytes(w, list.adapter_reads);
    try w.writeAll("};\n");
    try w.writeAll("pub const Exact = struct { address: u8, data: []const u8 };\n");
    try w.writeAll("pub const exact = [_]Exact{\n");
    for (list.exact) |e| {
        try w.print("    .{{ .address = 0x{X:0>2}, .data = &[_]u8{{", .{e.address});
        try writeBytes(w, e.data);
        try w.writeAll("} },\n");
    }
    try w.writeAll("};\n");
}

fn writeBytes(w: *std.Io.Writer, bytes: []const u8) std.Io.Writer.Error!void {
    for (bytes, 0..) |b, i| {
        if (i > 0) try w.writeAll(", ");
        try w.print("0x{X:0>2}", .{b});
    }
}

// ---- tests ---------------------------------------------------------------------------------

const testing = std.testing;

fn parseText(json: []const u8) Error!List {
    // Leaks into the testing allocator's arena on purpose: tests are short-lived.
    const State = struct {
        var arena: ?std.heap.ArenaAllocator = null;
    };
    if (State.arena == null) State.arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    return parse(State.arena.?.allocator(), json);
}

const good =
    \\{"version":1,"readServices":["12","1A"],"defaultSession":"81","adapterReads":["FA"],
    \\ "exact":[{"address":"57","data":"30 91 01 01","note":"x"}]}
;

test "the shared policy file parses" {
    const list = try parseText(@embedFile("telegrams_json"));
    try testing.expectEqual(@as(u32, 1), list.version);
    try testing.expect(list.read_services.len > 0);
}

test "a minimal valid file" {
    const list = try parseText(good);
    try testing.expectEqualSlices(u8, &.{ 0x12, 0x1A }, list.read_services);
    try testing.expectEqualSlices(u8, &.{ 0x30, 0x91, 0x01, 0x01 }, list.exact[0].data);
}

test "strictness" {
    const R = struct {
        fn bad(json: []const u8) !void {
            if (parseText(json)) |_| return error.TestExpectedError else |_| {}
        }
    };
    // unknown key, top level and in an entry
    try R.bad(
        \\{"version":1,"readServices":[],"defaultSession":"81","adapterReads":[],"exact":[],"extra":1}
    );
    try R.bad(
        \\{"version":1,"readServices":[],"defaultSession":"81","adapterReads":[],"exact":[{"address":"57","data":"30","note":"x","mask":"FF"}]}
    );
    // missing key
    try R.bad(
        \\{"version":1,"readServices":[],"defaultSession":"81","adapterReads":[]}
    );
    // duplicate key
    try R.bad(
        \\{"version":1,"version":1,"readServices":[],"defaultSession":"81","adapterReads":[],"exact":[]}
    );
    // version
    try R.bad(
        \\{"version":2,"readServices":[],"defaultSession":"81","adapterReads":[],"exact":[]}
    );
    // bad hex bytes
    try R.bad(
        \\{"version":1,"readServices":["1"],"defaultSession":"81","adapterReads":[],"exact":[]}
    );
    try R.bad(
        \\{"version":1,"readServices":["0x12"],"defaultSession":"81","adapterReads":[],"exact":[]}
    );
    try R.bad(
        \\{"version":1,"readServices":["G1"],"defaultSession":"81","adapterReads":[],"exact":[]}
    );
    try R.bad(
        \\{"version":1,"readServices":[],"defaultSession":"+1","adapterReads":[],"exact":[]}
    );
    // bad data
    for ([_][]const u8{ "", "30  91", "3091", "30 9", " 30", "30 ", "30-91", "30 9G" }) |data| {
        var buf: [256]u8 = undefined;
        const json = try std.fmt.bufPrint(&buf,
            \\{{"version":1,"readServices":[],"defaultSession":"81","adapterReads":[],"exact":[{{"address":"57","data":"{s}","note":"x"}}]}}
        , .{data});
        try R.bad(json);
    }
    // empty note
    try R.bad(
        \\{"version":1,"readServices":[],"defaultSession":"81","adapterReads":[],"exact":[{"address":"57","data":"30","note":""}]}
    );
    // services the rule never allows by ID
    for ([_][]const u8{ "10", "30", "31" }) |s| {
        var buf: [256]u8 = undefined;
        const json = try std.fmt.bufPrint(&buf,
            \\{{"version":1,"readServices":["{s}"],"defaultSession":"81","adapterReads":[],"exact":[]}}
        , .{s});
        try R.bad(json);
    }
    // wrong types
    try R.bad(
        \\{"version":"1","readServices":[],"defaultSession":"81","adapterReads":[],"exact":[]}
    );
    try R.bad("not json");
}

test "generated source" {
    const list = try parseText(good);
    var buf: [1024]u8 = undefined;
    var w: std.Io.Writer = .fixed(&buf);
    try writeZig(&w, list);
    const out = w.buffered();
    try testing.expect(std.mem.indexOf(u8, out, "pub const read_services = [_]u8{0x12, 0x1A};") != null);
    try testing.expect(std.mem.indexOf(u8, out, ".{ .address = 0x57, .data = &[_]u8{0x30, 0x91, 0x01, 0x01} },") != null);
}
