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

// <sys/termios.h>. std.c.termios types speed_t as an exhaustive enum, so a non-standard
// rate read back after IOSSIOSPEED would be illegal; this layout uses plain integers.
pub const tcflag_t = c_ulong;
pub const speed_t = c_ulong;
pub const NCCS = 20;
pub const termios = extern struct {
    c_iflag: tcflag_t,
    c_oflag: tcflag_t,
    c_cflag: tcflag_t,
    c_lflag: tcflag_t,
    c_cc: [NCCS]u8,
    c_ispeed: speed_t,
    c_ospeed: speed_t,
};
pub extern "c" fn tcgetattr(fd: c_int, t: *termios) c_int;
pub extern "c" fn tcsetattr(fd: c_int, action: c_int, t: *const termios) c_int;
pub extern "c" fn cfmakeraw(t: *termios) void;
pub extern "c" fn cfsetspeed(t: *termios, speed: speed_t) c_int;

pub const TCSANOW: c_int = 0;
pub const TCIOFLUSH: c_int = 3;
pub const VMIN = 16;
pub const VTIME = 17;
pub const IXON: tcflag_t = 0x200;
pub const IXOFF: tcflag_t = 0x400;
pub const IXANY: tcflag_t = 0x800;
pub const CSIZE: tcflag_t = 0x300;
pub const CS5: tcflag_t = 0x000;
pub const CS6: tcflag_t = 0x100;
pub const CS7: tcflag_t = 0x200;
pub const CS8: tcflag_t = 0x300;
pub const CSTOPB: tcflag_t = 0x400;
pub const CREAD: tcflag_t = 0x800;
pub const PARENB: tcflag_t = 0x1000;
pub const PARODD: tcflag_t = 0x2000;
pub const CLOCAL: tcflag_t = 0x8000;
pub const CRTSCTS: tcflag_t = 0x30000; // CCTS_OFLOW | CRTS_IFLOW

// IOKit and CoreFoundation, for finding the USB device behind a /dev node.
pub const io_object_t = c_uint;
pub const CFTypeRef = *const anyopaque;
pub const CFStringRef = *const anyopaque;
pub const CFMutableDictionaryRef = *anyopaque;
// These three are from IOKitLib.h (:112, :1013-1014), which Zig 0.16 cannot @cImport; they
// were checked with clang against the macOS 26 SDK when written.
pub const kIOMainPortDefault: c_uint = 0; // MACH_PORT_NULL
pub const kIORegistryIterateRecursively: c_uint = 1;
pub const kIORegistryIterateParents: c_uint = 2;
pub const kCFStringEncodingUTF8: u32 = 0x08000100;
pub const kCFNumberSInt32Type: isize = 3;
pub extern "c" fn IOServiceMatching(name: [*:0]const u8) ?CFMutableDictionaryRef;
pub extern "c" fn IOServiceGetMatchingServices(main_port: c_uint, matching: CFMutableDictionaryRef, existing: *io_object_t) c_int;
pub extern "c" fn IOIteratorNext(iterator: io_object_t) io_object_t;
pub extern "c" fn IOObjectRelease(object: io_object_t) c_int;
pub extern "c" fn IORegistryEntryCreateCFProperty(entry: io_object_t, key: CFStringRef, allocator: ?*const anyopaque, options: c_uint) ?CFTypeRef;
pub extern "c" fn IORegistryEntrySearchCFProperty(entry: io_object_t, plane: [*:0]const u8, key: CFStringRef, allocator: ?*const anyopaque, options: c_uint) ?CFTypeRef;
pub extern "c" fn CFStringCreateWithCString(alloc: ?*const anyopaque, cstr: [*:0]const u8, encoding: u32) ?CFStringRef;
pub extern "c" fn CFStringGetCString(str: CFStringRef, buffer: [*]u8, size: isize, encoding: u32) u8;
pub extern "c" fn CFNumberGetValue(number: CFTypeRef, the_type: isize, value: *anyopaque) u8;
pub extern "c" fn CFGetTypeID(cf: CFTypeRef) usize;
pub extern "c" fn CFStringGetTypeID() usize;
pub extern "c" fn CFNumberGetTypeID() usize;
pub extern "c" fn CFRelease(cf: CFTypeRef) void;

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

test "darwin termios and IOKit definitions match the SDK headers" {
    if (builtin.os.tag != .macos) return error.SkipZigTest;
    const c = @cImport({
        @cInclude("termios.h");
        // IOKitLib.h and CoreFoundation.h do not translate in Zig 0.16 (mach message types).
        @cInclude("CoreFoundation/CFString.h");
        @cInclude("CoreFoundation/CFNumber.h");
    });
    comptime {
        std.debug.assert(@sizeOf(c.struct_termios) == @sizeOf(termios));
        std.debug.assert(@offsetOf(c.struct_termios, "c_ispeed") == @offsetOf(termios, "c_ispeed"));
        std.debug.assert(@sizeOf(c.tcflag_t) == @sizeOf(tcflag_t));
        std.debug.assert(@sizeOf(c.speed_t) == @sizeOf(speed_t));
        std.debug.assert(c.NCCS == NCCS);
        std.debug.assert(c.TCSANOW == TCSANOW);
        std.debug.assert(c.TCIOFLUSH == TCIOFLUSH);
        std.debug.assert(c.VMIN == VMIN);
        std.debug.assert(c.VTIME == VTIME);
        std.debug.assert(c.IXON == IXON);
        std.debug.assert(c.IXOFF == IXOFF);
        std.debug.assert(c.IXANY == IXANY);
        std.debug.assert(c.CSIZE == CSIZE);
        std.debug.assert(c.CS5 == CS5);
        std.debug.assert(c.CS6 == CS6);
        std.debug.assert(c.CS7 == CS7);
        std.debug.assert(c.CS8 == CS8);
        std.debug.assert(c.CSTOPB == CSTOPB);
        std.debug.assert(c.CREAD == CREAD);
        std.debug.assert(c.PARENB == PARENB);
        std.debug.assert(c.PARODD == PARODD);
        std.debug.assert(c.CLOCAL == CLOCAL);
        std.debug.assert(c.CRTSCTS == CRTSCTS);
        std.debug.assert(c.kCFStringEncodingUTF8 == kCFStringEncodingUTF8);
        std.debug.assert(c.kCFNumberSInt32Type == kCFNumberSInt32Type);
    }
}
