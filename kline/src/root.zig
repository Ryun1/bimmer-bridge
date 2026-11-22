//! kline: the K+DCAN cable layer (FTDI FT232R, K-line), for macOS and Linux.

pub const sys = struct {
    pub const darwin = @import("sys/darwin.zig");
    pub const linux = @import("sys/linux.zig");
};

pub const port = @import("port.zig");
pub const identity = @import("identity.zig");
pub const Port = port.Port;

test {
    _ = port;
    _ = identity;
    _ = sys.darwin;
    _ = sys.linux;
}
