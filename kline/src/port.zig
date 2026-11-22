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
    /// Where the lock file goes. Default: $XDG_RUNTIME_DIR, else $TMPDIR, else /tmp.
    lock_dir: ?[]const u8 = null,
};
pub const OpenError = error{ NoDevice, Locked, NotFtdi, Permission, Io };
pub const LineError = error{ Unsupported, Io };

pub const Port = struct {
    fd: c_int,
    lock_fd: c_int,

    /// Opens a serial device for exclusive use: identity check, lock file, open, TIOCEXCL,
    /// raw mode. A missing device is NoDevice, a device another process holds is Locked.
    pub fn open(path: []const u8, opts: OpenOptions) OpenError!Port {
        var path_buf: [std.fs.max_path_bytes]u8 = undefined;
        const path_z = std.fmt.bufPrintZ(&path_buf, "{s}", .{path}) catch return error.NoDevice;

        // 1. Present at all? Checked first so a missing cable is never reported as NotFtdi.
        if (std.c.access(path_z, 0) != 0) return switch (libc.errno()) { // F_OK
            .NOENT, .NOTDIR, .NXIO, .NODEV => error.NoDevice,
            .ACCES, .PERM => error.Permission,
            else => error.Io,
        };

        // 2. The right cable.
        if (opts.require_ftdi) {
            const ids = identity.lookup(path) catch |e| switch (e) {
                error.NotUsb => return error.NotFtdi,
                error.Io => return error.Io,
            };
            if (ids.vid != identity.ftdi_ft232r.vid or ids.pid != identity.ftdi_ft232r.pid) return error.NotFtdi;
        }

        // 3. The lock file. flock is released by the kernel when its holder exits, so a file
        //    left behind by a crashed run never blocks.
        var lock_buf: [std.fs.max_path_bytes]u8 = undefined;
        const lock_path = lockPath(&lock_buf, opts.lock_dir orelse defaultLockDir(), path) catch return error.Io;
        const lock_fd = std.c.open(lock_path, .{ .ACCMODE = .RDWR, .CREAT = true, .CLOEXEC = true }, @as(std.c.mode_t, 0o600));
        if (lock_fd < 0) return switch (libc.errno()) {
            .ACCES, .PERM => error.Permission,
            else => error.Io,
        };
        errdefer _ = std.c.close(lock_fd);
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

    /// Releases the device, then the lock.
    pub fn close(self: *Port) void {
        if (self.fd >= 0) _ = std.c.close(self.fd);
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

fn defaultLockDir() []const u8 {
    inline for (.{ "XDG_RUNTIME_DIR", "TMPDIR" }) |name| {
        if (std.c.getenv(name)) |v| {
            const s = std.mem.span(v);
            if (s.len > 0) return s;
        }
    }
    return "/tmp";
}

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

test "open a pty, set 9600 8E1 and read it back" {
    var pty = try Pty.open();
    defer pty.close();
    var p = try Port.open(pty.slave_path, .{ .require_ftdi = false });
    defer p.close();
    try p.setLine(.{ .baud = 9600, .data_bits = 8, .parity = .even });
    try std.testing.expectEqual(LineSettings{ .baud = 9600, .data_bits = 8, .parity = .even }, try p.getLine());
    try p.setLine(.{ .baud = 115200, .data_bits = 8, .parity = .none });
    try std.testing.expectEqual(@as(u32, 115200), (try p.getLine()).baud);
}

test "odd parity and 7 data bits read back" {
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
