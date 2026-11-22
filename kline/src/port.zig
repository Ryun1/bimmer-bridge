//! The K+DCAN cable's serial port: open with a lock and exclusive access, check the cable is
//! an FTDI FT232R, and set and read back line settings.

const std = @import("std");
const builtin = @import("builtin");
const identity = @import("identity.zig");
const libc = @import("sys/libc.zig");
const darwin = @import("sys/darwin.zig");
const linux = @import("sys/linux.zig");
const Pty = @import("testing/pty.zig").Pty;
const TmpDir = @import("testing/pty.zig").TmpDir;

const is_darwin = builtin.os.tag == .macos;
const is_linux = builtin.os.tag == .linux;
comptime {
    if (!is_darwin and !is_linux) @compileError("kline supports macOS and Linux only");
}
const sys = if (is_darwin) darwin else linux;

pub const Parity = enum { none, even, odd };
pub const LineSettings = struct { baud: u32, data_bits: u4, parity: Parity };
pub const OpenOptions = struct {
    /// Refuse anything but an FTDI FT232R (0403:6001).
    require_ftdi: bool = true,
    /// Where the lock file goes; for tests. The default is always /tmp, never taken from the
    /// environment, so a run under sudo or another session still meets the same lock.
    lock_dir: ?[]const u8 = null,
};
pub const OpenError = error{ NoDevice, Locked, NotFtdi, Permission, Io };
pub const LineError = error{ Unsupported, Io };
pub const IoError = error{Io};
pub const LatencyError = error{ Unsupported, Io };
pub const ReceiveError = error{ Timeout, Io };

pub const Port = struct {
    fd: c_int,
    lock_fd: c_int,

    /// Opens a serial device for exclusive use: identity check, lock file, open, TIOCEXCL,
    /// raw mode. A missing device is NoDevice, a device another process holds is Locked.
    pub fn open(path: []const u8, opts: OpenOptions) OpenError!Port {
        var given_buf: [std.fs.max_path_bytes]u8 = undefined;
        const given_z = std.fmt.bufPrintZ(&given_buf, "{s}", .{path}) catch return error.NoDevice;

        // 1. Present at all? Checked first so a missing cable is never reported as NotFtdi.
        //    Resolved to its real path, so a symlink (/dev/serial/by-id/... on Linux) gets the
        //    same identity and the same lock as the node it points to.
        var path_buf: [std.fs.max_path_bytes]u8 = undefined;
        const real = std.c.realpath(given_z, &path_buf) orelse return switch (libc.errno()) {
            .NOENT, .NOTDIR, .NXIO, .NODEV => error.NoDevice,
            .ACCES, .PERM => error.Permission,
            else => error.Io,
        };
        const path_z: [:0]const u8 = std.mem.span(real);

        // 2. The right cable.
        if (opts.require_ftdi) {
            const ids = identity.lookup(path_z) catch |e| switch (e) {
                error.NotUsb => return error.NotFtdi,
                error.Io => return error.Io,
            };
            if (ids.vid != identity.ftdi_ft232r.vid or ids.pid != identity.ftdi_ft232r.pid) return error.NotFtdi;
        }

        // 3. The lock file. flock is released by the kernel when its holder exits, so a file
        //    left behind by a crashed run never blocks.
        var lock_buf: [std.fs.max_path_bytes]u8 = undefined;
        const lock_path = lockPath(&lock_buf, opts.lock_dir orelse default_lock_dir, path_z) catch return error.Io;
        const lock_fd = std.c.open(lock_path, .{ .ACCMODE = .RDWR, .CREAT = true, .CLOEXEC = true, .NOFOLLOW = true }, @as(std.c.mode_t, 0o666));
        if (lock_fd < 0) return switch (libc.errno()) {
            .ACCES, .PERM => error.Permission,
            else => error.Io,
        };
        errdefer _ = std.c.close(lock_fd);
        // Readable and lockable by every user, whoever created it (umask applies at create),
        // so a lock file made under sudo never shuts the user out. Fails harmlessly when
        // someone else owns it.
        _ = fchmod(lock_fd, 0o666);
        if (std.c.flock(lock_fd, std.c.LOCK.EX | std.c.LOCK.NB) != 0) return switch (libc.errno()) {
            .AGAIN => error.Locked,
            else => error.Io,
        };

        // 4. The device itself.
        const fd = std.c.open(path_z, .{ .ACCMODE = .RDWR, .NOCTTY = true, .NONBLOCK = true, .CLOEXEC = true });
        if (fd < 0) return switch (libc.errno()) {
            .NOENT, .NXIO, .NODEV => error.NoDevice,
            .ACCES, .PERM => error.Permission,
            .BUSY => error.Locked,
            else => error.Io,
        };
        errdefer _ = std.c.close(fd);

        // 5. No other open of the device while we hold it.
        if (sys.ioctl(fd, sys.TIOCEXCL) != 0) return error.Io;

        // 6. Raw, 8N1, no flow control, reads return at once.
        var p: Port = .{ .fd = fd, .lock_fd = lock_fd };
        p.makeRaw() catch return error.Io;
        return p;
    }

    /// Releases the device, then the lock. Exclusive mode is cleared first: on Linux it is a
    /// flag on the tty, which outlives this fd wherever something else keeps the tty alive
    /// (a pty's master does), and would refuse the next open with EBUSY.
    pub fn close(self: *Port) void {
        if (self.fd >= 0) {
            _ = sys.ioctl(self.fd, sys.TIOCNXCL);
            _ = std.c.close(self.fd);
        }
        self.fd = -1;
        if (self.lock_fd >= 0) _ = std.c.close(self.lock_fd);
        self.lock_fd = -1;
    }

    /// Sets baud rate, data bits and parity (one stop bit). 9600, 19200, 38400, 57600 and
    /// 115200 are standard rates; any other rate (10400 for KWP init) uses IOSSIOSPEED on
    /// macOS and termios2 BOTHER on Linux, and is Unsupported where the driver refuses it.
    /// A parity change waits for pending output to drain first.
    pub fn setLine(self: *Port, s: LineSettings) LineError!void {
        const size: Tcflag = switch (s.data_bits) {
            5 => sys.CS5,
            6 => sys.CS6,
            7 => sys.CS7,
            8 => sys.CS8,
            else => return error.Unsupported,
        };
        const parity: Tcflag = switch (s.parity) {
            .none => 0,
            .even => sys.PARENB,
            .odd => sys.PARENB | sys.PARODD,
        };
        if (is_darwin) return self.setLineDarwin(s.baud, size, parity);
        return self.setLineLinux(s.baud, size, parity);
    }

    /// The line settings as the driver reports them.
    pub fn getLine(self: *Port) LineError!LineSettings {
        var t: Termios = undefined;
        try self.getAttr(&t);
        return .{
            .baud = @intCast(if (is_darwin) t.c_ospeed else t.c_ospeed),
            .data_bits = switch (t.c_cflag & sys.CSIZE) {
                sys.CS5 => 5,
                sys.CS6 => 6,
                sys.CS7 => 7,
                else => 8,
            },
            .parity = if (t.c_cflag & sys.PARENB == 0) .none else if (t.c_cflag & sys.PARODD != 0) .odd else .even,
        };
    }

    pub fn setDtr(self: *Port, on: bool) IoError!void {
        return self.modemBits(sys.TIOCM_DTR, on);
    }

    pub fn setRts(self: *Port, on: bool) IoError!void {
        return self.modemBits(sys.TIOCM_RTS, on);
    }

    pub fn getDsr(self: *Port) IoError!bool {
        var bits: c_int = 0;
        if (sys.ioctl(self.fd, sys.TIOCMGET, &bits) != 0) return error.Io;
        return bits & sys.TIOCM_DSR != 0;
    }

    /// Holds the line in break until cleared. The caller times the pulse.
    pub fn setBreak(self: *Port, on: bool) IoError!void {
        if (sys.ioctl(self.fd, if (on) sys.TIOCSBRK else sys.TIOCCBRK) != 0) return error.Io;
    }

    /// Asks the driver to deliver received bytes after 1 ms instead of the FTDI default 16.
    pub fn setLowLatency(self: *Port) LatencyError!void {
        return self.setLatency(1);
    }

    /// Sets the FTDI latency timer explicitly, so a measurement at 16 ms is really at 16 ms
    /// even after an earlier 1 ms setting (both settings outlive the open until replug).
    /// macOS: IOSSDATALAT in milliseconds (never 0). Linux: 1 sets ASYNC_LOW_LATENCY, which
    /// ftdi_sio maps to 1 ms; 16 clears it (the driver default); other values are
    /// Unsupported there. Unsupported when the device is not a serial driver that takes it.
    pub fn setLatency(self: *Port, ms: u8) LatencyError!void {
        if (ms == 0) return error.Unsupported;
        if (is_darwin) {
            var v: c_ulong = ms;
            if (darwin.ioctl(self.fd, darwin.IOSSDATALAT, &v) != 0) return unsupportedOrIo();
            return;
        }
        if (ms != 1 and ms != 16) return error.Unsupported;
        var ss: linux.struct_serial_struct = undefined;
        if (linux.ioctl(self.fd, linux.TIOCGSERIAL, &ss) != 0) return unsupportedOrIo();
        if (ms == 1) ss.flags |= linux.ASYNC_LOW_LATENCY else ss.flags &= ~linux.ASYNC_LOW_LATENCY;
        if (linux.ioctl(self.fd, linux.TIOCSSERIAL, &ss) != 0) return unsupportedOrIo();
    }

    /// Writes all of bytes, waiting for room when the driver's buffer is full.
    pub fn write(self: *Port, bytes: []const u8) IoError!void {
        var rest = bytes;
        while (rest.len > 0) {
            const n = std.c.write(self.fd, rest.ptr, rest.len);
            if (n > 0) {
                rest = rest[@intCast(n)..];
                continue;
            }
            if (n < 0) switch (libc.errno()) {
                .INTR => continue,
                .AGAIN => {
                    var fds = [_]std.c.pollfd{.{ .fd = self.fd, .events = std.c.POLL.OUT, .revents = 0 }};
                    const rc = std.c.poll(&fds, 1, 1000);
                    if (rc < 0 and libc.errno() == .INTR) continue;
                    if (rc != 1 or fds[0].revents & (std.c.POLL.ERR | std.c.POLL.HUP | std.c.POLL.NVAL) != 0) return error.Io;
                    continue;
                },
                else => return error.Io,
            };
            return error.Io;
        }
    }

    /// Drops anything received but not read, and anything written but not yet sent.
    pub fn purge(self: *Port) IoError!void {
        if (libc.tcflush(self.fd, sys.TCIOFLUSH) != 0) return error.Io;
    }

    /// Fills buf exactly. The first byte must arrive within timeout_ms and no later gap may exceed
    /// timeout_tel_end_ms (EdiabasLib ReceiveData semantics, EdInterfaceObd.cs:3381-3450).
    pub fn receive(self: *Port, buf: []u8, timeout_ms: u32, timeout_tel_end_ms: u32) ReceiveError!void {
        var got: usize = 0;
        var deadline = libc.monotonicNs() + @as(u64, timeout_ms) * std.time.ns_per_ms;
        while (got < buf.len) {
            const now = libc.monotonicNs();
            if (now >= deadline) return error.Timeout;
            const wait_ms: c_int = @intCast(@min((deadline - now + std.time.ns_per_ms - 1) / std.time.ns_per_ms, std.math.maxInt(c_int)));
            var fds = [_]std.c.pollfd{.{ .fd = self.fd, .events = std.c.POLL.IN, .revents = 0 }};
            const rc = std.c.poll(&fds, 1, wait_ms);
            if (rc < 0) {
                if (libc.errno() == .INTR) continue;
                return error.Io;
            }
            if (rc == 0) continue; // the deadline check above decides
            const ev = fds[0].revents;
            if (ev & std.c.POLL.IN == 0) {
                if (ev & (std.c.POLL.ERR | std.c.POLL.HUP | std.c.POLL.NVAL) != 0) return error.Io;
                continue;
            }
            const n = std.c.read(self.fd, buf[got..].ptr, buf.len - got);
            if (n > 0) {
                got += @intCast(n);
                deadline = libc.monotonicNs() + @as(u64, timeout_tel_end_ms) * std.time.ns_per_ms;
            } else if (n == 0) {
                // With VMIN=0/VTIME=0 a read can return 0 on a spurious wakeup (Linux), so only
                // a hang-up or error means the other end has gone; otherwise the deadline rules.
                if (ev & (std.c.POLL.ERR | std.c.POLL.HUP | std.c.POLL.NVAL) != 0) return error.Io;
            } else switch (libc.errno()) {
                .INTR, .AGAIN => {},
                else => return error.Io,
            }
        }
    }

    fn modemBits(self: *Port, bits: c_int, on: bool) IoError!void {
        var b = bits;
        if (sys.ioctl(self.fd, if (on) sys.TIOCMBIS else sys.TIOCMBIC, &b) != 0) return error.Io;
    }

    fn unsupportedOrIo() LatencyError {
        return switch (libc.errno()) {
            .NOTTY, .INVAL, .OPNOTSUPP => error.Unsupported,
            else => error.Io,
        };
    }

    const Termios = if (is_darwin) darwin.termios else linux.struct_termios2;
    const Tcflag = if (is_darwin) darwin.tcflag_t else linux.tcflag_t;

    fn getAttr(self: *Port, t: *Termios) LineError!void {
        const rc = if (is_darwin) darwin.tcgetattr(self.fd, t) else linux.ioctl(self.fd, linux.TCGETS2, t);
        if (rc != 0) return error.Io;
    }

    fn setAttr(self: *Port, t: *const Termios) LineError!void {
        const rc = if (is_darwin) darwin.tcsetattr(self.fd, darwin.TCSANOW, t) else linux.ioctl(self.fd, linux.TCSETS2, t);
        if (rc != 0) return error.Io;
    }

    fn makeRaw(self: *Port) LineError!void {
        var t: Termios = undefined;
        try self.getAttr(&t);
        if (is_darwin) {
            darwin.cfmakeraw(&t);
        } else {
            // cfmakeraw, on termios2.
            t.c_iflag &= ~(linux.IGNBRK | linux.BRKINT | linux.PARMRK | linux.ISTRIP | linux.INLCR | linux.IGNCR | linux.ICRNL | linux.IXON);
            t.c_oflag &= ~linux.OPOST;
            t.c_lflag &= ~(linux.ECHO | linux.ECHONL | linux.ICANON | linux.ISIG | linux.IEXTEN);
            t.c_cflag &= ~(linux.CSIZE | linux.PARENB);
            t.c_cflag |= linux.CS8;
        }
        t.c_iflag &= ~(sys.IXON | sys.IXOFF | sys.IXANY);
        t.c_cflag &= ~(sys.CSTOPB | sys.CRTSCTS);
        t.c_cflag |= sys.CLOCAL | sys.CREAD;
        t.c_cc[sys.VMIN] = 0;
        t.c_cc[sys.VTIME] = 0;
        try self.setAttr(&t);
    }

    fn drainIfParityChanges(self: *Port, t: *const Termios, parity: Tcflag) LineError!void {
        if (t.c_cflag & (sys.PARENB | sys.PARODD) != parity) {
            if (libc.tcdrain(self.fd) != 0) return error.Io;
        }
    }

    fn setLineDarwin(self: *Port, baud: u32, size: Tcflag, parity: Tcflag) LineError!void {
        var t: Termios = undefined;
        try self.getAttr(&t);
        try self.drainIfParityChanges(&t, parity);
        t.c_cflag &= ~(sys.CSIZE | sys.PARENB | sys.PARODD);
        t.c_cflag |= size | parity;
        if (isStandard(baud)) {
            if (darwin.cfsetspeed(&t, baud) != 0) return error.Unsupported;
            try self.setAttr(&t);
            return;
        }
        // Frame settings first, at whatever standard speed is current, then the custom rate.
        try self.setAttr(&t);
        var speed: darwin.speed_t = baud;
        if (darwin.ioctl(self.fd, darwin.IOSSIOSPEED, &speed) != 0) return switch (libc.errno()) {
            .NOTTY, .INVAL, .OPNOTSUPP => error.Unsupported,
            else => error.Io,
        };
    }

    fn setLineLinux(self: *Port, baud: u32, size: Tcflag, parity: Tcflag) LineError!void {
        var t: Termios = undefined;
        try self.getAttr(&t);
        try self.drainIfParityChanges(&t, parity);
        t.c_cflag &= ~(sys.CSIZE | sys.PARENB | sys.PARODD | linux.CBAUD);
        t.c_cflag |= size | parity | switch (baud) {
            9600 => linux.B9600,
            19200 => linux.B19200,
            38400 => linux.B38400,
            57600 => linux.B57600,
            115200 => linux.B115200,
            else => linux.BOTHER,
        };
        t.c_ispeed = baud;
        t.c_ospeed = baud;
        if (linux.ioctl(self.fd, linux.TCSETS2, &t) != 0) return switch (libc.errno()) {
            .NOTTY, .INVAL => error.Unsupported,
            else => error.Io,
        };
    }
};

fn isStandard(baud: u32) bool {
    return switch (baud) {
        9600, 19200, 38400, 57600, 115200 => true,
        else => false,
    };
}

const default_lock_dir = "/tmp";

extern "c" fn fchmod(fd: c_int, mode: std.c.mode_t) c_int;

/// <dir>/kline-<name>.lock, where name is the device's basename without macOS's "cu." or
/// "tty." prefix, so /dev/cu.X and /dev/tty.X (the same device) share one lock.
pub fn lockPath(buf: []u8, dir: []const u8, device: []const u8) error{NameTooLong}![:0]const u8 {
    var name = std.fs.path.basename(device);
    inline for (.{ "cu.", "tty." }) |prefix| {
        if (std.mem.startsWith(u8, name, prefix) and name.len > prefix.len) name = name[prefix.len..];
    }
    const trimmed = std.mem.trimEnd(u8, dir, "/");
    return std.fmt.bufPrintZ(buf, "{s}/kline-{s}.lock", .{ trimmed, name }) catch error.NameTooLong;
}

/// Test helper: creates the lock file Port.open would use for device, without locking it.
fn createLockFileFor(dir: []const u8, device: []const u8) !void {
    var buf: [std.fs.max_path_bytes]u8 = undefined;
    const p = try lockPath(&buf, dir, device);
    const fd = std.c.open(p, .{ .ACCMODE = .RDWR, .CREAT = true }, @as(std.c.mode_t, 0o600));
    if (fd < 0) return error.CreateFailed;
    _ = std.c.close(fd);
}

// A Linux pty forces CS8 and no parity whatever is set (the kernel's pty_set_termios), so on
// Linux only the baud rate can be read back from a pty; `kline probe` checks 8E1 on the cable.
const pty_keeps_framing = is_darwin;

test "open a pty, set 9600 8E1 and read it back" {
    var pty = try Pty.open();
    defer pty.close();
    var p = try Port.open(pty.slave_path, .{ .require_ftdi = false });
    defer p.close();
    try p.setLine(.{ .baud = 9600, .data_bits = 8, .parity = .even });
    if (pty_keeps_framing) {
        try std.testing.expectEqual(LineSettings{ .baud = 9600, .data_bits = 8, .parity = .even }, try p.getLine());
    } else {
        try std.testing.expectEqual(@as(u32, 9600), (try p.getLine()).baud);
    }
    try p.setLine(.{ .baud = 115200, .data_bits = 8, .parity = .none });
    try std.testing.expectEqual(@as(u32, 115200), (try p.getLine()).baud);
}

test "odd parity and 7 data bits read back" {
    if (!pty_keeps_framing) return error.SkipZigTest;
    var pty = try Pty.open();
    defer pty.close();
    var p = try Port.open(pty.slave_path, .{ .require_ftdi = false });
    defer p.close();
    try p.setLine(.{ .baud = 19200, .data_bits = 7, .parity = .odd });
    try std.testing.expectEqual(LineSettings{ .baud = 19200, .data_bits = 7, .parity = .odd }, try p.getLine());
}

test "data bits outside 5..8 are Unsupported" {
    var pty = try Pty.open();
    defer pty.close();
    var p = try Port.open(pty.slave_path, .{ .require_ftdi = false });
    defer p.close();
    try std.testing.expectError(error.Unsupported, p.setLine(.{ .baud = 9600, .data_bits = 9, .parity = .none }));
}

test "second open of the same device is Locked" { // Review Focus 4
    var pty = try Pty.open();
    defer pty.close();
    var a = try Port.open(pty.slave_path, .{ .require_ftdi = false });
    defer a.close();
    try std.testing.expectError(error.Locked, Port.open(pty.slave_path, .{ .require_ftdi = false }));
}

test "exclusive access refuses a second open even without the lock file" { // Review Focus 4
    // macOS ptys accept TIOCEXCL but do not enforce it (real serial devices do: EBUSY); Linux
    // ptys enforce it. On macOS this is checked on the cable by `kline probe`.
    if (is_darwin) return error.SkipZigTest;
    var pty = try Pty.open();
    defer pty.close();
    var d1 = try TmpDir.create();
    defer d1.cleanup();
    var d2 = try TmpDir.create();
    defer d2.cleanup();
    var a = try Port.open(pty.slave_path, .{ .require_ftdi = false, .lock_dir = d1.path });
    defer a.close();
    try std.testing.expectError(error.Locked, Port.open(pty.slave_path, .{ .require_ftdi = false, .lock_dir = d2.path }));
}

test "the device can be opened again after close" {
    var pty = try Pty.open();
    defer pty.close();
    var a = try Port.open(pty.slave_path, .{ .require_ftdi = false });
    a.close();
    var b = try Port.open(pty.slave_path, .{ .require_ftdi = false });
    b.close();
}

test "stale lock file not held by anyone does not block" { // Review Focus 5
    var pty = try Pty.open();
    defer pty.close();
    var tmp = try TmpDir.create();
    defer tmp.cleanup();
    // create the lock file the way Port.open names it, without holding flock
    try createLockFileFor(tmp.path, pty.slave_path);
    var p = try Port.open(pty.slave_path, .{ .require_ftdi = false, .lock_dir = tmp.path });
    p.close();
}

test "missing device is NoDevice" { // Review Focus 3
    try std.testing.expectError(error.NoDevice, Port.open("/dev/cu.usbserial-doesnotexist", .{}));
}

test "a pty is not FTDI when FTDI is required" {
    var pty = try Pty.open();
    defer pty.close();
    try std.testing.expectError(error.NotFtdi, Port.open(pty.slave_path, .{}));
}

test "lock names ignore the macOS cu./tty. prefix" {
    var a: [std.fs.max_path_bytes]u8 = undefined;
    var b: [std.fs.max_path_bytes]u8 = undefined;
    try std.testing.expectEqualStrings(
        try lockPath(&a, "/x", "/dev/cu.usbserial-A1"),
        try lockPath(&b, "/x", "/dev/tty.usbserial-A1"),
    );
    try std.testing.expectEqualStrings("/x/kline-ttyUSB0.lock", try lockPath(&a, "/x", "/dev/ttyUSB0"));
}

// ---- Task 6: modem lines, break, latency, timed receive ----

fn writeMaster(pty: *Pty, bytes: []const u8) !void {
    if (std.c.write(pty.master, bytes.ptr, bytes.len) != @as(isize, @intCast(bytes.len))) return error.WriteFailed;
}

fn readMaster(pty: *Pty, buf: []u8, timeout_ms: i32) !usize {
    var fds = [_]std.c.pollfd{.{ .fd = pty.master, .events = std.c.POLL.IN, .revents = 0 }};
    if (std.c.poll(&fds, 1, timeout_ms) != 1) return error.NothingToRead;
    const n = std.c.read(pty.master, buf.ptr, buf.len);
    if (n <= 0) return error.ReadFailed;
    return @intCast(n);
}

test "receive gets bytes written by the other end" {
    var pty = try Pty.open();
    defer pty.close();
    var p = try Port.open(pty.slave_path, .{ .require_ftdi = false });
    defer p.close();
    try writeMaster(&pty, "\x83\xf1\x01");
    var buf: [3]u8 = undefined;
    try p.receive(&buf, 100, 20);
    try std.testing.expectEqualSlices(u8, "\x83\xf1\x01", &buf);
}

test "receive times out when nothing arrives" {
    var pty = try Pty.open();
    defer pty.close();
    var p = try Port.open(pty.slave_path, .{ .require_ftdi = false });
    defer p.close();
    var buf: [1]u8 = undefined;
    const t0 = libc.monotonicNs();
    try std.testing.expectError(error.Timeout, p.receive(&buf, 50, 20));
    try std.testing.expect(libc.monotonicNs() - t0 >= 50 * std.time.ns_per_ms);
}

const GapWriter = struct {
    fn run(pty: *Pty, gap_ms: u64) void {
        writeMaster(pty, "\x01") catch return;
        libc.sleepNs(gap_ms * std.time.ns_per_ms);
        writeMaster(pty, "\x02") catch return;
    }
};

test "receive times out on a gap longer than timeout_tel_end" {
    var pty = try Pty.open();
    defer pty.close();
    var p = try Port.open(pty.slave_path, .{ .require_ftdi = false });
    defer p.close();
    const t = try std.Thread.spawn(.{}, GapWriter.run, .{ &pty, 60 });
    defer t.join();
    var buf: [2]u8 = undefined;
    try std.testing.expectError(error.Timeout, p.receive(&buf, 100, 20));
}

test "receive spans a gap shorter than timeout_tel_end" {
    var pty = try Pty.open();
    defer pty.close();
    var p = try Port.open(pty.slave_path, .{ .require_ftdi = false });
    defer p.close();
    const t = try std.Thread.spawn(.{}, GapWriter.run, .{ &pty, 5 });
    defer t.join();
    var buf: [2]u8 = undefined;
    try p.receive(&buf, 100, 50);
    try std.testing.expectEqualSlices(u8, "\x01\x02", &buf);
}

test "write reaches the other end" {
    var pty = try Pty.open();
    defer pty.close();
    var p = try Port.open(pty.slave_path, .{ .require_ftdi = false });
    defer p.close();
    try p.write("\x55");
    var buf: [4]u8 = undefined;
    const n = try readMaster(&pty, &buf, 200);
    try std.testing.expectEqualSlices(u8, "\x55", buf[0..n]);
}

test "purge drops unread input" {
    var pty = try Pty.open();
    defer pty.close();
    var p = try Port.open(pty.slave_path, .{ .require_ftdi = false });
    defer p.close();
    try writeMaster(&pty, "\xaa\xbb");
    libc.sleepNs(10 * std.time.ns_per_ms);
    try p.purge();
    var buf: [1]u8 = undefined;
    try std.testing.expectError(error.Timeout, p.receive(&buf, 30, 20));
}

test "receive after the other end has gone is Io, not a hang" { // Review Focus 3
    var pty = try Pty.open();
    var p = try Port.open(pty.slave_path, .{ .require_ftdi = false });
    defer p.close();
    pty.close();
    var buf: [1]u8 = undefined;
    try std.testing.expectError(error.Io, p.receive(&buf, 100, 20));
}

test "low latency on a pty is Unsupported, not a crash" {
    var pty = try Pty.open();
    defer pty.close();
    var p = try Port.open(pty.slave_path, .{ .require_ftdi = false });
    defer p.close();
    try std.testing.expectError(error.Unsupported, p.setLowLatency());
}

test "break on a pty returns without crashing" {
    var pty = try Pty.open();
    defer pty.close();
    var p = try Port.open(pty.slave_path, .{ .require_ftdi = false });
    defer p.close();
    p.setBreak(true) catch |e| try std.testing.expectEqual(error.Io, e);
    p.setBreak(false) catch |e| try std.testing.expectEqual(error.Io, e);
}

test "setting 16 ms latency on a pty is Unsupported, not a crash" { // review finding 1
    var pty = try Pty.open();
    defer pty.close();
    var p = try Port.open(pty.slave_path, .{ .require_ftdi = false });
    defer p.close();
    try std.testing.expectError(error.Unsupported, p.setLatency(16));
}

test "latency 0 is refused before reaching the driver" {
    var pty = try Pty.open();
    defer pty.close();
    var p = try Port.open(pty.slave_path, .{ .require_ftdi = false });
    defer p.close();
    try std.testing.expectError(error.Unsupported, p.setLatency(0));
}

extern "c" fn setenv(name: [*:0]const u8, value: [*:0]const u8, overwrite: c_int) c_int;
extern "c" fn unsetenv(name: [*:0]const u8) c_int;
extern "c" fn symlink(target: [*:0]const u8, link: [*:0]const u8) c_int;

test "the default lock does not depend on the environment" { // review finding 3
    var pty = try Pty.open();
    defer pty.close();
    var a = try Port.open(pty.slave_path, .{ .require_ftdi = false });
    defer a.close();
    var other = try TmpDir.create();
    defer other.cleanup();
    const saved: ?[*:0]u8 = std.c.getenv("TMPDIR");
    var saved_copy: [std.fs.max_path_bytes:0]u8 = undefined;
    if (saved) |v| _ = try std.fmt.bufPrintZ(&saved_copy, "{s}", .{std.mem.span(v)});
    defer {
        if (saved != null) _ = setenv("TMPDIR", &saved_copy, 1) else _ = unsetenv("TMPDIR");
    }
    _ = setenv("TMPDIR", other.path, 1);
    _ = setenv("XDG_RUNTIME_DIR", other.path, 1);
    defer _ = unsetenv("XDG_RUNTIME_DIR");
    try std.testing.expectError(error.Locked, Port.open(pty.slave_path, .{ .require_ftdi = false }));
}

test "a symlink to the device takes the same lock" { // review finding 4
    var pty = try Pty.open();
    defer pty.close();
    var tmp = try TmpDir.create();
    defer tmp.cleanup();
    var link_buf: [std.fs.max_path_bytes]u8 = undefined;
    const link = try std.fmt.bufPrintZ(&link_buf, "{s}/cable", .{tmp.path});
    if (symlink(pty.slave_path, link) != 0) return error.SymlinkFailed;
    var a = try Port.open(pty.slave_path, .{ .require_ftdi = false, .lock_dir = tmp.path });
    defer a.close();
    try std.testing.expectError(error.Locked, Port.open(link, .{ .require_ftdi = false, .lock_dir = tmp.path }));
}
