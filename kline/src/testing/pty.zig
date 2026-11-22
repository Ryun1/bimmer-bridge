//! Test helpers: a pseudo-terminal standing in for the cable, and a temporary directory.

const std = @import("std");
const libc = @import("../sys/libc.zig");

pub const Pty = struct {
    master: std.posix.fd_t,
    slave_path: [:0]const u8,

    pub fn open() !Pty {
        const flags: c_int = @bitCast(std.c.O{ .ACCMODE = .RDWR, .NOCTTY = true });
        const fd = libc.posix_openpt(flags);
        if (fd < 0) return error.PtyUnavailable;
        errdefer _ = std.c.close(fd);
        if (libc.grantpt(fd) != 0 or libc.unlockpt(fd) != 0) return error.PtyUnavailable;
        const name = libc.ptsname(fd) orelse return error.PtyUnavailable;
        const path = try std.heap.c_allocator.dupeZ(u8, std.mem.span(name));
        return .{ .master = fd, .slave_path = path };
    }

    pub fn close(self: *Pty) void {
        if (self.master >= 0) _ = std.c.close(self.master);
        self.master = -1;
        if (self.slave_path.len > 0) std.heap.c_allocator.free(self.slave_path);
        self.slave_path = "";
    }
};

/// A fresh directory under /tmp, removed with its (flat) contents by cleanup.
pub const TmpDir = struct {
    path: [:0]const u8,

    pub fn create() !TmpDir {
        var template = "/tmp/kline-test-XXXXXX".*;
        const p = libc.mkdtemp(&template) orelse return error.TmpDirUnavailable;
        return .{ .path = try std.heap.c_allocator.dupeZ(u8, std.mem.span(p)) };
    }

    pub fn cleanup(self: *TmpDir) void {
        if (std.c.opendir(self.path)) |dir| {
            defer _ = std.c.closedir(dir);
            while (std.c.readdir(dir)) |ent| {
                const name = std.mem.sliceTo(@as([*:0]const u8, @ptrCast(&ent.name)), 0);
                if (std.mem.eql(u8, name, ".") or std.mem.eql(u8, name, "..")) continue;
                var full: [std.fs.max_path_bytes]u8 = undefined;
                const f = std.fmt.bufPrintZ(&full, "{s}/{s}", .{ self.path, name }) catch continue;
                _ = std.c.unlink(f);
            }
        }
        _ = std.c.rmdir(self.path);
        std.heap.c_allocator.free(self.path);
        self.path = "";
    }
};
