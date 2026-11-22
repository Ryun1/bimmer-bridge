using System.Text.Json;
using EdiabasLib.Native.Tests;

namespace Ediabasd.Tests;

public sealed class CliTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ediabasd-cli-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string Config(string extra = "")
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{\"ecuPath\": " + JsonSerializer.Serialize(TestPaths.UpstreamTestEcu) + extra + "}");
        return path;
    }

    (int Exit, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var env = new Dictionary<string, string?> { ["HOME"] = _dir, ["XDG_STATE_HOME"] = Path.Combine(_dir, "state") };
        var exit = Cli.Run(args, new StringReader(""), o, e, env);
        return (exit, o.ToString(), e.ToString());
    }

    [Fact]
    public void Describe_NoComments_PrintsJson()
    {
        var (exit, output, err) = Run("describe", "cmd_test2", "--no-comments", "--config", Config());
        Assert.True(exit == 0, err);
        Assert.Equal("CMD_TEST2", JsonDocument.Parse(output).RootElement.GetProperty("ecu").GetString());
    }

    [Fact]
    public void Job_WithoutArgs_IsUsage()
    {
        Assert.Equal(2, Run("job", "--config", Config()).Exit);
    }

    [Fact]
    public void UnknownCommand_IsUsage()
    {
        Assert.Equal(2, Run("flash", "--config", Config()).Exit);
    }

    [Fact]
    public void BadConfig_Is78()
    {
        var (exit, _, err) = Run("describe", "cmd_test2", "--config", Config(", \"maxJobTimeoutSeconds\": 99"));
        Assert.Equal(78, exit);
        Assert.Contains("maxJobTimeoutSeconds", err);
    }

    [Fact]
    public void ErrorResponse_Exits1()
    {
        var (exit, output, _) = Run("job", "cmd_test2", "TEST_IFACE_FLAGS", "--config", Config());
        Assert.Equal(1, exit);
        Assert.Equal("job_not_allowed", JsonDocument.Parse(output).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Health_Exits0()
    {
        var (exit, output, _) = Run("health", "--config", Config());
        Assert.Equal(0, exit);
        Assert.Equal("ediabasd-net", JsonDocument.Parse(output).RootElement.GetProperty("engine").GetString());
    }
}
