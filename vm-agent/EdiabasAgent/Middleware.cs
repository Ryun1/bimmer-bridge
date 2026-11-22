using System.Diagnostics;
using System.Net;
using EdiabasAgent.Core;
using Microsoft.AspNetCore.Http;

namespace EdiabasAgent;

/// <summary>
/// The whole request pipeline as one terminal delegate, so the order is explicit and cannot be
/// changed by framework defaults:
/// <list type="number">
/// <item>audit (outermost, in <c>finally</c>): one line for every request, rejected ones included;</item>
/// <item>error shaping: unhandled exception becomes 500 <c>internal_error</c>; details go to agent.log only;</item>
/// <item>remote-address check: only the selected gateway (the Mac) may talk to us, else 403 <c>forbidden</c>;</item>
/// <item>auth: Bearer token, 401 <c>unauthorized</c> before any routing;</item>
/// <item>routing: unknown path 404 <c>not_found</c>, wrong method 405 <c>method_not_allowed</c>.</item>
/// </list>
/// </summary>
internal sealed class RequestPipeline
{
    private readonly AgentContext _ctx;
    private readonly BindSelection _selection;
    private readonly IReadOnlyList<Route> _routes;

    public RequestPipeline(AgentContext ctx, BindSelection selection, IReadOnlyList<Route> routes)
    {
        _ctx = ctx;
        _selection = selection;
        _routes = routes;
    }

    public async Task Handle(HttpContext http)
    {
        var started = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        string? errorCode = null;

        try
        {
            try
            {
                errorCode = await Process(http);
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
                errorCode = "aborted";
            }
            catch (Exception ex)
            {
                // Details only to agent.log; the client gets the generic message.
                var where = AuditLine.Neutralise($"{http.Request.Method} {http.Request.Path.Value}");
                _ctx.Log.Error($"unhandled exception for {where}", ex);
                errorCode = ApiErrors.InternalError;
                if (!http.Response.HasStarted)
                {
                    http.Response.Clear();
                    await Responses.WriteError(http, StatusCodes.Status500InternalServerError, ApiErrors.InternalError,
                        "Internal error. Details are in the agent log.");
                }
                else
                {
                    http.Abort();
                }
            }
        }
        finally
        {
            sw.Stop();
            var detail = http.Items.TryGetValue(EdiabasEndpoints.AuditKey, out var a) ? a as RequestAudit : null;
            _ctx.Audit.Write(
                started,
                http.Connection.RemoteIpAddress?.ToString(),
                http.Request.Method,
                http.Request.Path.Value,
                http.Request.QueryString.Value,
                http.Response.StatusCode,
                errorCode,
                sw.ElapsedMilliseconds,
                detail?.Ecu,
                detail?.Job,
                detail?.Rule,
                http.Items.TryGetValue(HandlerSupport.AuditTargetKey, out var t) ? t as string : null);
        }
    }

    /// <summary>Returns the error code written, or null on success.</summary>
    private async Task<string?> Process(HttpContext http)
    {
        // Defence in depth beside the firewall: only the gateway of the selected adapter.
        var remote = Normalize(http.Connection.RemoteIpAddress);
        if (remote is null || _selection.Gateway is null || !remote.Equals(_selection.Gateway))
        {
            await Responses.WriteError(http, StatusCodes.Status403Forbidden, ApiErrors.Forbidden,
                "Requests are accepted only from the network gateway.");
            return ApiErrors.Forbidden;
        }

        // Auth before routing: exactly one Authorization header, well-formed, correct token.
        var authValues = http.Request.Headers.Authorization;
        var header = authValues.Count == 1 ? authValues[0] : null;
        if (!_ctx.Auth.IsAuthorized(header))
        {
            http.Response.Headers.WWWAuthenticate = "Bearer";
            await Responses.WriteError(http, StatusCodes.Status401Unauthorized, ApiErrors.Unauthorized,
                "Missing or invalid bearer token.");
            return ApiErrors.Unauthorized;
        }

        var path = http.Request.Path.Value ?? "";
        var forPath = _routes.Where(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (forPath.Count == 0)
        {
            await Responses.WriteError(http, StatusCodes.Status404NotFound, ApiErrors.NotFound, "Unknown path.");
            return ApiErrors.NotFound;
        }

        var route = forPath.FirstOrDefault(r => HttpMethods.Equals(r.Method, http.Request.Method));
        if (route is null)
        {
            http.Response.Headers.Allow = string.Join(", ", forPath.Select(r => r.Method));
            await Responses.WriteError(http, StatusCodes.Status405MethodNotAllowed, ApiErrors.MethodNotAllowed,
                "Method not allowed for this path.");
            return ApiErrors.MethodNotAllowed;
        }

        await route.Handler(http);

        // Handlers that answer with an error record its code for the audit line.
        return http.Items.TryGetValue(EdiabasEndpoints.AuditKey, out var a) && a is RequestAudit audit ? audit.ErrorCode : null;
    }

    private static IPAddress? Normalize(IPAddress? address) =>
        address is not null && address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
