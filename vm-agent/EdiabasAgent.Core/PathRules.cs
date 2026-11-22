namespace EdiabasAgent.Core;

/// <summary>
/// The text rules for log paths (docs/api.md "/logs/read"). Security code: the agent runs
/// elevated, so these rules and the resolved-path check in <see cref="LogService"/> are the
/// only barrier between a request and every file on the disk. Everything here fails closed:
/// a path is accepted only in the one plain shape <c>X:\name\name\...\name</c>.
/// </summary>
public static class PathRules
{
    /// <summary>MAX_PATH less the terminating NUL. Longer paths are refused, never shortened.</summary>
    public const int MaxPathLength = 259;

    public const int MaxSegmentLength = 255;

    // Reasons returned in path_not_allowed details and written to the audit line as its rule.
    public const string Empty = "empty";
    public const string TooLong = "too_long";
    public const string ControlCharacter = "control_character";
    public const string UncOrDevice = "unc_or_device_path";
    public const string Relative = "relative";
    public const string ForwardSlash = "forward_slash";
    public const string AlternateDataStream = "alternate_data_stream";
    public const string InvalidCharacter = "invalid_character";
    public const string EmptySegment = "empty_segment";
    public const string DotSegment = "dot_segment";
    public const string TrailingDotOrSpace = "trailing_dot_or_space";
    public const string DeviceName = "device_name";
    public const string OutsideRoots = "outside_roots";
    public const string ResolvesOutsideRoots = "resolves_outside_roots";
    public const string Unresolvable = "unresolvable";
    public const string NotAFile = "not_a_regular_file";
    public const string HardLink = "hard_link";

    // DOS device names. Windows treats them as devices in any folder, with any extension, and
    // with the superscript digits too.
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "COM\u00B9", "COM\u00B2", "COM\u00B3",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };

    /// <summary>
    /// Null when <paramref name="path"/> has the one accepted shape, else the reason it is
    /// refused. Decided from the text alone, before anything touches the file system. Used for
    /// log roots too: a bare drive (<c>C:\</c>) has no name after the drive and is refused.
    /// </summary>
    public static string? Check(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return Empty;
        }

        if (path.Length > MaxPathLength)
        {
            return TooLong;
        }

        foreach (var c in path)
        {
            // C0, DEL, C1 and the Unicode line/paragraph separators.
            if (char.IsControl(c) || c is '\u2028' or '\u2029')
            {
                return ControlCharacter;
            }
        }

        if (path.StartsWith('\\') || path.StartsWith('/'))
        {
            // \\server\share, \\?\, \\.\, \??\ and root-relative \folder alike.
            return path.Length >= 2 && path[1] is '\\' or '/' or '?'
                ? UncOrDevice
                : Relative;
        }

        if (path.Contains('/'))
        {
            return ForwardSlash;
        }

        // Exactly "X:\" then the rest. "C:foo" is relative to C:'s current directory.
        if (path.Length < 3 || !IsAsciiLetter(path[0]) || path[1] != ':')
        {
            return Relative;
        }

        if (path[2] != '\\')
        {
            return Relative;
        }

        var rest = path[3..];
        if (rest.Contains(':'))
        {
            // file:stream, file::$DATA, and any other colon after the drive.
            return AlternateDataStream;
        }

        if (rest.IndexOfAny(['*', '?', '<', '>', '"', '|']) >= 0)
        {
            return InvalidCharacter;
        }

        if (rest.Length == 0)
        {
            return Relative;
        }

        foreach (var segment in rest.Split('\\'))
        {
            var reason = CheckSegment(segment);
            if (reason is not null)
            {
                return reason;
            }
        }

        return null;
    }

    /// <summary>One file or folder name between backslashes.</summary>
    public static string? CheckSegment(string segment)
    {
        if (segment.Length == 0)
        {
            return EmptySegment;
        }

        if (segment.Length > MaxSegmentLength)
        {
            return TooLong;
        }

        if (segment.All(c => c == '.'))
        {
            return DotSegment;
        }

        // Windows strips trailing dots and spaces, so "api.trc." and "api.trc " name api.trc.
        if (segment[^1] is '.' or ' ')
        {
            return TrailingDotOrSpace;
        }

        if (IsDeviceName(segment))
        {
            return DeviceName;
        }

        return null;
    }

    /// <summary>
    /// <c>NUL</c>, <c>nul.txt</c>, <c>COM1.log</c>, <c>con .x</c>: the part before the first
    /// dot, without trailing spaces, is a device name.
    /// </summary>
    public static bool IsDeviceName(string segment)
    {
        var dot = segment.IndexOf('.');
        var stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
        return DeviceNames.Contains(stem);
    }

    /// <summary>
    /// A final path as GetFinalPathNameByHandle gives it (<c>\\?\C:\...</c>) in plain
    /// <c>C:\...</c> form, or null when it is anything else (UNC, a volume GUID path, a device).
    /// The result has passed <see cref="Check(string?)"/>.
    /// </summary>
    public static string? NormalizeFinal(string? finalPath)
    {
        if (string.IsNullOrEmpty(finalPath))
        {
            return null;
        }

        var p = finalPath;
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            p = p[4..];
        }

        if (p.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Check(p) is null ? p : null;
    }

    /// <summary>
    /// True when <paramref name="path"/> lies strictly below <paramref name="root"/>, on a folder
    /// boundary, compared case-insensitively. Both must already be in the checked plain form;
    /// <c>C:\EDIABAS\TRACE2\x</c> is not under <c>C:\EDIABAS\TRACE</c>.
    /// </summary>
    public static bool IsUnder(string path, string root)
    {
        if (Check(path) is not null || Check(root) is not null)
        {
            return false;
        }

        return path.Length > root.Length + 1
            && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && path[root.Length] == '\\';
    }

    /// <summary>A configured root with any trailing backslashes removed (so <c>C:\X\</c> works).</summary>
    public static string TrimRoot(string root) => root.Length > 3 ? root.TrimEnd('\\') : root;

    private static bool IsAsciiLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
