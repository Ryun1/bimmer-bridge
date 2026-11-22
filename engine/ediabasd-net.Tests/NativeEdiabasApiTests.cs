using EdiabasAgent.Core;
using EdiabasLib;
using EdiabasLib.Native;
using EdiabasLib.Native.Tests;

namespace Ediabasd.Tests;

public sealed class NativeEdiabasApiTests : IDisposable
{
    readonly Recordings _rec = new();

    public void Dispose() => _rec.Dispose();

    static int Wait(NativeEdiabasApi api, int timeoutMs = 10_000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        int s;
        while ((s = api.State()) == ApiState.Busy)
        {
            if (Environment.TickCount64 > until) throw new TimeoutException("job still busy");
            Thread.Sleep(5);
        }
        return s;
    }

    NativeEdiabasApi Api(CableFactory cables) =>
        new(TestPaths.UpstreamTestEcu, cables.Make, new NoVariantCache(), traceDir: null);

    CableFactory EmptyReplay() =>
        new(() => LiveCable.Replay(_rec.Empty()));

    static List<string> JobNames(NativeEdiabasApi api)
    {
        Assert.True(api.ResultSets(out var n));
        var names = new List<string>();
        for (var s = 1; s <= n; s++)
        {
            if (api.ResultText("JOBNAME", s, out var name)) names.Add(name);
        }
        return names;
    }

    [Fact]
    public void OfflineJobs_ListsJobs()
    {
        var cables = EmptyReplay();
        using var api = Api(cables);
        Assert.True(api.TryLoad(out _));
        Assert.True(api.Init());
        api.Job("cmd_test2", "_JOBS", "", "");
        Assert.Equal(ApiState.Ready, Wait(api));
        Assert.Contains("TEST_IFACE_FLAGS", JobNames(api));
        api.End();
        Assert.Equal(0, cables.Calls);
    }

    [Fact]
    public void LiveJob_RunsOnLiveInstance()
    {
        var cables = EmptyReplay();
        using var api = Api(cables);
        Assert.True(api.Init());
        api.Job("cmd_test1", "TEST_MATH", "", "");
        Assert.Equal(ApiState.Ready, Wait(api));
        Assert.Equal(1, cables.Calls);

        // The same job straight on EdiabasNet gives the same sets.
        using var direct = EdiabasFactory.Create(TestPaths.UpstreamTestEcu, new OfflineInterface(), noInitForVJobs: false);
        direct.ResolveSgbdFile("cmd_test1");
        direct.ExecuteJob("TEST_MATH");
        Assert.True(api.ResultSets(out var n));
        Assert.Equal(direct.ResultSets.Count - 1, n);
        for (var s = 0; s <= n; s++)
        {
            Assert.True(api.ResultNumber(s, out var count));
            Assert.Equal(direct.ResultSets[s].Count, count);
        }
        api.End();
    }

    [Fact]
    public void LiveInstance_RunsInit_OfflineDoesNot()
    {
        using var api = Api(EmptyReplay());
        Assert.True(api.Init());
        api.Job("cmd_test1", "TEST_MATH", "", "");
        Wait(api);
        api.Job("cmd_test1", "_JOBS", "", "");
        Wait(api);
        Assert.False(api.LiveNoInitForVJobs);
        Assert.True(api.OfflineNoInitForVJobs);
    }

    [Fact]
    public void GateRefusal_FailsJobEvenWhenMasked()
    {
        var cables = new CableFactory(() => new FakeCable(new FakeInterface()) { Reason = KlineReason.GateRefused });
        using var api = Api(cables);
        Assert.True(api.Init());
        api.Job("cmd_test1", "TEST_MATH", "", "");
        Assert.Equal(ApiState.Error, Wait(api));
        Assert.Equal(NativeErrorCodes.GateRefused, api.ErrorCode());
    }

    // No upstream test job fails through the interface: TEST_IFACE_FLAGS traps every interface
    // error and ends ready. So the reason-to-code rules are tested on their own.

    [Theory]
    [InlineData(KlineReason.NoDevice, NativeErrorCodes.NoDevice)]
    [InlineData(KlineReason.Locked, NativeErrorCodes.Locked)]
    [InlineData(KlineReason.NotFtdi, NativeErrorCodes.NotFtdi)]
    [InlineData(KlineReason.Permission, NativeErrorCodes.Permission)]
    public void ConnectReason_ReplacesIfh0018(KlineReason reason, int expected)
    {
        var m = NativeEdiabasApi.MapCableReason(ApiState.Error, 28, "IFH-0018: INITIALIZATION ERROR", reason);
        Assert.Equal(ApiState.Error, m.State);
        Assert.Equal(expected, m.Code);
        Assert.Contains("IFH-0018", m.Text);
        Assert.Contains("libkline", m.Text);
    }

    [Fact]
    public void GateRefused_FailsAReadyJob()
    {
        var m = NativeEdiabasApi.MapCableReason(ApiState.Ready, 0, "", KlineReason.GateRefused);
        Assert.Equal(ApiState.Error, m.State);
        Assert.Equal(NativeErrorCodes.GateRefused, m.Code);
    }

    [Fact]
    public void Ifh0003_WithNoReason_IsEchoMismatch()
    {
        var m = NativeEdiabasApi.MapCableReason(ApiState.Error, 13, "IFH-0003: DATATRANSMISSION TO CONTROLUNIT DISTURBED", KlineReason.Ok);
        Assert.Equal(NativeErrorCodes.EchoMismatch, m.Code);
    }

    [Fact]
    public void Ifh0003_WithReplayMismatch_StaysEdiabasError()
    {
        var m = NativeEdiabasApi.MapCableReason(ApiState.Error, 13, "IFH-0003", KlineReason.ReplayMismatch);
        Assert.Equal(13, m.Code);
    }

    [Fact]
    public void ReadyJob_WithConnectReason_StaysReady()
    {
        // EDIABAS behaviour: an SGBD that traps the error and ends ready is ready.
        var m = NativeEdiabasApi.MapCableReason(ApiState.Ready, 0, "", KlineReason.NoDevice);
        Assert.Equal(ApiState.Ready, m.State);
        Assert.Equal(0, m.Code);
    }

    [Fact]
    public void MaskedInterfaceJob_OnMissingCable_EndsReady()
    {
        var cables = new CableFactory(() => LiveCable.Device("/dev/does-not-exist", recordDir: null));
        using var api = Api(cables);
        Assert.True(api.Init());
        api.Job("cmd_test2", "TEST_IFACE_FLAGS", "", "");
        Assert.Equal(ApiState.Ready, Wait(api));
        Assert.Equal(KlineReason.NoDevice, cables.Last!.Reason);
    }

    [Fact]
    public void Break_CancelsCable()
    {
        var cables = new CableFactory(() => new FakeCable(new FakeInterface { BlockOnTransmit = true }));
        using var api = Api(cables);
        Assert.True(api.Init());
        api.Job("cmd_test2", "TEST_IFACE_FLAGS", "", "");
        Thread.Sleep(200);
        Assert.Equal(ApiState.Busy, api.State());
        var start = Environment.TickCount64;
        api.Break();
        var state = Wait(api, 2000);
        Assert.True(Environment.TickCount64 - start < 2000);
        Assert.Contains(state, new[] { ApiState.Break, ApiState.Error });
        Assert.Equal(1, ((FakeCable)cables.Last!).CancelCount);
    }

    [Fact]
    public void Describe_NeverCreatesLiveCable()
    {
        var cables = EmptyReplay();
        using var api = Api(cables);
        Assert.True(api.Init());
        api.Job("cmd_test1", "_JOBS", "", "");
        Assert.Equal(ApiState.Ready, Wait(api));
        api.Job("cmd_test1", "_JOBCOMMENTS", "TEST_MATH", "");
        Assert.Equal(ApiState.Ready, Wait(api));
        api.End();
        Assert.Equal(0, cables.Calls);
    }

    [Fact]
    public void End_DisposesTheLiveCable()
    {
        var cables = new CableFactory(() => new FakeCable(new FakeInterface()));
        using var api = Api(cables);
        Assert.True(api.Init());
        api.Job("cmd_test1", "TEST_MATH", "", "");
        Wait(api);
        api.End();
        Assert.True(((FakeCable)cables.Last!).Disposed);
    }
}
