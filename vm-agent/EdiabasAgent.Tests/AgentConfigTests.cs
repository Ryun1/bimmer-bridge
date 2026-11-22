using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

public class AgentConfigTests
{
    private static AgentConfig LoadOk(string json)
    {
        var result = AgentConfig.Parse(json);
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.NotNull(result.Config);
        return result.Config!;
    }

    private static IReadOnlyList<string> LoadErrors(string json)
    {
        var result = AgentConfig.Parse(json);
        Assert.False(result.Ok);
        Assert.Null(result.Config);
        Assert.NotEmpty(result.Errors);
        return result.Errors;
    }

    [Fact]
    public void EmptyObject_TakesAllDefaults()
    {
        var c = LoadOk("{}");
        Assert.Equal("auto", c.BindAddress);
        Assert.Equal(8765, c.Port);
        Assert.Equal(@"C:\ProgramData\EdiabasBridge\token.txt", c.TokenFile);
        Assert.Equal(@"C:\EDIABAS\BIN\api32.dll", c.EdiabasDll);
        Assert.Equal(@"C:\EDIABAS\ECU", c.EcuPath);
        Assert.Equal(@"C:\ProgramData\EdiabasBridge\logs", c.LogDirectory);
        Assert.Equal(new[] { @"C:\EDIABAS\TRACE", @"C:\NCSEXPER\WORK" }, c.LogRoots);
        Assert.Equal(new[] { "INPALOAD", "TOOL32", "NCSEXPER", "WINKFPT", "BMW Coding Tool", "WINKFP", "ISTAGUI" }, c.ConflictProcesses);
        Assert.Empty(c.VoltageSources);
        Assert.Empty(c.ReviewedJobs);
        Assert.Equal(30, c.JobTimeoutSeconds);
        Assert.Equal(60, c.MaxJobTimeoutSeconds);
        Assert.Equal(20, c.EdiabasIdleReleaseSeconds);
    }

    [Fact]
    public void Defaults_ConflictProcesses_IncludeWinkfpAndIsta()
    {
        var c = LoadOk("{}");
        Assert.Equal(
            new[] { "INPALOAD", "TOOL32", "NCSEXPER", "WINKFPT", "BMW Coding Tool", "WINKFP", "ISTAGUI" },
            c.ConflictProcesses);
    }

    [Fact]
    public void Defaults_AreValid()
    {
        Assert.Empty(new AgentConfig().Validate());
    }

    [Fact]
    public void PlanExample_Loads()
    {
        const string json = """
        {
          "bindAddress": "auto",
          "port": 8765,
          "tokenFile": "C:\\ProgramData\\EdiabasBridge\\token.txt",
          "ediabasDll": "C:\\EDIABAS\\BIN\\api32.dll",
          "ecuPath": "C:\\EDIABAS\\ECU",
          "logRoots": ["C:\\EDIABAS\\TRACE", "C:\\NCSEXPER\\WORK"],
          "conflictProcesses": ["INPALOAD", "TOOL32", "NCSEXPER", "WINKFPT", "BMW Coding Tool"],
          "voltageSources": [ { "ecu": "NVE_65", "job": "STATUS_BORDNETZSPANNUNG" } ],
          "reviewedJobs": [],
          "jobTimeoutSeconds": 30,
          "maxJobTimeoutSeconds": 60
        }
        """;
        var c = LoadOk(json);
        Assert.Equal(8765, c.Port);
    }

    [Fact]
    public void PartialConfig_MissingFieldsTakeDefaults()
    {
        var c = LoadOk("""{ "port": 9000, "logRoots": ["D:\\x"] }""");
        Assert.Equal(9000, c.Port);
        Assert.Equal(new[] { @"D:\x" }, c.LogRoots);
        Assert.Equal("auto", c.BindAddress);
        Assert.Equal(30, c.JobTimeoutSeconds);
    }

    [Fact]
    public void CommentsAndTrailingCommas_Tolerated()
    {
        const string json = """
        {
          // line comment
          "port": 8766, /* block comment */
          "conflictProcesses": ["A", "B",],
        }
        """;
        var c = LoadOk(json);
        Assert.Equal(8766, c.Port);
        Assert.Equal(new[] { "A", "B" }, c.ConflictProcesses);
    }

    [Fact]
    public void UnknownProperties_Ignored()
    {
        var c = LoadOk("""{ "somethingNew": { "deep": [1,2,3] }, "port": 8765 }""");
        Assert.Equal(8765, c.Port);
    }

    // A typo in a key is otherwise silent: "reviewJobs" would leave every approval unused.
    [Fact]
    public void UnknownTopLevelKey_IsAWarning_LoadStillSucceeds()
    {
        var r = AgentConfig.Parse("""{ "reviewJobs": ["NVE_65:STATUS_X"], "port": 8765, /* note */ }""");
        Assert.True(r.Ok);
        Assert.Empty(r.Config!.ReviewedJobs);
        var w = Assert.Single(r.Warnings);
        Assert.Contains("\"reviewJobs\"", w);
        Assert.Contains("ignored", w);
        Assert.Equal(r.Warnings, r.Config.Warnings);
    }

    [Fact]
    public void KnownKeys_AnyCase_NoWarnings()
    {
        var r = AgentConfig.Parse("""{ "Port": 8765, "reviewedJobs": [], "EDIABASIDLERELEASESECONDS": 20, "voltageSources": [ { "ecu": "A", "job": "B", "extra": 1 } ] }""");
        Assert.True(r.Ok);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Defaults_NoWarnings()
    {
        Assert.Empty(AgentConfig.Parse("{}").Warnings);
        Assert.Empty(new AgentConfig().Warnings);
    }

    [Fact]
    public void LogRootThePathRulesRefuse_IsAWarning()
    {
        var r = AgentConfig.Parse("""{ "logRoots": ["C:/EDIABAS/TRACE", "C:\\NCSEXPER\\WORK\\", "C:\\"] }""");
        Assert.True(r.Ok);
        Assert.Equal(2, r.Warnings.Count);
        Assert.Contains("logRoots[0]", r.Warnings[0]);
        Assert.Contains("C:/EDIABAS/TRACE", r.Warnings[0]);
        Assert.Contains(PathRules.ForwardSlash, r.Warnings[0]);
        Assert.Contains("logRoots[2]", r.Warnings[1]);
    }

    [Fact]
    public void ConfigThatFails_HasNoWarnings()
    {
        var r = AgentConfig.Parse("""{ "port": 0, "reviewJobs": [] }""");
        Assert.False(r.Ok);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Utf8Bom_Tolerated()
    {
        var c = LoadOk("\uFEFF{ \"port\": 8770 }");
        Assert.Equal(8770, c.Port);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(100000)]
    public void Port_OutOfRange_Rejected(int port)
    {
        var errors = LoadErrors($$"""{ "port": {{port}} }""");
        Assert.Contains(errors, e => e.Contains("port", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public void Port_Boundaries_Accepted(int port)
    {
        Assert.Equal(port, LoadOk($$"""{ "port": {{port}} }""").Port);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("*")]
    [InlineData("+")]
    [InlineData("::")]
    [InlineData("[::]")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost")]
    [InlineData("vm.local")]
    [InlineData("example.com")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("127.1")]
    [InlineData("0x7f.0.0.1")]
    [InlineData("192.168.64.300")]
    [InlineData("192.168.064.3")]
    [InlineData("192.168.64.3:8765")]
    [InlineData("http://192.168.64.3")]
    [InlineData(" 192.168.64.3")]
    [InlineData("192.168.64.3 ")]
    [InlineData("0.1.2.3")]
    [InlineData("255.255.255.255")]
    [InlineData("224.0.0.1")]
    [InlineData("AUTO ")]
    public void BindAddress_Forbidden_Rejected(string value)
    {
        var json = "{ \"bindAddress\": " + System.Text.Json.JsonSerializer.Serialize(value) + " }";
        var errors = LoadErrors(json);
        Assert.Contains(errors, e => e.Contains("bindAddress", StringComparison.Ordinal));
    }

    [Fact]
    public void BindAddress_Null_Rejected()
    {
        var errors = LoadErrors("""{ "bindAddress": null }""");
        Assert.Contains(errors, e => e.Contains("bindAddress", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("Auto")]
    [InlineData("192.168.64.3")]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    public void BindAddress_Allowed(string value)
    {
        var c = LoadOk("{ \"bindAddress\": \"" + value + "\" }");
        Assert.Equal(value, c.BindAddress);
    }

    [Fact]
    public void IsAutoBind_RecognisesAuto()
    {
        Assert.True(new AgentConfig { BindAddress = "AUTO" }.IsAutoBind);
        Assert.False(new AgentConfig { BindAddress = "10.0.0.1" }.IsAutoBind);
    }

    [Theory]
    [InlineData("jobTimeoutSeconds", 0)]
    [InlineData("jobTimeoutSeconds", -5)]
    [InlineData("maxJobTimeoutSeconds", 0)]
    [InlineData("maxJobTimeoutSeconds", -1)]
    public void Timeouts_MustBePositive(string field, int value)
    {
        var errors = LoadErrors($$"""{ "{{field}}": {{value}} }""");
        Assert.Contains(errors, e => e.Contains(field, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(600)]
    public void IdleRelease_InRange_Loads(int value)
    {
        Assert.Equal(value, LoadOk($$"""{ "ediabasIdleReleaseSeconds": {{value}} }""").EdiabasIdleReleaseSeconds);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(601)]
    [InlineData(0)]
    [InlineData(-1)]
    public void IdleRelease_OutOfRange_IsAnError(int value)
    {
        var errors = LoadErrors($$"""{ "ediabasIdleReleaseSeconds": {{value}} }""");
        Assert.Contains(errors, e => e.StartsWith("ediabasIdleReleaseSeconds:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(61)]
    [InlineData(75)]
    [InlineData(120)]
    public void MaxJobTimeout_AboveTheLimit_IsAnError(int value)
    {
        // The Mac gives up on an EDIABAS call after 75 s; a request may take the maximum plus
        // the 2 s apiBreak grace, so the maximum stays at 60.
        var errors = LoadErrors($$"""{ "maxJobTimeoutSeconds": {{value}} }""");
        Assert.Contains(errors, e => e.StartsWith("maxJobTimeoutSeconds:", StringComparison.Ordinal) && e.Contains("60", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    public void MaxJobTimeout_UpToTheLimit_Loads(int value)
    {
        Assert.Equal(value, LoadOk($$"""{ "jobTimeoutSeconds": 1, "maxJobTimeoutSeconds": {{value}} }""").MaxJobTimeoutSeconds);
    }

    [Fact]
    public void JobTimeout_MustNotExceedMax()
    {
        var errors = LoadErrors("""{ "jobTimeoutSeconds": 61, "maxJobTimeoutSeconds": 60 }""");
        Assert.Contains(errors, e => e.Contains("jobTimeoutSeconds", StringComparison.Ordinal));
        LoadOk("""{ "jobTimeoutSeconds": 60, "maxJobTimeoutSeconds": 60 }""");
    }

    [Fact]
    public void MultipleErrors_ReportedTogether()
    {
        var errors = LoadErrors("""{ "port": 0, "bindAddress": "0.0.0.0", "jobTimeoutSeconds": -1 }""");
        Assert.True(errors.Count >= 3, string.Join("; ", errors));
        Assert.Contains(errors, e => e.Contains("port", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("bindAddress", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("jobTimeoutSeconds", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("tokenFile")]
    [InlineData("ediabasDll")]
    [InlineData("ecuPath")]
    [InlineData("logDirectory")]
    public void RequiredPaths_MustNotBeEmpty(string field)
    {
        var errors = LoadErrors($$"""{ "{{field}}": "" }""");
        Assert.Contains(errors, e => e.Contains(field, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("logRoots")]
    [InlineData("conflictProcesses")]
    [InlineData("voltageSources")]
    [InlineData("reviewedJobs")]
    [InlineData("tokenFile")]
    public void NullValues_Rejected(string field)
    {
        var errors = LoadErrors($$"""{ "{{field}}": null }""");
        Assert.Contains(errors, e => e.Contains(field, StringComparison.Ordinal));
    }

    [Fact]
    public void ConflictProcesses_EmptyEntry_Rejected()
    {
        var errors = LoadErrors("""{ "conflictProcesses": ["INPALOAD", " "] }""");
        Assert.Contains(errors, e => e.Contains("conflictProcesses", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{ \"port\": \"abc\" }")]
    [InlineData("")]
    [InlineData("null")]
    public void MalformedJson_ReportedAsError(string json)
    {
        LoadErrors(json);
    }

    [Fact]
    public void Load_MissingFile_ReportedAsError()
    {
        var result = AgentConfig.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "config.json"));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("config", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Load_File_Works()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{ \"port\": 8799 }");
        try
        {
            var result = AgentConfig.Load(path);
            Assert.True(result.Ok);
            Assert.Equal(8799, result.Config!.Port);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
