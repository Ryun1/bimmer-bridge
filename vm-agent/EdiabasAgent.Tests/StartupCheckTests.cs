using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

public class StartupCheckTests
{
    private const string Token = "q3Jx0b4mF8-_ZtYwLk2VnR9sPpQe7HcAaUu1dOiGxEw";

    private static ConfigLoadResult GoodConfig() => AgentConfig.Parse("{}");

    [Fact]
    public void GoodConfigAndToken_Ready()
    {
        var s = StartupCheck.Evaluate(GoodConfig(), Token + "\r\n", null);
        Assert.True(s.Ready);
        Assert.NotNull(s.Config);
        Assert.Equal(Token, s.Token);
        Assert.Null(s.Problem);
    }

    [Fact]
    public void InvalidConfig_NotReady_ReportsConfigErrors_AndIgnoresToken()
    {
        var s = StartupCheck.Evaluate(AgentConfig.Parse("""{ "port": 0, "bindAddress": "0.0.0.0" }"""), Token, null);
        Assert.False(s.Ready);
        Assert.Null(s.Config);
        Assert.Null(s.Token);
        Assert.Contains("port", s.Problem);
        Assert.Contains("bindAddress", s.Problem);
    }

    [Fact]
    public void MissingConfigFile_NotReady()
    {
        var missing = AgentConfig.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "config.json"));
        var s = StartupCheck.Evaluate(missing, Token, null);
        Assert.False(s.Ready);
        Assert.Contains("config", s.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnreadableToken_NotReady_KeepsConfig()
    {
        var s = StartupCheck.Evaluate(GoodConfig(), null, "FileNotFoundException");
        Assert.False(s.Ready);
        Assert.NotNull(s.Config);
        Assert.Null(s.Token);
        Assert.Contains("token", s.Problem!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FileNotFoundException", s.Problem!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0123456789012345678901234567890")] // 31 chars
    public void ShortToken_NotReady_DoesNotLeakToken(string content)
    {
        var s = StartupCheck.Evaluate(GoodConfig(), content, null);
        Assert.False(s.Ready);
        Assert.Null(s.Token);
        Assert.Contains("token", s.Problem!, StringComparison.OrdinalIgnoreCase);
        if (content.Trim().Length > 0)
        {
            Assert.DoesNotContain(content.Trim(), s.Problem!);
        }
    }

    [Fact]
    public void NullTokenContentWithoutError_NotReady()
    {
        Assert.False(StartupCheck.Evaluate(GoodConfig(), null, null).Ready);
    }

    [Fact]
    public void Problem_IsStableForSameInputs()
    {
        var a = StartupCheck.Evaluate(GoodConfig(), "short", null).Problem;
        var b = StartupCheck.Evaluate(GoodConfig(), "short", null).Problem;
        Assert.Equal(a, b);
    }
}

public class RepeatSuppressorTests
{
    [Fact]
    public void LogsOncePerDistinctReason()
    {
        var r = new RepeatSuppressor();
        Assert.True(r.ShouldReport("a"));
        Assert.False(r.ShouldReport("a"));
        Assert.False(r.ShouldReport("a"));
        Assert.True(r.ShouldReport("b"));
        Assert.False(r.ShouldReport("b"));
        Assert.True(r.ShouldReport("a"));
    }

    [Fact]
    public void Reset_AllowsSameReasonAgain()
    {
        var r = new RepeatSuppressor();
        Assert.True(r.ShouldReport("a"));
        r.Reset();
        Assert.True(r.ShouldReport("a"));
    }

    [Fact]
    public void NullReason_NeverReported()
    {
        var r = new RepeatSuppressor();
        Assert.False(r.ShouldReport(null));
        Assert.True(r.ShouldReport("a"));
    }
}
