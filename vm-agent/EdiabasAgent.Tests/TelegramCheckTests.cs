using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

/// <summary>Files for the telegram check: path to bytes. A path not present does not exist.</summary>
public sealed class FakeTraceFiles : ITraceFiles
{
    public Dictionary<string, List<byte>> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths whose reads throw IOException.</summary>
    public HashSet<string> Broken { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Write(string path, string text) => Files[path] = [.. Win1252.Encoding.GetBytes(text)];

    public void Append(string path, string text)
    {
        if (!Files.TryGetValue(path, out var bytes))
        {
            Files[path] = bytes = [];
        }

        bytes.AddRange(Win1252.Encoding.GetBytes(text));
    }

    public long? Length(string path)
    {
        if (Broken.Contains(path))
        {
            throw new IOException("sharing violation");
        }

        return Files.TryGetValue(path, out var b) ? b.Count : null;
    }

    public byte[] Read(string path, long offset, int count)
    {
        if (Broken.Contains(path))
        {
            throw new IOException("sharing violation");
        }

        var b = Files[path];
        return [.. b.Skip((int)offset).Take(count)];
    }
}

public class TelegramCheckTests
{
    private const string Dll = @"C:\EDIABAS\BIN\api32.dll";
    private const string Ini = @"C:\EDIABAS\BIN\EDIABAS.INI";
    private const string Trace = @"C:\EDIABAS\TRACE\IFH.trc";
    private const string Meta = "\t\t\t\t\t\t\t\t#@1;P00002600h;#\r\n";

    private readonly FakeEdiabasApi _api = new();
    private readonly FakeClock _clock = new();
    private readonly FakeConflicts _conflicts = new();
    private readonly FakeLog _log = new();
    private readonly DllLoadState _dll = new();
    private readonly FakeTraceFiles _files = new();
    private readonly TelegramLatch _latch = new();

    public TelegramCheckTests()
    {
        _files.Write(Ini, "[Configuration]\r\nEcuPath = C:\\EDIABAS\\ECU\r\nTracePath = C:\\EDIABAS\\TRACE\r\nIfhTrace = 3\r\n");
        _files.Write(Trace, Lines("_STD      >>>> Interface Communication Trace from Sun Nov 22 19:48:32 2026 <<<<"));
        _api.On("NVE_65 IDENT", FakeJob.Ok()).On("NVE_65 FS_LESEN", FakeJob.Ok());

        // apiInit opens the interface; the SGBD sets the concept (BMW-FAST) and sends a read.
        _api.DuringInit = () => _files.Append(Trace, Call("OPEN /Sun Nov 22 19:48:32 2026", "--"));
        Sends = [("010F", "82 12 F1 1A 80")];
        _api.DuringJob = () =>
        {
            foreach (var (concept, frame) in Sends)
            {
                if (concept is not null)
                {
                    _files.Append(Trace, Call("SETECUCOMM", concept[2..] + " " + concept[..2] + " 00 00 00 C2 01 00 B0 04 00 00 14 00 00 00 0A 00 00 00"));
                }

                _files.Append(Trace, Call("SENDECUTEL", frame));
            }
        };
    }

    /// <summary>What each apiJob writes to the trace: a concept to set first (or null), and a telegram.</summary>
    private List<(string? Concept, string Frame)> Sends { get; set; }

    private static string Lines(params string[] lines) => string.Concat(lines.Select(l => Meta + l + "\r\n"));

    private static string Call(string name, string input, string error = "NO ERROR") =>
        Lines($"_STD     [        16] {name}", $"_STD     [        16]  INPUT: {input} ", $"_STD     [        16]  ERROR: {error}", "_STD     [        16]  OUTPUT: --");

    private EdiabasService Service(string mode = TelegramCheckModes.Enforce, TelegramRule? rule = null, string? ediabasInterface = null, bool requireGate = false, bool check = true) =>
        new(
            _api,
            _conflicts,
            new FakeEcus("NVE_65"),
            JobPolicy.Embedded,
            new CommentScreen(JobPolicy.Embedded.ScreenTerms, JobPolicy.Embedded.ScreenIgnore, []),
            _clock,
            new CableUse(),
            _log,
            _dll,
            30,
            60,
            20,
            telegrams: check ? new TelegramCheck(mode, EdiabasIni.BesideDll(Dll), _files, rule ?? TelegramRule.Embedded, _latch, _log, _clock) : null,
            ediabasInterface: ediabasInterface,
            requireGate: requireGate);

    private Outcome<JobResult> Run(EdiabasService svc, RequestAudit? audit = null, string job = "IDENT") =>
        svc.RunJob(new JobRequest("NVE_65", job), null, audit ?? new RequestAudit(), default);

    // ---- a clean request ----

    [Fact]
    public void AllowedTelegram_Passes_AndIsCounted()
    {
        var audit = new RequestAudit();
        var r = Run(Service(), audit);

        Assert.True(r.Ok, r.Failure?.Error.Message);
        Assert.Equal(1, audit.TelegramsChecked);
        Assert.Empty(audit.TelegramFindings);
        Assert.Null(_latch.Info);
    }

    [Fact]
    public void Batch_IsOneRequest_AndCountsEveryTelegram()
    {
        Sends = [("010F", "82 12 F1 1A 80"), (null, "84 12 F1 18 02 FF FF")];
        var audit = new RequestAudit();
        var r = Service().RunJobs([new("NVE_65", "IDENT"), new("NVE_65", "FS_LESEN")], null, audit, default);

        Assert.True(r.Ok, r.Failure?.Error.Message);
        Assert.Equal(1, _api.Count("Init"));
        Assert.Equal(4, audit.TelegramsChecked);
    }

    [Fact]
    public void PolicyRefusal_UsesNoEdiabas_AndChecksNothing()
    {
        var audit = new RequestAudit();
        var r = Run(Service(), audit, "FS_LOESCHEN");

        Assert.Equal(ApiErrors.JobNotAllowed, r.Failure!.Error.Code);
        Assert.Null(audit.TelegramsChecked);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public void ConceptCarriesToTheNextRequest_WhenNothingElseWroteToTheTrace()
    {
        var svc = Service();
        Assert.True(Run(svc).Ok);

        // No OPEN this time, and no SETECUCOMM: the concept from the first request still holds.
        _api.DuringInit = null;
        Sends = [(null, "82 12 F1 1A 80")];
        Assert.True(Run(svc).Ok);
    }

    [Fact]
    public void ConceptIsForgotten_WhenSomethingElseWroteBetweenRequests()
    {
        var svc = Service();
        Assert.True(Run(svc).Ok);

        _files.Append(Trace, Call("SETTELPARAMETER", "00 00 00 00"));
        _api.DuringInit = null;
        Sends = [(null, "82 12 F1 1A 80")];
        var r = Run(svc);

        Assert.Equal(ApiErrors.TelegramNotAllowed, r.Failure!.Error.Code);
        Assert.Equal("no_concept", ((TelegramRefusalDetails)r.Failure.Error.Details!).Reason);
        Assert.Contains(_log.Lines, l => l.Contains("changed between requests", StringComparison.Ordinal));
    }

    // ---- findings under enforce ----

    [Fact]
    public void RefusedTelegram_FailsTheRequest_AndLatches()
    {
        Sends = [("010F", "83 12 F1 14 FF FF")];
        var audit = new RequestAudit();
        var svc = Service();

        var r = Run(svc, audit);

        Assert.Equal(ApiErrors.TelegramNotAllowed, r.Failure!.Error.Code);
        Assert.Equal(403, r.Failure.Status);
        var d = Assert.IsType<TelegramRefusalDetails>(r.Failure.Error.Details);
        Assert.Equal(new TelegramRefusalDetails("SENDECUTEL", "010F", "83 12 F1 14 FF FF", "service"), d);
        Assert.Contains("until it is restarted", r.Failure.Error.Message);
        Assert.Equal(1, audit.TelegramsChecked);
        Assert.Single(audit.TelegramFindings);

        // apiEnd ran before the check: the request's own cycle completed.
        Assert.Equal("End", _api.Calls[^1]);

        var latch = _latch.Info!;
        Assert.Equal(ApiErrors.TelegramNotAllowed, latch.Code);
        Assert.Equal("service", latch.Reason);
        Assert.Equal("83 12 F1 14 FF FF", latch.Frame);
        Assert.Equal("2026-11-21T17:00:00Z", latch.At);
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN telegram check: latched", StringComparison.Ordinal));
    }

    [Fact]
    public void Latched_RefusesEveryLaterRequest_BeforeAnything()
    {
        Sends = [("010F", "83 12 F1 14 FF FF")];
        var svc = Service();
        Run(svc);
        _api.Calls.Clear();
        var conflictChecks = _conflicts.Calls;

        var job = Run(svc);
        var batch = svc.RunJobs([new("NVE_65", "IDENT")], null, new RequestAudit(), default);
        var list = svc.DescribeList("NVE_65", null, true, new RequestAudit(), default);
        var one = svc.DescribeJob("NVE_65", "IDENT", new RequestAudit(), default);
        var ecus = svc.ListEcus(null);

        foreach (var f in new[] { job.Failure, batch.Failure, list.Failure, one.Failure, ecus.Failure })
        {
            Assert.Equal(ApiErrors.TelegramNotAllowed, f!.Error.Code);
            Assert.Equal("83 12 F1 14 FF FF", ((TelegramRefusalDetails)f.Error.Details!).Frame);
            Assert.Contains("2026-11-21T17:00:00Z", f.Error.Message);
        }

        Assert.Empty(_api.Calls);
        Assert.Equal(conflictChecks, _conflicts.Calls);
    }

    [Fact]
    public void Latch_OutlivesTheService()
    {
        // The runner thread may be restarted with a new service; only an agent restart clears it.
        Sends = [("010F", "83 12 F1 14 FF FF")];
        Run(Service());

        Assert.Equal(ApiErrors.TelegramNotAllowed, Run(Service()).Failure!.Error.Code);
    }

    [Fact]
    public void UnknownFunction_IsAFinding()
    {
        _api.DuringJob = () => _files.Append(Trace, Call("SENDECUTELFREQUENT", "82 12 F1 1A 80"));

        var r = Run(Service());

        var d = (TelegramRefusalDetails)r.Failure!.Error.Details!;
        Assert.Equal(new TelegramRefusalDetails("SENDECUTELFREQUENT", null, "82 12 F1 1A 80", "unknown_function"), d);
    }

    [Fact]
    public void UnreadableTraceLine_IsAFinding()
    {
        _api.DuringJob = () => _files.Append(Trace, Lines("_STD     [ 0] SENDECUTEL", "????"));

        var r = Run(Service());

        Assert.Equal(ApiErrors.TelegramNotAllowed, r.Failure!.Error.Code);
        Assert.Equal("unreadable_trace", ((TelegramRefusalDetails)r.Failure.Error.Details!).Reason);
    }

    [Fact]
    public void NoTraceForAJob_IsUnavailable_AndLatches()
    {
        _api.DuringInit = null;
        _api.DuringJob = null;

        var r = Run(Service());

        Assert.Equal(ApiErrors.TelegramCheckUnavailable, r.Failure!.Error.Code);
        Assert.Equal(503, r.Failure.Status);
        Assert.Equal("no_trace", ((ReasonDetails)r.Failure.Error.Details!).Reason);
        Assert.Equal(ApiErrors.TelegramCheckUnavailable, _latch.Info!.Code);
        Assert.Equal(ApiErrors.TelegramCheckUnavailable, Run(Service()).Failure!.Error.Code);
    }

    [Fact]
    public void InitFailure_WithoutAJob_IsNotANoTraceFinding()
    {
        _api.InitOk = false;
        _api.DuringInit = null;

        var r = Run(Service());

        Assert.Equal(ApiErrors.EdiabasUnavailable, r.Failure!.Error.Code);
        Assert.Null(_latch.Info);
    }

    [Fact]
    public void TraceUnreadableAfterTheRequest_IsUnavailable_AndLatches()
    {
        _api.DuringJob = () => _files.Broken.Add(Trace);

        var r = Run(Service());

        Assert.Equal(ApiErrors.TelegramCheckUnavailable, r.Failure!.Error.Code);
        Assert.Equal("unreadable_trace", ((ReasonDetails)r.Failure.Error.Details!).Reason);
        Assert.NotNull(_latch.Info);
    }

    [Fact]
    public void RotatedTrace_IsReadWhole_AndSaysSo()
    {
        // The file is replaced by a shorter one during the request.
        _files.Write(Trace, IfhTraceTests.Fixture());
        _api.DuringInit = () => _files.Write(Trace, Call("OPEN /x", "--"));
        Sends = [("010F", "83 12 F1 14 FF FF")];

        var r = Run(Service());

        Assert.Equal(ApiErrors.TelegramNotAllowed, r.Failure!.Error.Code);
        Assert.Contains("was replaced during the request", r.Failure.Error.Message);
        Assert.Contains(_log.Lines, l => l.Contains("shorter than before the request", StringComparison.Ordinal));
    }

    [Fact]
    public void RotatedTrace_CleanNewFile_Passes()
    {
        _files.Write(Trace, IfhTraceTests.Fixture());
        _api.DuringInit = () => _files.Write(Trace, Call("OPEN /x", "--"));
        Assert.True(Run(Service()).Ok);
    }

    [Fact]
    public void TraceRewrittenInPlace_IsUnavailable_AndLatches()
    {
        // Same length or longer, but the bytes before the request's start changed: the file was
        // replaced or wrapped, so the part read afterwards is not this request's.
        _files.Write(Trace, IfhTraceTests.Fixture());
        var before = _files.Files[Trace].Count;
        _api.DuringInit = () => _files.Write(Trace, new string('x', before) + Call("OPEN /x", "--"));
        Sends = [("010F", "83 12 F1 14 FF FF")];

        var r = Run(Service());

        Assert.Equal(ApiErrors.TelegramCheckUnavailable, r.Failure!.Error.Code);
        Assert.Equal("trace_rewritten", ((ReasonDetails)r.Failure.Error.Details!).Reason);
        Assert.NotNull(_latch.Info);
    }

    [Fact]
    public void ZeroPreface_Passes_ARealPrefaceIsRefused()
    {
        _api.DuringJob = () =>
        {
            _files.Append(Trace, Call("SETECUCOMM", "0F 01 00 00"));
            _files.Append(Trace, Call("SETTELPARAMETER", "00 00 00 00"));
            _files.Append(Trace, Call("SENDECUTEL", "82 12 F1 1A 80"));
        };
        Assert.True(Run(Service()).Ok);

        _api.DuringJob = () =>
        {
            _files.Append(Trace, Call("SETECUCOMM", "0F 01 00 00"));
            _files.Append(Trace, Call("SETTELPARAMETER", "00 2E 00 00"));
            _files.Append(Trace, Call("SENDECUTEL", "82 12 F1 1A 80"));
        };
        var r = Run(Service());
        Assert.Equal(ApiErrors.TelegramNotAllowed, r.Failure!.Error.Code);
        Assert.Equal("telegram_preface", ((TelegramRefusalDetails)r.Failure.Error.Details!).Reason);
        Assert.NotNull(_latch.Info);
    }

    [Fact]
    public void AmbiguousFrame_IsRefused()
    {
        // $22 to BMW-FAST, $2E to KWP2000*.
        Sends = [("010F", "A3 12 F1 22 2E" + string.Concat(Enumerable.Repeat(" 00", 33)))];
        var r = Run(Service());
        Assert.Equal(ApiErrors.TelegramNotAllowed, r.Failure!.Error.Code);
        Assert.Equal("ambiguous", ((TelegramRefusalDetails)r.Failure.Error.Details!).Reason);
    }

    [Fact]
    public void JobThatNeverStops_Latches()
    {
        _api.On("NVE_65 IDENT", new FakeJob { NeverFinishes = true, IgnoresBreak = true });

        var r = Run(Service());

        Assert.False(r.Ok);
        Assert.NotNull(_latch.Info);
        Assert.Equal("job_not_stopped", _latch.Info!.Reason);
    }

    [Fact]
    public void AnyFailureOfTheCheck_Latches()
    {
        // Not an IOException: the check itself fails. The request must not pass unchecked.
        _api.DuringJob = () => _files.Files[Trace] = null!;

        var r = Run(Service());

        Assert.Equal(ApiErrors.TelegramCheckUnavailable, r.Failure!.Error.Code);
        Assert.Equal("check_failed", ((ReasonDetails)r.Failure.Error.Details!).Reason);
        Assert.NotNull(_latch.Info);
    }

    // ---- EDIABAS.INI ----

    [Theory]
    [InlineData("[Configuration]\r\nTracePath = C:\\EDIABAS\\TRACE\r\nIfhTrace = 0\r\n", "IfhTrace")]
    [InlineData("[Configuration]\r\nTracePath = C:\\EDIABAS\\TRACE\r\nIfhTrace = 2\r\n", "IfhTrace")]
    [InlineData("[Configuration]\r\nTracePath = C:\\EDIABAS\\TRACE\r\n", "not set")]
    [InlineData("[Configuration]\r\nTracePath = C:\\EDIABAS\\TRACE\r\nIfhTrace = three\r\n", "three")]
    [InlineData("[Configuration]\r\nIfhTrace = 3\r\n", "TracePath")]
    [InlineData("[Configuration]\r\nTracePath = ..\\TRACE\r\nIfhTrace = 3\r\n", "not a full path")]
    [InlineData("[Other]\r\nTracePath = C:\\EDIABAS\\TRACE\r\nIfhTrace = 3\r\n", "IfhTrace")]
    [InlineData("[Configuration]\r\n;IfhTrace = 3\r\nTracePath = C:\\EDIABAS\\TRACE\r\n", "IfhTrace")]
    public void Enforce_WithoutAUsableTrace_RefusesBeforeApiInit(string ini, string mentions)
    {
        _files.Write(Ini, ini);

        var r = Run(Service());

        Assert.Equal(ApiErrors.TelegramCheckUnavailable, r.Failure!.Error.Code);
        Assert.Contains(mentions, r.Failure.Error.Message);
        Assert.Contains("nothing was sent", r.Failure.Error.Message);
        Assert.Contains("theirs to edit", r.Failure.Error.Message);
        Assert.DoesNotContain(_api.Calls, c => c.StartsWith("Init", StringComparison.Ordinal) || c.StartsWith("Job", StringComparison.Ordinal));
        Assert.Null(_latch.Info);
    }

    [Fact]
    public void Enforce_NoIni_Refuses()
    {
        _files.Files.Remove(Ini);
        var r = Run(Service());
        Assert.Equal(ApiErrors.TelegramCheckUnavailable, r.Failure!.Error.Code);
        Assert.Contains("does not exist", r.Failure.Error.Message);
    }

    [Fact]
    public void Enforce_IniUnreadable_Refuses()
    {
        _files.Broken.Add(Ini);
        Assert.Equal(ApiErrors.TelegramCheckUnavailable, Run(Service()).Failure!.Error.Code);
    }

    [Fact]
    public void Enforce_RuleNotLoaded_RefusesBeforeApiInit()
    {
        var r = Run(Service(rule: TelegramRule.Parse("{}")));

        Assert.Equal(ApiErrors.TelegramCheckUnavailable, r.Failure!.Error.Code);
        Assert.Contains("telegram rule", r.Failure.Error.Message);
        Assert.Equal(0, _api.Count("Init"));
    }

    [Fact]
    public void Enforce_NoTraceFileYet_CountsAsEmpty()
    {
        _files.Files.Remove(Trace);
        Assert.True(Run(Service()).Ok);
    }

    [Theory]
    [InlineData("[configuration]\nifhtrace=4\ntracepath=C:\\EDIABAS\\TRACE\\\n")]
    [InlineData("; comment\r\n[Configuration]\r\nIfhTrace = 3 ; full trace\r\nTracePath = C:/EDIABAS/TRACE\r\nIfhTrace = 0\r\n")]
    public void Ini_Variants_Accepted(string ini)
    {
        _files.Write(Ini, ini);
        Assert.True(Run(Service()).Ok);
    }

    // ---- report and off ----

    [Fact]
    public void Report_RecordsFindings_AndCarriesOn()
    {
        Sends = [("010F", "83 12 F1 14 FF FF")];
        var audit = new RequestAudit();
        var svc = Service(TelegramCheckModes.Report);

        var r = Run(svc, audit);

        Assert.True(r.Ok);
        Assert.Equal(1, audit.TelegramsChecked);
        var f = Assert.Single(audit.TelegramFindings);
        Assert.Equal("service", f.Reason);
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN telegram check (report): telegram_not_allowed service", StringComparison.Ordinal));
        Assert.Null(_latch.Info);
        Assert.True(Run(svc).Ok);
    }

    [Fact]
    public void Report_WithoutIfhTrace_RunsUnchecked()
    {
        _files.Write(Ini, "[Configuration]\r\nIfhTrace = 0\r\n");
        var audit = new RequestAudit();

        var r = Run(Service(TelegramCheckModes.Report), audit);

        Assert.True(r.Ok);
        Assert.Null(audit.TelegramsChecked);
        Assert.Contains(_log.Lines, l => l.Contains("not checking this request", StringComparison.Ordinal));
    }

    [Fact]
    public void Off_ChecksNothing()
    {
        Sends = [("010F", "83 12 F1 14 FF FF")];
        _files.Files.Clear();
        var audit = new RequestAudit();

        Assert.True(Run(Service(TelegramCheckModes.Off), audit).Ok);
        Assert.True(Run(Service(check: false), audit).Ok);
        Assert.Null(audit.TelegramsChecked);
    }

    // ---- interface selection ----

    [Fact]
    public void Interface_Null_UsesApiInit()
    {
        Run(Service());
        Assert.Contains("Init", _api.Calls);
        Assert.DoesNotContain(_api.Calls, c => c.StartsWith("InitExt", StringComparison.Ordinal));
    }

    [Fact]
    public void Interface_Set_UsesApiInitExt()
    {
        Assert.True(Run(Service(ediabasInterface: "GATE:OBD")).Ok);
        Assert.Contains("InitExt GATE:OBD", _api.Calls);
        Assert.DoesNotContain("Init", _api.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("STD:OBD")]
    [InlineData("gate:OBD")]
    public void RequireGate_WithoutAGate_RefusesBeforeAnything(string? ediabasInterface)
    {
        var r = Run(Service(ediabasInterface: ediabasInterface, requireGate: true));

        Assert.Equal(ApiErrors.EdiabasUnavailable, r.Failure!.Error.Code);
        Assert.Contains("requireGate", r.Failure.Error.Message);
        Assert.Empty(_api.Calls);
        Assert.Equal(0, _conflicts.Calls);
    }

    [Fact]
    public void RequireGate_WithAGate_Runs()
    {
        Assert.True(Run(Service(ediabasInterface: "GATE:OBD", requireGate: true)).Ok);
        Assert.Contains("InitExt GATE:OBD", _api.Calls);
    }

    [Fact]
    public void RequireGate_DoesNotStopTheEcuListing()
    {
        Assert.True(Service(requireGate: true).ListEcus(null).Ok);
    }

    // ---- /health ----

    [Fact]
    public void HealthShowsTheLatch()
    {
        Sends = [("010F", "83 12 F1 14 FF FF")];
        Run(Service());

        var json = ApiJson.Serialize(new TelegramCheckInfo(TelegramCheckModes.Enforce, _latch.Info));

        Assert.StartsWith(
            """{"mode":"enforce","latch":{"at":"2026-11-21T17:00:00Z","code":"telegram_not_allowed","reason":"service","function":"SENDECUTEL","concept":"010F","frame":"83 12 F1 14 FF FF","message":"EDIABAS sent 83 12 F1 14 FF FF under concept 010F""",
            json);
    }

    [Fact]
    public void Latch_KeepsTheFirstFinding()
    {
        var latch = new TelegramLatch();
        latch.Set(new TelegramLatchInfo("a", "telegram_not_allowed", "service", null, null, null, "first"));
        latch.Set(new TelegramLatchInfo("b", "telegram_not_allowed", "malformed", null, null, null, "second"));
        Assert.Equal("first", latch.Info!.Message);
    }
}
