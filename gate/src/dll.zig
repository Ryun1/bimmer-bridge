//! XGate32.dll: the Win32 x86 entry points EDIABAS calls, forwarding through src/gate.zig to
//! the real interface handler XStd32.dll in the same directory. Every entry point is exported
//! twice, under the stdcall-decorated name (`_dllCallIFH@8`) and the plain one (`dllCallIFH`).
//!
//! Nothing happens in DllMain. On the first call into any entry point the DLL reads XGate.ini,
//! opens the log and loads XStd32.dll by full path; if any of that fails for XStd32.dll, the
//! gate runs with no inner IFH and every call fails (fails closed).

const std = @import("std");
const builtin = @import("builtin");
const gate = @import("gate.zig");
const ini = @import("ini.zig");
const policy = @import("policy.zig");
const Message = gate.Message;

comptime {
    if (builtin.cpu.arch != .x86 or builtin.os.tag != .windows)
        @compileError("dll.zig is the Win32 x86 interface handler; build it with -Dtarget=x86-windows-gnu");
    // MESSAGE, compatibility 5/6, pack(4).
    std.debug.assert(@offsetOf(Message, "fkt_no") == 0);
    std.debug.assert(@offsetOf(Message, "w_param") == 2);
    std.debug.assert(@offsetOf(Message, "reserved") == 4);
    std.debug.assert(@offsetOf(Message, "channel") == 8);
    std.debug.assert(@offsetOf(Message, "len") == 10);
    std.debug.assert(@offsetOf(Message, "data") == 12);
    std.debug.assert(@sizeOf(Message) == 16);
}

/// A safety check failed (a bug): log it if the log is open, then crash the process. EDIABAS
/// dies with it, so nothing more reaches the cable: fails closed.
pub const panic = std.debug.FullPanic(struct {
    fn f(msg: []const u8, _: ?usize) noreturn {
        @branchHint(.cold);
        var buf: [512]u8 = undefined;
        logLine(null, std.fmt.bufPrint(&buf, "PANIC {s}", .{msg[0..@min(msg.len, 400)]}) catch "PANIC");
        @trap();
    }
}.f);

// ---- kernel32 ------------------------------------------------------------------------------

const BOOL = i32;
const HANDLE = ?*anyopaque;
const HMODULE = *opaque {};
const SRWLOCK = extern struct { ptr: ?*anyopaque = null };
const SYSTEMTIME = extern struct { year: u16, month: u16, dow: u16, day: u16, hour: u16, minute: u16, second: u16, ms: u16 };
const invalid_handle: HANDLE = @ptrFromInt(std.math.maxInt(usize));

const winapi = std.builtin.CallingConvention.winapi;
extern "kernel32" fn GetModuleHandleExW(flags: u32, name: ?*const anyopaque, module: *?HMODULE) callconv(winapi) BOOL;
extern "kernel32" fn GetModuleFileNameW(module: ?HMODULE, name: [*]u16, size: u32) callconv(winapi) u32;
extern "kernel32" fn LoadLibraryExW(name: [*:0]const u16, file: HANDLE, flags: u32) callconv(winapi) ?HMODULE;
extern "kernel32" fn GetProcAddress(module: HMODULE, name: [*:0]const u8) callconv(winapi) ?*const anyopaque;
extern "kernel32" fn CreateFileW(name: [*:0]const u16, access: u32, share: u32, sa: ?*anyopaque, disposition: u32, flags: u32, template: HANDLE) callconv(winapi) HANDLE;
extern "kernel32" fn ReadFile(file: HANDLE, buf: [*]u8, len: u32, read: ?*u32, overlapped: ?*anyopaque) callconv(winapi) BOOL;
extern "kernel32" fn WriteFile(file: HANDLE, buf: [*]const u8, len: u32, written: ?*u32, overlapped: ?*anyopaque) callconv(winapi) BOOL;
extern "kernel32" fn GetFileSizeEx(file: HANDLE, size: *i64) callconv(winapi) BOOL;
extern "kernel32" fn CloseHandle(file: HANDLE) callconv(winapi) BOOL;
extern "kernel32" fn MoveFileExW(from: [*:0]const u16, to: [*:0]const u16, flags: u32) callconv(winapi) BOOL;
extern "kernel32" fn MultiByteToWideChar(cp: u32, flags: u32, s: [*]const u8, n: i32, w: [*]u16, wn: i32) callconv(winapi) i32;
extern "kernel32" fn GetLocalTime(t: *SYSTEMTIME) callconv(winapi) void;
extern "kernel32" fn GetCurrentProcessId() callconv(winapi) u32;
extern "kernel32" fn AcquireSRWLockExclusive(lock: *SRWLOCK) callconv(winapi) void;
extern "kernel32" fn ReleaseSRWLockExclusive(lock: *SRWLOCK) callconv(winapi) void;

const GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x2;
const GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x4;
const LOAD_WITH_ALTERED_SEARCH_PATH = 0x8;
const GENERIC_READ = 0x80000000;
const FILE_APPEND_DATA = 0x4;
const FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;
const OPEN_EXISTING = 3;
const OPEN_ALWAYS = 4;
const CREATE_ALWAYS = 2;
const FILE_ATTRIBUTE_NORMAL = 0x80;
const MOVEFILE_REPLACE_EXISTING = 0x1;
const CP_ACP = 0;

// ---- the real IFH --------------------------------------------------------------------------

const Fns = struct {
    call: *const fn (?*Message, ?*Message) callconv(winapi) i16,
    check: *const fn (i16) callconv(winapi) i16,
    exit: *const fn () callconv(winapi) u32,
    lock: *const fn () callconv(winapi) BOOL,
    shutdown: *const fn () callconv(winapi) void,
    startup: *const fn (?[*:0]const u8, ?[*:0]const u8) callconv(winapi) i16,
    unlock: *const fn () callconv(winapi) void,
};

fn resolve(comptime T: type, module: HMODULE, comptime decorated: [:0]const u8, comptime plain: [:0]const u8) ?T {
    const p = GetProcAddress(module, decorated) orelse GetProcAddress(module, plain) orelse return null;
    return @ptrCast(p);
}

const adapter = struct {
    fn fns(ctx: *anyopaque) *const Fns {
        return @ptrCast(@alignCast(ctx));
    }
    fn startup(ctx: *anyopaque, a: ?[*:0]const u8, b: ?[*:0]const u8) i16 {
        return fns(ctx).startup(a, b);
    }
    fn shutdown(ctx: *anyopaque) void {
        fns(ctx).shutdown();
    }
    fn check(ctx: *anyopaque, c: i16) i16 {
        return fns(ctx).check(c);
    }
    fn call(ctx: *anyopaque, in: ?*Message, out: ?*Message) i16 {
        return fns(ctx).call(in, out);
    }
    fn exit(ctx: *anyopaque) u32 {
        return fns(ctx).exit();
    }
    const vtable = gate.Inner.VTable{ .startup = startup, .shutdown = shutdown, .check = check, .call = call, .exit = exit };
};

// ---- global state (guarded by `lock`) ------------------------------------------------------

var lock: SRWLOCK = .{};
var ready = false;
var g: gate.Gate = undefined;
var inner_fns: Fns = undefined;
var have_inner = false;

const path_cap = 1024;
var dir_w: [path_cap]u16 = undefined;
var dir_len: usize = 0;

var log_file: HANDLE = invalid_handle;
var log_size: u64 = 0;
var log_path_w: [path_cap + 4]u16 = undefined;
var log_old_w: [path_cap + 4]u16 = undefined;

/// The log is renamed to <LogPath>.1 (replacing it) when it would grow past this; if the
/// rename fails (someone holds it open without delete sharing) it is truncated instead.
const log_max_bytes: u64 = 1024 * 1024;

fn enter() void {
    AcquireSRWLockExclusive(&lock);
    if (!ready) {
        ready = true;
        load();
    }
}

fn leave() void {
    ReleaseSRWLockExclusive(&lock);
}

/// Joins our directory and an ASCII file name into `out` (zero-terminated).
fn inDir(out: []u16, name: []const u8) ?[:0]u16 {
    if (dir_len + name.len + 1 > out.len) return null;
    @memcpy(out[0..dir_len], dir_w[0..dir_len]);
    for (name, 0..) |c, i| out[dir_len + i] = c;
    out[dir_len + name.len] = 0;
    return out[0 .. dir_len + name.len :0];
}

fn load() void {
    var why: []const u8 = "";
    var self_module: ?HMODULE = null;
    const found_self = GetModuleHandleExW(
        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        @ptrCast(&load),
        &self_module,
    ) != 0;
    var have_dir = false;
    if (found_self) {
        var full: [path_cap]u16 = undefined;
        const n = GetModuleFileNameW(self_module, &full, path_cap);
        if (n > 0 and n < path_cap) {
            var cut: usize = n;
            while (cut > 0 and full[cut - 1] != '\\' and full[cut - 1] != '/') cut -= 1;
            if (cut > 0) {
                @memcpy(dir_w[0..cut], full[0..cut]);
                dir_len = cut;
                have_dir = true;
            }
        }
    }

    // XGate.ini → mode and log path. Anything unreadable → enforce, default path.
    var cfg: ini.Config = .{};
    var ini_buf: [4096]u8 = undefined;
    var ini_state: []const u8 = "missing";
    var ini_name: [path_cap + 16]u16 = undefined;
    if (have_dir) if (inDir(&ini_name, "XGate.ini")) |p| {
        const f = CreateFileW(p.ptr, GENERIC_READ, FILE_SHARE_ALL, null, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, null);
        if (f != invalid_handle) {
            var got: u32 = 0;
            if (ReadFile(f, &ini_buf, ini_buf.len, &got, null) != 0) {
                if (got < ini_buf.len) {
                    cfg = ini.parse(ini_buf[0..got]);
                    ini_state = "read";
                } else ini_state = "too large (ignored: enforce)";
            } else ini_state = "unreadable (enforce)";
            _ = CloseHandle(f);
        }
    };
    openLog(cfg.log_path orelse ini.default_log_path);

    // XStd32.dll from our own directory, never from the search path.
    var xstd: [path_cap + 16]u16 = undefined;
    if (!have_dir) {
        why = "own module path unknown";
    } else if (inDir(&xstd, "XStd32.dll")) |p| {
        if (LoadLibraryExW(p.ptr, null, LOAD_WITH_ALTERED_SEARCH_PATH)) |m| {
            if (self_module != null and m == self_module.?) {
                why = "XStd32.dll is this DLL";
            } else if (resolveAll(m)) |fns| {
                inner_fns = fns;
                have_inner = true;
            } else why = "XStd32.dll lacks an IFH export";
        } else why = "LoadLibraryExW(XStd32.dll) failed";
    } else why = "path too long";

    const inner: ?gate.Inner = if (have_inner) .{ .ctx = @ptrCast(&inner_fns), .vtable = &adapter.vtable } else null;
    g = gate.Gate.init(cfg.mode, inner, .{ .ctx = null, .lineFn = logLine });

    var lb: [512]u8 = undefined;
    const line = std.fmt.bufPrint(&lb, "LOAD XGate32 pid={d} mode={s} ini={s} rule=v{d} inner={s}{s}", .{
        GetCurrentProcessId(),
        @tagName(cfg.mode),
        ini_state,
        policy.version,
        if (have_inner) "XStd32.dll" else "none: every call fails: ",
        why,
    }) catch "LOAD XGate32";
    logLine(null, line);
}

fn resolveAll(m: HMODULE) ?Fns {
    return .{
        .call = resolve(@FieldType(Fns, "call"), m, "_dllCallIFH@8", "dllCallIFH") orelse return null,
        .check = resolve(@FieldType(Fns, "check"), m, "_dllCheckIFH@4", "dllCheckIFH") orelse return null,
        .exit = resolve(@FieldType(Fns, "exit"), m, "_dllExitIFH@0", "dllExitIFH") orelse return null,
        .lock = resolve(@FieldType(Fns, "lock"), m, "_dllLockIFH@0", "dllLockIFH") orelse return null,
        .shutdown = resolve(@FieldType(Fns, "shutdown"), m, "_dllShutdownIFH@0", "dllShutdownIFH") orelse return null,
        .startup = resolve(@FieldType(Fns, "startup"), m, "_dllStartupIFH@8", "dllStartupIFH") orelse return null,
        .unlock = resolve(@FieldType(Fns, "unlock"), m, "_dllUnlockIFH@0", "dllUnlockIFH") orelse return null,
    };
}

// ---- log -----------------------------------------------------------------------------------

fn openLog(path: []const u8) void {
    if (path.len == 0 or path.len >= path_cap) return;
    const n = MultiByteToWideChar(CP_ACP, 0, path.ptr, @intCast(path.len), &log_path_w, path_cap);
    if (n <= 0) return;
    const len: usize = @intCast(n);
    log_path_w[len] = 0;
    @memcpy(log_old_w[0..len], log_path_w[0..len]);
    log_old_w[len] = '.';
    log_old_w[len + 1] = '1';
    log_old_w[len + 2] = 0;
    reopenLog(OPEN_ALWAYS);
}

fn reopenLog(disposition: u32) void {
    const p: [*:0]const u16 = @ptrCast(&log_path_w);
    log_file = CreateFileW(p, FILE_APPEND_DATA, FILE_SHARE_ALL, null, disposition, FILE_ATTRIBUTE_NORMAL, null);
    log_size = 0;
    if (log_file != invalid_handle) {
        var size: i64 = 0;
        if (GetFileSizeEx(log_file, &size) != 0 and size > 0) log_size = @intCast(size);
    }
}

fn rotateLog() void {
    _ = CloseHandle(log_file);
    log_file = invalid_handle;
    const from: [*:0]const u16 = @ptrCast(&log_path_w);
    const to: [*:0]const u16 = @ptrCast(&log_old_w);
    if (MoveFileExW(from, to, MOVEFILE_REPLACE_EXISTING) != 0) {
        reopenLog(OPEN_ALWAYS);
    } else {
        reopenLog(CREATE_ALWAYS);
    }
}

/// One line, with its local timestamp and CRLF, written straight to the file (no buffering:
/// it is in the file when this returns). Called with `lock` held.
fn logLine(_: ?*anyopaque, text: []const u8) void {
    if (log_file == invalid_handle) return;
    var t: SYSTEMTIME = undefined;
    GetLocalTime(&t);
    var buf: [2200]u8 = undefined;
    var w: std.Io.Writer = .fixed(&buf);
    w.print("{d:0>4}-{d:0>2}-{d:0>2} {d:0>2}:{d:0>2}:{d:0>2}.{d:0>3} ", .{ t.year, t.month, t.day, t.hour, t.minute, t.second, t.ms }) catch {};
    const room = buf.len - w.end - 2;
    w.writeAll(text[0..@min(text.len, room)]) catch {};
    w.writeAll("\r\n") catch {};
    const bytes = w.buffered();
    if (log_size + bytes.len > log_max_bytes) rotateLog();
    if (log_file == invalid_handle) return;
    var written: u32 = 0;
    if (WriteFile(log_file, bytes.ptr, @intCast(bytes.len), &written, null) != 0) log_size += written;
}

// ---- exports -------------------------------------------------------------------------------

fn dllStartupIFH(ini_path: ?[*:0]const u8, ifh_name: ?[*:0]const u8) callconv(winapi) i16 {
    enter();
    defer leave();
    return g.startup(ini_path, ifh_name);
}

fn dllShutdownIFH() callconv(winapi) void {
    enter();
    defer leave();
    g.shutdown();
}

fn dllCheckIFH(compat: i16) callconv(winapi) i16 {
    enter();
    defer leave();
    return g.checkIfh(compat);
}

fn dllCallIFH(in: ?*Message, out: ?*Message) callconv(winapi) i16 {
    enter();
    defer leave();
    return g.call(in, out);
}

fn dllExitIFH() callconv(winapi) u32 {
    enter();
    defer leave();
    return g.exit();
}

// dllLockIFH / dllUnlockIFH may block in XStd32.dll, so they are forwarded without holding
// `lock` (a thread blocked in dllLockIFH must not stop dllUnlockIFH on another).

fn dllLockIFH() callconv(winapi) BOOL {
    enter();
    const fwd = have_inner;
    leave();
    const rc: BOOL = if (fwd) inner_fns.lock() else 0;
    enter();
    defer leave();
    var lb: [64]u8 = undefined;
    logLine(null, std.fmt.bufPrint(&lb, "LOCK fwd={s} rc={d}", .{ if (fwd) "yes" else "no", rc }) catch "LOCK");
    return rc;
}

fn dllUnlockIFH() callconv(winapi) void {
    enter();
    const fwd = have_inner;
    leave();
    if (fwd) inner_fns.unlock();
    enter();
    defer leave();
    logLine(null, if (fwd) "UNLOCK fwd=yes" else "UNLOCK fwd=no");
}

comptime {
    // "\x01" stops LLVM from decorating the name again; the MinGW linker then drops one
    // leading underscore from each exported name.
    for (.{
        .{ &dllCallIFH, "dllCallIFH", 8 },
        .{ &dllCheckIFH, "dllCheckIFH", 4 },
        .{ &dllExitIFH, "dllExitIFH", 0 },
        .{ &dllLockIFH, "dllLockIFH", 0 },
        .{ &dllShutdownIFH, "dllShutdownIFH", 0 },
        .{ &dllStartupIFH, "dllStartupIFH", 8 },
        .{ &dllUnlockIFH, "dllUnlockIFH", 0 },
    }) |e| {
        @export(e[0], .{ .name = std.fmt.comptimePrint("\x01__{s}@{d}", .{ e[1], e[2] }) });
        @export(e[0], .{ .name = std.fmt.comptimePrint("\x01_{s}", .{e[1]}) });
    }
}
