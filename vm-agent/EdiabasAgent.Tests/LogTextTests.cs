using System.Text;
using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

public class LogTextTests
{
    [Theory]
    // tail
    [InlineData(100, 10L, null, null, 90, 10, true)]
    [InlineData(100, 100L, null, null, 0, 100, false)]
    [InlineData(100, 500L, null, null, 0, 100, false)]
    [InlineData(0, 65536L, null, null, 0, 0, false)]
    [InlineData(300000, 300000L, null, null, 300000 - 262144, 262144, true)]
    [InlineData(300000, long.MaxValue, null, null, 300000 - 262144, 262144, true)]
    // offset + length
    [InlineData(100, null, 0L, 10L, 0, 10, true)]
    [InlineData(100, null, 90L, 10L, 90, 10, false)]
    [InlineData(100, null, 90L, 50L, 90, 10, false)]
    [InlineData(100, null, 100L, 10L, 100, 0, false)]
    [InlineData(100, null, long.MaxValue, 10L, 100, 0, false)]
    [InlineData(300000, null, 0L, 300000L, 0, 262144, true)]
    [InlineData(300000, null, 37856L, long.MaxValue, 37856, 262144, false)]
    [InlineData(300000, null, 0L, long.MaxValue, 0, 262144, true)]
    public void Select(long size, long? tail, long? offset, long? length, long expectedOffset, int expectedLength, bool truncated)
    {
        var r = LogText.Select(size, new LogReadRange(tail, offset, length));
        Assert.Equal(new ByteRange(expectedOffset, expectedLength, truncated), r);
    }

    private static string Decode(byte[] bytes, bool atStart, bool atEnd, out string enc, out int s, out int e) =>
        LogText.Decode(bytes, atStart, atEnd, out enc, out s, out e);

    [Fact]
    public void Decode_Ascii()
    {
        Assert.Equal("abc", Decode("abc"u8.ToArray(), true, true, out var enc, out var s, out var e));
        Assert.Equal(("utf-8", 0, 0), (enc, s, e));
    }

    [Fact]
    public void Decode_Utf8()
    {
        Assert.Equal("Lösch €", Decode(Encoding.UTF8.GetBytes("Lösch €"), true, true, out var enc, out _, out _));
        Assert.Equal("utf-8", enc);
    }

    [Fact]
    public void Decode_Windows1252()
    {
        Assert.Equal("Lösch €", Decode([0x4C, 0xF6, 0x73, 0x63, 0x68, 0x20, 0x80], true, true, out var enc, out var s, out var e));
        Assert.Equal(("windows-1252", 0, 0), (enc, s, e));
    }

    [Theory]
    // "€" is E2 82 AC. Cut at the start of the range (not the file): drop the 1-3 tail bytes of the character.
    [InlineData(new byte[] { 0x82, 0xAC, 0x61 }, false, true, "a", 2, 0)]
    [InlineData(new byte[] { 0xAC, 0x61 }, false, true, "a", 1, 0)]
    // Cut at the end of the range (not the file).
    [InlineData(new byte[] { 0x61, 0xE2 }, true, false, "a", 0, 1)]
    [InlineData(new byte[] { 0x61, 0xE2, 0x82 }, true, false, "a", 0, 2)]
    [InlineData(new byte[] { 0x61, 0xF0, 0x9F, 0x98 }, true, false, "a", 0, 3)]
    [InlineData(new byte[] { 0x61, 0xC3 }, true, false, "a", 0, 1)]
    // Both.
    [InlineData(new byte[] { 0xA4, 0x61, 0xC3 }, false, false, "a", 1, 1)]
    // A complete character at the end is kept.
    [InlineData(new byte[] { 0x61, 0xE2, 0x82, 0xAC }, true, false, "a€", 0, 0)]
    public void Decode_CutCharacterAtRangeEdge_StaysUtf8(byte[] bytes, bool atStart, bool atEnd, string text, int skipStart, int skipEnd)
    {
        Assert.Equal(text, Decode(bytes, atStart, atEnd, out var enc, out var s, out var e));
        Assert.Equal(("utf-8", skipStart, skipEnd), (enc, s, e));
    }

    [Theory]
    // At the file's own start or end a broken character is not a cut: the file is not UTF-8.
    [InlineData(new byte[] { 0x82, 0xAC, 0x61 }, true, true)]
    [InlineData(new byte[] { 0x61, 0xE2 }, true, true)]
    // More than 3 continuation bytes cannot be a cut character.
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x61 }, false, true)]
    // An invalid byte elsewhere.
    [InlineData(new byte[] { 0x82, 0x61, 0xFF, 0x62 }, false, false)]
    [InlineData(new byte[] { 0x61, 0xFC, 0x62, 0xE2 }, false, false)]
    public void Decode_NotUtf8_AllBytesAsWindows1252(byte[] bytes, bool atStart, bool atEnd)
    {
        var text = Decode(bytes, atStart, atEnd, out var enc, out var s, out var e);
        Assert.Equal(("windows-1252", 0, 0), (enc, s, e));
        Assert.Equal(bytes.Length, text.Length);
    }

    [Fact]
    public void Decode_Empty()
    {
        Assert.Equal("", Decode([], false, false, out var enc, out _, out _));
        Assert.Equal("utf-8", enc);
    }

    [Fact]
    public void Grep_LineBased()
    {
        var regex = LogText.CompileGrep("ERR|warn", out var bad)!;
        Assert.Null(bad);
        var text = LogText.Grep("ok\r\nERR one\r\nfine\nwarn two\n\nERR", regex, () => 0, out var n);
        Assert.Equal("ERR one\nwarn two\nERR", text);
        Assert.Equal(3, n);
    }

    [Fact]
    public void Grep_NoMatches()
    {
        var regex = LogText.CompileGrep("nothing", out _)!;
        Assert.Equal("", LogText.Grep("a\nb", regex, () => 0, out var n));
        Assert.Equal(0, n);
    }

    [Fact]
    public void Grep_InlineCaseInsensitive()
    {
        var regex = LogText.CompileGrep("(?i)error", out _)!;
        Assert.Equal("ERROR x", LogText.Grep("ERROR x\nok", regex, () => 0, out _));
    }

    // One 5 s deadline for the whole request, not 5 s per line: a long range of lines that are
    // each just under the limit must still stop.
    [Fact]
    public void Grep_StopsAtOneDeadlineAcrossAllLines()
    {
        var regex = LogText.CompileGrep("x", out _)!;
        long now = 0;
        long Tick() => now += 1000; // every look at the clock is one second later

        var ex = Assert.Throws<System.Text.RegularExpressions.RegexMatchTimeoutException>(
            () => LogText.Grep("x\nx\nx\nx\nx\nx\nx\nx\nx\nx", regex, Tick, out _));
        Assert.NotNull(ex);
        Assert.True(now <= 1000 + LogText.GrepDeadlineMs + 1000, $"kept going until {now} ms");
    }

    [Fact]
    public void Grep_WithinTheDeadline_AllLines()
    {
        var regex = LogText.CompileGrep("x", out _)!;
        long now = 0;
        Assert.Equal("x\nx\nx", LogText.Grep("x\ny\nx\nx", regex, () => now += 1000, out var n));
        Assert.Equal(3, n);
    }

    [Fact]
    public void Grep_PathologicalPattern_StaysFast()
    {
        // Catastrophic for a backtracking engine; linear here.
        var regex = LogText.CompileGrep("^(a+)+$", out _)!;
        var line = new string('a', 50000) + "!";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LogText.Grep(line, regex, () => 0, out var n);
        Assert.Equal(0, n);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("")]
    [InlineData("(")]
    [InlineData("[a-")]
    [InlineData("a{2,1}")]
    [InlineData("(a)\\1")]
    [InlineData("(?=x)")]
    [InlineData("(?!x)")]
    [InlineData("(?<=x)")]
    [InlineData("(?>x)")]
    public void CompileGrep_Refused(string pattern)
    {
        Assert.Null(LogText.CompileGrep(pattern, out var bad));
        Assert.Equal("bad_request", bad!.Error.Code);
        Assert.Equal("grep", ((ParameterDetails)bad.Error.Details!).Parameter);
    }

    [Fact]
    public void CompileGrep_Length()
    {
        Assert.NotNull(LogText.CompileGrep(new string('a', 200), out _));
        Assert.Null(LogText.CompileGrep(new string('a', 201), out _));
    }

    [Theory]
    [InlineData("0", 0, 10, 0L)]
    [InlineData("10", 0, 10, 10L)]
    [InlineData("007", 0, 10, 7L)]
    [InlineData("11", 0, 10, null)]
    [InlineData("-1", -5, 10, null)]
    [InlineData("+1", 0, 10, null)]
    [InlineData(" 1", 0, 10, null)]
    [InlineData("1 ", 0, 10, null)]
    [InlineData("1.0", 0, 10, null)]
    [InlineData("١", 0, 10, null)] // Arabic-Indic digit one
    [InlineData("", 0, 10, null)]
    [InlineData("9223372036854775807", 0, long.MaxValue, long.MaxValue)]
    [InlineData("9223372036854775808", 0, long.MaxValue, null)]
    public void ParseWhole(string value, long min, long max, long? expected)
    {
        Assert.Equal(expected, LogText.ParseWhole(value, min, max));
    }
}
