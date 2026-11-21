using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

public class ConflictMatcherTests
{
    private static readonly ConflictMatcher Matcher =
        new(["INPALOAD", "TOOL32", "NCSEXPER", "WINKFPT", "BMW Coding Tool"]);

    [Theory]
    [InlineData("INPALOAD")]
    [InlineData("inpaload")]
    [InlineData("InpaLoad")]
    [InlineData("INPALOAD.exe")]
    [InlineData("inpaload.EXE")]
    [InlineData("TOOL32")]
    [InlineData("tool32.exe")]
    [InlineData("BMW Coding Tool")]
    [InlineData("bmw coding tool.exe")]
    public void Matches(string name)
    {
        Assert.True(Matcher.IsConflict(name));
    }

    [Theory]
    [InlineData("TOOL32X")]
    [InlineData("XTOOL32")]
    [InlineData("TOOL3")]
    [InlineData("INPA")]
    [InlineData("INPALOADER")]
    [InlineData("TOOL32.exe.exe")]
    [InlineData("TOOL32.ex")]
    [InlineData("BMW Coding")]
    [InlineData("BMW Coding Tool 2")]
    [InlineData("")]
    [InlineData("explorer")]
    [InlineData(".exe")]
    public void DoesNotMatch(string name)
    {
        Assert.False(Matcher.IsConflict(name));
    }

    [Fact]
    public void Null_DoesNotMatch()
    {
        Assert.False(Matcher.IsConflict(null));
    }

    [Fact]
    public void ConfiguredNamesWithExe_StillMatch()
    {
        var m = new ConflictMatcher(["WINKFPT.EXE", "  NCSEXPER  "]);
        Assert.True(m.IsConflict("winkfpt"));
        Assert.True(m.IsConflict("NCSEXPER"));
        Assert.True(m.IsConflict("ncsexper.exe"));
    }

    [Fact]
    public void EmptyList_MatchesNothing()
    {
        var m = new ConflictMatcher([]);
        Assert.False(m.IsConflict("INPALOAD"));
    }

    [Fact]
    public void BlankConfiguredEntries_Ignored()
    {
        var m = new ConflictMatcher(["", "  ", ".exe"]);
        Assert.False(m.IsConflict(""));
        Assert.False(m.IsConflict(".exe"));
    }

    [Theory]
    [InlineData("TOOL32.exe", "TOOL32")]
    [InlineData("TOOL32.EXE", "TOOL32")]
    [InlineData("TOOL32", "TOOL32")]
    [InlineData("a.exe.exe", "a.exe")]
    public void Normalize_StripsOneTrailingExe(string input, string expected)
    {
        Assert.Equal(expected, ConflictMatcher.Normalize(input));
    }
}
