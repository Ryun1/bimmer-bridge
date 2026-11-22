using System.Text;
using System.Text.Json;

namespace EdiabasAgent.Core;

public sealed record JobBody(JobRequest Request, int? TimeoutSeconds);

public sealed record JobsBody(IReadOnlyList<JobRequest> Items, int? TimeoutSeconds);

/// <summary>Validated query parameters: each allowed key at most once, values as given.</summary>
public sealed record QueryValues(IReadOnlyDictionary<string, string> Values)
{
    public string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
}

/// <summary>
/// Strict parsing of request bodies and query strings for the EDIABAS endpoints. Anything
/// unexpected is <c>bad_request</c>: invalid UTF-8, a byte-order mark, comments, trailing
/// commas, duplicate or unknown properties, wrong types, more than 64 KB.
/// </summary>
public static class RequestParsing
{
    public const int MaxBodyBytes = 64 * 1024;
    public const int MaxFilterLength = 64;

    private static readonly string[] JobKeys = ["ecu", "job", "args", "results", "timeoutSeconds"];
    private static readonly string[] JobsKeys = ["items", "timeoutSeconds"];
    private static readonly string[] ItemKeys = ["ecu", "job", "args", "results"];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static Outcome<JobBody> ParseJob(ReadOnlySpan<byte> body)
    {
        try
        {
            using var doc = ParseDocument(body);
            var obj = Object(doc.RootElement, "body", JobKeys);
            var request = ReadJob(obj, "");
            return new JobBody(request, Timeout(obj));
        }
        catch (BadRequest ex)
        {
            return ex.Failure;
        }
    }

    public static Outcome<JobsBody> ParseJobs(ReadOnlySpan<byte> body)
    {
        try
        {
            using var doc = ParseDocument(body);
            var obj = Object(doc.RootElement, "body", JobsKeys);
            if (!obj.TryGetValue("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
            {
                throw new BadRequest("items", "items must be an array.");
            }

            var count = itemsEl.GetArrayLength();
            if (count is < 1 or > EdiabasService.MaxBatchItems)
            {
                throw new BadRequest("items", $"items must hold 1 to {EdiabasService.MaxBatchItems} jobs.");
            }

            var items = new List<JobRequest>(count);
            var i = 0;
            foreach (var el in itemsEl.EnumerateArray())
            {
                var where = $"items[{i}]";
                items.Add(ReadJob(Object(el, where, ItemKeys), where + "."));
                i++;
            }

            return new JobsBody(items, Timeout(obj));
        }
        catch (BadRequest ex)
        {
            return ex.Failure;
        }
    }

    /// <summary>
    /// Checks a query string given as key → every value supplied for it. Keys are
    /// case-sensitive and each may appear once.
    /// </summary>
    public static Outcome<QueryValues> ParseQuery(IEnumerable<KeyValuePair<string, IReadOnlyList<string?>>> query, params string[] allowed)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, vals) in query)
        {
            if (Array.IndexOf(allowed, key) < 0)
            {
                return Bad(key, $"Unknown query parameter '{Printable(key)}'.");
            }

            if (vals.Count != 1 || vals[0] is null)
            {
                return Bad(key, $"Query parameter '{key}' must be given exactly once.");
            }

            values[key] = vals[0]!;
        }

        return new QueryValues(values);
    }

    /// <summary><c>filter</c>: optional, at most 64 printable ASCII characters. Empty means none.</summary>
    public static ApiFailure? CheckFilter(string? filter)
    {
        if (filter is null)
        {
            return null;
        }

        return filter.Length <= MaxFilterLength && filter.All(c => c is >= ' ' and <= '~')
            ? null
            : Bad("filter", $"filter must be at most {MaxFilterLength} printable ASCII characters.");
    }

    /// <summary><c>true</c> or <c>false</c> exactly; absent gives <paramref name="absent"/>.</summary>
    public static bool? ParseBool(string? value, bool absent) => value switch
    {
        null => absent,
        "true" => true,
        "false" => false,
        _ => null,
    };

    public static ApiFailure Bad(string parameter, string message) =>
        ApiFailure.Of(ApiErrors.BadRequest, message, new ParameterDetails(parameter));

    private static JsonDocument ParseDocument(ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxBodyBytes)
        {
            throw new BadRequest("body", $"The body is larger than {MaxBodyBytes / 1024} KB.");
        }

        if (body.Length == 0)
        {
            throw new BadRequest("body", "The body is empty; a JSON object is required.");
        }

        if (body.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            throw new BadRequest("body", "The body must not start with a byte-order mark.");
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            throw new BadRequest("body", "The body is not valid UTF-8.");
        }

        try
        {
            return JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
        }
        catch (JsonException)
        {
            throw new BadRequest("body", "The body is not valid JSON.");
        }
    }

    private static Dictionary<string, JsonElement> Object(JsonElement el, string where, string[] keys)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            throw new BadRequest(where, $"{where} must be a JSON object.");
        }

        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in el.EnumerateObject())
        {
            var name = where == "body" ? p.Name : $"{where}.{p.Name}";
            if (Array.IndexOf(keys, p.Name) < 0)
            {
                throw new BadRequest(Printable(name), $"Unknown property '{Printable(name)}'.");
            }

            if (!map.TryAdd(p.Name, p.Value))
            {
                throw new BadRequest(name, $"Property '{name}' appears more than once.");
            }
        }

        return map;
    }

    private static JobRequest ReadJob(Dictionary<string, JsonElement> obj, string prefix) =>
        new(
            RequiredString(obj, "ecu", prefix),
            RequiredString(obj, "job", prefix),
            OptionalString(obj, "args", prefix),
            OptionalString(obj, "results", prefix));

    private static string RequiredString(Dictionary<string, JsonElement> obj, string key, string prefix) =>
        OptionalString(obj, key, prefix) ?? throw new BadRequest(prefix + key, $"{prefix + key} is required and must be a string.");

    private static string? OptionalString(Dictionary<string, JsonElement> obj, string key, string prefix)
    {
        if (!obj.TryGetValue(key, out var el) || el.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (el.ValueKind != JsonValueKind.String)
        {
            throw new BadRequest(prefix + key, $"{prefix + key} must be a string.");
        }

        try
        {
            return el.GetString();
        }
        catch (InvalidOperationException)
        {
            throw new BadRequest(prefix + key, $"{prefix + key} is not a valid string.");
        }
    }

    private static int? Timeout(Dictionary<string, JsonElement> obj)
    {
        if (!obj.TryGetValue("timeoutSeconds", out var el) || el.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var seconds) || seconds < 1)
        {
            throw new BadRequest("timeoutSeconds", "timeoutSeconds must be a whole number of seconds, at least 1.");
        }

        return seconds;
    }

    private static string Printable(string s) =>
        new(s.Take(64).Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());

    private sealed class BadRequest(string parameter, string message) : Exception(message)
    {
        public ApiFailure Failure { get; } = Bad(parameter, message);
    }
}
