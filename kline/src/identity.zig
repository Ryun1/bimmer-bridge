//! Which USB device a serial device node belongs to.

const std = @import("std");
const builtin = @import("builtin");
const Pty = @import("testing/pty.zig").Pty;
const darwin = @import("sys/darwin.zig");

pub const UsbIds = struct { vid: u16, pid: u16 };
pub const IdentityError = error{ NotUsb, Io };

/// The FTDI FT232R in K+DCAN cables.
pub const ftdi_ft232r: UsbIds = .{ .vid = 0x0403, .pid = 0x6001 };

/// The USB vendor and product IDs of the device behind a serial device node. A node that
/// is not a USB serial device (a pty, Bluetooth, a built-in UART) is NotUsb.
/// A symlink is resolved first, so /dev/serial/by-id/... finds the same device as /dev/ttyUSB0.
pub fn lookup(path: []const u8) IdentityError!UsbIds {
    var given: [std.fs.max_path_bytes]u8 = undefined;
    const given_z = std.fmt.bufPrintZ(&given, "{s}", .{path}) catch return error.NotUsb;
    var real_buf: [std.fs.max_path_bytes]u8 = undefined;
    const real: []const u8 = if (std.c.realpath(given_z, &real_buf)) |r| std.mem.span(r) else path;
    return switch (builtin.os.tag) {
        .macos => lookupDarwin(real),
        .linux => lookupLinux(real),
        else => error.NotUsb,
    };
}

/// A sysfs idVendor/idProduct file: four hex digits and an optional newline.
pub fn parseSysfsHex(text: []const u8) error{Invalid}!u16 {
    const t = std.mem.trim(u8, text, " \t\r\n");
    if (t.len == 0 or t.len > 4) return error.Invalid;
    return std.fmt.parseInt(u16, t, 16) catch error.Invalid;
}

/// macOS: find the IOSerialBSDClient whose IOCalloutDevice (/dev/cu.*) or IODialinDevice
/// (/dev/tty.*) is path, then search its parents for idVendor and idProduct.
fn lookupDarwin(path: []const u8) IdentityError!UsbIds {
    const matching = darwin.IOServiceMatching("IOSerialBSDClient") orelse return error.Io;
    var iter: darwin.io_object_t = 0;
    if (darwin.IOServiceGetMatchingServices(darwin.kIOMainPortDefault, matching, &iter) != 0) return error.Io;
    defer _ = darwin.IOObjectRelease(iter);

    const callout_key = cfString("IOCalloutDevice") orelse return error.Io;
    defer darwin.CFRelease(callout_key);
    const dialin_key = cfString("IODialinDevice") orelse return error.Io;
    defer darwin.CFRelease(dialin_key);

    while (true) {
        const service = darwin.IOIteratorNext(iter);
        if (service == 0) return error.NotUsb;
        defer _ = darwin.IOObjectRelease(service);
        if (!propertyEquals(service, callout_key, path) and !propertyEquals(service, dialin_key, path)) continue;
        const vid = searchParentsU16(service, "idVendor") orelse return error.NotUsb;
        const pid = searchParentsU16(service, "idProduct") orelse return error.NotUsb;
        return .{ .vid = vid, .pid = pid };
    }
}

fn cfString(s: [*:0]const u8) ?darwin.CFStringRef {
    return darwin.CFStringCreateWithCString(null, s, darwin.kCFStringEncodingUTF8);
}

fn propertyEquals(service: darwin.io_object_t, key: darwin.CFStringRef, want: []const u8) bool {
    const v = darwin.IORegistryEntryCreateCFProperty(service, key, null, 0) orelse return false;
    defer darwin.CFRelease(v);
    if (darwin.CFGetTypeID(v) != darwin.CFStringGetTypeID()) return false;
    var buf: [1024]u8 = undefined;
    if (darwin.CFStringGetCString(v, &buf, buf.len, darwin.kCFStringEncodingUTF8) == 0) return false;
    return std.mem.eql(u8, std.mem.sliceTo(&buf, 0), want);
}

fn searchParentsU16(service: darwin.io_object_t, name: [*:0]const u8) ?u16 {
    const key = cfString(name) orelse return null;
    defer darwin.CFRelease(key);
    const v = darwin.IORegistryEntrySearchCFProperty(service, "IOService", key, null, darwin.kIORegistryIterateRecursively | darwin.kIORegistryIterateParents) orelse return null;
    defer darwin.CFRelease(v);
    if (darwin.CFGetTypeID(v) != darwin.CFNumberGetTypeID()) return null;
    var n: i32 = 0;
    if (darwin.CFNumberGetValue(v, darwin.kCFNumberSInt32Type, &n) == 0) return null;
    return std.math.cast(u16, n);
}

/// Linux: /sys/class/tty/<name>/device links to the USB interface (or a non-USB device);
/// walk up from it to the first directory that has idVendor and idProduct.
fn lookupLinux(path: []const u8) IdentityError!UsbIds {
    const name = std.fs.path.basename(path);
    var link: [std.fs.max_path_bytes]u8 = undefined;
    const link_z = std.fmt.bufPrintZ(&link, "/sys/class/tty/{s}/device", .{name}) catch return error.NotUsb;
    var real: [std.fs.max_path_bytes]u8 = undefined;
    const resolved = std.c.realpath(link_z, &real) orelse return error.NotUsb;
    var dir: []const u8 = std.mem.span(resolved);
    while (dir.len > "/sys/devices".len and std.mem.startsWith(u8, dir, "/sys/devices")) {
        if (readSysfsHex(dir, "idVendor")) |vid| {
            const pid = readSysfsHex(dir, "idProduct") orelse return error.NotUsb;
            return .{ .vid = vid, .pid = pid };
        }
        dir = std.fs.path.dirname(dir) orelse break;
    }
    return error.NotUsb;
}

fn readSysfsHex(dir: []const u8, file: []const u8) ?u16 {
    var pbuf: [std.fs.max_path_bytes]u8 = undefined;
    const p = std.fmt.bufPrintZ(&pbuf, "{s}/{s}", .{ dir, file }) catch return null;
    const fd = std.c.open(p, .{ .ACCMODE = .RDONLY, .CLOEXEC = true });
    if (fd < 0) return null;
    defer _ = std.c.close(fd);
    var buf: [16]u8 = undefined;
    const n = std.c.read(fd, &buf, buf.len);
    if (n <= 0) return null;
    return parseSysfsHex(buf[0..@intCast(n)]) catch null;
}

test "parseSysfsHex" {
    try std.testing.expectEqual(@as(u16, 0x0403), try parseSysfsHex("0403\n"));
    try std.testing.expectEqual(@as(u16, 0x6001), try parseSysfsHex("6001"));
    try std.testing.expectError(error.Invalid, parseSysfsHex("zz"));
    try std.testing.expectError(error.Invalid, parseSysfsHex(""));
    try std.testing.expectError(error.Invalid, parseSysfsHex("10000"));
}

test "a pty is not USB" {
    var pty = try Pty.open();
    defer pty.close();
    try std.testing.expectError(error.NotUsb, lookup(pty.slave_path));
}
