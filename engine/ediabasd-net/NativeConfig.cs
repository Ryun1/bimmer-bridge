using System.Text.Json;
using EdiabasAgent.Core;

namespace Ediabasd;

/// <summary>
/// ediabasd's config: <c>$XDG_CONFIG_HOME/bimmer-bridge/config.json</c> (default
/// <c>~/.config/…</c>). State (audit log, variant cache) goes to
/// <c>$XDG_STATE_HOME/bimmer-bridge/</c> (default <c>~/.local/state/…</c>).
/// Unknown keys are errors: a typo must not leave a setting at its default silently.
/// </summary>
public sealed class NativeConfig
{
    public const string AppDir = "bimmer-bridge";

    public string EcuPath { get; set; } = "";

    /// The cable's device path; optional when exactly one FTDI cable is present.
    public string? Port { get; set; }

    public int JobTimeoutSeconds { get; set; } = 30;

    public int MaxJobTimeoutSeconds { get; set; } = 60;

    public List<string> ReviewedJobs { get; set; } = [];

    /// EdiabasLib's IFH/API traces of the live instance go here when set.
    public string? TraceDir { get; set; }

    /// libkline recordings of every live session go here when set.
    public string? RecordDir { get; set; }

    /// Where this config was read from (not a key).
    public string? ConfigPath { get; set; }

    /// <c>$XDG_STATE_HOME/bimmer-bridge</c> (not a key).
    public string StateDir { get; set; } = "";

    public string AuditPath => Path.Combine(StateDir, "audit.log");

    public string VariantsPath => Path.Combine(StateDir, "variants.json");

    static readonly string[] Keys =
    [
        "ecuPath", "port", "jobTimeoutSeconds", "maxJobTimeoutSeconds", "reviewedJobs", "traceDir", "recordDir",
    ];

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    sealed class Dto
    {
        public string? EcuPath { get; set; }
        public string? Port { get; set; }
        public int? JobTimeoutSeconds { get; set; }
        public int? MaxJobTimeoutSeconds { get; set; }
        public List<string>? ReviewedJobs { get; set; }
        public string? TraceDir { get; set; }
        public string? RecordDir { get; set; }
    }

    /// The default config path for this environment.
    public static string DefaultPath(IReadOnlyDictionary<string, string?> env) =>
        Path.Combine(XdgDir(env, "XDG_CONFIG_HOME", ".config"), AppDir, "config.json");

    static string XdgDir(IReadOnlyDictionary<string, string?> env, string name, string fallback)
    {
        var v = env.TryGetValue(name, out var x) ? x : null;
        if (!string.IsNullOrEmpty(v) && Path.IsPathRooted(v))
        {
            return v;
        }

        var home = env.TryGetValue("HOME", out var h) && !string.IsNullOrEmpty(h)
            ? h
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, fallback);
    }

    /// The process environment, for <see cref="Load"/>.
    public static IReadOnlyDictionary<string, string?> ProcessEnv() => new Dictionary<string, string?>
    {
        ["HOME"] = Environment.GetEnvironmentVariable("HOME"),
        ["XDG_CONFIG_HOME"] = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
        ["XDG_STATE_HOME"] = Environment.GetEnvironmentVariable("XDG_STATE_HOME"),
    };

    /// Reads and validates the config. Never throws for I/O or bad input.
    public static (NativeConfig? Config, IReadOnlyList<string> Errors) Load(string? path, IReadOnlyDictionary<string, string?> env)
    {
        path ??= DefaultPath(env);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, [$"config: cannot read {path} ({ex.GetType().Name}: {ex.Message}); create config.json with at least \"ecuPath\""]);
        }

        var errors = new List<string>();
        Dto? dto;
        try
        {
            using (var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return (null, ["config: the file must contain a JSON object"]);
                }

                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (!Keys.Contains(p.Name, StringComparer.Ordinal))
                    {
                        errors.Add($"config: unknown key \"{AuditLine.Neutralise(p.Name)}\" (keys: {string.Join(", ", Keys)})");
                    }
                }
            }

            dto = JsonSerializer.Deserialize<Dto>(text, Options);
        }
        catch (JsonException ex)
        {
            return (null, [$"config: invalid JSON: {ex.Message}"]);
        }

        if (dto is null)
        {
            return (null, ["config: the file must contain a JSON object"]);
        }

        var c = new NativeConfig
        {
            EcuPath = dto.EcuPath ?? "",
            Port = string.IsNullOrWhiteSpace(dto.Port) ? null : dto.Port,
            JobTimeoutSeconds = dto.JobTimeoutSeconds ?? 30,
            MaxJobTimeoutSeconds = dto.MaxJobTimeoutSeconds ?? 60,
            ReviewedJobs = dto.ReviewedJobs ?? [],
            TraceDir = string.IsNullOrWhiteSpace(dto.TraceDir) ? null : dto.TraceDir,
            RecordDir = string.IsNullOrWhiteSpace(dto.RecordDir) ? null : dto.RecordDir,
            ConfigPath = path,
            StateDir = Path.Combine(XdgDir(env, "XDG_STATE_HOME", Path.Combine(".local", "state")), AppDir),
        };
        errors.AddRange(c.Validate());
        return errors.Count == 0 ? (c, errors) : (null, errors);
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(EcuPath))
        {
            errors.Add("ecuPath: required (the folder holding your .prg and .grp files)");
        }
        else if (!Directory.Exists(EcuPath))
        {
            errors.Add($"ecuPath: {EcuPath} is not a folder");
        }

        if (MaxJobTimeoutSeconds is < 1 or > AgentConfig.MaxJobTimeoutLimitSeconds)
        {
            errors.Add($"maxJobTimeoutSeconds: must be 1 to {AgentConfig.MaxJobTimeoutLimitSeconds} (got {MaxJobTimeoutSeconds})");
        }

        if (JobTimeoutSeconds < 1 || JobTimeoutSeconds > MaxJobTimeoutSeconds)
        {
            errors.Add($"jobTimeoutSeconds: must be 1 to maxJobTimeoutSeconds ({MaxJobTimeoutSeconds}) (got {JobTimeoutSeconds})");
        }

        for (var i = 0; i < ReviewedJobs.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(ReviewedJobs[i]))
            {
                errors.Add($"reviewedJobs[{i}]: must not be blank");
            }
        }

        return errors;
    }
}
