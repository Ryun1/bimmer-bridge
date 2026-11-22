using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

public class EdiabasServiceTests
{
    private readonly FakeEdiabasApi _api = new();
    private readonly FakeClock _clock = new();
    private readonly FakeConflicts _conflicts = new();
    private readonly FakeLog _log = new();
    private readonly CableUse _cable = new();
    private readonly RequestAudit _audit = new();
    private readonly DllLoadState _dll = new();

    private const int IdleSeconds = 20;

    private EdiabasService Service(IEnumerable<string>? reviewed = null, int defaultTimeout = 30, int maxTimeout = 60, params string[] ecus) =>
        new(
            _api,
            _conflicts,
            new FakeEcus(ecus.Length == 0 ? ["NVE_65", "TMODE", "UTILITY", "D_MOTOR", "N62_TUE"] : ecus),
            JobPolicy.Embedded,
            new CommentScreen(JobPolicy.Embedded.ScreenTerms, JobPolicy.Embedded.ScreenIgnore, reviewed ?? []),
            _clock,
            _cable,
            _log,
            _dll,
            defaultTimeout,
            maxTimeout,
            IdleSeconds);

    private static List<FakeResult> Set(params FakeResult[] r) => [.. r];

    private static FakeResult Text(string name, string value) => new(name, ApiResultFormat.Text, value);

    // ---- single job ----

    [Fact]
    public void Job_HappyPath_CallOrderAndShape()
    {
        _api.On("NVE_65 FS_LESEN", FakeJob.Ok(Set(new FakeResult("F_ORT_NR", ApiResultFormat.Long, 42834), Text("F_ORT_TEXT", "x"), Text("JOB_STATUS", "OKAY"))));

        var r = Service().RunJob(new JobRequest("nve_65", "fs_lesen"), null, _audit, default);

        Assert.True(r.Ok);
        var v = r.Value!;
        Assert.Equal("NVE_65", v.Ecu);
        Assert.Equal("FS_LESEN", v.Job);
        Assert.Equal("VAR", v.Variant);
        Assert.Equal("OKAY", v.JobStatus);
        Assert.Equal(42834, v.Sets[0]["F_ORT_NR"]);
        Assert.Equal(["TryLoad", "Init", "Job NVE_65 FS_LESEN [] []", "State", "End"], _api.Calls.Where(c => !c.StartsWith("Error", StringComparison.Ordinal)));
        Assert.Equal(_clock.Start, _cable.LastInitAt);
        Assert.Equal("NVE_65", _audit.Ecu);
        Assert.Null(_audit.Rule);
    }

    [Fact]
    public void Job_ArgsAndResultsArePassedThrough()
    {
        _api.On("NVE_65 FS_LESEN_DETAIL", FakeJob.Ok());
        Service().RunJob(new JobRequest("NVE_65", "FS_LESEN_DETAIL", "0x4711", "F_ORT_NR"), null, _audit, default);
        Assert.Contains("Job NVE_65 FS_LESEN_DETAIL [0x4711] [F_ORT_NR]", _api.Calls);
    }

    [Fact]
    public void Job_PollsEvery50msUntilReady()
    {
        _api.On("NVE_65 IDENT", new FakeJob { BusyPolls = 3, Sets = [[]] });
        Assert.True(Service().RunJob(new JobRequest("NVE_65", "IDENT"), null, _audit, default).Ok);
        Assert.Equal(4, _api.Count("State"));
        Assert.Equal(3, _clock.Sleeps);
        Assert.Equal(150, _clock.ElapsedMs);
    }

    [Fact]
    public void ConflictCheckFails_NothingTouchesTheApi()
    {
        _conflicts.Answers[0] = [new ConflictingProcess("INPALOAD", 4312)];
        var r = Service().RunJob(new JobRequest("NVE_65", "IDENT"), null, _audit, default);

        Assert.Equal(409, r.Failure!.Status);
        Assert.Equal("cable_in_use", r.Failure.Error.Code);
        var d = Assert.IsType<ProcessesDetails>(r.Failure.Error.Details);
        Assert.Equal("INPALOAD", d.Processes[0].Name);
        Assert.Empty(_api.Calls);
        Assert.Null(_cable.LastInitAt);
    }

    [Theory]
    [InlineData("NVE_65", "FS_LOESCHEN", "deny.substring:LOESCH")]
    [InlineData("TMODE", "SENDE_TELEGRAMM", "deny.substring:TELEGR")]
    [InlineData("NVE_65", "FS_LESEN;FS_LOESCHEN", "shape.job")]
    [InlineData("UTILITY", "ABS_RELAIS", "ecu_restricted")]
    [InlineData("NVE_65", "C_C_LESEN", "default_deny")]
    public void RefusedJob_NeverReachesTheApi(string ecu, string job, string rule)
    {
        var r = Service().RunJob(new JobRequest(ecu, job), null, _audit, default);

        Assert.Equal(403, r.Failure!.Status);
        Assert.Equal("job_not_allowed", r.Failure.Error.Code);
        Assert.Equal(rule, Assert.IsType<JobRefusalDetails>(r.Failure.Error.Details).Rule);
        Assert.Empty(_api.Calls);
        Assert.Equal(rule, _audit.Rule);
    }

    [Fact]
    public void BrokenPolicy_RefusesAndNeverTouchesTheApi()
    {
        var broken = JobPolicy.Parse("{}");
        var svc = new EdiabasService(_api, _conflicts, new FakeEcus("NVE_65"), broken, new CommentScreen(broken.ScreenTerms, broken.ScreenIgnore, []), _clock, _cable, _log, _dll, 30, 60, IdleSeconds);

        var r = svc.RunJob(new JobRequest("NVE_65", "IDENT"), null, _audit, default);
        Assert.Equal("job_not_allowed", r.Failure!.Error.Code);
        Assert.Equal(JobPolicy.RuleUnavailable, ((JobRefusalDetails)r.Failure.Error.Details!).Rule);

        var d = svc.DescribeList("NVE_65", null, true, new RequestAudit(), default);
        Assert.Equal("job_not_allowed", d.Failure!.Error.Code);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public void ArgsShape_RefusedBeforeDenylist()
    {
        var r = Service().RunJob(new JobRequest("NVE_65", "IDENT", "a\tb"), null, _audit, default);
        Assert.Equal("shape.args", ((JobRefusalDetails)r.Failure!.Error.Details!).Rule);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public void UnknownEcu_IsEcuNotFound_WithoutInit()
    {
        var r = Service().RunJob(new JobRequest("XYZ_99", "IDENT"), null, _audit, default);
        Assert.Equal(404, r.Failure!.Status);
        Assert.Equal("ecu_not_found", r.Failure.Error.Code);
        Assert.Equal("XYZ_99", ((EcuDetails)r.Failure.Error.Details!).Ecu);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public void DllMissing_IsUnavailable_NoInitNoEnd()
    {
        _api.LoadOk = false;
        var r = Service().RunJob(new JobRequest("NVE_65", "IDENT"), null, _audit, default);
        Assert.Equal(503, r.Failure!.Status);
        Assert.Equal("ediabas_unavailable", r.Failure.Error.Code);
        Assert.Equal(["TryLoad"], _api.Calls);
    }

    [Fact]
    public void InitFails_IsUnavailable_AndEndIsStillCalled()
    {
        _api.InitOk = false;
        var r = Service().RunJob(new JobRequest("NVE_65", "IDENT"), null, _audit, default);
        Assert.Equal("ediabas_unavailable", r.Failure!.Error.Code);
        Assert.Contains("95", ((ReasonDetails)r.Failure.Error.Details!).Reason);
        Assert.Equal(0, _api.Count("Job"));
        Assert.Equal(1, _api.Count("End"));
        Assert.NotNull(_cable.LastInitAt);
    }

    [Fact]
    public void EdiabasError_ReturnsCodeAndText_AndLogsThem()
    {
        _api.On("NVE_65 IDENT", FakeJob.Error(19, "IFH-0009: NO RESPONSE FROM CONTROLUNIT"));
        _api.On("NVE_65 _JOBS", FakeJob.Jobs("IDENT"));
        var r = Service().RunJob(new JobRequest("NVE_65", "IDENT"), null, _audit, default);

        Assert.Equal(502, r.Failure!.Status);
        var d = Assert.IsType<EdiabasErrorDetails>(r.Failure.Error.Details);
        Assert.Equal(19, d.ErrorCode);
        Assert.Equal("IFH-0009: NO RESPONSE FROM CONTROLUNIT", d.ErrorText);
        Assert.Contains(_log.Lines, l => l.Contains("19", StringComparison.Ordinal) && l.Contains("IFH-0009", StringComparison.Ordinal));
        Assert.Equal(1, _api.Count("End"));
    }

    [Fact]
    public void Timeout_CallsBreak_ThenEnd()
    {
        _api.On("NVE_65 IDENT", new FakeJob { NeverFinishes = true });
        var r = Service().RunJob(new JobRequest("NVE_65", "IDENT"), 2, _audit, default);

        Assert.Equal(504, r.Failure!.Status);
        Assert.Equal("job_timeout", r.Failure.Error.Code);
        Assert.Equal(2, ((TimeoutDetails)r.Failure.Error.Details!).TimeoutSeconds);
        Assert.Equal(1, _api.Count("Break"));
        Assert.Equal("End", _api.Calls[^1]);
        Assert.Equal(1, _api.Count("End"));
        Assert.InRange(_clock.ElapsedMs, 2000, 2100);
    }

    [Fact]
    public void Timeout_IsClampedToMaximum()
    {
        _api.On("NVE_65 IDENT", new FakeJob { NeverFinishes = true });
        var r = Service(maxTimeout: 5).RunJob(new JobRequest("NVE_65", "IDENT"), 3600, _audit, default);
        Assert.Equal(5, ((TimeoutDetails)r.Failure!.Error.Details!).TimeoutSeconds);
    }

    [Fact]
    public void Timeout_DefaultsToConfig()
    {
        _api.On("NVE_65 IDENT", new FakeJob { NeverFinishes = true });
        var r = Service(defaultTimeout: 7).RunJob(new JobRequest("NVE_65", "IDENT"), null, _audit, default);
        Assert.Equal(7, ((TimeoutDetails)r.Failure!.Error.Details!).TimeoutSeconds);
    }

    [Fact]
    public void ThrowingJob_StillEndsOnce()
    {
        _api.On("NVE_65 IDENT", new FakeJob { Throws = true });
        Assert.Throws<InvalidOperationException>(() => Service().RunJob(new JobRequest("NVE_65", "IDENT"), null, _audit, default));
        Assert.Equal(1, _api.Count("End"));
    }

    [Fact]
    public void CancelledBeforeStart_NothingTouchesTheApi()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = Service().RunJob(new JobRequest("NVE_65", "IDENT"), null, _audit, cts.Token);
        Assert.Equal("not_run", r.Failure!.Error.Code);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public void ApiBreakIgnored_StopsFurtherJobs_NoApiEnd()
    {
        _api.On("NVE_65 IDENT", new FakeJob { NeverFinishes = true, IgnoresBreak = true });
        _api.On("NVE_65 FS_LESEN", FakeJob.Ok());
        var r = Service().RunJobs([new("NVE_65", "IDENT"), new("NVE_65", "FS_LESEN")], 1, _audit, default);

        Assert.Equal("job_timeout", r.Value!.Items[0].Error!.Code);
        Assert.Equal("not_run", r.Value.Items[1].Error!.Code);
        Assert.Single(_api.JobCalls);
        Assert.Equal(0, _api.Count("End")); // the job never stopped: no unbounded apiEnd on it
    }

    // ---- description screen ----

    [Fact]
    public void Screen_PassingDescription_RunsJob()
    {
        _api.On("NVE_65 _JOBCOMMENTS STATUS_BORDNETZSPANNUNG", FakeJob.Comments("Bordnetzspannung auslesen"));
        _api.On("NVE_65 STATUS_BORDNETZSPANNUNG", FakeJob.Ok(Set(new FakeResult("STAT_SPANNUNG_WERT", ApiResultFormat.Real, 12.6))));

        var r = Service().RunJob(new JobRequest("NVE_65", "STATUS_BORDNETZSPANNUNG"), null, _audit, default);

        Assert.True(r.Ok);
        Assert.Equal(12.6, r.Value!.Sets[0]["STAT_SPANNUNG_WERT"]);
        Assert.Equal(
            ["Job NVE_65 _JOBCOMMENTS [STATUS_BORDNETZSPANNUNG] []", "Job NVE_65 STATUS_BORDNETZSPANNUNG [] []"],
            _api.JobCalls);
        Assert.Equal(1, _api.Count("Init"));
        Assert.Equal(1, _api.Count("End"));
    }

    [Theory]
    [InlineData("Startet die Grundadaption", "start")]
    [InlineData("Fehlerspeicher LÖSCHEN", "lösch")]
    [InlineData("Activate the pump", "activate")]
    [InlineData("Status Lernen", "lernen")]
    public void Screen_FailingDescription_IsNeedsReview_JobNeverRuns(string description, string term)
    {
        _api.On("NVE_65 _JOBCOMMENTS STATUS_SYSTEMCHECK_GRUNDADAPT", FakeJob.Comments(description));

        var r = Service().RunJob(new JobRequest("NVE_65", "STATUS_SYSTEMCHECK_GRUNDADAPT"), null, _audit, default);

        Assert.Equal(403, r.Failure!.Status);
        Assert.Equal("job_needs_review", r.Failure.Error.Code);
        var d = Assert.IsType<JobReviewDetails>(r.Failure.Error.Details);
        Assert.Equal(description, d.Description);
        Assert.Contains(term, d.Matched);
        Assert.Single(_api.JobCalls);
        Assert.Equal(1, _api.Count("End"));
        Assert.Equal(RequestAudit.ScreenRule, _audit.Rule);
    }

    [Fact]
    public void Screen_NoDescription_IsNeedsReview()
    {
        _api.On("NVE_65 _JOBCOMMENTS STATUS_X", FakeJob.Comments());
        var r = Service().RunJob(new JobRequest("NVE_65", "STATUS_X"), null, _audit, default);

        var d = Assert.IsType<JobReviewDetails>(r.Failure!.Error.Details);
        Assert.Null(d.Description);
        Assert.Empty(d.Matched);
        Assert.Single(_api.JobCalls);
    }

    [Fact]
    public void Screen_SeesEveryResultOfTheCommentSets()
    {
        _api.On("NVE_65 _JOBCOMMENTS STATUS_X", new FakeJob
        {
            Sets = [[], [Text("JOBCOMMENT0", "Status lesen"), Text("SOMETHING_ELSE", "schreibt Wert")]],
        });
        var r = Service().RunJob(new JobRequest("NVE_65", "STATUS_X"), null, _audit, default);
        Assert.Equal("job_needs_review", r.Failure!.Error.Code);
    }

    [Fact]
    public void Screen_ErrorReadingDescription_JobNeverRuns()
    {
        _api.On("NVE_65 _JOBCOMMENTS STATUS_X", FakeJob.Error(100, "SYS-0010"));
        var r = Service().RunJob(new JobRequest("NVE_65", "STATUS_X"), null, _audit, default);
        Assert.Equal("ediabas_error", r.Failure!.Error.Code);
        Assert.Single(_api.JobCalls);
    }

    [Fact]
    public void ReviewedJobs_SkipOnlyTheScreen()
    {
        // SHA-256("Startet B") = 7b69322a754f...: a description the screen would refuse.
        _api.On("NVE_65 _JOBCOMMENTS STATUS_SYSTEMCHECK_GRUNDADAPT", FakeJob.Comments("Startet B"));
        _api.On("NVE_65 STATUS_SYSTEMCHECK_GRUNDADAPT", FakeJob.Ok());
        var svc = Service(reviewed: ["NVE_65:STATUS_SYSTEMCHECK_GRUNDADAPT@7b69322a754f", "NVE_65:FS_LOESCHEN@7b69322a754f", "NVE_65:C_C_LESEN@7b69322a754f"]);

        Assert.True(svc.RunJob(new JobRequest("nve_65", "status_systemcheck_grundadapt"), null, _audit, default).Ok);
        Assert.Equal(["Job NVE_65 _JOBCOMMENTS [STATUS_SYSTEMCHECK_GRUNDADAPT] []", "Job NVE_65 STATUS_SYSTEMCHECK_GRUNDADAPT [] []"], _api.JobCalls);

        // Listing a denied job does not get it past the name rules.
        Assert.Equal("deny.substring:LOESCH", ((JobRefusalDetails)svc.RunJob(new JobRequest("NVE_65", "FS_LOESCHEN"), null, new RequestAudit(), default).Failure!.Error.Details!).Rule);
        Assert.Equal("default_deny", ((JobRefusalDetails)svc.RunJob(new JobRequest("NVE_65", "C_C_LESEN"), null, new RequestAudit(), default).Failure!.Error.Details!).Rule);
        Assert.Equal(2, _api.JobCalls.Count());
    }

    [Fact]
    public void ReviewedJobs_AreExactUpperCase()
    {
        // Pinned to the right hash, but the names are not exact.
        _api.On("NVE_65 _JOBCOMMENTS STATUS_X", FakeJob.Comments("starte etwas"));
        var hash = CommentScreen.DescriptionHash("starte etwas");
        var r = Service(reviewed: [$"nve_65:status_x@{hash}", $"NVE_65:STATUS_X @{hash}", $"NVE_65:STATUS_x@{hash}"]).RunJob(new JobRequest("NVE_65", "STATUS_X"), null, _audit, default);
        Assert.Equal("job_needs_review", r.Failure!.Error.Code);
    }

    [Fact]
    public void ExactNameOnRestrictedEcu_IsNotScreened()
    {
        _api.On("UTILITY STATUS_ZUENDUNG", FakeJob.Ok());
        Assert.True(Service().RunJob(new JobRequest("UTILITY", "STATUS_ZUENDUNG"), null, _audit, default).Ok);
        Assert.Equal(["Job UTILITY STATUS_ZUENDUNG [] []"], _api.JobCalls);
    }

    [Fact]
    public void JobCommentsAsARequest_ArgsAreData_AndCannotChainAJob()
    {
        _api.On("NVE_65 _JOBCOMMENTS", FakeJob.Comments("Fehlerspeicher loeschen"));
        var r = Service().RunJob(new JobRequest("NVE_65", "_JOBCOMMENTS", "FS_LOESCHEN"), null, _audit, default);

        Assert.True(r.Ok);
        Assert.Equal(["Job NVE_65 _JOBCOMMENTS [FS_LOESCHEN] []"], _api.JobCalls);
    }

    [Fact]
    public void ScreenCall_UsesTheValidatedJobName_NotClientArgs()
    {
        // Whatever the client sends as args, the agent's _JOBCOMMENTS call carries the job name.
        _api.On("NVE_65 _JOBCOMMENTS STATUS_X", FakeJob.Comments("Status lesen"));
        _api.On("NVE_65 _ARGUMENTS STATUS_X", new FakeJob { Sets = [[], [Text("ARG", "NAME"), Text("ARGCOMMENT0", "Name des Wertes")]] });
        _api.On("NVE_65 STATUS_X", FakeJob.Ok());
        Service().RunJob(new JobRequest("nve_65", "status_x", "FS_LOESCHEN", "X"), null, _audit, default);
        Assert.Equal("Job NVE_65 _JOBCOMMENTS [STATUS_X] []", _api.JobCalls.First());
        Assert.Equal("Job NVE_65 _ARGUMENTS [STATUS_X] []", _api.JobCalls.ElementAt(1));
    }

    // ---- batch ----

    [Fact]
    public void Batch_PerItemVerdicts_InOrder_OneInitOneEnd()
    {
        _api.On("NVE_65 FS_LESEN", FakeJob.Ok(Set(Text("JOB_STATUS", "OKAY"))));
        _api.On("NVE_65 IDENT", FakeJob.Error(19, "IFH-0009"));
        _api.On("NVE_65 _JOBS", FakeJob.Jobs("FS_LESEN", "IDENT"));
        _api.On("D_MOTOR IDENT", FakeJob.Ok());

        var r = Service().RunJobs(
            [
                new("NVE_65", "FS_LESEN"),
                new("NVE_65", "FS_LOESCHEN"),
                new("NVE_65", "IDENT"),
                new("NOPE", "IDENT"),
                new("D_MOTOR", "IDENT"),
            ],
            null,
            _audit,
            default);

        var items = r.Value!.Items;
        Assert.True(items[0].Ok);
        Assert.Null(items[0].Error);
        Assert.Equal("job_not_allowed", items[1].Error!.Code);
        Assert.Null(items[1].Result);
        Assert.Equal("ediabas_error", items[2].Error!.Code);
        Assert.Equal("ecu_not_found", items[3].Error!.Code);
        Assert.True(items[4].Ok);
        Assert.Equal(1, _api.Count("Init"));
        Assert.Equal(1, _api.Count("End"));
        Assert.Equal(4, _api.JobCalls.Count()); // the third item's error also lists _JOBS

        Assert.Equal(5, _audit.Items.Count);
        Assert.Equal("deny.substring:LOESCH", _audit.Items[1].Rule);
        Assert.Equal("job_not_allowed", _audit.Items[1].ErrorCode);
        Assert.Null(_audit.Items[0].Rule);
    }

    [Fact]
    public void Batch_AllRefused_NeverTouchesTheApi()
    {
        var r = Service().RunJobs([new("NVE_65", "FS_LOESCHEN"), new("TMODE", "IDENT"), new("NVE_65", "x;y")], null, _audit, default);
        Assert.All(r.Value!.Items, i => Assert.Equal("job_not_allowed", i.Error!.Code));
        Assert.Empty(_api.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void Batch_Size(int n)
    {
        var r = Service().RunJobs(Enumerable.Repeat(new JobRequest("NVE_65", "IDENT"), n).ToList(), null, _audit, default);
        Assert.Equal("bad_request", r.Failure!.Error.Code);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public void Batch_OverallBudget_LeftoversAreNotRun()
    {
        _api.On("NVE_65 IDENT", new FakeJob { NeverFinishes = true });
        _api.On("NVE_65 FS_LESEN", FakeJob.Ok());

        var r = Service(maxTimeout: 10).RunJobs(
            [new("NVE_65", "IDENT"), new("NVE_65", "IDENT"), new("NVE_65", "FS_LESEN")],
            6,
            _audit,
            default);

        var items = r.Value!.Items;
        Assert.Equal("job_timeout", items[0].Error!.Code);
        Assert.Equal("job_timeout", items[1].Error!.Code);
        Assert.Equal("not_run", items[2].Error!.Code);
        Assert.Equal(2, _api.Count("Break"));
        Assert.Equal(1, _api.Count("End"));
    }

    // ---- the request budget binds every job ----

    [Fact]
    public void Batch_HangingSecondItem_EndsAtTheBudget()
    {
        _api.On("NVE_65 IDENT", new FakeJob { BusyPolls = 80 }); // 4 s
        _api.On("NVE_65 FS_LESEN", new FakeJob { NeverFinishes = true });
        _api.On("NVE_65 C_FG_LESEN", FakeJob.Ok());

        var r = Service(maxTimeout: 10).RunJobs(
            [new("NVE_65", "IDENT"), new("NVE_65", "FS_LESEN"), new("NVE_65", "C_FG_LESEN")],
            10,
            _audit,
            default);

        var items = r.Value!.Items;
        Assert.True(items[0].Ok);
        Assert.Equal("job_timeout", items[1].Error!.Code);
        Assert.Equal(10, ((TimeoutDetails)items[1].Error!.Details!).TimeoutSeconds);
        Assert.Equal("not_run", items[2].Error!.Code);

        // Not 4 s + 10 s: the second job was stopped when the 10 s budget ran out.
        Assert.Equal(10_000, _clock.ElapsedMs);
        Assert.Equal(1, _api.Count("Break"));
        Assert.Equal(1, _api.Count("End"));
    }

    [Fact]
    public void SingleJob_IsBoundByTheBudgetToo()
    {
        _api.On("NVE_65 IDENT", new FakeJob { NeverFinishes = true });
        var r = Service(defaultTimeout: 5, maxTimeout: 5).RunJob(new JobRequest("NVE_65", "IDENT"), null, _audit, default);
        Assert.Equal("job_timeout", r.Failure!.Error.Code);
        Assert.Equal(5_000, _clock.ElapsedMs);
    }

    [Fact]
    public void DescriptionScreen_IsBoundByTheBudget_AndTheJobNeverStarts()
    {
        _api.On("NVE_65 _JOBCOMMENTS STATUS_X", new FakeJob { NeverFinishes = true });
        var r = Service(maxTimeout: 8).RunJob(new JobRequest("NVE_65", "STATUS_X"), 8, _audit, default);

        Assert.Equal("job_timeout", r.Failure!.Error.Code);
        Assert.Equal(8_000, _clock.ElapsedMs);
        Assert.Equal(["Job NVE_65 _JOBCOMMENTS [STATUS_X] []"], _api.JobCalls);
        Assert.Equal(1, _api.Count("End"));
    }

    [Fact]
    public void DescriptionScreen_UsesUpTheBudget_ClientJobIsNotStarted()
    {
        // The screen finishes exactly at the budget: the job itself must not start after it.
        _api.On("NVE_65 _JOBCOMMENTS STATUS_X", new FakeJob { BusyPolls = 100, Sets = [[], [Text("JOBCOMMENT0", "Status lesen")]] });
        _api.On("NVE_65 STATUS_X", FakeJob.Ok());
        var r = Service(maxTimeout: 5).RunJob(new JobRequest("NVE_65", "STATUS_X"), 5, _audit, default);

        Assert.Equal("not_run", r.Failure!.Error.Code);
        Assert.Single(_api.JobCalls);
        Assert.Equal(1, _api.Count("End"));
    }

    [Fact]
    public void Vehicle_IsBoundByTheBudget()
    {
        _api.On("UTILITY STATUS_ZUENDUNG", new FakeJob { BusyPolls = 60 }); // 3 s
        _api.On("UTILITY STATUS_UBATT", new FakeJob { NeverFinishes = true });

        var r = Service(defaultTimeout: 5, maxTimeout: 5).Vehicle([], _audit, default).Value!;

        Assert.True(r.Ignition.Ok);
        Assert.Equal("job_timeout", r.Ubatt.Error!.Code);
        Assert.Equal("not_run", r.CableKl30.Error!.Code);
        Assert.Equal("not_run", r.CableKl15.Error!.Code);
        Assert.Equal(5_000, _clock.ElapsedMs);
    }

    [Fact]
    public void DescribeList_HangingCommentFetch_EndsAtTheBudget_RestNotChecked()
    {
        _api.On("NVE_65 _JOBS", FakeJob.Jobs("STATUS_A", "STATUS_B", "FS_LESEN", "STATUS_C"));
        _api.On("NVE_65 _JOBCOMMENTS STATUS_A", new FakeJob { BusyPolls = 40, Sets = [[], [Text("JOBCOMMENT0", "Status A lesen")]] }); // 2 s
        _api.On("NVE_65 _JOBCOMMENTS STATUS_B", new FakeJob { NeverFinishes = true });

        var r = Service(defaultTimeout: 30, maxTimeout: 10).DescribeList("NVE_65", null, true, _audit, default).Value!;

        var byName = r.Jobs.ToDictionary(j => j.Name);
        Assert.Equal(Review.Passed, byName["STATUS_A"].Verdict.Review);
        Assert.Equal(Review.NeedsReview, byName["STATUS_B"].Verdict.Review); // its own fetch failed
        Assert.Equal(Review.NotRequired, byName["FS_LESEN"].Verdict.Review);
        Assert.Null(byName["FS_LESEN"].Comment);
        Assert.Equal(Review.NotChecked, byName["STATUS_C"].Verdict.Review); // skipped, not failed
        Assert.Equal(10_000, _clock.ElapsedMs);
        Assert.Equal(3, _api.JobCalls.Count());
        Assert.Equal(1, _api.Count("End"));
    }

    [Fact]
    public void DescribeList_AfterPoisonedSession_LaterJobsAreNotChecked()
    {
        _api.On("NVE_65 _JOBS", FakeJob.Jobs("STATUS_A", "STATUS_B"));
        _api.On("NVE_65 _JOBCOMMENTS STATUS_A", new FakeJob { NeverFinishes = true, IgnoresBreak = true });

        var r = Service(defaultTimeout: 1, maxTimeout: 60).DescribeList("NVE_65", null, true, _audit, default).Value!;

        Assert.Equal(Review.NeedsReview, r.Jobs[0].Verdict.Review);
        Assert.Equal(Review.NotChecked, r.Jobs[1].Verdict.Review);
        Assert.Equal(2, _api.JobCalls.Count());
        Assert.Equal(0, _api.Count("End"));
    }

    [Fact]
    public void DescribeJob_IsBoundByTheBudget()
    {
        _api.On("NVE_65 _JOBS", FakeJob.Jobs("IDENT"));
        _api.On("NVE_65 _JOBCOMMENTS IDENT", FakeJob.Comments("Ident"));
        _api.On("NVE_65 _ARGUMENTS IDENT", new FakeJob { NeverFinishes = true });

        var f = Service(defaultTimeout: 30, maxTimeout: 6).DescribeJob("NVE_65", "IDENT", _audit, default).Failure!;
        Assert.Equal("job_timeout", f.Error.Code);
        Assert.Equal(6_000, _clock.ElapsedMs);
        Assert.Equal(1, _api.Count("End"));
    }

    [Fact]
    public void Batch_ToolStartsMidway_RestAreNotRun()
    {
        // Each job takes one recheck interval, so the check before every later item is due; the
        // tool appears during the second job, which ends before the next scan sees it.
        _api.On("NVE_65 IDENT", FakeJob.Ok());
        _api.DuringJob = () => _clock.ElapsedMs += EdiabasService.ConflictRecheckMs;
        _conflicts.Answer = () => _clock.ElapsedMs >= 2 * EdiabasService.ConflictRecheckMs ? [new ConflictingProcess("WINKFPT", 9)] : [];

        var r = Service().RunJobs([new("NVE_65", "IDENT"), new("NVE_65", "IDENT"), new("NVE_65", "IDENT")], null, _audit, default);

        var items = r.Value!.Items;
        Assert.True(items[0].Ok);
        Assert.True(items[1].Ok);
        Assert.Equal("not_run", items[2].Error!.Code);
        Assert.Contains("WINKFPT", items[2].Error!.Message, StringComparison.Ordinal);
        Assert.Equal(1, _api.Count("End"));
    }

    [Fact]
    public void Batch_ThrowingItem_StillEndsOnce()
    {
        _api.On("NVE_65 IDENT", FakeJob.Ok());
        _api.On("NVE_65 FS_LESEN", new FakeJob { Throws = true });
        Assert.Throws<InvalidOperationException>(() =>
            Service().RunJobs([new("NVE_65", "IDENT"), new("NVE_65", "FS_LESEN")], null, _audit, default));
        Assert.Equal(1, _api.Count("End"));
    }

    [Fact]
    public void Batch_InitFailure_IsWholeRequest()
    {
        _api.InitOk = false;
        var r = Service().RunJobs([new("NVE_65", "FS_LOESCHEN"), new("NVE_65", "IDENT")], null, _audit, default);
        Assert.Equal("ediabas_unavailable", r.Failure!.Error.Code);
        Assert.Equal(1, _api.Count("End"));
    }

    // ---- vehicle ----

    [Fact]
    public void Vehicle_RunsTheFixedJobs_ThroughThePolicy()
    {
        _api.On("UTILITY STATUS_ZUENDUNG", FakeJob.Ok(Set(new FakeResult("STAT_ZUENDUNG", ApiResultFormat.Integer, (short)1))));
        _api.On("UTILITY STATUS_UBATT", FakeJob.Ok());
        _api.On("NVE_65 _JOBCOMMENTS STATUS_BORDNETZSPANNUNG", FakeJob.Comments("Bordnetzspannung"));
        _api.On("NVE_65 STATUS_BORDNETZSPANNUNG", FakeJob.Ok());
        _api.On("TMODE LESE_SPANNUNG_KL30", FakeJob.Ok());
        _api.On("TMODE LESE_SPANNUNG_KL15", FakeJob.Error(20, "IFH-0010"));
        _api.On("TMODE _JOBS", FakeJob.Jobs("LESE_SPANNUNG_KL30", "LESE_SPANNUNG_KL15"));

        var sources = new List<VoltageSource>
        {
            new() { Ecu = "NVE_65", Job = "STATUS_BORDNETZSPANNUNG" },
            new() { Ecu = "NVE_65", Job = "FS_LOESCHEN" },
        };
        var r = Service().Vehicle(sources, _audit, default).Value!;

        Assert.True(r.Ignition.Ok);
        Assert.Equal((short)1, r.Ignition.Result!.Sets[0]["STAT_ZUENDUNG"]);
        Assert.True(r.Ubatt.Ok);
        Assert.Equal(2, r.Voltages.Count);
        Assert.True(r.Voltages[0].Ok);
        Assert.Equal("NVE_65", r.Voltages[0].Ecu);
        Assert.Equal("job_not_allowed", r.Voltages[1].Error!.Code);
        Assert.True(r.CableKl30.Ok);
        Assert.Equal("ediabas_error", r.CableKl15.Error!.Code);
        Assert.Equal(1, _api.Count("Init"));
        Assert.Equal(1, _api.Count("End"));
        Assert.Equal(6, _audit.Items.Count);
    }

    [Fact]
    public void Vehicle_ConflictFails_NothingCalled()
    {
        _conflicts.Answers[0] = [new ConflictingProcess("NCSEXPER", 1)];
        Assert.Equal("cable_in_use", Service().Vehicle([], _audit, default).Failure!.Error.Code);
        Assert.Empty(_api.Calls);
    }

    // ---- describe ----

    private void ScriptNve65Jobs()
    {
        _api.On("NVE_65 _JOBS", FakeJob.Jobs("FS_LESEN", "STEUERN_X", "STATUS_A", "STATUS_B", "IDENT"));
        _api.On("NVE_65 _JOBCOMMENTS FS_LESEN", FakeJob.Comments("Fehlerspeicher lesen"));
        _api.On("NVE_65 _JOBCOMMENTS STEUERN_X", FakeJob.Comments("Ansteuern X"));
        _api.On("NVE_65 _JOBCOMMENTS STATUS_A", FakeJob.Comments("Status A lesen"));
        _api.On("NVE_65 _JOBCOMMENTS STATUS_B", FakeJob.Comments("Startet B"));
        _api.On("NVE_65 _JOBCOMMENTS IDENT", FakeJob.Comments());
    }

    [Fact]
    public void DescribeList_WithComments()
    {
        ScriptNve65Jobs();
        var r = Service().DescribeList("nve_65", null, true, _audit, default).Value!;

        Assert.Equal("NVE_65", r.Ecu);
        Assert.Equal("VAR", r.Variant);
        Assert.Equal(5, r.JobCount);
        var byName = r.Jobs.ToDictionary(j => j.Name);
        Assert.Equal(new JobVerdict(true, "allow.global", Review.NotRequired), byName["FS_LESEN"].Verdict);
        Assert.Equal("Fehlerspeicher lesen", byName["FS_LESEN"].Comment);
        Assert.Equal(new JobVerdict(false, "deny.substring:STEUER", Review.NotRequired), byName["STEUERN_X"].Verdict);
        Assert.Equal(Review.Passed, byName["STATUS_A"].Verdict.Review);
        Assert.Equal(Review.NeedsReview, byName["STATUS_B"].Verdict.Review);
        Assert.Null(byName["IDENT"].Comment);

        // Only info jobs were run, one cycle.
        Assert.All(_api.JobCalls, c => Assert.Matches(@"^Job NVE_65 _JOB(S|COMMENTS) ", c));
        Assert.Equal(1, _api.Count("End"));
    }

    [Fact]
    public void DescribeList_NoComments_FilterAndNotChecked()
    {
        // Without descriptions no approval can be checked, pinned or not.
        ScriptNve65Jobs();
        var r = Service(reviewed: ["NVE_65:STATUS_B", "NVE_65:STATUS_A@7b69322a754f"]).DescribeList("NVE_65", "status", false, _audit, default).Value!;

        Assert.Equal(["STATUS_A", "STATUS_B"], r.Jobs.Select(j => j.Name));
        Assert.Equal(Review.NotChecked, r.Jobs[0].Verdict.Review);
        Assert.Equal(Review.NotChecked, r.Jobs[1].Verdict.Review);
        Assert.All(r.Jobs, j => Assert.Null(j.Comment));
        Assert.Equal(["Job NVE_65 _JOBS [] []"], _api.JobCalls);
    }

    [Fact]
    public void DescribeList_UnknownEcu()
    {
        Assert.Equal("ecu_not_found", Service().DescribeList("NOPE", null, true, _audit, default).Failure!.Error.Code);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public void DescribeList_BadEcuShape()
    {
        var f = Service().DescribeList("../TMODE", null, true, _audit, default).Failure!;
        Assert.Equal("job_not_allowed", f.Error.Code);
        Assert.Equal("shape.ecu", ((JobRefusalDetails)f.Error.Details!).Rule);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public void DescribeList_CommentsStopAtBudget()
    {
        _api.On("NVE_65 _JOBS", FakeJob.Jobs("STATUS_A", "STATUS_B"));
        _api.On("NVE_65 _JOBCOMMENTS STATUS_A", new FakeJob { NeverFinishes = true });
        var r = Service(defaultTimeout: 5, maxTimeout: 5).DescribeList("NVE_65", null, true, _audit, default).Value!;

        Assert.Equal(Review.NeedsReview, r.Jobs[0].Verdict.Review);
        Assert.Equal(Review.NotChecked, r.Jobs[1].Verdict.Review);
        Assert.Equal(1, _api.Count("End"));
    }

    [Fact]
    public void DescribeJob_DeniedJobCanBeDescribed()
    {
        _api.On("NVE_65 _JOBS", FakeJob.Jobs("FS_LOESCHEN"));
        _api.On("NVE_65 _JOBCOMMENTS FS_LOESCHEN", FakeJob.Comments("Fehlerspeicher loeschen"));
        _api.On("NVE_65 _ARGUMENTS FS_LOESCHEN", FakeJob.Error(100, "no arguments"));
        _api.On("NVE_65 _RESULTS FS_LOESCHEN", new FakeJob
        {
            Sets =
            [
                [],
                [Text("RESULT", "JOB_STATUS"), Text("RESULTTYPE", "string"), Text("RESULTCOMMENT0", "OKAY wenn fehlerfrei"), Text("RESULTCOMMENT1", "sonst Fehler")],
            ],
        });

        var r = Service().DescribeJob("NVE_65", "fs_loeschen", _audit, default).Value!;

        Assert.Equal("FS_LOESCHEN", r.Job);
        Assert.Equal("Fehlerspeicher loeschen", r.Comment);
        Assert.Equal(new JobVerdict(false, "deny.substring:LOESCH", Review.NotRequired), r.Verdict);
        Assert.Empty(r.Arguments);
        Assert.Equal(new JobParameterInfo("JOB_STATUS", "string", "OKAY wenn fehlerfrei\nsonst Fehler"), r.Results[0]);
        Assert.DoesNotContain(_api.JobCalls, c => c.StartsWith("Job NVE_65 FS_LOESCHEN", StringComparison.Ordinal));
        Assert.Equal(1, _api.Count("End"));
    }

    [Fact]
    public void DescribeJob_ScreenedJob_ShowsReview()
    {
        _api.On("NVE_65 _JOBS", FakeJob.Jobs("STATUS_B"));
        _api.On("NVE_65 _JOBCOMMENTS STATUS_B", FakeJob.Comments("Startet B"));
        _api.On("NVE_65 _ARGUMENTS STATUS_B", new FakeJob { Sets = [[], [Text("ARG", "MODE"), Text("ARGTYPE", "int"), Text("ARGCOMMENT0", "Modus")]] });
        _api.On("NVE_65 _RESULTS STATUS_B", new FakeJob { Sets = [[]] });

        var r = Service().DescribeJob("NVE_65", "STATUS_B", _audit, default).Value!;
        Assert.Equal(new JobVerdict(true, "allow.pattern", Review.NeedsReview), r.Verdict);
        Assert.Equal(new JobParameterInfo("MODE", "int", "Modus"), r.Arguments.Single());
        Assert.Empty(r.Results);
    }

    [Fact]
    public void DescribeJob_UnknownJob()
    {
        _api.On("NVE_65 _JOBS", FakeJob.Jobs("IDENT"));
        var f = Service().DescribeJob("NVE_65", "FS_LESEN", _audit, default).Failure!;
        Assert.Equal(404, f.Status);
        Assert.Equal("job_not_found", f.Error.Code);
        Assert.Equal(1, _api.Count("End"));
    }

    [Theory]
    [InlineData("FS_LESEN;X")]
    [InlineData("FS LESEN")]
    [InlineData("")]
    public void DescribeJob_BadShape_NothingCalled(string job)
    {
        var f = Service().DescribeJob("NVE_65", job, _audit, default).Failure!;
        Assert.Equal("shape.job", ((JobRefusalDetails)f.Error.Details!).Rule);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public void DescribeJob_OnRestrictedEcu_InfoJobsAreAllowed()
    {
        _api.On("TMODE _JOBS", FakeJob.Jobs("SENDE_TELEGRAMM"));
        _api.On("TMODE _JOBCOMMENTS SENDE_TELEGRAMM", FakeJob.Comments("sendet"));
        _api.On("TMODE _ARGUMENTS SENDE_TELEGRAMM", new FakeJob { Sets = [[]] });
        _api.On("TMODE _RESULTS SENDE_TELEGRAMM", new FakeJob { Sets = [[]] });

        var r = Service().DescribeJob("TMODE", "SENDE_TELEGRAMM", _audit, default).Value!;
        Assert.False(r.Verdict.Allowed);
        Assert.DoesNotContain(_api.JobCalls, c => c.StartsWith("Job TMODE SENDE", StringComparison.Ordinal));
    }

    // ---- ecus ----

    [Fact]
    public void ListEcus_ChecksConflictsFirst()
    {
        _conflicts.Answers[0] = [new ConflictingProcess("TOOL32", 2)];
        Assert.Equal("cable_in_use", Service().ListEcus(null).Failure!.Error.Code);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public void ListEcus_NeverTouchesTheApi()
    {
        Assert.True(Service().ListEcus(null).Ok);
        Assert.Empty(_api.Calls);
    }
}
