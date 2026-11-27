//! kline: desk bring-up for the K+DCAN cable. Never run against a car.

const std = @import("std");
const kline = @import("kline");
const Port = kline.Port;
const identity = kline.identity;
const libc = kline.sys.libc;
const builtin = @import("builtin");

pub const Command = union(enum) {
    list,
    probe: []const u8,
    loopback: struct { device: []const u8, latency_ms: u8, count: u32 },
};
pub const ParseError = error{ UnknownCommand, MissingDevice, BenchFlagRequired, BadNumber };

const usage =
    \\usage: kline list
    \\       kline probe <device>
    \\       kline loopback <device> --bench [--latency 1|16] [--count N]
    \\
    \\Desk bring-up only: never with the cable plugged into a car. loopback needs a bench
    \\12 V supply on OBD pin 16 (ground on pins 4/5), never a car.
    \\
;

/// args excludes the program name.
pub fn parseArgs(args: []const []const u8) ParseError!Command {
    if (args.len == 0) return error.UnknownCommand;
    const cmd = args[0];
    if (std.mem.eql(u8, cmd, "list")) {
        if (args.len != 1) return error.UnknownCommand;
        return .list;
    }
    if (std.mem.eql(u8, cmd, "probe")) {
        if (args.len < 2) return error.MissingDevice;
        if (args.len > 2) return error.UnknownCommand;
        return .{ .probe = args[1] };
    }
    if (std.mem.eql(u8, cmd, "loopback")) {
        if (args.len < 2 or std.mem.startsWith(u8, args[1], "--")) return error.MissingDevice;
        var bench = false;
        var latency_ms: u8 = 1;
        var count: u32 = 100;
        var i: usize = 2;
        while (i < args.len) : (i += 1) {
            const a = args[i];
            if (std.mem.eql(u8, a, "--bench")) {
                bench = true;
            } else if (std.mem.eql(u8, a, "--latency")) {
                i += 1;
                if (i >= args.len) return error.BadNumber;
                latency_ms = std.fmt.parseInt(u8, args[i], 10) catch return error.BadNumber;
                if (latency_ms != 1 and latency_ms != 16) return error.BadNumber;
            } else if (std.mem.eql(u8, a, "--count")) {
                i += 1;
                if (i >= args.len) return error.BadNumber;
                count = std.fmt.parseInt(u32, args[i], 10) catch return error.BadNumber;
                if (count == 0) return error.BadNumber;
            } else return error.UnknownCommand;
        }
        if (!bench) return error.BenchFlagRequired;
        return .{ .loopback = .{ .device = args[1], .latency_ms = latency_ms, .count = count } };
    }
    return error.UnknownCommand;
}

pub fn main(init: std.process.Init) !void {
    const arena = init.arena.allocator();
    const argv = try init.minimal.args.toSlice(arena);
    var buf: [4096]u8 = undefined;
    // Streaming, not positional: .init would pwrite at offset 0 when stdout is a regular file.
    var w: std.Io.File.Writer = .initStreaming(.stdout(), init.io, &buf);
    const out = &w.interface;

    const cmd = parseArgs(if (argv.len > 0) argv[1..] else argv) catch |e| {
        const reason = switch (e) {
            error.UnknownCommand => "unknown command or option",
            error.MissingDevice => "missing device path",
            error.BenchFlagRequired => "loopback needs --bench: it is for a bench 12 V supply only, never a car",
            error.BadNumber => "--latency takes 1 or 16, --count a positive number",
        };
        std.debug.print("kline: {s}\n{s}", .{ reason, usage });
        std.process.exit(2);
    };
    const code: u8 = switch (cmd) {
        .list => try list(arena, out),
        .probe => |dev| try probe(out, dev),
        .loopback => |l| try loopback(arena, out, l.device, l.latency_ms, l.count),
    };
    try out.flush();
    std.process.exit(code);
}

fn openReason(e: kline.port.OpenError) []const u8 {
    return switch (e) {
        error.NoDevice => "no such device (cable unplugged, or passed through to a VM?)",
        error.Locked => "in use by another process or tool",
        error.NotFtdi => "not an FTDI FT232R (0403:6001)",
        error.Permission => if (builtin.os.tag == .linux) "permission denied (install the udev rule in kline/README.md)" else "permission denied",
        error.Io => "i/o error",
    };
}

// ---- list ----

fn list(arena: std.mem.Allocator, out: *std.Io.Writer) !u8 {
    const prefix = if (builtin.os.tag == .macos) "cu.usbserial-" else "ttyUSB";
    var names: std.ArrayList([]const u8) = .empty;
    if (std.c.opendir("/dev")) |dir| {
        defer _ = std.c.closedir(dir);
        while (std.c.readdir(dir)) |ent| {
            const name = std.mem.sliceTo(@as([*:0]const u8, @ptrCast(&ent.name)), 0);
            if (std.mem.startsWith(u8, name, prefix)) try names.append(arena, try arena.dupe(u8, name));
        }
    }
    std.mem.sort([]const u8, names.items, {}, struct {
        fn lt(_: void, a: []const u8, b: []const u8) bool {
            return std.mem.lessThan(u8, a, b);
        }
    }.lt);
    for (names.items) |name| {
        const path = try std.fmt.allocPrint(arena, "/dev/{s}", .{name});
        if (identity.lookup(path)) |ids| {
            const ftdi = ids.vid == identity.ftdi_ft232r.vid and ids.pid == identity.ftdi_ft232r.pid;
            try out.print("{s}  {x:0>4}:{x:0>4}  {s}\n", .{ path, ids.vid, ids.pid, if (ftdi) "ftdi" else "other" });
        } else |_| {
            try out.print("{s}  ????:????  other\n", .{path});
        }
    }
    return 0;
}

// ---- probe ----

fn check(out: *std.Io.Writer, ok: bool, comptime fmt: []const u8, args: anytype) !void {
    try out.print("{s} ", .{if (ok) "ok  " else "FAIL"});
    try out.print(fmt ++ "\n", args);
}

fn probe(out: *std.Io.Writer, device: []const u8) !u8 {
    var essential_ok = true;

    // present at all: one line, no cascade of failures (Review Focus 3)
    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    const path_z = std.fmt.bufPrintZ(&path_buf, "{s}", .{device}) catch return 2;
    if (std.c.access(path_z, 0) != 0) {
        try check(out, false, "{s}: {s}", .{ device, openReason(error.NoDevice) });
        try out.flush();
        return 1;
    }

    // identity
    if (identity.lookup(device)) |ids| {
        const ok = ids.vid == identity.ftdi_ft232r.vid and ids.pid == identity.ftdi_ft232r.pid;
        essential_ok = essential_ok and ok;
        try check(out, ok, "identity {x:0>4}:{x:0>4}", .{ ids.vid, ids.pid });
    } else |e| {
        essential_ok = false;
        try check(out, false, "identity ({s})", .{if (e == error.NotUsb) "not a USB serial device" else "i/o error"});
    }

    // lock: open without the FTDI check so the remaining checks still run on another chip.
    var p = Port.open(device, .{ .require_ftdi = false }) catch |e| {
        try check(out, false, "lock: {s}", .{openReason(e)});
        try out.flush();
        return 1;
    };
    defer p.close();
    {
        // A second open must be refused twice over: by the lock file, and (with a different
        // lock directory) by TIOCEXCL on the device itself.
        const by_flock = if (Port.open(device, .{ .require_ftdi = false })) |q| blk: {
            var qq = q;
            qq.close();
            break :blk false;
        } else |e| e == error.Locked;
        var tmpl = "/tmp/kline-probe-XXXXXX".*;
        const by_excl = if (libc.mkdtemp(&tmpl)) |dir| blk: {
            const d = std.mem.span(dir);
            defer _ = std.c.rmdir(dir);
            defer {
                var lb: [std.fs.max_path_bytes]u8 = undefined;
                if (kline.port.lockPath(&lb, d, device)) |lp| _ = std.c.unlink(lp) else |_| {}
            }
            break :blk if (Port.open(device, .{ .require_ftdi = false, .lock_dir = d })) |q| inner: {
                var qq = q;
                qq.close();
                break :inner false;
            } else |e| e == error.Locked;
        } else false;
        const ok = by_flock and by_excl;
        essential_ok = essential_ok and ok;
        try check(out, ok, "lock (second open refused: lock file {s}, exclusive {s})", .{ yesNo(by_flock), yesNo(by_excl) });
    }

    // line settings, each read back
    const lines = [_]struct { s: kline.port.LineSettings, label: []const u8 }{
        .{ .s = .{ .baud = 9600, .data_bits = 8, .parity = .even }, .label = "9600 8E1" },
        .{ .s = .{ .baud = 115200, .data_bits = 8, .parity = .none }, .label = "115200 8N1" },
        .{ .s = .{ .baud = 10400, .data_bits = 8, .parity = .none }, .label = "10400 8N1" },
    };
    for (lines) |l| {
        p.setLine(l.s) catch |e| {
            essential_ok = false;
            try check(out, false, "line {s}: {s}", .{ l.label, if (e == error.Unsupported) "unsupported" else "i/o error" });
            continue;
        };
        if (p.getLine()) |got| {
            const ok = got.baud == l.s.baud and got.data_bits == l.s.data_bits and got.parity == l.s.parity;
            essential_ok = essential_ok and ok;
            try check(out, ok, "line {s} (read back {d} {d}{c}1)", .{ l.label, got.baud, got.data_bits, parityChar(got.parity) });
        } else |_| {
            essential_ok = false;
            try check(out, false, "line {s}: read-back failed", .{l.label});
        }
    }

    // latency
    if (p.setLowLatency()) |_| {
        try check(out, true, "latency 1ms", .{});
    } else |e| {
        try check(out, false, "latency 1ms: {s}", .{if (e == error.Unsupported) "unsupported" else "i/o error"});
    }

    // modem lines
    const dtr_ok = if (p.setDtr(true)) |_| (if (p.setDtr(false)) |_| true else |_| false) else |_| false;
    try check(out, dtr_ok, "dtr on/off", .{});
    if (p.getDsr()) |dsr| {
        try check(out, true, "dsr {s}", .{if (dsr) "on" else "off"});
    } else |_| try check(out, false, "dsr: i/o error", .{});

    // break, held 25 ms on the monotonic clock
    if (p.setBreak(true)) |_| {
        const t0 = libc.monotonicNs();
        libc.sleepNs(25 * std.time.ns_per_ms);
        const cleared = if (p.setBreak(false)) |_| true else |_| false;
        const held_us = (libc.monotonicNs() - t0) / std.time.ns_per_us;
        try check(out, cleared, "break 25ms (held {d}.{d:0>3} ms by the host clock)", .{ held_us / 1000, held_us % 1000 });
    } else |_| try check(out, false, "break 25ms: i/o error", .{});

    try out.flush();
    return if (essential_ok) 0 else 1;
}

fn yesNo(b: bool) []const u8 {
    return if (b) "yes" else "NO";
}

fn parityChar(p: kline.port.Parity) u8 {
    return switch (p) {
        .none => 'N',
        .even => 'E',
        .odd => 'O',
    };
}

// ---- loopback (bench supply only) ----

fn loopback(arena: std.mem.Allocator, out: *std.Io.Writer, device: []const u8, latency_ms: u8, count: u32) !u8 {
    var p = Port.open(device, .{}) catch |e| {
        std.debug.print("kline: {s}: {s}\n", .{ device, openReason(e) });
        return 1;
    };
    defer p.close();
    try p.setLine(.{ .baud = 115200, .data_bits = 8, .parity = .none });
    // Set explicitly either way: a 1 ms setting from probe or an earlier run lasts until replug.
    p.setLatency(latency_ms) catch |e| {
        std.debug.print("kline: latency {d}ms: {s}\n", .{ latency_ms, if (e == error.Unsupported) "unsupported" else "i/o error" });
        return 1;
    };
    try p.purge();

    const rtt_us = try arena.alloc(u64, count);
    var got: usize = 0;
    var timeouts: u32 = 0;
    var wrong: u32 = 0;
    for (0..count) |_| {
        const t0 = libc.monotonicNs();
        try p.write("U");
        var echo: [1]u8 = undefined;
        p.receive(&echo, 500, 20) catch |e| switch (e) {
            error.Timeout => {
                timeouts += 1;
                continue;
            },
            error.Io => return e,
        };
        if (echo[0] != 0x55) wrong += 1;
        rtt_us[got] = (libc.monotonicNs() - t0) / std.time.ns_per_us;
        got += 1;
        libc.sleepNs(2 * std.time.ns_per_ms);
    }
    const r = rtt_us[0..got];
    std.mem.sort(u64, r, {}, std.sort.asc(u64));
    try out.print("loopback {s} latency {d}ms: {d}/{d} echoes, {d} timeouts, {d} wrong\n", .{ device, latency_ms, got, count, timeouts, wrong });
    if (got > 0) {
        try out.print("round trip ms: min {d}.{d:0>3}  median {d}.{d:0>3}  max {d}.{d:0>3}\n", .{
            r[0] / 1000,         r[0] % 1000,
            r[got / 2] / 1000,   r[got / 2] % 1000,
            r[got - 1] / 1000,   r[got - 1] % 1000,
        });
    }
    return if (got == count and wrong == 0) 0 else 1;
}

test "loopback without --bench is refused" {
    try std.testing.expectError(error.BenchFlagRequired, parseArgs(&.{ "loopback", "/dev/x", "--latency", "1", "--count", "10" }));
}

test "probe needs a device" {
    try std.testing.expectError(error.MissingDevice, parseArgs(&.{"probe"}));
}

test "unknown command" {
    try std.testing.expectError(error.UnknownCommand, parseArgs(&.{"flash"}));
}

test "no command is unknown" {
    try std.testing.expectError(error.UnknownCommand, parseArgs(&.{}));
}

test "loopback parses" {
    const c = try parseArgs(&.{ "loopback", "/dev/x", "--bench", "--latency", "16", "--count", "200" });
    try std.testing.expectEqualStrings("/dev/x", c.loopback.device);
    try std.testing.expectEqual(@as(u8, 16), c.loopback.latency_ms);
    try std.testing.expectEqual(@as(u32, 200), c.loopback.count);
}

test "loopback latency is 1 or 16 only" {
    try std.testing.expectError(error.BadNumber, parseArgs(&.{ "loopback", "/dev/x", "--bench", "--latency", "0", "--count", "5" }));
    try std.testing.expectError(error.BadNumber, parseArgs(&.{ "loopback", "/dev/x", "--bench", "--latency", "x", "--count", "5" }));
    try std.testing.expectError(error.BadNumber, parseArgs(&.{ "loopback", "/dev/x", "--bench", "--count", "0" }));
}

test "probe and list parse" {
    try std.testing.expectEqualStrings("/dev/x", (try parseArgs(&.{ "probe", "/dev/x" })).probe);
    try std.testing.expect((try parseArgs(&.{"list"})) == .list);
}
