using EdiabasLib.Native;

namespace EdiabasLib.Native.Tests;

public class OfflineInterfaceTests
{
    [Fact]
    public void JobsOnUpstreamTestSgbd_ListsTestIfaceFlags()
    {
        using var ed = EdiabasFactory.Create(TestPaths.UpstreamTestEcu, new OfflineInterface(), noInitForVJobs: true);
        ed.ResolveSgbdFile("cmd_test2");
        ed.ExecuteJob("_JOBS");
        var names = ed.ResultSets.Skip(1).Select(s => (string)s["JOBNAME"].OpData).ToList();
        Assert.Contains("TEST_IFACE_FLAGS", names);
    }

    [Fact]
    public void InterfaceCall_IsRefusedOffline()
    {
        using var ed = EdiabasFactory.Create(TestPaths.UpstreamTestEcu, new OfflineInterface(), noInitForVJobs: true);
        ed.ResolveSgbdFile("cmd_test2");
        var ex = Assert.ThrowsAny<Exception>(() => ed.ExecuteJob("TEST_IFACE_FLAGS"));
        Assert.Contains(Chain(ex), e => e is OfflineInterfaceException);
    }

    static IEnumerable<Exception> Chain(Exception e) { for (; e != null; e = e.InnerException) yield return e; }
}
