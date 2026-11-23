using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EdiabasAgent.Core;
using EdiabasAgent.Logging;

namespace EdiabasAgent;

/// <summary>
/// Ends the agent's process when it is alive but no longer useful, so the scheduled task (its
/// every-5-minutes trigger) starts a fresh one (issue #15). Runs on its own thread, never the
/// thread pool, which may be the thing that is starved. The decisions are in
/// <see cref="WatchdogState"/>; this class only supplies the clock, the probe and the exit.
/// <para>The probe connects to the agent's own address and sends a line Kestrel cannot parse.
/// Kestrel answers 400 itself, before the request pipeline, so the probe writes no audit line
/// and reaches no handler.</para>
/// </summary>
internal sealed class AgentWatchdog : IDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly byte[] ProbeRequest = Encoding.ASCII.GetBytes("WATCHDOG\r\n\r\n");

    private readonly AgentContext _ctx;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;

    public AgentWatchdog(AgentContext ctx)
    {
        _ctx = ctx;
        _thread = new Thread(Loop) { IsBackground = true, Name = "watchdog" };
    }

    private AgentLog Log => _ctx.Log;

    public void Start() => _thread.Start();

    private void Loop()
    {
        var state = new WatchdogState(WatchdogState.RunnerStallMsFor(_ctx.Config.MaxJobTimeoutSeconds), Environment.TickCount64);
        while (!_stop.Token.WaitHandle.WaitOne(WatchdogState.TickMs))
        {
            try
            {
                var listening = _ctx.Listening;
                var check = state.Tick(Environment.TickCount64, _ctx.Runner.BusySinceMs, listening is not null, _ctx.NativeCalls.Describe);
                if (check.Note is not null)
                {
                    Log.Info(check.Note);
                }

                if (check.ExitReason is not null)
                {
                    Exit(check.ExitReason);
                    return;
                }

                if (!check.ProbeDue || listening is null)
                {
                    continue;
                }

                var why = Probe(listening);
                if (why is not null)
                {
                    Log.Warn($"watchdog: the listener on {listening} did not answer a check ({why}); miss {state.ProbeMisses + 1} of {WatchdogState.ProbeMissesToExit}");
                }

                var exit = state.ProbeResult(why is null, why);
                if (exit is not null)
                {
                    Exit(exit);
                    return;
                }
            }
            catch (Exception ex)
            {
                // The watchdog must outlive its own faults.
                Log.Error("watchdog check failed", ex);
            }
        }
    }

    /// <summary>Null when the listener answered with an HTTP status line, else why not.</summary>
    private static string? Probe(IPEndPoint endpoint)
    {
        try
        {
            using var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.SendTimeout = (int)ProbeTimeout.TotalMilliseconds;
            socket.ReceiveTimeout = (int)ProbeTimeout.TotalMilliseconds;

            // Synchronous calls with timeouts: nothing here needs the thread pool.
            var connect = socket.BeginConnect(endpoint, null, null);
            if (!connect.AsyncWaitHandle.WaitOne(ProbeTimeout))
            {
                return $"no connection within {ProbeTimeout.TotalSeconds:0} s";
            }

            socket.EndConnect(connect);
            socket.Send(ProbeRequest);
            var buffer = new byte[16];
            var read = 0;
            while (read < 9)
            {
                var n = socket.Receive(buffer, read, buffer.Length - read, SocketFlags.None);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            return Encoding.ASCII.GetString(buffer, 0, read).StartsWith("HTTP/1.1 ", StringComparison.Ordinal)
                ? null
                : read == 0 ? "the connection closed without an answer" : "the answer was not HTTP";
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode == SocketError.TimedOut
                ? $"no answer within {ProbeTimeout.TotalSeconds:0} s"
                : $"socket error {ex.SocketErrorCode}";
        }
        catch (ObjectDisposedException)
        {
            return "the socket closed";
        }
    }

    private void Exit(string reason)
    {
        Log.Error($"watchdog: {reason}. Ending the agent process so the scheduled task starts a fresh one (within 5 minutes).");

        // Not Environment.Exit: an orderly shutdown waits for the host and the threads that are
        // stuck, which is the problem. Every log line is already on disk (each write closes the file).
        using var self = Process.GetCurrentProcess();
        self.Kill();
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(2)))
        {
            Log.Warn("watchdog thread did not stop within 2 s");
        }

        _stop.Dispose();
    }
}
