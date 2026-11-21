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
    public bool Ok => Config is not null && Errors.Count == 0;

    internal static ConfigLoadResult Success(AgentConfig config) => new(config, []);
    internal static ConfigLoadResult Failure(IReadOnlyList<string> errors) => new(null, errors);
}

/// <summary>
/// The agent's configuration (<c>C:\ProgramData\EdiabasBridge\config.json</c>).
/// Missing properties take the defaults below; unknown properties are ignored;
/// comments and trailing commas are tolerated.
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
        return errors.Count == 0 ? ConfigLoadResult.Success(config) : ConfigLoadResult.Failure(errors);
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

        if (JobTimeoutSeconds > 0 && MaxJobTimeoutSeconds > 0 && JobTimeoutSeconds > MaxJobTimeoutSeconds)
        {
            errors.Add($"jobTimeoutSeconds: must not exceed maxJobTimeoutSeconds ({JobTimeoutSeconds} > {MaxJobTimeoutSeconds})");
        }

        return errors;
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
