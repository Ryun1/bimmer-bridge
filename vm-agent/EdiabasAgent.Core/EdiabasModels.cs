using System.Text.Json.Serialization;

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

/// <summary><c>CommentsCutOff</c>: null when every description asked for was fetched (or none
/// was asked for); else why the rest are <c>not_checked</c>, one of <see cref="CutOff"/>.</summary>
public sealed record DescribeListResponse(
    string Ecu,
    string? Variant,
    int JobCount,
    IReadOnlyList<DescribeJobEntry> Jobs,
    long DurationMs,
    string? CommentsCutOff = null);

/// <summary>Why a describe stopped fetching descriptions before the end of the list.</summary>
public static class CutOff
{
    /// <summary>The request reached its time limit. Asking again goes further: the descriptions
    /// already read come from the agent's cache.</summary>
    public const string TimeLimit = "time_limit";

    /// <summary>A BMW tool started during the request.</summary>
    public const string ToolStarted = "tool_started";

    /// <summary>A job did not stop after apiBreak; EDIABAS is not used again until a restart.</summary>
    public const string EdiabasStuck = "ediabas_stuck";

    /// <summary>The client went away.</summary>
    public const string Abandoned = "abandoned";
}

public sealed record JobParameterInfo(string? Name, string? Type, string? Comment);

/// <summary><c>DescriptionHash</c>: <see cref="CommentScreen.DescriptionHash"/> of <c>Comment</c>, what a
/// pinned <c>reviewedJobs</c> entry holds; null when the description is empty or could not be read.</summary>
public sealed record DescribeJobResponse(
    string Ecu,
    string? Variant,
    string Job,
    string? Comment,
    string? DescriptionHash,
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

/// <summary><c>ResultsUnread</c>: true only when the job finished but its results could not be read,
/// so the ECU is not to blame. Left out of the JSON otherwise, so every other error keeps its shape.</summary>
public sealed record EdiabasErrorDetails(
    int ErrorCode,
    string ErrorText,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ResultsUnread = null);

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
