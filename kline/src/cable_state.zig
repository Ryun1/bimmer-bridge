//! The cable's state without opening it: present or not, the right cable or not, and whether
//! another session holds its lock. Never opens the tty, so a health check cannot toggle DTR
//! or disturb a session that has it.

const std = @import("std");
const port = @import("port.zig");
const identity = @import("identity.zig");
const libc = @import("sys/libc.zig");
const Pty = @import("testing/pty.zig").Pty;
const TmpDir = @import("testing/pty.zig").TmpDir;

pub const CableState = enum(c_int) { absent = 0, free = 1, locked = 2, not_ftdi = 3, io = 4 };

/// The cable at path: absent, not an FTDI FT232R, held by another session (locked), or free.
/// lock_dir is for tests; null is Port.open's default.
pub fn cableState(path: []const u8, lock_dir: ?[]const u8) CableState {
    return cableStateOpts(path, lock_dir, true);
}

/// cableState with the FTDI check optional (tests use ptys).
pub fn cableStateOpts(path: []const u8, lock_dir: ?[]const u8, require_ftdi: bool) CableState {
    var given_buf: [std.fs.max_path_bytes]u8 = undefined;
    const given_z = std.fmt.bufPrintZ(&given_buf, "{s}", .{path}) catch return .absent;
    // Resolved as Port.open does, so the lock file is the one a session would take.
    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    const real = std.c.realpath(given_z, &path_buf) orelse return switch (libc.errno()) {
        .NOENT, .NOTDIR, .NXIO, .NODEV => .absent,
        else => .io,
    };
    const path_z: [:0]const u8 = std.mem.span(real);

    if (require_ftdi) {
        const ids = identity.lookup(path_z) catch |e| return switch (e) {
            error.NotUsb => .not_ftdi,
            error.Io => .io,
        };
        if (ids.vid != identity.ftdi_ft232r.vid or ids.pid != identity.ftdi_ft232r.pid) return .not_ftdi;
    }

    var lock_buf: [std.fs.max_path_bytes]u8 = undefined;
    const lock_path = port.lockPath(&lock_buf, lock_dir orelse port.default_lock_dir, path_z) catch return .io;
    // Never created here: no lock file means nobody has held the cable since boot.
    const fd = std.c.open(lock_path, .{ .ACCMODE = .RDONLY, .CLOEXEC = true, .NOFOLLOW = true });
    if (fd < 0) return switch (libc.errno()) {
        .NOENT => .free,
        else => .io,
    };
    defer _ = std.c.close(fd);
    if (std.c.flock(fd, std.c.LOCK.EX | std.c.LOCK.NB) != 0) return switch (libc.errno()) {
        .AGAIN => .locked,
        else => .io,
    };
    _ = std.c.flock(fd, std.c.LOCK.UN);
    return .free;
}

// ---------------------------------------------------------------- tests

const testing = std.testing;

test "cable state of a missing path is absent" {
    try testing.expectEqual(CableState.absent, cableState("/dev/does-not-exist", null));
}

test "cable state is free when nobody holds the lock" {
    var pty = try Pty.open();
    defer pty.close();
    var lock = try TmpDir.create();
    defer lock.cleanup();
    try testing.expectEqual(CableState.free, cableStateOpts(pty.slave_path, lock.path, false));
}

test "locked while another fd holds the lock" {
    var pty = try Pty.open();
    defer pty.close();
    var lock = try TmpDir.create();
    defer lock.cleanup();
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    var real_buf: [std.fs.max_path_bytes]u8 = undefined;
    const real = std.mem.span(std.c.realpath(pty.slave_path, &real_buf).?);
    const lp = try port.lockPath(&buf, lock.path, real);
    const fd = std.c.open(lp, .{ .ACCMODE = .RDWR, .CREAT = true }, @as(std.c.mode_t, 0o600));
    try testing.expect(fd >= 0);
    defer _ = std.c.close(fd);
    try testing.expectEqual(@as(c_int, 0), std.c.flock(fd, std.c.LOCK.EX));
    try testing.expectEqual(CableState.locked, cableStateOpts(pty.slave_path, lock.path, false));
    _ = std.c.flock(fd, std.c.LOCK.UN);
    try testing.expectEqual(CableState.free, cableStateOpts(pty.slave_path, lock.path, false));
}

test "checking the state does not open the device" {
    var pty = try Pty.open();
    defer pty.close();
    var lock = try TmpDir.create();
    defer lock.cleanup();
    try testing.expectEqual(@as(c_int, 0), chmod(pty.slave_path, 0));
    // The device itself cannot be opened now (EACCES), yet the check still answers.
    const fd = std.c.open(pty.slave_path, .{ .ACCMODE = .RDWR, .NOCTTY = true, .NONBLOCK = true });
    if (fd >= 0) {
        _ = std.c.close(fd);
        return error.SkipZigTest; // running as root: chmod 000 does not stop an open
    }
    try testing.expectEqual(CableState.free, cableStateOpts(pty.slave_path, lock.path, false));
}

test "the check leaves no lock file behind when there was none" {
    var pty = try Pty.open();
    defer pty.close();
    var lock = try TmpDir.create();
    defer lock.cleanup();
    _ = cableStateOpts(pty.slave_path, lock.path, false);
    const d = std.c.opendir(lock.path).?;
    defer _ = std.c.closedir(d);
    while (std.c.readdir(d)) |ent| {
        const name = std.mem.sliceTo(@as([*:0]const u8, @ptrCast(&ent.name)), 0);
        try testing.expect(std.mem.eql(u8, name, ".") or std.mem.eql(u8, name, ".."));
    }
}

test "a pty is not an FTDI cable" {
    var pty = try Pty.open();
    defer pty.close();
    try testing.expectEqual(CableState.not_ftdi, cableState(pty.slave_path, null));
}

extern "c" fn chmod(path: [*:0]const u8, mode: std.c.mode_t) c_int;
