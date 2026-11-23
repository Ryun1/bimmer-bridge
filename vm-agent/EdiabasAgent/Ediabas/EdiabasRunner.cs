using System.Collections.Concurrent;
using System.Diagnostics;
using EdiabasAgent.Core;
using EdiabasAgent.Logging;

namespace EdiabasAgent.Ediabas;

/// <summary>
/// One dedicated thread owns api32.dll and every EDIABAS call. Requests are queued and run one
/// at a time; handlers await the result. A whole request (init … end) always runs to its end on
/// this thread, whatever happens to the HTTP client: a client that goes away only cancels the
/// request's token, which makes the service stop starting jobs, break a running one, and still
/// call apiEnd. A request still queued when its client has gone is dropped without running.
/// <para>The time an item waited in the queue is handed to the work, because the client's
/// deadline started when it sent the request, not when this thread got to it.</para>
/// <para>Between requests the thread waits only as long as the service says, then lets the
/// service release api32.dll if it has been idle long enough; so the unload happens on this
/// thread and never during a request.</para>
/// <para>A handler waits for its answer at most <c>handlerWait</c>, and never after its client has
/// gone: a native call that never returns must not also hold every HTTP connection that queues
/// behind it (issue #15). <see cref="BusySinceMs"/> lets the watchdog see such a call.</para>
/// </summary>
internal sealed class EdiabasRunner : IDisposable
{
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(10);

    private readonly BlockingCollection<Action<EdiabasService?>> _queue = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Func<EdiabasService> _createService;
    private readonly AgentLog _log;
    private readonly Thread _thread;
    private readonly TimeSpan _handlerWait;

    // Environment.TickCount64 when the current piece of work started; -1 while idle.
    private long _busySince = -1;

    /// <param name="handlerWait">How long a handler waits for its answer: longer than any request
    /// can take, so that only a stuck runner reaches it.</param>
    public EdiabasRunner(Func<EdiabasService> createService, AgentLog log, TimeSpan handlerWait)
    {
        _createService = createService;
        _log = log;
        _handlerWait = handlerWait;

        // Background so a call hung inside the DLL cannot keep the process alive for ever.
        _thread = new Thread(Loop) { IsBackground = true, Name = "EDIABAS" };
        _thread.Start();
    }

    /// <summary><see cref="Environment.TickCount64"/> when the work now on the runner thread
    /// started (a request, loading the service, or an idle release); null while it waits.</summary>
    public long? BusySinceMs
    {
        get
        {
            var since = Interlocked.Read(ref _busySince);
            return since < 0 ? null : since;
        }
    }

    /// <param name="work">Gets the service, how long the item waited in the queue in
    /// milliseconds, and the request's token.</param>
    /// <returns>The work's outcome; <c>ediabas_unavailable</c> if the runner has not answered
    /// within the handler wait. Cancelled when the client goes away first.</returns>
    public async Task<Outcome<T>> Run<T>(Func<EdiabasService, long, CancellationToken, Outcome<T>> work, CancellationToken requestAborted)
        where T : class
    {
        var tcs = new TaskCompletionSource<Outcome<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedAt = Stopwatch.GetTimestamp();

        // Cancelled when the client goes away or the handler stops waiting: a queued item is then
        // dropped, and a running one stops starting jobs and breaks the one it runs.
        using var abandon = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        var abandoned = abandon.Token;
        void Item(EdiabasService? service)
        {
            if (service is null)
            {
                tcs.TrySetException(new InvalidOperationException("the EDIABAS runner could not start; see agent.log"));
                return;
            }

            CancellationTokenSource linked;
            try
            {
                if (abandoned.IsCancellationRequested || _stopping.IsCancellationRequested)
                {
                    tcs.TrySetCanceled();
                    return;
                }

                linked = CancellationTokenSource.CreateLinkedTokenSource(abandoned, _stopping.Token);
            }
            catch (ObjectDisposedException)
            {
                // The handler has already answered and gone.
                tcs.TrySetCanceled();
                return;
            }

            using var _ = linked;
            try
            {
                var queuedMs = (long)Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
                tcs.TrySetResult(work(service, queuedMs, linked.Token));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        if (!_queue.TryAdd(Item))
        {
            tcs.TrySetException(new InvalidOperationException("the EDIABAS runner is stopping"));
        }

        try
        {
            return await tcs.Task.WaitAsync(_handlerWait, requestAborted);
        }
        catch (TimeoutException)
        {
            abandon.Cancel();
            _log.Error($"EDIABAS runner did not answer a request within {_handlerWait.TotalSeconds:0} s; answering ediabas_unavailable");
            return ApiFailure.Of(
                ApiErrors.EdiabasUnavailable,
                $"The agent's EDIABAS thread did not answer within {_handlerWait.TotalSeconds:0} s. If it stays stuck, the agent restarts itself within a few minutes.",
                new ReasonDetails("the EDIABAS thread did not answer in time"));
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            abandon.Cancel();
            throw;
        }
    }

    private void Loop()
    {
        EdiabasService service;
        try
        {
            // Created here so the API object belongs to this thread from the start.
            MarkBusy();
            service = _createService();
        }
        catch (Exception ex)
        {
            _log.Error("EDIABAS runner could not start; every EDIABAS request will fail", ex);
            MarkIdle();
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                item(null);
            }

            return;
        }

        MarkIdle();

        while (true)
        {
            Action<EdiabasService?>? item;
            try
            {
                if (!_queue.TryTake(out item, WaitMs(service)))
                {
                    if (_queue.IsCompleted)
                    {
                        return;
                    }

                    // Waited the idle time with nothing to do.
                    MarkBusy();
                    service.ReleaseIfIdle();
                    continue;
                }
            }
            catch (Exception ex)
            {
                // ReleaseIfIdle already logs its own failures; this keeps the thread alive.
                _log.Error("EDIABAS runner idle release failed", ex);
                continue;
            }
            finally
            {
                MarkIdle();
            }

            try
            {
                MarkBusy();
                item(service);
            }
            catch (Exception ex)
            {
                // Item() already catches; this is only a last line so the thread never dies.
                _log.Error("EDIABAS runner item failed", ex);
            }
            finally
            {
                MarkIdle();
            }
        }
    }

    private void MarkBusy() => Interlocked.Exchange(ref _busySince, Environment.TickCount64);

    private void MarkIdle() => Interlocked.Exchange(ref _busySince, -1);

    private int WaitMs(EdiabasService service)
    {
        try
        {
            return service.MillisecondsUntilRelease();
        }
        catch (Exception ex)
        {
            _log.Error("EDIABAS runner could not work out the idle release time", ex);
            return Timeout.Infinite;
        }
    }

    /// <summary>Stops taking work, cancels the running request (which still calls apiEnd), and waits briefly.</summary>
    public void Dispose()
    {
        _stopping.Cancel();
        _queue.CompleteAdding();
        if (!_thread.Join(JoinTimeout))
        {
            _log.Warn($"EDIABAS runner did not finish within {JoinTimeout.TotalSeconds:0} s of shutdown");
        }
    }
}
