//! Recordings of a cable session: JSON lines, one event per line,
//! `{"t_us":<monotonic µs since open>,"op":"<op>",...}`. The recorder writes them as a session
//! runs; `load` reads them back for replay.
//!
//! Ops and their fields: open {path, latency?}, line {baud, bits, parity}, dtr {on},
//! rts {on}, break {on}, concept {params}, interbyte {ms}, send {hex}, recv {hex}, purge,
//! refused {hex, reason}, close.

const std = @import("std");
const libc = @import("sys/libc.zig");

pub const Op = enum { open, line, dtr, rts, @"break", concept, interbyte, send, recv, purge, refused, close };

pub const Event = struct {
    t_us: u64,
    op: Op,
    /// send, recv and refused: the bytes. Empty otherwise.
    bytes: []const u8 = &.{},
};

pub const Recording = struct {
    arena: std.heap.ArenaAllocator,
    events: []const Event,

    pub fn deinit(self: *Recording) void {
        self.arena.deinit();
    }
};

pub const LoadError = error{ NoDevice, Io, BadRecording, OutOfMemory };

/// Reads a recording. A missing file is NoDevice; a line that is not a recording event is
/// BadRecording. Unknown fields are ignored, so later versions can add them.
pub fn load(gpa: std.mem.Allocator, path: []const u8) LoadError!Recording {
    var arena = std.heap.ArenaAllocator.init(gpa);
    errdefer arena.deinit();
    const a = arena.allocator();
    const text = try readFile(a, path);
    var events: std.ArrayList(Event) = .empty;
    var lines = std.mem.splitScalar(u8, text, '\n');
    while (lines.next()) |raw| {
        const line = std.mem.trim(u8, raw, " \r\t");
        if (line.len == 0) continue;
        const v = std.json.parseFromSliceLeaky(std.json.Value, a, line, .{}) catch return error.BadRecording;
        if (v != .object) return error.BadRecording;
        const op_v = v.object.get("op") orelse return error.BadRecording;
        if (op_v != .string) return error.BadRecording;
        const op = std.meta.stringToEnum(Op, op_v.string) orelse return error.BadRecording;
        const t_us: u64 = if (v.object.get("t_us")) |t| switch (t) {
            .integer => |i| @intCast(@max(i, 0)),
            else => 0,
        } else 0;
        var bytes: []const u8 = &.{};
        if (op == .send or op == .recv or op == .refused) {
            const h = v.object.get("hex") orelse return error.BadRecording;
            if (h != .string or h.string.len % 2 != 0) return error.BadRecording;
            const out = try a.alloc(u8, h.string.len / 2);
            bytes = std.fmt.hexToBytes(out, h.string) catch return error.BadRecording;
        }
        try events.append(a, .{ .t_us = t_us, .op = op, .bytes = bytes });
    }
    return .{ .arena = arena, .events = try events.toOwnedSlice(a) };
}

fn readFile(a: std.mem.Allocator, path: []const u8) LoadError![]u8 {
    const path_z = try a.dupeZ(u8, path);
    const fd = std.c.open(path_z, .{ .ACCMODE = .RDONLY, .CLOEXEC = true });
    if (fd < 0) return switch (libc.errno()) {
        .NOENT, .NOTDIR => error.NoDevice,
        else => error.Io,
    };
    defer _ = std.c.close(fd);
    var buf: std.ArrayList(u8) = .empty;
    var chunk: [4096]u8 = undefined;
    while (true) {
        const n = std.c.read(fd, &chunk, chunk.len);
        if (n < 0) {
            if (libc.errno() == .INTR) continue;
            return error.Io;
        }
        if (n == 0) break;
        try buf.appendSlice(a, chunk[0..@intCast(n)]);
    }
    return buf.items;
}

/// Appends events to `<dir>/kline-<yyyyMMddTHHmmssZ>-<pid>.jsonl`. A write failure is
/// remembered (`failed`) but never fails the session: the cable matters more than the record.
pub const Writer = struct {
    fd: c_int,
    start_ns: u64,
    failed: bool = false,

    pub fn create(dir: []const u8) error{Io}!Writer {
        var ts: std.c.timespec = undefined;
        if (std.c.clock_gettime(std.c.CLOCK.REALTIME, &ts) != 0) return error.Io;
        const es = std.time.epoch.EpochSeconds{ .secs = @intCast(ts.sec) };
        const day = es.getEpochDay().calculateYearDay();
        const md = day.calculateMonthDay();
        const secs = es.getDaySeconds();
        var buf: [std.fs.max_path_bytes]u8 = undefined;
        const trimmed = std.mem.trimEnd(u8, dir, "/");
        const path = std.fmt.bufPrintZ(&buf, "{s}/kline-{d:0>4}{d:0>2}{d:0>2}T{d:0>2}{d:0>2}{d:0>2}Z-{d}.jsonl", .{
            trimmed,                         day.year,                  md.month.numeric(),          @as(u32, md.day_index) + 1,
            secs.getHoursIntoDay(),          secs.getMinutesIntoHour(), secs.getSecondsIntoMinute(), std.c.getpid(),
        }) catch return error.Io;
        const fd = std.c.open(path, .{ .ACCMODE = .WRONLY, .CREAT = true, .APPEND = true, .CLOEXEC = true }, @as(std.c.mode_t, 0o600));
        if (fd < 0) return error.Io;
        return .{ .fd = fd, .start_ns = libc.monotonicNs() };
    }

    pub fn close(self: *Writer) void {
        if (self.fd >= 0) _ = std.c.close(self.fd);
        self.fd = -1;
    }

    /// One event. `fields` is either empty or starts with a comma: `,"on":true`.
    pub fn event(self: *Writer, op: Op, comptime fields_fmt: []const u8, args: anytype) void {
        var buf: [8192]u8 = undefined;
        const t_us = (libc.monotonicNs() - self.start_ns) / std.time.ns_per_us;
        const line = std.fmt.bufPrint(&buf, "{{\"t_us\":{d},\"op\":\"{s}\"" ++ fields_fmt ++ "}}\n", .{ t_us, @tagName(op) } ++ args) catch {
            self.failed = true;
            return;
        };
        self.writeAll(line);
    }

    /// An event carrying bytes as upper-case hex (send, recv, refused), plus optional extra fields.
    pub fn bytesEvent(self: *Writer, op: Op, bytes: []const u8, extra: []const u8) void {
        const t_us = (libc.monotonicNs() - self.start_ns) / std.time.ns_per_us;
        var head: [64]u8 = undefined;
        const h = std.fmt.bufPrint(&head, "{{\"t_us\":{d},\"op\":\"{s}\",\"hex\":\"", .{ t_us, @tagName(op) }) catch unreachable;
        self.writeAll(h);
        var chunk: [512]u8 = undefined;
        var rest = bytes;
        while (rest.len > 0) {
            const n = @min(rest.len, chunk.len / 2);
            const hex = "0123456789ABCDEF";
            for (rest[0..n], 0..) |b, i| {
                chunk[2 * i] = hex[b >> 4];
                chunk[2 * i + 1] = hex[b & 0xF];
            }
            self.writeAll(chunk[0 .. 2 * n]);
            rest = rest[n..];
        }
        self.writeAll("\"");
        self.writeAll(extra);
        self.writeAll("}\n");
    }

    fn writeAll(self: *Writer, bytes: []const u8) void {
        if (self.fd < 0) return;
        var rest = bytes;
        while (rest.len > 0) {
            const n = std.c.write(self.fd, rest.ptr, rest.len);
            if (n > 0) {
                rest = rest[@intCast(n)..];
            } else if (n < 0 and libc.errno() == .INTR) {
                continue;
            } else {
                self.failed = true;
                return;
            }
        }
    }
};

/// Writes a JSON string value (with quotes) for a path, escaping what JSON requires.
pub fn jsonString(buf: []u8, s: []const u8) []const u8 {
    var w: usize = 0;
    const put = struct {
        fn f(b: []u8, i: *usize, c: u8) void {
            if (i.* < b.len) b[i.*] = c;
            i.* += 1;
        }
    }.f;
    put(buf, &w, '"');
    for (s) |c| {
        switch (c) {
            '"', '\\' => {
                put(buf, &w, '\\');
                put(buf, &w, c);
            },
            0...0x1F => {
                const hex = "0123456789abcdef";
                for ("\\u00") |x| put(buf, &w, x);
                put(buf, &w, hex[c >> 4]);
                put(buf, &w, hex[c & 0xF]);
            },
            else => put(buf, &w, c),
        }
    }
    put(buf, &w, '"');
    return buf[0..@min(w, buf.len)];
}

test "load reads ops and bytes, and ignores unknown fields" {
    var tmp = try @import("testing/pty.zig").TmpDir.create();
    defer tmp.cleanup();
    var w = try Writer.create(tmp.path);
    w.event(.open, ",\"path\":{s},\"future\":1", .{"\"/dev/x\""});
    w.bytesEvent(.send, &.{ 0x82, 0x12, 0xF1 }, "");
    w.event(.close, "", .{});
    w.close();
    try std.testing.expect(!w.failed);
    var dir_buf: [256]u8 = undefined;
    const d = std.c.opendir(tmp.path).?;
    defer _ = std.c.closedir(d);
    var path: ?[]const u8 = null;
    while (std.c.readdir(d)) |ent| {
        const name = std.mem.sliceTo(@as([*:0]const u8, @ptrCast(&ent.name)), 0);
        if (std.mem.startsWith(u8, name, "kline-")) path = try std.fmt.bufPrint(&dir_buf, "{s}/{s}", .{ tmp.path, name });
    }
    var r = try load(std.testing.allocator, path.?);
    defer r.deinit();
    try std.testing.expectEqual(@as(usize, 3), r.events.len);
    try std.testing.expectEqual(Op.open, r.events[0].op);
    try std.testing.expectEqualSlices(u8, &.{ 0x82, 0x12, 0xF1 }, r.events[1].bytes);
    try std.testing.expectEqual(Op.close, r.events[2].op);
}

test "load of a bad line is BadRecording" {
    var tmp = try @import("testing/pty.zig").TmpDir.create();
    defer tmp.cleanup();
    var buf: [256]u8 = undefined;
    const p = try std.fmt.bufPrintZ(&buf, "{s}/bad.jsonl", .{tmp.path});
    const fd = std.c.open(p, .{ .ACCMODE = .WRONLY, .CREAT = true }, @as(std.c.mode_t, 0o600));
    _ = std.c.write(fd, "{\"op\":\"nope\"}\n", 14);
    _ = std.c.close(fd);
    try std.testing.expectError(error.BadRecording, load(std.testing.allocator, p));
}

test "jsonString escapes quotes, backslashes and control bytes" {
    var buf: [64]u8 = undefined;
    try std.testing.expectEqualStrings("\"a\\\"b\\\\c\\u000a\"", jsonString(&buf, "a\"b\\c\n"));
}
