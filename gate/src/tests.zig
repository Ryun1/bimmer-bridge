//! Root of `zig build test`.

test {
    _ = @import("rule.zig");
    _ = @import("cases_test.zig");
    _ = @import("gate_test.zig");
    _ = @import("ini.zig");
    _ = @import("allowlist_json.zig");
    _ = @import("parse_cases_test.zig");
}
