using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

/// <summary>The request path's gates after the second review: cancel, queue time, apiEnd,
/// unreadable results, the argument screen and jobs the SGBD does not have.</summary>
public class EdiabasRequestPathTests
{
    private readonly FakeEdiabasApi _api = new();
    private readonly FakeClock _clock = new();
    private readonly FakeConflicts _conflicts = new();
    private readonly FakeLog _log = new();
    private readonly RequestAudit _audit = new();

    private EdiabasService Service(IEnumerable<string>? reviewed = null, int defaultTimeout = 30, int maxTimeout = 60, params string[] ecus) =>
        new(
            _api,
            _conflicts,
            new FakeEcus(ecus.Length == 0 ? ["NVE_65", "TMODE", "UTILITY", "D_MOTOR"] : ecus),
            JobPolicy.Embedded,
            new CommentScreen(JobPolicy.Embedded.ScreenTerms, JobPolicy.Embedded.ScreenIgnore, reviewed ?? []),
            _clock,
            new CableUse(),
            _log,
            new DllLoadState(),
            defaultTimeout,
            maxTimeout,
            20);

    private static FakeResult Text(string name, string value) => new(name, ApiResultFormat.Text, value);

    // ---------------------------------------------------------------- A1: cancel is checked before every apiJob

    [Fact]
    public void CancelledDuringTheScreen_ClientJobNeverStarts()
    {
        using var cts = new CancellationTokenSource();
        _api.On("NVE_65 _JOBCOMMENTS STATUS_X", FakeJob.Comments("Status lesen"));
        _api.On("NVE_65 STATUS_X", FakeJob.Ok());
        _api.DuringJob = cts.Cancel;

        var r = Service().RunJob(new JobRequest("NVE_65", "STATUS_X"), null, _audit, cts.Token);

        Assert.Equal("not_run", r.Failure!.Error.Code);
        Assert.Equal("The request was abandoned before STATUS_X started.", r.Failure.Error.Message);
        Assert.Equal(["Job NVE_65 _JOBCOMMENTS [STATUS_X] []"], _api.JobCalls);
        Assert.Equal(1, _api.Count("End"));
    }

    [Fact]
    public void CancelledAfterJobsInDescribeJob_NoFurtherInfoJob()
    {
        using var cts = new CancellationTokenSource();
        _api.On("NVE_65 _JOBS", FakeJob.Jobs("IDENT"));
        _api.On("NVE_65 _JOBCOMMENTS IDENT", FakeJob.Comments("Ident"));
        _api.DuringJob = cts.Cancel;

        var r = Service().DescribeJob("NVE_65", "IDENT", _audit, cts.Token);

        Assert.Equal("not_run", r.Failure!.Error.Code);
        Assert.Equal(["Job NVE_65 _JOBS [] []"], _api.JobCalls);
        Assert.Equal(1, _api.Count("End"));
    }

    // ---------------------------------------------------------------- A2: queue time counts against the budget

    [Fact]
    public void Queued50sOf60_LeavesTenSecondsForTheJob()
    {
        _api.On("NVE_65 IDENT", new FakeJob { NeverFinishes = true });

        var r = Service(maxTimeout: 60).RunJob(new JobRequest("NVE_65", "IDENT"), 30, _audit, default, queuedMs: 50_000);

        Assert.Equal("job_timeout", r.Failure!.Error.Code);
        Assert.Contains("60 s limit", r.Failure.Error.Message, StringComparison.Ordinal);
        Assert.Equal(10_000, _clock.ElapsedMs);
        Assert.Equal(1, _api.Count("End"));
    }

    [Fact]
    public void Queued50sOf60_BatchLeftoversAreNotRun()
    {
        _api.On("NVE_65 IDENT", new FakeJob { BusyPolls = 200, Sets = [[]] }); // 10 s

        var r = Service(maxTimeout: 60).RunJobs([new("NVE_65", "IDENT"), new("NVE_65", "IDENT")], 30, _audit, default, queuedMs: 50_000);

        var items = r.Value!.Items;
        Assert.True(items[0].Ok);
        Assert.Equal("not_run", items[1].Error!.Code);
        Assert.Single(_api.JobCalls);

        // durationMs is the time the request ran, not the time it waited.
        Assert.Equal(10_000, r.Value.DurationMs);
    }

    public static TheoryData<string> Requests => ["job", "jobs", "vehicle", "describe-list", "describe-job"];

    [Theory]
    [MemberData(nameof(Requests))]
    public void QueuedTheWholeBudget_StartsNothing(string kind)
    {
        const string message = "The request waited behind another EDIABAS request for the whole 60 s limit; nothing was started.";
        var svc = Service(maxTimeout: 60);
        const long queued = 60_000;

        ApiFailure? f = kind switch
        {
            "job" => svc.RunJob(new JobRequest("NVE_65", "IDENT"), null, _audit, default, queued).Failure,
            "jobs" => svc.RunJobs([new("NVE_65", "IDENT")], null, _audit, default, queued).Failure,
            "vehicle" => svc.Vehicle([], _audit, default, queued).Failure,
            "describe-list" => svc.DescribeList("NVE_65", null, true, _audit, default, queued).Failure,
            _ => svc.DescribeJob("NVE_65", "IDENT", _audit, default, queued).Failure,
        };

        Assert.Equal("not_run", f!.Error.Code);
        Assert.Equal(message, f.Error.Message);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public void QueueTime_DoesNotMakeTheConflictCheckStale()
    {
        // The request's own conflict check just ran; the first job reuses it however long the wait.
        _api.On("NVE_65 IDENT", FakeJob.Ok());

        Assert.True(Service().RunJob(new JobRequest("NVE_65", "IDENT"), null, _audit, default, queuedMs: 5_000).Ok);
        Assert.Equal(1, _conflicts.Calls);
    }

    // ---------------------------------------------------------------- A3: a failed apiEnd keeps the result

    [Fact]
    public void ApiEndThrows_TheResultIsStillReturned_AndTheDllIsNeverReleased()
    {
        _api.On("NVE_65 IDENT", FakeJob.Ok([Text("JOB_STATUS", "OKAY")]));
        _api.EndThrows = true;
        var svc = Service();

        var r = svc.RunJob(new JobRequest("NVE_65", "IDENT"), null, _audit, default);

        Assert.True(r.Ok);
        Assert.Equal("OKAY", r.Value!.JobStatus);
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN apiEnd threw", StringComparison.Ordinal));

        _clock.ElapsedMs += 100_000;
        Assert.False(svc.ReleaseIfIdle());
        Assert.Equal(0, _api.Count("Unload"));
    }

    // ---------------------------------------------------------------- A4: unreadable results

    [Theory]
    [InlineData(0, "")]
    [InlineData(134, "SYS-0002: SOMETHING")]
    public void ResultsCannotBeRead_HasItsOwnMessage(int code, string text)
    {
        _api.On("NVE_65 IDENT", new FakeJob { ResultSetsFail = true, ErrorCode = code, ErrorText = text });
        _api.On("NVE_65 _JOBS", FakeJob.Jobs("IDENT"));

        var f = Service().RunJob(new JobRequest("NVE_65", "IDENT"), null, _audit, default).Failure!;

        Assert.Equal("ediabas_error", f.Error.Code);
        Assert.Equal("The job finished but its results could not be read.", f.Error.Message);
        Assert.Equal(new EdiabasErrorDetails(code, text), f.Error.Details);
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN NVE_65 IDENT: finished but its results could not be read", StringComparison.Ordinal));
    }
}
