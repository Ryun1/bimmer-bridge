using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdiabasAgent.Core;

/// <summary>One entry of <c>voltageSources</c> (used from Phase 2 on).</summary>
public sealed class VoltageSource
{
    public string Ecu { get; set; } = "";
    public string Job { get; set; } = "";
}

/// <summary>Result of loading a config file: either a validated config or every error found.</summary>
public sealed class ConfigLoadResult
{
    private ConfigLoadResult(AgentConfig? config, IReadOnlyList<string> errors)
    {
        Config = config;
        Errors = errors;
    }

    public AgentConfig? Config { get; }
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Problems that do not stop the agent (an unknown key, a log root that grants
    /// nothing). Empty when the load failed.</summary>
    public IReadOnlyList<string> Warnings => Config?.Warnings ?? [];

    public bool Ok => Config is not null && Errors.Count == 0;

    internal static ConfigLoadResult Success(AgentConfig config) => new(config, []);
    internal static ConfigLoadResult Failure(IReadOnlyList<string> errors) => new(null, errors);
}

/// <summary>
/// The agent's configuration (<c>C:\ProgramData\EdiabasBridge\config.json</c>).
/// Missing properties take the defaults below; unknown top-level properties are ignored with a
/// warning; comments and trailing commas are tolerated.
/// </summary>
public sealed class AgentConfig
{
    public const string DefaultConfigPath = @"C:\ProgramData\EdiabasBridge\config.json";
    public const string DefaultLogDirectory = @"C:\ProgramData\EdiabasBridge\logs";
    public const string AutoBind = "auto";

    public string BindAddress { get; set; } = AutoBind;
    public int Port { get; set; } = 8765;
    public string TokenFile { get; set; } = @"C:\ProgramData\EdiabasBridge\token.txt";
    public string EdiabasDll { get; set; } = @"C:\EDIABAS\BIN\api32.dll";
    public string EcuPath { get; set; } = @"C:\EDIABAS\ECU";
    public string LogDirectory { get; set; } = DefaultLogDirectory;
    public List<string> LogRoots { get; set; } = [@"C:\EDIABAS\TRACE", @"C:\NCSEXPER\WORK"];
    public List<string> ConflictProcesses { get; set; } = ["INPALOAD", "TOOL32", "NCSEXPER", "WINKFPT", "BMW Coding Tool"];
    public List<VoltageSource> VoltageSources { get; set; } = [new VoltageSource { Ecu = "NVE_65", Job = "STATUS_BORDNETZSPANNUNG" }];
    public List<string> ReviewedJobs { get; set; } = [];
    public int JobTimeoutSeconds { get; set; } = 30;
    public int MaxJobTimeoutSeconds { get; set; } = 60;

    /// <summary>Upper bound of <see cref="MaxJobTimeoutSeconds"/>. The Mac gives up on an EDIABAS
    /// call after 75 s; a request can last the maximum plus the 2 s apiBreak grace, so 60 keeps
    /// it inside that with room for loading the DLL and reading results.</summary>
    public const int MaxJobTimeoutLimitSeconds = 60;

    /// <summary>Seconds without an EDIABAS request after which api32.dll is unloaded, so that
    /// EDIABAS's ebas32.exe can exit (docs/api.md, "Idle release").</summary>
    public int EdiabasIdleReleaseSeconds { get; set; } = 20;

    public const int MinIdleReleaseSeconds = 5;
    public const int MaxIdleReleaseSeconds = 600;

    /// <summary>Set by <see cref="Parse"/>; logged when the agent starts.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    [JsonIgnore]
    public bool IsAutoBind => string.Equals(BindAddress, AutoBind, StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    /// <summary>Reads and validates a config file. Never throws for I/O or parse problems.</summary>
    public static ConfigLoadResult Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ConfigLoadResult.Failure([$"config: cannot read '{path}': {ex.GetType().Name}: {ex.Message}"]);
        }

        return Parse(text);
    }

    /// <summary>Parses and validates config JSON. Never throws for bad input.</summary>
    public static ConfigLoadResult Parse(string json)
    {
        if (json.Length > 0 && json[0] == '\uFEFF')
        {
            json = json[1..];
        }

        AgentConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<AgentConfig>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            return ConfigLoadResult.Failure([$"config: invalid JSON: {ex.Message}"]);
        }

        if (config is null)
        {
            return ConfigLoadResult.Failure(["config: the file must contain a JSON object"]);
        }

        var errors = config.Validate();
        if (errors.Count > 0)
        {
            return ConfigLoadResult.Failure(errors);
        }

        config.Warnings = [.. UnknownKeys(json), .. config.LogRootWarnings(), .. config.ReviewedJobWarnings()];
        return ConfigLoadResult.Success(config);
    }

    private static readonly HashSet<string> KnownKeys = new(
        typeof(AgentConfig).GetProperties()
            .Where(p => p.CanWrite && p.SetMethod!.IsPublic && !Attribute.IsDefined(p, typeof(JsonIgnoreAttribute)))
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Top-level keys the agent does not read. A typo ("reviewJobs") would otherwise
    /// be silent and the setting would keep its default.</summary>
    private static List<string> UnknownKeys(string json)
    {
        var warnings = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return warnings;
            }

            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (!KnownKeys.Contains(p.Name))
                {
                    warnings.Add($"config: unknown key \"{Printable(p.Name)}\" is ignored (a typo?)");
                }
            }
        }
        catch (JsonException)
        {
            // The serializer accepted it; nothing more to say.
        }

        return warnings;
    }

    /// <summary>A log root the path rules refuse grants nothing and is shown missing; say so at start.</summary>
    private List<string> LogRootWarnings()
    {
        var warnings = new List<string>();
        for (var i = 0; i < LogRoots.Count; i++)
        {
            if (PathRules.Check(PathRules.TrimRoot(LogRoots[i])) is { } reason)
            {
                warnings.Add($"logRoots[{i}]: \"{Printable(LogRoots[i])}\" is not used ({reason}); write a full path such as C:\\EDIABAS\\TRACE");
            }
        }

        return warnings;
    }

    /// <summary>Returns every validation error; empty when the config is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (Port is < 1 or > 65535)
        {
            errors.Add($"port: must be between 1 and 65535 (got {Port})");
        }

        // Safety rule: bind to one literal IPv4 address or "auto". Never a wildcard, never a hostname.
        if (BindAddress is null)
        {
            errors.Add("bindAddress: must be \"auto\" or one literal IPv4 address (got null)");
        }
        else if (!IsAutoBind && !BindAddressSelector.TryParseLiteral(BindAddress, out _))
        {
            errors.Add($"bindAddress: must be \"auto\" or one literal unicast IPv4 address such as 192.168.64.3; " +
                       $"wildcards (0.0.0.0, *, +, ::), hostnames and IPv6 are not allowed (got \"{Printable(BindAddress)}\")");
        }

        RequireNonEmpty(errors, "tokenFile", TokenFile);
        RequireNonEmpty(errors, "ediabasDll", EdiabasDll);
        RequireNonEmpty(errors, "ecuPath", EcuPath);
        RequireNonEmpty(errors, "logDirectory", LogDirectory);

        RequireList(errors, "logRoots", LogRoots, allowBlankEntries: false);
        RequireList(errors, "conflictProcesses", ConflictProcesses, allowBlankEntries: false);
        RequireList(errors, "reviewedJobs", ReviewedJobs, allowBlankEntries: false);

        if (VoltageSources is null)
        {
            errors.Add("voltageSources: must be an array (got null)");
        }
        else
        {
            for (var i = 0; i < VoltageSources.Count; i++)
            {
                var v = VoltageSources[i];
                if (v is null || string.IsNullOrWhiteSpace(v.Ecu) || string.IsNullOrWhiteSpace(v.Job))
                {
                    errors.Add($"voltageSources[{i}]: needs non-empty \"ecu\" and \"job\"");
                }
            }
        }

        if (JobTimeoutSeconds <= 0)
        {
            errors.Add($"jobTimeoutSeconds: must be positive (got {JobTimeoutSeconds})");
        }

        if (MaxJobTimeoutSeconds <= 0)
        {
            errors.Add($"maxJobTimeoutSeconds: must be positive (got {MaxJobTimeoutSeconds})");
        }
        else if (MaxJobTimeoutSeconds > MaxJobTimeoutLimitSeconds)
        {
            errors.Add($"maxJobTimeoutSeconds: must be at most {MaxJobTimeoutLimitSeconds}, because the Mac stops waiting after 75 s (got {MaxJobTimeoutSeconds})");
        }

        if (JobTimeoutSeconds > 0 && MaxJobTimeoutSeconds > 0 && JobTimeoutSeconds > MaxJobTimeoutSeconds)
        {
            errors.Add($"jobTimeoutSeconds: must not exceed maxJobTimeoutSeconds ({JobTimeoutSeconds} > {MaxJobTimeoutSeconds})");
        }

        if (EdiabasIdleReleaseSeconds is < MinIdleReleaseSeconds or > MaxIdleReleaseSeconds)
        {
            errors.Add($"ediabasIdleReleaseSeconds: must be between {MinIdleReleaseSeconds} and {MaxIdleReleaseSeconds} (got {EdiabasIdleReleaseSeconds})");
        }

        return errors;
    }

    /// <summary>A <c>reviewedJobs</c> entry of the wrong shape approves nothing (it never becomes a
    /// name-only approval) and is a warning, not an error: a typo there must not stop the agent
    /// from starting after an update.</summary>
    private List<string> ReviewedJobWarnings()
    {
        var warnings = new List<string>();
        for (var i = 0; i < ReviewedJobs.Count; i++)
        {
            var entry = ReviewedJobs[i];
            if (!ApprovalList.TryParseEntry(entry, out _, out _))
            {
                warnings.Add($"reviewedJobs[{i}]: \"{Printable(entry)}\" approves nothing; write \"ECU:JOB\" or \"ECU:JOB@\" followed by {ApprovalList.HashLength} lower-case hex digits");
            }
        }

        return warnings;
    }

    private static void RequireNonEmpty(List<string> errors, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{name}: must not be empty");
        }
    }

    private static void RequireList(List<string> errors, string name, List<string>? list, bool allowBlankEntries)
    {
        if (list is null)
        {
            errors.Add($"{name}: must be an array (got null)");
            return;
        }

        if (!allowBlankEntries && list.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add($"{name}: entries must not be empty");
        }
    }

    private static string Printable(string s)
    {
        var chars = s.Select(c => char.IsControl(c) ? '?' : c).Take(64).ToArray();
        return new string(chars);
    }
}
