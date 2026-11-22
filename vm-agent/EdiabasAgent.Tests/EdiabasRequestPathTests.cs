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
}
