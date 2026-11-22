using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace EdiabasAgent.Core;

/// <summary>Which bytes of a file a read asks for: the tail, or an explicit range.</summary>
public sealed record LogReadRange(long? TailBytes, long? Offset, long? Length)
{
    public const long DefaultTailBytes = 65536;

    public static LogReadRange Tail(long bytes) => new(bytes, null, null);

    public static LogReadRange At(long offset, long length) => new(null, offset, length);
}

/// <summary>The byte range that will actually be read.</summary>
public readonly record struct ByteRange(long Offset, int Length, bool Truncated);

/// <summary>Range selection, decoding and grep for /logs/read. No file access here.</summary>
public static class LogText
{
    /// <summary>At most this many bytes per call.</summary>
    public const int MaxReadBytes = 262144;

    public const int MaxGrepLength = 200;

    public const string Utf8Name = "utf-8";
    public const string Windows1252Name = "windows-1252";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The range to read from a file of <paramref name="size"/> bytes. <c>truncated</c> is true
    /// when more of the file lies before (tail) or after (offset) the returned range, which
    /// covers a request cut to the cap.
    /// </summary>
    public static ByteRange Select(long size, LogReadRange request)
    {
        size = Math.Max(0, size);
        if (request.TailBytes is { } tail)
        {
            var want = Math.Min(Math.Max(0, tail), MaxReadBytes);
            var start = Math.Max(0, size - want);
            return new ByteRange(start, (int)(size - start), start > 0);
        }

        var offset = Math.Min(Math.Max(0, request.Offset ?? 0), size);
        var length = (int)Math.Min(Math.Min(Math.Max(0, request.Length ?? 0), MaxReadBytes), size - offset);
        return new ByteRange(offset, length, offset + length < size);
    }

    /// <summary>
    /// Decodes a range. UTF-8 when the bytes are valid UTF-8, allowing a character cut in half
    /// at an edge of the range that is not an edge of the file: those 1 to 3 bytes are dropped
    /// (<paramref name="skipStart"/>, <paramref name="skipEnd"/>) rather than turning the
    /// whole range into Windows-1252. Otherwise Windows-1252, every byte kept.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> bytes, bool startsAtFileStart, bool endsAtFileEnd,
        out string encoding, out int skipStart, out int skipEnd)
    {
        skipStart = 0;
        skipEnd = 0;

        var head = 0;
        if (!startsAtFileStart)
        {
            while (head < bytes.Length && head < 3 && IsContinuation(bytes[head]))
            {
                head++;
            }
        }

        var tail = endsAtFileEnd ? 0 : IncompleteTail(bytes[head..]);
        var body = bytes.Slice(head, bytes.Length - head - tail);
        if (Utf8.IsValid(body))
        {
            skipStart = head;
            skipEnd = tail;
            encoding = Utf8Name;
            return StrictUtf8.GetString(body);
        }

        encoding = Windows1252Name;
        return Win1252.Encoding.GetString(bytes);
    }

    /// <summary>
    /// <c>grep</c>: 1 to 200 characters, compiled with <see cref="RegexOptions.NonBacktracking"/>
    /// so no pattern can make matching slower than linear. Null with <paramref name="failure"/>
    /// set when the pattern is refused.
    /// </summary>
    public static Regex? CompileGrep(string pattern, out ApiFailure? failure)
    {
        failure = null;
        if (pattern.Length is < 1 or > MaxGrepLength)
        {
            failure = RequestParsing.Bad("grep", $"grep must be 1 to {MaxGrepLength} characters.");
            return null;
        }

        try
        {
            return new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // Bad syntax, or a construct the non-backtracking engine does not support
            // (backreferences, lookarounds, atomic groups), or a pattern too large for it.
            failure = RequestParsing.Bad("grep", "grep is not a usable regular expression: " + FirstLine(ex.Message));
            return null;
        }
    }

    /// <summary>All the lines of one request are matched within this many milliseconds.</summary>
    public const int GrepDeadlineMs = 5000;

    /// <summary>
    /// The lines of <paramref name="text"/> that match, joined with <c>\n</c>. Throws
    /// <see cref="RegexMatchTimeoutException"/> once <see cref="GrepDeadlineMs"/> have passed on
    /// <paramref name="elapsedMs"/> (monotonic milliseconds): the regex's own timeout bounds one
    /// line, this bounds the request.
    /// </summary>
    public static string Grep(string text, Regex regex, Func<long> elapsedMs, out int matchedLines)
    {
        var sb = new StringBuilder();
        matchedLines = 0;
        var start = elapsedMs();
        foreach (var raw in text.Split('\n'))
        {
            if (elapsedMs() - start > GrepDeadlineMs)
            {
                throw new RegexMatchTimeoutException(string.Empty, regex.ToString(), TimeSpan.FromMilliseconds(GrepDeadlineMs));
            }

            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (!regex.IsMatch(line))
            {
                continue;
            }

            if (matchedLines > 0)
            {
                sb.Append('\n');
            }

            sb.Append(line);
            matchedLines++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// A whole number in plain digits (no sign, spaces or exponent) within the bounds; null
    /// when it is anything else.
    /// </summary>
    public static long? ParseWhole(string value, long min, long max)
    {
        if (value.Length is < 1 or > 19 || !value.All(char.IsAsciiDigit))
        {
            return null;
        }

        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max
            ? n
            : null;
    }

    private static bool IsContinuation(byte b) => (b & 0xC0) == 0x80;

    /// <summary>
    /// Length of a multi-byte sequence that starts near the end and is cut off by the end of
    /// the range, else 0.
    /// </summary>
    private static int IncompleteTail(ReadOnlySpan<byte> bytes)
    {
        for (var back = 1; back <= 3 && back <= bytes.Length; back++)
        {
            var b = bytes[^back];
            if (IsContinuation(b))
            {
                continue;
            }

            var need = b switch
            {
                >= 0xC2 and <= 0xDF => 2,
                >= 0xE0 and <= 0xEF => 3,
                >= 0xF0 and <= 0xF4 => 4,
                _ => 0,
            };
            return need > back ? back : 0;
        }

        return 0;
    }

    private static string FirstLine(string s)
    {
        var nl = s.IndexOfAny(['\r', '\n']);
        return nl < 0 ? s : s[..nl];
    }
}
