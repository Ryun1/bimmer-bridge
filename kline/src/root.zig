//! kline: the K+DCAN cable layer (FTDI FT232R, K-line), for macOS and Linux.

pub const sys = struct {
    pub const darwin = @import("sys/darwin.zig");
    pub const linux = @import("sys/linux.zig");
};

test {
    _ = sys.darwin;
    _ = sys.linux;
}
