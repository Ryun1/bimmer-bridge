using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdiabasAgent.Core;

// Response records for docs/api.md. Property order here is the JSON property order.

/// <summary><c>details</c> is null when there are none; otherwise one of the records in EdiabasModels.cs.</summary>
public sealed record ErrorDetail(string Code, string Message, object? Details);

public sealed record ErrorBody(ErrorDetail Error);

public sealed record OsInfo(string Description, string OsArchitecture, string ProcessArchitecture);

public sealed record ConflictingProcess(string Name, int Pid);

public sealed record EdiabasInfo(string DllPath, bool DllFound);

public sealed record HealthResponse(
    string Agent,
    string Version,
    string StartedAt,
    long UptimeSeconds,
    OsInfo Os,
    string User,
    bool Elevated,
    int SessionId,
    string BindAddress,
    int Port,
    string? Gateway,
    IReadOnlyList<ConflictingProcess> ConflictingProcesses,
    EdiabasInfo Ediabas,
    string? LastCableUseAt,
    int? PolicyVersion,
    IReadOnlyList<string> Capabilities)
{
    public const string AgentName = "ediabas-bridge-agent";
}

public sealed record Bounds(int X, int Y, int Width, int Height);

public sealed record WindowInfo(string Handle, string Title, string? ProcessName, int Pid, Bounds Bounds, bool Minimized);

public sealed record WindowsResponse(IReadOnlyList<WindowInfo> Windows);

/// <summary>Error codes from docs/api.md and their HTTP status.</summary>
public static class ApiErrors
{
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string InternalError = "internal_error";

    public const string BadRequest = "bad_request";
    public const string JobNotAllowed = "job_not_allowed";
    public const string JobNeedsReview = "job_needs_review";
    public const string EcuNotFound = "ecu_not_found";
    public const string JobNotFound = "job_not_found";
    public const string CableInUse = "cable_in_use";
    public const string EdiabasError = "ediabas_error";
    public const string EdiabasUnavailable = "ediabas_unavailable";
    public const string JobTimeout = "job_timeout";
    public const string NotRun = "not_run";

    public static ErrorBody Create(string code, string message, object? details = null) =>
        new(new ErrorDetail(code, message, details));

    /// <summary>HTTP status for a code; 500 for anything unknown.</summary>
    public static int Status(string code) => code switch
    {
        BadRequest => 400,
        Unauthorized => 401,
        Forbidden or JobNotAllowed or JobNeedsReview => 403,
        NotFound or EcuNotFound or JobNotFound => 404,
        MethodNotAllowed => 405,
        CableInUse => 409,
        EdiabasError => 502,
        EdiabasUnavailable or NotRun => 503,
        JobTimeout => 504,
        _ => 500,
    };
}

/// <summary>Formatting rules shared by every response.</summary>
public static class ApiFormat
{
    /// <summary>UTC, whole seconds, <c>Z</c> suffix: <c>2026-11-21T15:04:05Z</c>.</summary>
    public static string Timestamp(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>HWND as <c>0x</c> plus 8 upper-case hex digits (low 32 bits; HWNDs are 32-bit significant).</summary>
    public static string Handle(long hwnd) =>
        "0x" + unchecked((uint)hwnd).ToString("X8", CultureInfo.InvariantCulture);
}

/// <summary>JSON settings for every response: camelCase, explicit nulls, compact.</summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public const string ContentType = "application/json; charset=utf-8";

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static byte[] SerializeToUtf8<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
}
