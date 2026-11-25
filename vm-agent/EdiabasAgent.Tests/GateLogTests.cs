using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

public class GateLogTests
{
    [Theory]
    [InlineData(null, GateLog.DefaultPath)]
    [InlineData("", GateLog.DefaultPath)]
    [InlineData("Mode=log\r\n", GateLog.DefaultPath)]
    [InlineData("Mode=log\r\nLogPath=D:\\x\\g.log\r\n", "D:\\x\\g.log")]
    [InlineData("\uFEFF[XGate]\r\n logpath = D:\\a.log \r\n", "D:\\a.log")]
    [InlineData("LogPath=D:\\a.log\nLogPath=D:\\b.log", "D:\\b.log")]
    [InlineData("LogPath=D:\\a.log\nLogPath=", GateLog.DefaultPath)]
    [InlineData("; LogPath=D:\\a.log\n# LogPath=D:\\b.log", GateLog.DefaultPath)]
    public void LogPath_AsTheGateReadsIt(string? ini, string expected) =>
        Assert.Equal(expected, GateLog.LogPathFrom(ini));

    [Fact]
    public void IniBesideDll() =>
        Assert.Equal(@"C:\EDIABAS\BIN\XGate.ini", GateLog.IniBesideDll(@"C:\EDIABAS\BIN\api32.dll"));

    // Lines from the VM's XGate.log, desk run 2026-11-25 (gate in enforce mode).
    private const string Desk =
        "2026-11-25 11:48:52.447 CALL fkt=14 ifhGetPowerState wParam=0 ch=1 len=0 data=- concept=010F verdict=allow fwd=yes rc=0 outLen=8 out=FFFFFFFFFFFFFFFF\r\n" +
        "2026-11-25 11:48:52.905 REFUSED service fkt=29 frame=8457F130910100\r\n" +
        "2026-11-25 11:48:52.905 LATCHED\r\n" +
        "2026-11-25 11:48:52.905 CALL fkt=29 ifhSendTelegram wParam=0 ch=1 len=7 data=8457F130910100 concept=010F verdict=refuse:service fwd=no rc=-1\r\n";

    [Fact]
    public void Parse_TheDeskRefusal()
    {
        var s = GateLog.Parse(Desk);
        Assert.Equal(2, s.Calls);
        var r = Assert.Single(s.Refusals);
        Assert.Equal(new GateLog.Refusal(false, "service", 29, "84 57 F1 30 91 01 00"), r);
    }

    [Fact]
    public void Parse_LogModeAndOddFrames()
    {
        var s = GateLog.Parse(
            "2026-11-25 10:00:00.000 WOULD_REFUSE function fkt=41 frame=00\n" +
            "2026-11-25 10:00:00.001 REFUSED function fkt=54 frame=-\n" +
            "2026-11-25 10:00:00.002 REFUSED malformed fkt=29 frame=(null)\n" +
            "2026-11-25 10:00:00.003 REFUSED service fkt=29 frame=8457+300\n" +
            "REFUSED service fkt=29 frame=00\n");
        Assert.Equal(
            [
                new GateLog.Refusal(true, "function", 41, "00"),
                new GateLog.Refusal(false, "function", 54, null),
                new GateLog.Refusal(false, "malformed", 29, "(null)"),
                new GateLog.Refusal(false, "service", 29, "8457+300"),
            ],
            s.Refusals);
        Assert.Equal(0, s.Calls);
    }
}
