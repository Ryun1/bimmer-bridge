namespace EdiabasAgent.Core;

// Response and error-detail records for the EDIABAS endpoints (docs/api.md). Property order
// here is the JSON property order.

/// <summary>One result set: result name → value, in the order EDIABAS returned them.</summary>
public sealed class ResultSet : OrderedDictionary<string, object?>
{
    public ResultSet()
        : base(StringComparer.Ordinal)
    {
    }
}

public sealed record JobResult(
    string Ecu,
    string Job,
    string? Variant,
    string? JobStatus,
    ResultSet SystemResults,
    IReadOnlyList<ResultSet> Sets,
    long DurationMs);

public sealed record BatchItem(bool Ok, JobResult? Result, ErrorDetail? Error);

public sealed record BatchResponse(IReadOnlyList<BatchItem> Items, long DurationMs);

public sealed record VehicleVoltage(string Ecu, string Job, bool Ok, JobResult? Result, ErrorDetail? Error);

public sealed record VehicleResponse(
    BatchItem Ignition,
    BatchItem Ubatt,
    IReadOnlyList<VehicleVoltage> Voltages,
    BatchItem CableKl30,
    BatchItem CableKl15,
    long DurationMs);

/// <summary><c>review</c> values of a describe verdict.</summary>
public static class Review
{
    public const string NotRequired = "not_required";
    public const string Passed = "passed";
    public const string NeedsReview = "needs_review";
    public const string Reviewed = "reviewed";
    public const string NotChecked = "not_checked";
}

public sealed record JobVerdict(bool Allowed, string Rule, string Review);

public sealed record DescribeJobEntry(string Name, string? Comment, JobVerdict Verdict);

public sealed record DescribeListResponse(
    string Ecu,
    string? Variant,
    int JobCount,
    IReadOnlyList<DescribeJobEntry> Jobs,
    long DurationMs);

public sealed record JobParameterInfo(string? Name, string? Type, string? Comment);

public sealed record DescribeJobResponse(
    string Ecu,
    string? Variant,
    string Job,
    string? Comment,
    JobVerdict Verdict,
    IReadOnlyList<JobParameterInfo> Arguments,
    IReadOnlyList<JobParameterInfo> Results,
    long DurationMs);

public sealed record EcuFile(string Name, string Type);

public sealed record EcusResponse(string EcuPath, int Count, bool Truncated, IReadOnlyList<EcuFile> Files);

// ---- error details ----

public sealed record ParameterDetails(string Parameter);

public sealed record JobRefusalDetails(string Ecu, string Job, string Rule);

public sealed record JobReviewDetails(string Ecu, string Job, string? Description, IReadOnlyList<string> Matched);

public sealed record EcuDetails(string Ecu);

public sealed record EcuJobDetails(string Ecu, string Job);

public sealed record ProcessesDetails(IReadOnlyList<ConflictingProcess> Processes);

public sealed record EdiabasErrorDetails(int ErrorCode, string ErrorText);

public sealed record ReasonDetails(string Reason);

public sealed record TimeoutDetails(int TimeoutSeconds);

/// <summary>A failure with its HTTP status, for handlers to write as an error body.</summary>
public sealed record ApiFailure(int Status, ErrorDetail Error)
{
    public static ApiFailure Of(string code, string message, object? details = null) =>
        new(ApiErrors.Status(code), new ErrorDetail(code, message, details));
}

/// <summary>Either a value or a failure.</summary>
public sealed class Outcome<T>
    where T : class
{
    private Outcome(T? value, ApiFailure? failure)
    {
        Value = value;
        Failure = failure;
    }

    public T? Value { get; }
    public ApiFailure? Failure { get; }
    public bool Ok => Failure is null;

    public static implicit operator Outcome<T>(T value) => new(value, null);

    public static implicit operator Outcome<T>(ApiFailure failure) => new(null, failure);
}
