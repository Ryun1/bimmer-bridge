using System.Reflection;
using System.Text;
using EdiabasLib.Native;

namespace EdiabasLib.Native.Tests;

/// EdiabasNet binds its static register table to the newest instance; Activate binds it back
/// to the instance about to run, so an offline and a live instance can take turns.
public class ActivateTests
{
    static string Run(EdiabasNet ed, string sgbd, string job)
    {
        ed.ResolveSgbdFile(sgbd);
        ed.ExecuteJob(job);
        var sb = new StringBuilder();
        foreach (var set in ed.ResultSets)
        {
            foreach (var kv in set.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var v = kv.Value.OpData is byte[] b ? Convert.ToHexString(b) : Convert.ToString(kv.Value.OpData, System.Globalization.CultureInfo.InvariantCulture);
                sb.Append(kv.Key).Append(':').Append(kv.Value.ResType).Append('=').Append(v).Append('\n');
            }
            sb.Append("--\n");
        }
        return sb.ToString();
    }

    [Fact]
    public void Interleaved_Instances_MatchSingleInstance()
    {
        string lone;
        using (var single = EdiabasFactory.Create(TestPaths.UpstreamTestEcu, new OfflineInterface(), noInitForVJobs: true))
        {
            lone = Run(single, "cmd_test1", "TEST_MATH");
        }

        using var a = EdiabasFactory.Create(TestPaths.UpstreamTestEcu, new OfflineInterface(), noInitForVJobs: true);
        using var b = EdiabasFactory.Create(TestPaths.UpstreamTestEcu, new OfflineInterface(), noInitForVJobs: true);
        EdiabasFactory.Activate(a);
        var first = Run(a, "cmd_test1", "TEST_MATH");
        EdiabasFactory.Activate(b);
        Run(b, "cmd_test2", "_JOBS");
        EdiabasFactory.Activate(a);
        var second = Run(a, "cmd_test1", "TEST_MATH");

        Assert.Equal(lone, first);
        Assert.Equal(lone, second);
    }

    [Fact]
    public void Activate_BindsRegistersToInstance()
    {
        using var a = EdiabasFactory.Create(TestPaths.UpstreamTestEcu, new OfflineInterface(), noInitForVJobs: true);
        using var b = EdiabasFactory.Create(TestPaths.UpstreamTestEcu, new OfflineInterface(), noInitForVJobs: true);
        EdiabasFactory.Activate(a);

        var list = (Array)typeof(EdiabasNet).GetField("RegisterList", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var owner = list.GetValue(0)!.GetType().GetField("_ediabas", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var reg in list)
            Assert.Same(a, owner.GetValue(reg));
    }
}
