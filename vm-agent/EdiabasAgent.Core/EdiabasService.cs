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
}

/// <summary>
/// Everything the EDIABAS endpoints do, against <see cref="IEdiabasApi"/>. The host calls these
/// methods on its one EDIABAS thread, one request at a time.
/// <para>Per request: conflict check → (lazily, at the first job that will really run) apiInit
/// → per job: policy → Ecu file → description screen where required → apiJob → poll apiState
/// every 50 ms until not busy or timeout (apiBreak on timeout) → read every result set →
/// apiEnd in <c>finally</c>. Nothing touches the API when the conflict check fails or when no
/// job survives the policy.</para>
/// <para>Every job the agent sends, including its own <c>_JOBS</c> / <c>_JOBCOMMENTS</c> /
/// <c>_ARGUMENTS</c> / <c>_RESULTS</c> calls, is checked by the policy first.</para>
/// </summary>
public sealed class EdiabasService
{
    public const int PollIntervalMs = 50;
    public const int BreakGraceMs = 2000;
    public const int MaxBatchItems = 20;

    private readonly IEdiabasApi _api;
    private readonly IConflictCheck _conflicts;
    private readonly IEcuCatalog _ecus;
    private readonly JobPolicy _policy;
    private readonly CommentScreen _screen;
    private readonly IRunnerClock _clock;
    private readonly CableUse _cableUse;
    private readonly IEdiabasLog _log;
    private readonly int _defaultTimeoutSeconds;
    private readonly int _maxTimeoutSeconds;

    public EdiabasService(
        IEdiabasApi api,
        IConflictCheck conflicts,
        IEcuCatalog ecus,
        JobPolicy policy,
        CommentScreen screen,
        IRunnerClock clock,
        CableUse cableUse,
        IEdiabasLog log,
        int defaultTimeoutSeconds,
        int maxTimeoutSeconds)
    {
        _api = api;
        _conflicts = conflicts;
        _ecus = ecus;
        _policy = policy;
        _screen = screen;
        _clock = clock;
        _cableUse = cableUse;
        _log = log;
        _maxTimeoutSeconds = Math.Max(1, maxTimeoutSeconds);
        _defaultTimeoutSeconds = Math.Clamp(defaultTimeoutSeconds, 1, _maxTimeoutSeconds);
    }

    /// <summary>The request's <c>timeoutSeconds</c>, defaulted and clamped to the maximum.</summary>
    public int EffectiveTimeout(int? requested) =>
        requested is null ? _defaultTimeoutSeconds : Math.Clamp(requested.Value, 1, _maxTimeoutSeconds);

    // ---------------------------------------------------------------- /ediabas/ecus

    public Outcome<EcusResponse> ListEcus(string? filter)
    {
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

    public Outcome<JobResult> RunJob(JobRequest request, int? timeoutSeconds, RequestAudit audit, CancellationToken ct)
    {
        audit.Ecu = request.Ecu;
        audit.Job = request.Job;
        if (ConflictFailure() is { } busy)
        {
            return busy;
        }

        using var session = new Session(this);
        var start = session.StartMs;
        var run = RunItem(session, request, EffectiveTimeout(timeoutSeconds), start, recheckConflicts: false, ct);
        audit.Ecu = run.Ecu;
        audit.Job = run.Job;
        audit.Rule = run.Rule;
        if (run.WholeRequest is { } whole)
        {
            return whole;
        }

        return run.Item.Ok
            ? run.Item.Result!
            : new ApiFailure(ApiErrors.Status(run.Item.Error!.Code), run.Item.Error);
    }

    // ---------------------------------------------------------------- /ediabas/jobs

    public Outcome<BatchResponse> RunJobs(IReadOnlyList<JobRequest> items, int? timeoutSeconds, RequestAudit audit, CancellationToken ct)
    {
        if (items.Count is < 1 or > MaxBatchItems)
        {
            return ApiFailure.Of(ApiErrors.BadRequest, $"items must hold 1 to {MaxBatchItems} jobs.", new ParameterDetails("items"));
        }

        if (ConflictFailure() is { } busy)
        {
            return busy;
        }

        using var session = new Session(this);
        var start = session.StartMs;
        var batch = RunBatch(session, items, EffectiveTimeout(timeoutSeconds), start, audit, ct);
        if (batch.WholeRequest is { } whole)
        {
            return whole;
        }

        return new BatchResponse(batch.Items, _clock.ElapsedMs - start);
    }

    // ---------------------------------------------------------------- /ediabas/vehicle

    public Outcome<VehicleResponse> Vehicle(IReadOnlyList<VoltageSource> voltageSources, RequestAudit audit, CancellationToken ct)
    {
        if (ConflictFailure() is { } busy)
        {
            return busy;
        }

        var items = new List<JobRequest>
        {
            new("UTILITY", "STATUS_ZUENDUNG"),
            new("UTILITY", "STATUS_UBATT"),
        };
        items.AddRange(voltageSources.Select(v => new JobRequest(v.Ecu, v.Job)));
        items.Add(new JobRequest("TMODE", "LESE_SPANNUNG_KL30"));
        items.Add(new JobRequest("TMODE", "LESE_SPANNUNG_KL15"));

        using var session = new Session(this);
        var start = session.StartMs;
        var batch = RunBatch(session, items, _defaultTimeoutSeconds, start, audit, ct);
        if (batch.WholeRequest is { } whole)
        {
            return whole;
        }

        var r = batch.Items;
        var n = voltageSources.Count;
        var voltages = voltageSources
            .Select((v, i) => new VehicleVoltage(v.Ecu, v.Job, r[2 + i].Ok, r[2 + i].Result, r[2 + i].Error))
            .ToList();
        return new VehicleResponse(r[0], r[1], voltages, r[2 + n], r[3 + n], _clock.ElapsedMs - start);
    }

    // ---------------------------------------------------------------- /ediabas/describe

    public Outcome<DescribeListResponse> DescribeList(string? ecu, string? filter, bool comments, RequestAudit audit, CancellationToken ct)
    {
        audit.Ecu = ecu;
        if (ConflictFailure() is { } busy)
        {
            return busy;
        }

        if (ct.IsCancellationRequested)
        {
            return NotRun("The request was abandoned before it started.");
        }

        using var session = new Session(this);
        var start = session.StartMs;
        var jobs = ReadJobList(session, ecu, audit, ct, out var variant, out var upperEcu);
        if (!jobs.Ok)
        {
            return jobs.Failure!;
        }

        var timeout = _defaultTimeoutSeconds;
        var entries = new List<DescribeJobEntry>();
        foreach (var name in jobs.Value!)
        {
            if (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var verdict = _policy.Evaluate(upperEcu, name);
            string? comment = null;
            var fetch = DescriptionFetch.Skipped;
            if (comments && !session.Poisoned && !ct.IsCancellationRequested && !OverBudget(start))
            {
                var d = ReadDescription(session, upperEcu!, name, timeout, ct);
                if (d.Ok)
                {
                    comment = NullIfBlank(d.Value!.Text);
                    fetch = DescriptionFetch.Read;
                }
                else
                {
                    fetch = DescriptionFetch.Failed;
                }
            }

            entries.Add(new DescribeJobEntry(name, comment, ToJobVerdict(verdict, fetch, comment)));
        }

        return new DescribeListResponse(upperEcu!, variant, entries.Count, entries, _clock.ElapsedMs - start);
    }

    public Outcome<DescribeJobResponse> DescribeJob(string? ecu, string? job, RequestAudit audit, CancellationToken ct)
    {
        audit.Ecu = ecu;
        audit.Job = job;
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

        using var session = new Session(this);
        var start = session.StartMs;
        var jobs = ReadJobList(session, verdict.Ecu, audit, ct, out var variant, out _);
        if (!jobs.Ok)
        {
            return jobs.Failure!;
        }

        if (!jobs.Value!.Any(j => string.Equals(j, verdict.Job, StringComparison.OrdinalIgnoreCase)))
        {
            return ApiFailure.Of(ApiErrors.JobNotFound, $"{verdict.Ecu} has no job {verdict.Job}.", new EcuJobDetails(verdict.Ecu, verdict.Job));
        }

        var timeout = _defaultTimeoutSeconds;
        var d = ReadDescription(session, verdict.Ecu, verdict.Job, timeout, ct);
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
            return d.Failure!;
        }

        var arguments = ReadParameterTable(session, verdict.Ecu, "_ARGUMENTS", verdict.Job, "ARG", "ARGTYPE", "ARGCOMMENT", timeout, ct);
        if (!arguments.Ok)
        {
            return arguments.Failure!;
        }

        var results = ReadParameterTable(session, verdict.Ecu, "_RESULTS", verdict.Job, "RESULT", "RESULTTYPE", "RESULTCOMMENT", timeout, ct);
        if (!results.Ok)
        {
            return results.Failure!;
        }

        return new DescribeJobResponse(
            verdict.Ecu,
            variant,
            verdict.Job,
            comment,
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

    private BatchRun RunBatch(Session session, IReadOnlyList<JobRequest> items, int timeout, long start, RequestAudit audit, CancellationToken ct)
    {
        var results = new List<BatchItem>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var itemStart = _clock.ElapsedMs;
            var run = RunItem(session, items[i], timeout, start, recheckConflicts: i > 0, ct);
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

    private ItemRun RunItem(Session session, JobRequest request, int timeout, long requestStart, bool recheckConflicts, CancellationToken ct)
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

        if (OverBudget(requestStart))
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

        // A BMW tool started since the request began: start nothing more.
        if (recheckConflicts && _conflicts.Running() is { Count: > 0 } running)
        {
            return Fail(NotRun($"A BMW tool started during the request ({Names(running)}); no further jobs are started."));
        }

        if (session.EnsureInit() is { } initFailure)
        {
            return new ItemRun(new BatchItem(false, null, initFailure.Error), ecu, job, null, initFailure);
        }

        if (verdict.Screen && !_screen.IsReviewed(verdict.Ecu!, verdict.Job!))
        {
            var d = ReadDescription(session, verdict.Ecu!, verdict.Job!, timeout, ct);
            if (!d.Ok)
            {
                return Fail(d.Failure!);
            }

            var description = NullIfBlank(d.Value!.Text);
            var screened = _screen.Screen(description);
            if (!screened.Passed)
            {
                return Fail(
                    ApiFailure.Of(
                        ApiErrors.JobNeedsReview,
                        description is null
                            ? $"{verdict.Job} has no description, so it cannot be shown to be a read. Ryan can approve it in the agent config."
                            : $"The description of {verdict.Job} mentions: {string.Join(", ", screened.Matched)}. Ryan can approve it in the agent config.",
                        new JobReviewDetails(verdict.Ecu!, verdict.Job!, description, screened.Matched)),
                    RequestAudit.ScreenRule);
            }
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

    private Outcome<List<JobParameterInfo>> ReadParameterTable(
        Session session, string ecu, string infoJob, string job, string nameKey, string typeKey, string commentPrefix, int timeout, CancellationToken ct)
    {
        var run = AgentJob(session, ecu, infoJob, job, timeout, ct);
        if (!run.Ok)
        {
            // A job without arguments may make EDIABAS report an error rather than no sets.
            return run.Failure!.Error.Code == ApiErrors.EdiabasError
                ? new List<JobParameterInfo>()
                : run.Failure!;
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

        // The request budget binds every job, not only the decision to start an item: no job may
        // run past it, so the whole request ends within maxJobTimeoutSeconds plus the break grace.
        var budgetLeftMs = session.StartMs + (long)_maxTimeoutSeconds * 1000 - _clock.ElapsedMs;
        if (budgetLeftMs <= 0)
        {
            return NotRun($"The request reached its {_maxTimeoutSeconds} s limit before {job} started.");
        }

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
        return read is null ? EdiabasFailure(ecu, job) : read;
    }

    private void BreakAndSettle(Session session, string ecu, string job)
    {
        _api.Break();
        var since = _clock.ElapsedMs;
        while (_api.State() == ApiState.Busy)
        {
            if (_clock.ElapsedMs - since >= BreakGraceMs)
            {
                _log.Warn($"{ecu} {job}: still busy {BreakGraceMs} ms after apiBreak; starting no further jobs in this request");
                session.Poisoned = true;
                return;
            }

            _clock.Sleep(PollIntervalMs);
        }
    }

    private ApiFailure EdiabasFailure(string ecu, string job)
    {
        var code = _api.ErrorCode();
        var text = _api.ErrorText();

        // Which codes mean "port busy" or "no cable" is settled at the desk; until then every
        // code is returned as it is and logged so it can be mapped later.
        _log.Warn($"EDIABAS error {code} for {ecu} {job}: {text}");
        return ApiFailure.Of(ApiErrors.EdiabasError, $"EDIABAS reported error {code}: {text}", new EdiabasErrorDetails(code, text));
    }

    private JobVerdict ToJobVerdict(PolicyVerdict v, DescriptionFetch fetch, string? comment)
    {
        if (!v.Allowed || !v.Screen)
        {
            return new JobVerdict(v.Allowed, v.Rule, Review.NotRequired);
        }

        if (_screen.IsReviewed(v.Ecu!, v.Job!))
        {
            return new JobVerdict(true, v.Rule, Review.Reviewed);
        }

        var review = fetch switch
        {
            DescriptionFetch.Skipped => Review.NotChecked,
            DescriptionFetch.Failed => Review.NeedsReview,
            _ => _screen.Screen(comment).Passed ? Review.Passed : Review.NeedsReview,
        };
        return new JobVerdict(true, v.Rule, review);
    }

    private ApiFailure? ConflictFailure()
    {
        var running = _conflicts.Running();
        return running.Count == 0
            ? null
            : ApiFailure.Of(ApiErrors.CableInUse, $"A BMW tool is using EDIABAS ({Names(running)}). Close it and try again.", new ProcessesDetails(running));
    }

    private bool OverBudget(long requestStart) => _clock.ElapsedMs - requestStart >= (long)_maxTimeoutSeconds * 1000;

    private static ApiFailure Refused(string? ecu, string? job, string rule) =>
        ApiFailure.Of(ApiErrors.JobNotAllowed, $"The job policy refuses this job (rule {rule}).", new JobRefusalDetails(ecu ?? "", job ?? "", rule));

    private static ApiFailure NotRun(string message) => ApiFailure.Of(ApiErrors.NotRun, message);

    private static string Names(IEnumerable<ConflictingProcess> running) =>
        string.Join(", ", running.Select(p => $"{p.Name} pid {p.Pid}"));

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>One apiInit … apiEnd cycle. apiEnd is called exactly once if apiInit was called.</summary>
    private sealed class Session(EdiabasService owner) : IDisposable
    {
        private bool _initCalled;
        private ApiFailure? _initFailure;

        /// <summary>When the request began, for its overall budget.</summary>
        public long StartMs { get; } = owner._clock.ElapsedMs;

        public bool Initialised { get; private set; }

        public bool Poisoned { get; set; }

        public ApiFailure? EnsureInit()
        {
            if (_initCalled || _initFailure is not null)
            {
                return _initFailure;
            }

            if (!owner._api.TryLoad(out var reason))
            {
                owner._log.Warn($"EDIABAS unavailable: {reason}");
                _initFailure = ApiFailure.Of(ApiErrors.EdiabasUnavailable, "EDIABAS cannot be used: " + reason, new ReasonDetails(reason ?? "unknown"));
                return _initFailure;
            }

            owner._cableUse.Mark(owner._clock.UtcNow);
            _initCalled = true;
            if (owner._api.Init())
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
            if (!_initCalled)
            {
                return;
            }

            try
            {
                owner._api.End();
            }
            catch (Exception ex)
            {
                owner._log.Warn($"apiEnd threw {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }
    }
}
