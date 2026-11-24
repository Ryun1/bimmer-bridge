using System.Globalization;

namespace EdiabasAgent.Core;

/// <summary>
/// The files the telegram check reads: <c>EDIABAS.INI</c> and the IFH trace. The host reads the
/// real files (sharing them with EDIABAS, which keeps the trace open); tests use a fake.
/// </summary>
public interface ITraceFiles
{
    /// <summary>The file's length in bytes; null when it does not exist. Throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when it cannot be asked.</summary>
    long? Length(string path);

    /// <summary>Up to <paramref name="count"/> bytes from <paramref name="offset"/>; fewer at the
    /// end of the file. Throws as <see cref="Length"/> does.</summary>
    byte[] Read(string path, long offset, int count);
}

/// <summary>
/// One thing the check found. <see cref="Reason"/> is a telegram rule reason (<c>service</c>,
/// <c>malformed</c>, …) for a refused telegram, or <c>unknown_function</c>,
/// <c>unreadable_trace</c>, <c>no_trace</c>, <c>trace_too_large</c>. Function, concept (four hex
/// digits) and frame (hex bytes) are null when they do not apply.
/// </summary>
public sealed record TelegramFinding(string Code, string Reason, string? Function, string? Concept, string? Frame, string Message);

/// <summary><c>telegram_not_allowed</c> details (docs/api.md).</summary>
public sealed record TelegramRefusalDetails(string? Function, string? Concept, string? Frame, string Reason);

/// <summary>What /health shows of the telegram check.</summary>
public sealed record TelegramLatchInfo(string At, string Code, string Reason, string? Function, string? Concept, string? Frame, string Message);

/// <summary>
/// The latch, shared between the EDIABAS runner (which sets it) and /health (which shows it).
/// It outlives a restart of the runner thread; only an agent restart clears it. Thread-safe.
/// </summary>
public sealed class TelegramLatch
{
    private TelegramLatchInfo? _info;

    public TelegramLatchInfo? Info => Volatile.Read(ref _info);

    /// <summary>Sets the latch unless it is already set; the first finding is the one shown.</summary>
    public void Set(TelegramLatchInfo info) => Interlocked.CompareExchange(ref _info, info, null);
}

/// <summary>The outcome of checking one request's trace: how many telegrams were judged, what
/// was found, and the failure the request gets (only under <c>enforce</c>).</summary>
public sealed record TelegramCheckResult(int TelegramsChecked, IReadOnlyList<TelegramFinding> Findings, ApiFailure? Failure);

/// <summary>
/// The agent's check of what EDIABAS sent (docs/api.md "Telegram check (agent)"). Per EDIABAS
/// request: <see cref="Begin"/> before <c>apiInit</c> reads <c>EDIABAS.INI</c> and notes the
/// length of <c>IFH.trc</c>; <see cref="End"/> after <c>apiEnd</c> reads what was added, parses it
/// (<see cref="IfhTrace"/>) and judges every <c>SENDECUTEL</c> with the telegram rule.
/// <para>Findings: a telegram the rule refuses, a function name not in <see cref="KnownFunctions"/>,
/// a trace line the parser cannot read, more trace than <see cref="MaxSegmentBytes"/>
/// (<c>telegram_not_allowed</c>); and no trace at all for a request that ran a job, or a trace
/// that could not be read (<c>telegram_check_unavailable</c>). Under <c>enforce</c> any finding
/// fails the request and latches: every later EDIABAS request is refused with the latched code
/// until the agent restarts. Under <c>report</c> findings are only logged and audited.</para>
/// <para>Not thread-safe: used on the EDIABAS runner thread only, apart from the latch.</para>
/// </summary>
public sealed class TelegramCheck
{
    public const string TelegramNotAllowed = ApiErrors.TelegramNotAllowed;
    public const string CheckUnavailable = ApiErrors.TelegramCheckUnavailable;

    public const string ReasonUnknownFunction = "unknown_function";
    public const string ReasonUnreadable = "unreadable_trace";
    public const string ReasonNoTrace = "no_trace";
    public const string ReasonTooLarge = "trace_too_large";
    public const string ReasonRewritten = "trace_rewritten";
    public const string ReasonJobNotStopped = "job_not_stopped";
    public const string ReasonPreface = "telegram_preface";
    public const string ReasonCheckFailed = "check_failed";

    /// <summary>More trace than this for one request is not read; it is a finding.</summary>
    public const int MaxSegmentBytes = 16 * 1024 * 1024;

    /// <summary>How many bytes before a request's start are kept and compared afterwards, to see
    /// that the trace was appended to, not replaced or rewritten.</summary>
    public const int FingerprintBytes = 256;

    /// <summary>The function that sets the telegram preface; EDIABAS calls it with four zero bytes
    /// before every job. A real preface changes the bytes on the wire, so anything else is a
    /// finding.</summary>
    public const string SetTelParameter = "SETTELPARAMETER";

    private const int MaxIniBytes = 1024 * 1024;

    /// <summary>The interface functions seen in the VM's trace of read sessions. Any other name
    /// (a frequent or repeated telegram, raw mode, programming voltage, …) is a finding.</summary>
    public static readonly IReadOnlySet<string> KnownFunctions = new HashSet<string>(StringComparer.Ordinal)
    {
        IfhTrace.Open, "GETBATTERYVOLTAGE", "STOPECUTELFREQUENT", IfhTrace.SetEcuComm, SetTelParameter, IfhTrace.SendEcuTel,
    };

    private readonly string _mode;
    private readonly string _iniPath;
    private readonly ITraceFiles _files;
    private readonly TelegramRule _rule;
    private readonly TelegramLatch _latch;
    private readonly IEdiabasLog _log;
    private readonly IRunnerClock _clock;

    // Where the last segment read ended, and the concept in force there: the next request starts
    // with it only when nothing else wrote to the trace in between.
    private string? _carryPath;
    private long _carryOffset = -1;
    private ushort? _carryConcept;

    public TelegramCheck(string mode, string iniPath, ITraceFiles files, TelegramRule rule, TelegramLatch latch, IEdiabasLog log, IRunnerClock clock)
    {
        _mode = TelegramCheckModes.IsValid(mode) ? mode : TelegramCheckModes.Enforce;
        _iniPath = iniPath;
        _files = files;
        _rule = rule;
        _latch = latch;
        _log = log;
        _clock = clock;
    }

    public string Mode => _mode;

    private bool Enforce => _mode == TelegramCheckModes.Enforce;

    /// <summary>The refusal every EDIABAS request gets once the check has latched; else null.</summary>
    public ApiFailure? LatchedRefusal()
    {
        if (_latch.Info is not { } l)
        {
            return null;
        }

        var message = $"The agent stopped using EDIABAS at {l.At}: {l.Message} Every EDIABAS request is refused until the agent is restarted.";
        return l.Code == TelegramNotAllowed
            ? ApiFailure.Of(TelegramNotAllowed, message, new TelegramRefusalDetails(l.Function, l.Concept, l.Frame, l.Reason))
            : ApiFailure.Of(CheckUnavailable, message, new ReasonDetails(l.Reason));
    }

    /// <summary>
    /// Before <c>apiInit</c>: reads EDIABAS.INI and notes where the trace ends. Returns the
    /// failure for the request (only under <c>enforce</c>, before anything was sent), else null;
    /// <paramref name="start"/> is null when nothing is to be checked after the request.
    /// </summary>
    public ApiFailure? Begin(out TraceStart? start)
    {
        start = null;
        if (_mode == TelegramCheckModes.Off)
        {
            return null;
        }

        string? why;
        string? traceFile = null;
        long length = 0;
        try
        {
            why = _rule.Loaded ? ReadIni(out traceFile) : $"the telegram rule built into the agent did not load ({_rule.LoadError})";
            if (why is null)
            {
                length = _files.Length(traceFile!) ?? 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            why = $"{(traceFile ?? _iniPath)} cannot be read ({ex.GetType().Name}: {ex.Message})";
        }

        if (why is not null)
        {
            if (!Enforce)
            {
                _log.Warn($"telegram check (report): not checking this request: {why}");
                return null;
            }

            _log.Warn($"telegram check: refusing the request before EDIABAS is used: {why}");
            var fix = _rule.Loaded
                ? $" The user needs IfhTrace = {EdiabasIni.RequiredIfhTrace} (and a full TracePath) in {_iniPath}; that file is theirs to edit."
                : " The agent needs a build with a valid policy/telegrams.json.";
            return ApiFailure.Of(CheckUnavailable, $"The telegram check cannot run, so nothing was sent: {why}.{fix}", new ReasonDetails(why));
        }

        // Trace written between two requests is another program's (a BMW tool, which the conflict
        // check keeps out during a request) or a late write of ours: it is not judged, only noted.
        if (traceFile == _carryPath && length != _carryOffset)
        {
            _log.Info($"telegram check: {traceFile} changed between requests ({_carryOffset} -> {length} bytes); that part is not checked");
        }

        var concept = traceFile == _carryPath && length == _carryOffset ? _carryConcept : null;
        byte[] tail;
        try
        {
            var n = (int)Math.Min(length, FingerprintBytes);
            tail = n > 0 ? _files.Read(traceFile!, length - n, n) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            tail = [];
            _log.Warn($"telegram check: the end of {traceFile} cannot be read before the request ({ex.GetType().Name}); a replaced trace is then noticed only if it is shorter");
        }

        start = new TraceStart(traceFile!, length, concept, tail);
        return null;
    }

    /// <summary>Reads IfhTrace and TracePath; null when the check can run, else why not.</summary>
    private string? ReadIni(out string? traceFile)
    {
        traceFile = null;
        var size = _files.Length(_iniPath);
        if (size is null)
        {
            return $"{_iniPath} does not exist";
        }

        var ini = EdiabasIni.Parse(Win1252.Encoding.GetString(_files.Read(_iniPath, 0, (int)Math.Min(size.Value, MaxIniBytes))));
        if (ini.IfhTraceLevel is not { } level || level < EdiabasIni.RequiredIfhTrace)
        {
            return $"IfhTrace in {_iniPath} is {(ini.IfhTrace is null ? "not set" : $"\"{Clip(ini.IfhTrace)}\"")}; it must be {EdiabasIni.RequiredIfhTrace} or more";
        }

        traceFile = ini.TraceFile;
        return traceFile is null
            ? $"TracePath in {_iniPath} is {(ini.TracePath is null ? "not set" : $"\"{Clip(ini.TracePath)}\", not a full path")}"
            : null;
    }

    /// <summary>
    /// After <c>apiEnd</c>: reads and judges what the request added to the trace.
    /// <paramref name="ranJob"/> says whether the request started an EDIABAS job (then the trace
    /// must have grown).
    /// </summary>
    public TelegramCheckResult End(TraceStart start, bool ranJob, bool jobNotStopped = false)
    {
        try
        {
            return EndCore(start, ranJob, jobNotStopped);
        }
        catch (Exception ex)
        {
            // Whatever went wrong (memory, a regex timeout, a bug), the request was not checked.
            var findings = new List<TelegramFinding>
            {
                new(CheckUnavailable, ReasonCheckFailed, null, null, null,
                    $"the check of {start.TraceFile} failed ({ex.GetType().Name}: {ex.Message}), so what EDIABAS sent was not checked."),
            };
            return Finish(start, findings, 0, null, null);
        }
    }

    private TelegramCheckResult EndCore(TraceStart start, bool ranJob, bool jobNotStopped)
    {
        var findings = new List<TelegramFinding>();
        if (jobNotStopped)
        {
            findings.Add(new TelegramFinding(CheckUnavailable, ReasonJobNotStopped, null, null, null,
                "a job did not stop after apiBreak, so what it sends from now on cannot be checked."));
        }

        var checkedCount = 0;
        string text;
        var rotated = false;
        long readFrom = start.Length;
        try
        {
            var now = _files.Length(start.TraceFile) ?? 0;
            if (now < start.Length)
            {
                // The file is shorter than before the request: EDIABAS started a new one.
                rotated = true;
                readFrom = 0;
            }

            if (!rotated && start.Tail.Length > 0)
            {
                var again = _files.Read(start.TraceFile, start.Length - start.Tail.Length, start.Tail.Length);
                if (!again.AsSpan().SequenceEqual(start.Tail))
                {
                    findings.Add(new TelegramFinding(CheckUnavailable, ReasonRewritten, null, null, null,
                        $"{start.TraceFile} was replaced or rewritten during the request (its bytes before the request's start changed), so what EDIABAS sent cannot be found in it."));
                    return Finish(start, findings, 0, null, null);
                }
            }

            var added = now - readFrom;
            if (added > MaxSegmentBytes)
            {
                findings.Add(new TelegramFinding(TelegramNotAllowed, ReasonTooLarge, null, null, null,
                    $"the request added {added} bytes to {start.TraceFile}, more than the check reads ({MaxSegmentBytes})."));
                return Finish(start, findings, 0, null, null);
            }

            var bytes = added > 0 ? _files.Read(start.TraceFile, readFrom, (int)added) : [];
            readFrom += bytes.Length;
            text = Win1252.Encoding.GetString(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var why = $"{start.TraceFile} cannot be read after the request ({ex.GetType().Name}: {ex.Message})";
            findings.Add(new TelegramFinding(CheckUnavailable, ReasonUnreadable, null, null, null, why + "."));
            return Finish(start, findings, 0, null, null);
        }

        var segment = IfhTrace.Parse(text, rotated ? null : start.Concept);
        var note = rotated ? $" ({start.TraceFile} was replaced during the request, so the whole new file was read)" : "";
        if (rotated)
        {
            _log.Warn($"telegram check: {start.TraceFile} is shorter than before the request; reading the whole new file");
        }

        if (ranJob && segment.TraceLines == 0)
        {
            findings.Add(new TelegramFinding(CheckUnavailable, ReasonNoTrace, null, null, null,
                $"EDIABAS wrote no interface trace for a request that ran a job, so what it sent cannot be checked{note}."));
        }

        foreach (var p in segment.Problems)
        {
            findings.Add(new TelegramFinding(TelegramNotAllowed, ReasonUnreadable, null, null, null,
                $"a line of the interface trace cannot be read ({p.Why}: \"{p.Text}\"), so what it held cannot be checked{note}."));
        }

        foreach (var e in segment.Events)
        {
            var concept = TelegramRule.ConceptHex(e.Concept);
            var input = e.Input is null ? null : TelegramRule.Hex(e.Input);
            if (!KnownFunctions.Contains(e.Name))
            {
                findings.Add(new TelegramFinding(TelegramNotAllowed, ReasonUnknownFunction, e.Name, concept, input,
                    $"EDIABAS called the interface function {e.Name}, which the check does not know{note}."));
                continue;
            }

            if (e.Name == SetTelParameter && (e.Input ?? []).Any(b => b != 0))
            {
                findings.Add(new TelegramFinding(TelegramNotAllowed, ReasonPreface, e.Name, concept, input,
                    $"EDIABAS set the telegram preface to {input}, which would change the bytes sent after they were checked{note}."));
                continue;
            }

            if (e.Name != IfhTrace.SendEcuTel)
            {
                continue;
            }

            checkedCount++;
            var v = _rule.Evaluate(e.Concept, e.Input ?? []);
            if (!v.Allowed)
            {
                findings.Add(new TelegramFinding(TelegramNotAllowed, v.Reason!, e.Name, concept, input,
                    $"EDIABAS sent {(input is null or "" ? "a telegram with no bytes" : input)} under concept {concept ?? "none"}, which the telegram rule refuses ({v.Reason}){note}."));
            }
        }

        return Finish(start, findings, checkedCount, start.TraceFile, readFrom, segment.EndConcept);
    }

    private TelegramCheckResult Finish(TraceStart start, List<TelegramFinding> findings, int checkedCount, string? carryPath, long? carryOffset, ushort? carryConcept = null)
    {
        // Carry the concept only from a clean segment; after anything odd, start the next one unknown.
        _carryPath = findings.Count == 0 ? carryPath : null;
        _carryOffset = findings.Count == 0 && carryOffset is { } o ? o : -1;
        _carryConcept = findings.Count == 0 ? carryConcept : null;

        foreach (var f in findings)
        {
            var where = f.Function is null ? "" : $" [{f.Function}{(f.Concept is null ? "" : " concept " + f.Concept)}{(f.Frame is null ? "" : " frame " + f.Frame)}]";
            _log.Warn(AuditLine.Neutralise($"telegram check ({_mode}): {f.Code} {f.Reason}{where}: {f.Message}"));
        }

        if (findings.Count == 0 || !Enforce)
        {
            return new TelegramCheckResult(checkedCount, findings, null);
        }

        // The first finding that refuses a telegram outranks one that only says the check could not run.
        var first = findings.FirstOrDefault(f => f.Code == TelegramNotAllowed) ?? findings[0];
        var at = ApiFormat.Timestamp(_clock.UtcNow);
        _latch.Set(new TelegramLatchInfo(at, first.Code, first.Reason, first.Function, first.Concept, first.Frame, Capitalise(first.Message)));
        var latched = _latch.Info!;
        _log.Warn($"telegram check: latched at {latched.At} ({latched.Code} {latched.Reason}); every EDIABAS request is refused until the agent restarts");

        var message = first.Code == TelegramNotAllowed
            ? $"The telegram check found: {first.Message} The agent now refuses every EDIABAS request until it is restarted."
            : $"The telegram check could not verify this request: {first.Message} The agent now refuses every EDIABAS request until it is restarted.";
        var failure = first.Code == TelegramNotAllowed
            ? ApiFailure.Of(TelegramNotAllowed, message, new TelegramRefusalDetails(first.Function, first.Concept, first.Frame, first.Reason))
            : ApiFailure.Of(CheckUnavailable, message, new ReasonDetails(first.Reason));
        return new TelegramCheckResult(checkedCount, findings, failure);
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.InvariantCulture) + s[1..];

    private static string Clip(string s) => s.Length > 64 ? s[..64] + "..." : s;
}

/// <summary>Where a request's part of the trace starts, and the concept in force there.</summary>
/// <para><paramref name="Tail"/>: the trace's last bytes before the start, compared after the
/// request.</para>
public sealed record TraceStart(string TraceFile, long Length, ushort? Concept, byte[] Tail)
{
    public TraceStart(string traceFile, long length, ushort? concept)
        : this(traceFile, length, concept, [])
    {
    }
}
