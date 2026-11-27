//! kline: the K+DCAN cable layer (FTDI FT232R, K-line), for macOS and Linux.

pub const sys = struct {
    pub const darwin = @import("sys/darwin.zig");
    pub const linux = @import("sys/linux.zig");
    pub const libc = @import("sys/libc.zig");
};

pub const port = @import("port.zig");
pub const cable_state = @import("cable_state.zig");
pub const identity = @import("identity.zig");
pub const gate = @import("gate.zig");
pub const recording = @import("recording.zig");
pub const session = @import("session.zig");
pub const Session = session.Session;
/// Pty and temp-dir helpers, for tests here and in the C ABI module.
pub const testing = @import("testing/pty.zig");
pub const Port = port.Port;

test {
    _ = port;
    _ = cable_state;
    _ = identity;
    _ = gate;
    _ = recording;
    _ = session;
    _ = sys.darwin;
    _ = sys.linux;
}
