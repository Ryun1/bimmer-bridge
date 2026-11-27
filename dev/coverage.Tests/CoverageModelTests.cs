using System.Text.Json;
using Coverage;

namespace Coverage.Tests;

public class CoverageModelTests
{
    [Fact]
    public void Parse_NameOnly()
    {
        var j = JobString.Parse("TEST_MATH");
        Assert.Equal("TEST_MATH", j.Job);
        Assert.Null(j.Args);
        Assert.Null(j.ArgsBinary);
        Assert.Equal("", j.Results);
        Assert.Null(j.StdArgs);
        Assert.Null(j.Sgbd);
    }

    [Fact]
    public void Parse_BinaryArgs()
    {
        var j = JobString.Parse("TEST_PARY#|12131415");
        Assert.Equal(new byte[] { 0x12, 0x13, 0x14, 0x15 }, j.ArgsBinary);
        Assert.Null(j.Args);
    }

    [Fact]
    public void Parse_EmptyBinaryArgs()
    {
        Assert.Equal(Array.Empty<byte>(), JobString.Parse("TEST_PARY#|").ArgsBinary);
    }

    [Fact]
    public void Parse_StdArgsAndSgbd()
    {
        var j = JobString.Parse("TEST_SHMID###EXIT_EXCEPTION#cmd_test1.prg");
        Assert.Equal("TEST_SHMID", j.Job);
        Assert.Equal("", j.Args);
        Assert.Equal("", j.Results);
        Assert.Equal("EXIT_EXCEPTION", j.StdArgs);
        Assert.Equal("cmd_test1", j.Sgbd);
    }

    [Fact]
    public void Parse_EmptyStdArgsAndSgbd()
    {
        var j = JobString.Parse("TEST_SHMID####cmd_test1.prg");
        Assert.Equal("", j.StdArgs);
        Assert.Equal("cmd_test1", j.Sgbd);
    }

    [Fact]
    public void Parse_ArgsWithSpacesKept()
    {
        Assert.Equal(" 123.45 ", JobString.Parse("TEST_PARR# 123.45 ").Args);
    }

    [Fact]
    public void Parse_EmptyJobName_Throws()
    {
        Assert.Throws<FormatException>(() => JobString.Parse("#ARGS"));
    }

    [Theory]
    [InlineData("cmd_test1", true)]
    [InlineData("CMD_TEST2.prg", true)]
    [InlineData("base1", true)]
    [InlineData("base2.PRG", true)]
    [InlineData("cmd_ident.grp", true)]
    [InlineData("dm338ds1_interbyte", false)]
    [InlineData("d60m47a0_dcan", false)]
    [InlineData("adapter_prg", false)]
    [InlineData("NVE_65", false)]
    [InlineData("../cmd_test1", false)]
    public void AllowedSgbds(string name, bool allowed)
    {
        Assert.Equal(allowed, Sgbds.IsAllowed(name));
    }

    [Fact]
    public void JobList_LoadsAndEverySgbdIsAllowed()
    {
        var runs = JobList.Load(JobList.DefaultPath());
        Assert.True(runs.Count > 100);
        Assert.All(runs, r => Assert.True(Sgbds.IsAllowed(r.Sgbd), r.Run));
        Assert.All(runs.SelectMany(r => r.Jobs), j => Assert.True(JobString.Parse(j).Sgbd is null || Sgbds.IsAllowed(JobString.Parse(j).Sgbd!), j));
    }

    [Fact]
    public void JobList_RefusesAForbiddenSgbd()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, "{\"runs\":[{\"run\":\"x\",\"sgbd\":\"dm338ds1_interbyte\",\"jobs\":[\"_JOBS\"]}]}");
        try
        {
            Assert.Throws<InvalidOperationException>(() => JobList.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void JobList_RefusesAForbiddenSgbdInAJobString()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, "{\"runs\":[{\"run\":\"x\",\"sgbd\":\"cmd_test1\",\"jobs\":[\"TEST####d60m47a0_dcan.prg\"]}]}");
        try
        {
            Assert.Throws<InvalidOperationException>(() => JobList.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    static JobRecord Rec(string run, int index, params ResultValue[] set1) =>
        new(run, index, "cmd_test1", "TEST", true, null,
            [[new ResultValue("SAETZE", "word", JsonSerializer.SerializeToElement(1), [])], [.. set1]]);

    static ResultValue V(string name, object value, Dictionary<string, string?>? formatted = null) =>
        new(name, "long", JsonSerializer.SerializeToElement(value), formatted ?? []);

    [Fact]
    public void Compare_EqualRecords_Match()
    {
        var r = Comparison.Run([Rec("a", 0, V("X", 1))], [Rec("a", 0, V("X", 1))]);
        Assert.Equal(1, r.Matched);
        Assert.Empty(r.Differences);
    }

    [Fact]
    public void Compare_ValueDifference_NamesSetAndResult()
    {
        var r = Comparison.Run([Rec("a", 0, V("X", 1))], [Rec("a", 0, V("X", 2))]);
        Assert.Equal(0, r.Matched);
        var d = Assert.Single(r.Differences);
        Assert.Contains("a#0", d);
        Assert.Contains("set 1", d);
        Assert.Contains("X", d);
    }

    [Fact]
    public void Compare_ResultOrderInASet_DoesNotMatter()
    {
        var r = Comparison.Run([Rec("a", 0, V("X", 1), V("Y", 2))], [Rec("a", 0, V("Y", 2), V("X", 1))]);
        Assert.Equal(1, r.Matched);
    }

    [Fact]
    public void Compare_FormattedDifference_Reported()
    {
        var r = Comparison.Run(
            [Rec("a", 0, V("X", 1, new() { ["6.4W"] = "  1.0" }))],
            [Rec("a", 0, V("X", 1, new() { ["6.4W"] = "1.0" }))]);
        Assert.Contains("6.4W", Assert.Single(r.Differences));
    }

    [Fact]
    public void Compare_MissingJob_Reported()
    {
        var r = Comparison.Run([Rec("a", 0), Rec("a", 1)], [Rec("a", 0)]);
        Assert.Contains(r.Differences, d => d.Contains("a#1") && d.Contains("missing"));
    }

    [Fact]
    public void Compare_ErrorCodes()
    {
        var vm = new JobRecord("a", 0, "cmd_test1", "T", false, new ErrorInfo(91, "BIP-0001"), []);
        var mac = new JobRecord("a", 0, "cmd_test1", "T", false, new ErrorInfo(91, "BIP-0001: other text"), []);
        Assert.Equal(1, Comparison.Run([vm], [mac]).Matched);
        var mac2 = mac with { Error = new ErrorInfo(92, "x") };
        Assert.Contains("error", Assert.Single(Comparison.Run([vm], [mac2]).Differences));
    }

    [Fact]
    public void Jsonl_RoundTrip()
    {
        var rec = Rec("a", 0, V("X", 1.5, new() { ["R"] = "1.5" }));
        var line = Jsonl.Write(rec);
        Assert.DoesNotContain('\n', line);
        var back = Jsonl.Read(line);
        Assert.Equal(1, Comparison.Run([rec], [back]).Matched);
    }
}
