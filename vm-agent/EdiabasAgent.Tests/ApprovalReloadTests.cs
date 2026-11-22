using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

/// <summary>A scripted config.json: its last-write time and text.</summary>
public sealed class FakeConfigFile : IConfigFile
{
    /// <summary>Null: the file is missing.</summary>
    public DateTime? Stamp { get; set; } = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);

    public string Text { get; set; } = "{}";

    public bool ReadThrows { get; set; }

    public int Reads { get; private set; }

    public DateTime? LastWriteUtc() => Stamp;

    public string ReadAllText()
    {
        Reads++;
        return ReadThrows ? throw new IOException("in use") : Text;
    }

    /// <summary>A new write: later time, new text.</summary>
    public void Write(string text)
    {
        Stamp = (Stamp ?? new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc)).AddSeconds(1);
        Text = text;
    }
}

/// <summary>reviewedJobs is re-read when config.json changes; nothing else is.</summary>
public class ApprovalReloadTests
{
    private readonly FakeEdiabasApi _api = new();
    private readonly FakeClock _clock = new();
    private readonly FakeLog _log = new();
    private readonly FakeConfigFile _file = new();

    private const string StartetBHash = "7b69322a754f";

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
        Assert.Equal(new ApprovalCounts(1, 0), state.Current.Counts);
        Assert.Equal(["INFO reviewedJobs re-read: 1 pinned, 0 unpinned"], ReReads);
        Assert.DoesNotContain(_log.Lines, l => l.Contains(StartetBHash, StringComparison.Ordinal));
    }

    [Fact]
    public void Unchanged_File_IsNotReadAgain()
    {
        _file.Text = Config("NVE_65:STATUS_B");
        var svc = Service(State("NVE_65:STATUS_B"));
        Assert.True(RunB(svc).Ok);
        Assert.True(RunB(svc).Ok);
        Assert.True(RunB(svc).Ok);

        Assert.Equal(1, _file.Reads); // the first request, to be sure nothing changed since start-up
        Assert.Empty(ReReads); // same list: nothing to say
    }

    [Theory]
    [InlineData("{ \"reviewedJobs\": [\"NVE_65:STATUS_B\" ")]
    [InlineData("[]")]
    [InlineData("{ \"reviewedJobs\": [], \"port\": 0 }")]
    public void BrokenFile_KeepsTheOldList_WarnsOnce_LaterFixIsPickedUp(string broken)
    {
        _file.Text = Config("NVE_65:STATUS_B");
        var svc = Service(State("NVE_65:STATUS_B"));
        Assert.True(RunB(svc).Ok);

        _file.Write(broken);
        Assert.True(RunB(svc).Ok);
        Assert.True(RunB(svc).Ok);
        Assert.Single(Warnings);

        _file.Write(Config());
        Assert.Equal("job_needs_review", RunB(svc).Failure!.Error.Code);
        Assert.Equal(["INFO reviewedJobs re-read: 0 pinned, 0 unpinned"], ReReads);
        Assert.Single(Warnings);
    }

    [Fact]
    public void EntryOfTheWrongShape_ApprovesNothing_TheRestIsReRead_WarnsOnce()
    {
        _file.Text = Config("NVE_65:STATUS_B");
        var svc = Service(State("NVE_65:STATUS_B"));
        Assert.True(RunB(svc).Ok);

        // A valid file with one bad entry: the file is still the truth, the bad entry approves nothing.
        _file.Write("{ \"reviewedJobs\": [\"NVE_65:STATUS_B@XYZ\"] }");
        Assert.Equal("job_needs_review", RunB(svc).Failure!.Error.Code);
        Assert.Equal("job_needs_review", RunB(svc).Failure!.Error.Code);
        Assert.Equal(["INFO reviewedJobs re-read: 0 pinned, 0 unpinned"], ReReads);
        var w = Assert.Single(Warnings);
        Assert.Contains("reviewedJobs[0]", w);
    }

    [Fact]
    public void MissingOrUnreadableFile_KeepsTheOldList_WarnsOncePerChange()
    {
        _file.Text = Config("NVE_65:STATUS_B");
        var svc = Service(State("NVE_65:STATUS_B"));

        _file.Stamp = null;
        Assert.True(RunB(svc).Ok);
        Assert.True(RunB(svc).Ok);
        Assert.Single(Warnings);

        _file.Write(Config());
        _file.ReadThrows = true;
        Assert.True(RunB(svc).Ok);
        Assert.True(RunB(svc).Ok);
        Assert.Equal(2, Warnings.Count());

        _file.ReadThrows = false;
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

        _file.Write("""{ "reviewedJobs": ["NVE_65:STATUS_B"], "jobTimeoutSeconds": 2, "maxJobTimeoutSeconds": 5, "conflictProcesses": [], "ediabasIdleReleaseSeconds": 5 }""");
        Assert.True(RunB(svc).Ok);

        Assert.Equal(new ApprovalCounts(0, 1), state.Current.Counts);
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

        _file.Write(Config("NVE_65:STATUS_B"));
        Assert.Equal(Review.Reviewed, svc.DescribeJob("NVE_65", "STATUS_B", new RequestAudit(), default).Value!.Verdict.Review);

        _file.Write(Config());
        Assert.Equal(Review.NeedsReview, svc.DescribeList("NVE_65", null, true, new RequestAudit(), default).Value!.Jobs[0].Verdict.Review);

        _file.Write(Config("NVE_65:STATUS_B"));
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
