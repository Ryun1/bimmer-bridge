//! The allow-list compiled into the DLL: policy/telegrams.json, validated and turned into Zig
//! source at build time (tools/gen_allowlist.zig). Nothing is read at run time.

const rule = @import("rule.zig");
const g = @import("allowlist");

pub const version = g.version;

const exact = blk: {
    var out: [g.exact.len]rule.Exact = undefined;
    for (g.exact, 0..) |e, i| out[i] = .{ .address = e.address, .data = e.data };
    const final = out;
    break :blk final;
};

pub const list = rule.AllowList{
    .read_services = &g.read_services,
    .default_session = g.default_session,
    .adapter_reads = &g.adapter_reads,
    .exact = &exact,
};

comptime {
    if (g.version != 1) @compileError("XGate32 implements telegram rule version 1 only");
}
