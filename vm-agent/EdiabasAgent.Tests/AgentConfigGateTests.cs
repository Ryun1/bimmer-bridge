using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

/// <summary><c>telegramCheck</c>, <c>ediabasInterface</c> and <c>requireGate</c>.</summary>
public class AgentConfigGateTests
{
    [Fact]
    public void Defaults()
    {
        var c = AgentConfig.Parse("{}").Config!;
        Assert.Equal("enforce", c.TelegramCheck);
        Assert.Null(c.EdiabasInterface);
        Assert.False(c.RequireGate);
        Assert.False(c.UsesGate);
        Assert.Empty(c.Warnings);
    }

    [Theory]
    [InlineData("enforce")]
    [InlineData("report")]
    [InlineData("off")]
    public void TelegramCheck_Valid(string mode)
    {
        var r = AgentConfig.Parse($"{{\"telegramCheck\": \"{mode}\"}}");
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        Assert.Equal(mode, r.Config!.TelegramCheck);
        Assert.Empty(r.Warnings);
    }

    [Theory]
    [InlineData("\"Enforce\"")]
    [InlineData("\"on\"")]
    [InlineData("\"\"")]
    [InlineData("\"enforce \"")]
    [InlineData("null")]
    public void TelegramCheck_Invalid(string value)
    {
        var r = AgentConfig.Parse($"{{\"telegramCheck\": {value}}}");
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.StartsWith("telegramCheck:", StringComparison.Ordinal));
    }

    [Fact]
    public void TelegramCheck_NotAString_IsInvalid()
    {
        Assert.False(AgentConfig.Parse("{\"telegramCheck\": true}").Ok);
    }

    [Theory]
    [InlineData("GATE:OBD", true)]
    [InlineData("STD:OBD", false)]
    [InlineData("gate:OBD", false)]
    [InlineData("A_1:B_2", false)]
    [InlineData("ABCDEFGHIJKLMNOP:ABCDEFGHIJKLMNOP", false)]
    public void EdiabasInterface_Valid(string value, bool gate)
    {
        var r = AgentConfig.Parse($"{{\"ediabasInterface\": \"{value}\"}}");
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        Assert.Equal(value, r.Config!.EdiabasInterface);
        Assert.Equal(gate, r.Config.UsesGate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("GATE")]
    [InlineData("GATE:")]
    [InlineData(":OBD")]
    [InlineData("GATE:OBD:X")]
    [InlineData("GATE: OBD")]
    [InlineData("GATE:OBD\\n")]
    [InlineData("GATE:OB-D")]
    [InlineData("GATE:ÖBD")]
    [InlineData("ABCDEFGHIJKLMNOPQ:OBD")]
    [InlineData("GATE:ABCDEFGHIJKLMNOPQ")]
    [InlineData("..\\\\x:OBD")]
    public void EdiabasInterface_Invalid(string value)
    {
        var r = AgentConfig.Parse($"{{\"ediabasInterface\": \"{value}\"}}");
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.StartsWith("ediabasInterface:", StringComparison.Ordinal));
    }

    [Fact]
    public void EdiabasInterface_Null_IsTheDefault()
    {
        var r = AgentConfig.Parse("{\"ediabasInterface\": null}");
        Assert.True(r.Ok);
        Assert.Null(r.Config!.EdiabasInterface);
    }

    [Fact]
    public void RequireGate_ParsesAndIsKnown()
    {
        var r = AgentConfig.Parse("{\"requireGate\": true, \"ediabasInterface\": \"GATE:OBD\"}");
        Assert.True(r.Ok);
        Assert.True(r.Config!.RequireGate);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void RequireGate_WithoutAGate_IsStillAValidConfig()
    {
        // The agent starts and says why it refuses, rather than not starting.
        var r = AgentConfig.Parse("{\"requireGate\": true}");
        Assert.True(r.Ok);
        Assert.False(r.Config!.UsesGate);
    }
}
