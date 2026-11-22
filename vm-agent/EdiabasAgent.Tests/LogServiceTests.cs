using System.Text;
using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

public class LogServiceTests
{
    private const string Trace = @"C:\EDIABAS\TRACE";
    private const string Work = @"C:\NCSEXPER\WORK";

    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private static QueryValues Q(params (string Key, string Value)[] pairs) =>
        new(pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));

    private static (LogService Service, FakeLogFileSystem Fs) Make(params string[] roots)
    {
        var fs = new FakeLogFileSystem();
        foreach (var r in roots)
        {
            fs.Dir(PathRules.TrimRoot(r));
        }

        return (new LogService(roots.Length == 0 ? [Trace, Work] : roots, fs), fs);
    }

    private static LogReadQuery Read(string path, LogReadRange? range = null, string? grep = null) =>
        new(path, range ?? LogReadRange.Tail(LogReadRange.DefaultTailBytes), grep);

    private static string Reason(ApiFailure f) => ((ReasonDetails)f.Error.Details!).Reason;

    private static void AssertRefused(Outcome<LogReadResponse> r, string? refusal, string reason)
    {
        Assert.False(r.Ok);
        Assert.Equal(403, r.Failure!.Status);
        Assert.Equal("path_not_allowed", r.Failure.Error.Code);
        Assert.Equal(reason, Reason(r.Failure));
        Assert.Equal(reason, refusal);
    }

    // ------------------------------------------------------------------ reading: allowed

    [Fact]
    public void Read_PlainFile()
    {
        var (svc, fs) = Make(Trace);
        var f = fs.File(@"C:\EDIABAS\TRACE\api.trc", "line one\r\nline two\n");

        var r = svc.Read(Read(@"C:\EDIABAS\TRACE\api.trc"), out var refusal);

        Assert.Null(refusal);
        var v = r.Value!;
        Assert.Equal(@"C:\EDIABAS\TRACE\api.trc", v.Path);
        Assert.Equal(19, v.SizeBytes);
        Assert.Equal("2026-11-21T16:40:02Z", v.ModifiedAt);
        Assert.Equal(0, v.Offset);
        Assert.Equal(19, v.Length);
        Assert.Equal("utf-8", v.Encoding);
        Assert.False(v.Truncated);
        Assert.Null(v.Grep);
        Assert.Equal("line one\r\nline two\n", v.Text);
        Assert.True(f.Disposed);
    }

    [Fact]
    public void Read_ReportsTheResolvedPath()
    {
        var (svc, fs) = Make(Trace);
        fs.File(@"c:\ediabas\trace\API.TRC", "x", final: @"\\?\C:\EDIABAS\TRACE\api.trc");
        Assert.Equal(@"C:\EDIABAS\TRACE\api.trc", svc.Read(Read(@"c:\ediabas\trace\API.TRC"), out _).Value!.Path);
    }

    [Fact]
    public void Read_ShortNameOfTheRoot_RefusedWithoutOpening()
    {
        // The path must be spelt under a root before anything is opened; a short-name spelling
        // of the root is not, even though it would lead inside.
        var (svc, fs) = Make(Trace);
        fs.File(@"C:\EDIABA~1\TRACE\api.trc", "x", final: @"\\?\C:\EDIABAS\TRACE\api.trc");
        AssertRefused(svc.Read(Read(@"C:\EDIABA~1\TRACE\api.trc"), out var refusal), refusal, PathRules.OutsideRoots);
        Assert.Empty(fs.Opened);
    }

    [Fact]
    public void Read_ShortNameBelowTheRoot_Allowed()
    {
        var (svc, fs) = Make(Trace);
        fs.File(@"C:\EDIABAS\TRACE\NCSTRA~1.TRC", "x", final: @"\\?\C:\EDIABAS\TRACE\ncstrace-long.trc");
        Assert.True(svc.Read(Read(@"C:\EDIABAS\TRACE\NCSTRA~1.TRC"), out _).Ok);
    }

    [Fact]
    public void Read_LinkFromOutsideThatResolvesInside_RefusedWithoutOpening()
    {
        var (svc, fs) = Make(Trace);
        fs.File(@"C:\Users\ryan\trace-link\api.trc", "x", final: @"\\?\C:\EDIABAS\TRACE\api.trc");
        AssertRefused(svc.Read(Read(@"C:\Users\ryan\trace-link\api.trc"), out var refusal), refusal, PathRules.OutsideRoots);
        Assert.Empty(fs.Opened);
    }

    [Theory]
    [InlineData(@"Z:\x.trc")] // a mapped network drive, or any drive with no root on it
    [InlineData(@"D:\EDIABAS\TRACE\api.trc")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"C:\EDIABAS\TRACE2\api.trc")]
    public void Read_OutsideTheRootsByText_NeverTouchesTheFileSystem(string path)
    {
        var (svc, fs) = Make(Trace);
        fs.File(path, "x");
        AssertRefused(svc.Read(Read(path), out var refusal), refusal, PathRules.OutsideRoots);
        Assert.Empty(fs.Opened);
        Assert.DoesNotContain('Z', fs.DriveQueries);
        Assert.DoesNotContain('D', fs.DriveQueries);
    }

    [Fact]
    public void Read_RootOnANetworkDrive_GrantsNothingAndIsNotOpened()
    {
        // Z: is mapped to \\host\share: neither the root nor the file is opened.
        var fs = new FakeLogFileSystem().Dir(@"Z:\traces");
        fs.NonLocalDrives.Add('Z');
        var svc = new LogService([@"Z:\traces"], fs);
        fs.File(@"Z:\traces\api.trc", "x");

        AssertRefused(svc.Read(Read(@"Z:\traces\api.trc"), out var refusal), refusal, PathRules.OutsideRoots);
        Assert.Empty(fs.Opened);
        Assert.Empty(fs.Resolved);
    }

    [Fact]
    public void Read_RootThatResolvesToANetworkDrive_FileThereNotOpened()
    {
        // C:\EDIABAS\TRACE is a link to a folder on Z:, a network drive: the root itself is on
        // a local disk, but a path spelt on Z: is not opened.
        var fs = new FakeLogFileSystem().Dir(Trace, @"Z:\traces");
        fs.NonLocalDrives.Add('Z');
        var svc = new LogService([Trace], fs);
        fs.File(@"Z:\traces\api.trc", "x");

        AssertRefused(svc.Read(Read(@"Z:\traces\api.trc"), out var refusal), refusal, PathRules.OutsideRoots);
        Assert.Empty(fs.Opened);
    }

    [Fact]
    public void List_RootOnANetworkDrive_ShownMissingAndNotTouched()
    {
        var fs = new FakeLogFileSystem().Dir(@"Z:\traces");
        fs.NonLocalDrives.Add('Z');
        var svc = new LogService([@"Z:\traces"], fs);

        var r = svc.List(new LogListQuery(null, null, null, 50));
        Assert.False(r.Roots[0].Exists);
        Assert.Empty(fs.Resolved);
        Assert.Empty(fs.Listed);
    }

    // A root that is a junction somewhere /logs/read would refuse is not walked either: list
    // and read decide from the same roots.
    [Theory]
    [InlineData(@"Z:\traces")]      // resolves onto a drive that is not a local fixed disk
    [InlineData(@"C:\")]            // resolves to a bare drive
    [InlineData(@"C:\a\con")]       // resolves to a path NormalizeFinal refuses
    [InlineData(@"UNC\server\x")]   // resolves to a share
    public void List_RootThatResolvesWhereReadRefuses_ShownMissingAndNotWalked(string final)
    {
        var fs = new FakeLogFileSystem().Dir(Trace, final);
        fs.NonLocalDrives.Add('Z');
        fs.Listings[Trace] = [new LogDirEntry("win.ini", false, false, 10, T0)];
        var svc = new LogService([Trace], fs);

        var r = svc.List(new LogListQuery(null, null, null, 50));
        Assert.Equal([new LogRoot(0, Trace, false)], r.Roots);
        Assert.Empty(r.Files);
        Assert.Empty(fs.Listed);
    }

    [Fact]
    public void List_RootThatIsALinkToALocalFolder_Walked()
    {
        var fs = new FakeLogFileSystem().Dir(Trace, @"D:\traces");
        fs.Listings[Trace] = [new LogDirEntry("api.trc", false, false, 10, T0)];
        var svc = new LogService([Trace], fs);

        var r = svc.List(new LogListQuery(null, null, null, 50));
        Assert.True(r.Roots[0].Exists);
        Assert.Equal([@"C:\EDIABAS\TRACE\api.trc"], r.Files.Select(f => f.Path));
    }

    [Fact]
    public void Read_RootThatIsALink_ComparedByWhereItResolves()
    {
        var fs = new FakeLogFileSystem().Dir(Trace, @"D:\traces");
        var svc = new LogService([Trace], fs);
        fs.File(@"C:\EDIABAS\TRACE\api.trc", "x", final: @"\\?\D:\traces\api.trc");
        fs.File(@"C:\EDIABAS\TRACE\other.trc", "x", final: @"\\?\C:\EDIABAS\TRACE\other.trc");

        Assert.True(svc.Read(Read(@"C:\EDIABAS\TRACE\api.trc"), out _).Ok);
        AssertRefused(svc.Read(Read(@"C:\EDIABAS\TRACE\other.trc"), out var refusal), refusal, PathRules.ResolvesOutsideRoots);
    }

    [Fact]
    public void Read_RootConfiguredWithTrailingBackslash()
    {
        var (svc, fs) = Make(@"C:\EDIABAS\TRACE\");
        fs.File(@"C:\EDIABAS\TRACE\api.trc", "x");
        Assert.True(svc.Read(Read(@"C:\EDIABAS\TRACE\api.trc"), out _).Ok);
    }

    [Fact]
    public void Read_SecondRoot()
    {
        var (svc, fs) = Make(Trace, Work);
        fs.File(@"C:\NCSEXPER\WORK\NCSTRACE.TRC", "x");
        Assert.True(svc.Read(Read(@"C:\NCSEXPER\WORK\NCSTRACE.TRC"), out _).Ok);
    }

    // ------------------------------------------------------------------ reading: refused

    [Theory]
    [InlineData(@"C:\EDIABAS\TRACE\..\BIN\EDIABAS.INI", PathRules.DotSegment)]
    [InlineData(@"EDIABAS\TRACE\api.trc", PathRules.Relative)]
    [InlineData(@"\\server\share\api.trc", PathRules.UncOrDevice)]
    [InlineData(@"\\?\C:\EDIABAS\TRACE\api.trc", PathRules.UncOrDevice)]
    [InlineData(@"\\.\COM1", PathRules.UncOrDevice)]
    [InlineData(@"C:\EDIABAS\TRACE\api.trc:secret", PathRules.AlternateDataStream)]
    [InlineData(@"C:\EDIABAS\TRACE\api.trc::$DATA", PathRules.AlternateDataStream)]
    [InlineData(@"C:\EDIABAS\TRACE\COM1", PathRules.DeviceName)]
    [InlineData(@"C:\EDIABAS\TRACE\nul.trc", PathRules.DeviceName)]
    [InlineData(@"C:\EDIABAS\TRACE\api.trc.", PathRules.TrailingDotOrSpace)]
    [InlineData(@"C:\EDIABAS\TRACE\api.trc ", PathRules.TrailingDotOrSpace)]
    [InlineData("C:\\EDIABAS\\TRACE\\api\0.trc", PathRules.ControlCharacter)]
    [InlineData(@"C:/EDIABAS/TRACE/api.trc", PathRules.ForwardSlash)]
    [InlineData("", PathRules.Empty)]
    public void Read_TextRefusals_NeverTouchTheFileSystem(string path, string reason)
    {
        var (svc, fs) = Make(Trace);
        fs.File(@"C:\EDIABAS\TRACE\api.trc", "x");

        AssertRefused(svc.Read(Read(path), out var refusal), refusal, reason);
        Assert.Empty(fs.Opened);
    }

    [Fact]
    public void Read_OverLongPath_NeverTouchesTheFileSystem()
    {
        var (svc, fs) = Make(Trace);
        var path = Trace + @"\" + string.Join(@"\", Enumerable.Repeat("abcdefghij", 30));
        AssertRefused(svc.Read(Read(path), out var refusal), refusal, PathRules.TooLong);
        Assert.Empty(fs.Opened);
    }

    [Fact]
    public void Read_JunctionEscape_RefusedAndNotRead()
    {
        var (svc, fs) = Make(Trace);
        var secret = fs.File(@"C:\EDIABAS\TRACE\jn\token.txt", "SECRET", final: @"\\?\C:\ProgramData\EdiabasBridge\token.txt");

        AssertRefused(svc.Read(Read(@"C:\EDIABAS\TRACE\jn\token.txt"), out var refusal), refusal, PathRules.ResolvesOutsideRoots);
        Assert.Equal(0, secret.Reads);
        Assert.True(secret.Disposed);
    }

    [Fact]
    public void Read_SymlinkFileEscape_Refused()
    {
        var (svc, fs) = Make(Trace);
        fs.File(@"C:\EDIABAS\TRACE\link.trc", "x", final: @"\\?\C:\EDIABAS\BIN\EDIABAS.INI");
        AssertRefused(svc.Read(Read(@"C:\EDIABAS\TRACE\link.trc"), out var refusal), refusal, PathRules.ResolvesOutsideRoots);
    }

    [Fact]
    public void Read_ShortNameThatResolvesOutside_Refused()
    {
        var (svc, fs) = Make(Trace);
        var secret = fs.File(@"C:\PROGRA~3\EDIABA~1\token.txt", "SECRET", final: @"\\?\C:\ProgramData\EdiabasBridge\token.txt");
        AssertRefused(svc.Read(Read(@"C:\PROGRA~3\EDIABA~1\token.txt"), out var refusal), refusal, PathRules.OutsideRoots);
        Assert.Equal(0, secret.Reads);
        Assert.Empty(fs.Opened);
    }

    [Fact]
    public void Read_SiblingFolderWithTheRootAsPrefix_Refused()
    {
        var (svc, fs) = Make(Trace);
        fs.File(@"C:\EDIABAS\TRACE2\api.trc", "x");
        AssertRefused(svc.Read(Read(@"C:\EDIABAS\TRACE2\api.trc"), out var refusal), refusal, PathRules.OutsideRoots);

        // Even when a link makes it look inside.
        fs.File(@"C:\EDIABAS\TRACE\sib\api.trc", "x", final: @"\\?\C:\EDIABAS\TRACE2\api.trc");
        AssertRefused(svc.Read(Read(@"C:\EDIABAS\TRACE\sib\api.trc"), out refusal), refusal, PathRules.ResolvesOutsideRoots);
    }

    [Fact]
    public void Read_OutsideTheRoots_SameAnswerWhetherOrNotItExists()
    {
        // No probing of what exists elsewhere on the disk.
        var (svc, fs) = Make(Trace);
        fs.File(@"C:\Windows\win.ini", "x");
        fs.Failures[@"C:\Windows\locked.dat"] = LogOpenStatus.Unreadable;

        AssertRefused(svc.Read(Read(@"C:\Windows\win.ini"), out var r1), r1, PathRules.OutsideRoots);
        AssertRefused(svc.Read(Read(@"C:\Windows\nothing-here.ini"), out var r2), r2, PathRules.OutsideRoots);
        AssertRefused(svc.Read(Read(@"C:\Windows\locked.dat"), out var r3), r3, PathRules.OutsideRoots);
        Assert.Equal(0, fs.Files[@"C:\Windows\win.ini"].Reads);
        Assert.Empty(fs.Opened);
    }

    [Fact]
    public void Read_InsideButMissing_FileNotFound()
    {
        var (svc, _) = Make(Trace);
        var r = svc.Read(Read(@"C:\EDIABAS\TRACE\gone.trc"), out var refusal);
        Assert.Null(refusal);
        Assert.Equal(404, r.Failure!.Status);
        Assert.Equal("file_not_found", r.Failure.Error.Code);
        Assert.Null(r.Failure.Error.Details);
    }

    [Fact]
    public void Read_InsideButUnreadable_InternalError()
    {
        var (svc, fs) = Make(Trace);
        fs.Failures[@"C:\EDIABAS\TRACE\locked.trc"] = LogOpenStatus.Unreadable;
        var r = svc.Read(Read(@"C:\EDIABAS\TRACE\locked.trc"), out _);
        Assert.Equal(500, r.Failure!.Status);
        Assert.Equal("internal_error", r.Failure.Error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"\\?\UNC\server\share\api.trc")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\api.trc")]
    [InlineData(@"\\?\GLOBALROOT\Device\Serial0")]
    [InlineData(@"\\?\C:\EDIABAS\TRACE\con")]
    [InlineData(@"\\?\C:\EDIABAS\TRACE\api.trc:x")]
    public void Read_FinalPathNotPlain_Unresolvable(string? final)
    {
        var (svc, fs) = Make(Trace);
        var f = fs.File(@"C:\EDIABAS\TRACE\api.trc", "x");
        f.FinalPath = final;
        AssertRefused(svc.Read(Read(@"C:\EDIABAS\TRACE\api.trc"), out var refusal), refusal, PathRules.Unresolvable);
        Assert.Equal(0, f.Reads);
    }

    [Fact]
    public void Read_NotARegularFile_Refused()
    {
        var (svc, fs) = Make(Trace);
        var f = fs.File(@"C:\EDIABAS\TRACE\api.trc", "x");
        f.IsRegularFile = false;
        AssertRefused(svc.Read(Read(@"C:\EDIABAS\TRACE\api.trc"), out var refusal), refusal, PathRules.NotAFile);
        Assert.Equal(0, f.Reads);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public void Read_HardLinkOrUnknownLinkCount_Refused(int links)
    {
        var (svc, fs) = Make(Trace);
        var f = fs.File(@"C:\EDIABAS\TRACE\api.trc", "x");
        f.LinkCount = links;
        AssertRefused(svc.Read(Read(@"C:\EDIABAS\TRACE\api.trc"), out var refusal), refusal, PathRules.HardLink);
        Assert.Equal(0, f.Reads);
    }

    [Fact]
    public void Read_RootMissing_EverythingRefused()
    {
        var fs = new FakeLogFileSystem();
        var svc = new LogService([Trace], fs);
        fs.File(@"C:\EDIABAS\TRACE\api.trc", "x");
        AssertRefused(svc.Read(Read(@"C:\EDIABAS\TRACE\api.trc"), out var refusal), refusal, PathRules.OutsideRoots);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:")]
    [InlineData(@"EDIABAS\TRACE")]
    [InlineData(@"\\server\share")]
    [InlineData(@"C:\EDIABAS\..\")]
    public void Read_UnusableRootConfig_GrantsNothing(string root)
    {
        var fs = new FakeLogFileSystem().Dir(root).Dir(@"C:\");
        var svc = new LogService([root], fs);
        fs.File(@"C:\Windows\win.ini", "x");
        AssertRefused(svc.Read(Read(@"C:\Windows\win.ini"), out var refusal), refusal, PathRules.OutsideRoots);
    }

    [Fact]
    public void Read_RootThatResolvesToADrive_GrantsNothing()
    {
        var fs = new FakeLogFileSystem().Dir(Trace, @"C:\");
        var svc = new LogService([Trace], fs);
        fs.File(@"C:\Windows\win.ini", "x");
        AssertRefused(svc.Read(Read(@"C:\Windows\win.ini"), out var refusal), refusal, PathRules.OutsideRoots);
    }

    // ------------------------------------------------------------------ reading: ranges, decoding, grep

    [Fact]
    public void Read_Tail()
    {
        var (svc, fs) = Make(Trace);
        fs.File(@"C:\EDIABAS\TRACE\api.trc", "0123456789");
        var v = svc.Read(Read(@"C:\EDIABAS\TRACE\api.trc", LogReadRange.Tail(4)), out _).Value!;
        Assert.Equal((6L, 4L, true, "6789"), (v.Offset, v.Length, v.Truncated, v.Text));
    }

    [Fact]
    public void Read_OffsetLength()
    {
        var (svc, fs) = Make(Trace);
        fs.File(@"C:\EDIABAS\TRACE\api.trc", "0123456789");
        var v = svc.Read(Read(@"C:\EDIABAS\TRACE\api.trc", LogReadRange.At(2, 3)), out _).Value!;
        Assert.Equal((2L, 3L, true, "234"), (v.Offset, v.Length, v.Truncated, v.Text));

        v = svc.Read(Read(@"C:\EDIABAS\TRACE\api.trc", LogReadRange.At(7, 100)), out _).Value!;
        Assert.Equal((7L, 3L, false, "789"), (v.Offset, v.Length, v.Truncated, v.Text));

        v = svc.Read(Read(@"C:\EDIABAS\TRACE\api.trc", LogReadRange.At(50, 10)), out _).Value!;
        Assert.Equal((10L, 0L, false, ""), (v.Offset, v.Length, v.Truncated, v.Text));
    }

    [Fact]
    public void Read_CappedAt256KB()
    {
        var (svc, fs) = Make(Trace);
        var f = fs.File(@"C:\EDIABAS\TRACE\big.trc", "");
        f.Bytes = Enumerable.Repeat((byte)'a', LogText.MaxReadBytes + 1000).ToArray();

        var tail = svc.Read(Read(@"C:\EDIABAS\TRACE\big.trc", LogReadRange.Tail(10_000_000)), out _).Value!;
        Assert.Equal((1000L, (long)LogText.MaxReadBytes, true), (tail.Offset, tail.Length, tail.Truncated));
        Assert.Equal(LogText.MaxReadBytes, tail.Text.Length);

        var head = svc.Read(Read(@"C:\EDIABAS\TRACE\big.trc", LogReadRange.At(0, 10_000_000)), out _).Value!;
        Assert.Equal((0L, (long)LogText.MaxReadBytes, true), (head.Offset, head.Length, head.Truncated));
    }

    [Fact]
    public void Read_RangeStartingMidCharacter_StaysUtf8()
    {
        var (svc, fs) = Make(Trace);
        var f = fs.File(@"C:\EDIABAS\TRACE\api.trc", "");
        f.Bytes = Encoding.UTF8.GetBytes("aä€b"); // 61 C3A4 E282AC 62

        var v = svc.Read(Read(@"C:\EDIABAS\TRACE\api.trc", LogReadRange.At(2, 3)), out _).Value!; // A4 E2 82
        Assert.Equal("utf-8", v.Encoding);
        Assert.Equal("", v.Text);
        Assert.Equal(3L, v.Offset);
        Assert.Equal(0L, v.Length);

        v = svc.Read(Read(@"C:\EDIABAS\TRACE\api.trc", LogReadRange.Tail(5)), out _).Value!; // A4 E2 82 AC 62
        Assert.Equal(("utf-8", "€b", 3L, 4L), (v.Encoding, v.Text, v.Offset, v.Length));
    }

    [Fact]
    public void Read_Windows1252File()
    {
        var (svc, fs) = Make(Trace);
        var f = fs.File(@"C:\EDIABAS\TRACE\api.trc", "");
        f.Bytes = [0x46, 0x65, 0x68, 0x6C, 0x65, 0x72, 0x20, 0xFC, 0x62, 0x65, 0x72, 0x20, 0x80]; // "Fehler über €"
        var v = svc.Read(Read(@"C:\EDIABAS\TRACE\api.trc"), out _).Value!;
        Assert.Equal(("windows-1252", "Fehler über €", 13L), (v.Encoding, v.Text, v.Length));
    }

    [Fact]
    public void Read_Grep()
    {
        var (svc, fs) = Make(Trace);
        fs.File(@"C:\EDIABAS\TRACE\api.trc", "apiJob NVE_65\r\nok\r\napiJob TMODE\r\n");
        var v = svc.Read(Read(@"C:\EDIABAS\TRACE\api.trc", grep: "^apiJob"), out _).Value!;
        Assert.Equal("apiJob NVE_65\napiJob TMODE", v.Text);
        Assert.Equal(new GrepInfo("^apiJob", 2), v.Grep);
        Assert.Equal(0L, v.Offset);
        Assert.Equal(33L, v.Length); // the range, not the matches
    }

    private sealed class TickingClock : IRunnerClock
    {
        public long ElapsedMs => Now += 1000;
        public long Now { get; private set; }
        public DateTimeOffset UtcNow => T0.AddMilliseconds(Now);
        public void Sleep(int milliseconds) => Now += milliseconds;
    }

    [Fact]
    public void Read_GrepPastTheRequestDeadline_BadRequest()
    {
        var fs = new FakeLogFileSystem().Dir(Trace);
        var svc = new LogService([Trace], fs, new TickingClock());
        fs.File(@"C:\EDIABAS\TRACE\api.trc", string.Concat(Enumerable.Repeat("apiJob x\r\n", 20)));

        var r = svc.Read(Read(@"C:\EDIABAS\TRACE\api.trc", grep: "apiJob"), out _);
        Assert.Equal((400, "bad_request"), (r.Failure!.Status, r.Failure.Error.Code));
        Assert.Equal("grep took too long; use a simpler pattern or a smaller range.", r.Failure.Error.Message);
    }

    [Fact]
    public void Read_BadGrep_BadRequestBeforeReading()
    {
        var (svc, fs) = Make(Trace);
        var f = fs.File(@"C:\EDIABAS\TRACE\api.trc", "x");
        var r = svc.Read(Read(@"C:\EDIABAS\TRACE\api.trc", grep: "(a)\\1"), out _);
        Assert.Equal("bad_request", r.Failure!.Error.Code);
        Assert.Equal(0, f.Reads);
    }

    // ------------------------------------------------------------------ query parsing

    [Fact]
    public void ParseRead_Defaults()
    {
        var q = LogService.ParseRead(Q(("path", @"C:\x\y"))).Value!;
        Assert.Equal(new LogReadQuery(@"C:\x\y", LogReadRange.Tail(65536), null), q);
    }

    [Fact]
    public void ParseRead_Full()
    {
        Assert.Equal(LogReadRange.At(10, 20), LogService.ParseRead(Q(("path", "p"), ("offset", "10"), ("length", "20"))).Value!.Range);
        Assert.Equal(LogReadRange.Tail(5), LogService.ParseRead(Q(("path", "p"), ("tailBytes", "5"))).Value!.Range);
        Assert.Equal("ERROR", LogService.ParseRead(Q(("path", "p"), ("grep", "ERROR"))).Value!.Grep);
        Assert.Equal(LogReadRange.At(0, 999999999), LogService.ParseRead(Q(("path", "p"), ("offset", "0"), ("length", "999999999"))).Value!.Range);
    }

    [Theory]
    [InlineData("path", null, null, null, null)]
    [InlineData("tailBytes", "1", "0", "1", null)]
    [InlineData("tailBytes", "1", null, "1", null)]
    [InlineData("length", null, "0", null, null)]
    [InlineData("offset", null, null, "5", null)]
    [InlineData("tailBytes", "0", null, null, null)]
    [InlineData("tailBytes", "-1", null, null, null)]
    [InlineData("tailBytes", "+5", null, null, null)]
    [InlineData("tailBytes", " 5", null, null, null)]
    [InlineData("tailBytes", "5.0", null, null, null)]
    [InlineData("tailBytes", "1e3", null, null, null)]
    [InlineData("tailBytes", "0x10", null, null, null)]
    [InlineData("tailBytes", "99999999999999999999", null, null, null)]
    [InlineData("tailBytes", "", null, null, null)]
    [InlineData("offset", null, "-1", "5", null)]
    [InlineData("length", null, "0", "0", null)]
    [InlineData("grep", null, null, null, "")]
    [InlineData("grep", null, null, null, "(")]
    [InlineData("grep", null, null, null, "(?=a)b")]
    [InlineData("grep", null, null, null, "(?<=a)b")]
    [InlineData("grep", null, null, null, "(a)\\1")]
    [InlineData("grep", null, null, null, "(?>a)")]
    public void ParseRead_BadRequests(string parameter, string? tail, string? offset, string? length, string? grep)
    {
        var pairs = new List<(string, string)>();
        if (parameter != "path")
        {
            pairs.Add(("path", @"C:\EDIABAS\TRACE\api.trc"));
        }

        if (tail is not null) pairs.Add(("tailBytes", tail));
        if (offset is not null) pairs.Add(("offset", offset));
        if (length is not null) pairs.Add(("length", length));
        if (grep is not null) pairs.Add(("grep", grep));

        var f = LogService.ParseRead(Q(pairs.ToArray())).Failure!;
        Assert.Equal("bad_request", f.Error.Code);
        Assert.Equal(parameter, ((ParameterDetails)f.Error.Details!).Parameter);
    }

    [Fact]
    public void ParseRead_GrepOver200Characters_BadRequest()
    {
        Assert.True(LogService.ParseRead(Q(("path", "p"), ("grep", new string('a', 200)))).Ok);
        Assert.Equal("bad_request", LogService.ParseRead(Q(("path", "p"), ("grep", new string('a', 201)))).Failure!.Error.Code);
    }

    [Fact]
    public void ParseList_DefaultsAndValues()
    {
        Assert.Equal(new LogListQuery(null, null, null, 50), LogService.ParseList(Q(), 2).Value);
        var q = LogService.ParseList(Q(("root", "1"), ("glob", "*.trc"), ("since", "2026-11-21T16:40:00Z"), ("limit", "200")), 2).Value!;
        Assert.Equal(new LogListQuery(1, "*.trc", new DateTimeOffset(2026, 11, 21, 16, 40, 0, TimeSpan.Zero), 200), q);
    }

    [Theory]
    [InlineData("2026-11-21", "2026-11-21T00:00:00Z")]
    [InlineData("2026-11-21T16:40", "2026-11-21T16:40:00Z")]
    [InlineData("2026-11-21T16:40:05", "2026-11-21T16:40:05Z")]
    [InlineData("2026-11-21T17:40:05+01:00", "2026-11-21T16:40:05Z")]
    [InlineData("2026-11-21T16:40:05.123Z", "2026-11-21T16:40:05Z")]
    public void ParseList_Since(string since, string utc)
    {
        var q = LogService.ParseList(Q(("since", since)), 1).Value!;
        Assert.Equal(utc, ApiFormat.Timestamp(q.Since!.Value));
    }

    [Theory]
    [InlineData("root", "2")]
    [InlineData("root", "-1")]
    [InlineData("root", "a")]
    [InlineData("root", "")]
    [InlineData("glob", "")]
    [InlineData("glob", @"sub\*.trc")]
    [InlineData("glob", "*/x")]
    [InlineData("glob", "C:*")]
    [InlineData("glob", "a\nb")]
    [InlineData("since", "yesterday")]
    [InlineData("since", "21/11/2026")]
    [InlineData("since", "")]
    [InlineData("limit", "0")]
    [InlineData("limit", "201")]
    [InlineData("limit", "ten")]
    public void ParseList_BadRequests(string key, string value)
    {
        var f = LogService.ParseList(Q((key, value)), 2).Failure!;
        Assert.Equal("bad_request", f.Error.Code);
        Assert.Equal(key, ((ParameterDetails)f.Error.Details!).Parameter);
    }

    [Fact]
    public void ParseList_GlobOf200_Ok()
    {
        Assert.True(LogService.ParseList(Q(("glob", new string('*', 200))), 1).Ok);
        Assert.False(LogService.ParseList(Q(("glob", new string('*', 201))), 1).Ok);
    }

    // ------------------------------------------------------------------ listing

    private static LogDirEntry F(string name, int minutes, long size = 10) => new(name, false, false, size, T0.AddMinutes(minutes));

    private static LogDirEntry D(string name, bool reparse = false) => new(name, true, reparse, 0, T0);

    [Fact]
    public void List_NewestFirst_AcrossRoots()
    {
        var (svc, fs) = Make(Trace, Work);
        fs.Listings[Trace] = [F("api.trc", 5), F("ifh.trc", 10)];
        fs.Listings[Work] = [F("NCSTRACE.TRC", 7)];

        var r = svc.List(new LogListQuery(null, null, null, 50));

        Assert.Equal([new LogRoot(0, Trace, true), new LogRoot(1, Work, true)], r.Roots);
        Assert.Equal([@"C:\EDIABAS\TRACE\ifh.trc", @"C:\NCSEXPER\WORK\NCSTRACE.TRC", @"C:\EDIABAS\TRACE\api.trc"], r.Files.Select(f => f.Path));
        Assert.Equal([0, 1, 0], r.Files.Select(f => f.Root));
        Assert.Equal("2026-11-21T12:10:00Z", r.Files[0].ModifiedAt);
        Assert.False(r.Truncated);
    }

    [Fact]
    public void List_OneRoot_StillShowsEveryRoot()
    {
        var (svc, fs) = Make(Trace, Work);
        fs.Listings[Trace] = [F("api.trc", 5)];
        fs.Listings[Work] = [F("NCSTRACE.TRC", 7)];

        var r = svc.List(new LogListQuery(1, null, null, 50));

        Assert.Equal(2, r.Roots.Count);
        Assert.Equal([@"C:\NCSEXPER\WORK\NCSTRACE.TRC"], r.Files.Select(f => f.Path));
        Assert.DoesNotContain(Trace, fs.Listed);
    }

    [Fact]
    public void List_MissingRoot()
    {
        var fs = new FakeLogFileSystem().Dir(Trace);
        var svc = new LogService([Trace, Work], fs);
        fs.Listings[Trace] = [F("api.trc", 5)];

        var r = svc.List(new LogListQuery(null, null, null, 50));
        Assert.Equal([new LogRoot(0, Trace, true), new LogRoot(1, Work, false)], r.Roots);
        Assert.Single(r.Files);
        Assert.DoesNotContain(Work, fs.Listed);
    }

    [Fact]
    public void List_DepthThree_NoReparsePoints()
    {
        var (svc, fs) = Make(Trace);
        fs.Listings[Trace] = [F("0.trc", 0), D("a"), D("jn", reparse: true), new LogDirEntry("link.trc", false, true, 99, T0)];
        fs.Listings[Trace + @"\a"] = [F("1.trc", 1), D("b")];
        fs.Listings[Trace + @"\a\b"] = [F("2.trc", 2), D("c")];
        fs.Listings[Trace + @"\a\b\c"] = [F("3.trc", 3), D("d")];
        fs.Listings[Trace + @"\a\b\c\d"] = [F("4.trc", 4)];
        fs.Listings[Trace + @"\jn"] = [F("secret.txt", 9)];

        var r = svc.List(new LogListQuery(null, null, null, 50));

        Assert.Equal([@"C:\EDIABAS\TRACE\a\b\c\3.trc", @"C:\EDIABAS\TRACE\a\b\2.trc", @"C:\EDIABAS\TRACE\a\1.trc", @"C:\EDIABAS\TRACE\0.trc"],
            r.Files.Select(f => f.Path));
        Assert.DoesNotContain(Trace + @"\jn", fs.Listed);
        Assert.DoesNotContain(Trace + @"\a\b\c\d", fs.Listed);
    }

    [Fact]
    public void List_SkipsNamesTheReaderWouldRefuse()
    {
        var (svc, fs) = Make(Trace);
        fs.Listings[Trace] = [F("ok.trc", 1), F("con", 2), F("x.", 3), F("..", 4), F("a:b", 5)];
        var r = svc.List(new LogListQuery(null, null, null, 50));
        Assert.Equal([@"C:\EDIABAS\TRACE\ok.trc"], r.Files.Select(f => f.Path));
    }

    [Fact]
    public void List_GlobSinceLimit()
    {
        var (svc, fs) = Make(Trace);
        fs.Listings[Trace] = [F("api.trc", 1), F("API2.TRC", 2), F("ifh.trc", 3), F("notes.txt", 4), F("old.trc", -60)];

        var glob = svc.List(new LogListQuery(null, "a*.trc", null, 50));
        Assert.Equal([@"C:\EDIABAS\TRACE\API2.TRC", @"C:\EDIABAS\TRACE\api.trc"], glob.Files.Select(f => f.Path));

        var since = svc.List(new LogListQuery(null, "*.trc", T0, 50));
        Assert.Equal(3, since.Files.Count);
        Assert.DoesNotContain(since.Files, f => f.Path.EndsWith("old.trc"));

        var limited = svc.List(new LogListQuery(null, null, null, 2));
        Assert.Equal([@"C:\EDIABAS\TRACE\notes.txt", @"C:\EDIABAS\TRACE\ifh.trc"], limited.Files.Select(f => f.Path));
        Assert.True(limited.Truncated);
    }

    [Fact]
    public void List_StopsAtTheScanBudget()
    {
        var (svc, fs) = Make(Trace);
        fs.Listings[Trace] = Enumerable.Range(0, LogService.MaxScannedEntries + 5).Select(i => F($"f{i}.trc", 0)).ToList();
        var r = svc.List(new LogListQuery(null, "nomatch", null, 50));
        Assert.Empty(r.Files);
        Assert.True(r.Truncated);
    }

    // ------------------------------------------------------------------ glob

    [Theory]
    [InlineData("api.trc", "*.trc", true)]
    [InlineData("API.TRC", "*.trc", true)]
    [InlineData("api.trc", "api.???", true)]
    [InlineData("api.trc", "api.??", false)]
    [InlineData("api.trc", "*", true)]
    [InlineData("api.trc", "**", true)]
    [InlineData("api.trc", "a*c", true)]
    [InlineData("api.trc", "*api*", true)]
    [InlineData("api.trc", "*.txt", false)]
    [InlineData("api.trc.bak", "*.trc", false)]
    [InlineData("api.trc", "api.trc", true)]
    [InlineData("api.trc", "api.tr", false)]
    [InlineData("", "*", true)]
    [InlineData("a", "", false)]
    [InlineData("a[1].trc", "a[1]*", true)]
    [InlineData("übung.trc", "ÜBUNG.*", true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaab", "*a*a*a*a*a*a*a*a*c", false)]
    public void Glob_Matches(string name, string pattern, bool expected)
    {
        Assert.Equal(expected, Glob.IsMatch(name, pattern));
    }
}
