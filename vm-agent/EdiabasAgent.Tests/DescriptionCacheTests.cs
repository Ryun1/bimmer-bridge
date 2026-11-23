using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

/// <summary>Issue #16: describe caches descriptions by SGBD file hash and says when it was cut off.</summary>
public class DescriptionCacheTests
{
    private readonly FakeEdiabasApi _api = new();
    private readonly FakeClock _clock = new();
    private readonly FakeConflicts _conflicts = new();
    private readonly FakeLog _log = new();
    private readonly FakeEcus _ecus = new("NVE_65");
    private readonly DescriptionCache _cache = new();

    public DescriptionCacheTests()
    {
        // FakeJob.Jobs reports variant VAR: the file that answered.
        _ecus.Hashes["VAR"] = "hash1";
    }

    private EdiabasService Service(int maxTimeout = 60) =>
        new(
            _api,
            _conflicts,
            _ecus,
            JobPolicy.Embedded,
            new CommentScreen(JobPolicy.Embedded.ScreenTerms, JobPolicy.Embedded.ScreenIgnore, []),
            _clock,
            new CableUse(),
            _log,
            new DllLoadState(),
            30,
            maxTimeout,
            20,
            descriptions: _cache);

    private static FakeResult Text(string name, string value) => new(name, ApiResultFormat.Text, value);

    private static FakeJob SlowComment(int seconds, string text) =>
        new() { BusyPolls = seconds * 1000 / EdiabasService.PollIntervalMs, Sets = [[], [Text("JOBCOMMENT0", text)]] };

    private void ScriptThreeStatusJobs()
    {
        _api.On("NVE_65 _JOBS", FakeJob.Jobs("STATUS_A", "STATUS_B", "STATUS_C"));
        _api.On("NVE_65 _JOBCOMMENTS STATUS_A", SlowComment(2, "Status A lesen"));
        _api.On("NVE_65 _JOBCOMMENTS STATUS_B", SlowComment(5, "Status B lesen"));
        _api.On("NVE_65 _JOBCOMMENTS STATUS_C", FakeJob.Comments("Status C lesen"));
    }

    private int CommentCalls => _api.JobCalls.Count(c => c.Contains("_JOBCOMMENTS", StringComparison.Ordinal));

    [Fact]
    public void SecondDescribe_ReadsDescriptionsFromTheCache()
    {
        ScriptThreeStatusJobs();
        var first = Service().DescribeList("NVE_65", null, true, new RequestAudit(), default).Value!;
        Assert.Equal(3, CommentCalls);
        Assert.Null(first.CommentsCutOff);

        var second = Service().DescribeList("NVE_65", null, true, new RequestAudit(), default).Value!;

        Assert.Equal(3, CommentCalls); // none more
        Assert.Equal(first.Jobs, second.Jobs);
        Assert.All(second.Jobs, j => Assert.Equal(Review.Passed, j.Verdict.Review));
    }

    [Fact]
    public void CutOffByTime_IsReported_AndAskingAgainGoesFurther()
    {
        ScriptThreeStatusJobs();

        // 6 s: A (2 s) is read, B (5 s) hits the limit, C is never started.
        var first = Service(maxTimeout: 6).DescribeList("NVE_65", null, true, new RequestAudit(), default).Value!;
        Assert.Equal(CutOff.TimeLimit, first.CommentsCutOff);
        Assert.Equal([Review.Passed, Review.NeedsReview, Review.NotChecked], first.Jobs.Select(j => j.Verdict.Review));

        // Again: A from the cache, so B and C fit.
        var second = Service(maxTimeout: 6).DescribeList("NVE_65", null, true, new RequestAudit(), default).Value!;
        Assert.Null(second.CommentsCutOff);
        Assert.Equal([Review.Passed, Review.Passed, Review.Passed], second.Jobs.Select(j => j.Verdict.Review));
        Assert.Equal(1, _api.JobCalls.Count(c => c == "Job NVE_65 _JOBCOMMENTS [STATUS_A] []"));
    }

    [Fact]
    public void FailedRead_IsNotCached()
    {
        _api.On("NVE_65 _JOBS", FakeJob.Jobs("STATUS_A"));
        _api.On("NVE_65 _JOBCOMMENTS STATUS_A", FakeJob.Error(100, "SYS-0010: INITIALIZATION ERROR"));

        var first = Service().DescribeList("NVE_65", null, true, new RequestAudit(), default).Value!;
        Assert.Equal(Review.NeedsReview, first.Jobs[0].Verdict.Review);
        Assert.Null(first.CommentsCutOff); // failed, not cut off
        Service().DescribeList("NVE_65", null, true, new RequestAudit(), default);

        Assert.Equal(2, CommentCalls);
        Assert.Equal(0, _cache.Count);
    }

    [Fact]
    public void NoFileHash_NoCaching()
    {
        _ecus.Hashes.Clear();
        ScriptThreeStatusJobs();

        Service().DescribeList("NVE_65", null, true, new RequestAudit(), default);
        Service().DescribeList("NVE_65", null, true, new RequestAudit(), default);

        Assert.Equal(6, CommentCalls);
    }

    [Fact]
    public void ChangedFile_IsReadAgain()
    {
        ScriptThreeStatusJobs();
        Service().DescribeList("NVE_65", null, true, new RequestAudit(), default);

        _ecus.Hashes["VAR"] = "hash2";
        _api.On("NVE_65 _JOBCOMMENTS STATUS_A", FakeJob.Comments("Schreibt A"));
        var r = Service().DescribeList("NVE_65", null, true, new RequestAudit(), default).Value!;

        Assert.Equal(6, CommentCalls);
        Assert.Equal("Schreibt A", r.Jobs[0].Comment);
        Assert.Equal(Review.NeedsReview, r.Jobs[0].Verdict.Review);
    }

    [Fact]
    public void NoComments_NoCutOff_NoHashing()
    {
        ScriptThreeStatusJobs();
        _ecus.Hashes.Clear();
        var r = Service(maxTimeout: 1).DescribeList("NVE_65", null, false, new RequestAudit(), default).Value!;

        Assert.Null(r.CommentsCutOff);
        Assert.Equal(0, CommentCalls);
    }

    [Fact]
    public void ToolStarted_IsTheCutOffReason()
    {
        ScriptThreeStatusJobs();

        // Clean before the request, then INPA appears while the first description is read.
        _conflicts.Answers.Clear();
        _conflicts.Answers.Add([]);
        _api.DuringJob = () =>
        {
            if (_api.JobCalls.Count() == 2)
            {
                _conflicts.Answers.Clear();
                _conflicts.Answers.Add([new ConflictingProcess("inpaload", 42)]);
            }
        };

        var r = Service().DescribeList("NVE_65", null, true, new RequestAudit(), default).Value!;

        Assert.Equal(CutOff.ToolStarted, r.CommentsCutOff);
        Assert.All(r.Jobs.Skip(1), j => Assert.Equal(Review.NotChecked, j.Verdict.Review));
    }

    [Fact]
    public void DescribeJob_UsesTheCacheTheListFilled()
    {
        ScriptThreeStatusJobs();
        _api.On("NVE_65 _ARGUMENTS STATUS_A", FakeJob.Ok());
        _api.On("NVE_65 _RESULTS STATUS_A", FakeJob.Ok());
        Service().DescribeList("NVE_65", null, true, new RequestAudit(), default);

        var d = Service().DescribeJob("NVE_65", "status_a", new RequestAudit(), default).Value!;

        Assert.Equal("Status A lesen", d.Comment);
        Assert.Equal(Review.Passed, d.Verdict.Review);
        Assert.Equal(3, CommentCalls);
    }

    [Fact]
    public void Cache_IsKeyedByFileAndJob_CaseInsensitiveJob()
    {
        var c = new DescriptionCache();
        c.Add("h", "status_a", ["x"]);

        Assert.True(c.TryGet("h", "STATUS_A", out var lines));
        Assert.Equal(["x"], lines);
        Assert.False(c.TryGet("other", "STATUS_A", out _));
    }
}

public class EcuFolderHashTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ecu-hash-" + Guid.NewGuid().ToString("N"));

    public EcuFolderHashTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void PrgHash_IsSha256OfTheFile_AndFollowsChanges()
    {
        var path = Path.Combine(_dir, "NVE_65.prg");
        File.WriteAllBytes(path, [1, 2, 3]);
        var folder = new EcuFolder(_dir);

        var h1 = folder.PrgHash("nve_65");
        Assert.Equal("039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81", h1);

        File.WriteAllBytes(path, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.NotEqual(h1, folder.PrgHash("NVE_65"));
    }

    [Theory]
    [InlineData("MISSING")]
    [InlineData("../NVE_65")]
    [InlineData("NVE_65.prg")]
    [InlineData("")]
    public void PrgHash_NullForMissingOrNonPlainNames(string name)
    {
        File.WriteAllBytes(Path.Combine(_dir, "NVE_65.prg"), [1]);
        Assert.Null(new EcuFolder(_dir).PrgHash(name));
    }

    [Fact]
    public void PrgHash_IgnoresGroupFiles()
    {
        File.WriteAllBytes(Path.Combine(_dir, "D_MOTOR.grp"), [1]);
        Assert.Null(new EcuFolder(_dir).PrgHash("D_MOTOR"));
    }
}
