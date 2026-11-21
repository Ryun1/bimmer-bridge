using System.Runtime.InteropServices;
using EdiabasAgent.Core;
using EdiabasAgent.Windows;
using Microsoft.AspNetCore.Http;

namespace EdiabasAgent;

/// <summary>One route: exact path (case-insensitive) and method.</summary>
internal sealed record Route(string Path, string Method, Func<HttpContext, Task> Handler);

/// <summary>Handlers for <c>GET /health</c> and <c>GET /windows</c> (docs/api.md).</summary>
internal sealed class Endpoints
{
    private readonly AgentContext _ctx;
    private readonly BindSelection _selection;

    public Endpoints(AgentContext ctx, BindSelection selection)
    {
        _ctx = ctx;
        _selection = selection;
        Routes =
        [
            new Route("/health", HttpMethods.Get, Health),
            new Route("/windows", HttpMethods.Get, Windows),
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
            Ediabas: new EdiabasInfo(dll, dllFound),
            LastCableUseAt: null);

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
}
