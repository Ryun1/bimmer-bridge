using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

public class PathRulesTests
{
    [Theory]
    [InlineData(@"C:\EDIABAS\TRACE\api.trc")]
    [InlineData(@"c:\ediabas\trace\API.TRC")]
    [InlineData(@"C:\EDIABAS\TRACE\sub\deeper\ifh.trc")]
    [InlineData(@"C:\NCSEXPER\WORK\NCSTRACE.TRC")]
    [InlineData(@"D:\logs\a")]
    [InlineData(@"C:\EDIABA~1\TRACE\api.trc")] // a short name is fine as text; where it resolves decides
    [InlineData(@"C:\EDIABAS\TRACE\.hidden")]
    [InlineData(@"C:\EDIABAS\TRACE\a..b.trc")]
    [InlineData(@"C:\EDIABAS\TRACE\console.log")] // CON only as a whole stem
    [InlineData(@"C:\EDIABAS\TRACE\COM10.trc")]
    [InlineData(@"C:\EDIABAS\TRACE\nul_trace.trc")]
    [InlineData(@"C:\EDIABAS\TRACE\Spur ä ö ü.trc")]
    public void Accepted(string path)
    {
        Assert.Null(PathRules.Check(path));
    }

    [Theory]
    // empty
    [InlineData("", PathRules.Empty)]
    // relative and drive-relative
    [InlineData(@"api.trc", PathRules.Relative)]
    [InlineData(@"EDIABAS\TRACE\api.trc", PathRules.Relative)]
    [InlineData(@"C:api.trc", PathRules.Relative)]
    [InlineData(@"C:", PathRules.Relative)]
    [InlineData(@"C:\", PathRules.Relative)]
    [InlineData(@"\EDIABAS\TRACE\api.trc", PathRules.Relative)]
    [InlineData(@"1:\x", PathRules.Relative)]
    [InlineData(@"CC:\x", PathRules.Relative)]
    // UNC and device paths
    [InlineData(@"\\server\share\api.trc", PathRules.UncOrDevice)]
    [InlineData(@"\\?\C:\EDIABAS\TRACE\api.trc", PathRules.UncOrDevice)]
    [InlineData(@"\\.\C:\EDIABAS\TRACE\api.trc", PathRules.UncOrDevice)]
    [InlineData(@"\\.\PhysicalDrive0", PathRules.UncOrDevice)]
    [InlineData(@"\\.\pipe\x", PathRules.UncOrDevice)]
    [InlineData(@"\??\C:\EDIABAS\TRACE\api.trc", PathRules.UncOrDevice)]
    [InlineData(@"\\?\UNC\server\share\x", PathRules.UncOrDevice)]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\x", PathRules.UncOrDevice)]
    [InlineData(@"//server/share/x", PathRules.UncOrDevice)]
    // forward slashes anywhere else
    [InlineData(@"C:/EDIABAS/TRACE/api.trc", PathRules.ForwardSlash)]
    [InlineData(@"C:\EDIABAS\TRACE/api.trc", PathRules.ForwardSlash)]
    [InlineData(@"C:\EDIABAS\TRACE\../../Windows/win.ini", PathRules.ForwardSlash)]
    // alternate data streams and any later colon
    [InlineData(@"C:\EDIABAS\TRACE\api.trc:secret", PathRules.AlternateDataStream)]
    [InlineData(@"C:\EDIABAS\TRACE\api.trc::$DATA", PathRules.AlternateDataStream)]
    [InlineData(@"C:\EDIABAS\TRACE\api.trc:secret:$DATA", PathRules.AlternateDataStream)]
    [InlineData(@"C:\EDIABAS\TRACE:x\api.trc", PathRules.AlternateDataStream)]
    [InlineData(@"C:\EDIABAS\TRACE\D:\x", PathRules.AlternateDataStream)]
    // wildcards and characters Windows forbids in names
    [InlineData(@"C:\EDIABAS\TRACE\*.trc", PathRules.InvalidCharacter)]
    [InlineData(@"C:\EDIABAS\TRACE\api?.trc", PathRules.InvalidCharacter)]
    [InlineData(@"C:\EDIABAS\TRACE\<x", PathRules.InvalidCharacter)]
    [InlineData(@"C:\EDIABAS\TRACE\a>b", PathRules.InvalidCharacter)]
    [InlineData("C:\\EDIABAS\\TRACE\\a\"b", PathRules.InvalidCharacter)]
    [InlineData(@"C:\EDIABAS\TRACE\a|b", PathRules.InvalidCharacter)]
    // empty segments, trailing separator
    [InlineData(@"C:\EDIABAS\\TRACE\api.trc", PathRules.EmptySegment)]
    [InlineData(@"C:\EDIABAS\TRACE\", PathRules.EmptySegment)]
    [InlineData(@"C:\\EDIABAS", PathRules.EmptySegment)]
    // dot segments
    [InlineData(@"C:\EDIABAS\TRACE\..\..\Windows\win.ini", PathRules.DotSegment)]
    [InlineData(@"C:\EDIABAS\TRACE\..", PathRules.DotSegment)]
    [InlineData(@"C:\EDIABAS\.\TRACE\api.trc", PathRules.DotSegment)]
    [InlineData(@"C:\..\Windows\win.ini", PathRules.DotSegment)]
    [InlineData(@"C:\EDIABAS\TRACE\...\x", PathRules.DotSegment)]
    // trailing dots or spaces (Windows strips them, so the name means something else)
    [InlineData(@"C:\EDIABAS\TRACE\api.trc.", PathRules.TrailingDotOrSpace)]
    [InlineData(@"C:\EDIABAS\TRACE\api.trc ", PathRules.TrailingDotOrSpace)]
    [InlineData(@"C:\EDIABAS\TRACE. \api.trc", PathRules.TrailingDotOrSpace)]
    [InlineData(@"C:\EDIABAS\TRACE \api.trc", PathRules.TrailingDotOrSpace)]
    [InlineData(@"C:\EDIABAS\TRACE\.. ", PathRules.TrailingDotOrSpace)]
    [InlineData(@"C:\EDIABAS\TRACE\ ", PathRules.TrailingDotOrSpace)]
    // DOS device names, any case, with or without extension, in any segment
    [InlineData(@"C:\EDIABAS\TRACE\CON", PathRules.DeviceName)]
    [InlineData(@"C:\EDIABAS\TRACE\con", PathRules.DeviceName)]
    [InlineData(@"C:\EDIABAS\TRACE\NUL.trc", PathRules.DeviceName)]
    [InlineData(@"C:\EDIABAS\TRACE\nul.tar.gz", PathRules.DeviceName)]
    [InlineData(@"C:\EDIABAS\TRACE\Aux.log", PathRules.DeviceName)]
    [InlineData(@"C:\EDIABAS\TRACE\PRN", PathRules.DeviceName)]
    [InlineData(@"C:\EDIABAS\TRACE\COM1", PathRules.DeviceName)]
    [InlineData(@"C:\EDIABAS\TRACE\com9.txt", PathRules.DeviceName)]
    [InlineData(@"C:\EDIABAS\TRACE\COM0", PathRules.DeviceName)]
    [InlineData(@"C:\EDIABAS\TRACE\LPT1.x", PathRules.DeviceName)]
    [InlineData(@"C:\EDIABAS\TRACE\lpt9", PathRules.DeviceName)]
    [InlineData("C:\\EDIABAS\\TRACE\\COM\u00B9", PathRules.DeviceName)]
    [InlineData("C:\\EDIABAS\\TRACE\\LPT\u00B3.log", PathRules.DeviceName)]
    [InlineData(@"C:\EDIABAS\TRACE\CONIN$", PathRules.DeviceName)]
    [InlineData(@"C:\EDIABAS\TRACE\conout$.txt", PathRules.DeviceName)]
    [InlineData(@"C:\EDIABAS\TRACE\CLOCK$", PathRules.DeviceName)]
    [InlineData(@"C:\EDIABAS\TRACE\nul .trc", PathRules.DeviceName)]
    [InlineData(@"C:\NUL\api.trc", PathRules.DeviceName)]
    [InlineData(@"C:\EDIABAS\com1.dir\api.trc", PathRules.DeviceName)]
    // control characters, including NUL, DEL, C1 and Unicode line breaks
    [InlineData("C:\\EDIABAS\\TRACE\\api.trc\0.txt", PathRules.ControlCharacter)]
    [InlineData("C:\\EDIABAS\\TRACE\\api\n.trc", PathRules.ControlCharacter)]
    [InlineData("C:\\EDIABAS\\TRACE\\api\t.trc", PathRules.ControlCharacter)]
    [InlineData("C:\\EDIABAS\\TRACE\\api\u007F.trc", PathRules.ControlCharacter)]
    [InlineData("C:\\EDIABAS\\TRACE\\api\u0085.trc", PathRules.ControlCharacter)]
    [InlineData("C:\\EDIABAS\\TRACE\\api\u2028.trc", PathRules.ControlCharacter)]
    [InlineData("\0C:\\x", PathRules.ControlCharacter)]
    public void Refused(string path, string reason)
    {
        Assert.Equal(reason, PathRules.Check(path));
    }

    [Fact]
    public void Null_IsEmpty()
    {
        Assert.Equal(PathRules.Empty, PathRules.Check(null));
    }

    [Fact]
    public void OverLong_Refused()
    {
        var ok = @"C:\" + new string('a', PathRules.MaxPathLength - 3);
        Assert.Equal(PathRules.MaxPathLength, ok.Length);
        Assert.Equal(PathRules.TooLong, PathRules.Check(@"C:\" + new string('a', 255) + @"\" + new string('b', 255)));
        Assert.Equal(PathRules.TooLong, PathRules.Check(ok + "x"));
        Assert.Equal(PathRules.TooLong, PathRules.Check(new string('\\', 10000)));
        Assert.Equal(PathRules.TooLong, PathRules.CheckSegment(new string('a', 256)));
    }

    [Theory]
    [InlineData(@"C:\EDIABAS\TRACE\api.trc", @"C:\EDIABAS\TRACE", true)]
    [InlineData(@"c:\ediabas\trace\API.trc", @"C:\EDIABAS\TRACE", true)]
    [InlineData(@"C:\EDIABAS\TRACE\a\b\c\d.trc", @"C:\EDIABAS\TRACE", true)]
    [InlineData(@"C:\EDIABAS\TRACE2\api.trc", @"C:\EDIABAS\TRACE", false)] // not a folder boundary
    [InlineData(@"C:\EDIABAS\TRACEapi.trc", @"C:\EDIABAS\TRACE", false)]
    [InlineData(@"C:\EDIABAS\TRACE", @"C:\EDIABAS\TRACE", false)] // the root itself is no file under it
    [InlineData(@"C:\EDIABAS\api.trc", @"C:\EDIABAS\TRACE", false)]
    [InlineData(@"D:\EDIABAS\TRACE\api.trc", @"C:\EDIABAS\TRACE", false)]
    [InlineData(@"C:\EDIABAS\TRACE\..\BIN\EDIABAS.INI", @"C:\EDIABAS\TRACE", false)] // text rules apply too
    [InlineData(@"C:\EDIABAS\TRACE\api.trc:x", @"C:\EDIABAS\TRACE", false)]
    [InlineData(@"C:\Windows\win.ini", @"C:\", false)] // a drive is never a root
    [InlineData(@"C:\EDIABAS\TRACE\api.trc", @"C:\EDIABAS\TRACE\", false)] // roots are trimmed before use
    public void IsUnder(string path, string root, bool expected)
    {
        Assert.Equal(expected, PathRules.IsUnder(path, root));
    }

    [Theory]
    [InlineData(@"\\?\C:\EDIABAS\TRACE\api.trc", @"C:\EDIABAS\TRACE\api.trc")]
    [InlineData(@"C:\EDIABAS\TRACE\api.trc", @"C:\EDIABAS\TRACE\api.trc")]
    [InlineData(@"\\?\UNC\server\share\api.trc", null)]
    [InlineData(@"\\?\Volume{0b1e2c3d-0000-0000-0000-100000000000}\x.trc", null)]
    [InlineData(@"\\?\GLOBALROOT\Device\Serial0", null)]
    [InlineData(@"\\?\C:\EDIABAS\TRACE\api.trc:stream", null)]
    [InlineData(@"\\?\C:\EDIABAS\TRACE\con", null)] // a file named like a device, made through \\?\
    [InlineData(@"\\?\C:\EDIABAS\TRACE\x.", null)]
    [InlineData(@"\\?\C:\", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeFinal(string? final, string? expected)
    {
        Assert.Equal(expected, PathRules.NormalizeFinal(final));
    }

    [Theory]
    [InlineData(@"C:\EDIABAS\TRACE\", @"C:\EDIABAS\TRACE")]
    [InlineData(@"C:\EDIABAS\TRACE\\", @"C:\EDIABAS\TRACE")]
    [InlineData(@"C:\EDIABAS\TRACE", @"C:\EDIABAS\TRACE")]
    [InlineData(@"C:\", @"C:\")]
    public void TrimRoot(string root, string expected)
    {
        Assert.Equal(expected, PathRules.TrimRoot(root));
    }
}
