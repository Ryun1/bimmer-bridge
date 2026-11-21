using EdiabasAgent.Core;
using Microsoft.AspNetCore.Http;

namespace EdiabasAgent;

/// <summary>
/// <c>GET /logs</c> and <c>GET /logs/read</c> (docs/api.md "Logs"). Parse, call
/// <see cref="LogService"/> (Core), answer. Read-only. The audit line of a read carries the path
/// asked for and, when it was refused, the reason as its rule.
/// </summary>
internal sealed class LogEndpoints
{
    private readonly LogService _logs;

    public LogEndpoints(LogService logs)
    {
        _logs = logs;
    }

    public async Task List(HttpContext http)
    {
        var audit = HandlerSupport.Audit(http);
        var q = HandlerSupport.Query(http, "root", "glob", "since", "limit");
        if (!q.Ok)
        {
            await HandlerSupport.Fail(http, audit, q.Failure!);
            return;
        }

        var query = LogService.ParseList(q.Value!, _logs.RootCount);
        if (!query.Ok)
        {
            await HandlerSupport.Fail(http, audit, query.Failure!);
            return;
        }

        await Responses.WriteJson(http, StatusCodes.Status200OK, _logs.List(query.Value!));
    }

    public async Task Read(HttpContext http)
    {
        var audit = HandlerSupport.Audit(http);
        var q = HandlerSupport.Query(http, "path", "tailBytes", "offset", "length", "grep");
        if (!q.Ok)
        {
            await HandlerSupport.Fail(http, audit, q.Failure!);
            return;
        }

        HandlerSupport.Target(http, q.Value!.Get("path"));
        var query = LogService.ParseRead(q.Value!);
        if (!query.Ok)
        {
            await HandlerSupport.Fail(http, audit, query.Failure!);
            return;
        }

        var outcome = _logs.Read(query.Value!, out var refusal);
        audit.Rule = refusal;
        if (!outcome.Ok)
        {
            await HandlerSupport.Fail(http, audit, outcome.Failure!);
            return;
        }

        await Responses.WriteJson(http, StatusCodes.Status200OK, outcome.Value!);
    }
}
