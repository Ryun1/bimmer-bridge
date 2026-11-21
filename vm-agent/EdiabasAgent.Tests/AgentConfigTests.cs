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
        Assert.Equal(new[] { "INPALOAD", "TOOL32", "NCSEXPER", "WINKFPT", "BMW Coding Tool" }, c.ConflictProcesses);
        var vs = Assert.Single(c.VoltageSources);
        Assert.Equal("NVE_65", vs.Ecu);
        Assert.Equal("STATUS_BORDNETZSPANNUNG", vs.Job);
        Assert.Empty(c.ReviewedJobs);
        Assert.Equal(30, c.JobTimeoutSeconds);
        Assert.Equal(60, c.MaxJobTimeoutSeconds);
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
