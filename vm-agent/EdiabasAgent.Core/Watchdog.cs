namespace EdiabasAgent.Core;

/// <summary>
/// Which native EDIABAS call the runner thread is in, or last made, and since when (monotonic
/// milliseconds). Written by the runner thread around every api32.dll call and LoadLibrary /
/// FreeLibrary; read by the watchdog to say where a stalled runner is stuck. Thread-safe.
/// </summary>
public sealed class NativeCallTracker
{
    private readonly object _gate = new();
    private readonly Func<long> _nowMs;
    private string? _name;
    private long _sinceMs;
    private bool _inside;

    public NativeCallTracker(Func<long>? nowMs = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _nowMs = nowMs ?? (() => sw.ElapsedMilliseconds);
    }

    public void Enter(string name)
    {
        lock (_gate)
        {
            _name = name;
            _sinceMs = _nowMs();
            _inside = true;
        }
    }

    public void Exit()
    {
        lock (_gate)
        {
            _inside = false;
            _sinceMs = _nowMs();
        }
    }

    /// <summary>For the log, e.g. "inside apiState for 183 s" or "last call apiEnd, returned 12 s ago".</summary>
    public string Describe()
    {
        lock (_gate)
        {
            if (_name is null)
            {
                return "no api32 call made yet";
            }

            var seconds = (_nowMs() - _sinceMs) / 1000;
            return _inside
                ? $"inside {_name} for {seconds} s"
                : $"last api32 call {_name}, returned {seconds} s ago";
        }
    }
}

/// <summary>What one watchdog check found.</summary>
/// <param name="ExitReason">Non-null: the agent should log this and end its process, so the
/// scheduled task starts a fresh one.</param>
/// <param name="ProbeDue">Time to check the listener answers.</param>
/// <param name="Note">Something to log that needs no action.</param>
public sealed record WatchdogCheck(string? ExitReason, bool ProbeDue, string? Note);

/// <summary>
/// The decisions of the agent's watchdog (issue #15), apart from the thread, the clock and the
/// socket, so they can be tested. The host calls <see cref="Tick"/> every few seconds from a
/// dedicated thread (never the thread pool, which may be what is starved) and reports each
/// listener probe to <see cref="ProbeResult"/>.
/// <list type="bullet">
/// <item>EDIABAS runner stalled: one request has held the runner thread for longer than any
/// request can (its budget, the apiBreak grace and apiEnd), so it is stuck inside a native call
/// that will not return. Nothing but a new process gets EDIABAS back.</item>
/// <item>Listener stalled: the agent sends itself a request it cannot parse; Kestrel answers it
/// with 400 before the request pipeline (so there is no audit line). Several misses in a row mean
/// the agent no longer accepts connections, though its process is alive.</item>
/// <item>A long gap between two checks means the VM was paused or the Mac slept: every clock
/// starts again, so a request that was running across the pause is not taken for a stall.</item>
/// </list>
/// </summary>
public sealed class WatchdogState
{
    public const int TickMs = 5_000;
    public const int ProbeEveryMs = 60_000;
    public const int ProbeMissesToExit = 3;

    /// <summary>A gap between ticks this long means the process was not running (VM paused).</summary>
    public const int PauseMs = 30_000;

    private readonly long _runnerStallMs;
    private long? _lastTickMs;
    private long _clocksFromMs;
    private long _lastProbeMs;
    private int _probeMisses;

    /// <param name="runnerStallMs">How long one request may hold the runner thread.</param>
    /// <param name="startMs">Now, on the same clock as <see cref="Tick"/>.</param>
    public WatchdogState(long runnerStallMs, long startMs)
    {
        _runnerStallMs = runnerStallMs;
        _clocksFromMs = startMs;
        _lastProbeMs = startMs;
    }

    /// <summary>The runner stall limit for a maximum request time: the request's budget, the
    /// apiBreak grace, and generous room for apiEnd and loading the DLL.</summary>
    public static long RunnerStallMsFor(int maxJobTimeoutSeconds) =>
        (Math.Max(1, maxJobTimeoutSeconds) * 1000L) + EdiabasService.BreakGraceMs + 120_000;

    public int ProbeMisses => _probeMisses;

    /// <param name="nowMs">Monotonic milliseconds.</param>
    /// <param name="runnerBusySinceMs">When the request now on the runner thread started, on the
    /// same clock; null while it is idle.</param>
    /// <param name="listening">Whether the agent is listening now (false while it binds).</param>
    /// <param name="nativeCall">Where the runner is, for the exit reason.</param>
    public WatchdogCheck Tick(long nowMs, long? runnerBusySinceMs, bool listening, Func<string> nativeCall)
    {
        string? note = null;
        if (_lastTickMs is { } last && nowMs - last >= PauseMs)
        {
            note = $"watchdog: {(nowMs - last) / 1000} s passed between two checks (VM paused or the Mac asleep?); its clocks start again";
            _clocksFromMs = nowMs;
            _lastProbeMs = nowMs;
            _probeMisses = 0;
        }

        _lastTickMs = nowMs;

        if (runnerBusySinceMs is { } since)
        {
            var busyMs = nowMs - Math.Max(since, _clocksFromMs);
            if (busyMs >= _runnerStallMs)
            {
                return new WatchdogCheck(
                    $"the EDIABAS thread has run one request for {busyMs / 1000} s, longer than any request can ({_runnerStallMs / 1000} s); {nativeCall()}",
                    false,
                    note);
            }
        }

        if (!listening)
        {
            // Binding or rebinding: nothing to probe, and misses from before do not count.
            _probeMisses = 0;
            _lastProbeMs = nowMs;
            return new WatchdogCheck(null, false, note);
        }

        var due = nowMs - _lastProbeMs >= ProbeEveryMs;
        if (due)
        {
            _lastProbeMs = nowMs;
        }

        return new WatchdogCheck(null, due, note);
    }

    /// <summary>Records one probe. Returns the exit reason once enough probes in a row missed.</summary>
    public string? ProbeResult(bool answered, string? why)
    {
        if (answered)
        {
            _probeMisses = 0;
            return null;
        }

        _probeMisses++;
        return _probeMisses >= ProbeMissesToExit
            ? $"the listener did not answer {_probeMisses} checks in a row, {ProbeEveryMs / 1000} s apart (last: {why ?? "no answer"})"
            : null;
    }
}
