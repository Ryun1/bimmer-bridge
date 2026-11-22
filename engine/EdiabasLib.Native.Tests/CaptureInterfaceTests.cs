using EdiabasLib.Native;

namespace EdiabasLib.Native.Tests;

public class CaptureInterfaceTests
{
    [Fact] // cmd_test2's TEST_IFACE_FLAGS sets BMW-FAST 115200 (cmd_test2.b2v:5331)
    public void CapturesXsetparFromTestIfaceFlags()
    {
        var cap = new CaptureInterface();
        using var ed = EdiabasFactory.Create(TestPaths.UpstreamTestEcu, cap, noInitForVJobs: true);
        ed.ResolveSgbdFile("cmd_test2");
        try { ed.ExecuteJob("TEST_IFACE_FLAGS"); } catch (Exception) { /* CaptureStop or job error after xsetpar */ }
        Assert.Equal(new uint[] { 0x010F, 115200, 1200, 20, 10, 2, 5000 }, cap.LastCommParameter);
    }

    [Fact]
    public void TestIfaceFlags_StopsAtTheFirstSend_AndRecordsIt()
    {
        var cap = new CaptureInterface();
        using var ed = EdiabasFactory.Create(TestPaths.UpstreamTestEcu, cap, noInitForVJobs: true);
        ed.ResolveSgbdFile("cmd_test2");
        var ex = Assert.ThrowsAny<Exception>(() => ed.ExecuteJob("TEST_IFACE_FLAGS"));
        Assert.Contains(Chain(ex), e => e is CaptureStopException);
        Assert.Equal(1, cap.ParameterSets);
        Assert.Contains("BatteryVoltage", cap.Calls);
        Assert.Equal("TransmitData", cap.Calls[^1]);
    }

    [Fact]
    public void Connected_OnlyAfterInterfaceConnect()
    {
        var cap = new CaptureInterface();
        Assert.False(cap.Connected);
        Assert.True(cap.InterfaceConnect());
        Assert.True(cap.Connected);
    }

    static IEnumerable<Exception> Chain(Exception e) { for (; e != null; e = e.InnerException) yield return e; }
}
