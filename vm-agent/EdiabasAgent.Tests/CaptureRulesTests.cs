using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

public class CaptureRulesTests
{
    private static QueryValues Q(params (string Key, string Value)[] pairs) =>
        new(pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));

    private static WindowInfo W(string handle, string title, bool minimized = false) =>
        new(handle, title, "INPALOAD", 1, new Bounds(0, 0, 100, 100), minimized);

    // ------------------------------------------------------------------ parameters

    [Fact]
    public void Parse_Defaults()
    {
        Assert.Equal(new CaptureRequest(CaptureTarget.Desktop, 1600, CaptureFormat.Jpeg), CaptureRules.Parse(Q()).Value);
    }

    [Theory]
    [InlineData("desktop", CaptureTargetKind.Desktop, 0u, null)]
    [InlineData("Desktop", CaptureTargetKind.Desktop, 0u, null)]
    [InlineData("0x000A0B2C", CaptureTargetKind.Handle, 0x000A0B2Cu, null)]
    [InlineData("0xa0b2c", CaptureTargetKind.Handle, 0x000A0B2Cu, null)]
    [InlineData("0x1", CaptureTargetKind.Handle, 1u, null)]
    [InlineData("0xFFFFFFFF", CaptureTargetKind.Handle, 0xFFFFFFFFu, null)]
    [InlineData("INPA", CaptureTargetKind.Title, 0u, "INPA")]
    [InlineData("0x", CaptureTargetKind.Title, 0u, "0x")]
    [InlineData("0xZZ", CaptureTargetKind.Title, 0u, "0xZZ")]
    [InlineData("0X1F", CaptureTargetKind.Title, 0u, "0X1F")]
    [InlineData(" 0x1F", CaptureTargetKind.Title, 0u, " 0x1F")]
    [InlineData("desktop window", CaptureTargetKind.Title, 0u, "desktop window")]
    [InlineData("NCS Expert ü", CaptureTargetKind.Title, 0u, "NCS Expert ü")]
    public void Parse_Window(string window, CaptureTargetKind kind, uint handle, string? title)
    {
        var target = CaptureRules.Parse(Q(("window", window))).Value!.Target;
        Assert.Equal(new CaptureTarget(kind, handle, title), target);
    }

    [Theory]
    [InlineData("window", "")]
    [InlineData("window", "0x123456789")]
    [InlineData("window", "0x0000000000000001")]
    [InlineData("window", "INPA\n")]
    [InlineData("window", "a\u0000")]
    [InlineData("window", "a\u2028")]
    [InlineData("maxEdge", "199")]
    [InlineData("maxEdge", "4001")]
    [InlineData("maxEdge", "1600.5")]
    [InlineData("maxEdge", "-1600")]
    [InlineData("maxEdge", "")]
    [InlineData("format", "gif")]
    [InlineData("format", "JPEG")]
    [InlineData("format", "jpg")]
    [InlineData("format", "")]
    public void Parse_BadRequests(string key, string value)
    {
        var f = CaptureRules.Parse(Q((key, value))).Failure!;
        Assert.Equal("bad_request", f.Error.Code);
        Assert.Equal(key, ((ParameterDetails)f.Error.Details!).Parameter);
    }

    [Fact]
    public void Parse_WindowLength()
    {
        Assert.True(CaptureRules.Parse(Q(("window", new string('a', 256)))).Ok);
        Assert.False(CaptureRules.Parse(Q(("window", new string('a', 257)))).Ok);
    }

    [Theory]
    [InlineData("200", 200)]
    [InlineData("4000", 4000)]
    [InlineData("1024", 1024)]
    public void Parse_MaxEdge(string value, int expected)
    {
        Assert.Equal(expected, CaptureRules.Parse(Q(("maxEdge", value))).Value!.MaxEdge);
    }

    [Fact]
    public void Parse_Png()
    {
        Assert.Equal(CaptureFormat.Png, CaptureRules.Parse(Q(("format", "png"))).Value!.Format);
        Assert.Equal(CaptureFormat.Jpeg, CaptureRules.Parse(Q(("format", "jpeg"))).Value!.Format);
    }

    // ------------------------------------------------------------------ which window

    private static readonly IReadOnlyList<WindowInfo> Windows =
    [
        W("0x00010001", "INPA - BMW Group"),
        W("0x00020002", "Tool32 - inpa helper", minimized: true),
        W("0x00030003", "inpa second"),
        W("0x00040004", "NCS Expert"),
    ];

    [Fact]
    public void FindWindow_TitleSubstring_FrontMostWins()
    {
        Assert.Equal("0x00010001", CaptureRules.FindWindow(Windows, new CaptureTarget(CaptureTargetKind.Title, 0, "inpa")).Value!.Handle);
        Assert.Equal("0x00040004", CaptureRules.FindWindow(Windows, new CaptureTarget(CaptureTargetKind.Title, 0, "ncs EXPERT")).Value!.Handle);
    }

    [Fact]
    public void FindWindow_Handle()
    {
        Assert.Equal("inpa second", CaptureRules.FindWindow(Windows, new CaptureTarget(CaptureTargetKind.Handle, 0x30003, null)).Value!.Title);
    }

    [Fact]
    public void FindWindow_UnknownHandle_NotFound()
    {
        // Only a visible, titled top-level window from the list can be captured, not any HWND.
        var f = CaptureRules.FindWindow(Windows, new CaptureTarget(CaptureTargetKind.Handle, 0x12345, null)).Failure!;
        Assert.Equal((404, "window_not_found"), (f.Status, f.Error.Code));
        Assert.Null(f.Error.Details);
    }

    [Fact]
    public void FindWindow_NoTitleMatch_NotFound()
    {
        var f = CaptureRules.FindWindow(Windows, new CaptureTarget(CaptureTargetKind.Title, 0, "WinKFP")).Failure!;
        Assert.Equal("window_not_found", f.Error.Code);
    }

    [Fact]
    public void FindWindow_Minimised_Refused()
    {
        var f = CaptureRules.FindWindow(Windows, new CaptureTarget(CaptureTargetKind.Title, 0, "tool32")).Failure!;
        Assert.Equal((409, "window_minimized"), (f.Status, f.Error.Code));
        Assert.Equal(new WindowDetails("0x00020002", "Tool32 - inpa helper"), f.Error.Details);

        f = CaptureRules.FindWindow(Windows, new CaptureTarget(CaptureTargetKind.Handle, 0x20002, null)).Failure!;
        Assert.Equal("window_minimized", f.Error.Code);
    }

    [Fact]
    public void FindWindow_EmptyList()
    {
        Assert.Equal("window_not_found", CaptureRules.FindWindow([], new CaptureTarget(CaptureTargetKind.Title, 0, "x")).Failure!.Error.Code);
    }

    [Theory]
    [InlineData("0x000A0B2C", 0x000A0B2Cu)]
    [InlineData("0xFFFFFFFF", 0xFFFFFFFFu)]
    [InlineData("0x", null)]
    [InlineData("0x123456789", null)]
    [InlineData("000A0B2C", null)]
    [InlineData("desktop", null)]
    public void ParseHandle(string handle, uint? expected)
    {
        Assert.Equal(expected, CaptureRules.ParseHandle(handle));
    }

    [Fact]
    public void ParseHandle_RoundTripsApiFormat()
    {
        foreach (var h in new uint[] { 0, 1, 0xA0B2C, 0x7FFFFFFF, 0xFFFFFFFF })
        {
            Assert.Equal(h, CaptureRules.ParseHandle(ApiFormat.Handle(h)));
        }
    }

    // ------------------------------------------------------------------ sizes

    [Theory]
    [InlineData(1280, 800, 1600, 1280, 800)] // never scaled up
    [InlineData(1600, 900, 1600, 1600, 900)]
    [InlineData(3200, 1800, 1600, 1600, 900)]
    [InlineData(1800, 3200, 1600, 900, 1600)]
    [InlineData(2000, 2000, 1600, 1600, 1600)]
    [InlineData(1921, 1081, 200, 200, 113)]
    [InlineData(10000, 1, 200, 200, 1)] // never below one pixel
    [InlineData(1, 10000, 200, 1, 200)]
    [InlineData(1, 1, 4000, 1, 1)]
    public void Fit(int w, int h, int maxEdge, int ew, int eh)
    {
        Assert.Equal((ew, eh), CaptureRules.Fit(w, h, maxEdge));
    }

    [Fact]
    public void Fit_NoArea_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CaptureRules.Fit(0, 10, 1600));
        Assert.Throws<ArgumentOutOfRangeException>(() => CaptureRules.Fit(10, -1, 1600));
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(1920, 1080, true)]
    [InlineData(7680, 4320, true)]
    [InlineData(8192, 100, true)]
    [InlineData(8193, 100, false)]
    [InlineData(8192, 8192, false)]
    [InlineData(0, 100, false)]
    [InlineData(-5, 100, false)]
    public void UsableSourceSize(int w, int h, bool expected)
    {
        Assert.Equal(expected, CaptureRules.UsableSourceSize(w, h));
    }

    [Fact]
    public void VisiblePart_CropsInvisibleBorders()
    {
        var rect = new PixelRect(93, 43, 1307, 857); // window rect with 7 px invisible borders
        var frame = new PixelRect(100, 43, 1300, 850);
        Assert.Equal(new PixelRect(7, 0, 1207, 807), CaptureRules.VisiblePart(rect, frame));
    }

    [Fact]
    public void VisiblePart_FallsBackToWholeImage()
    {
        var rect = new PixelRect(-8, -8, 1928, 1048);
        var whole = new PixelRect(0, 0, 1936, 1056);
        Assert.Equal(whole, CaptureRules.VisiblePart(rect, null));
        Assert.Equal(whole, CaptureRules.VisiblePart(rect, new PixelRect(-20, 0, 100, 100))); // outside the window
        Assert.Equal(whole, CaptureRules.VisiblePart(rect, new PixelRect(10, 10, 10, 100))); // no area
    }

    // ------------------------------------------------------------------ desktop, pixels, headers

    [Theory]
    [InlineData(1, "Default", true)]
    [InlineData(2, "default", true)]
    [InlineData(1, "Winlogon", false)]
    [InlineData(1, "Screen-saver", false)]
    [InlineData(1, null, false)]
    [InlineData(0, "Default", false)]
    public void DesktopAvailable(int session, string? name, bool expected)
    {
        Assert.Equal(expected, CaptureRules.DesktopAvailable(session, name));
    }

    [Fact]
    public void DesktopUnavailable_Shape()
    {
        var f = CaptureRules.DesktopUnavailable("locked");
        Assert.Equal((503, "desktop_unavailable"), (f.Status, f.Error.Code));
        Assert.Null(f.Error.Details);
    }

    // Three different answers: only a locked screen, secure desktop or session 0 tells Ryan to
    // unlock the VM. A capture queued too long and a failed GDI copy say what happened.
    [Fact]
    public void CaptureFailures_MapToTheirOwnCodes()
    {
        var locked = CaptureRules.DesktopUnavailable("the screen is locked");
        Assert.Equal((503, "desktop_unavailable"), (locked.Status, locked.Error.Code));
        Assert.Contains("Unlock the VM", locked.Error.Message);

        var busy = CaptureRules.Busy();
        Assert.Equal((503, "capture_busy"), (busy.Status, busy.Error.Code));
        Assert.Contains("another capture is still running", busy.Error.Message);
        Assert.DoesNotContain("Unlock", busy.Error.Message);
        Assert.Null(busy.Error.Details);

        var failed = CaptureRules.Failed("the screen could not be read");
        Assert.Equal((500, "capture_failed"), (failed.Status, failed.Error.Code));
        Assert.Contains("the screen could not be read", failed.Error.Message);
        Assert.DoesNotContain("Unlock", failed.Error.Message);
        Assert.Null(failed.Error.Details);
    }

    [Fact]
    public void IsAllBlack()
    {
        Assert.True(CaptureRules.IsAllBlack(new byte[16]));
        Assert.True(CaptureRules.IsAllBlack([0, 0, 0, 255, 0, 0, 0, 7])); // the unused fourth byte does not count
        Assert.False(CaptureRules.IsAllBlack([0, 0, 0, 0, 0, 1, 0, 0]));
        Assert.True(CaptureRules.IsAllBlack([]));
    }

    [Theory]
    [InlineData("desktop", "desktop")]
    [InlineData("INPA - BMW Group", "INPA%20-%20BMW%20Group")]
    [InlineData("Fehlerspeicher lesen ü", "Fehlerspeicher%20lesen%20%C3%BC")]
    [InlineData("a\r\nb", "a%0D%0Ab")]
    [InlineData("100%", "100%25")]
    [InlineData("a+b/c?d#e&f", "a%2Bb%2Fc%3Fd%23e%26f")]
    [InlineData("€", "%E2%82%AC")]
    [InlineData("", "")]
    public void PercentEncode(string title, string expected)
    {
        Assert.Equal(expected, CaptureRules.PercentEncode(title));
        Assert.Equal(title, Uri.UnescapeDataString(expected));
    }

    [Fact]
    public void PercentEncode_IsAsciiOnly_EvenForBrokenSurrogates()
    {
        var encoded = CaptureRules.PercentEncode("a\uD800b\uDFFF😀");
        Assert.All(encoded, c => Assert.True(c is >= '!' and <= '~'));
        Assert.Equal("a�b�😀", Uri.UnescapeDataString(encoded));
    }
}
