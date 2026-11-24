//! The interface-handler gate, independent of Windows: the decision for each IFH function
//! number, the concept per channel, the latch, the log lines, and the forward to the real IFH
//! through an abstract `Inner` (XStd32.dll in the DLL, a fake in the tests). Not thread-safe:
//! src/dll.zig serialises every call into it.

const std = @import("std");
const rule = @import("rule.zig");
const policy = @import("policy.zig");

/// EDIABAS's MESSAGE, compatibility numbers 5 and 6 (pack(4)). On x86 the offsets are
/// 0, 2, 4, 8, 10, 12 and the size is 16 (asserted in dll.zig for the x86 build).
pub const Message = extern struct {
    fkt_no: i16,
    w_param: i16,
    reserved: u32,
    channel: u16,
    len: u16,
    data: ?[*]u8,
};

/// The lowest compatibility number whose MESSAGE layout is the one above.
pub const min_compat: i16 = 5;

/// What a refused dllCallIFH (or any call while XStd32.dll is not loaded) returns. Not
/// verified: which EDIABAS error this produces must be observed in the VM. The agent detects
/// refusals from the log, not from this value.
pub const refused_rc: i16 = -1;

pub const Mode = enum { log, enforce };

pub const Class = enum {
    /// msgIn data is a telegram (29, 30).
    telegram,
    /// a telegram when msgIn len > 0, else allowed (31, 34).
    telegram_or_empty,
    /// allowed without inspection.
    allow,
    /// refused, whatever the data.
    refuse,
};

pub fn classify(fkt: i16) Class {
    return switch (fkt) {
        29, 30 => .telegram,
        31, 34 => .telegram_or_empty,
        1, 2, 3, 4, 5, 8, 11, 12, 13, 14, 20, 21, 22, 23, 24, 27, 28, 32, 33, 35, 37, 40, 43, 46, 47, 48, 49, 51, 54 => .allow,
        else => .refuse,
    };
}

/// Functions still forwarded while latched: status, error and result queries, and end and
/// disconnect so that EDIABAS can close cleanly.
pub fn passesLatch(fkt: i16) bool {
    return switch (fkt) {
        3, 4, 5, 8, 21 => true,
        else => false,
    };
}

pub fn fktName(fkt: i16) []const u8 {
    return switch (fkt) {
        1 => "ifhInit",
        2 => "ifhGetVersion",
        3 => "ifhGetIfhStatus",
        4 => "ifhGetIfhError",
        5 => "ifhGetResult",
        8 => "ifhEnd",
        11 => "ifhPassSetConfig",
        12 => "ifhPassGetConfig",
        13 => "ifhNotifyConfig",
        14 => "ifhGetPowerState",
        20 => "ifhConnect",
        21 => "ifhDisconnect",
        22 => "ifhInterfaceType",
        23 => "ifhPowerSupply",
        24 => "ifhIgnition",
        25 => "ifhWarmStart",
        26 => "ifhReset",
        27 => "ifhSetParameter",
        28 => "ifhSetTelPreface",
        29 => "ifhSendTelegram",
        30 => "ifhSendTelegramFreq",
        31 => "ifhRequTelegramFreq",
        32 => "ifhStopFreqTelegram",
        33 => "ifhRequestKeyBytes",
        34 => "ifhRepeatLastMsg",
        35 => "ifhRequestState",
        36 => "ifhSetPort",
        37 => "ifhGetPort",
        38 => "ifhSetProgVoltage",
        39 => "ifhLoopTest",
        40 => "ifhVersion",
        41 => "ifhDownload",
        42 => "ifhSwitchSiRelais",
        43 => "ifhStopTransmission",
        44 => "ifhRawMode",
        45 => "ifhSend",
        46 => "ifhReceive",
        47 => "ifhSysInfo",
        48 => "ifhOpenChannel",
        49 => "ifhCloseChannel",
        50 => "ifhSendDirect",
        51 => "ifhReceiveDirect",
        54 => "IfhSetParameterRaw",
        else => "unknown",
    };
}

pub const Reason = enum {
    // the telegram rule's reasons (rule.Refusal), same names
    no_concept,
    unknown_concept,
    ds2,
    malformed,
    adapter,
    service,
    /// the function number is refused (or unknown)
    function,
    /// the MESSAGE layout is not known to be the compatibility 5/6 one
    compat,
    /// msgIn is a null pointer
    no_message,
    /// dllStartupIFH's interface name does not fit the rewrite buffer
    ifh_name,

    fn fromRule(r: rule.Refusal) Reason {
        return switch (r) {
            inline else => |t| @field(Reason, @tagName(t)),
        };
    }
};

/// `GATE:<rest>` (GATE in any case) → `STD:<rest>`; anything else unchanged. Returns null only
/// when the rewritten name does not fit `out` (with its terminating zero).
pub fn rewriteIfhName(name: []const u8, out: []u8) ?[:0]const u8 {
    const prefix = "GATE:";
    if (name.len >= prefix.len and std.ascii.eqlIgnoreCase(name[0..prefix.len], prefix)) {
        const rest = name[prefix.len..];
        const n = 4 + rest.len;
        if (n + 1 > out.len) return null;
        @memcpy(out[0..4], "STD:");
        @memcpy(out[4..n], rest);
        out[n] = 0;
        return out[0..n :0];
    }
    if (name.len + 1 > out.len) return null;
    @memcpy(out[0..name.len], name);
    out[name.len] = 0;
    return out[0..name.len :0];
}

/// The real interface handler (XStd32.dll in the VM).
pub const Inner = struct {
    ctx: *anyopaque,
    vtable: *const VTable,

    pub const VTable = struct {
        startup: *const fn (ctx: *anyopaque, ini: ?[*:0]const u8, name: ?[*:0]const u8) i16,
        shutdown: *const fn (ctx: *anyopaque) void,
        check: *const fn (ctx: *anyopaque, compat: i16) i16,
        call: *const fn (ctx: *anyopaque, in: ?*Message, out: ?*Message) i16,
        exit: *const fn (ctx: *anyopaque) u32,
    };
};

/// Where finished log lines go (without timestamp or line end: the sink adds them).
pub const Log = struct {
    ctx: ?*anyopaque,
    lineFn: *const fn (ctx: ?*anyopaque, line: []const u8) void,

    pub const discard: Log = .{ .ctx = null, .lineFn = struct {
        fn f(_: ?*anyopaque, _: []const u8) void {}
    }.f };

    fn line(self: Log, text: []const u8) void {
        self.lineFn(self.ctx, text);
    }
};

/// Data bytes logged per message (the `len=` field always gives the full length).
pub const log_data_cap: usize = 256;
const log_out_cap: usize = 64;

/// The concept last set with ifhSetParameter, per MESSAGE channel. A channel never set, or set
/// with data too short to hold a concept, has none (every telegram on it → no_concept). When
/// the table is full a new channel cannot be recorded and so stays at none (fails closed).
pub const Concepts = struct {
    const slots = 16;
    channel: [slots]u16 = undefined,
    concept: [slots]?u16 = undefined,
    used: usize = 0,

    pub fn get(self: *const Concepts, ch: u16) ?u16 {
        for (self.channel[0..self.used], self.concept[0..self.used]) |c, v| {
            if (c == ch) return v;
        }
        return null;
    }

    pub fn set(self: *Concepts, ch: u16, v: ?u16) void {
        for (self.channel[0..self.used], 0..) |c, i| {
            if (c == ch) {
                self.concept[i] = v;
                return;
            }
        }
        if (self.used == slots) return;
        self.channel[self.used] = ch;
        self.concept[self.used] = v;
        self.used += 1;
    }

    pub fn clear(self: *Concepts) void {
        self.used = 0;
    }
};

const Verdict = union(enum) {
    allow,
    refuse: Reason,
    /// refused because an earlier call latched the gate
    latched,
};

pub const Gate = struct {
    mode: Mode,
    /// null when XStd32.dll (or one of its exports) could not be loaded: everything fails.
    inner: ?Inner,
    log: Log,
    /// From dllCheckIFH. Null until it is called.
    compat: ?i16 = null,
    /// Set by a refusal; cleared by dllStartupIFH and dllShutdownIFH. In log mode it is only
    /// recorded (verdict=latched) and everything is still forwarded.
    latched: bool = false,
    concepts: Concepts = .{},
    name_buf: [264]u8 = undefined,

    pub fn init(mode: Mode, inner: ?Inner, log: Log) Gate {
        return .{ .mode = mode, .inner = inner, .log = log };
    }

    fn reset(self: *Gate) void {
        self.latched = false;
        self.concepts.clear();
    }

    /// The MESSAGE fields beyond fktNo/wParam may be read only with a known 5/6 layout.
    fn layoutKnown(self: *const Gate) bool {
        return if (self.compat) |c| c >= min_compat else false;
    }

    pub fn startup(self: *Gate, ini: ?[*:0]const u8, name: ?[*:0]const u8) i16 {
        var lb: LineBuf = .{};
        const w = lb.writer();
        w.writeAll("STARTUP ini=") catch {};
        writeCStr(w, ini);
        w.writeAll(" ifh=") catch {};
        writeCStr(w, name);
        self.reset();
        const inner = self.inner orelse {
            w.print(" fwd=no rc={d} (XStd32.dll not loaded)", .{refused_rc}) catch {};
            self.log.line(lb.text());
            return refused_rc;
        };
        var fwd_name: ?[*:0]const u8 = null;
        if (name) |n| {
            const rewritten = rewriteIfhName(std.mem.span(n), &self.name_buf) orelse {
                w.print(" fwd=no rc={d}", .{refused_rc}) catch {};
                self.log.line(lb.text());
                self.refuseLine(.ifh_name, 0, null);
                self.log.line("LATCHED");
                self.latched = true;
                return refused_rc;
            };
            fwd_name = rewritten.ptr;
        }
        w.writeAll(" as=") catch {};
        writeCStr(w, fwd_name);
        const rc = inner.vtable.startup(inner.ctx, ini, fwd_name);
        w.print(" fwd=yes rc={d}", .{rc}) catch {};
        self.log.line(lb.text());
        return rc;
    }

    pub fn shutdown(self: *Gate) void {
        self.reset();
        if (self.inner) |inner| {
            inner.vtable.shutdown(inner.ctx);
            self.log.line("SHUTDOWN fwd=yes");
        } else self.log.line("SHUTDOWN fwd=no (XStd32.dll not loaded)");
    }

    pub fn exit(self: *Gate) u32 {
        if (self.inner) |inner| {
            const rc = inner.vtable.exit(inner.ctx);
            var lb: LineBuf = .{};
            lb.writer().print("EXIT fwd=yes eax={d}", .{rc}) catch {};
            self.log.line(lb.text());
            return rc;
        }
        self.log.line("EXIT fwd=no (XStd32.dll not loaded)");
        return 0;
    }

    pub fn checkIfh(self: *Gate, compat: i16) i16 {
        self.compat = compat;
        var lb: LineBuf = .{};
        const w = lb.writer();
        w.print("CHECK compat={d}", .{compat}) catch {};
        var rc: i16 = refused_rc;
        if (self.inner) |inner| {
            rc = inner.vtable.check(inner.ctx, compat);
            w.print(" fwd=yes rc={d}", .{rc}) catch {};
        } else w.print(" fwd=no rc={d} (XStd32.dll not loaded)", .{rc}) catch {};
        if (compat < min_compat) w.writeAll(" layout=unsupported (every call will be refused)") catch {};
        self.log.line(lb.text());
        return rc;
    }

    pub fn call(self: *Gate, in: ?*Message, out: ?*Message) i16 {
        var lb: LineBuf = .{};
        const w = lb.writer();
        const msg = in orelse {
            w.writeAll("CALL msgIn=null") catch {};
            return self.finish(&lb, null, out, .{ .refuse = .no_message }, .{ .refuse = .no_message });
        };
        const fkt = msg.fkt_no;
        w.print("CALL fkt={d} {s} wParam={d}", .{ fkt, fktName(fkt), msg.w_param }) catch {};

        if (!self.layoutKnown()) {
            // Only fktNo and wParam are at the same place in every layout; nothing else is read.
            w.writeAll(" layout=unknown") catch {};
            const v: Verdict = if (self.latched)
                (if (passesLatch(fkt)) .allow else .latched)
            else if (self.compat != null or self.mode == .enforce)
                // A known older layout is refused in every mode; an unknown one in enforce.
                .{ .refuse = .compat }
            else
                .allow; // log mode, dllCheckIFH not called (yet): forward, to find out.
            return self.finish(&lb, msg, out, v, v);
        }

        const data = messageData(msg);
        w.print(" ch={d} len={d} data=", .{ msg.channel, msg.len }) catch {};
        writeHexCapped(w, data, log_data_cap);
        w.writeAll(" concept=") catch {};
        writeConcept(w, self.concepts.get(msg.channel));

        // Judged even while latched, so that log mode records every telegram's own verdict.
        const judged: Verdict = blk: {
            const frame = switch (classify(fkt)) {
                .allow => break :blk .allow,
                .refuse => break :blk .{ .refuse = .function },
                .telegram_or_empty => if (msg.len == 0) break :blk .allow else data,
                .telegram => data,
            };
            const f = frame orelse break :blk .{ .refuse = .malformed };
            if (rule.check(self.concepts.get(msg.channel), f, &policy.list)) |r| break :blk .{ .refuse = Reason.fromRule(r) };
            break :blk .allow;
        };
        const v: Verdict = if (self.latched and !passesLatch(fkt)) .latched else judged;
        return self.finish(&lb, msg, out, v, judged);
    }

    /// `v` is the verdict (the latch applied), `judged` the call's own verdict.
    fn finish(self: *Gate, lb: *LineBuf, msg: ?*Message, out: ?*Message, v: Verdict, judged: Verdict) i16 {
        const w = lb.writer();
        const known = self.layoutKnown();
        writeVerdict(w, " verdict=", v);
        if (v == .latched) writeVerdict(w, " judged=", judged);

        if (v == .refuse) {
            // The REFUSED line comes before the CALL line, so the agent sees it even if a
            // forward (log mode) never returns.
            const fkt: i16 = if (msg) |m| m.fkt_no else 0;
            const frame: ?[]const u8 = if (msg != null and known) messageData(msg.?) else null;
            self.refuseLine(v.refuse, fkt, frame);
            self.log.line(if (self.enforces(v.refuse)) "LATCHED" else "WOULD_LATCH (log mode: forwarded)");
            self.latched = true;
        } else if (v == .latched and judged == .refuse and !self.enforces(judged.refuse)) {
            // log mode, already latched: still list each telegram that would be refused.
            const frame: ?[]const u8 = if (msg != null and known) messageData(msg.?) else null;
            self.refuseLine(judged.refuse, if (msg) |m| m.fkt_no else 0, frame);
        }

        const forward = switch (v) {
            .allow => true,
            .refuse => |r| !self.enforces(r),
            .latched => self.mode == .log and !(self.compat != null and !known),
        };
        const inner = self.inner orelse null;
        if (!forward or inner == null) {
            // Only a known 5/6 layout lets us touch msgOut: say there is no answer.
            if (known) if (out) |o| {
                o.len = 0;
            };
            w.print(" fwd=no rc={d}", .{refused_rc}) catch {};
            if (inner == null) w.writeAll(" (XStd32.dll not loaded)") catch {};
            self.log.line(lb.text());
            return refused_rc;
        }
        const fwd = inner.?;

        // Concept bookkeeping, only for what the real IFH actually receives.
        if (known) if (msg) |m| if (m.fkt_no == 27) {
            self.concepts.set(m.channel, conceptOf(m));
        };
        const rc = fwd.vtable.call(fwd.ctx, msg, out);
        if (msg) |m| switch (m.fkt_no) {
            1, 8 => self.concepts.clear(),
            else => {},
        };
        w.print(" fwd=yes rc={d}", .{rc}) catch {};
        if (known) {
            if (msg) |m| if (m.fkt_no == 27) {
                w.writeAll(" newConcept=") catch {};
                writeConcept(w, self.concepts.get(m.channel));
            };
            if (out) |o| {
                w.print(" outLen={d} out=", .{o.len}) catch {};
                writeHexCapped(w, messageData(o), log_out_cap);
            }
        }
        self.log.line(lb.text());
        return rc;
    }

    /// Whether a refusal for this reason stops the call: always in enforce mode; in log mode
    /// only a MESSAGE layout older than 5 (do not guess).
    fn enforces(self: *const Gate, reason: Reason) bool {
        return self.mode == .enforce or (reason == .compat and self.compat != null);
    }

    fn refuseLine(self: *Gate, reason: Reason, fkt: i16, frame: ?[]const u8) void {
        var lb: LineBuf = .{};
        const w = lb.writer();
        w.print("{s} {s} fkt={d} frame=", .{
            if (self.enforces(reason)) "REFUSED" else "WOULD_REFUSE",
            @tagName(reason),
            fkt,
        }) catch {};
        writeHexCapped(w, frame, log_data_cap);
        self.log.line(lb.text());
    }
};

const empty: []const u8 = &.{};

/// msgIn/msgOut data as a slice; null when len > 0 and the pointer is null.
fn messageData(m: *const Message) ?[]const u8 {
    if (m.len == 0) return empty;
    const p = m.data orelse return null;
    return p[0..m.len];
}

/// ifhSetParameter: the concept is the first two data bytes, little-endian. Fewer → none.
fn conceptOf(m: *const Message) ?u16 {
    const d = messageData(m) orelse return null;
    if (d.len < 2) return null;
    return std.mem.readInt(u16, d[0..2], .little);
}

fn writeConcept(w: *std.Io.Writer, c: ?u16) void {
    if (c) |v| w.print("{X:0>4}", .{v}) catch {} else w.writeAll("none") catch {};
}
fn writeVerdict(w: *std.Io.Writer, label: []const u8, v: Verdict) void {
    w.writeAll(label) catch {};
    switch (v) {
        .allow => w.writeAll("allow") catch {},
        .refuse => |r| w.print("refuse:{s}", .{@tagName(r)}) catch {},
        .latched => w.writeAll("latched") catch {},
    }
}

/// A fixed line buffer: formatting never allocates; an over-long line is cut short.
const LineBuf = struct {
    buf: [2048]u8 = undefined,
    w: std.Io.Writer = undefined,
    ready: bool = false,

    fn writer(self: *LineBuf) *std.Io.Writer {
        if (!self.ready) {
            self.w = .fixed(&self.buf);
            self.ready = true;
        }
        return &self.w;
    }

    fn text(self: *LineBuf) []const u8 {
        return self.writer().buffered();
    }
};

fn writeCStr(w: *std.Io.Writer, s: ?[*:0]const u8) void {
    const p = s orelse {
        w.writeAll("(null)") catch {};
        return;
    };
    const span = std.mem.span(p);
    w.writeAll(span[0..@min(span.len, 300)]) catch {};
}

/// Uppercase hex without separators; `-` for empty, `(null)` for a null data pointer, and
/// `+<n>` after the first `cap` bytes when there are more.
fn writeHexCapped(w: *std.Io.Writer, data: ?[]const u8, cap: usize) void {
    const d = data orelse {
        w.writeAll("(null)") catch {};
        return;
    };
    if (d.len == 0) {
        w.writeAll("-") catch {};
        return;
    }
    const n = @min(d.len, cap);
    for (d[0..n]) |b| w.print("{X:0>2}", .{b}) catch {};
    if (d.len > n) w.print("+{d}", .{d.len - n}) catch {};
}
