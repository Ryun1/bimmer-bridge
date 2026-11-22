using System.Globalization;
using EdiabasAgent.Capture;
using EdiabasAgent.Core;
using Microsoft.AspNetCore.Http;

namespace EdiabasAgent;

/// <summary>
/// <c>GET /screen/capture</c> (docs/api.md). Parse, capture with <see cref="WindowCapture"/>,
/// answer with the image bytes and the contract's headers. One capture at a time, which bounds
/// the memory a burst of requests can take.
/// </summary>
internal sealed class ScreenEndpoints
{
    private static readonly TimeSpan QueueWait = TimeSpan.FromSeconds(15);

    private readonly AgentContext _ctx;
    private readonly SemaphoreSlim _one = new(1, 1);

    public ScreenEndpoints(AgentContext ctx)
    {
        _ctx = ctx;
    }

    public async Task Capture(HttpContext http)
    {
        var audit = HandlerSupport.Audit(http);
        var q = HandlerSupport.Query(http, "window", "maxEdge", "format");
        if (!q.Ok)
        {
            await HandlerSupport.Fail(http, audit, q.Failure!);
            return;
        }

        HandlerSupport.Target(http, q.Value!.Get("window") ?? CaptureRules.DesktopName);
        var request = CaptureRules.Parse(q.Value!);
        if (!request.Ok)
        {
            await HandlerSupport.Fail(http, audit, request.Failure!);
            return;
        }

        if (!await _one.WaitAsync(QueueWait, http.RequestAborted))
        {
            await HandlerSupport.Fail(http, audit, CaptureRules.Busy());
            return;
        }

        Outcome<CapturedImage> outcome;
        try
        {
            outcome = WindowCapture.Capture(request.Value!, _ctx.SessionId);
        }
        finally
        {
            _one.Release();
        }

        if (!outcome.Ok)
        {
            await HandlerSupport.Fail(http, audit, outcome.Failure!);
            return;
        }

        var image = outcome.Value!;
        var headers = http.Response.Headers;
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = image.ContentType;
        headers.CacheControl = "no-store";
        headers.XContentTypeOptions = "nosniff";
        headers["X-Window-Title"] = CaptureRules.PercentEncode(image.Title);
        headers["X-Window-Handle"] = image.Handle;
        headers["X-Capture-Method"] = image.Method;
        headers["X-Image-Width"] = image.Width.ToString(CultureInfo.InvariantCulture);
        headers["X-Image-Height"] = image.Height.ToString(CultureInfo.InvariantCulture);
        http.Response.ContentLength = image.Bytes.Length;
        await http.Response.Body.WriteAsync(image.Bytes, http.RequestAborted);
    }
}
