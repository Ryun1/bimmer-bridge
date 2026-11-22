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
/// </summary>
internal sealed class EdiabasRunner : IDisposable
{
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(10);

    private readonly BlockingCollection<Action<EdiabasService?>> _queue = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Func<EdiabasService> _createService;
    private readonly AgentLog _log;
    private readonly Thread _thread;

    public EdiabasRunner(Func<EdiabasService> createService, AgentLog log)
    {
        _createService = createService;
        _log = log;

        // Background so a call hung inside the DLL cannot keep the process alive for ever.
        _thread = new Thread(Loop) { IsBackground = true, Name = "EDIABAS" };
        _thread.Start();
    }

    /// <param name="work">Gets the service, how long the item waited in the queue in
    /// milliseconds, and the request's token.</param>
    public Task<T> Run<T>(Func<EdiabasService, long, CancellationToken, T> work, CancellationToken requestAborted)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedAt = Stopwatch.GetTimestamp();
        void Item(EdiabasService? service)
        {
            if (service is null)
            {
                tcs.TrySetException(new InvalidOperationException("the EDIABAS runner could not start; see agent.log"));
                return;
            }

            if (requestAborted.IsCancellationRequested || _stopping.IsCancellationRequested)
            {
                tcs.TrySetCanceled();
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, _stopping.Token);
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

        return tcs.Task;
    }

    private void Loop()
    {
        EdiabasService service;
        try
        {
            // Created here so the API object belongs to this thread from the start.
            service = _createService();
        }
        catch (Exception ex)
        {
            _log.Error("EDIABAS runner could not start; every EDIABAS request will fail", ex);
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                item(null);
            }

            return;
        }

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

            try
            {
                item(service);
            }
            catch (Exception ex)
            {
                // Item() already catches; this is only a last line so the thread never dies.
                _log.Error("EDIABAS runner item failed", ex);
            }
        }
    }

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
