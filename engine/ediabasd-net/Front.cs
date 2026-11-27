using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using EdiabasAgent.Core;
using EdiabasLib.Native;

namespace Ediabasd;

/// One request's answer: a result body (as docs/api.md defines it for the matching endpoint)
/// or an error.
public sealed record FrontResponse(bool Ok, object? Result, ErrorDetail? Error)
{
    public static FrontResponse Success(object result) => new(true, result, null);

    public static FrontResponse Failure(ApiFailure f) => new(false, null, f.Error);
}

/// State: absent, free, locked, several, unsupported or unknown. Latency: what the last live
/// session's open applied ("1ms", "16ms", "unsupported"), null before any in this process.
public sealed record CableHealth(string State, string? Port, string? Latency);

public sealed record NativeHealth(string Engine, string Version, string Kline, string EcuPath, CableHealth Cable, int? PolicyVersion, string? PolicySha256);

/// <summary>
/// Dispatches the stdio and CLI methods to EdiabasService: <c>ecus</c>, <c>describe</c>,
/// <c>job</c>, <c>jobs</c>, <c>health</c>. Sets the API's request mode first:
/// only job and jobs may reach the car. One audit line per request (and one per
/// batch item).
/// </summary>
public sealed class Front(EdiabasdHost host, AuditLog audit)
{
    public static string Version { get; } =
        typeof(Front).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    static readonly string[] NoKeys = [];
    static readonly string[] EcusKeys = ["filter"];
    static readonly string[] DescribeKeys = ["ecu", "job", "filter", "comments"];

    public FrontResponse Handle(string? method, JsonElement? parameters)
    {
        var sw = Stopwatch.StartNew();
        var a = new RequestAudit();
        FrontResponse r;
        try
        {
            r = Dispatch(method, parameters, a);
        }
        catch (Exception ex)
        {
            host.Log.Warn($"{method}: {ex.GetType().Name}: {ex.Message}");
            r = FrontResponse.Failure(ApiFailure.Of(ApiErrors.InternalError, "ediabasd failed while handling the request; see its stderr."));
        }

        var code = r.Error?.Code;
        var status = r.Ok ? 200 : ApiErrors.Status(code!);
        var name = string.IsNullOrEmpty(method) ? "-" : method;
        audit.Write(DateTimeOffset.UtcNow, name, status, code, sw.ElapsedMilliseconds, a.Ecu, a.Job, a.Rule);
        foreach (var item in a.Items)
        {
            audit.Write(DateTimeOffset.UtcNow, $"{name}[{item.Index}]", status, item.ErrorCode, item.DurationMs, item.Ecu, item.Job, item.Rule);
        }

        return r;
    }

    FrontResponse Dispatch(string? method, JsonElement? p, RequestAudit a)
    {
        var ct = CancellationToken.None;
        switch (method)
        {
            case "ecus":
            {
                host.Api.Mode = RequestMode.Offline;
                if (!Params(p, EcusKeys, out var o, out var bad)) return bad!;
                if (!Str(o, "filter", out var filter, out bad)) return bad!;
                if (RequestParsing.CheckFilter(filter) is { } f) return FrontResponse.Failure(f);
                return Answer(host.Service.ListEcus(filter));
            }

            case "describe":
            {
                host.Api.Mode = RequestMode.Offline;
                if (!Params(p, DescribeKeys, out var o, out var bad)) return bad!;
                if (!Str(o, "ecu", out var ecu, out bad) || !Str(o, "job", out var job, out bad) || !Str(o, "filter", out var filter, out bad)) return bad!;
                a.Ecu = ecu;
                a.Job = job;
                if (ecu is null) return Bad("ecu", "ecu is required.");
                if (RequestParsing.CheckFilter(filter) is { } f) return FrontResponse.Failure(f);
                var comments = true;
                if (o.TryGetValue("comments", out var c))
                {
                    if (c.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Bad("comments", "comments must be true or false.");
                    comments = c.GetBoolean();
                }

                return job is null
                    ? Answer(host.Service.DescribeList(ecu, filter, comments, a, ct))
                    : Answer(host.Service.DescribeJob(ecu, job, a, ct));
            }

            case "job":
            {
                host.Api.Mode = RequestMode.Live;
                var parsed = RequestParsing.ParseJob(Body(p));
                if (!parsed.Ok) return FrontResponse.Failure(parsed.Failure!);
                a.Ecu = parsed.Value!.Request.Ecu;
                a.Job = parsed.Value.Request.Job;
                return Answer(host.Service.RunJob(parsed.Value.Request, parsed.Value.TimeoutSeconds, a, ct));
            }

            case "jobs":
            {
                host.Api.Mode = RequestMode.Live;
                var parsed = RequestParsing.ParseJobs(Body(p));
                if (!parsed.Ok) return FrontResponse.Failure(parsed.Failure!);
                return Answer(host.Service.RunJobs(parsed.Value!.Items, parsed.Value.TimeoutSeconds, a, ct));
            }

            case "health":
            {
                if (!Params(p, NoKeys, out _, out var bad)) return bad!;
                return FrontResponse.Success(Health());
            }

            default:
                return Bad("method", $"unknown method \"{AuditLine.Neutralise(method ?? "")}\"; use ecus, describe, job, jobs or health.");
        }
    }

    /// Never opens the cable: its USB identity and a try-lock of its lock file only.
    NativeHealth Health()
    {
        string kline;
        CableHealth cable;
        var latency = KlineTransport.LastOpenLatency;
        try
        {
            kline = KlineLibrary.Version();
            if (!string.IsNullOrEmpty(host.Config.Port))
            {
                cable = new CableHealth(KlineLibrary.CableState(host.Config.Port), host.Config.Port, latency);
            }
            else
            {
                var found = KlineLibrary.FindCable(out var count);
                var state = count switch { 0 => "absent", 1 => KlineLibrary.CableState(found), _ => "several" };
                cable = new CableHealth(state, found, latency);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            kline = "unavailable: " + ex.Message;
            cable = new CableHealth("unknown", host.Config.Port, latency);
        }

        return new NativeHealth("ediabasd-net", Version, kline, host.Config.EcuPath, cable, host.Policy.Version, host.Policy.Sha256);
    }

    static FrontResponse Answer<T>(Outcome<T> o)
        where T : class =>
        o.Ok ? FrontResponse.Success(o.Value!) : FrontResponse.Failure(o.Failure!);

    static FrontResponse Bad(string parameter, string message) => FrontResponse.Failure(RequestParsing.Bad(parameter, message));

    static byte[] Body(JsonElement? p) =>
        p is { ValueKind: not JsonValueKind.Undefined } e ? Encoding.UTF8.GetBytes(e.GetRawText()) : "{}"u8.ToArray();

    static bool Params(JsonElement? p, string[] allowed, out Dictionary<string, JsonElement> o, out FrontResponse? bad)
    {
        o = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        bad = null;
        if (p is null || p.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        if (p.Value.ValueKind != JsonValueKind.Object)
        {
            bad = Bad("params", "params must be an object.");
            return false;
        }

        foreach (var prop in p.Value.EnumerateObject())
        {
            if (!allowed.Contains(prop.Name, StringComparer.Ordinal))
            {
                bad = Bad(prop.Name, $"unknown parameter \"{AuditLine.Neutralise(prop.Name)}\".");
                return false;
            }

            o[prop.Name] = prop.Value;
        }

        return true;
    }

    static bool Str(Dictionary<string, JsonElement> o, string name, out string? value, out FrontResponse? bad)
    {
        value = null;
        bad = null;
        if (!o.TryGetValue(name, out var e) || e.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (e.ValueKind != JsonValueKind.String)
        {
            bad = Bad(name, $"{name} must be a string.");
            return false;
        }

        value = e.GetString();
        return true;
    }
}
