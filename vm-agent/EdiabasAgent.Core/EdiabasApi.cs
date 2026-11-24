namespace EdiabasAgent.Core;

/// <summary>
/// The minimal seam over api32.dll: only what read jobs need. The host implements it with
/// P/Invoke (Ediabas/Api32.cs); tests use a scripted fake. Every call is made from one thread.
/// There is deliberately no way here to set configuration, switch devices, send raw data or
/// install callbacks.
/// </summary>
public interface IEdiabasApi
{
    /// <summary>Loads the DLL and resolves every export, without calling any of them.
    /// False, with a reason, when the DLL or an export is missing.</summary>
    bool TryLoad(out string? reason);

    /// <summary>Drops every function resolved from the DLL, then unloads it, so that EDIABAS's
    /// ebas32.exe can exit. Only called between requests, after <c>apiEnd</c>. Afterwards
    /// <see cref="TryLoad"/> loads and resolves it again. Throws if something went wrong; the
    /// caller logs it and loads afresh on the next request.</summary>
    void Unload();

    /// <summary><c>apiInit</c> when <paramref name="ediabasInterface"/> is null (EDIABAS.INI's
    /// interface); else <c>apiInitExt(ediabasInterface, "", "", "")</c>, such as <c>GATE:OBD</c>.</summary>
    bool Init(string? ediabasInterface);

    /// <summary><c>apiJob(ecu, job, para, result)</c>. Returns at once; poll <see cref="State"/>.</summary>
    void Job(string ecu, string job, string parameters, string results);

    /// <summary><c>apiState</c>: see <see cref="ApiState"/>.</summary>
    int State();

    /// <summary><c>apiBreak</c>.</summary>
    void Break();

    /// <summary><c>apiEnd</c>.</summary>
    void End();

    int ErrorCode();

    string ErrorText();

    /// <summary>Number of job result sets, not counting set 0.</summary>
    bool ResultSets(out int sets);

    bool ResultNumber(int set, out int count);

    /// <summary>Name of result <paramref name="index"/> (1-based) in <paramref name="set"/>.</summary>
    bool ResultName(int index, int set, out string name);

    bool ResultFormat(string name, int set, out int format);

    bool ResultChar(string name, int set, out sbyte value);

    bool ResultByte(string name, int set, out byte value);

    bool ResultInt(string name, int set, out short value);

    bool ResultWord(string name, int set, out ushort value);

    bool ResultLong(string name, int set, out int value);

    bool ResultDWord(string name, int set, out uint value);

    bool ResultReal(string name, int set, out double value);

    bool ResultText(string name, int set, out string value);

    bool ResultBinary(string name, int set, out byte[] value);
}

/// <summary><c>APIBUSY</c> … <c>APIERROR</c> from api.h.</summary>
public static class ApiState
{
    public const int Busy = 0;
    public const int Ready = 1;
    public const int Break = 2;
    public const int Error = 3;
}

/// <summary><c>APIRESULTFORMAT</c> from api.h.</summary>
public static class ApiResultFormat
{
    public const int Char = 0;
    public const int Byte = 1;
    public const int Integer = 2;
    public const int Word = 3;
    public const int Long = 4;
    public const int DWord = 5;
    public const int Text = 6;
    public const int Binary = 7;
    public const int Real = 8;
}

/// <summary>Processes that hold the cable right now. Empty means EDIABAS may be used.</summary>
public interface IConflictCheck
{
    IReadOnlyList<ConflictingProcess> Running();
}

/// <summary>Time for the runner, so polling and timeouts can be tested without waiting.</summary>
public interface IRunnerClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Monotonic milliseconds.</summary>
    long ElapsedMs { get; }

    void Sleep(int milliseconds);
}

public sealed class SystemRunnerClock : IRunnerClock
{
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public long ElapsedMs => _sw.ElapsedMilliseconds;

    public void Sleep(int milliseconds) => Thread.Sleep(milliseconds);
}

/// <summary>Where the runner reports what it sees (the agent log in the host).</summary>
public interface IEdiabasLog
{
    void Info(string message);

    void Warn(string message);
}

/// <summary>Whether api32.dll is loaded right now, and whether EDIABAS is stuck, for /health. Thread-safe.</summary>
public sealed class DllLoadState
{
    private volatile bool _loaded;
    private volatile bool _stuck;
    private volatile bool _heldOpen;

    public bool Loaded
    {
        get => _loaded;
        set => _loaded = value;
    }

    /// <summary>A job was still busy after <c>apiBreak</c> and its grace time: EDIABAS is not
    /// used again (no <c>apiEnd</c>, no unload, no new job) until the agent restarts.</summary>
    public bool Stuck
    {
        get => _stuck;
        set => _stuck = value;
    }

    /// <summary><c>apiEnd</c> threw: a handle may still be open, so EDIABAS stays loaded (and the
    /// interface in use) until the agent restarts.</summary>
    public bool HeldOpen
    {
        get => _heldOpen;
        set => _heldOpen = value;
    }
}

/// <summary>When the runner last called <c>apiInit</c>, for /health. Thread-safe.</summary>
public sealed class CableUse
{
    private long _ticks;

    public DateTimeOffset? LastInitAt
    {
        get
        {
            var t = Interlocked.Read(ref _ticks);
            return t == 0 ? null : new DateTimeOffset(t, TimeSpan.Zero);
        }
    }

    public void Mark(DateTimeOffset when) => Interlocked.Exchange(ref _ticks, when.UtcTicks);
}
