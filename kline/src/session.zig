//! A session on the cable: a real Port or a replayed recording, with the declared concept, the
//! frame gate and its latch, inter-byte pacing, transmit cancel, the last error, and the
//! recorder. This is what the C ABI (capi.zig) exposes, one handle per session.

const std = @import("std");
const testing = std.testing;
const libc = @import("sys/libc.zig");
const pty_mod = @import("testing/pty.zig");
const Pty = pty_mod.Pty;
const TmpDir = pty_mod.TmpDir;
const port_mod = @import("port.zig");
const Port = port_mod.Port;
const LineSettings = port_mod.LineSettings;
const gate = @import("gate.zig");
const recording = @import("recording.zig");

/// Why a call failed. The numbers are the C ABI's (kline.h).
pub const Reason = enum(c_int) {
    ok = 0,
    no_device = 1,
    locked = 2,
    not_ftdi = 3,
    permission = 4,
    gate_refused = 5,
    io = 6,
    timeout = 7,
    cancelled = 8,
    replay_mismatch = 9,
    unsupported = 10,
};

pub const OpenError = port_mod.OpenError || error{OutOfMemory};

pub fn reasonOf(err: anyerror) Reason {
    return switch (err) {
        error.NoDevice => .no_device,
        error.Locked => .locked,
        error.NotFtdi => .not_ftdi,
        error.Permission => .permission,
        error.Unsupported => .unsupported,
        error.Timeout => .timeout,
        error.Cancelled => .cancelled,
        else => .io,
    };
}

/// A recording opened in place of a device. Each recorded send must be matched in order by
/// the session's sends; the bytes recorded as received after it are what receive returns.
/// Anything else (line settings, modem lines, break, purge) is accepted and does nothing.
const Replay = struct {
    rec: recording.Recording,
    /// Index into rec.events of the next event to consider.
    next: usize = 0,
    /// Received bytes queued by the last matched send.
    queue: std.ArrayList(u8) = .empty,
    pos: usize = 0,
    broken: bool = false,

    fn send(self: *Replay, alloc: std.mem.Allocator, bytes: []const u8) Reason {
        if (self.broken) return .replay_mismatch;
        const evs = self.rec.events;
        while (self.next < evs.len and evs[self.next].op != .send) self.next += 1;
        if (self.next >= evs.len or !std.mem.eql(u8, evs[self.next].bytes, bytes)) {
            self.broken = true;
            return .replay_mismatch;
        }
        self.next += 1;
        self.queue.clearRetainingCapacity();
        self.pos = 0;
        while (self.next < evs.len and evs[self.next].op != .send) : (self.next += 1) {
            if (evs[self.next].op == .recv) self.queue.appendSlice(alloc, evs[self.next].bytes) catch return .io;
        }
        return .ok;
    }

    fn receive(self: *Replay, buf: []u8) Reason {
        const left = self.queue.items.len - self.pos;
        if (left < buf.len) return .timeout;
        @memcpy(buf, self.queue.items[self.pos..][0..buf.len]);
        self.pos += buf.len;
        return .ok;
    }
};

pub const Session = struct {
    alloc: std.mem.Allocator,
    device: union(enum) { port: Port, replay: Replay },
    concept: ?u32 = null,
    latched: bool = false,
    last: Reason = .ok,
    interbyte_ms: u32 = 0,
    line: LineSettings = .{ .baud = 9600, .data_bits = 8, .parity = .none },
    cancelled: std.atomic.Value(bool) = .init(false),
    rec: ?recording.Writer = null,

    /// Opens the cable (FTDI 0403:6001 only) with its lock, exclusive access and 1 ms latency.
    pub fn openDevice(alloc: std.mem.Allocator, path: []const u8, record_dir: ?[]const u8, lock_dir: ?[]const u8) OpenError!*Session {
        return openDeviceOpts(alloc, path, record_dir, lock_dir, true);
    }

    /// openDevice, with the FTDI check optional (tests open ptys).
    pub fn openDeviceOpts(alloc: std.mem.Allocator, path: []const u8, record_dir: ?[]const u8, lock_dir: ?[]const u8, require_ftdi: bool) OpenError!*Session {
        var p = try Port.open(path, .{ .require_ftdi = require_ftdi, .lock_dir = lock_dir });
        errdefer p.close();
        const latency_ok = if (p.setLowLatency()) |_| true else |_| false;
        const s = try alloc.create(Session);
        s.* = .{ .alloc = alloc, .device = .{ .port = p } };
        if (record_dir) |d| {
            if (recording.Writer.create(d)) |w| s.rec = w else |_| {}
        }
        if (s.rec) |*w| {
            var buf: [std.fs.max_path_bytes * 2]u8 = undefined;
            const latency = if (latency_ok) "1ms" else "unsupported";
            w.event(.open, ",\"path\":{s},\"latency\":\"{s}\"", .{ recording.jsonString(&buf, path), latency });
        }
        return s;
    }

    /// Opens a recording in place of the cable. A missing file is NoDevice.
    pub fn openReplay(alloc: std.mem.Allocator, recording_path: []const u8) (recording.LoadError)!*Session {
        var r = try recording.load(alloc, recording_path);
        errdefer r.deinit();
        const s = try alloc.create(Session);
        s.* = .{ .alloc = alloc, .device = .{ .replay = .{ .rec = r } } };
        return s;
    }

    pub fn close(self: *Session) void {
        switch (self.device) {
            .port => |*p| p.close(),
            .replay => |*r| {
                r.queue.deinit(self.alloc);
                r.rec.deinit();
            },
        }
        if (self.rec) |*w| {
            w.event(.close, "", .{});
            w.close();
        }
        self.alloc.destroy(self);
    }

    /// The last failure's reason; gate_refused from the first refusal on, whatever followed.
    pub fn lastError(self: *const Session) Reason {
        return if (self.latched) .gate_refused else self.last;
    }

    fn done(self: *Session, r: Reason) Reason {
        if (r != .ok) self.last = r;
        return r;
    }

    pub fn setLine(self: *Session, s: LineSettings) Reason {
        switch (self.device) {
            .port => |*p| p.setLine(s) catch |e| return self.done(reasonOf(e)),
            .replay => {},
        }
        self.line = s;
        if (self.rec) |*w| w.event(.line, ",\"baud\":{d},\"bits\":{d},\"parity\":\"{s}\"", .{ s.baud, s.data_bits, @tagName(s.parity) });
        return .ok;
    }

    pub fn setDtr(self: *Session, on: bool) Reason {
        switch (self.device) {
            .port => |*p| p.setDtr(on) catch |e| return self.done(reasonOf(e)),
            .replay => {},
        }
        if (self.rec) |*w| w.event(.dtr, ",\"on\":{}", .{on});
        return .ok;
    }

    pub fn setRts(self: *Session, on: bool) Reason {
        switch (self.device) {
            .port => |*p| p.setRts(on) catch |e| return self.done(reasonOf(e)),
            .replay => {},
        }
        if (self.rec) |*w| w.event(.rts, ",\"on\":{}", .{on});
        return .ok;
    }

    pub fn getDsr(self: *Session, out: *bool) Reason {
        out.* = switch (self.device) {
            .port => |*p| p.getDsr() catch |e| return self.done(reasonOf(e)),
            .replay => false,
        };
        return .ok;
    }

    pub fn setBreak(self: *Session, on: bool) Reason {
        switch (self.device) {
            .port => |*p| p.setBreak(on) catch |e| return self.done(reasonOf(e)),
            .replay => {},
        }
        if (self.rec) |*w| w.event(.@"break", ",\"on\":{}", .{on});
        return .ok;
    }

    /// The SGBD's communication parameters; params[0] is the concept. An empty array clears it.
    pub fn setConcept(self: *Session, params: []const u32) Reason {
        self.concept = if (params.len > 0) params[0] else null;
        if (self.rec) |*w| {
            var buf: [1024]u8 = undefined;
            var n: usize = 0;
            for (params, 0..) |v, i| {
                const part = std.fmt.bufPrint(buf[n..], "{s}{d}", .{ if (i == 0) "" else ",", v }) catch break;
                n += part.len;
            }
            w.event(.concept, ",\"params\":[{s}]", .{buf[0..n]});
        }
        return .ok;
    }

    pub fn setInterByte(self: *Session, ms: u32) Reason {
        self.interbyte_ms = ms;
        if (self.rec) |*w| w.event(.interbyte, ",\"ms\":{d}", .{ms});
        return .ok;
    }

    /// Gate first (a refusal latches until close), then the line: paced byte by byte when an
    /// inter-byte time is set, otherwise written whole. The cable's echo is the caller's.
    pub fn send(self: *Session, bytes: []const u8) Reason {
        if (self.latched) return self.done(.gate_refused);
        if (gate.check(self.concept, bytes)) |refusal| {
            self.latched = true;
            if (self.rec) |*w| {
                var buf: [64]u8 = undefined;
                const extra = std.fmt.bufPrint(&buf, ",\"reason\":\"{s}\"", .{@tagName(refusal)}) catch "";
                w.bytesEvent(.refused, bytes, extra);
            }
            return self.done(.gate_refused);
        }
        switch (self.device) {
            .replay => |*r| {
                const res = r.send(self.alloc, bytes);
                if (res != .ok) return self.done(res);
            },
            .port => |*p| {
                const res = self.writePaced(p, bytes);
                if (res != .ok) return self.done(res);
            },
        }
        if (self.rec) |*w| w.bytesEvent(.send, bytes, "");
        return .ok;
    }

    fn writePaced(self: *Session, p: *Port, bytes: []const u8) Reason {
        if (self.interbyte_ms == 0) {
            p.write(bytes) catch |e| return reasonOf(e);
            return .ok;
        }
        // As EdiabasLib paces it (EdInterfaceObd.cs:3256-3297): each byte starts one byte time
        // plus the inter-byte time after the previous one.
        const bits: u64 = if (self.line.parity == .none) @as(u64, self.line.data_bits) + 2 else @as(u64, self.line.data_bits) + 3;
        const byte_ns = bits * std.time.ns_per_s / @max(self.line.baud, 1);
        const step_ns = byte_ns + @as(u64, self.interbyte_ms) * std.time.ns_per_ms;
        const start = libc.monotonicNs();
        for (bytes, 0..) |b, i| {
            if (self.cancelled.load(.acquire)) return .cancelled;
            const due = start + step_ns * i;
            const now = libc.monotonicNs();
            if (due > now) libc.sleepNs(due - now);
            p.write(&.{b}) catch |e| return reasonOf(e);
        }
        return .ok;
    }

    /// Fills buf exactly: the first byte within timeout_ms, no gap longer than tel_end_ms.
    /// A receive that fails after some bytes arrived (a short read, or EdiabasLib's drain that
    /// always ends in a timeout) still records those bytes, with the reason.
    pub fn receive(self: *Session, buf: []u8, timeout_ms: u32, tel_end_ms: u32) Reason {
        if (self.cancelled.load(.acquire)) return self.done(.cancelled);
        var got: usize = buf.len;
        var res: Reason = .ok;
        switch (self.device) {
            .replay => |*r| res = r.receive(buf),
            .port => |*p| p.receiveCancellable(buf, timeout_ms, tel_end_ms, &self.cancelled, &got) catch |e| {
                res = reasonOf(e);
            },
        }
        if (res != .ok and self.device == .replay) got = 0;
        if (self.rec) |*w| {
            if (res == .ok) {
                w.bytesEvent(.recv, buf, "");
            } else if (got > 0) {
                var extra: [48]u8 = undefined;
                w.bytesEvent(.recv, buf[0..got], std.fmt.bufPrint(&extra, ",\"reason\":\"{s}\"", .{@tagName(res)}) catch "");
            }
        }
        return self.done(res);
    }

    pub fn purge(self: *Session) Reason {
        switch (self.device) {
            .port => |*p| p.purge() catch |e| return self.done(reasonOf(e)),
            .replay => {},
        }
        if (self.rec) |*w| w.event(.purge, "", .{});
        return .ok;
    }

    /// Thread-safe. While set, send and receive return cancelled promptly.
    pub fn cancel(self: *Session, on: bool) void {
        self.cancelled.store(on, .release);
    }
};

// ---------------------------------------------------------------- tests

const bmw_fast_params = [_]u32{ 0x010F, 115200, 800, 20, 10, 2, 5000 };
const ident = [_]u8{ 0x82, 0x12, 0xF1, 0x1A, 0x80, 0x9F };
const clear_faults = [_]u8{ 0x83, 0x12, 0xF1, 0x14, 0xFF, 0xFF };

fn openPty(pty: *Pty, lock: *TmpDir, record_dir: ?[]const u8) !*Session {
    return Session.openDeviceOpts(testing.allocator, pty.slave_path, record_dir, lock.path, false);
}

/// Reads up to n bytes from the pty master within timeout_ms.
fn readMaster(fd: c_int, buf: []u8, timeout_ms: u32) usize {
    var got: usize = 0;
    const deadline = libc.monotonicNs() + @as(u64, timeout_ms) * std.time.ns_per_ms;
    while (got < buf.len) {
        const now = libc.monotonicNs();
        if (now >= deadline) break;
        var fds = [_]std.c.pollfd{.{ .fd = fd, .events = std.c.POLL.IN, .revents = 0 }};
        if (std.c.poll(&fds, 1, 10) <= 0) continue;
        const n = std.c.read(fd, buf[got..].ptr, buf.len - got);
        if (n > 0) got += @intCast(n);
    }
    return got;
}

fn writeFile(dir: []const u8, name: []const u8, content: []const u8) ![:0]u8 {
    const path = try std.fmt.allocPrintSentinel(testing.allocator, "{s}/{s}", .{ dir, name }, 0);
    errdefer testing.allocator.free(path);
    const fd = std.c.open(path, .{ .ACCMODE = .WRONLY, .CREAT = true, .TRUNC = true }, @as(std.c.mode_t, 0o600));
    if (fd < 0) return error.CreateFailed;
    defer _ = std.c.close(fd);
    if (std.c.write(fd, content.ptr, content.len) != @as(isize, @intCast(content.len))) return error.WriteFailed;
    return path;
}

test "send refused before any concept, and the refusal latches" {
    var pty = try Pty.open();
    defer pty.close();
    var lock = try TmpDir.create();
    defer lock.cleanup();
    const s = try openPty(&pty, &lock, null);
    defer s.close();
    try testing.expectEqual(Reason.gate_refused, s.send(&ident));
    try testing.expectEqual(Reason.ok, s.setConcept(&bmw_fast_params));
    try testing.expectEqual(Reason.gate_refused, s.send(&ident));
    try testing.expectEqual(Reason.gate_refused, s.lastError());
    var buf: [16]u8 = undefined;
    try testing.expectEqual(@as(usize, 0), readMaster(pty.master, &buf, 50));
}

test "allowed send reaches the line whole" {
    var pty = try Pty.open();
    defer pty.close();
    var lock = try TmpDir.create();
    defer lock.cleanup();
    const s = try openPty(&pty, &lock, null);
    defer s.close();
    try testing.expectEqual(Reason.ok, s.setConcept(&bmw_fast_params));
    try testing.expectEqual(Reason.ok, s.send(&ident));
    var buf: [16]u8 = undefined;
    const n = readMaster(pty.master, &buf, 500);
    try testing.expectEqualSlices(u8, &ident, buf[0..n]);
    try testing.expectEqual(Reason.ok, s.lastError());
}

const Arrivals = struct {
    fd: c_int,
    times: [8]u64 = undefined,
    count: usize = 0,

    fn run(self: *Arrivals, want: usize) void {
        const deadline = libc.monotonicNs() + 2 * std.time.ns_per_s;
        while (self.count < want and libc.monotonicNs() < deadline) {
            var fds = [_]std.c.pollfd{.{ .fd = self.fd, .events = std.c.POLL.IN, .revents = 0 }};
            if (std.c.poll(&fds, 1, 5) <= 0) continue;
            var b: [8]u8 = undefined;
            const n = std.c.read(self.fd, &b, b.len);
            if (n <= 0) continue;
            const now = libc.monotonicNs();
            var i: usize = 0;
            while (i < @as(usize, @intCast(n)) and self.count < self.times.len) : (i += 1) {
                self.times[self.count] = now;
                self.count += 1;
            }
        }
    }
};

test "inter-byte pacing" {
    var pty = try Pty.open();
    defer pty.close();
    var lock = try TmpDir.create();
    defer lock.cleanup();
    const s = try openPty(&pty, &lock, null);
    defer s.close();
    try testing.expectEqual(Reason.ok, s.setConcept(&.{ 0x0006, 9600 }));
    try testing.expectEqual(Reason.ok, s.setInterByte(5));
    var arrivals = Arrivals{ .fd = pty.master };
    const t = try std.Thread.spawn(.{}, Arrivals.run, .{ &arrivals, 4 });
    try testing.expectEqual(Reason.ok, s.send(&.{ 0x12, 0x04, 0x00, 0x16 }));
    t.join();
    try testing.expectEqual(@as(usize, 4), arrivals.count);
    const spread_ms = (arrivals.times[3] - arrivals.times[0]) / std.time.ns_per_ms;
    try testing.expect(spread_ms >= 3 * 5 - 1);
}

fn cancelAfter(s: *Session, ms: u64) void {
    libc.sleepNs(ms * std.time.ns_per_ms);
    s.cancel(true);
}

test "cancel interrupts a blocked receive" {
    var pty = try Pty.open();
    defer pty.close();
    var lock = try TmpDir.create();
    defer lock.cleanup();
    const s = try openPty(&pty, &lock, null);
    defer s.close();
    const t = try std.Thread.spawn(.{}, cancelAfter, .{ s, 50 });
    const start = libc.monotonicNs();
    var buf: [4]u8 = undefined;
    const r = s.receive(&buf, 5000, 100);
    t.join();
    try testing.expectEqual(Reason.cancelled, r);
    try testing.expect(libc.monotonicNs() - start < 200 * std.time.ns_per_ms);
    // Cleared by the caller; then the port works again.
    s.cancel(false);
    try testing.expectEqual(Reason.timeout, s.receive(&buf, 20, 20));
}

test "recorder writes every event" {
    var pty = try Pty.open();
    defer pty.close();
    var lock = try TmpDir.create();
    defer lock.cleanup();
    var rec = try TmpDir.create();
    defer rec.cleanup();
    {
        const s = try openPty(&pty, &lock, rec.path);
        defer s.close();
        try testing.expectEqual(Reason.ok, s.setLine(.{ .baud = 115200, .data_bits = 8, .parity = .none }));
        try testing.expectEqual(Reason.ok, s.setConcept(&bmw_fast_params));
        try testing.expectEqual(Reason.ok, s.send(&ident));
        var echo: [6]u8 = undefined;
        _ = readMaster(pty.master, &echo, 500);
        const reply = [_]u8{ 0x82, 0xF1, 0x12, 0x5A, 0x80, 0x5F };
        _ = std.c.write(pty.master, &reply, reply.len);
        var got: [6]u8 = undefined;
        try testing.expectEqual(Reason.ok, s.receive(&got, 500, 50));
    }
    const path = (try onlyFile(rec.path)).?;
    defer testing.allocator.free(path);
    var r = try recording.load(testing.allocator, path);
    defer r.deinit();
    var ops: std.ArrayList(u8) = .empty;
    defer ops.deinit(testing.allocator);
    for (r.events) |e| {
        if (ops.items.len > 0) try ops.append(testing.allocator, ',');
        try ops.appendSlice(testing.allocator, @tagName(e.op));
    }
    try testing.expectEqualStrings("open,line,concept,send,recv,close", ops.items);
    try testing.expectEqualSlices(u8, &ident, r.events[3].bytes);
    try testing.expectEqualSlices(u8, &.{ 0x82, 0xF1, 0x12, 0x5A, 0x80, 0x5F }, r.events[4].bytes);
}

test "recorder keeps the bytes of a receive that times out" {
    var pty = try Pty.open();
    defer pty.close();
    var lock = try TmpDir.create();
    defer lock.cleanup();
    var rec = try TmpDir.create();
    defer rec.cleanup();
    {
        const s = try openPty(&pty, &lock, rec.path);
        defer s.close();
        const partial = [_]u8{ 0x82, 0xF1 };
        _ = std.c.write(pty.master, &partial, partial.len);
        var buf: [4]u8 = undefined;
        try testing.expectEqual(Reason.timeout, s.receive(&buf, 200, 30));
    }
    const path = (try onlyFile(rec.path)).?;
    defer testing.allocator.free(path);
    var r = try recording.load(testing.allocator, path);
    defer r.deinit();
    var found = false;
    for (r.events) |e| {
        if (e.op == .recv) {
            try testing.expectEqualSlices(u8, &.{ 0x82, 0xF1 }, e.bytes);
            found = true;
        }
    }
    try testing.expect(found);
    // The reason is in the line, for M2's count of short reads.
    var buf: [4096]u8 = undefined;
    const fd = std.c.open(path, .{ .ACCMODE = .RDONLY });
    defer _ = std.c.close(fd);
    const n = std.c.read(fd, &buf, buf.len);
    try testing.expect(std.mem.indexOf(u8, buf[0..@intCast(n)], "\"reason\":\"timeout\"") != null);
}

test "recorder writes nothing for a receive that got no bytes" {
    var pty = try Pty.open();
    defer pty.close();
    var lock = try TmpDir.create();
    defer lock.cleanup();
    var rec = try TmpDir.create();
    defer rec.cleanup();
    {
        const s = try openPty(&pty, &lock, rec.path);
        defer s.close();
        var buf: [4]u8 = undefined;
        try testing.expectEqual(Reason.timeout, s.receive(&buf, 20, 20));
    }
    const path = (try onlyFile(rec.path)).?;
    defer testing.allocator.free(path);
    var r = try recording.load(testing.allocator, path);
    defer r.deinit();
    for (r.events) |e| try testing.expect(e.op != .recv);
}

fn onlyFile(dir: [:0]const u8) !?[:0]u8 {
    const d = std.c.opendir(dir) orelse return null;
    defer _ = std.c.closedir(d);
    while (std.c.readdir(d)) |ent| {
        const name = std.mem.sliceTo(@as([*:0]const u8, @ptrCast(&ent.name)), 0);
        if (std.mem.startsWith(u8, name, "kline-") and std.mem.endsWith(u8, name, ".jsonl")) {
            return try std.fmt.allocPrintSentinel(testing.allocator, "{s}/{s}", .{ dir, name }, 0);
        }
    }
    return null;
}

const exchange =
    \\{"t_us":0,"op":"open","path":"/dev/cu.usbserial-X"}
    \\{"t_us":10,"op":"concept","params":[271,115200,800,20,10,2,5000]}
    \\{"t_us":20,"op":"send","hex":"8212F11A809F"}
    \\{"t_us":30,"op":"recv","hex":"8212F11A809F"}
    \\{"t_us":40,"op":"recv","hex":"82F1125A805F"}
    \\{"t_us":50,"op":"close"}
    \\
;

test "replay answers from the recording" {
    var dir = try TmpDir.create();
    defer dir.cleanup();
    const path = try writeFile(dir.path, "r.jsonl", exchange);
    defer testing.allocator.free(path);
    const s = try Session.openReplay(testing.allocator, path);
    defer s.close();
    try testing.expectEqual(Reason.ok, s.setConcept(&bmw_fast_params));
    try testing.expectEqual(Reason.ok, s.send(&ident));
    var a: [3]u8 = undefined;
    try testing.expectEqual(Reason.ok, s.receive(&a, 100, 50));
    try testing.expectEqualSlices(u8, &.{ 0x82, 0x12, 0xF1 }, &a);
    var b: [9]u8 = undefined;
    try testing.expectEqual(Reason.ok, s.receive(&b, 100, 50));
    try testing.expectEqualSlices(u8, &.{ 0x1A, 0x80, 0x9F, 0x82, 0xF1, 0x12, 0x5A, 0x80, 0x5F }, &b);
}

test "replay refuses a different send" {
    var dir = try TmpDir.create();
    defer dir.cleanup();
    const path = try writeFile(dir.path, "r.jsonl", exchange);
    defer testing.allocator.free(path);
    const s = try Session.openReplay(testing.allocator, path);
    defer s.close();
    try testing.expectEqual(Reason.ok, s.setConcept(&bmw_fast_params));
    try testing.expectEqual(Reason.replay_mismatch, s.send(&.{ 0x82, 0x12, 0xF1, 0x1A, 0x81, 0xA0 }));
    try testing.expectEqual(Reason.replay_mismatch, s.send(&ident));
    try testing.expectEqual(Reason.replay_mismatch, s.lastError());
}

test "replay runs out" {
    var dir = try TmpDir.create();
    defer dir.cleanup();
    const path = try writeFile(dir.path, "r.jsonl", exchange);
    defer testing.allocator.free(path);
    const s = try Session.openReplay(testing.allocator, path);
    defer s.close();
    try testing.expectEqual(Reason.ok, s.setConcept(&bmw_fast_params));
    try testing.expectEqual(Reason.ok, s.send(&ident));
    var all: [12]u8 = undefined;
    try testing.expectEqual(Reason.ok, s.receive(&all, 100, 50));
    var more: [1]u8 = undefined;
    const start = libc.monotonicNs();
    try testing.expectEqual(Reason.timeout, s.receive(&more, 5000, 50));
    try testing.expect(libc.monotonicNs() - start < 50 * std.time.ns_per_ms);
    try testing.expectEqual(Reason.replay_mismatch, s.send(&ident));
}

test "replay of an empty file" {
    var dir = try TmpDir.create();
    defer dir.cleanup();
    const path = try writeFile(dir.path, "empty.jsonl", "");
    defer testing.allocator.free(path);
    const s = try Session.openReplay(testing.allocator, path);
    defer s.close();
    try testing.expectEqual(Reason.ok, s.setConcept(&bmw_fast_params));
    try testing.expectEqual(Reason.replay_mismatch, s.send(&ident));
}

test "replay of a missing file is no_device" {
    try testing.expectError(error.NoDevice, Session.openReplay(testing.allocator, "/nonexistent/kline-replay.jsonl"));
}

test "gate applies in replay too" {
    var dir = try TmpDir.create();
    defer dir.cleanup();
    const path = try writeFile(dir.path, "r.jsonl",
        \\{"t_us":0,"op":"send","hex":"8312F114FFFF"}
        \\
    );
    defer testing.allocator.free(path);
    const s = try Session.openReplay(testing.allocator, path);
    defer s.close();
    try testing.expectEqual(Reason.ok, s.setConcept(&bmw_fast_params));
    try testing.expectEqual(Reason.gate_refused, s.send(&clear_faults));
    try testing.expectEqual(Reason.gate_refused, s.lastError());
}
