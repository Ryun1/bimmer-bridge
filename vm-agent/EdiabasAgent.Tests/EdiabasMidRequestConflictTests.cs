using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

/// <summary>
/// A BMW tool that starts while a request is running: the conflict check runs again before every
/// EDIABAS job the agent sends, not only at the start (review finding I1).
/// </summary>
public class EdiabasMidRequestConflictTests
{
    private readonly FakeEdiabasApi _api = new();
    private readonly FakeClock _clock = new();
    private readonly FakeConflicts _conflicts = new();
    private readonly FakeLog _log = new();
    private readonly RequestAudit _audit = new();

    private static readonly ConflictingProcess WinKfp = new("WINKFPT", 9);

    private EdiabasService Service(int maxTimeout = 60) =>
        new(
            _api,
            _conflicts,
            new FakeEcus("NVE_65", "TMODE", "UTILITY"),
            JobPolicy.Embedded,
            new CommentScreen(JobPolicy.Embedded.ScreenTerms, JobPolicy.Embedded.ScreenIgnore, []),
            _clock,
            new CableUse(),
            _log,
            new DllLoadState(),
            30,
            maxTimeout,
            20);

    /// <summary>A BMW tool appears at <paramref name="atMs"/> on the runner clock.</summary>
    private void ToolStartsAt(long atMs) =>
        _conflicts.Answer = () => _clock.ElapsedMs >= atMs ? [WinKfp] : [];

    /// <summary>A job that takes <paramref name="ms"/> (polled every 50 ms).</summary>
    private static FakeJob Takes(int ms, params List<FakeResult>[] sets) =>
        new() { BusyPolls = ms / EdiabasService.PollIntervalMs, Sets = [[], .. sets] };

    private static List<FakeResult> Comment(string text) => [new("JOBCOMMENT0", ApiResultFormat.Text, text)];

    private static FakeJob JobList(int ms, params string[] names) =>
        new()
        {
            BusyPolls = ms / EdiabasService.PollIntervalMs,
            Sets =
            [
                [new FakeResult("VARIANTE", ApiResultFormat.Text, "VAR")],
                .. names.Select(n => new List<FakeResult> { new("JOBNAME", ApiResultFormat.Text, n) }),
            ],
        };

    private int CommentFetches => _api.JobCalls.Count(c => c.StartsWith("Job NVE_65 _JOBCOMMENTS", StringComparison.Ordinal));

    // ---------------------------------------------------------------- describe, job list

    [Fact]
    public void DescribeList_ToolStartsMidway_StopsFetching_KeepsWhatWasRead()
    {
        var names = Enumerable.Range(0, 10).Select(i => $"STATUS_{i}").ToArray();
        _api.On("NVE_65 _JOBS", JobList(300, names));
        foreach (var n in names)
        {
            _api.On($"NVE_65 _JOBCOMMENTS {n}", Takes(300, Comment($"{n} lesen")));
        }

        // _JOBS ends at 300 ms, then one fetch per 300 ms: the tool appears during the 3rd fetch,
        // which is stopped with apiBreak.
        ToolStartsAt(1000);
        var r = Service().DescribeList("NVE_65", null, true, _audit, default);

        var list = r.Value!;
        Assert.Equal(10, list.JobCount);
        Assert.Equal(3, CommentFetches);
        Assert.Equal(1, _api.Count("Break"));
        Assert.All(list.Jobs.Take(2), j => Assert.Equal(Review.Passed, j.Verdict.Review));
        Assert.All(list.Jobs.Take(2), j => Assert.NotNull(j.Comment));
        Assert.All(list.Jobs.Skip(2), j => Assert.Equal(Review.NotChecked, j.Verdict.Review));
        Assert.All(list.Jobs.Skip(2), j => Assert.Null(j.Comment));
        Assert.Equal(1, _api.Count("End"));
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("WINKFPT", StringComparison.Ordinal));
    }

    [Fact]
    public void DescribeList_ToolStartsDuringTheJobList_IsCableInUse_NoCommentFetched()
    {
        _api.On("NVE_65 _JOBS", JobList(300, "STATUS_A", "STATUS_B"));
        ToolStartsAt(100); // while _JOBS runs: it is stopped, so there is no list to give

        var f = Service().DescribeList("NVE_65", null, true, _audit, default).Failure!;

        Assert.Equal("cable_in_use", f.Error.Code);
        Assert.Equal(1, _api.Count("Break"));
        Assert.Equal(0, CommentFetches);
    }

    [Fact]
    public void DescribeList_Recheck_IsRateLimited_SoALongListIsNotSlowed()
    {
        // 150 jobs, 50 ms each: 7.5 s of fetches. At most one process scan per 250 ms.
        var names = Enumerable.Range(0, 150).Select(i => $"STATUS_{i}").ToArray();
        _api.On("NVE_65 _JOBS", JobList(50, names));
        foreach (var n in names)
        {
            _api.On($"NVE_65 _JOBCOMMENTS {n}", Takes(50, Comment("lesen")));
        }

        var r = Service().DescribeList("NVE_65", null, true, _audit, default).Value!;

        Assert.Equal(150, CommentFetches);
        Assert.All(r.Jobs, j => Assert.Equal(Review.Passed, j.Verdict.Review));
        var maxScans = 1 + (int)(_clock.ElapsedMs / EdiabasService.ConflictRecheckMs) + 1;
        Assert.InRange(_conflicts.Calls, 2, maxScans);
    }

    [Fact]
    public void DescribeList_FastJobs_NoExtraScans()
    {
        _api.On("NVE_65 _JOBS", FakeJob.Jobs("STATUS_A", "STATUS_B"));
        _api.On("NVE_65 _JOBCOMMENTS STATUS_A", FakeJob.Comments("A lesen"));
        _api.On("NVE_65 _JOBCOMMENTS STATUS_B", FakeJob.Comments("B lesen"));

        Assert.True(Service().DescribeList("NVE_65", null, true, _audit, default).Ok);
        Assert.Equal(2, _conflicts.Calls); // the start, and after the DLL loads
    }

    // ---------------------------------------------------------------- describe, one job

    [Theory]
    [InlineData(200, "_JOBCOMMENTS")]
    [InlineData(500, "_ARGUMENTS")]
    [InlineData(700, "_RESULTS")]
    public void DescribeJob_ToolStartsMidway_IsCableInUse(long atMs, string firstNotRun)
    {
        _api.On("NVE_65 _JOBS", JobList(300, "FS_LESEN"));
        _api.On("NVE_65 _JOBCOMMENTS FS_LESEN", Takes(300, Comment("Fehlerspeicher lesen")));
        _api.On("NVE_65 _ARGUMENTS FS_LESEN", Takes(300));
        _api.On("NVE_65 _RESULTS FS_LESEN", Takes(300));
        ToolStartsAt(atMs);

        var f = Service().DescribeJob("NVE_65", "FS_LESEN", _audit, default).Failure!;

        Assert.Equal(409, f.Status);
        Assert.Equal("cable_in_use", f.Error.Code);
        Assert.Contains("started during the request", f.Error.Message, StringComparison.Ordinal);
        Assert.Equal([WinKfp], Assert.IsType<ProcessesDetails>(f.Error.Details).Processes);
        Assert.DoesNotContain(_api.JobCalls, c => c.StartsWith($"Job NVE_65 {firstNotRun}", StringComparison.Ordinal));
        Assert.Equal(1, _api.Count("Break")); // the job running when the tool appeared
        Assert.Equal(1, _api.Count("End"));
    }

    // ---------------------------------------------------------------- screen, then the job

    [Fact]
    public void SingleJob_ToolStartsDuringTheScreen_IsCableInUse_JobNeverRuns()
    {
        _api.On("NVE_65 _JOBCOMMENTS STATUS_BORDNETZSPANNUNG", Takes(300, Comment("Bordnetzspannung auslesen")));
        _api.On("NVE_65 STATUS_BORDNETZSPANNUNG", FakeJob.Ok());
        ToolStartsAt(100);

        var f = Service().RunJob(new JobRequest("NVE_65", "STATUS_BORDNETZSPANNUNG"), null, _audit, default).Failure!;

        Assert.Equal("cable_in_use", f.Error.Code);
        Assert.Equal(409, f.Status);
        Assert.Equal(["Job NVE_65 _JOBCOMMENTS [STATUS_BORDNETZSPANNUNG] []"], _api.JobCalls);
        Assert.Equal(1, _api.Count("End"));
    }

    [Fact]
    public void BatchItem_ToolStartsDuringTheScreen_ItemAndRestAreNotRun()
    {
        _api.On("NVE_65 _JOBCOMMENTS STATUS_BORDNETZSPANNUNG", Takes(300, Comment("Bordnetzspannung auslesen")));
        _api.On("NVE_65 STATUS_BORDNETZSPANNUNG", FakeJob.Ok());
        _api.On("NVE_65 IDENT", FakeJob.Ok());
        ToolStartsAt(100);

        var r = Service().RunJobs([new("NVE_65", "STATUS_BORDNETZSPANNUNG"), new("NVE_65", "IDENT")], null, _audit, default).Value!;

        Assert.Equal("not_run", r.Items[0].Error!.Code);
        Assert.Contains("WINKFPT", r.Items[0].Error!.Message, StringComparison.Ordinal);
        Assert.Equal("not_run", r.Items[1].Error!.Code);
        Assert.Equal(["Job NVE_65 _JOBCOMMENTS [STATUS_BORDNETZSPANNUNG] []"], _api.JobCalls);
        Assert.Equal(1, _api.Count("End"));
    }

    [Fact]
    public void SingleJob_NoToolStarted_ScreenThenJobRuns()
    {
        _api.On("NVE_65 _JOBCOMMENTS STATUS_BORDNETZSPANNUNG", Takes(300, Comment("Bordnetzspannung auslesen")));
        _api.On("NVE_65 STATUS_BORDNETZSPANNUNG", FakeJob.Ok());
        ToolStartsAt(long.MaxValue);

        Assert.True(Service().RunJob(new JobRequest("NVE_65", "STATUS_BORDNETZSPANNUNG"), null, _audit, default).Ok);
        Assert.Equal(3, _conflicts.Calls); // the start, after the DLL loads, and once while the screen runs
    }

    // ---------------------------------------------------------------- while the job runs, before apiInit

    [Fact]
    public void SingleJob_ToolStartsDuringTheJob_JobIsBroken_NotRun()
    {
        _api.On("NVE_65 IDENT", Takes(5000));
        ToolStartsAt(1000);

        var f = Service().RunJob(new JobRequest("NVE_65", "IDENT"), null, _audit, default).Failure!;

        // A single job cannot give a partial answer: the item's not_run becomes cable_in_use.
        Assert.Equal("cable_in_use", f.Error.Code);
        Assert.Contains("WINKFPT", f.Error.Message, StringComparison.Ordinal);
        Assert.Contains("Break", _api.Calls);
        Assert.True(_clock.ElapsedMs < 1000 + 2 * EdiabasService.ConflictRecheckMs + EdiabasService.PollIntervalMs, $"took {_clock.ElapsedMs} ms");
        Assert.Equal(1, _api.Count("End"));
    }

    [Fact]
    public void BatchItem_ToolStartsDuringTheJob_JobIsBroken_NotRun()
    {
        _api.On("NVE_65 IDENT", Takes(5000));
        _api.On("NVE_65 FS_LESEN", FakeJob.Ok());
        ToolStartsAt(1000);

        var r = Service().RunJobs([new("NVE_65", "IDENT"), new("NVE_65", "FS_LESEN")], null, _audit, default).Value!;

        Assert.Equal("not_run", r.Items[0].Error!.Code);
        Assert.Contains("WINKFPT", r.Items[0].Error!.Message, StringComparison.Ordinal);
        Assert.Equal("not_run", r.Items[1].Error!.Code);
        Assert.Contains("Break", _api.Calls);
        Assert.Equal(["Job NVE_65 IDENT [] []"], _api.JobCalls);
        Assert.True(_clock.ElapsedMs < 1000 + 2 * EdiabasService.ConflictRecheckMs + EdiabasService.PollIntervalMs, $"took {_clock.ElapsedMs} ms");
    }

    [Fact]
    public void SingleJob_ToolStartsDuringDllLoad_NoApiInit()
    {
        _api.Clock = _clock;
        _api.LoadMs = 10_000;
        _api.On("NVE_65 IDENT", FakeJob.Ok());
        ToolStartsAt(5000);

        var f = Service().RunJob(new JobRequest("NVE_65", "IDENT"), null, _audit, default).Failure!;

        Assert.Equal("cable_in_use", f.Error.Code);
        Assert.Equal(409, f.Status);
        Assert.Contains("WINKFPT", f.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Init", _api.Calls);
        Assert.Empty(_api.JobCalls);
        Assert.Equal(0, _api.Count("End")); // no apiInit, so no apiEnd
    }

    [Fact]
    public void SingleJob_LongJob_NoTool_ConflictScansAreRateLimited()
    {
        _api.On("NVE_65 IDENT", Takes(3000));

        Assert.True(Service().RunJob(new JobRequest("NVE_65", "IDENT"), null, _audit, default).Ok);
        Assert.True(_conflicts.Calls <= 3000 / EdiabasService.ConflictRecheckMs + 2, $"{_conflicts.Calls} scans");
        Assert.True(_conflicts.Calls > 2, $"{_conflicts.Calls} scans: the running job is not checked");
    }
}
