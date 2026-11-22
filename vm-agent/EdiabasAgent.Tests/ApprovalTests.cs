using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

/// <summary>Pinned approvals: <c>ECU:JOB@hash</c> in <c>reviewedJobs</c> (docs/safety.md, "Approving one job").</summary>
public class ApprovalTests
{
    private readonly FakeEdiabasApi _api = new();
    private readonly FakeClock _clock = new();
    private readonly FakeConflicts _conflicts = new();
    private readonly FakeLog _log = new();
    private readonly CableUse _cable = new();
    private readonly RequestAudit _audit = new();
    private readonly DllLoadState _dll = new();

    // SHA-256("Status lesen") = b8ae0dd9d242 7c80...; SHA-256("Startet B") = 7b69322a754f a9b6...
    private const string StatusLesenHash = "b8ae0dd9d242";
    private const string StartetBHash = "7b69322a754f";

    private EdiabasService Service(params string[] reviewed) =>
        new(
            _api,
            _conflicts,
            new FakeEcus("NVE_65", "TMODE", "UTILITY"),
            JobPolicy.Embedded,
            new CommentScreen(JobPolicy.Embedded.ScreenTerms, JobPolicy.Embedded.ScreenIgnore, reviewed),
            _clock,
            _cable,
            _log,
            _dll,
            30,
            60,
            20);

    private static FakeResult Text(string name, string value) => new(name, ApiResultFormat.Text, value);

    // ---- the hash ----

    [Fact]
    public void DescriptionHash_KnownAnswers()
    {
        Assert.Equal(StatusLesenHash, CommentScreen.DescriptionHash("Status lesen"));
        Assert.Equal(StartetBHash, CommentScreen.DescriptionHash("Startet B"));

        // Trimmed, UTF-8: SHA-256("Status der Klemme 30 – ändern? nein") = 20b0f672ec37...
        Assert.Equal("20b0f672ec37", CommentScreen.DescriptionHash("  Status der Klemme 30 – ändern? nein\n"));
        Assert.Equal(StatusLesenHash, CommentScreen.DescriptionHash("\tStatus lesen \n"));
    }

    // ---- entry shapes ----

    [Theory]
    [InlineData("NVE_65:STATUS_X", "NVE_65:STATUS_X", null)]
    [InlineData("NVE_65:STATUS_X@b8ae0dd9d242", "NVE_65:STATUS_X", "b8ae0dd9d242")]
    public void Entry_Parses(string entry, string key, string? hash)
    {
        Assert.True(ApprovalList.TryParseEntry(entry, out var k, out var h));
        Assert.Equal(key, k);
        Assert.Equal(hash, h);
    }

    [Theory]
    [InlineData("ECU:JOB@XYZ")]
    [InlineData("ECU:JOB@")]
    [InlineData("ECU:JOB@B8AE0DD9D242")]
    [InlineData("ECU:JOB@b8ae0dd9d24")]
    [InlineData("ECU:JOB@b8ae0dd9d2427")]
    [InlineData("ECU:JOB@b8ae0dd9d242@b8ae0dd9d242")]
    [InlineData("ECUJOB")]
    [InlineData("ECU:")]
    [InlineData(":JOB")]
    [InlineData("ECU:JOB:X")]
    [InlineData("ECU:JOB ")]
    [InlineData("ECU :JOB")]
    public void Entry_BadShape(string entry)
    {
        Assert.False(ApprovalList.TryParseEntry(entry, out _, out _));
    }

    [Fact]
    public void Config_UnpinnedEntry_LoadsWithAWarning()
    {
        // Not an error: an agent updated before its config must still start.
        var r = AgentConfig.Parse("""{ "reviewedJobs": ["NVE_65:STATUS_X", "NVE_65:STATUS_Y@b8ae0dd9d242"] }""");
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        Assert.Equal(["NVE_65:STATUS_X", "NVE_65:STATUS_Y@b8ae0dd9d242"], r.Config!.ReviewedJobs);
        Assert.StartsWith("reviewedJobs[0]:", Assert.Single(r.Warnings));
    }

    [Theory]
    [InlineData("ECU:JOB@XYZ")]
    [InlineData("ECU:JOB@")]
    [InlineData("ECU:JOB@B8AE0DD9D242")]
    [InlineData("NVE_65")]
    public void Config_BadEntryIsAWarningAndApprovesNothing(string entry)
    {
        // Not an error: a typo in one approval must not stop the agent from starting.
        var r = AgentConfig.Parse($$"""{ "reviewedJobs": ["NVE_65:STATUS_X@b8ae0dd9d242", "{{entry}}"] }""");
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        var w = Assert.Single(r.Warnings);
        Assert.StartsWith("reviewedJobs[1]:", w);

        var list = new ApprovalList(r.Config!.ReviewedJobs);
        Assert.Equal(1, list.PinnedCount);
        Assert.Equal(1, list.IgnoredCount);
        Assert.Equal(ApprovalKind.None, list.Lookup("ECU", "JOB").Kind);
        Assert.Equal(ApprovalKind.None, list.Lookup("NVE_65", "").Kind);
    }

    [Fact]
    public void Counts()
    {
        var list = new ApprovalList(["A:B", "A:B", "A:C@b8ae0dd9d242", "A:C@7b69322a754f", "A:B@b8ae0dd9d242", "A:B@XYZ", "A:B@XYZ"]);
        Assert.Equal(3, list.PinnedCount);
        Assert.Equal(2, list.IgnoredCount); // "A:B" and "A:B@XYZ", each once
        Assert.Equal(["A:B@b8ae0dd9d242", "A:C@7b69322a754f", "A:C@b8ae0dd9d242"], list.Entries);
    }

    // ---- running a pinned job ----

    [Fact]
    public void RunJob_PinnedApproval_NoArgs_SkipsDescriptionScreen_Runs()
    {
        // "Startet B" fails the description screen; the pin skips it, and with no arguments
        // there is nothing for the argument screen to read.
        _api.On("NVE_65 _JOBCOMMENTS STATUS_B", FakeJob.Comments("Startet B"));
        _api.On("NVE_65 STATUS_B", FakeJob.Ok());

        var r = Service($"NVE_65:STATUS_B@{StartetBHash}").RunJob(new JobRequest("nve_65", "status_b"), null, _audit, default);

        Assert.True(r.Ok);
        Assert.Equal(["Job NVE_65 _JOBCOMMENTS [STATUS_B] []", "Job NVE_65 STATUS_B [] []"], _api.JobCalls);
        Assert.Null(_audit.Rule);
    }

    [Fact]
    public void RunJob_PinnedApproval_WithArgs_ArgumentScreenStillRuns()
    {
        _api.On("NVE_65 _JOBCOMMENTS STATUS_X", FakeJob.Comments("Status lesen"));
        _api.On("NVE_65 _ARGUMENTS STATUS_X", new FakeJob { Sets = [[], [Text("ARG", "WERT"), Text("ARGCOMMENT0", "Wert setzen")]] });
        _api.On("NVE_65 STATUS_X", FakeJob.Ok());

        var r = Service($"NVE_65:STATUS_X@{StatusLesenHash}").RunJob(new JobRequest("NVE_65", "STATUS_X", "1"), null, _audit, default);

        Assert.Equal("job_needs_review", r.Failure!.Error.Code);
        Assert.StartsWith("The argument description of STATUS_X mentions: ", r.Failure.Error.Message);
        Assert.Equal(["Job NVE_65 _JOBCOMMENTS [STATUS_X] []", "Job NVE_65 _ARGUMENTS [STATUS_X] []"], _api.JobCalls);
        Assert.Equal(RequestAudit.ScreenRule, _audit.Rule);
    }

    [Fact]
    public void RunJob_PinnedApproval_WithDescribedArgs_Runs()
    {
        _api.On("NVE_65 _JOBCOMMENTS STATUS_X", FakeJob.Comments("Status lesen"));
        _api.On("NVE_65 _ARGUMENTS STATUS_X", new FakeJob { Sets = [[], [Text("ARG", "KANAL"), Text("ARGCOMMENT0", "Kanalnummer 1-4")]] });
        _api.On("NVE_65 STATUS_X", FakeJob.Ok());

        var r = Service($"NVE_65:STATUS_X@{StatusLesenHash}").RunJob(new JobRequest("NVE_65", "STATUS_X", "1"), null, _audit, default);

        Assert.True(r.Ok);
        Assert.Equal(
            ["Job NVE_65 _JOBCOMMENTS [STATUS_X] []", "Job NVE_65 _ARGUMENTS [STATUS_X] []", "Job NVE_65 STATUS_X [1] []"],
            _api.JobCalls);
    }

    [Fact]
    public void Pinned_Match_EvenWhenTheScreenWouldRefuse()
    {
        _api.On("NVE_65 _JOBCOMMENTS STATUS_B", FakeJob.Comments("Startet B"));
        _api.On("NVE_65 STATUS_B", FakeJob.Ok());

        Assert.True(Service($"NVE_65:STATUS_B@{StartetBHash}").RunJob(new JobRequest("NVE_65", "STATUS_B"), null, _audit, default).Ok);
        Assert.Equal(2, _api.JobCalls.Count());
    }

    [Fact]
    public void Pinned_Mismatch_IsNeedsReview_WithTheNewHash_JobNeverRuns()
    {
        _api.On("NVE_65 _JOBCOMMENTS STATUS_X", FakeJob.Comments("Status lesen"));

        var r = Service("NVE_65:STATUS_X@0123456789ab").RunJob(new JobRequest("NVE_65", "STATUS_X"), null, _audit, default);

        Assert.Equal(403, r.Failure!.Status);
        Assert.Equal("job_needs_review", r.Failure.Error.Code);
        Assert.Equal(
            $"The description of STATUS_X has changed since it was approved (now {StatusLesenHash}). The user can approve it again in the agent config.",
            r.Failure.Error.Message);
        var d = Assert.IsType<JobReviewDetails>(r.Failure.Error.Details);
        Assert.Equal("Status lesen", d.Description);
        Assert.Equal(["Job NVE_65 _JOBCOMMENTS [STATUS_X] []"], _api.JobCalls);
        Assert.Equal(RequestAudit.ScreenRule, _audit.Rule);
    }

    [Fact]
    public void Pinned_EmptyDescription_IsNeedsReview()
    {
        _api.On("NVE_65 _JOBCOMMENTS STATUS_X", FakeJob.Comments());
        var r = Service($"NVE_65:STATUS_X@{StatusLesenHash}").RunJob(new JobRequest("NVE_65", "STATUS_X"), null, _audit, default);

        Assert.Equal("job_needs_review", r.Failure!.Error.Code);
        Assert.Null(((JobReviewDetails)r.Failure.Error.Details!).Description);
        Assert.Single(_api.JobCalls);
    }

    [Fact]
    public void Pinned_UnreadableDescription_IsNeedsReview()
    {
        _api.On("NVE_65 _JOBCOMMENTS STATUS_X", FakeJob.Error(100, "SYS-0010"));
        var r = Service($"NVE_65:STATUS_X@{StatusLesenHash}").RunJob(new JobRequest("NVE_65", "STATUS_X"), null, _audit, default);

        Assert.Equal("job_needs_review", r.Failure!.Error.Code);
        Assert.Single(_api.JobCalls);
    }

    [Fact]
    public void Pinned_RequestFailuresKeepTheirCode()
    {
        // The client goes away while the description is being read: not_run, not needs_review.
        _api.On("NVE_65 _JOBCOMMENTS STATUS_X", new FakeJob { BusyPolls = 5, Sets = [[], [Text("JOBCOMMENT0", "Status lesen")]] });
        using var cts = new CancellationTokenSource();
        _api.DuringJob = cts.Cancel;
        var r = Service("NVE_65:STATUS_X@0123456789ab").RunJob(new JobRequest("NVE_65", "STATUS_X"), null, _audit, cts.Token);

        Assert.Equal("not_run", r.Failure!.Error.Code);
        Assert.Null(_audit.Rule);
        Assert.Equal(["Job NVE_65 _JOBCOMMENTS [STATUS_X] []"], _api.JobCalls);
    }

    internal static void AssertCounts(int pinned, int ignored, ApprovalCounts counts)
    {
        Assert.Equal(pinned, counts.Pinned);
        Assert.Equal(ignored, counts.Ignored);
    }

    [Fact]
    public void Health_Approvals_HashIsStableForSameEntries_DiffersWhenOneAdded()
    {
        var a = new ApprovalList(["NVE_65:STATUS_X@b8ae0dd9d242", "NVE_65:STATUS_Y@7b69322a754f"]).Counts;
        var reordered = new ApprovalList(["NVE_65:STATUS_Y@7b69322a754f", "NVE_65:STATUS_X@b8ae0dd9d242", "NVE_65:STATUS_X@b8ae0dd9d242"]).Counts;
        var added = new ApprovalList(["NVE_65:STATUS_X@b8ae0dd9d242", "NVE_65:STATUS_Y@7b69322a754f", "NVE_65:STATUS_Z@b8ae0dd9d242"]).Counts;
        Assert.Equal(["NVE_65:STATUS_X@b8ae0dd9d242", "NVE_65:STATUS_Y@7b69322a754f"], a.Entries);
        Assert.Matches("^[0-9a-f]{12}$", a.Hash);
        Assert.Equal(a.Hash, reordered.Hash);
        Assert.NotEqual(a.Hash, added.Hash);
        // SHA-256("") starts e3b0c44298fc
        Assert.Equal("e3b0c44298fc", ApprovalList.Empty.Counts.Hash);
    }

    [Fact]
    public void ApprovalList_LowerCaseEcuOrJob_CountsIgnored_NotInEntries_WarningSaysUpperCase()
    {
        var list = new ApprovalList(["nve_65:STATUS_X@b8ae0dd9d242", "NVE_65:status_x@b8ae0dd9d242", "NVE_65:STATUS_X@b8ae0dd9d242"]);
        Assert.Equal(1, list.PinnedCount);
        Assert.Equal(2, list.IgnoredCount);
        Assert.Equal(["NVE_65:STATUS_X@b8ae0dd9d242"], list.Entries);
        var r = AgentConfig.Parse("{ \"reviewedJobs\": [\"nve_65:STATUS_X@b8ae0dd9d242\"] }");
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        Assert.Contains("upper case", Assert.Single(r.Warnings));
    }

    [Fact]
    public void ApprovalList_UnpinnedEntry_ApprovesNothing_CountsIgnored()
    {
        var list = new ApprovalList(["NVE_65:STATUS_X"]);
        Assert.Equal(ApprovalKind.None, list.Lookup("NVE_65", "STATUS_X").Kind);
        Assert.Equal(0, list.PinnedCount);
        Assert.Equal(1, list.IgnoredCount);
        Assert.Empty(list.Entries);
        AssertCounts(0, 1, list.Counts);
    }

    [Fact]
    public void ApprovalList_UnpinnedEntry_LogsWarningNamingThePinnedForm()
    {
        var r = AgentConfig.Parse("""{ "reviewedJobs": ["NVE_65:STATUS_Y@b8ae0dd9d242", "NVE_65:STATUS_X"] }""");
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        var w = Assert.Single(r.Warnings);
        Assert.Equal(
            "reviewedJobs[1]: \"NVE_65:STATUS_X\" is unpinned and approves nothing since 0.5.0; run ediabas_describe on the job and use the \"ECU:JOB@<hash>\" line it prints",
            w);
        Assert.Contains("ECU:JOB@<hash>", w);
        Assert.Contains("ediabas_describe", w);
    }

    [Fact]
    public void Unpinned_IsRefused_LikeAnUnapprovedJob()
    {
        _api.On("NVE_65 _JOBCOMMENTS STATUS_B", FakeJob.Comments("Startet B"));
        _api.On("NVE_65 STATUS_B", FakeJob.Ok());
        var r = Service("NVE_65:STATUS_B").RunJob(new JobRequest("NVE_65", "STATUS_B"), null, _audit, default);

        Assert.Equal("job_needs_review", r.Failure!.Error.Code);
        Assert.Equal(["Job NVE_65 _JOBCOMMENTS [STATUS_B] []"], _api.JobCalls);
    }

    [Fact]
    public void Unpinned_BesideAPin_ChangesNothing()
    {
        _api.On("NVE_65 _JOBCOMMENTS STATUS_B", FakeJob.Comments("Startet B"));
        var r = Service("NVE_65:STATUS_B", "NVE_65:STATUS_B@0123456789ab").RunJob(new JobRequest("NVE_65", "STATUS_B"), null, _audit, default);

        Assert.Equal("job_needs_review", r.Failure!.Error.Code);
        Assert.Single(_api.JobCalls);
    }

    [Fact]
    public void SeveralPins_AnyMatchApproves()
    {
        _api.On("NVE_65 _JOBCOMMENTS STATUS_B", FakeJob.Comments("Startet B"));
        _api.On("NVE_65 STATUS_B", FakeJob.Ok());
        Assert.True(Service("NVE_65:STATUS_B@0123456789ab", $"NVE_65:STATUS_B@{StartetBHash}").RunJob(new JobRequest("NVE_65", "STATUS_B"), null, _audit, default).Ok);
    }

    [Theory]
    [InlineData("NVE_65", "FS_LOESCHEN", "deny.substring:LOESCH")]
    [InlineData("NVE_65", "STEUERN_X", "deny.substring:STEUER")]
    [InlineData("NVE_65", "C_C_LESEN", "default_deny")]
    [InlineData("TMODE", "STATUS_X", "ecu_restricted")]
    public void AnyApproval_ForADeniedJob_IsStillNotAllowed(string ecu, string job, string rule)
    {
        var svc = Service($"{ecu}:{job}", $"{ecu}:{job}@{StatusLesenHash}");
        var r = svc.RunJob(new JobRequest(ecu, job), null, _audit, default);

        Assert.Equal("job_not_allowed", r.Failure!.Error.Code);
        Assert.Equal(rule, ((JobRefusalDetails)r.Failure.Error.Details!).Rule);
        Assert.Empty(_api.Calls);
    }

    // ---- describe agrees with a run ----

    private void ScriptDescribe(string job, string description)
    {
        _api.On("NVE_65 _JOBS", FakeJob.Jobs(job));
        _api.On($"NVE_65 _JOBCOMMENTS {job}", FakeJob.Comments(description));
        _api.On($"NVE_65 _ARGUMENTS {job}", new FakeJob { Sets = [[]] });
        _api.On($"NVE_65 _RESULTS {job}", new FakeJob { Sets = [[]] });
    }

    [Theory]
    [InlineData(new string[0], Review.NeedsReview)]
    [InlineData(new[] { "NVE_65:STATUS_B" }, Review.NeedsReview)]
    [InlineData(new[] { "NVE_65:STATUS_B@7b69322a754f" }, Review.Reviewed)]
    [InlineData(new[] { "NVE_65:STATUS_B@0123456789ab" }, Review.NeedsReview)]
    [InlineData(new[] { "NVE_65:STATUS_B", "NVE_65:STATUS_B@0123456789ab" }, Review.NeedsReview)]
    public void DescribeJob_Verdict(string[] reviewed, string review)
    {
        ScriptDescribe("STATUS_B", "Startet B");
        var r = Service(reviewed).DescribeJob("NVE_65", "STATUS_B", _audit, default).Value!;
        Assert.Equal(new JobVerdict(true, "allow.pattern", review), r.Verdict);
    }

    [Fact]
    public void DescribeJob_Pinned_UnreadableDescription_IsNeedsReview()
    {
        _api.On("NVE_65 _JOBS", FakeJob.Jobs("STATUS_B"));
        _api.On("NVE_65 _JOBCOMMENTS STATUS_B", FakeJob.Error(100, "SYS-0010"));
        _api.On("NVE_65 _ARGUMENTS STATUS_B", new FakeJob { Sets = [[]] });
        _api.On("NVE_65 _RESULTS STATUS_B", new FakeJob { Sets = [[]] });
        var r = Service($"NVE_65:STATUS_B@{StartetBHash}").DescribeJob("NVE_65", "STATUS_B", _audit, default).Value!;
        Assert.Equal(Review.NeedsReview, r.Verdict.Review);
    }

    [Fact]
    public void DescribeList_PinnedPerJob()
    {
        _api.On("NVE_65 _JOBS", FakeJob.Jobs("STATUS_A", "STATUS_B", "STATUS_C"));
        _api.On("NVE_65 _JOBCOMMENTS STATUS_A", FakeJob.Comments("Status lesen"));
        _api.On("NVE_65 _JOBCOMMENTS STATUS_B", FakeJob.Comments("Startet B"));
        _api.On("NVE_65 _JOBCOMMENTS STATUS_C", FakeJob.Comments("Startet B"));
        var svc = Service($"NVE_65:STATUS_A@{StartetBHash}", $"NVE_65:STATUS_B@{StartetBHash}", "NVE_65:STATUS_C");

        var withComments = svc.DescribeList("NVE_65", null, true, _audit, default).Value!.Jobs.ToDictionary(j => j.Name, j => j.Verdict.Review);
        Assert.Equal(Review.NeedsReview, withComments["STATUS_A"]);
        Assert.Equal(Review.Reviewed, withComments["STATUS_B"]);
        Assert.Equal(Review.NeedsReview, withComments["STATUS_C"]);

        var without = svc.DescribeList("NVE_65", null, false, new RequestAudit(), default).Value!.Jobs.ToDictionary(j => j.Name, j => j.Verdict.Review);
        Assert.Equal(Review.NotChecked, without["STATUS_A"]);
        Assert.Equal(Review.NotChecked, without["STATUS_B"]);
        Assert.Equal(Review.NotChecked, without["STATUS_C"]);
    }

    [Fact]
    public void DescribeAndRun_Agree()
    {
        ScriptDescribe("STATUS_X", "Status lesen");
        _api.On("NVE_65 STATUS_X", FakeJob.Ok());
        foreach (var reviewed in new[] { $"NVE_65:STATUS_X@{StatusLesenHash}", "NVE_65:STATUS_X@0123456789ab" })
        {
            var svc = Service(reviewed);
            var described = svc.DescribeJob("NVE_65", "STATUS_X", new RequestAudit(), default).Value!.Verdict.Review;
            var ran = svc.RunJob(new JobRequest("NVE_65", "STATUS_X"), null, new RequestAudit(), default).Ok;
            Assert.Equal(ran, described == Review.Reviewed);
        }
    }

    // ---- describe shows what to paste ----

    [Fact]
    public void DescribeJob_CarriesTheDescriptionHash()
    {
        ScriptDescribe("STATUS_B", "Startet B");
        var r = Service().DescribeJob("NVE_65", "STATUS_B", _audit, default).Value!;
        Assert.Equal(StartetBHash, r.DescriptionHash);
        Assert.Equal(CommentScreen.DescriptionHash(r.Comment!), r.DescriptionHash);
    }

    [Fact]
    public void DescribeJob_UnpinnedEntry_ShowsNeedsReviewAndHash()
    {
        // The owner's old unpinned entry: describe still prints the hash to pin it with.
        ScriptDescribe("STATUS_B", "Startet B");
        var r = Service("NVE_65:STATUS_B").DescribeJob("NVE_65", "STATUS_B", _audit, default).Value!;
        Assert.Equal(Review.NeedsReview, r.Verdict.Review);
        Assert.Equal(StartetBHash, r.DescriptionHash);
    }

    [Fact]
    public void DescribeJob_NoDescription_NoHash()
    {
        ScriptDescribe("STATUS_B", " ");
        Assert.Null(Service().DescribeJob("NVE_65", "STATUS_B", _audit, default).Value!.DescriptionHash);
    }

    [Fact]
    public void DescribeJob_UnreadableDescription_NoHash()
    {
        _api.On("NVE_65 _JOBS", FakeJob.Jobs("STATUS_B"));
        _api.On("NVE_65 _JOBCOMMENTS STATUS_B", FakeJob.Error(100, "SYS-0010"));
        _api.On("NVE_65 _ARGUMENTS STATUS_B", new FakeJob { Sets = [[]] });
        _api.On("NVE_65 _RESULTS STATUS_B", new FakeJob { Sets = [[]] });
        Assert.Null(Service().DescribeJob("NVE_65", "STATUS_B", _audit, default).Value!.DescriptionHash);
    }

    [Fact]
    public void DescribeJob_MultiLineDescription_HashIsOfTheJoinedText()
    {
        ScriptDescribe("STATUS_B", "Zeile 1");
        _api.On("NVE_65 _JOBCOMMENTS STATUS_B", FakeJob.Comments("Status lesen", "Zeile 2"));
        var r = Service().DescribeJob("NVE_65", "STATUS_B", _audit, default).Value!;
        Assert.Equal(CommentScreen.DescriptionHash("Status lesen\nZeile 2"), r.DescriptionHash);
    }
}
