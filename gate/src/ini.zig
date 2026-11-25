//! XGate.ini, next to XGate32.dll:
//!
//!     Mode=log            ; or enforce (the default)
//!     LogPath=C:\EDIABAS\TRACE\XGate.log
//!
//! Keys are case-insensitive, anywhere in the file (section headers are ignored), `;` and `#`
//! start comment lines, spaces around keys and values are trimmed. The mode is `log` only when
//! there is at least one `Mode` line and every `Mode` line says `log` (any case); a missing file,
//! a missing key or any other value means `enforce`.

const std = @import("std");
const Mode = @import("gate.zig").Mode;

pub const default_log_path = "C:\\EDIABAS\\TRACE\\XGate.log";

pub const Config = struct {
    mode: Mode = .enforce,
    /// Slice of the parsed text; null → default_log_path.
    log_path: ?[]const u8 = null,
};

pub fn parse(text: []const u8) Config {
    var saw_mode = false;
    var all_log = true;
    var cfg: Config = .{};
    var it = std.mem.splitScalar(u8, text, '\n');
    var first = true;
    while (it.next()) |raw| {
        var line = std.mem.trim(u8, raw, " \t\r");
        if (first) {
            first = false;
            // UTF-8 byte order mark
            if (std.mem.startsWith(u8, line, "\xEF\xBB\xBF")) line = std.mem.trim(u8, line[3..], " \t\r");
        }
        if (line.len == 0 or line[0] == ';' or line[0] == '#' or line[0] == '[') continue;
        const eq = std.mem.indexOfScalar(u8, line, '=') orelse continue;
        const key = std.mem.trim(u8, line[0..eq], " \t");
        const value = std.mem.trim(u8, line[eq + 1 ..], " \t");
        if (std.ascii.eqlIgnoreCase(key, "Mode")) {
            saw_mode = true;
            if (!std.ascii.eqlIgnoreCase(value, "log")) all_log = false;
        } else if (std.ascii.eqlIgnoreCase(key, "LogPath")) {
            cfg.log_path = if (value.len == 0) null else value;
        }
    }
    cfg.mode = if (saw_mode and all_log) .log else .enforce;
    return cfg;
}

const testing = std.testing;

test "mode" {
    try testing.expectEqual(Mode.enforce, parse("").mode);
    try testing.expectEqual(Mode.enforce, parse("LogPath=x").mode);
    try testing.expectEqual(Mode.log, parse("Mode=log").mode);
    try testing.expectEqual(Mode.log, parse("\xEF\xBB\xBF[XGate]\r\n mode = LOG \r\n").mode);
    try testing.expectEqual(Mode.enforce, parse("Mode=enforce").mode);
    try testing.expectEqual(Mode.enforce, parse("Mode=logging").mode);
    try testing.expectEqual(Mode.enforce, parse("Mode=").mode);
    try testing.expectEqual(Mode.enforce, parse("Mode=log\nMode=enforce").mode);
    try testing.expectEqual(Mode.enforce, parse("Mode=enforce\nMode=log").mode);
    try testing.expectEqual(Mode.enforce, parse(";Mode=log").mode);
    try testing.expectEqual(Mode.enforce, parse("Mode log").mode);
}

test "log path" {
    try testing.expectEqual(@as(?[]const u8, null), parse("Mode=log").log_path);
    try testing.expectEqualStrings("D:\\x\\g.log", parse("LogPath = D:\\x\\g.log \r\n").log_path.?);
    try testing.expectEqual(@as(?[]const u8, null), parse("LogPath=").log_path);
}
