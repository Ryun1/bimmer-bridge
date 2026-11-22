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
        Assert.Empty(c.VoltageSources);
        Assert.Empty(c.ReviewedJobs);
        Assert.Equal(Path.Combine(_home, ".local", "state", NativeConfig.AppDir, "audit.log"), c.AuditPath);
        Assert.Equal(Path.Combine(_home, ".local", "state", NativeConfig.AppDir, "variants.json"), c.VariantsPath);
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
    public void VoltageSourcesAndReviewedJobs_Parse()
    {
        WriteConfig(Json(", \"port\": \"/dev/cu.usbserial-X\", \"voltageSources\": [{\"ecu\": \"NVE_65\", \"job\": \"STATUS_SPANNUNG\"}], \"reviewedJobs\": [\"NVE_65.STATUS_X\"], \"traceDir\": \"/tmp/t\", \"recordDir\": \"/tmp/r\""));
        var (c, errors) = NativeConfig.Load(null, Env());
        Assert.Empty(errors);
        Assert.Equal("/dev/cu.usbserial-X", c!.Port);
        Assert.Equal("NVE_65", Assert.Single(c.VoltageSources).Ecu);
        Assert.Equal("NVE_65.STATUS_X", Assert.Single(c.ReviewedJobs));
        Assert.Equal("/tmp/t", c.TraceDir);
        Assert.Equal("/tmp/r", c.RecordDir);
    }

    [Fact]
    public void VoltageSourceWithoutJob_IsError()
    {
        WriteConfig(Json(", \"voltageSources\": [{\"ecu\": \"NVE_65\"}]"));
        var (_, errors) = NativeConfig.Load(null, Env());
        Assert.Contains(errors, e => e.Contains("voltageSources[0]"));
    }

    [Fact]
    public void InvalidJson_IsError()
    {
        WriteConfig("{nope");
        var (_, errors) = NativeConfig.Load(null, Env());
        Assert.Contains(errors, e => e.Contains("JSON"));
    }
}
