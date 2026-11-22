using EdiabasAgent.Core;
using Microsoft.AspNetCore.Http;

namespace EdiabasAgent;

/// <summary>
/// What the screenshot and log handlers share with each other: the strict query parser, the
/// request's audit record (the same one the EDIABAS handlers leave for the pipeline), and the
/// audit target (what was asked for: a log path or a <c>window</c> value).
/// </summary>
internal static class HandlerSupport
{
    /// <summary>Key in <see cref="HttpContext.Items"/> for the audit line's <c>target</c> field.</summary>
    public const string AuditTargetKey = "ediabas-bridge.audit-target";

    public static RequestAudit Audit(HttpContext http)
    {
        var audit = new RequestAudit();
        http.Items[EdiabasEndpoints.AuditKey] = audit;
        return audit;
    }

    public static void Target(HttpContext http, string? target) => http.Items[AuditTargetKey] = target;

    public static Outcome<QueryValues> Query(HttpContext http, params string[] allowed) =>
        RequestParsing.ParseQuery(
            http.Request.Query.Select(kv => new KeyValuePair<string, IReadOnlyList<string?>>(kv.Key, kv.Value.ToArray())),
            allowed);

    public static Task Fail(HttpContext http, RequestAudit audit, ApiFailure failure)
    {
        audit.ErrorCode = failure.Error.Code;
        return Responses.WriteFailure(http, failure);
    }
}
