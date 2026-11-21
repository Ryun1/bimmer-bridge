using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

/// <summary>One scripted result: name, api.h format, value (null = the read call fails).</summary>
public sealed record FakeResult(string Name, int Format, object? Value);

/// <summary>How the fake answers one apiJob call.</summary>
public sealed class FakeJob
{
    /// <summary>apiState answers busy this many times before the final state.</summary>
    public int BusyPolls { get; init; }

    /// <summary>Busy for ever (until apiBreak, unless <see cref="IgnoresBreak"/>).</summary>
    public bool NeverFinishes { get; init; }

    public bool IgnoresBreak { get; init; }

    public int FinalState { get; init; } = ApiState.Ready;

    public int ErrorCode { get; init; }

    public string ErrorText { get; init; } = "";

    /// <summary>Set 0 first, then the job sets.</summary>
    public List<List<FakeResult>> Sets { get; init; } = [[]];

    public bool Throws { get; init; }

    public static FakeJob Ok(params List<FakeResult>[] jobSets) =>
        new() { Sets = [[new FakeResult("VARIANTE", ApiResultFormat.Text, "VAR")], .. jobSets] };

    public static FakeJob Comments(params string[] lines) =>
        new()
        {
            Sets =
            [
                [],
                lines.Select((l, i) => new FakeResult("JOBCOMMENT" + i, ApiResultFormat.Text, l)).ToList(),
            ],
        };

    public static FakeJob Jobs(params string[] names) =>
        new()
        {
            Sets =
            [
                [new FakeResult("VARIANTE", ApiResultFormat.Text, "VAR")],
                .. names.Select(n => new List<FakeResult> { new("JOBNAME", ApiResultFormat.Text, n) }),
            ],
        };

    public static FakeJob Error(int code, string text) =>
        new() { FinalState = ApiState.Error, ErrorCode = code, ErrorText = text };
}

/// <summary>
/// Scripted <see cref="IEdiabasApi"/>. Jobs are looked up by "ECU JOB ARGS" then "ECU JOB";
/// an unscripted job fails the test. Every call is recorded in <see cref="Calls"/>.
/// </summary>
public sealed class FakeEdiabasApi : IEdiabasApi
{
    private readonly Dictionary<string, FakeJob> _jobs = new(StringComparer.Ordinal);
    private FakeJob? _current;
    private int _pollsLeft;
    private bool _broken;
    private int _lastErrorCode;
    private string _lastErrorText = "";

    public List<string> Calls { get; } = [];

    public bool LoadOk { get; set; } = true;

    public bool InitOk { get; set; } = true;

    public bool UnloadThrows { get; set; }

    /// <summary>Run inside every apiJob call, to act while a request is in flight.</summary>
    public Action? DuringJob { get; set; }

    /// <summary>apiEnd throws (a native failure while closing the handle).</summary>
    public bool EndThrows { get; set; }

    public FakeEdiabasApi On(string key, FakeJob job)
    {
        _jobs[key] = job;
        return this;
    }

    public int Count(string prefix) => Calls.Count(c => c == prefix || c.StartsWith(prefix + " ", StringComparison.Ordinal));

    public IEnumerable<string> JobCalls => Calls.Where(c => c.StartsWith("Job ", StringComparison.Ordinal));

    public bool TryLoad(out string? reason)
    {
        Calls.Add("TryLoad");
        reason = LoadOk ? null : "api32.dll not found";
        return LoadOk;
    }

    public bool Init()
    {
        Calls.Add("Init");
        if (!InitOk)
        {
            _lastErrorCode = 95;
            _lastErrorText = "SYS-0005: init failed";
        }

        return InitOk;
    }

    public void Job(string ecu, string job, string parameters, string results)
    {
        Calls.Add($"Job {ecu} {job} [{parameters}] [{results}]");
        DuringJob?.Invoke();
        if (!_jobs.TryGetValue($"{ecu} {job} {parameters}", out var j) && !_jobs.TryGetValue($"{ecu} {job}", out j))
        {
            throw new Xunit.Sdk.XunitException($"unscripted job {ecu} {job} [{parameters}]");
        }

        if (j.Throws)
        {
            throw new InvalidOperationException("native failure");
        }

        _current = j;
        _pollsLeft = j.BusyPolls;
        _broken = false;
        _lastErrorCode = j.ErrorCode;
        _lastErrorText = j.ErrorText;
    }

    public int State()
    {
        Calls.Add("State");
        if (_current is null)
        {
            return ApiState.Error;
        }

        if (_current.NeverFinishes)
        {
            return _broken && !_current.IgnoresBreak ? ApiState.Break : ApiState.Busy;
        }

        if (_pollsLeft > 0)
        {
            _pollsLeft--;
            return ApiState.Busy;
        }

        return _current.FinalState;
    }

    public void Break()
    {
        Calls.Add("Break");
        _broken = true;
    }

    public void End()
    {
        Calls.Add("End");
        if (EndThrows)
        {
            throw new InvalidOperationException("native failure in apiEnd");
        }
    }

    public void Unload()
    {
        Calls.Add("Unload");
        if (UnloadThrows)
        {
            throw new InvalidOperationException("FreeLibrary failed");
        }
    }

    public int ErrorCode()
    {
        Calls.Add("ErrorCode");
        return _lastErrorCode;
    }

    public string ErrorText()
    {
        Calls.Add("ErrorText");
        return _lastErrorText;
    }

    public bool ResultSets(out int sets)
    {
        sets = (_current?.Sets.Count ?? 1) - 1;
        return _current is not null;
    }

    public bool ResultNumber(int set, out int count)
    {
        count = 0;
        if (_current is null || set >= _current.Sets.Count)
        {
            return false;
        }

        count = _current.Sets[set].Count;
        return true;
    }

    public bool ResultName(int index, int set, out string name)
    {
        name = _current!.Sets[set][index - 1].Name;
        return true;
    }

    public bool ResultFormat(string name, int set, out int format)
    {
        var r = Find(name, set);
        format = r?.Format ?? -1;
        return r is not null;
    }

    public bool ResultChar(string name, int set, out sbyte value) => Get(name, set, out value);

    public bool ResultByte(string name, int set, out byte value) => Get(name, set, out value);

    public bool ResultInt(string name, int set, out short value) => Get(name, set, out value);

    public bool ResultWord(string name, int set, out ushort value) => Get(name, set, out value);

    public bool ResultLong(string name, int set, out int value) => Get(name, set, out value);

    public bool ResultDWord(string name, int set, out uint value) => Get(name, set, out value);

    public bool ResultReal(string name, int set, out double value) => Get(name, set, out value);

    public bool ResultText(string name, int set, out string value)
    {
        var ok = Get<string>(name, set, out var v);
        value = v ?? "";
        return ok;
    }

    public bool ResultBinary(string name, int set, out byte[] value)
    {
        var ok = Get<byte[]>(name, set, out var v);
        value = v ?? [];
        return ok;
    }

    private FakeResult? Find(string name, int set) =>
        _current?.Sets.ElementAtOrDefault(set)?.FirstOrDefault(r => r.Name == name);

    private bool Get<T>(string name, int set, out T value)
    {
        var r = Find(name, set);
        if (r?.Value is T t)
        {
            value = t;
            return true;
        }

        value = default!;
        return false;
    }
}

public sealed class FakeClock : IRunnerClock
{
    public DateTimeOffset Start { get; } = new(2026, 11, 21, 17, 0, 0, TimeSpan.Zero);

    public long ElapsedMs { get; set; }

    public DateTimeOffset UtcNow => Start.AddMilliseconds(ElapsedMs);

    public int Sleeps { get; private set; }

    public void Sleep(int milliseconds)
    {
        Sleeps++;
        ElapsedMs += milliseconds;
    }
}

public sealed class FakeConflicts : IConflictCheck
{
    /// <summary>Answers in turn; the last answer repeats.</summary>
    public List<IReadOnlyList<ConflictingProcess>> Answers { get; } = [[]];

    public int Calls { get; private set; }

    public IReadOnlyList<ConflictingProcess> Running()
    {
        var a = Answers[Math.Min(Calls, Answers.Count - 1)];
        Calls++;
        return a;
    }
}

public sealed class FakeEcus(params string[] present) : IEcuCatalog
{
    public bool Exists(string ecu) => present.Contains(ecu, StringComparer.OrdinalIgnoreCase);

    public EcusResponse? List(string? filter, int max) =>
        new("X", present.Length, false, present.Select(p => new EcuFile(p, "prg")).ToList());
}

public sealed class FakeLog : IEdiabasLog
{
    public List<string> Lines { get; } = [];

    public void Info(string message) => Lines.Add("INFO " + message);

    public void Warn(string message) => Lines.Add("WARN " + message);
}
