//! The gate through a fake inner IFH: function decisions, the startup name rewrite, concept
//! tracking, the latch, log mode, compatibility handling and the log lines.

const std = @import("std");
const testing = std.testing;
const gate = @import("gate.zig");
const Gate = gate.Gate;
const Message = gate.Message;

const Fake = struct {
    forwarded: [64]i16 = undefined,
    n: usize = 0,
    startup_name: [64]u8 = undefined,
    startup_name_len: usize = 0,
    startups: usize = 0,
    shutdowns: usize = 0,
    checks: usize = 0,
    exits: usize = 0,
    rc: i16 = 0,
    /// written to msgOut on each forwarded call
    answer: [3]u8 = .{ 0xC2, 0xF1, 0x12 },

    fn ctx(self: *Fake) *anyopaque {
        return self;
    }
    fn startup(c: *anyopaque, _: ?[*:0]const u8, name: ?[*:0]const u8) i16 {
        const self: *Fake = @ptrCast(@alignCast(c));
        self.startups += 1;
        if (name) |nm| {
            const s = std.mem.span(nm);
            @memcpy(self.startup_name[0..s.len], s);
            self.startup_name_len = s.len;
        }
        return 0;
    }
    fn shutdown(c: *anyopaque) void {
        const self: *Fake = @ptrCast(@alignCast(c));
        self.shutdowns += 1;
    }
    fn check(c: *anyopaque, _: i16) i16 {
        const self: *Fake = @ptrCast(@alignCast(c));
        self.checks += 1;
        return 1;
    }
    fn call(c: *anyopaque, in: ?*Message, out: ?*Message) i16 {
        const self: *Fake = @ptrCast(@alignCast(c));
        self.forwarded[self.n] = if (in) |m| m.fkt_no else -999;
        self.n += 1;
        if (out) |o| {
            o.data = &self.answer;
            o.len = self.answer.len;
        }
        return self.rc;
    }
    fn exit(c: *anyopaque) u32 {
        const self: *Fake = @ptrCast(@alignCast(c));
        self.exits += 1;
        return 42;
    }
    const vtable = gate.Inner.VTable{ .startup = startup, .shutdown = shutdown, .check = check, .call = call, .exit = exit };

    fn inner(self: *Fake) gate.Inner {
        return .{ .ctx = self, .vtable = &vtable };
    }
    fn last(self: *const Fake) ?i16 {
        return if (self.n == 0) null else self.forwarded[self.n - 1];
    }
};

const Lines = struct {
    buf: [128][1024]u8 = undefined,
    lens: [128]usize = undefined,
    n: usize = 0,

    fn sink(c: ?*anyopaque, line: []const u8) void {
        const self: *Lines = @ptrCast(@alignCast(c.?));
        if (self.n == self.buf.len) return;
        const k = @min(line.len, 1024);
        @memcpy(self.buf[self.n][0..k], line[0..k]);
        self.lens[self.n] = k;
        self.n += 1;
    }
    fn log(self: *Lines) gate.Log {
        return .{ .ctx = self, .lineFn = sink };
    }
    fn get(self: *const Lines, i: usize) []const u8 {
        return self.buf[i][0..self.lens[i]];
    }
    fn has(self: *const Lines, needle: []const u8) bool {
        for (0..self.n) |i| if (std.mem.indexOf(u8, self.get(i), needle) != null) return true;
        return false;
    }
    fn count(self: *const Lines, needle: []const u8) usize {
        var k: usize = 0;
        for (0..self.n) |i| {
            if (std.mem.indexOf(u8, self.get(i), needle) != null) k += 1;
        }
        return k;
    }
    fn dump(self: *const Lines) void {
        for (0..self.n) |i| std.debug.print("  | {s}\n", .{self.get(i)});
    }
};

const Harness = struct {
    fake: Fake = .{},
    lines: Lines = .{},
    g: Gate = undefined,
    out_buf: [8]u8 = undefined,
    out: Message = undefined,

    fn init(self: *Harness, mode: gate.Mode) void {
        self.g = Gate.init(mode, self.fake.inner(), self.lines.log());
        _ = self.g.checkIfh(6);
        _ = self.g.startup("C:\\EDIABAS\\BIN\\EDIABAS.INI", "GATE:OBD");
    }

    fn send(self: *Harness, fkt: i16, data: []const u8) i16 {
        return self.sendCh(fkt, 0, data);
    }

    fn sendCh(self: *Harness, fkt: i16, ch: u16, data: []const u8) i16 {
        var copy: [300]u8 = undefined;
        @memcpy(copy[0..data.len], data);
        var in = Message{ .fkt_no = fkt, .w_param = 0, .reserved = 0, .channel = ch, .len = @intCast(data.len), .data = if (data.len == 0) null else &copy };
        self.out = .{ .fkt_no = 0, .w_param = 0, .reserved = 0, .channel = 0, .len = 99, .data = &self.out_buf };
        return self.g.call(&in, &self.out);
    }

    fn concept(self: *Harness, c: u16) void {
        _ = self.send(27, &.{ @truncate(c), @truncate(c >> 8), 0x00, 0x00 });
    }
};

test "function classification" {
    const telegram = [_]i16{ 29, 30 };
    const either = [_]i16{ 31, 34 };
    const allowed = [_]i16{ 1, 2, 3, 4, 5, 8, 11, 12, 13, 14, 20, 21, 22, 23, 24, 27, 32, 33, 35, 37, 40, 43, 46, 47, 48, 49, 51 };
    var fkt: i32 = std.math.minInt(i16);
    while (fkt <= std.math.maxInt(i16)) : (fkt += 1) {
        const f: i16 = @intCast(fkt);
        const want: gate.Class = if (std.mem.indexOfScalar(i16, &telegram, f) != null)
            .telegram
        else if (std.mem.indexOfScalar(i16, &either, f) != null)
            .telegram_or_empty
        else if (f == 28)
            .zero_data
        else if (std.mem.indexOfScalar(i16, &allowed, f) != null)
            .allow
        else
            .refuse;
        try testing.expectEqual(want, gate.classify(f));
    }
    for ([_]i16{ 25, 26, 36, 38, 39, 41, 42, 44, 45, 50, 54, 0, 6, 7, 52, 53, 55, -1 }) |f| try testing.expectEqual(gate.Class.refuse, gate.classify(f));
}

test "dllStartupIFH name rewrite" {
    var buf: [32]u8 = undefined;
    try testing.expectEqualStrings("STD:OBD", gate.rewriteIfhName("GATE:OBD", &buf).?);
    try testing.expectEqualStrings("STD:OBD", gate.rewriteIfhName("gate:OBD", &buf).?);
    try testing.expectEqualStrings("STD:ADS", gate.rewriteIfhName("GaTe:ADS", &buf).?);
    try testing.expectEqualStrings("STD:", gate.rewriteIfhName("GATE:", &buf).?);
    try testing.expectEqualStrings("STD:OBD", gate.rewriteIfhName("STD:OBD", &buf).?);
    try testing.expectEqualStrings("GATE", gate.rewriteIfhName("GATE", &buf).?);
    try testing.expectEqualStrings("GATEOBD", gate.rewriteIfhName("GATEOBD", &buf).?);
    try testing.expectEqualStrings("XGATE:OBD", gate.rewriteIfhName("XGATE:OBD", &buf).?);
    try testing.expectEqualStrings("", gate.rewriteIfhName("", &buf).?);
    const r = gate.rewriteIfhName("GATE:OBD", &buf).?;
    try testing.expectEqual(@as(u8, 0), r.ptr[r.len]);
    var small: [8]u8 = undefined;
    try testing.expectEqualStrings("STD:OBD", gate.rewriteIfhName("GATE:OBD", &small).?);
    try testing.expectEqual(@as(?[:0]const u8, null), gate.rewriteIfhName("GATE:OBDX", &small));
    try testing.expectEqual(@as(?[:0]const u8, null), gate.rewriteIfhName("ABCDEFGH", &small));
}

test "startup forwards the rewritten name and logs both arguments" {
    var h: Harness = .{};
    h.init(.enforce);
    try testing.expectEqualStrings("STD:OBD", h.fake.startup_name[0..h.fake.startup_name_len]);
    try testing.expect(h.lines.has("STARTUP ini=C:\\EDIABAS\\BIN\\EDIABAS.INI ifh=GATE:OBD as=STD:OBD fwd=yes rc=0"));
    try testing.expect(h.lines.has("CHECK compat=6 fwd=yes rc=1"));
    _ = h.g.startup(null, null);
    try testing.expect(h.lines.has("STARTUP ini=(null) ifh=(null) as=(null) fwd=yes"));
}

test "a read telegram is forwarded; a write is refused and latches" {
    var h: Harness = .{};
    h.init(.enforce);
    h.concept(0x010F);
    try testing.expectEqual(@as(i16, 0), h.send(29, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80 }));
    try testing.expectEqual(@as(?i16, 29), h.fake.last());
    try testing.expect(h.lines.has("CALL fkt=29 ifhSendTelegram wParam=0 ch=0 len=5 data=8212F11A80 concept=010F verdict=allow fwd=yes rc=0 outLen=3 out=C2F112"));
    try testing.expect(h.lines.has("newConcept=010F"));

    const before = h.fake.n;
    try testing.expectEqual(gate.refused_rc, h.send(29, &.{ 0x83, 0x12, 0xF1, 0x14, 0xFF, 0xFF }));
    try testing.expectEqual(before, h.fake.n);
    try testing.expectEqual(@as(u16, 0), h.out.len);
    try testing.expect(h.lines.has("REFUSED service fkt=29 frame=8312F114FFFF"));
    try testing.expect(h.lines.has("LATCHED"));
    try testing.expect(h.lines.has("verdict=refuse:service fwd=no rc=-1"));

    // Latched: even an allowed read is refused, and so is every other function ...
    try testing.expectEqual(gate.refused_rc, h.send(29, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80 }));
    try testing.expectEqual(gate.refused_rc, h.send(27, &.{ 0x0F, 0x01 }));
    try testing.expectEqual(gate.refused_rc, h.send(1, &.{}));
    try testing.expectEqual(gate.refused_rc, h.send(2, &.{}));
    try testing.expectEqual(gate.refused_rc, h.send(20, &.{}));
    try testing.expectEqual(before, h.fake.n);
    try testing.expect(h.lines.has("verdict=latched judged=allow fwd=no"));
    try testing.expectEqual(@as(usize, 1), h.lines.count("LATCHED"));
    // ... except status, error, result, end and disconnect.
    for ([_]i16{ 3, 4, 5, 21, 8 }) |f| {
        try testing.expectEqual(@as(i16, 0), h.send(f, &.{}));
        try testing.expectEqual(@as(?i16, f), h.fake.last());
    }
    // dllShutdownIFH clears the latch.
    h.g.shutdown();
    try testing.expectEqual(@as(usize, 1), h.fake.shutdowns);
    h.concept(0x010F);
    try testing.expectEqual(@as(i16, 0), h.send(29, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80 }));
    // and so does dllStartupIFH
    _ = h.send(29, &.{ 0x82, 0x12, 0xF1, 0x11, 0x01 });
    try testing.expect(h.g.latched);
    _ = h.g.startup("x", "GATE:OBD");
    try testing.expect(!h.g.latched);
}

test "refused functions latch; allowed ones are forwarded without inspection" {
    for ([_]i16{ 25, 26, 36, 38, 39, 41, 42, 44, 45, 50, 0, 52, 99, -5 }) |f| {
        var h: Harness = .{};
        h.init(.enforce);
        const n = h.fake.n;
        try testing.expectEqual(gate.refused_rc, h.send(f, &.{ 0x01, 0x02 }));
        try testing.expectEqual(n, h.fake.n);
        try testing.expect(h.lines.has("REFUSED function"));
        try testing.expect(h.g.latched);
    }
    var h: Harness = .{};
    h.init(.enforce);
    for ([_]i16{ 1, 2, 3, 4, 5, 8, 11, 12, 13, 14, 20, 21, 22, 23, 24, 27, 32, 33, 35, 37, 40, 43, 46, 47, 48, 49, 51 }) |f| {
        try testing.expectEqual(@as(i16, 0), h.send(f, &.{ 0xDE, 0xAD }));
        try testing.expectEqual(@as(?i16, f), h.fake.last());
    }
    try testing.expect(!h.g.latched);
}

test "telegram functions 30, 31, 34" {
    var h: Harness = .{};
    h.init(.enforce);
    h.concept(0x010F);
    try testing.expectEqual(@as(i16, 0), h.send(30, &.{ 0x82, 0x12, 0xF1, 0x21, 0x01 }));
    try testing.expectEqual(@as(i16, 0), h.send(31, &.{}));
    try testing.expectEqual(@as(i16, 0), h.send(34, &.{}));
    try testing.expectEqual(@as(i16, 0), h.send(31, &.{ 0x82, 0x12, 0xF1, 0x22, 0x01 }));
    try testing.expectEqual(@as(i16, 0), h.send(34, &.{ 0x81, 0x12, 0xF1, 0x3E }));
    try testing.expect(!h.g.latched);
    try testing.expectEqual(gate.refused_rc, h.send(34, &.{ 0x82, 0x12, 0xF1, 0x11, 0x01 }));
    try testing.expect(h.g.latched);

    var h2: Harness = .{};
    h2.init(.enforce);
    h2.concept(0x010F);
    try testing.expectEqual(gate.refused_rc, h2.send(30, &.{}));
    try testing.expect(h2.lines.has("REFUSED malformed fkt=30 frame=-"));
}

test "a telegram before any concept, after ifhInit/ifhEnd, or under DS2 is refused" {
    var h: Harness = .{};
    h.init(.enforce);
    try testing.expectEqual(gate.refused_rc, h.send(29, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80 }));
    try testing.expect(h.lines.has("REFUSED no_concept"));

    h.g.shutdown();
    h.concept(0x010F);
    _ = h.send(1, &.{});
    try testing.expectEqual(gate.refused_rc, h.send(29, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80 }));

    h.g.shutdown();
    h.concept(0x010F);
    _ = h.send(8, &.{});
    try testing.expectEqual(gate.refused_rc, h.send(29, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80 }));

    h.g.shutdown();
    h.concept(0x0006);
    try testing.expectEqual(gate.refused_rc, h.send(29, &.{ 0x12, 0x05, 0x0B, 0x03, 0x1F }));
    try testing.expect(h.lines.has("REFUSED ds2"));

    // a concept from data too short to hold one clears it
    h.g.shutdown();
    h.concept(0x010F);
    _ = h.send(27, &.{0x0F});
    try testing.expectEqual(gate.refused_rc, h.send(29, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80 }));
    try testing.expect(h.lines.has("newConcept=none"));
}

test "the concept is per channel, and channels must agree" {
    var h: Harness = .{};
    h.init(.enforce);
    _ = h.sendCh(27, 1, &.{ 0x0F, 0x01 });
    _ = h.sendCh(27, 2, &.{ 0x0F, 0x01 });
    try testing.expectEqual(@as(i16, 0), h.sendCh(29, 1, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80 }));
    try testing.expectEqual(@as(i16, 0), h.sendCh(29, 2, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80 }));
    try testing.expect(!h.g.latched);
    // a channel with no concept of its own
    try testing.expectEqual(gate.refused_rc, h.sendCh(29, 3, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80 }));
    try testing.expect(h.lines.has("REFUSED no_concept"));
}

test "the concept table never overflows into a wrong concept" {
    var c: gate.Concepts = .{};
    var ch: u16 = 0;
    while (ch < 40) : (ch += 1) c.set(ch, 0x010F);
    try testing.expectEqual(@as(?u16, 0x010F), c.get(0));
    try testing.expectEqual(@as(?u16, 0x010F), c.get(15));
    try testing.expectEqual(@as(?u16, null), c.get(16));
    try testing.expectEqual(@as(?u16, null), c.get(39));
}

test "a null msgIn, or data null with a length, is refused" {
    var h: Harness = .{};
    h.init(.enforce);
    try testing.expectEqual(gate.refused_rc, h.g.call(null, null));
    try testing.expect(h.lines.has("REFUSED no_message"));

    var h2: Harness = .{};
    h2.init(.enforce);
    h2.concept(0x010F);
    var in = Message{ .fkt_no = 29, .w_param = 0, .reserved = 0, .channel = 0, .len = 5, .data = null };
    try testing.expectEqual(gate.refused_rc, h2.g.call(&in, null));
    try testing.expect(h2.lines.has("REFUSED malformed fkt=29 frame=(null)"));
}

test "log mode forwards everything but logs the same decisions" {
    var h: Harness = .{};
    h.init(.log);
    h.concept(0x010F);
    try testing.expectEqual(@as(i16, 0), h.send(29, &.{ 0x83, 0x12, 0xF1, 0x14, 0xFF, 0xFF }));
    try testing.expectEqual(@as(?i16, 29), h.fake.last());
    try testing.expect(h.lines.has("WOULD_REFUSE service fkt=29 frame=8312F114FFFF"));
    try testing.expect(h.lines.has("WOULD_LATCH"));
    try testing.expect(!h.lines.has("REFUSED"));
    try testing.expect(h.lines.has("verdict=refuse:service fwd=yes"));
    try testing.expectEqual(@as(i16, 0), h.send(29, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80 }));
    try testing.expect(h.lines.has("verdict=latched judged=allow fwd=yes"));
    try testing.expectEqual(@as(i16, 0), h.send(41, &.{0x00}));
    try testing.expectEqual(@as(?i16, 41), h.fake.last());
    try testing.expect(h.lines.has("verdict=latched judged=refuse:function fwd=yes"));
    try testing.expect(h.lines.has("WOULD_REFUSE function fkt=41 frame=00"));
    try testing.expectEqual(@as(usize, 1), h.lines.count("WOULD_LATCH"));
}

test "28 ifhSetTelPreface: only an empty or all-zero preface passes" {
    var h: Harness = .{};
    h.init(.enforce);
    try testing.expectEqual(@as(i16, 0), h.send(28, &.{}));
    try testing.expectEqual(@as(i16, 0), h.send(28, &.{ 0x00, 0x00, 0x00, 0x00 }));
    try testing.expect(!h.g.latched);
    const n = h.fake.n;
    try testing.expectEqual(gate.refused_rc, h.send(28, &.{ 0x00, 0x2E, 0x00, 0x00 }));
    try testing.expectEqual(n, h.fake.n);
    try testing.expect(h.lines.has("REFUSED function fkt=28"));
    try testing.expect(h.g.latched);
}

test "54 ifhSetParameterRaw is refused (it could set the protocol unseen)" {
    var h: Harness = .{};
    h.init(.enforce);
    try testing.expectEqual(gate.refused_rc, h.send(54, &.{ 0x0D, 0x01 }));
    try testing.expectEqual(@as(usize, 0), h.fake.n);
    try testing.expect(h.g.latched);
}

test "channels that disagree on the concept judge as none" {
    var h: Harness = .{};
    h.init(.enforce);
    h.concept(0x010F);
    _ = h.sendCh(27, 1, &.{ 0x0D, 0x01, 0x00, 0x00 });
    try testing.expectEqual(gate.refused_rc, h.send(29, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80 }));
    try testing.expect(h.lines.has("REFUSED no_concept fkt=29"));
}

test "connect and disconnect clear the concept" {
    for ([_]i16{ 20, 21 }) |f| {
        var h: Harness = .{};
        h.init(.enforce);
        h.concept(0x010F);
        _ = h.send(f, &.{});
        try testing.expectEqual(gate.refused_rc, h.send(29, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80 }));
        try testing.expect(h.lines.has("REFUSED no_concept"));
    }
}

test "a frame both framings read is refused unless both readings are allowed" {
    var h: Harness = .{};
    h.init(.enforce);
    h.concept(0x010F);
    var f: [3 + 35]u8 = [_]u8{0} ** (3 + 35);
    f[0] = 0xA3;
    f[1] = 0x12;
    f[2] = 0xF1;
    f[3] = 0x22;
    f[4] = 0x2E; // KWP2000* reading: WriteDataByIdentifier
    try testing.expectEqual(gate.refused_rc, h.send(29, &f));
    try testing.expect(h.lines.has("REFUSED ambiguous"));
}

test "compatibility above 6 is not assumed to have the 5/6 layout" {
    var h: Harness = .{};
    h.g = Gate.init(.enforce, h.fake.inner(), h.lines.log());
    _ = h.g.checkIfh(7);
    _ = h.g.startup("ini", "GATE:OBD");
    try testing.expectEqual(gate.refused_rc, h.send(27, &.{ 0x0F, 0x01 }));
    try testing.expect(h.lines.has("layout=unsupported"));
    try testing.expect(h.lines.has("REFUSED compat"));
}

test "compatibility below 5 refuses everything, in every mode" {
    for ([_]gate.Mode{ .enforce, .log }) |mode| {
        var h: Harness = .{};
        h.g = Gate.init(mode, h.fake.inner(), h.lines.log());
        _ = h.g.checkIfh(4);
        _ = h.g.startup("ini", "GATE:OBD");
        try testing.expectEqual(gate.refused_rc, h.send(27, &.{ 0x0F, 0x01 }));
        try testing.expectEqual(gate.refused_rc, h.send(29, &.{ 0x82, 0x12, 0xF1, 0x1A, 0x80 }));
        try testing.expectEqual(@as(usize, 0), h.fake.n);
        // msgOut is not touched: its layout is not known
        try testing.expectEqual(@as(u16, 99), h.out.len);
        try testing.expect(h.lines.has("REFUSED compat fkt=27"));
        try testing.expect(h.lines.has("layout=unknown"));
        // end and disconnect still reach the IFH
        try testing.expectEqual(@as(i16, 0), h.send(8, &.{}));
        try testing.expectEqual(@as(?i16, 8), h.fake.last());
    }
}

test "dllCheckIFH never called: enforce refuses, log mode forwards" {
    {
        var h: Harness = .{};
        h.g = Gate.init(.enforce, h.fake.inner(), h.lines.log());
        _ = h.g.startup("ini", "GATE:OBD");
        try testing.expectEqual(gate.refused_rc, h.send(1, &.{}));
        try testing.expectEqual(@as(usize, 0), h.fake.n);
        try testing.expect(h.lines.has("REFUSED compat"));
    }
    {
        var h: Harness = .{};
        h.g = Gate.init(.log, h.fake.inner(), h.lines.log());
        _ = h.g.startup("ini", "GATE:OBD");
        try testing.expectEqual(@as(i16, 0), h.send(29, &.{ 0x83, 0x12, 0xF1, 0x14, 0xFF, 0xFF }));
        try testing.expectEqual(@as(?i16, 29), h.fake.last());
        try testing.expect(h.lines.has("CALL fkt=29 ifhSendTelegram wParam=0 layout=unknown verdict=allow fwd=yes rc=0"));
    }
}

test "no inner IFH: everything fails, nothing crashes" {
    var lines: Lines = .{};
    var g = Gate.init(.log, null, lines.log());
    try testing.expectEqual(gate.refused_rc, g.checkIfh(6));
    try testing.expectEqual(gate.refused_rc, g.startup("ini", "GATE:OBD"));
    var data = [_]u8{ 0x82, 0x12, 0xF1, 0x1A, 0x80 };
    var in = Message{ .fkt_no = 2, .w_param = 0, .reserved = 0, .channel = 0, .len = 0, .data = null };
    try testing.expectEqual(gate.refused_rc, g.call(&in, null));
    in = .{ .fkt_no = 29, .w_param = 0, .reserved = 0, .channel = 0, .len = data.len, .data = &data };
    try testing.expectEqual(gate.refused_rc, g.call(&in, null));
    g.shutdown();
    try testing.expectEqual(@as(u32, 0), g.exit());
    try testing.expect(lines.has("XStd32.dll not loaded"));
}

test "exit forwards the inner return register" {
    var h: Harness = .{};
    h.init(.enforce);
    try testing.expectEqual(@as(u32, 42), h.g.exit());
}

test "long data is capped in the log" {
    var h: Harness = .{};
    h.init(.enforce);
    var big: [300]u8 = undefined;
    @memset(&big, 0xAB);
    _ = h.send(46, &big);
    try testing.expect(h.lines.has("len=300 data=ABAB"));
    try testing.expect(h.lines.has("+44 concept=none"));
}

test "fuzz: random messages never crash and never forward a refused telegram" {
    var prng = std.Random.DefaultPrng.init(0xF00D);
    const r = prng.random();
    var h: Harness = .{};
    h.init(.enforce);
    var i: usize = 0;
    while (i < 50_000) : (i += 1) {
        if (h.g.latched) {
            h.g.shutdown();
        }
        var data: [300]u8 = undefined;
        const n = if (r.boolean()) r.uintLessThan(usize, 8) else r.uintLessThan(usize, data.len);
        r.bytes(data[0..n]);
        const fkt: i16 = if (r.boolean()) r.intRangeAtMost(i16, 0, 60) else r.int(i16);
        const before = h.fake.n;
        const rc = h.send(fkt, data[0..n]);
        if (h.fake.n > before) {
            // forwarded: must have been allowed
            try testing.expect(rc == 0);
            if (gate.classify(fkt) == .telegram) try testing.expect(n >= 4 and data[2] == 0xF1);
        } else {
            try testing.expectEqual(gate.refused_rc, rc);
        }
        if (h.fake.n > 60) h.fake.n = 0;
        if (h.lines.n > 100) h.lines.n = 0;
    }
}
