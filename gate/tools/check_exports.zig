//! `zig build check-exports` (also run by `zig build`): reads the export table of the built
//! XGate32.dll (embedded as "xgate_dll") and checks that it is a 32-bit x86 DLL exporting the
//! seven IFH entry points under both their stdcall-decorated and plain names, each pair at the
//! same address. Prints the export table.

const std = @import("std");

const dll = @embedFile("xgate_dll");

const required = [_][2][]const u8{
    .{ "_dllCallIFH@8", "dllCallIFH" },
    .{ "_dllCheckIFH@4", "dllCheckIFH" },
    .{ "_dllExitIFH@0", "dllExitIFH" },
    .{ "_dllLockIFH@0", "dllLockIFH" },
    .{ "_dllShutdownIFH@0", "dllShutdownIFH" },
    .{ "_dllStartupIFH@8", "dllStartupIFH" },
    .{ "_dllUnlockIFH@0", "dllUnlockIFH" },
};

fn u16At(off: usize) !u16 {
    if (off + 2 > dll.len) return error.Truncated;
    return std.mem.readInt(u16, dll[off..][0..2], .little);
}
fn u32At(off: usize) !u32 {
    if (off + 4 > dll.len) return error.Truncated;
    return std.mem.readInt(u32, dll[off..][0..4], .little);
}

const Pe = struct {
    sections: usize,
    nsec: usize,

    fn fileOffset(self: Pe, rva: u32) !usize {
        var i: usize = 0;
        while (i < self.nsec) : (i += 1) {
            const s = self.sections + i * 40;
            const vsize = try u32At(s + 8);
            const va = try u32At(s + 12);
            const raw = try u32At(s + 20);
            if (rva >= va and rva < va + @max(vsize, 1)) return rva - va + raw;
        }
        return error.BadRva;
    }

    fn cstr(self: Pe, rva: u32) ![]const u8 {
        const off = try self.fileOffset(rva);
        const end = std.mem.indexOfScalarPos(u8, dll, off, 0) orelse return error.Truncated;
        return dll[off..end];
    }
};

test "XGate32.dll export table" {
    const pe_off = try u32At(0x3C);
    try std.testing.expectEqualSlices(u8, "PE\x00\x00", dll[pe_off..][0..4]);
    const machine = try u16At(pe_off + 4);
    try std.testing.expectEqual(@as(u16, 0x014C), machine); // IMAGE_FILE_MACHINE_I386
    const characteristics = try u16At(pe_off + 22);
    try std.testing.expect(characteristics & 0x2000 != 0); // IMAGE_FILE_DLL
    const nsec = try u16At(pe_off + 6);
    const opt_size = try u16At(pe_off + 20);
    const opt = pe_off + 24;
    try std.testing.expectEqual(@as(u16, 0x10B), try u16At(opt)); // PE32
    const export_rva = try u32At(opt + 96);
    try std.testing.expect(export_rva != 0);
    const pe = Pe{ .sections = opt + opt_size, .nsec = nsec };

    const ed = try pe.fileOffset(export_rva);
    const dll_name = try pe.cstr(try u32At(ed + 12));
    const n_names = try u32At(ed + 24);
    const funcs = try pe.fileOffset(try u32At(ed + 28));
    const names = try pe.fileOffset(try u32At(ed + 32));
    const ords = try pe.fileOffset(try u32At(ed + 36));

    var found: [required.len][2]?u32 = @splat(.{ null, null });
    std.debug.print("\nXGate32.dll export table (DLL name {s}, {d} names):\n", .{ dll_name, n_names });
    var i: usize = 0;
    while (i < n_names) : (i += 1) {
        const name = try pe.cstr(try u32At(names + 4 * i));
        const ord = try u16At(ords + 2 * i);
        const rva = try u32At(funcs + 4 * @as(usize, ord));
        std.debug.print("  {s:<20} rva=0x{X:0>8}\n", .{ name, rva });
        for (required, 0..) |pair, k| {
            for (pair, 0..) |want, j| {
                if (std.mem.eql(u8, name, want)) found[k][j] = rva;
            }
        }
    }
    try std.testing.expectEqualStrings("XGate32.dll", dll_name);
    for (required, found) |pair, f| {
        if (f[0] == null or f[1] == null) {
            std.debug.print("missing export: {s} / {s}\n", .{ pair[0], pair[1] });
            return error.MissingExport;
        }
        try std.testing.expectEqual(f[0].?, f[1].?);
    }
    std.debug.print("all {d} IFH exports present (decorated and plain, same address)\n", .{required.len * 2});
}
