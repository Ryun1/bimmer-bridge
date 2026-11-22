//! libc calls the port needs that Zig 0.16's std.c does not declare, and a monotonic clock.

const std = @import("std");

pub extern "c" fn tcdrain(fd: c_int) c_int;
pub extern "c" fn tcflush(fd: c_int, queue: c_int) c_int;
pub extern "c" fn posix_openpt(flags: c_int) c_int;
pub extern "c" fn grantpt(fd: c_int) c_int;
pub extern "c" fn unlockpt(fd: c_int) c_int;
pub extern "c" fn ptsname(fd: c_int) ?[*:0]u8;
pub extern "c" fn mkdtemp(template: [*:0]u8) ?[*:0]u8;

/// errno of the last failed libc call.
pub fn errno() std.c.E {
    return std.c.errno(@as(c_int, -1));
}

/// Nanoseconds on the monotonic clock.
pub fn monotonicNs() u64 {
    var ts: std.c.timespec = undefined;
    if (std.c.clock_gettime(std.c.CLOCK.MONOTONIC, &ts) != 0) unreachable;
    return @as(u64, @intCast(ts.sec)) * std.time.ns_per_s + @as(u64, @intCast(ts.nsec));
}

pub fn sleepNs(ns: u64) void {
    var req: std.c.timespec = .{ .sec = @intCast(ns / std.time.ns_per_s), .nsec = @intCast(ns % std.time.ns_per_s) };
    var rem: std.c.timespec = undefined;
    while (std.c.nanosleep(&req, &rem) != 0) req = rem;
}
