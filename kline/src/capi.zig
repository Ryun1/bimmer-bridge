//! The C ABI over Session (include/kline.h). Every int return is a kline reason (Reason);
//! a null handle gives KLINE_IO and never crashes.

const std = @import("std");
const kline = @import("kline");
const Session = kline.Session;
const Reason = kline.session.Reason;
const builtin = @import("builtin");

const alloc = std.heap.c_allocator;
const io: c_int = @intFromEnum(Reason.io);

fn r(x: Reason) c_int {
    return @intFromEnum(x);
}

export fn kline_open(path: ?[*:0]const u8, record_dir: ?[*:0]const u8, out: ?*?*Session) c_int {
    const o = out orelse return io;
    o.* = null;
    const p = path orelse return io;
    const dir: ?[]const u8 = if (record_dir) |d| std.mem.span(d) else null;
    o.* = Session.openDevice(alloc, std.mem.span(p), dir, null) catch |e| return r(kline.session.reasonOf(e));
    return 0;
}

export fn kline_open_replay(recording_path: ?[*:0]const u8, out: ?*?*Session) c_int {
    const o = out orelse return io;
    o.* = null;
    const p = recording_path orelse return io;
    o.* = Session.openReplay(alloc, std.mem.span(p)) catch |e| return r(kline.session.reasonOf(e));
    return 0;
}

export fn kline_close(k: ?*Session) void {
    if (k) |s| s.close();
}

export fn kline_set_line(k: ?*Session, baud: u32, data_bits: u8, parity: u8) c_int {
    const s = k orelse return io;
    const par: kline.port.Parity = switch (parity) {
        0 => .none,
        1 => .even,
        2 => .odd,
        else => return r(.unsupported),
    };
    if (data_bits < 5 or data_bits > 8) return r(.unsupported);
    return r(s.setLine(.{ .baud = baud, .data_bits = @intCast(data_bits), .parity = par }));
}

export fn kline_set_dtr(k: ?*Session, on: c_int) c_int {
    const s = k orelse return io;
    return r(s.setDtr(on != 0));
}

export fn kline_set_rts(k: ?*Session, on: c_int) c_int {
    const s = k orelse return io;
    return r(s.setRts(on != 0));
}

export fn kline_get_dsr(k: ?*Session, dsr: ?*c_int) c_int {
    const s = k orelse return io;
    const d = dsr orelse return io;
    var v = false;
    const res = s.getDsr(&v);
    d.* = @intFromBool(v);
    return r(res);
}

export fn kline_set_break(k: ?*Session, on: c_int) c_int {
    const s = k orelse return io;
    return r(s.setBreak(on != 0));
}

export fn kline_set_concept(k: ?*Session, params: ?[*]const u32, count: usize) c_int {
    const s = k orelse return io;
    if (count > 0 and params == null) return io;
    return r(s.setConcept(if (count == 0) &.{} else params.?[0..count]));
}

export fn kline_set_interbyte(k: ?*Session, ms: u32) c_int {
    const s = k orelse return io;
    return r(s.setInterByte(ms));
}

export fn kline_send(k: ?*Session, data: ?[*]const u8, len: usize) c_int {
    const s = k orelse return io;
    if (len > 0 and data == null) return io;
    return r(s.send(if (len == 0) &.{} else data.?[0..len]));
}

export fn kline_receive(k: ?*Session, buf: ?[*]u8, len: usize, timeout_ms: u32, tel_end_ms: u32) c_int {
    const s = k orelse return io;
    if (len > 0 and buf == null) return io;
    return r(s.receive(if (len == 0) &.{} else buf.?[0..len], timeout_ms, tel_end_ms));
}

export fn kline_purge(k: ?*Session) c_int {
    const s = k orelse return io;
    return r(s.purge());
}

export fn kline_cancel(k: ?*Session, on: c_int) void {
    if (k) |s| s.cancel(on != 0);
}

export fn kline_last_error(k: ?*Session) c_int {
    const s = k orelse return io;
    return r(s.lastError());
}

/// The number of FTDI 0403:6001 serial devices; when exactly one, its path is written to
/// path (NUL-terminated, if it fits). Opens nothing.
export fn kline_find_cable(path: ?[*]u8, path_len: usize) c_int {
    const prefix = if (builtin.os.tag == .macos) "cu.usbserial-" else "ttyUSB";
    var count: c_int = 0;
    var first: [std.fs.max_path_bytes]u8 = undefined;
    var first_len: usize = 0;
    const dir = std.c.opendir("/dev") orelse return 0;
    defer _ = std.c.closedir(dir);
    while (std.c.readdir(dir)) |ent| {
        const name = std.mem.sliceTo(@as([*:0]const u8, @ptrCast(&ent.name)), 0);
        if (!std.mem.startsWith(u8, name, prefix)) continue;
        var buf: [std.fs.max_path_bytes]u8 = undefined;
        const full = std.fmt.bufPrint(&buf, "/dev/{s}", .{name}) catch continue;
        const ids = kline.identity.lookup(full) catch continue;
        if (ids.vid != kline.identity.ftdi_ft232r.vid or ids.pid != kline.identity.ftdi_ft232r.pid) continue;
        if (count == 0) {
            @memcpy(first[0..full.len], full);
            first_len = full.len;
        }
        count += 1;
    }
    if (count == 1) {
        if (path) |p| {
            if (first_len < path_len) {
                @memcpy(p[0..first_len], first[0..first_len]);
                p[first_len] = 0;
            }
        }
    }
    return count;
}

export fn kline_version() [*:0]const u8 {
    return "0.1.0";
}

test "replay round trip through the C ABI" {
    var tmp = try kline.testing.TmpDir.create();
    defer tmp.cleanup();
    var buf: [256]u8 = undefined;
    const path = try std.fmt.bufPrintZ(&buf, "{s}/r.jsonl", .{tmp.path});
    const content =
        \\{"t_us":0,"op":"send","hex":"8212F11A809F"}
        \\{"t_us":1,"op":"recv","hex":"8212F11A809F82F1125A805F"}
        \\
    ;
    const fd = std.c.open(path, .{ .ACCMODE = .WRONLY, .CREAT = true }, @as(std.c.mode_t, 0o600));
    _ = std.c.write(fd, content.ptr, content.len);
    _ = std.c.close(fd);

    var k: ?*Session = null;
    try std.testing.expectEqual(@as(c_int, 0), kline_open_replay(path.ptr, &k));
    defer kline_close(k);
    const params = [_]u32{ 0x010F, 115200, 800, 20, 10, 2, 5000 };
    try std.testing.expectEqual(@as(c_int, 0), kline_set_concept(k, &params, params.len));
    const ident = [_]u8{ 0x82, 0x12, 0xF1, 0x1A, 0x80, 0x9F };
    try std.testing.expectEqual(@as(c_int, 0), kline_send(k, &ident, ident.len));
    var got: [12]u8 = undefined;
    try std.testing.expectEqual(@as(c_int, 0), kline_receive(k, &got, got.len, 100, 50));
    try std.testing.expectEqualSlices(u8, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80, 0x9F, 0x82, 0xF1, 0x12, 0x5A, 0x80, 0x5F }, &got);
    const clear = [_]u8{ 0x83, 0x12, 0xF1, 0x14, 0xFF, 0xFF };
    try std.testing.expectEqual(@as(c_int, 5), kline_send(k, &clear, clear.len));
    try std.testing.expectEqual(@as(c_int, 5), kline_last_error(k));
}

test "open of a missing device is no_device and leaves the handle null" {
    var k: ?*Session = null;
    try std.testing.expectEqual(@as(c_int, 1), kline_open("/dev/does-not-exist", null, &k));
    try std.testing.expect(k == null);
}

test "null handle is io, never a crash" {
    var b: [1]u8 = undefined;
    var dsr: c_int = 0;
    try std.testing.expectEqual(@as(c_int, 6), kline_send(null, &b, 1));
    try std.testing.expectEqual(@as(c_int, 6), kline_receive(null, &b, 1, 1, 1));
    try std.testing.expectEqual(@as(c_int, 6), kline_set_line(null, 9600, 8, 1));
    try std.testing.expectEqual(@as(c_int, 6), kline_get_dsr(null, &dsr));
    try std.testing.expectEqual(@as(c_int, 6), kline_last_error(null));
    kline_cancel(null, 1);
    kline_close(null);
}

test "bad parity value is unsupported" {
    var pty = try kline.testing.Pty.open();
    defer pty.close();
    // Through Session directly: kline_open requires an FTDI cable, a pty is not one.
    var lock = try kline.testing.TmpDir.create();
    defer lock.cleanup();
    const s = try Session.openDeviceOpts(std.heap.c_allocator, pty.slave_path, null, lock.path, false);
    defer kline_close(s);
    try std.testing.expectEqual(@as(c_int, 10), kline_set_line(s, 9600, 8, 7));
    try std.testing.expectEqual(@as(c_int, 0), kline_set_line(s, 9600, 8, 1));
}

test "version" {
    try std.testing.expectEqualStrings("0.1.0", std.mem.span(kline_version()));
}

test "find cable with none present writes nothing" {
    var buf: [64]u8 = .{0} ** 64;
    const n = kline_find_cable(&buf, buf.len);
    try std.testing.expect(n >= 0);
    if (n != 1) try std.testing.expectEqual(@as(u8, 0), buf[0]);
}
