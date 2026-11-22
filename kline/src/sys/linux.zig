//! Linux serial definitions that Zig 0.16's standard library does not declare (termios2,
//! serial_struct, ASYNC_LOW_LATENCY) plus the tty ioctls the port uses. Values are the
//! asm-generic ones used by x86_64, aarch64, arm and riscv64; the test checks them against
//! the kernel headers.

const std = @import("std");
const builtin = @import("builtin");

comptime {
    if (builtin.os.tag == .linux) switch (builtin.cpu.arch) {
        .x86_64, .x86, .aarch64, .aarch64_be, .arm, .armeb, .thumb, .riscv64, .riscv32 => {},
        else => @compileError("kline: Linux ioctl values here are asm-generic; this architecture differs"),
    };
}

/// ioctl with the request as glibc and musl declare it (unsigned long).
pub extern "c" fn ioctl(fd: c_int, request: c_ulong, ...) c_int;

pub const tcflag_t = c_uint;
pub const cc_t = u8;
pub const speed_t = c_uint;
pub const NCCS2 = 19;

/// <asm/termbits.h> struct termios2: termios with explicit input and output speeds.
pub const struct_termios2 = extern struct {
    c_iflag: tcflag_t,
    c_oflag: tcflag_t,
    c_cflag: tcflag_t,
    c_lflag: tcflag_t,
    c_line: cc_t,
    c_cc: [NCCS2]cc_t,
    c_ispeed: speed_t,
    c_ospeed: speed_t,
};

pub const TCGETS2: c_ulong = 0x802c542a; // _IOR('T', 0x2A, struct termios2)
pub const TCSETS2: c_ulong = 0x402c542b; // _IOW('T', 0x2B, struct termios2)
pub const TCSETSW2: c_ulong = 0x402c542c; // _IOW('T', 0x2C, struct termios2): drain first
pub const BOTHER: c_uint = 0o010000;
pub const CBAUD: c_uint = 0o010017;

/// <linux/serial.h> struct serial_struct.
pub const struct_serial_struct = extern struct {
    type: c_int,
    line: c_int,
    port: c_uint,
    irq: c_int,
    flags: c_int,
    xmit_fifo_size: c_int,
    custom_divisor: c_int,
    baud_base: c_int,
    close_delay: c_ushort,
    io_type: u8,
    reserved_char: [1]u8,
    hub6: c_int,
    closing_wait: c_ushort,
    closing_wait2: c_ushort,
    iomem_base: ?*u8,
    iomem_reg_shift: c_ushort,
    port_high: c_uint,
    iomap_base: c_ulong,
};

pub const TIOCGSERIAL: c_ulong = 0x541e;
pub const TIOCSSERIAL: c_ulong = 0x541f;
pub const ASYNC_LOW_LATENCY: c_int = 1 << 13;

pub const TIOCEXCL: c_ulong = 0x540c;
pub const TIOCSBRK: c_ulong = 0x5427;
pub const TIOCCBRK: c_ulong = 0x5428;
pub const TIOCMGET: c_ulong = 0x5415;
pub const TIOCMBIS: c_ulong = 0x5416;
pub const TIOCMBIC: c_ulong = 0x5417;
pub const FIONREAD: c_ulong = 0x541b;

pub const TIOCM_DTR: c_int = 0x002;
pub const TIOCM_RTS: c_int = 0x004;
pub const TIOCM_DSR: c_int = 0x100;

test "linux constants match the kernel headers" {
    if (builtin.os.tag != .linux) return error.SkipZigTest;
    const c = @cImport({
        @cInclude("asm/termbits.h");
        @cInclude("asm/ioctls.h");
        @cInclude("asm/termios.h");
        @cInclude("linux/serial.h");
    });
    // Checked again at compile time, so a cross-compiled `--test-no-exec` build verifies them.
    comptime {
        std.debug.assert(@as(usize, @sizeOf(c.struct_termios2)) == @sizeOf(struct_termios2));
        std.debug.assert(@as(usize, @offsetOf(c.struct_termios2, "c_ispeed")) == @offsetOf(struct_termios2, "c_ispeed"));
        std.debug.assert(@as(usize, @sizeOf(c.struct_serial_struct)) == @sizeOf(struct_serial_struct));
        std.debug.assert(@as(usize, @offsetOf(c.struct_serial_struct, "flags")) == @offsetOf(struct_serial_struct, "flags"));
        std.debug.assert(@as(c_ulong, c.TCGETS2) == TCGETS2);
        std.debug.assert(@as(c_ulong, c.TCSETS2) == TCSETS2);
        std.debug.assert(@as(c_ulong, c.TCSETSW2) == TCSETSW2);
        std.debug.assert(@as(c_uint, c.BOTHER) == BOTHER);
        std.debug.assert(@as(c_uint, c.CBAUD) == CBAUD);
        std.debug.assert(@as(c_ulong, c.TIOCGSERIAL) == TIOCGSERIAL);
        std.debug.assert(@as(c_ulong, c.TIOCSSERIAL) == TIOCSSERIAL);
        std.debug.assert(@as(c_int, c.ASYNC_LOW_LATENCY) == ASYNC_LOW_LATENCY);
        std.debug.assert(@as(c_ulong, c.TIOCEXCL) == TIOCEXCL);
        std.debug.assert(@as(c_ulong, c.TIOCSBRK) == TIOCSBRK);
        std.debug.assert(@as(c_ulong, c.TIOCCBRK) == TIOCCBRK);
        std.debug.assert(@as(c_ulong, c.TIOCMGET) == TIOCMGET);
        std.debug.assert(@as(c_ulong, c.TIOCMBIS) == TIOCMBIS);
        std.debug.assert(@as(c_ulong, c.TIOCMBIC) == TIOCMBIC);
        std.debug.assert(@as(c_ulong, c.FIONREAD) == FIONREAD);
        std.debug.assert(@as(c_int, c.TIOCM_DTR) == TIOCM_DTR);
        std.debug.assert(@as(c_int, c.TIOCM_RTS) == TIOCM_RTS);
        std.debug.assert(@as(c_int, c.TIOCM_DSR) == TIOCM_DSR);
    }
    try std.testing.expectEqual(@as(usize, @sizeOf(c.struct_termios2)), @sizeOf(struct_termios2));
    try std.testing.expectEqual(@as(usize, @offsetOf(c.struct_termios2, "c_ispeed")), @offsetOf(struct_termios2, "c_ispeed"));
    try std.testing.expectEqual(@as(usize, @sizeOf(c.struct_serial_struct)), @sizeOf(struct_serial_struct));
    try std.testing.expectEqual(@as(usize, @offsetOf(c.struct_serial_struct, "flags")), @offsetOf(struct_serial_struct, "flags"));
    try std.testing.expectEqual(@as(c_ulong, c.TCGETS2), TCGETS2);
    try std.testing.expectEqual(@as(c_ulong, c.TCSETS2), TCSETS2);
    try std.testing.expectEqual(@as(c_ulong, c.TCSETSW2), TCSETSW2);
    try std.testing.expectEqual(@as(c_uint, c.BOTHER), BOTHER);
    try std.testing.expectEqual(@as(c_uint, c.CBAUD), CBAUD);
    try std.testing.expectEqual(@as(c_ulong, c.TIOCGSERIAL), TIOCGSERIAL);
    try std.testing.expectEqual(@as(c_ulong, c.TIOCSSERIAL), TIOCSSERIAL);
    try std.testing.expectEqual(@as(c_int, c.ASYNC_LOW_LATENCY), ASYNC_LOW_LATENCY);
    try std.testing.expectEqual(@as(c_ulong, c.TIOCEXCL), TIOCEXCL);
    try std.testing.expectEqual(@as(c_ulong, c.TIOCSBRK), TIOCSBRK);
    try std.testing.expectEqual(@as(c_ulong, c.TIOCCBRK), TIOCCBRK);
    try std.testing.expectEqual(@as(c_ulong, c.TIOCMGET), TIOCMGET);
    try std.testing.expectEqual(@as(c_ulong, c.TIOCMBIS), TIOCMBIS);
    try std.testing.expectEqual(@as(c_ulong, c.TIOCMBIC), TIOCMBIC);
    try std.testing.expectEqual(@as(c_ulong, c.FIONREAD), FIONREAD);
    try std.testing.expectEqual(@as(c_int, c.TIOCM_DTR), TIOCM_DTR);
    try std.testing.expectEqual(@as(c_int, c.TIOCM_RTS), TIOCM_RTS);
    try std.testing.expectEqual(@as(c_int, c.TIOCM_DSR), TIOCM_DSR);
}
