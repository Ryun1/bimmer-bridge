using EdiabasAgent.Core;
using Microsoft.AspNetCore.Http;

namespace EdiabasAgent;

/// <summary>
/// Handlers for the EDIABAS endpoints (docs/api.md "EDIABAS endpoints"). They only parse, hand
/// the work to the EDIABAS runner thread, and write the answer and audit lines; every decision
/// is made in <see cref="EdiabasService"/> (Core).
/// </summary>
internal sealed class EdiabasEndpoints
{
    /// <summary>Key in <see cref="HttpContext.Items"/> under which a handler leaves its <see cref="RequestAudit"/>.</summary>
    public const string AuditKey = "ediabas-bridge.audit";

    private readonly AgentContext _ctx;

    public EdiabasEndpoints(AgentContext ctx)
    {
        _ctx = ctx;
    }

    public async Task Ecus(HttpContext http)
    {
        var audit = Audit(http);
        var q = Query(http, "filter");
        if (!q.Ok)
        {
            await Fail(http, audit, q.Failure!);
            return;
        }

        var filter = q.Value!.Get("filter");
        if (RequestParsing.CheckFilter(filter) is { } bad)
        {
            await Fail(http, audit, bad);
            return;
        }

        var outcome = await _ctx.Runner.Run((svc, _, _) => svc.ListEcus(filter), http.RequestAborted);
        await Answer(http, audit, outcome);
    }

    public async Task Describe(HttpContext http)
    {
        var audit = Audit(http);
        var q = Query(http, "ecu", "job", "filter", "comments");
        if (!q.Ok)
        {
            await Fail(http, audit, q.Failure!);
            return;
        }

        var values = q.Value!;
        var ecu = values.Get("ecu");
        var job = values.Get("job");
        var filter = values.Get("filter");
        audit.Ecu = ecu;
        audit.Job = job;
        if (ecu is null)
        {
            await Fail(http, audit, RequestParsing.Bad("ecu", "ecu is required."));
            return;
        }

        if (RequestParsing.CheckFilter(filter) is { } bad)
        {
            await Fail(http, audit, bad);
            return;
        }

        var comments = RequestParsing.ParseBool(values.Get("comments"), absent: true);
        if (comments is null)
        {
            await Fail(http, audit, RequestParsing.Bad("comments", "comments must be true or false."));
            return;
        }

        if (job is null)
        {
            var list = await _ctx.Runner.Run((svc, queuedMs, ct) => svc.DescribeList(ecu, filter, comments.Value, audit, ct, queuedMs), http.RequestAborted);
            await Answer(http, audit, list);
        }
        else
        {
            var one = await _ctx.Runner.Run((svc, queuedMs, ct) => svc.DescribeJob(ecu, job, audit, ct, queuedMs), http.RequestAborted);
            await Answer(http, audit, one);
        }
    }

    public async Task Job(HttpContext http)
    {
        var audit = Audit(http);
        if (!await NoQuery(http, audit))
        {
            return;
        }

        var body = await ReadBody(http);
        var parsed = body is null ? TooLarge() : RequestParsing.ParseJob(body);
        if (!parsed.Ok)
        {
            await Fail(http, audit, parsed.Failure!);
            return;
        }

        var request = parsed.Value!;
        audit.Ecu = request.Request.Ecu;
        audit.Job = request.Request.Job;
        var outcome = await _ctx.Runner.Run((svc, queuedMs, ct) => svc.RunJob(request.Request, request.TimeoutSeconds, audit, ct, queuedMs), http.RequestAborted);
        await Answer(http, audit, outcome);
    }

    public async Task Jobs(HttpContext http)
    {
        var audit = Audit(http);
        if (!await NoQuery(http, audit))
        {
            return;
        }

        var body = await ReadBody(http);
        var parsed = body is null ? TooLargeJobs() : RequestParsing.ParseJobs(body);
        if (!parsed.Ok)
        {
            await Fail(http, audit, parsed.Failure!);
            return;
        }

        var request = parsed.Value!;
        try
        {
            var outcome = await _ctx.Runner.Run((svc, queuedMs, ct) => svc.RunJobs(request.Items, request.TimeoutSeconds, audit, ct, queuedMs), http.RequestAborted);
            await Answer(http, audit, outcome);
        }
        finally
        {
            WriteItemLines(http, audit);
        }
    }

    public async Task Vehicle(HttpContext http)
    {
        var audit = Audit(http);
        if (!await NoQuery(http, audit))
        {
            return;
        }

        var sources = _ctx.Config.VoltageSources;
        try
        {
            var outcome = await _ctx.Runner.Run((svc, queuedMs, ct) => svc.Vehicle(sources, audit, ct, queuedMs), http.RequestAborted);
            await Answer(http, audit, outcome);
        }
        finally
        {
            WriteItemLines(http, audit);
        }
    }

    // ---- helpers ----

    private static RequestAudit Audit(HttpContext http)
    {
        var audit = new RequestAudit();
        http.Items[AuditKey] = audit;
        return audit;
    }

    private static Outcome<QueryValues> Query(HttpContext http, params string[] allowed) =>
        RequestParsing.ParseQuery(
            http.Request.Query.Select(kv => new KeyValuePair<string, IReadOnlyList<string?>>(kv.Key, kv.Value.ToArray())),
            allowed);

    /// <summary>POST bodies and /vehicle take no query parameters.</summary>
    private static async Task<bool> NoQuery(HttpContext http, RequestAudit audit)
    {
        var q = Query(http);
        if (q.Ok)
        {
            return true;
        }

        await Fail(http, audit, q.Failure!);
        return false;
    }

    /// <summary>The body, or null when it is over 64 KB (also when Kestrel's own limit trips).</summary>
    private static async Task<byte[]?> ReadBody(HttpContext http)
    {
        if (http.Request.ContentLength > RequestParsing.MaxBodyBytes)
        {
            return null;
        }

        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            int n;
            while ((n = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) > 0)
            {
                if (ms.Length + n > RequestParsing.MaxBodyBytes)
                {
                    return null;
                }

                ms.Write(buffer, 0, n);
            }
        }
        catch (BadHttpRequestException)
        {
            return null;
        }

        return ms.ToArray();
    }

    private static Outcome<JobBody> TooLarge() =>
        RequestParsing.Bad("body", $"The body is larger than {RequestParsing.MaxBodyBytes / 1024} KB.");

    private static Outcome<JobsBody> TooLargeJobs() =>
        RequestParsing.Bad("body", $"The body is larger than {RequestParsing.MaxBodyBytes / 1024} KB.");

    private static async Task Answer<T>(HttpContext http, RequestAudit audit, Outcome<T> outcome)
        where T : class
    {
        if (outcome.Ok)
        {
            await Responses.WriteJson(http, StatusCodes.Status200OK, outcome.Value!);
        }
        else
        {
            await Fail(http, audit, outcome.Failure!);
        }
    }

    private static Task Fail(HttpContext http, RequestAudit audit, ApiFailure failure)
    {
        audit.ErrorCode = failure.Error.Code;
        return Responses.WriteFailure(http, failure);
    }

    /// <summary>One audit line per batch item, beside the request's own line.</summary>
    private void WriteItemLines(HttpContext http, RequestAudit audit)
    {
        foreach (var item in audit.Items)
        {
            _ctx.Audit.Write(
                DateTimeOffset.UtcNow,
                http.Connection.RemoteIpAddress?.ToString(),
                http.Request.Method,
                $"{http.Request.Path.Value}[{item.Index}]",
                null,
                http.Response.StatusCode,
                item.ErrorCode,
                item.DurationMs,
                item.Ecu,
                item.Job,
                item.Rule);
        }
    }
}
