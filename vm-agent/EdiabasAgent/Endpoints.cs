using System.Runtime.InteropServices;
using EdiabasAgent.Core;
using EdiabasAgent.Windows;
using Microsoft.AspNetCore.Http;

namespace EdiabasAgent;

/// <summary>One route: exact path (case-insensitive) and method.</summary>
internal sealed record Route(string Path, string Method, Func<HttpContext, Task> Handler);

/// <summary>
/// The route table, the single list of what exists, plus the handlers for <c>GET /health</c> and
/// <c>GET /windows</c> (docs/api.md). EDIABAS handlers are in EdiabasEndpoints.cs, screenshots
/// in ScreenEndpoints.cs, logs in LogEndpoints.cs.
/// </summary>
internal sealed class Endpoints
{
    private readonly AgentContext _ctx;
    private readonly BindSelection _selection;

    public Endpoints(AgentContext ctx, BindSelection selection)
    {
        _ctx = ctx;
        _selection = selection;
        var ediabas = new EdiabasEndpoints(ctx);
        var screen = new ScreenEndpoints(ctx);
        var logs = new LogEndpoints(ctx.Logs);
        Routes =
        [
            new Route("/health", HttpMethods.Get, Health),
            new Route("/windows", HttpMethods.Get, Windows),
            new Route("/screen/capture", HttpMethods.Get, screen.Capture),
            new Route("/logs", HttpMethods.Get, logs.List),
            new Route("/logs/read", HttpMethods.Get, logs.Read),
            new Route("/ediabas/ecus", HttpMethods.Get, ediabas.Ecus),
            new Route("/ediabas/describe", HttpMethods.Get, ediabas.Describe),
            new Route("/ediabas/job", HttpMethods.Post, ediabas.Job),
            new Route("/ediabas/jobs", HttpMethods.Post, ediabas.Jobs),
            new Route("/ediabas/vehicle", HttpMethods.Get, ediabas.Vehicle),
        ];
    }

    public IReadOnlyList<Route> Routes { get; }

    private Task Health(HttpContext http)
    {
        var now = DateTimeOffset.UtcNow;
        var dll = _ctx.Config.EdiabasDll;

        // File-existence check only: /health never loads api32.dll and never touches the cable.
        bool dllFound;
        try
        {
            dllFound = File.Exists(dll);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            dllFound = false;
        }

        var body = new HealthResponse(
            Agent: HealthResponse.AgentName,
            Version: AgentContext.Version,
            StartedAt: ApiFormat.Timestamp(_ctx.StartedAt),
            UptimeSeconds: (long)Math.Max(0, (now - _ctx.StartedAt).TotalSeconds),
            Os: new OsInfo(
                RuntimeInformation.OSDescription,
                RuntimeInformation.OSArchitecture.ToString(),
                RuntimeInformation.ProcessArchitecture.ToString()),
            User: Environment.UserName,
            Elevated: _ctx.Elevated,
            SessionId: _ctx.SessionId,
            BindAddress: _selection.Address!.ToString(),
            Port: _ctx.Config.Port,
            Gateway: _selection.Gateway?.ToString(),
            ConflictingProcesses: _ctx.Conflicts.Running(),
            Ediabas: new EdiabasInfo(dll, dllFound, _ctx.DllState.Loaded),
            LastCableUseAt: _ctx.CableUse.LastInitAt is { } t ? ApiFormat.Timestamp(t) : null,
            PolicyVersion: _ctx.Policy.Version,
            Capabilities: AgentContext.Capabilities);

        return Responses.WriteJson(http, StatusCodes.Status200OK, body);
    }

    private Task Windows(HttpContext http)
    {
        var body = new WindowsResponse(WindowEnumerator.List());
        return Responses.WriteJson(http, StatusCodes.Status200OK, body);
    }
}

/// <summary>Writes JSON responses with the contract's content type.</summary>
internal static class Responses
{
    public static async Task WriteJson<T>(HttpContext http, int status, T body)
    {
        var bytes = ApiJson.SerializeToUtf8(body);
        http.Response.StatusCode = status;
        http.Response.ContentType = ApiJson.ContentType;
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.ContentLength = bytes.Length;
        await http.Response.Body.WriteAsync(bytes, http.RequestAborted);
    }

    public static Task WriteError(HttpContext http, int status, string code, string message) =>
        WriteJson(http, status, ApiErrors.Create(code, message));

    public static Task WriteFailure(HttpContext http, ApiFailure failure) =>
        WriteJson(http, failure.Status, new ErrorBody(failure.Error));
}
