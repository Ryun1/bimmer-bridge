//! Darwin serial ioctls that Zig 0.16's standard library does not declare.
//! Values are for macOS arm64 and x86_64 (identical); the test checks them against the SDK.

const std = @import("std");
const builtin = @import("builtin");

/// ioctl with the request as the SDK declares it (unsigned long). std.c.ioctl takes c_int,
/// which cannot hold the IOSS* requests.
pub extern "c" fn ioctl(fd: c_int, request: c_ulong, ...) c_int;

pub const TIOCEXCL: c_ulong = 0x2000740d; // _IO('t', 13)
pub const TIOCSBRK: c_ulong = 0x2000747b; // _IO('t', 123)
pub const TIOCCBRK: c_ulong = 0x2000747a; // _IO('t', 122)
pub const TIOCMBIS: c_ulong = 0x8004746c; // _IOW('t', 108, int)
pub const TIOCMBIC: c_ulong = 0x8004746b; // _IOW('t', 107, int)
pub const TIOCMGET: c_ulong = 0x4004746a; // _IOR('t', 106, int)
pub const IOSSIOSPEED: c_ulong = 0x80085402; // _IOW('T', 2, speed_t)
/// Takes the FTDI latency in milliseconds (unsigned long). Use 1, never 0. Resets on replug.
pub const IOSSDATALAT: c_ulong = 0x80085400; // _IOW('T', 0, unsigned long)
pub const FIONREAD: c_ulong = 0x4004667f; // _IOR('f', 127, int)

pub const TIOCM_DTR: c_int = 0x002;
pub const TIOCM_RTS: c_int = 0x004;
pub const TIOCM_DSR: c_int = 0x100;

test "darwin ioctl constants match the SDK headers" {
    if (builtin.os.tag != .macos) return error.SkipZigTest;
    const c = @cImport({
        @cInclude("sys/ioctl.h");
        @cInclude("IOKit/serial/ioss.h");
    });
    // Checked again at compile time, so a cross-compiled `--test-no-exec` build verifies them.
    comptime {
        std.debug.assert(@as(c_ulong, c.TIOCEXCL) == TIOCEXCL);
        std.debug.assert(@as(c_ulong, 0x2000740d) == TIOCEXCL);
        std.debug.assert(@as(c_ulong, c.TIOCSBRK) == TIOCSBRK);
        std.debug.assert(@as(c_ulong, 0x2000747b) == TIOCSBRK);
        std.debug.assert(@as(c_ulong, c.TIOCCBRK) == TIOCCBRK);
        std.debug.assert(@as(c_ulong, 0x2000747a) == TIOCCBRK);
        std.debug.assert(@as(c_ulong, c.TIOCMBIS) == TIOCMBIS);
        std.debug.assert(@as(c_ulong, 0x8004746c) == TIOCMBIS);
        std.debug.assert(@as(c_ulong, c.TIOCMBIC) == TIOCMBIC);
        std.debug.assert(@as(c_ulong, 0x8004746b) == TIOCMBIC);
        std.debug.assert(@as(c_ulong, c.TIOCMGET) == TIOCMGET);
        std.debug.assert(@as(c_ulong, c.IOSSIOSPEED) == IOSSIOSPEED);
        std.debug.assert(@as(c_ulong, 0x80085402) == IOSSIOSPEED);
        std.debug.assert(@as(c_ulong, c.IOSSDATALAT) == IOSSDATALAT);
        std.debug.assert(@as(c_ulong, 0x80085400) == IOSSDATALAT);
        std.debug.assert(@as(c_ulong, c.FIONREAD) == FIONREAD);
        std.debug.assert(@as(c_ulong, 0x4004667f) == FIONREAD);
        std.debug.assert(@as(c_int, c.TIOCM_DTR) == TIOCM_DTR);
        std.debug.assert(@as(c_int, c.TIOCM_RTS) == TIOCM_RTS);
        std.debug.assert(@as(c_int, c.TIOCM_DSR) == TIOCM_DSR);
    }
    try std.testing.expectEqual(@as(c_ulong, c.TIOCEXCL), TIOCEXCL);
    try std.testing.expectEqual(@as(c_ulong, 0x2000740d), TIOCEXCL);
    try std.testing.expectEqual(@as(c_ulong, c.TIOCSBRK), TIOCSBRK);
    try std.testing.expectEqual(@as(c_ulong, 0x2000747b), TIOCSBRK);
    try std.testing.expectEqual(@as(c_ulong, c.TIOCCBRK), TIOCCBRK);
    try std.testing.expectEqual(@as(c_ulong, 0x2000747a), TIOCCBRK);
    try std.testing.expectEqual(@as(c_ulong, c.TIOCMBIS), TIOCMBIS);
    try std.testing.expectEqual(@as(c_ulong, 0x8004746c), TIOCMBIS);
    try std.testing.expectEqual(@as(c_ulong, c.TIOCMBIC), TIOCMBIC);
    try std.testing.expectEqual(@as(c_ulong, 0x8004746b), TIOCMBIC);
    try std.testing.expectEqual(@as(c_ulong, c.TIOCMGET), TIOCMGET);
    try std.testing.expectEqual(@as(c_ulong, c.IOSSIOSPEED), IOSSIOSPEED);
    try std.testing.expectEqual(@as(c_ulong, 0x80085402), IOSSIOSPEED);
    try std.testing.expectEqual(@as(c_ulong, c.IOSSDATALAT), IOSSDATALAT);
    try std.testing.expectEqual(@as(c_ulong, 0x80085400), IOSSDATALAT);
    try std.testing.expectEqual(@as(c_ulong, c.FIONREAD), FIONREAD);
    try std.testing.expectEqual(@as(c_ulong, 0x4004667f), FIONREAD);
    try std.testing.expectEqual(@as(c_int, c.TIOCM_DTR), TIOCM_DTR);
    try std.testing.expectEqual(@as(c_int, c.TIOCM_RTS), TIOCM_RTS);
    try std.testing.expectEqual(@as(c_int, c.TIOCM_DSR), TIOCM_DSR);
}
