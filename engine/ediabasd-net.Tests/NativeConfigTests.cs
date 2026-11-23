using EdiabasLib.Native.Tests;

namespace Ediabasd.Tests;

public sealed class NativeConfigTests : IDisposable
{
    readonly string _home = Directory.CreateTempSubdirectory("ediabasd-home-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    Dictionary<string, string?> Env(string? xdgConfig = null, string? xdgState = null) => new()
    {
        ["HOME"] = _home,
        ["XDG_CONFIG_HOME"] = xdgConfig,
        ["XDG_STATE_HOME"] = xdgState,
    };

    string WriteConfig(string json, string? dir = null)
    {
        dir ??= Path.Combine(_home, ".config", NativeConfig.AppDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    static string Json(string extra = "") =>
        "{\"ecuPath\": " + System.Text.Json.JsonSerializer.Serialize(TestPaths.UpstreamTestEcu) + extra + "}";

    [Fact]
    public void Defaults()
    {
        WriteConfig(Json());
        var (c, errors) = NativeConfig.Load(null, Env());
        Assert.Empty(errors);
        Assert.NotNull(c);
        Assert.Equal(30, c!.JobTimeoutSeconds);
        Assert.Equal(60, c.MaxJobTimeoutSeconds);
        Assert.Null(c.Port);
        Assert.Empty(c.ReviewedJobs);
        Assert.Equal(Path.Combine(_home, ".local", "state", NativeConfig.AppDir, "audit.log"), c.AuditPath);
        Assert.Equal(Path.Combine(_home, ".local", "state", NativeConfig.AppDir, "variants.json"), c.VariantsPath);
    }

    [Fact]
    public void LatencyMs_DefaultsTo1()
    {
        WriteConfig(Json());
        var (c, errors) = NativeConfig.Load(null, Env());
        Assert.Empty(errors);
        Assert.Equal(1, c!.LatencyMs);
    }

    [Fact]
    public void LatencyMs_16_Parses()
    {
        WriteConfig(Json(", \"latencyMs\": 16"));
        var (c, errors) = NativeConfig.Load(null, Env());
        Assert.Empty(errors);
        Assert.Equal(16, c!.LatencyMs);
    }

    [Fact]
    public void LatencyMs_Other_IsError()
    {
        WriteConfig(Json(", \"latencyMs\": 5"));
        var (c, errors) = NativeConfig.Load(null, Env());
        Assert.Null(c);
        Assert.Contains(errors, e => e.Contains("latencyMs: must be 1 or 16"));
    }

    [Fact]
    public void MaxJobTimeoutAbove60_IsError()
    {
        WriteConfig(Json(", \"maxJobTimeoutSeconds\": 61"));
        var (c, errors) = NativeConfig.Load(null, Env());
        Assert.Null(c);
        Assert.Contains(errors, e => e.Contains("maxJobTimeoutSeconds"));
    }

    [Fact]
    public void JobTimeoutAboveMax_IsError()
    {
        WriteConfig(Json(", \"jobTimeoutSeconds\": 50, \"maxJobTimeoutSeconds\": 40"));
        var (_, errors) = NativeConfig.Load(null, Env());
        Assert.Contains(errors, e => e.Contains("jobTimeoutSeconds"));
    }

    [Fact]
    public void MissingEcuPath_IsError()
    {
        WriteConfig("{}");
        var (_, errors) = NativeConfig.Load(null, Env());
        Assert.Contains(errors, e => e.Contains("ecuPath"));
    }

    [Fact]
    public void EcuPathThatDoesNotExist_IsError()
    {
        WriteConfig("{\"ecuPath\": \"/nonexistent/ecu\"}");
        var (_, errors) = NativeConfig.Load(null, Env());
        Assert.Contains(errors, e => e.Contains("ecuPath"));
    }

    [Fact]
    public void MissingFile_IsError()
    {
        var (_, errors) = NativeConfig.Load(null, Env());
        Assert.Contains(errors, e => e.Contains("config.json"));
    }

    [Fact]
    public void XdgDirectories_AreHonoured()
    {
        var cfg = Path.Combine(_home, "cfg");
        var state = Path.Combine(_home, "st");
        WriteConfig(Json(), Path.Combine(cfg, NativeConfig.AppDir));
        var (c, errors) = NativeConfig.Load(null, Env(cfg, state));
        Assert.Empty(errors);
        Assert.Equal(Path.Combine(state, NativeConfig.AppDir, "audit.log"), c!.AuditPath);
    }

    [Fact]
    public void ExplicitPath_Wins()
    {
        var path = WriteConfig(Json(), Path.Combine(_home, "elsewhere"));
        var (c, errors) = NativeConfig.Load(path, Env());
        Assert.Empty(errors);
        Assert.Equal(path, c!.ConfigPath);
    }

    [Fact]
    public void UnknownKey_IsError()
    {
        WriteConfig(Json(", \"reviewJobs\": []"));
        var (_, errors) = NativeConfig.Load(null, Env());
        Assert.Contains(errors, e => e.Contains("reviewJobs"));
    }

    [Fact]
    public void PortReviewedJobsAndDirs_Parse()
    {
        WriteConfig(Json(", \"port\": \"/dev/cu.usbserial-X\", \"reviewedJobs\": [\"NVE_65.STATUS_X\"], \"traceDir\": \"/tmp/t\", \"recordDir\": \"/tmp/r\""));
        var (c, errors) = NativeConfig.Load(null, Env());
        Assert.Empty(errors);
        Assert.Equal("/dev/cu.usbserial-X", c!.Port);
        Assert.Equal("NVE_65.STATUS_X", Assert.Single(c.ReviewedJobs));
        Assert.Equal("/tmp/t", c.TraceDir);
        Assert.Equal("/tmp/r", c.RecordDir);
    }

    [Fact]
    public void VoltageSources_IsAnUnknownKey()
    {
        // Vehicle status is a job batch run by the MCP server; voltageSources lives in its
        // config.local.json (main, 0.5.0: /ediabas/vehicle removed).
        WriteConfig(Json(", \"voltageSources\": [{\"ecu\": \"NVE_65\", \"job\": \"STATUS_SPANNUNG\"}]"));
        var (c, errors) = NativeConfig.Load(null, Env());
        Assert.Null(c);
        Assert.Contains(errors, e => e.Contains("unknown key \"voltageSources\""));
    }

    [Fact]
    public void InvalidJson_IsError()
    {
        WriteConfig("{nope");
        var (_, errors) = NativeConfig.Load(null, Env());
        Assert.Contains(errors, e => e.Contains("JSON"));
    }
}
