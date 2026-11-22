using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

/// <summary>A scripted config.json: its text, or a queue of exceptions to throw first.</summary>
public sealed class FakeConfigFile : IConfigFile
{
    public string Text { get; set; } = "{}";

    /// <summary>Thrown by the next reads, one per read, before the text is returned again.</summary>
    public Queue<Exception> Failures { get; } = new();

    public int Reads { get; private set; }

    public string ReadAllText()
    {
        Reads++;
        return Failures.Count > 0 ? throw Failures.Dequeue() : Text;
    }

    public void Write(string text) => Text = text;
}

/// <summary>reviewedJobs is re-read when config.json changes; nothing else is.</summary>
public class ApprovalReloadTests
{
    private readonly FakeEdiabasApi _api = new();
    private readonly FakeClock _clock = new();
    private readonly FakeLog _log = new();
    private readonly FakeConfigFile _file = new();

    private const string StartetBHash = "7b69322a754f";
    private const string PinnedB = "NVE_65:STATUS_B@" + StartetBHash;

    private EdiabasService Service(ApprovalState state) =>
        new(
            _api,
            new FakeConflicts(),
            new FakeEcus("NVE_65"),
            JobPolicy.Embedded,
            new CommentScreen(JobPolicy.Embedded.ScreenTerms, JobPolicy.Embedded.ScreenIgnore, state),
            _clock,
            new CableUse(),
            _log,
            new DllLoadState(),
            30,
            60,
            20,
            new ApprovalReloader(_file, state, _log));

    private static ApprovalState State(params string[] entries) => new(new ApprovalList(entries));

    private Outcome<JobResult> RunB(EdiabasService svc) => svc.RunJob(new JobRequest("NVE_65", "STATUS_B"), null, new RequestAudit(), default);

    private static string Config(params string[] entries) =>
        $$"""{ "port": 8765, "reviewedJobs": [{{string.Join(", ", entries.Select(e => "\"" + e + "\""))}}] }""";

    private IEnumerable<string> Warnings => _log.Lines.Where(l => l.StartsWith("WARN ", StringComparison.Ordinal) && l.Contains("reviewedJobs", StringComparison.Ordinal));

    private IEnumerable<string> ReReads => _log.Lines.Where(l => l.Contains("reviewedJobs re-read", StringComparison.Ordinal));

    public ApprovalReloadTests()
    {
        _api.On("NVE_65 _JOBCOMMENTS STATUS_B", FakeJob.Comments("Startet B"));
        _api.On("NVE_65 STATUS_B", FakeJob.Ok());
    }

    [Fact]
    public void NewEntry_IsPickedUp_WithoutARestart()
    {
        _file.Text = Config();
        var state = State();
        var svc = Service(state);
        Assert.Equal("job_needs_review", RunB(svc).Failure!.Error.Code);

        _file.Write(Config($"NVE_65:STATUS_B@{StartetBHash}"));
        Assert.True(RunB(svc).Ok);
        ApprovalTests.AssertCounts(1, 0, state.Current.Counts);
        Assert.Equal(["INFO reviewedJobs re-read: 1 pinned, 0 ignored"], ReReads);
        Assert.DoesNotContain(_log.Lines, l => l.Contains(StartetBHash, StringComparison.Ordinal));
    }

    [Fact]
    public void Unchanged_File_IsReadEveryRequest_AndSaysNothing()
    {
        _file.Text = Config(PinnedB);
        var svc = Service(State(PinnedB));
        Assert.True(RunB(svc).Ok);
        Assert.True(RunB(svc).Ok);
        Assert.True(RunB(svc).Ok);

        Assert.Equal(3, _file.Reads);
        Assert.Empty(ReReads); // same list: nothing to say
    }

    [Fact]
    public void Refresh_ReadFailsThenSucceeds_RemovalApplies()
    {
        _file.Text = Config();
        var state = State(PinnedB);
        var reloader = new ApprovalReloader(_file, state, _log);
        _file.Failures.Enqueue(new IOException("in use"));

        reloader.Refresh();
        reloader.Refresh();

        Assert.Equal(ApprovalKind.None, state.Current.Lookup("NVE_65", "STATUS_B").Kind);
    }

    [Fact]
    public void Refresh_SameBadText_WarnsOnce()
    {
        _file.Text = "{ not json";
        var state = State(PinnedB);
        var reloader = new ApprovalReloader(_file, state, _log);

        reloader.Refresh();
        reloader.Refresh();
        reloader.Refresh();

        Assert.Single(Warnings);
        ApprovalTests.AssertCounts(1, 0, state.Current.Counts);
    }

    [Fact]
    public void Refresh_FileMissing_KeepsListAndWarnsOnce()
    {
        var state = State(PinnedB);
        var reloader = new ApprovalReloader(_file, state, _log);
        for (var i = 0; i < 3; i++)
        {
            _file.Failures.Enqueue(new FileNotFoundException("gone"));
        }

        reloader.Refresh();
        reloader.Refresh();
        reloader.Refresh();

        Assert.Single(Warnings);
        ApprovalTests.AssertCounts(1, 0, state.Current.Counts);
    }

    [Theory]
    [InlineData("{ \"reviewedJobs\": [\"NVE_65:STATUS_B\" ")]
    [InlineData("[]")]
    [InlineData("{ \"reviewedJobs\": [], \"port\": 0 }")]
    public void BrokenFile_KeepsTheOldList_WarnsOnce_LaterFixIsPickedUp(string broken)
    {
        _file.Text = Config(PinnedB);
        var svc = Service(State(PinnedB));
        Assert.True(RunB(svc).Ok);

        _file.Write(broken);
        Assert.True(RunB(svc).Ok);
        Assert.True(RunB(svc).Ok);
        Assert.Single(Warnings);

        _file.Write(Config());
        Assert.Equal("job_needs_review", RunB(svc).Failure!.Error.Code);
        Assert.Equal(["INFO reviewedJobs re-read: 0 pinned, 0 ignored"], ReReads);
        Assert.Single(Warnings);
    }

    [Fact]
    public void EntryOfTheWrongShape_ApprovesNothing_TheRestIsReRead_WarnsOnce()
    {
        _file.Text = Config(PinnedB);
        var svc = Service(State(PinnedB));
        Assert.True(RunB(svc).Ok);

        // A valid file with one bad entry: the file is still the truth, the bad entry approves nothing.
        _file.Write("{ \"reviewedJobs\": [\"NVE_65:STATUS_B@XYZ\"] }");
        Assert.Equal("job_needs_review", RunB(svc).Failure!.Error.Code);
        Assert.Equal("job_needs_review", RunB(svc).Failure!.Error.Code);
        Assert.Equal(["INFO reviewedJobs re-read: 0 pinned, 1 ignored"], ReReads);
        var w = Assert.Single(Warnings);
        Assert.Contains("reviewedJobs[0]", w);
    }

    [Fact]
    public void UnpinnedEntry_ApprovesNothing_WarnsWithThePinnedForm_CountsIgnored()
    {
        _file.Text = Config(PinnedB);
        var state = State(PinnedB);
        var svc = Service(state);
        Assert.True(RunB(svc).Ok);

        // An entry from before 0.5.0: the job is refused, and the log says what to write instead.
        _file.Write(Config("NVE_65:STATUS_B"));
        Assert.Equal("job_needs_review", RunB(svc).Failure!.Error.Code);
        Assert.Equal("job_needs_review", RunB(svc).Failure!.Error.Code);
        ApprovalTests.AssertCounts(0, 1, state.Current.Counts);
        Assert.Equal(["INFO reviewedJobs re-read: 0 pinned, 1 ignored"], ReReads);
        var w = Assert.Single(Warnings);
        Assert.Contains("\"NVE_65:STATUS_B\" is unpinned", w);
        Assert.Contains("ECU:JOB@<hash>", w);
        Assert.Contains("ediabas_describe", w);
    }

    [Fact]
    public void MissingOrUnreadableFile_KeepsTheOldList_WarnsOncePerChange()
    {
        _file.Text = Config(PinnedB);
        var svc = Service(State(PinnedB));

        _file.Failures.Enqueue(new FileNotFoundException("gone"));
        _file.Failures.Enqueue(new FileNotFoundException("gone"));
        Assert.True(RunB(svc).Ok);
        Assert.True(RunB(svc).Ok);
        Assert.Single(Warnings);

        _file.Failures.Enqueue(new IOException("in use"));
        _file.Failures.Enqueue(new IOException("in use"));
        Assert.True(RunB(svc).Ok);
        Assert.True(RunB(svc).Ok);
        Assert.Equal(2, Warnings.Count());

        _file.Write(Config());
        Assert.Equal("job_needs_review", RunB(svc).Failure!.Error.Code);
    }

    [Fact]
    public void OnlyTheListIsReRead()
    {
        _file.Text = Config();
        var state = State();
        var svc = Service(state);
        Assert.Equal(30, svc.EffectiveTimeout(null));

        _file.Write("""{ "reviewedJobs": ["NVE_65:STATUS_B@7b69322a754f"], "jobTimeoutSeconds": 2, "maxJobTimeoutSeconds": 5, "conflictProcesses": [], "ediabasIdleReleaseSeconds": 5 }""");
        Assert.True(RunB(svc).Ok);

        ApprovalTests.AssertCounts(1, 0, state.Current.Counts);
        Assert.Equal(30, svc.EffectiveTimeout(null));
        Assert.Equal(60, svc.EffectiveTimeout(600));
    }

    [Fact]
    public void ReRead_HappensBeforeThePolicy_OnEveryEdiabasRequest()
    {
        _file.Text = Config();
        var svc = Service(State());
        _api.On("NVE_65 _JOBS", FakeJob.Jobs("STATUS_B"));
        _api.On("NVE_65 _ARGUMENTS STATUS_B", new FakeJob { Sets = [[]] });
        _api.On("NVE_65 _RESULTS STATUS_B", new FakeJob { Sets = [[]] });

        _file.Write(Config(PinnedB));
        Assert.Equal(Review.Reviewed, svc.DescribeJob("NVE_65", "STATUS_B", new RequestAudit(), default).Value!.Verdict.Review);

        _file.Write(Config());
        Assert.Equal(Review.NeedsReview, svc.DescribeList("NVE_65", null, true, new RequestAudit(), default).Value!.Jobs[0].Verdict.Review);

        _file.Write(Config(PinnedB));
        Assert.True(svc.RunJobs([new("NVE_65", "STATUS_B")], null, new RequestAudit(), default).Value!.Items[0].Ok);

        _file.Write(Config());
        Assert.Equal("job_needs_review", RunB(svc).Failure!.Error.Code);
    }

    [Fact]
    public void ReRead_NeverLetsADeniedJobRun()
    {
        _file.Text = Config();
        var svc = Service(State());
        _file.Write(Config("NVE_65:FS_LOESCHEN", $"NVE_65:FS_LOESCHEN@{StartetBHash}"));

        var r = svc.RunJob(new JobRequest("NVE_65", "FS_LOESCHEN"), null, new RequestAudit(), default);
        Assert.Equal("job_not_allowed", r.Failure!.Error.Code);
        Assert.Empty(_api.Calls);
    }
}
