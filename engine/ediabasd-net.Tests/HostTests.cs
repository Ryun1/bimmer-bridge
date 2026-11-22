using EdiabasAgent.Core;
using EdiabasLib.Native.Tests;

namespace Ediabasd.Tests;

public sealed class HostTests : IDisposable
{
    readonly string _state = Directory.CreateTempSubdirectory("ediabasd-state-").FullName;

    public void Dispose() => Directory.Delete(_state, recursive: true);

    /// A config over upstream's test Ecu folder, with state in stateDir.
    public static NativeConfig Config(string stateDir) => new()
    {
        EcuPath = TestPaths.UpstreamTestEcu,
        StateDir = stateDir,
    };

    [Fact]
    public void Host_DescribeList_UpstreamTestSgbd()
    {
        using var host = new EdiabasdHost(Config(_state));
        var audit = new RequestAudit();
        var r = host.Service.DescribeList("cmd_test2", null, true, audit, CancellationToken.None);
        Assert.True(r.Ok, r.Failure?.Error.Message);
        var entry = Assert.Single(r.Value!.Jobs, j => j.Name == "TEST_IFACE_FLAGS");
        Assert.NotNull(entry.Verdict);
    }

    [Fact]
    public void Host_NeverRunsJobsInLiveMode_ForDescribe()
    {
        var made = 0;
        using var host = new EdiabasdHost(Config(_state), () => { made++; throw new InvalidOperationException("no cable in describe"); });
        var r = host.Service.DescribeList("cmd_test1", null, true, new RequestAudit(), CancellationToken.None);
        Assert.True(r.Ok, r.Failure?.Error.Message);
        Assert.Equal(0, made);
    }

    [Fact]
    public void Host_PortFromConfig_Wins()
    {
        var c = Config(_state);
        c.Port = "/dev/cu.usbserial-TEST";
        using var host = new EdiabasdHost(c);
        Assert.Equal("/dev/cu.usbserial-TEST", host.ResolvePort());
    }

    [Fact]
    public void AuditLog_AppendsOneLinePerRequest()
    {
        var c = Config(_state);
        var log = new AuditLog(c.AuditPath);
        log.Write(DateTimeOffset.UnixEpoch, "describe", 403, ApiErrors.JobNotAllowed, 12, "NVE_65", "_JOBS", "deny.x");
        log.Write(DateTimeOffset.UnixEpoch, "ecus", 200, null, 3, null, null, null);
        var lines = File.ReadAllLines(c.AuditPath);
        Assert.Equal(2, lines.Length);
        Assert.Contains("describe", lines[0]);
        Assert.Contains("job_not_allowed", lines[0]);
        Assert.Contains("NVE_65", lines[0]);
    }
}
