using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EdiabasAgent.Core;

/// <summary>
/// The verdict for one ECU/job. <see cref="Ecu"/> and <see cref="Job"/> are the upper-cased
/// names once the shape rule has passed, else null.
/// </summary>
public sealed record PolicyVerdict(bool Allowed, string Rule, bool Screen, string? Ecu, string? Job);

/// <summary>
/// The job policy from <c>policy/policy.json</c>, evaluated exactly as docs/api.md "Job policy"
/// says. Default-deny. A policy that fails to load or validate refuses everything (rule
/// <see cref="RuleUnavailable"/>): a half-understood policy must never allow a job.
/// </summary>
public sealed class JobPolicy
{
    public const string RuleUnavailable = "policy_unavailable";
    public const string ResourceName = "EdiabasAgent.Core.policy.json";

    private static readonly Lazy<JobPolicy> EmbeddedPolicy = new(LoadEmbedded);

    // Every key the agent understands. Anything else (a new kind of deny rule, say) makes the
    // policy invalid rather than silently ignored.
    private static readonly string[] TopKeys = ["version", "namePattern", "maxArgLength", "allow", "deny", "descriptionScreen"];
    private static readonly string[] AllowKeys = ["globalExact", "patterns", "restrictedEcus"];
    private static readonly string[] DenyKeys = ["substrings", "tokens"];
    private static readonly string[] ScreenKeys = ["terms", "ignore"];

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private readonly Regex? _namePattern;
    private readonly int _maxArgLength;
    private readonly HashSet<string> _globalExact = new(StringComparer.Ordinal);
    private readonly List<Regex> _patterns = [];
    private readonly Dictionary<string, HashSet<string>> _restricted = new(StringComparer.Ordinal);
    private readonly List<string> _denySubstrings = [];
    private readonly List<string> _denyTokens = [];
    private readonly List<string> _screenTerms = [];
    private readonly List<string> _screenIgnore = [];

    private JobPolicy(string error)
    {
        LoadError = error;
    }

    private JobPolicy(int version, Regex namePattern, int maxArgLength)
    {
        Version = version;
        _namePattern = namePattern;
        _maxArgLength = maxArgLength;
    }

    /// <summary>The policy built into this assembly (linked from policy/policy.json).</summary>
    public static JobPolicy Embedded => EmbeddedPolicy.Value;

    public bool Loaded => LoadError is null;

    /// <summary>Why the policy is unusable; null when it loaded.</summary>
    public string? LoadError { get; }

    /// <summary>The file's <c>version</c>; null when the policy did not load.</summary>
    public int? Version { get; }

    /// <summary>Terms of the description screen (empty when the policy did not load).</summary>
    public IReadOnlyList<string> ScreenTerms => _screenTerms;

    /// <summary>Text the description screen removes before matching its terms
    /// (<c>descriptionScreen.ignore</c>; empty when the policy did not load).</summary>
    public IReadOnlyList<string> ScreenIgnore => _screenIgnore;

    /// <summary>Parses and validates policy JSON. Never throws.</summary>
    public static JobPolicy Parse(string json)
    {
        // JsonDocument would read a lone surrogate as U+FFFD and load the file; the Mac refuses it.
        if (HasLoneSurrogate(json))
        {
            return new JobPolicy("policy.json: it has text that is not valid Unicode (a lone surrogate)");
        }

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            return Build(doc.RootElement);
        }
        catch (JsonException ex)
        {
            return new JobPolicy($"policy.json is not valid JSON: {ex.Message}");
        }
        catch (PolicyFormatException ex)
        {
            return new JobPolicy($"policy.json: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            // A pattern that does not compile.
            return new JobPolicy($"policy.json: bad regular expression: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            // A string escape such as \ud800 that is not valid Unicode.
            return new JobPolicy($"policy.json: it has text that cannot be read: {ex.Message}");
        }
    }

    private static JobPolicy LoadEmbedded()
    {
        try
        {
            using var stream = typeof(JobPolicy).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return new JobPolicy("policy.json is not embedded in the agent");
            }

            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or DecoderFallbackException)
        {
            return new JobPolicy($"policy.json cannot be read: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static JobPolicy Build(JsonElement root)
    {
        var top = Obj(root, "the file", TopKeys);

        var versionEl = top["version"];
        if (versionEl.ValueKind != JsonValueKind.Number || !versionEl.TryGetInt32(out var version) || version < 1)
        {
            throw new PolicyFormatException("version must be a positive integer");
        }

        var namePattern = Compile(Str(top["namePattern"], "namePattern"));

        var maxEl = top["maxArgLength"];
        if (maxEl.ValueKind != JsonValueKind.Number || !maxEl.TryGetInt32(out var maxArgLength) || maxArgLength < 0)
        {
            throw new PolicyFormatException("maxArgLength must be a non-negative integer");
        }

        var policy = new JobPolicy(version, namePattern, maxArgLength);

        var allow = Obj(top["allow"], "allow", AllowKeys);
        foreach (var s in UpperArray(allow["globalExact"], "allow.globalExact"))
        {
            policy._globalExact.Add(s);
        }

        // Compiled here and by JavaScript on the Mac. The two dialects differ on escapes
        // (\d, \w, \p{..}, \_) and on non-ASCII, and agree without them.
        foreach (var s in StrArray(allow["patterns"], "allow.patterns"))
        {
            if (s.Any(c => c == '\\' || c > '\x7f'))
            {
                throw new PolicyFormatException($"allow.patterns entry '{s}' must not contain a backslash or a non-ASCII character");
            }

            policy._patterns.Add(Compile(s));
        }

        var restrictedEl = allow["restrictedEcus"];
        if (restrictedEl.ValueKind != JsonValueKind.Object)
        {
            throw new PolicyFormatException("allow.restrictedEcus must be an object");
        }

        foreach (var ecu in restrictedEl.EnumerateObject())
        {
            RequireUpper(ecu.Name, "allow.restrictedEcus key");
            if (ecu.Name.Length == 0 || !policy._restricted.TryAdd(ecu.Name, new HashSet<string>(UpperArray(ecu.Value, $"allow.restrictedEcus.{ecu.Name}"), StringComparer.Ordinal)))
            {
                throw new PolicyFormatException($"allow.restrictedEcus has an empty or duplicate key '{ecu.Name}'");
            }
        }

        var deny = Obj(top["deny"], "deny", DenyKeys);
        policy._denySubstrings.AddRange(UpperArray(deny["substrings"], "deny.substrings"));
        policy._denyTokens.AddRange(UpperArray(deny["tokens"], "deny.tokens"));

        var screen = Obj(top["descriptionScreen"], "descriptionScreen", ScreenKeys);
        policy._screenTerms.AddRange(StrArray(screen["terms"], "descriptionScreen.terms"));
        if (policy._screenTerms.Count == 0)
        {
            throw new PolicyFormatException("descriptionScreen.terms must not be empty");
        }

        // Lower-case and each listed once, so an entry reads as what it removes. Checked the
        // same way as the Mac's parser (policy.ts) and policy/policy-parse-cases.json.
        foreach (var s in StrArray(screen["ignore"], "descriptionScreen.ignore"))
        {
            if (!string.Equals(s, s.ToLowerInvariant(), StringComparison.Ordinal))
            {
                throw new PolicyFormatException($"descriptionScreen.ignore entry '{s}' must be lower-case");
            }

            if (policy._screenIgnore.Contains(s, StringComparer.Ordinal))
            {
                throw new PolicyFormatException($"descriptionScreen.ignore lists '{s}' twice");
            }

            policy._screenIgnore.Add(s);
        }

        return policy;
    }

    /// <summary>
    /// Evaluates one ECU/job (docs/api.md "Job policy"). <paramref name="args"/> and
    /// <paramref name="results"/> are null when absent.
    /// </summary>
    public PolicyVerdict Evaluate(string? ecu, string? job, string? args = null, string? results = null)
    {
        if (!Loaded)
        {
            return Refuse(RuleUnavailable);
        }

        // 1. Shape. Non-ASCII never reaches upper-casing because the name pattern is ASCII.
        if (!FullMatch(_namePattern!, ecu))
        {
            return Refuse("shape.ecu");
        }

        if (!FullMatch(_namePattern!, job))
        {
            return Refuse("shape.job");
        }

        if (args is not null && !PrintableAscii(args, _maxArgLength))
        {
            return Refuse("shape.args");
        }

        if (results is not null && !PrintableAscii(results, _maxArgLength))
        {
            return Refuse("shape.results");
        }

        // 2. Upper-case.
        var e = ecu!.ToUpperInvariant();
        var j = job!.ToUpperInvariant();

        // 3. Deny: substrings, then whole _-separated tokens. Overrides every allow rule.
        foreach (var s in _denySubstrings)
        {
            if (j.Contains(s, StringComparison.Ordinal))
            {
                return new PolicyVerdict(false, "deny.substring:" + s, false, e, j);
            }
        }

        var tokens = j.Split('_', StringSplitOptions.RemoveEmptyEntries);
        foreach (var t in _denyTokens)
        {
            if (Array.IndexOf(tokens, t) >= 0)
            {
                return new PolicyVerdict(false, "deny.token:" + t, false, e, j);
            }
        }

        // 4. Restricted ECU: its own list and nothing else.
        if (_restricted.TryGetValue(e, out var only))
        {
            return only.Contains(j)
                ? new PolicyVerdict(true, "allow.ecu", false, e, j)
                : new PolicyVerdict(false, "ecu_restricted", false, e, j);
        }

        // 5. Global exact names.
        if (_globalExact.Contains(j))
        {
            return new PolicyVerdict(true, "allow.global", false, e, j);
        }

        // 6. Patterns: allowed, but the description must be screened.
        foreach (var p in _patterns)
        {
            if (FullMatch(p, j))
            {
                return new PolicyVerdict(true, "allow.pattern", true, e, j);
            }
        }

        // 7.
        return new PolicyVerdict(false, "default_deny", false, e, j);

        static PolicyVerdict Refuse(string rule) => new(false, rule, false, null, null);
    }

    /// <summary>
    /// Whole-string match. .NET's <c>$</c> also matches before a trailing newline, so the match
    /// must cover every character rather than trusting the anchors.
    /// </summary>
    private static bool FullMatch(Regex regex, string? value)
    {
        if (value is null)
        {
            return false;
        }

        try
        {
            var m = regex.Match(value);
            return m.Success && m.Index == 0 && m.Length == value.Length;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool PrintableAscii(string value, int maxLength)
    {
        if (value.Length > maxLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c < ' ' || c > '~')
            {
                return false;
            }
        }

        return true;
    }

    private static Regex Compile(string pattern) =>
        new(pattern, RegexOptions.CultureInvariant, RegexTimeout);

    private static Dictionary<string, JsonElement> Obj(JsonElement el, string where, string[] keys)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            throw new PolicyFormatException($"{where} must be an object");
        }

        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in el.EnumerateObject())
        {
            if (Array.IndexOf(keys, p.Name) < 0)
            {
                throw new PolicyFormatException($"{where} has an unknown key '{p.Name}'; this agent does not understand it");
            }

            if (!map.TryAdd(p.Name, p.Value))
            {
                throw new PolicyFormatException($"{where} has a duplicate key '{p.Name}'");
            }
        }

        foreach (var k in keys)
        {
            if (!map.ContainsKey(k))
            {
                throw new PolicyFormatException($"{where} is missing '{k}'");
            }
        }

        return map;
    }

    private static string Str(JsonElement el, string where)
    {
        if (el.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(el.GetString()))
        {
            throw new PolicyFormatException($"{where} must be a non-empty string");
        }

        return el.GetString()!;
    }

    private static List<string> StrArray(JsonElement el, string where)
    {
        if (el.ValueKind != JsonValueKind.Array)
        {
            throw new PolicyFormatException($"{where} must be an array of strings");
        }

        return el.EnumerateArray().Select(x => Str(x, where + " entry")).ToList();
    }

    /// <summary>Names are compared with the upper-cased ECU and job exactly as written, so an
    /// entry that is not upper-case could never match: refused rather than left dead.</summary>
    private static List<string> UpperArray(JsonElement el, string where)
    {
        var list = StrArray(el, where);
        foreach (var s in list)
        {
            RequireUpper(s, where + " entry");
        }

        return list;
    }

    /// <summary>Unchanged by upper-casing and no lower-case letter (such as ß, which has no
    /// single upper-case form). The Mac's parser applies the same two tests.</summary>
    private static void RequireUpper(string s, string where)
    {
        if (!string.Equals(s, s.ToUpperInvariant(), StringComparison.Ordinal) || s.EnumerateRunes().Any(Rune.IsLower))
        {
            throw new PolicyFormatException($"{where} '{s}' must be upper-case");
        }
    }

    private static bool HasLoneSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(s[i]))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class PolicyFormatException(string message) : Exception(message);
}
