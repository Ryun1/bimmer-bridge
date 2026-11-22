using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

/// <summary>Idle release of api32.dll and the <c>cable_not_found</c> mapping (task G3).</summary>
public class EdiabasReleaseAndCableTests
{
    private const int IdleSeconds = 20;
    private const int IdleMs = IdleSeconds * 1000;

    private readonly FakeEdiabasApi _api = new();
    private readonly FakeClock _clock = new();
    private readonly FakeConflicts _conflicts = new();
    private readonly FakeLog _log = new();
    private readonly DllLoadState _dll = new();

    private EdiabasService Service() =>
        new(
            _api,
            _conflicts,
            new FakeEcus("NVE_65", "TMODE", "UTILITY", "D_MOTOR"),
            JobPolicy.Embedded,
            new CommentScreen(JobPolicy.Embedded.ScreenTerms, JobPolicy.Embedded.ScreenIgnore, []),
            _clock,
            new CableUse(),
            _log,
            _dll,
            30,
            60,
            IdleSeconds);

    private static readonly FakeJob CableMissing = FakeJob.Error(28, "IFH-0018: INITIALIZATION ERROR");

    private Outcome<JobResult> Ident(EdiabasService svc) =>
        svc.RunJob(new JobRequest("NVE_65", "IDENT"), null, new RequestAudit(), default);

    private int LogCount(string text) => _log.Lines.Count(l => l.Contains(text, StringComparison.Ordinal));

    // ---------------------------------------------------------------- idle release

    [Fact]
    public void NothingLoaded_NothingToRelease()
    {
        var svc = Service();

        Assert.Equal(Timeout.Infinite, svc.MillisecondsUntilRelease());
        _clock.ElapsedMs += 10 * IdleMs;
        Assert.False(svc.ReleaseIfIdle());
        Assert.Empty(_api.Calls);
        Assert.False(_dll.Loaded);
    }

    [Fact]
    public void FirstRequest_LoadsOnce_AndLogsOneLine()
    {
        _api.On("NVE_65 IDENT", FakeJob.Ok());
        var svc = Service();

        Assert.True(Ident(svc).Ok);
        Assert.True(_dll.Loaded);
        Assert.True(Ident(svc).Ok);

        Assert.Equal(1, LogCount("api32.dll loaded"));
        Assert.Equal(0, _api.Count("Unload"));
    }

    [Fact]
    public void ReleasesExactlyAtTheIdleTime_AfterEnd()
    {
        _api.On("NVE_65 IDENT", FakeJob.Ok());
        var svc = Service();
        Ident(svc);

        Assert.Equal(IdleMs, svc.MillisecondsUntilRelease());

        _clock.ElapsedMs += IdleMs - 1;
        Assert.Equal(1, svc.MillisecondsUntilRelease());
        Assert.False(svc.ReleaseIfIdle());
        Assert.Equal(0, _api.Count("Unload"));
        Assert.True(_dll.Loaded);

        _clock.ElapsedMs += 1;
        Assert.Equal(0, svc.MillisecondsUntilRelease());
        Assert.True(svc.ReleaseIfIdle());

        Assert.Equal(["End", "Unload"], _api.Calls.TakeLast(2));
        Assert.False(_dll.Loaded);
        Assert.Equal(1, LogCount("api32.dll released after 20 s"));

        // Released: nothing more to wait for, and a second call does nothing.
        Assert.Equal(Timeout.Infinite, svc.MillisecondsUntilRelease());
        Assert.False(svc.ReleaseIfIdle());
        Assert.Equal(1, _api.Count("Unload"));
    }

    [Fact]
    public void IdleTime_CountsFromTheEndOfTheLastRequest()
    {
        _api.On("NVE_65 IDENT", new FakeJob { BusyPolls = 200, Sets = [[]] });
        var svc = Service();
        Ident(svc);

        // The job took 10 s; the idle time starts after it.
        Assert.Equal(10_000, _clock.ElapsedMs);
        Assert.Equal(IdleMs, svc.MillisecondsUntilRelease());
    }

    [Fact]
    public void EveryRequest_RestartsTheIdleTime()
    {
        _api.On("NVE_65 IDENT", FakeJob.Ok());
        var svc = Service();
        Ident(svc);
        _clock.ElapsedMs += IdleMs - 1000;
        Ident(svc);
        _clock.ElapsedMs += IdleMs - 1000;

        Assert.False(svc.ReleaseIfIdle());
        Assert.Equal(1000, svc.MillisecondsUntilRelease());
    }

    [Fact]
    public void NextRequestAfterRelease_LoadsAndResolvesAgain()
    {
        _api.On("NVE_65 IDENT", FakeJob.Ok());
        var svc = Service();
        Ident(svc);
        _clock.ElapsedMs += IdleMs;
        svc.ReleaseIfIdle();

        Assert.True(Ident(svc).Ok);

        var afterUnload = _api.Calls.SkipWhile(c => c != "Unload").Skip(1).ToList();
        Assert.Equal("TryLoad", afterUnload[0]);
        Assert.Equal("Init", afterUnload[1]);
        Assert.True(_dll.Loaded);
        Assert.Equal(2, LogCount("api32.dll loaded"));
        Assert.Equal(IdleMs, svc.MillisecondsUntilRelease());
    }

    [Fact]
    public void FailedUnload_IsLogged_AndTheNextRequestStillWorks()
    {
        _api.On("NVE_65 IDENT", FakeJob.Ok());
        _api.UnloadThrows = true;
        var svc = Service();
        Ident(svc);
        _clock.ElapsedMs += IdleMs;

        Assert.True(svc.ReleaseIfIdle());
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN api32.dll release did not complete", StringComparison.Ordinal));
        Assert.False(_dll.Loaded);
        Assert.Equal(Timeout.Infinite, svc.MillisecondsUntilRelease());

        _api.UnloadThrows = false;
        Assert.True(Ident(svc).Ok);
        Assert.True(_dll.Loaded);
        Assert.Equal(2, _api.Count("TryLoad"));
        Assert.Equal(1, _api.Count("Unload"));
    }

    [Fact]
    public void NeverReleasedWhileARequestIsRunning()
    {
        _api.On("NVE_65 IDENT", FakeJob.Ok());
        var svc = Service();
        Ident(svc);
        _clock.ElapsedMs += 10 * IdleMs;

        bool? releasedDuringJob = null;
        int? waitDuringJob = null;
        _api.DuringJob = () =>
        {
            waitDuringJob = svc.MillisecondsUntilRelease();
            releasedDuringJob = svc.ReleaseIfIdle();
        };
        Assert.True(Ident(svc).Ok);

        Assert.False(releasedDuringJob);
        Assert.Equal(Timeout.Infinite, waitDuringJob);
        Assert.Equal(0, _api.Count("Unload"));
        Assert.True(_dll.Loaded);
    }

    [Fact]
    public void ApiEndThrew_NeverReleased()
    {
        _api.On("NVE_65 IDENT", FakeJob.Ok());
        _api.EndThrows = true;
        var svc = Service();

        // The job's answer is kept; only the release is given up.
        Assert.True(Ident(svc).Ok);
        _clock.ElapsedMs += 100 * IdleMs;

        Assert.Equal(Timeout.Infinite, svc.MillisecondsUntilRelease());
        Assert.False(svc.ReleaseIfIdle());
        Assert.Equal(0, _api.Count("Unload"));
        Assert.True(_dll.Loaded);
        Assert.Contains(_log.Lines, l => l.Contains("stays loaded until the agent restarts", StringComparison.Ordinal));

        // Even a later clean request does not make it releasable again.
        _api.EndThrows = false;
        Assert.True(Ident(svc).Ok);
        _clock.ElapsedMs += 100 * IdleMs;
        Assert.False(svc.ReleaseIfIdle());
        Assert.Equal(0, _api.Count("Unload"));
    }

    [Fact]
    public void InitFailed_DllWasLoaded_SoItIsReleased()
    {
        _api.InitOk = false;
        var svc = Service();
        Assert.Equal("ediabas_unavailable", Ident(svc).Failure!.Error.Code);
        Assert.True(_dll.Loaded);

        _clock.ElapsedMs += IdleMs;
        Assert.True(svc.ReleaseIfIdle());
        Assert.Equal(1, _api.Count("Unload"));
    }

    [Fact]
    public void LoadFailed_NothingToRelease()
    {
        _api.LoadOk = false;
        var svc = Service();
        Ident(svc);

        Assert.False(_dll.Loaded);
        Assert.Equal(Timeout.Infinite, svc.MillisecondsUntilRelease());
        Assert.Equal(0, LogCount("api32.dll loaded"));
    }

    [Fact]
    public void RequestsThatNeverLoad_DoNotLoad()
    {
        var svc = Service();
        svc.RunJob(new JobRequest("NVE_65", "FS_LOESCHEN"), null, new RequestAudit(), default);
        svc.ListEcus(null);
        _conflicts.Answers[0] = [new ConflictingProcess("INPALOAD", 1)];
        Ident(svc);

        Assert.Empty(_api.Calls);
        Assert.False(_dll.Loaded);
        Assert.Equal(Timeout.Infinite, svc.MillisecondsUntilRelease());
    }

    // ---------------------------------------------------------------- a job that ignored apiBreak

    private static readonly FakeJob Hangs = new() { NeverFinishes = true, IgnoresBreak = true };

    private Outcome<JobResult> IdentWithTimeout(EdiabasService svc) =>
        svc.RunJob(new JobRequest("NVE_65", "IDENT"), 1, new RequestAudit(), default);

    [Fact]
    public void JobIgnoringBreak_NoApiEndOnTheStuckHandle()
    {
        _api.On("NVE_65 IDENT", Hangs);
        var svc = Service();

        Assert.Equal("job_timeout", IdentWithTimeout(svc).Failure!.Error.Code);

        Assert.Equal(1, _api.Count("Break"));
        Assert.Equal(0, _api.Count("End"));
        Assert.True(_dll.Stuck);
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("agent restarts", StringComparison.Ordinal));
    }

    [Fact]
    public void JobIgnoringBreak_DllIsNeverReleased()
    {
        _api.On("NVE_65 IDENT", Hangs);
        var svc = Service();
        IdentWithTimeout(svc);
        _clock.ElapsedMs += 100 * IdleMs;

        Assert.Equal(Timeout.Infinite, svc.MillisecondsUntilRelease());
        Assert.False(svc.ReleaseIfIdle());
        Assert.Equal(0, _api.Count("Unload"));
        Assert.True(_dll.Loaded);
    }

    [Fact]
    public void JobIgnoringBreak_LaterRequestsAreUnavailable_UntilRestart()
    {
        _api.On("NVE_65 IDENT", Hangs);
        _api.On("NVE_65 _JOBS", FakeJob.Jobs("IDENT"));
        var svc = Service();
        IdentWithTimeout(svc);
        var callsBefore = _api.Calls.Count;

        var single = Ident(svc).Failure!;
        Assert.Equal("ediabas_unavailable", single.Error.Code);
        Assert.Equal(503, single.Status);
        Assert.Contains("restart", single.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("apiBreak", single.Error.Message, StringComparison.Ordinal);

        Assert.Equal("ediabas_unavailable", svc.RunJobs([new("NVE_65", "IDENT")], null, new RequestAudit(), default).Failure!.Error.Code);
        Assert.Equal("ediabas_unavailable", svc.Vehicle([], new RequestAudit(), default).Failure!.Error.Code);
        Assert.Equal("ediabas_unavailable", svc.DescribeList("NVE_65", null, true, new RequestAudit(), default).Failure!.Error.Code);
        Assert.Equal("ediabas_unavailable", svc.DescribeJob("NVE_65", "IDENT", new RequestAudit(), default).Failure!.Error.Code);

        // Nothing at all reaches the DLL any more: no load, no apiInit, no job, no apiEnd.
        Assert.Equal(callsBefore, _api.Calls.Count);
        _clock.ElapsedMs += 100 * IdleMs;
        Assert.False(svc.ReleaseIfIdle());
    }

    [Fact]
    public void JobIgnoringBreak_InABatch_RestNotRun_NoApiEnd()
    {
        _api.On("NVE_65 IDENT", Hangs);
        _api.On("NVE_65 FS_LESEN", FakeJob.Ok());
        var svc = Service();
        var r = svc.RunJobs([new("NVE_65", "IDENT"), new("NVE_65", "FS_LESEN")], 1, new RequestAudit(), default).Value!;

        Assert.Equal("job_timeout", r.Items[0].Error!.Code);
        Assert.Equal("not_run", r.Items[1].Error!.Code);
        Assert.Equal(0, _api.Count("End"));
        Assert.True(_dll.Stuck);
    }

    [Fact]
    public void JobThatStopsAfterBreak_IsNotStuck()
    {
        _api.On("NVE_65 IDENT", new FakeJob { NeverFinishes = true });
        var svc = Service();
        Assert.Equal("job_timeout", IdentWithTimeout(svc).Failure!.Error.Code);

        Assert.Equal(1, _api.Count("End"));
        Assert.False(_dll.Stuck);
        _clock.ElapsedMs += IdleMs;
        Assert.True(svc.ReleaseIfIdle());
    }

    // ---------------------------------------------------------------- cable_not_found

    [Fact]
    public void ErrorMap_OnlyCode28IsCableNotFound()
    {
        var f = EdiabasErrorMap.Failure(28, "IFH-0018: INITIALIZATION ERROR");
        Assert.Equal(502, f.Status);
        Assert.Equal("cable_not_found", f.Error.Code);
        Assert.Contains("could not initialise the interface", f.Error.Message, StringComparison.Ordinal);
        Assert.Contains("cable is probably not connected to the VM", f.Error.Message, StringComparison.Ordinal);
        var d = Assert.IsType<EdiabasErrorDetails>(f.Error.Details);
        Assert.Equal(28, d.ErrorCode);
        Assert.Equal("IFH-0018: INITIALIZATION ERROR", d.ErrorText);

        foreach (var other in new[] { -1, 0, 19, 27, 29, 159 })
        {
            var o = EdiabasErrorMap.Failure(other, "x");
            Assert.Equal("ediabas_error", o.Error.Code);
            Assert.Equal(502, o.Status);
            Assert.Equal(other, Assert.IsType<EdiabasErrorDetails>(o.Error.Details).ErrorCode);
        }

        Assert.Equal(502, ApiErrors.Status(ApiErrors.CableNotFound));
    }

    [Fact]
    public void SingleJob_Error28_IsCableNotFound()
    {
        _api.On("NVE_65 IDENT", CableMissing);
        var audit = new RequestAudit();
        var r = Service().RunJob(new JobRequest("NVE_65", "IDENT"), null, audit, default);

        Assert.Equal(502, r.Failure!.Status);
        Assert.Equal("cable_not_found", r.Failure.Error.Code);
        Assert.Equal(new EdiabasErrorDetails(28, "IFH-0018: INITIALIZATION ERROR"), r.Failure.Error.Details);
        Assert.Contains(_log.Lines, l => l.Contains("EDIABAS error 28", StringComparison.Ordinal));
        Assert.Equal(1, _api.Count("End"));
    }

    [Fact]
    public void SingleJob_OtherCode_StaysEdiabasError()
    {
        _api.On("NVE_65 IDENT", FakeJob.Error(19, "IFH-0009: NO RESPONSE FROM CONTROLUNIT"));
        _api.On("NVE_65 _JOBS", FakeJob.Jobs("IDENT"));
        var r = Ident(Service());
        Assert.Equal(502, r.Failure!.Status);
        Assert.Equal("ediabas_error", r.Failure.Error.Code);
    }

    [Fact]
    public void BatchItem_Error28_IsCableNotFound_RestStillRun()
    {
        _api.On("NVE_65 IDENT", CableMissing);
        _api.On("TMODE INFO", FakeJob.Ok());
        var audit = new RequestAudit();
        var r = Service().RunJobs([new("NVE_65", "IDENT"), new("TMODE", "INFO")], null, audit, default);

        var items = r.Value!.Items;
        Assert.Equal("cable_not_found", items[0].Error!.Code);
        Assert.Equal(28, Assert.IsType<EdiabasErrorDetails>(items[0].Error!.Details).ErrorCode);
        Assert.True(items[1].Ok);
        Assert.Equal("cable_not_found", audit.Items[0].ErrorCode);
    }

    [Fact]
    public void Vehicle_Error28_IsCableNotFound()
    {
        _api.On("UTILITY STATUS_ZUENDUNG", CableMissing);
        _api.On("UTILITY STATUS_UBATT", CableMissing);
        _api.On("TMODE LESE_SPANNUNG_KL30", FakeJob.Ok());
        _api.On("TMODE LESE_SPANNUNG_KL15", FakeJob.Ok());

        var r = Service().Vehicle([], new RequestAudit(), default).Value!;

        Assert.Equal("cable_not_found", r.Ignition.Error!.Code);
        Assert.Equal("cable_not_found", r.Ubatt.Error!.Code);
        Assert.True(r.CableKl30.Ok);
    }

    [Fact]
    public void DescribeList_Error28_IsCableNotFound()
    {
        _api.On("NVE_65 _JOBS", CableMissing);
        var r = Service().DescribeList("NVE_65", null, true, new RequestAudit(), default);

        Assert.Equal(502, r.Failure!.Status);
        Assert.Equal("cable_not_found", r.Failure.Error.Code);
        Assert.Equal(1, _api.Count("End"));
    }

    [Fact]
    public void DescribeJob_Error28_IsCableNotFound_AtEveryStep()
    {
        // _JOBS itself.
        _api.On("NVE_65 _JOBS", CableMissing);
        Assert.Equal("cable_not_found", Service().DescribeJob("NVE_65", "FS_LESEN", new RequestAudit(), default).Failure!.Error.Code);
    }

    [Theory]
    [InlineData("_JOBCOMMENTS")]
    [InlineData("_ARGUMENTS")]
    [InlineData("_RESULTS")]
    public void DescribeJob_Error28InAnInfoJob_IsNotSwallowed(string infoJob)
    {
        _api.On("NVE_65 _JOBS", FakeJob.Jobs("FS_LESEN"));
        _api.On("NVE_65 _JOBCOMMENTS FS_LESEN", FakeJob.Comments("Fehlerspeicher lesen"));
        _api.On("NVE_65 _ARGUMENTS FS_LESEN", FakeJob.Ok());
        _api.On("NVE_65 _RESULTS FS_LESEN", FakeJob.Ok());
        _api.On($"NVE_65 {infoJob} FS_LESEN", CableMissing);

        var r = Service().DescribeJob("NVE_65", "FS_LESEN", new RequestAudit(), default);

        Assert.Equal(502, r.Failure!.Status);
        Assert.Equal("cable_not_found", r.Failure.Error.Code);
    }
}
