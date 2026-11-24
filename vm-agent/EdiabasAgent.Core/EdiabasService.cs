namespace EdiabasAgent.Core;

/// <summary>One job as asked for by a client. Null means absent.</summary>
public sealed record JobRequest(string? Ecu, string? Job, string? Args = null, string? Results = null);

/// <summary>One line of the audit log for one item of a batch.</summary>
public sealed record ItemAudit(int Index, string? Ecu, string? Job, string? Rule, string? ErrorCode, long DurationMs);

/// <summary>What a handler writes to the audit log for a request: ECU, job and, for refusals,
/// the rule; plus one entry per batch item.</summary>
public sealed class RequestAudit
{
    /// <summary>Rule written for a <c>job_needs_review</c> refusal.</summary>
    public const string ScreenRule = "description_screen";

    public string? Ecu { get; set; }
    public string? Job { get; set; }
    public string? Rule { get; set; }

    /// <summary>The error code the request was answered with, else null.</summary>
    public string? ErrorCode { get; set; }

    public List<ItemAudit> Items { get; } = [];

    /// <summary>How many telegrams (<c>SENDECUTEL</c>) the telegram check judged for the
    /// request; null when it did not check (no EDIABAS use, or the check is off).</summary>
    public int? TelegramsChecked { get; set; }

    /// <summary>What the telegram check found for the request, under enforce or report.</summary>
    public List<TelegramFinding> TelegramFindings { get; } = [];
}

/// <summary>
/// Everything the EDIABAS endpoints do, against <see cref="IEdiabasApi"/>. The host calls these
/// methods on its one EDIABAS thread, one request at a time.
/// <para>Per request: re-read <c>reviewedJobs</c> → conflict check → (lazily, at the first job that will really run) load the DLL, conflict check again, apiInit
/// → per job: policy → Ecu file → description screen where required (and, for a call with
/// arguments, the argument screen) → cancel, budget and conflict check again
/// (before every apiJob, the screen's too; a check under <see cref="ConflictRecheckMs"/> old is
/// reused) → apiJob → poll apiState
/// every 50 ms until not busy or timeout (apiBreak on timeout, or when the conflict check, run
/// while the job is busy at the same rate, sees a BMW tool) → read every result set →
/// apiEnd in <c>finally</c>. Nothing touches the API when the conflict check fails or when no
/// job survives the policy.</para>
/// <para>Telegram check (<see cref="TelegramCheck"/>): a latched check, or <c>requireGate</c>
/// without a <c>GATE:</c> interface, refuses the request first. Just before the DLL is loaded
/// for apiInit, the check reads EDIABAS.INI and notes the length of IFH.trc (under enforce an
/// IfhTrace below 3 refuses the request there, before anything is sent); after apiEnd it reads
/// what the request added and judges every telegram. A batch is one request.</para>
/// <para>Every job the agent sends, including its own <c>_JOBS</c> / <c>_JOBCOMMENTS</c> /
/// <c>_ARGUMENTS</c> / <c>_RESULTS</c> calls, is checked by the policy first.</para>
/// <para>Idle release: the DLL is loaded at the first request that needs it and unloaded once no
/// such request has run for the configured idle time (<see cref="ReleaseIfIdle"/>), because
/// <c>apiEnd</c> alone leaves EDIABAS's <c>ebas32.exe</c> running while the DLL is attached. The
/// host's runner thread asks <see cref="MillisecondsUntilRelease"/> how long to wait for work.</para>
/// </summary>
public sealed class EdiabasService
{
    public const int PollIntervalMs = 50;
    public const int BreakGraceMs = 10000;
    public const int MaxBatchItems = 20;

    /// <summary>Within a request the conflict check runs again before every EDIABAS job and while
    /// it runs, but a check younger than this is reused: the process scan costs tens of
    /// milliseconds, and a describe of a large SGBD sends hundreds of short jobs.</summary>
    public const int ConflictRecheckMs = 250;

    private readonly IEdiabasApi _api;
    private readonly IConflictCheck _conflicts;
    private readonly IEcuCatalog _ecus;
    private readonly JobPolicy _policy;
    private readonly CommentScreen _screen;
    private readonly IRunnerClock _clock;
    private readonly CableUse _cableUse;
    private readonly IEdiabasLog _log;
    private readonly DllLoadState _dll;
    private readonly ApprovalReloader? _approvals;
    private readonly DescriptionCache _descriptions;
    private readonly int _defaultTimeoutSeconds;
    private readonly int _maxTimeoutSeconds;
    private readonly long _idleReleaseMs;

    // The check of what EDIABAS sent (null: no check, as with telegramCheck "off"), the interface
    // to open (null: apiInit), and whether only a GATE: interface may be used.
    private readonly TelegramCheck? _telegrams;
    private readonly string? _interface;
    private readonly bool _requireGate;

    // Idle-release state. Touched only on the runner thread.
    private bool _loaded;
    private long _lastUseMs;
    private int _openSessions;
    private bool _handleMayBeOpen;

    // A job was still busy after apiBreak and its grace time. EDIABAS may still be working on it,
    // so nothing touches the DLL again (no apiEnd, no unload, no new apiInit) until the agent
    // restarts. Touched only on the runner thread; mirrored to DllLoadState for /health.
    private bool _stuck;

    public EdiabasService(
        IEdiabasApi api,
        IConflictCheck conflicts,
        IEcuCatalog ecus,
        JobPolicy policy,
        CommentScreen screen,
        IRunnerClock clock,
        CableUse cableUse,
        IEdiabasLog log,
        DllLoadState dll,
        int defaultTimeoutSeconds,
        int maxTimeoutSeconds,
        int idleReleaseSeconds,
        ApprovalReloader? approvals = null,
        DescriptionCache? descriptions = null,
        TelegramCheck? telegrams = null,
        string? ediabasInterface = null,
        bool requireGate = false)
    {
        _telegrams = telegrams;
        _interface = ediabasInterface;
        _requireGate = requireGate;
        _api = api;
        _conflicts = conflicts;
        _ecus = ecus;
        _policy = policy;
        _screen = screen;
        _clock = clock;
        _cableUse = cableUse;
        _log = log;
        _dll = dll;
        _approvals = approvals;
        _descriptions = descriptions ?? new DescriptionCache();
        _maxTimeoutSeconds = Math.Max(1, maxTimeoutSeconds);
        _defaultTimeoutSeconds = Math.Clamp(defaultTimeoutSeconds, 1, _maxTimeoutSeconds);
        _idleReleaseMs = (long)Math.Clamp(idleReleaseSeconds, AgentConfig.MinIdleReleaseSeconds, AgentConfig.MaxIdleReleaseSeconds) * 1000;
    }

    // ---------------------------------------------------------------- idle release

    /// <summary>
    /// How long the runner may wait for the next request before calling <see cref="ReleaseIfIdle"/>:
    /// <see cref="Timeout.Infinite"/> (-1) when there is nothing to release.
    /// </summary>
    public int MillisecondsUntilRelease()
    {
        if (!CanRelease())
        {
            return Timeout.Infinite;
        }

        var left = _lastUseMs + _idleReleaseMs - _clock.ElapsedMs;
        return (int)Math.Clamp(left, 0, int.MaxValue);
    }

    /// <summary>
    /// Unloads api32.dll if it is loaded and no request has used it for the idle time. Never while
    /// a request is running, and never after an <c>apiEnd</c> that failed (a handle may still be
    /// open). A failed unload is logged; the next request loads the DLL again. True if it unloaded
    /// (or tried to).
    /// </summary>
    public bool ReleaseIfIdle()
    {
        if (!CanRelease())
        {
            return false;
        }

        var idleMs = _clock.ElapsedMs - _lastUseMs;
        if (idleMs < _idleReleaseMs)
        {
            return false;
        }

        // Forget the DLL first: whatever Unload does, the next request starts from a fresh load.
        _loaded = false;
        _dll.Loaded = false;
        try
        {
            _api.Unload();
            _log.Info($"api32.dll released after {idleMs / 1000} s without an EDIABAS request");
        }
        catch (Exception ex)
        {
            _log.Warn($"api32.dll release did not complete ({ex.GetType().Name}: {ex.Message}); the next request loads it again");
        }

        return true;
    }

    private bool CanRelease() => _loaded && _openSessions == 0 && !_handleMayBeOpen;

    /// <summary>The request's <c>timeoutSeconds</c>, defaulted and clamped to the maximum.</summary>
    public int EffectiveTimeout(int? requested) =>
        requested is null ? _defaultTimeoutSeconds : Math.Clamp(requested.Value, 1, _maxTimeoutSeconds);

    // ---------------------------------------------------------------- /ediabas/ecus

    public Outcome<EcusResponse> ListEcus(string? filter)
    {
        _approvals?.Refresh();

        // Only the latch: the listing does not use EDIABAS, but a latched agent answers nothing
        // on /ediabas/* until it is restarted.
        if (_telegrams?.LatchedRefusal() is { } latched)
        {
            return latched;
        }

        if (ConflictFailure() is { } busy)
        {
            return busy;
        }

        var listing = _ecus.List(filter, EcuFolder.MaxEntries);
        return listing is null
            ? ApiFailure.Of(ApiErrors.EdiabasUnavailable, "The Ecu folder cannot be read.", new ReasonDetails("Ecu folder cannot be read"))
            : listing;
    }

    // ---------------------------------------------------------------- /ediabas/job

    public Outcome<JobResult> RunJob(JobRequest request, int? timeoutSeconds, RequestAudit audit, CancellationToken ct, long queuedMs = 0)
    {
        _approvals?.Refresh();
        audit.Ecu = request.Ecu;
        audit.Job = request.Job;
        if (RequestRefusal() is { } refused)
        {
            return refused;
        }

        if (WaitedTooLong(queuedMs) is { } waited)
        {
            return waited;
        }

        if (ConflictFailure() is { } busy)
        {
            return busy;
        }

        return InSession(queuedMs, audit, session => RunJobIn(session, request, timeoutSeconds, audit, ct));
    }

    private Outcome<JobResult> RunJobIn(Session session, JobRequest request, int? timeoutSeconds, RequestAudit audit, CancellationToken ct)
    {
        var run = RunItem(session, request, EffectiveTimeout(timeoutSeconds), ct);
        audit.Ecu = run.Ecu;
        audit.Job = run.Job;
        audit.Rule = run.Rule;
        if (run.WholeRequest is { } whole)
        {
            return whole;
        }

        if (run.Item.Ok)
        {
            return run.Item.Result!;
        }

        return ToolStartedFailure(session) ?? new ApiFailure(ApiErrors.Status(run.Item.Error!.Code), run.Item.Error);
    }

    // ---------------------------------------------------------------- /ediabas/jobs

    public Outcome<BatchResponse> RunJobs(IReadOnlyList<JobRequest> items, int? timeoutSeconds, RequestAudit audit, CancellationToken ct, long queuedMs = 0)
    {
        _approvals?.Refresh();
        if (items.Count is < 1 or > MaxBatchItems)
        {
            return ApiFailure.Of(ApiErrors.BadRequest, $"items must hold 1 to {MaxBatchItems} jobs.", new ParameterDetails("items"));
        }

        if (RequestRefusal() is { } refused)
        {
            return refused;
        }

        if (WaitedTooLong(queuedMs) is { } waited)
        {
            return waited;
        }

        if (ConflictFailure() is { } busy)
        {
            return busy;
        }

        var start = _clock.ElapsedMs;
        return InSession<BatchResponse>(queuedMs, audit, session =>
        {
            var batch = RunBatch(session, items, EffectiveTimeout(timeoutSeconds), audit, ct);
            if (batch.WholeRequest is { } whole)
            {
                return whole;
            }

            return new BatchResponse(batch.Items, _clock.ElapsedMs - start);
        });
    }

    // ---------------------------------------------------------------- /ediabas/describe

    public Outcome<DescribeListResponse> DescribeList(string? ecu, string? filter, bool comments, RequestAudit audit, CancellationToken ct, long queuedMs = 0)
    {
        _approvals?.Refresh();
        audit.Ecu = ecu;
        if (RequestRefusal() is { } refused)
        {
            return refused;
        }

        if (WaitedTooLong(queuedMs) is { } waited)
        {
            return waited;
        }

        if (ConflictFailure() is { } busy)
        {
            return busy;
        }

        if (ct.IsCancellationRequested)
        {
            return NotRun("The request was abandoned before it started.");
        }

        return InSession(queuedMs, audit, session => DescribeListIn(session, ecu, filter, comments, audit, ct));
    }

    private Outcome<DescribeListResponse> DescribeListIn(Session session, string? ecu, string? filter, bool comments, RequestAudit audit, CancellationToken ct)
    {
        var start = _clock.ElapsedMs;
        var jobs = ReadJobList(session, ecu, audit, ct, out var variant, out var upperEcu);
        if (!jobs.Ok)
        {
            return ToolStartedFailure(session) ?? jobs.Failure!;
        }

        // A BMW tool that starts during the list stops the comment fetches; what was read is
        // kept and the rest stay not_checked, as when the time budget runs out. Descriptions
        // already read from the same file come from the cache, so asking again after a cut-off
        // goes further each time.
        var timeout = _defaultTimeoutSeconds;
        var fileHash = comments ? DescribedFileHash(variant, upperEcu!) : null;
        var entries = new List<DescribeJobEntry>();
        var skipped = false;
        var budgetHit = false;
        foreach (var name in jobs.Value!)
        {
            if (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var verdict = _policy.Evaluate(upperEcu, name);
            string? comment = null;
            var fetch = DescriptionFetch.Skipped;
            if (comments && fileHash is not null && _descriptions.TryGet(fileHash, name, out var cached))
            {
                comment = NullIfBlank(new TextList(cached).Text);
                fetch = DescriptionFetch.Read;
            }
            else if (comments && !session.Poisoned && session.ToolStarted is null && !ct.IsCancellationRequested && session.BudgetLeftMs > 0)
            {
                var d = ReadDescription(session, upperEcu!, name, timeout, ct);
                if (d.Ok)
                {
                    comment = NullIfBlank(d.Value!.Text);
                    fetch = DescriptionFetch.Read;
                    if (fileHash is not null)
                    {
                        _descriptions.Add(fileHash, name, d.Value.Lines);
                    }
                }
                else if (session.ToolStarted is null)
                {
                    fetch = DescriptionFetch.Failed;

                    // Stopped by the request's limit, not by anything about this job: asking
                    // again gives it a fresh try.
                    budgetHit |= d.Failure!.Error.Code == ApiErrors.JobTimeout && session.BudgetLeftMs <= 0;
                }
                else
                {
                    skipped = true;
                }
            }
            else if (comments)
            {
                skipped = true;
            }

            entries.Add(new DescribeJobEntry(name, comment, ToJobVerdict(verdict, fetch, comment)));
        }

        var cutOff = !skipped && !budgetHit ? null
            : session.Poisoned ? CutOff.EdiabasStuck
            : session.ToolStarted is not null ? CutOff.ToolStarted
            : ct.IsCancellationRequested ? CutOff.Abandoned
            : CutOff.TimeLimit;
        return new DescribeListResponse(upperEcu!, variant, entries.Count, entries, _clock.ElapsedMs - start, cutOff);
    }

    /// <summary>The hash of the SGBD file that answered: the variant EDIABAS reported (for a group
    /// file, the SGBD it chose), else the requested name. Null means no caching.</summary>
    private string? DescribedFileHash(string? variant, string ecu)
    {
        var name = string.IsNullOrWhiteSpace(variant) ? ecu : variant.Trim();
        try
        {
            return _ecus.PrgHash(name);
        }
        catch (Exception ex)
        {
            _log.Warn($"{name}: could not hash the SGBD file for the description cache ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    public Outcome<DescribeJobResponse> DescribeJob(string? ecu, string? job, RequestAudit audit, CancellationToken ct, long queuedMs = 0)
    {
        _approvals?.Refresh();
        audit.Ecu = ecu;
        audit.Job = job;
        if (RequestRefusal() is { } refused)
        {
            return refused;
        }

        if (WaitedTooLong(queuedMs) is { } waited)
        {
            return waited;
        }

        if (ConflictFailure() is { } busy)
        {
            return busy;
        }

        // Describing a denied job is allowed, but its name must have the right shape.
        var verdict = _policy.Evaluate(ecu, job);
        if (verdict.Ecu is null || verdict.Job is null)
        {
            audit.Rule = verdict.Rule;
            return Refused(ecu, job, verdict.Rule);
        }

        audit.Ecu = verdict.Ecu;
        audit.Job = verdict.Job;

        if (ct.IsCancellationRequested)
        {
            return NotRun("The request was abandoned before it started.");
        }

        return InSession(queuedMs, audit, session => DescribeJobIn(session, verdict, audit, ct));
    }

    private Outcome<DescribeJobResponse> DescribeJobIn(Session session, PolicyVerdict verdict, RequestAudit audit, CancellationToken ct)
    {
        if (verdict.Ecu is null || verdict.Job is null)
        {
            // The caller has refused a name of the wrong shape.
            throw new InvalidOperationException("describe of a job whose name failed the shape rule");
        }

        var start = _clock.ElapsedMs;
        var jobs = ReadJobList(session, verdict.Ecu, audit, ct, out var variant, out _);
        if (!jobs.Ok)
        {
            return ToolStartedFailure(session) ?? jobs.Failure!;
        }

        if (!jobs.Value!.Any(j => string.Equals(j, verdict.Job, StringComparison.OrdinalIgnoreCase)))
        {
            return ApiFailure.Of(ApiErrors.JobNotFound, $"{verdict.Ecu} has no job {verdict.Job}.", new EcuJobDetails(verdict.Ecu, verdict.Job));
        }

        var timeout = _defaultTimeoutSeconds;
        var fileHash = DescribedFileHash(variant, verdict.Ecu);
        Outcome<TextList> d = fileHash is not null && _descriptions.TryGet(fileHash, verdict.Job, out var cached)
            ? new TextList(cached)
            : ReadDescription(session, verdict.Ecu, verdict.Job, timeout, ct);
        if (d.Ok && fileHash is not null)
        {
            _descriptions.Add(fileHash, verdict.Job, d.Value!.Lines);
        }

        string? comment = null;
        DescriptionFetch fetch;
        if (d.Ok)
        {
            comment = NullIfBlank(d.Value!.Text);
            fetch = DescriptionFetch.Read;
        }
        else if (d.Failure!.Error.Code == ApiErrors.EdiabasError)
        {
            fetch = DescriptionFetch.Failed;
        }
        else
        {
            return ToolStartedFailure(session) ?? d.Failure!;
        }

        // No partial answer here: a BMW tool that starts midway fails the request.
        var arguments = ReadParameterTable(session, verdict.Ecu, "_ARGUMENTS", verdict.Job, "ARG", "ARGTYPE", "ARGCOMMENT", timeout, ct);
        if (!arguments.Ok)
        {
            return ToolStartedFailure(session) ?? arguments.Failure!;
        }

        var results = ReadParameterTable(session, verdict.Ecu, "_RESULTS", verdict.Job, "RESULT", "RESULTTYPE", "RESULTCOMMENT", timeout, ct);
        if (!results.Ok)
        {
            return ToolStartedFailure(session) ?? results.Failure!;
        }

        return new DescribeJobResponse(
            verdict.Ecu,
            variant,
            verdict.Job,
            comment,
            comment is null ? null : CommentScreen.DescriptionHash(comment),
            ToJobVerdict(verdict, fetch, comment),
            arguments.Value!,
            results.Value!,
            _clock.ElapsedMs - start);
    }

    // ---------------------------------------------------------------- internals

    private enum DescriptionFetch
    {
        Skipped,
        Read,
        Failed,
    }

    private sealed record ItemRun(BatchItem Item, string? Ecu, string? Job, string? Rule, ApiFailure? WholeRequest);

    private sealed record BatchRun(IReadOnlyList<BatchItem> Items, ApiFailure? WholeRequest);

    private sealed record TextList(IReadOnlyList<string> Lines)
    {
        public string Text => string.Join("\n", Lines);
    }

    private BatchRun RunBatch(Session session, IReadOnlyList<JobRequest> items, int timeout, RequestAudit audit, CancellationToken ct)
    {
        var results = new List<BatchItem>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var itemStart = _clock.ElapsedMs;
            var run = RunItem(session, items[i], timeout, ct);
            if (run.WholeRequest is { } whole)
            {
                audit.Items.Add(new ItemAudit(i, run.Ecu, run.Job, run.Rule, whole.Error.Code, _clock.ElapsedMs - itemStart));
                return new BatchRun(results, whole);
            }

            results.Add(run.Item);
            audit.Items.Add(new ItemAudit(i, run.Ecu, run.Job, run.Rule, run.Item.Error?.Code, _clock.ElapsedMs - itemStart));
        }

        return new BatchRun(results, null);
    }

    private ItemRun RunItem(Session session, JobRequest request, int timeout, CancellationToken ct)
    {
        var itemStart = _clock.ElapsedMs;
        string? ecu = request.Ecu;
        string? job = request.Job;

        ItemRun Fail(ApiFailure f, string? rule = null) => new(new BatchItem(false, null, f.Error), ecu, job, rule, null);

        if (ct.IsCancellationRequested)
        {
            return Fail(NotRun("The request was abandoned before this job started."));
        }

        if (session.Poisoned)
        {
            return Fail(NotRun("An earlier job did not stop after apiBreak; no further jobs are started."));
        }

        if (session.BudgetLeftMs <= 0)
        {
            return Fail(NotRun($"The request reached its {_maxTimeoutSeconds} s limit before this job started."));
        }

        var verdict = _policy.Evaluate(request.Ecu, request.Job, request.Args, request.Results);
        ecu = verdict.Ecu ?? request.Ecu;
        job = verdict.Job ?? request.Job;
        if (!verdict.Allowed)
        {
            return Fail(Refused(ecu, job, verdict.Rule), verdict.Rule);
        }

        if (!_ecus.Exists(verdict.Ecu!))
        {
            return Fail(ApiFailure.Of(ApiErrors.EcuNotFound, $"There is no {verdict.Ecu}.prg or {verdict.Ecu}.grp in the Ecu folder.", new EcuDetails(verdict.Ecu!)));
        }

        // A BMW tool started since the request began: start nothing more. Execute checks again
        // before each job (the screen, then the job) and while it runs; EnsureInit after the load.
        if (session.ToolStarted is { } started)
        {
            return Fail(ToolStartedNotRun(started));
        }

        if (session.EnsureInit() is { } initFailure)
        {
            return new ItemRun(new BatchItem(false, null, initFailure.Error), ecu, job, null, initFailure);
        }

        if (verdict.Screen && ScreenJob(session, verdict.Ecu!, verdict.Job!, request.Args, timeout, ct) is { } screenFailure)
        {
            return Fail(screenFailure, screenFailure.Error.Code == ApiErrors.JobNeedsReview ? RequestAudit.ScreenRule : null);
        }

        var run = Execute(session, verdict.Ecu!, verdict.Job!, request.Args ?? "", request.Results ?? "", timeout, ct);
        if (!run.Ok)
        {
            return Fail(run.Failure!);
        }

        var read = run.Value!;
        var result = new JobResult(verdict.Ecu!, verdict.Job!, read.Variant, read.JobStatus, read.SystemResults, read.Sets, _clock.ElapsedMs - itemStart);
        return new ItemRun(new BatchItem(true, result, null), ecu, job, null, null);
    }

    /// <summary>
    /// The screen for a job allowed only by the name pattern. Null when the job may run. The
    /// description is always read. A pinned approval then stands in for the description screen
    /// only while the description still hashes to the pin; an unapproved job goes through the
    /// description screen. Either way a call with arguments then goes through the argument
    /// screen. A failure that is about the request (time, cancel, a BMW tool) keeps its own code.
    /// </summary>
    private ApiFailure? ScreenJob(Session session, string ecu, string job, string? args, int timeout, CancellationToken ct)
    {
        var approval = _screen.Approval(ecu, job);
        var d = ReadDescription(session, ecu, job, timeout, ct);
        if (approval.Kind == ApprovalKind.Pinned)
        {
            if (CheckPin(approval, d, ecu, job) is { } pinFailure)
            {
                return pinFailure;
            }
        }
        else
        {
            if (!d.Ok)
            {
                return d.Failure!;
            }

            var description = NullIfBlank(d.Value!.Text);
            var screened = _screen.Screen(description);
            if (!screened.Passed)
            {
                return ApiFailure.Of(
                    ApiErrors.JobNeedsReview,
                    description is null
                        ? $"{job} has no description, so it cannot be shown to be a read. The user can approve it in the agent config."
                        : $"The description of {job} mentions: {string.Join(", ", screened.Matched)}. The user can approve it in the agent config.",
                    new JobReviewDetails(ecu, job, description, screened.Matched));
            }
        }

        return string.IsNullOrEmpty(args) ? null : ScreenArguments(session, ecu, job, timeout, ct);
    }

    /// <summary>
    /// A pinned approval holds only for the description the user read: one that changed or is
    /// empty is <c>job_needs_review</c>. A description that cannot be read keeps its own failure,
    /// as for an unpinned job: an EDIABAS error there is usually a module that does not answer,
    /// not a changed approval, and the job is not run either way. A matching pin skips the
    /// description screen only.
    /// </summary>
    private static ApiFailure? CheckPin(JobApproval approval, Outcome<TextList> read, string ecu, string job)
    {
        if (!read.Ok)
        {
            return read.Failure;
        }

        var description = NullIfBlank(read.Value!.Text);
        if (approval.Matches(description))
        {
            return null;
        }

        var message = description is not null
            ? $"The description of {job} has changed since it was approved (now {CommentScreen.DescriptionHash(description)}). The user can approve it again in the agent config."
            : $"{job} has no description now, so its approval cannot be checked. The user can approve it again in the agent config.";
        return ApiFailure.Of(ApiErrors.JobNeedsReview, message, new JobReviewDetails(ecu, job, description, []));
    }

    /// <summary>
    /// The <c>_JOBS</c> list of an ECU. Shared by both describe modes.
    /// </summary>
    private Outcome<List<string>> ReadJobList(Session session, string? ecu, RequestAudit audit, CancellationToken ct, out string? variant, out string? upperEcu)
    {
        variant = null;
        upperEcu = null;
        var verdict = _policy.Evaluate(ecu, "_JOBS");
        if (!verdict.Allowed)
        {
            audit.Rule = verdict.Rule;
            return Refused(verdict.Ecu ?? ecu, "_JOBS", verdict.Rule);
        }

        upperEcu = verdict.Ecu!;
        audit.Ecu = upperEcu;
        if (!_ecus.Exists(upperEcu))
        {
            return ApiFailure.Of(ApiErrors.EcuNotFound, $"There is no {upperEcu}.prg or {upperEcu}.grp in the Ecu folder.", new EcuDetails(upperEcu));
        }

        if (session.EnsureInit() is { } initFailure)
        {
            return initFailure;
        }

        var run = Execute(session, upperEcu, "_JOBS", "", "", _defaultTimeoutSeconds, ct);
        if (!run.Ok)
        {
            return run.Failure!;
        }

        variant = run.Value!.Variant;
        var names = run.Value.Sets
            .Select(s => s.TryGetValue("JOBNAME", out var v) ? v as string : null)
            .OfType<string>()
            .Where(n => n.Length > 0)
            .ToList();
        return names;
    }

    /// <summary>
    /// The agent's own <c>_JOBCOMMENTS</c> call for one job. The argument is the job name the
    /// agent is about to run (or describe), never anything from the client, and the call itself
    /// passes the policy like any other job.
    /// </summary>
    private Outcome<TextList> ReadDescription(Session session, string ecu, string job, int timeout, CancellationToken ct)
    {
        var run = AgentJob(session, ecu, "_JOBCOMMENTS", job, timeout, ct);
        if (!run.Ok)
        {
            return run.Failure!;
        }

        // Every value of every job set, not only JOBCOMMENTn: text the screen does not see
        // could hide a write.
        var lines = run.Value!.Sets
            .SelectMany(s => s.Values)
            .Select(ResultShaping.AsText)
            .OfType<string>()
            .Where(t => t.Length > 0)
            .ToList();
        return new TextList(lines);
    }

    /// <summary>
    /// The argument screen for a screened job called with arguments: the job's own description
    /// passed, but the arguments it takes could still select a write. Every <c>ARG</c> name and
    /// <c>ARGCOMMENT</c> goes through the same screen. Null when the call may go ahead; a failed
    /// read or no argument rows at all is <c>job_needs_review</c>, because nothing then shows the
    /// arguments are harmless. A failure that is about the request (time, cancel, a BMW tool)
    /// keeps its own code.
    /// </summary>
    private ApiFailure? ScreenArguments(Session session, string ecu, string job, int timeout, CancellationToken ct)
    {
        var rows = ReadParameterRows(session, ecu, "_ARGUMENTS", job, "ARG", "ARGTYPE", "ARGCOMMENT", timeout, ct);
        if (!rows.Ok && rows.Failure!.Error.Code != ApiErrors.EdiabasError)
        {
            return rows.Failure;
        }

        var text = rows.Ok
            ? NullIfBlank(string.Join("\n", rows.Value!.SelectMany(a => new[] { a.Name, a.Comment }).OfType<string>().Where(t => t.Length > 0)))
            : null;
        var screened = _screen.Screen(text);
        if (screened.Passed)
        {
            return null;
        }

        return ApiFailure.Of(
            ApiErrors.JobNeedsReview,
            screened.Matched.Count > 0
                ? $"The argument description of {job} mentions: {string.Join(", ", screened.Matched)}. The user can approve it in the agent config."
                : $"{job} takes arguments but they are not described, so the call cannot be shown to be a read. The user can approve it in the agent config.",
            new JobReviewDetails(ecu, job, text, screened.Matched));
    }

    /// <summary>For describe: a failed read counts as no rows.</summary>
    private Outcome<List<JobParameterInfo>> ReadParameterTable(
        Session session, string ecu, string infoJob, string job, string nameKey, string typeKey, string commentPrefix, int timeout, CancellationToken ct)
    {
        var rows = ReadParameterRows(session, ecu, infoJob, job, nameKey, typeKey, commentPrefix, timeout, ct);

        // A job without arguments may make EDIABAS report an error rather than no sets.
        return !rows.Ok && rows.Failure!.Error.Code == ApiErrors.EdiabasError
            ? new List<JobParameterInfo>()
            : rows;
    }

    private Outcome<List<JobParameterInfo>> ReadParameterRows(
        Session session, string ecu, string infoJob, string job, string nameKey, string typeKey, string commentPrefix, int timeout, CancellationToken ct)
    {
        var run = AgentJob(session, ecu, infoJob, job, timeout, ct);
        if (!run.Ok)
        {
            return run.Failure!;
        }

        var list = run.Value!.Sets
            .Select(s => new JobParameterInfo(
                ResultShaping.AsText(s.GetValueOrDefault(nameKey)),
                ResultShaping.AsText(s.GetValueOrDefault(typeKey)),
                NullIfBlank(string.Join("\n", s
                    .Where(kv => kv.Key.StartsWith(commentPrefix, StringComparison.Ordinal))
                    .Select(kv => ResultShaping.AsText(kv.Value))
                    .OfType<string>()
                    .Where(t => t.Length > 0)))))
            .ToList();
        return list;
    }

    /// <summary>A job the agent issues itself, with an argument of its own: policy-checked too.</summary>
    private Outcome<ReadResults> AgentJob(Session session, string ecu, string infoJob, string argument, int timeout, CancellationToken ct)
    {
        if (session.Poisoned)
        {
            return NotRun("An earlier job did not stop after apiBreak; no further jobs are started.");
        }

        var verdict = _policy.Evaluate(ecu, infoJob, argument);
        if (!verdict.Allowed)
        {
            return Refused(verdict.Ecu ?? ecu, infoJob, verdict.Rule);
        }

        return Execute(session, verdict.Ecu!, verdict.Job!, argument, "", timeout, ct);
    }

    /// <summary>apiJob, poll, results. Init must already have succeeded.</summary>
    private Outcome<ReadResults> Execute(Session session, string ecu, string job, string parameters, string results, int timeoutSeconds, CancellationToken ct)
    {
        if (!session.Initialised)
        {
            // Programming error, not a runtime state: never call apiJob without apiInit.
            throw new InvalidOperationException("apiJob without a successful apiInit");
        }

        // The one gate every apiJob passes: a client that has gone starts nothing more, whether
        // the next job is its own or one of the agent's info jobs.
        if (ct.IsCancellationRequested)
        {
            return NotRun($"The request was abandoned before {job} started.");
        }

        // The request budget binds every job, not only the decision to start an item: no job may
        // run past it, so the whole request ends within maxJobTimeoutSeconds plus the break grace.
        var budgetLeftMs = session.BudgetLeftMs;
        if (budgetLeftMs <= 0)
        {
            return NotRun($"The request reached its {_maxTimeoutSeconds} s limit before {job} started.");
        }

        // Every EDIABAS job, not only the first: a BMW tool may have started since the last check.
        if (ToolStartedSince(session, force: false) is { } running)
        {
            return ToolStartedNotRun(running);
        }

        session.JobStarted = true;
        _api.Job(ecu, job, parameters, results);
        var started = _clock.ElapsedMs;
        var limitMs = Math.Min((long)timeoutSeconds * 1000, budgetLeftMs);
        int state;
        while ((state = _api.State()) == ApiState.Busy)
        {
            if (ct.IsCancellationRequested)
            {
                BreakAndSettle(session, ecu, job);
                return NotRun("The request was abandoned; the running job was stopped with apiBreak.");
            }

            if (_clock.ElapsedMs - started >= limitMs)
            {
                var why = limitMs < (long)timeoutSeconds * 1000
                    ? $"when the request reached its {_maxTimeoutSeconds} s limit"
                    : $"after {timeoutSeconds} s";
                _log.Warn($"{ecu} {job}: still busy {why}; calling apiBreak");
                BreakAndSettle(session, ecu, job);
                return ApiFailure.Of(ApiErrors.JobTimeout, $"{job} was still running {why} and was stopped.", new TimeoutDetails(timeoutSeconds));
            }

            // A BMW tool that starts while the job runs must not share the cable with it. A check
            // that fails cannot say no tool started: fail closed, and never leave the job busy for
            // apiEnd.
            IReadOnlyList<ConflictingProcess>? tool;
            try
            {
                tool = ToolStartedSince(session, force: false);
            }
            catch (Exception ex)
            {
                _log.Warn($"{ecu} {job}: the BMW tool check failed while it ran ({ex.GetType().Name}: {ex.Message}); calling apiBreak");
                BreakAndSettle(session, ecu, job);
                return NotRun($"A BMW tool check failed while {job} was running; the job was stopped with apiBreak.");
            }

            if (tool is not null)
            {
                _log.Warn($"{ecu} {job}: a BMW tool started while it ran; calling apiBreak");
                session.ToolStoppedJob = true;
                BreakAndSettle(session, ecu, job);
                return ToolStartedNotRun(tool, stoppedJob: true);
            }

            _clock.Sleep(PollIntervalMs);
        }

        if (state != ApiState.Ready)
        {
            if (state is not ApiState.Error and not ApiState.Break)
            {
                _log.Warn($"{ecu} {job}: unexpected apiState {state}");
            }

            return EdiabasFailure(ecu, job);
        }

        var read = ResultShaping.ReadAll(_api);
        return read is null ? UnreadableResults(ecu, job) : read;
    }

    /// <summary>The job ended ready but apiResultSets failed. EDIABAS may or may not have an error
    /// code for that, so whatever it says goes in the details, not in the message.</summary>
    private ApiFailure UnreadableResults(string ecu, string job)
    {
        var code = _api.ErrorCode();
        var text = _api.ErrorText();
        _log.Warn($"{ecu} {job}: finished but its results could not be read (EDIABAS error {code}: {text})");
        return ApiFailure.Of(ApiErrors.EdiabasError, "The job finished but its results could not be read.", new EdiabasErrorDetails(code, text, ResultsUnread: true));
    }

    private void BreakAndSettle(Session session, string ecu, string job)
    {
        _api.Break();
        var since = _clock.ElapsedMs;
        while (_api.State() == ApiState.Busy)
        {
            if (_clock.ElapsedMs - since >= BreakGraceMs)
            {
                // apiEnd on a handle whose job never stopped may block for good, and unloading the
                // DLL could pull code out from under the job: neither happens until a restart.
                _log.Warn($"{ecu} {job}: still busy {BreakGraceMs} ms after apiBreak; no apiEnd, api32.dll stays loaded and EDIABAS is not used again until the agent restarts");
                session.Poisoned = true;
                _stuck = true;
                _handleMayBeOpen = true;
                _dll.Stuck = true;
                return;
            }

            _clock.Sleep(PollIntervalMs);
        }
    }

    private ApiFailure EdiabasFailure(string ecu, string job)
    {
        var code = _api.ErrorCode();
        var text = _api.ErrorText();

        // Every code is logged, so codes not yet in the map can be added later.
        _log.Warn($"EDIABAS error {code} for {ecu} {job}: {text}");
        return EdiabasErrorMap.Failure(code, text);
    }

    private JobVerdict ToJobVerdict(PolicyVerdict v, DescriptionFetch fetch, string? comment)
    {
        if (!v.Allowed || !v.Screen)
        {
            return new JobVerdict(v.Allowed, v.Rule, Review.NotRequired);
        }

        // The same decision a run would make (ScreenJob).
        var approval = _screen.Approval(v.Ecu!, v.Job!);
        if (approval.Kind == ApprovalKind.Pinned)
        {
            var pinned = fetch switch
            {
                DescriptionFetch.Skipped => Review.NotChecked,
                DescriptionFetch.Read when approval.Matches(comment) => Review.Reviewed,
                _ => Review.NeedsReview,
            };
            return new JobVerdict(true, v.Rule, pinned);
        }

        var review = fetch switch
        {
            DescriptionFetch.Skipped => Review.NotChecked,
            DescriptionFetch.Failed => Review.NeedsReview,
            _ => _screen.Screen(comment).Passed ? Review.Passed : Review.NeedsReview,
        };
        return new JobVerdict(true, v.Rule, review);
    }

    /// <summary>
    /// Runs <paramref name="body"/> in one apiInit … apiEnd cycle, then (after apiEnd) checks what
    /// the request added to the IFH trace. Under enforce a finding replaces the answer with its
    /// failure. The check runs even when the body throws (the latch is then set all the same).
    /// </summary>
    private Outcome<T> InSession<T>(long queuedMs, RequestAudit audit, Func<Session, Outcome<T>> body)
        where T : class
    {
        Outcome<T>? outcome = null;
        var session = new Session(this, queuedMs);
        try
        {
            outcome = body(session);
        }
        finally
        {
            session.Dispose();
            if (session.TraceStart is { } start && _telegrams is not null)
            {
                var result = _telegrams.End(start, session.JobStarted);
                audit.TelegramsChecked = result.TelegramsChecked;
                lock (audit.TelegramFindings)
                {
                    audit.TelegramFindings.AddRange(result.Findings);
                }

                if (result.Failure is { } failure && outcome is not null)
                {
                    outcome = failure;
                }
            }
        }

        return outcome;
    }

    /// <summary>Refusals before anything else in an EDIABAS request: the telegram check's latch,
    /// and <c>requireGate</c> without a <c>GATE:</c> interface.</summary>
    private ApiFailure? RequestRefusal()
    {
        if (_telegrams?.LatchedRefusal() is { } latched)
        {
            return latched;
        }

        if (_requireGate && (_interface is null || !_interface.StartsWith(AgentConfig.GatePrefix, StringComparison.Ordinal)))
        {
            var why = $"requireGate is set in the agent config, but ediabasInterface is {(_interface is null ? "not set" : $"\"{_interface}\"")}, not a {AgentConfig.GatePrefix} interface";
            return ApiFailure.Of(
                ApiErrors.EdiabasUnavailable,
                $"EDIABAS is not used: {why}. Nothing was sent. The user sets ediabasInterface (for example \"GATE:OBD\") once the telegram gate is installed, or clears requireGate.",
                new ReasonDetails(why));
        }

        return null;
    }

    private ApiFailure? ConflictFailure()
    {
        var running = _conflicts.Running();
        return running.Count == 0
            ? null
            : ApiFailure.Of(ApiErrors.CableInUse, $"A BMW tool is using EDIABAS ({Names(running)}). Close it and try again.", new ProcessesDetails(running));
    }

    /// <summary>
    /// The BMW tools that started since the request began, else null. Once seen, the answer sticks
    /// for the rest of the request. Without <paramref name="force"/>, a check younger than
    /// <see cref="ConflictRecheckMs"/> is reused.
    /// </summary>
    private IReadOnlyList<ConflictingProcess>? ToolStartedSince(Session session, bool force)
    {
        if (session.ToolStarted is { } seen)
        {
            return seen;
        }

        if (!force && _clock.ElapsedMs - session.LastConflictCheckMs < ConflictRecheckMs)
        {
            return null;
        }

        var running = _conflicts.Running();
        session.LastConflictCheckMs = _clock.ElapsedMs;
        if (running.Count == 0)
        {
            return null;
        }

        _log.Warn($"A BMW tool started during the request ({Names(running)}); no further EDIABAS jobs in this request");
        session.ToolStarted = running;
        return running;
    }

    /// <param name="stoppedJob">True when the tool appeared while this job ran and the job was
    /// stopped with apiBreak; false when the job never started.</param>
    private static ApiFailure ToolStartedNotRun(IReadOnlyList<ConflictingProcess> running, bool stoppedJob = false) =>
        NotRun(stoppedJob
            ? $"A BMW tool started during the request ({Names(running)}); the running job was stopped with apiBreak and no further jobs are started."
            : $"A BMW tool started during the request ({Names(running)}); no further jobs are started.");

    /// <summary><c>cable_in_use</c> for a request that cannot give a partial answer, when a BMW
    /// tool started during it; null when none did.</summary>
    private static ApiFailure? ToolStartedFailure(Session session) =>
        session.ToolStarted is { } running
            ? ApiFailure.Of(
                ApiErrors.CableInUse,
                session.ToolStoppedJob
                    ? $"A BMW tool started during the request ({Names(running)}); the running job was stopped with apiBreak and no further EDIABAS job was started. Close it and try again."
                    : $"A BMW tool started during the request ({Names(running)}); no further EDIABAS job was started. Close it and try again.",
                new ProcessesDetails(running))
            : null;

    /// <summary><c>not_run</c> for a request that spent its whole budget queued behind another
    /// one: the client has given up or is about to, so EDIABAS is not even loaded.</summary>
    private ApiFailure? WaitedTooLong(long queuedMs) =>
        queuedMs >= (long)_maxTimeoutSeconds * 1000
            ? NotRun($"The request waited behind another EDIABAS request for the whole {_maxTimeoutSeconds} s limit; nothing was started.")
            : null;

    private static ApiFailure Refused(string? ecu, string? job, string rule) =>
        ApiFailure.Of(ApiErrors.JobNotAllowed, $"The job policy refuses this job (rule {rule}).", new JobRefusalDetails(ecu ?? "", job ?? "", rule));

    private static ApiFailure NotRun(string message) => ApiFailure.Of(ApiErrors.NotRun, message);

    private static string Names(IEnumerable<ConflictingProcess> running) =>
        string.Join(", ", running.Select(p => $"{p.Name} pid {p.Pid}"));

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private const string StuckReason = "an earlier job did not stop after apiBreak; the agent must be restarted";

    private const string StuckMessage =
        "EDIABAS cannot be used: an earlier job did not stop after apiBreak, so the agent no longer touches EDIABAS. " +
        "The agent must be restarted (restart Windows in the VM).";

    /// <summary>One apiInit … apiEnd cycle. apiEnd is called exactly once if apiInit was called.</summary>
    private sealed class Session : IDisposable
    {
        private readonly EdiabasService owner;
        private bool _initCalled;
        private ApiFailure? _initFailure;

        /// <param name="queuedMs">How long the request waited for the runner thread: it counts
        /// against the budget, because the client's own deadline started when it sent the request.</param>
        public Session(EdiabasService owner, long queuedMs)
        {
            this.owner = owner;
            var now = owner._clock.ElapsedMs;
            StartMs = now - Math.Max(0, queuedMs);
            LastConflictCheckMs = now;

            // Counted so the DLL is never released while a request is running.
            owner._openSessions++;
        }

        /// <summary>When the request arrived (before any time in the queue), for its overall budget.</summary>
        public long StartMs { get; }

        /// <summary>What is left of the request's <c>maxJobTimeoutSeconds</c> budget; zero or
        /// less means no further job may start.</summary>
        public long BudgetLeftMs => StartMs + (long)owner._maxTimeoutSeconds * 1000 - owner._clock.ElapsedMs;

        public bool Initialised { get; private set; }

        public bool Poisoned { get; set; }

        /// <summary>When the conflict check last ran: the request's own check just before the
        /// session opened, then every recheck.</summary>
        public long LastConflictCheckMs { get; set; }

        /// <summary>BMW tools seen to start during the request; null while none has.</summary>
        public IReadOnlyList<ConflictingProcess>? ToolStarted { get; set; }

        /// <summary>Whether a job was running when <see cref="ToolStarted"/> was seen, and was
        /// stopped with apiBreak.</summary>
        public bool ToolStoppedJob { get; set; }

        /// <summary>Where this request's part of the IFH trace starts; null when the telegram
        /// check is off, could not run (report), or apiInit was never reached.</summary>
        public TraceStart? TraceStart { get; private set; }

        /// <summary>Whether apiJob was called in this request (then EDIABAS must have traced it).</summary>
        public bool JobStarted { get; set; }

        public ApiFailure? EnsureInit()
        {
            if (_initCalled || _initFailure is not null)
            {
                return _initFailure;
            }

            if (owner._stuck)
            {
                _initFailure = ApiFailure.Of(ApiErrors.EdiabasUnavailable, StuckMessage, new ReasonDetails(StuckReason));
                return _initFailure;
            }

            if (!owner._api.TryLoad(out var reason))
            {
                owner._log.Warn($"EDIABAS unavailable: {reason}");
                _initFailure = ApiFailure.Of(ApiErrors.EdiabasUnavailable, "EDIABAS cannot be used: " + reason, new ReasonDetails(reason ?? "unknown"));
                return _initFailure;
            }

            if (!owner._loaded)
            {
                owner._loaded = true;
                owner._dll.Loaded = true;
                owner._log.Info("api32.dll loaded");
            }

            // Loading the DLL can take seconds: a BMW tool that started meanwhile gets the cable,
            // and no apiInit happens (so Dispose calls no apiEnd).
            if (owner.ToolStartedSince(this, force: true) is not null)
            {
                _initFailure = ToolStartedFailure(this);
                return _initFailure;
            }

            // The telegram check notes where the IFH trace ends now, just before apiInit, so what
            // it reads afterwards is this request's alone. Under enforce it refuses here, before
            // anything is sent, when EDIABAS.INI does not have IfhTrace 3.
            if (owner._telegrams is { } telegrams)
            {
                if (telegrams.Begin(out var start) is { } unavailable)
                {
                    _initFailure = unavailable;
                    return _initFailure;
                }

                TraceStart = start;
            }

            owner._cableUse.Mark(owner._clock.UtcNow);
            _initCalled = true;
            if (owner._api.Init(owner._interface))
            {
                Initialised = true;
                return null;
            }

            var code = owner._api.ErrorCode();
            var text = owner._api.ErrorText();
            owner._log.Warn($"apiInit failed: EDIABAS error {code}: {text}");
            var why = $"apiInit failed (error {code}: {text})";
            _initFailure = ApiFailure.Of(ApiErrors.EdiabasUnavailable, "EDIABAS cannot be used: " + why, new ReasonDetails(why));
            return _initFailure;
        }

        public void Dispose()
        {
            try
            {
                if (!_initCalled)
                {
                    return;
                }

                if (Poisoned)
                {
                    // The job never stopped: an apiEnd now has no bound. Logged in BreakAndSettle.
                    return;
                }

                try
                {
                    owner._api.End();
                }
                catch (Exception ex)
                {
                    // A handle may still be open: the DLL must not be unloaded under it, ever. Not
                    // rethrown: the job's result was already read, and the client should get it.
                    owner._handleMayBeOpen = true;
                    owner._dll.HeldOpen = true;
                    owner._log.Warn($"apiEnd threw {ex.GetType().Name}: {ex.Message}; api32.dll stays loaded until the agent restarts");
                }
            }
            finally
            {
                // The idle time counts from the end of the last request.
                owner._lastUseMs = owner._clock.ElapsedMs;
                owner._openSessions--;
            }
        }
    }
}
