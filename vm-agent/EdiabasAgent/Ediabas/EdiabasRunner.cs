using System.Collections.Concurrent;
using EdiabasAgent.Core;
using EdiabasAgent.Logging;

namespace EdiabasAgent.Ediabas;

/// <summary>
/// One dedicated thread owns api32.dll and every EDIABAS call. Requests are queued and run one
/// at a time; handlers await the result. A whole request (init … end) always runs to its end on
/// this thread, whatever happens to the HTTP client: a client that goes away only cancels the
/// request's token, which makes the service stop starting jobs, break a running one, and still
/// call apiEnd. A request still queued when its client has gone is dropped without running.
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

    public Task<T> Run<T>(Func<EdiabasService, CancellationToken, T> work, CancellationToken requestAborted)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                tcs.TrySetResult(work(service, linked.Token));
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

        foreach (var item in _queue.GetConsumingEnumerable())
        {
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
