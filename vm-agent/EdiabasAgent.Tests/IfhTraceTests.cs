using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

public class IfhTraceTests
{
    /// <summary>A real IFH.trc from the VM (EDIABAS 7.3.0, IfhTrace 3, desk sessions with no
    /// car: CRLF, tab-indented metadata, multi-line INPUT).</summary>
    internal static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "IFH-2026-11-24.trc"), Win1252.Encoding);

    private const string Meta = "\t\t\t\t\t\t\t\t#@1;P00002600h;#\r\n";

    private static string Lines(params string[] lines) => string.Concat(lines.Select(l => Meta + l + "\r\n"));

    [Fact]
    public void Fixture_IsCrlf()
    {
        var text = Fixture();
        Assert.Contains("\r\n", text);
        Assert.Equal(text.Split('\n').Length - 1, text.Split("\r\n").Length - 1);
    }

    [Fact]
    public void Fixture_ParsesWithoutProblems()
    {
        var s = IfhTrace.Parse(Fixture());

        Assert.Empty(s.Problems);
        var counts = s.Events.GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(32, counts["OPEN"]);
        Assert.Equal(28, counts["SETECUCOMM"]);
        Assert.Equal(23, counts["SETTELPARAMETER"]);
        Assert.Equal(22, counts["STOPECUTELFREQUENT"]);
        Assert.Equal(3, counts["GETBATTERYVOLTAGE"]);
        Assert.Equal(6, counts["SENDECUTEL"]);
        Assert.Equal(6, counts.Count);
    }

    [Fact]
    public void Fixture_SendsCarryTheConceptSetBeforeThem()
    {
        var sends = IfhTrace.Parse(Fixture()).Events.Where(e => e.Name == IfhTrace.SendEcuTel).ToList();

        Assert.Equal(
            [
                ("010F", "82 12 F1 1A 80"), ("010D", "B8 12 F1 02 1A 80"), ("0110", "82 12 F1 1A 80"),
                ("010F", "82 12 F1 1A 80"), ("010D", "B8 12 F1 02 1A 80"), ("0110", "82 12 F1 1A 80"),
            ],
            sends.Select(e => (TelegramRule.ConceptHex(e.Concept), TelegramRule.Hex(e.Input))).ToList());

        // Every telegram in the desk trace passes the rule.
        Assert.All(sends, e => Assert.True(TelegramRule.Embedded.Evaluate(e.Concept, e.Input).Allowed));
    }

    [Fact]
    public void Fixture_ContinuationLinesExtendTheInput()
    {
        var set = IfhTrace.Parse(Fixture()).Events.First(e => e.Name == IfhTrace.SetEcuComm && e.Error == IfhTrace.NoError);

        // 20 bytes on the INPUT line and 8 on its continuation line.
        Assert.Equal(28, set.Input!.Length);
        Assert.Equal("0F 01 00 00", TelegramRule.Hex(set.Input.AsSpan(0, 4)));
        Assert.Equal("88 13 00 00", TelegramRule.Hex(set.Input.AsSpan(24, 4)));
    }

    [Fact]
    public void Fixture_FailedSetEcuComm_LeavesNoConcept()
    {
        // The 20:07:59 session: SETECUCOMM answered IFH_0018 (no cable).
        var s = IfhTrace.Parse(Fixture());
        var failed = s.Events.First(e => e.Name == IfhTrace.SetEcuComm && e.Error == "IFH_0018");
        var after = s.Events.SkipWhile(e => e != failed).Skip(1).FirstOrDefault();
        Assert.True(after is null || after.Name == IfhTrace.Open || after.Concept is null);
    }

    [Fact]
    public void Fixture_SplitBetweenCalls_GivesTheSameCalls()
    {
        // The agent reads the trace in segments, one per request, carrying the concept across.
        // A request boundary falls between calls: here, before the metadata line of each send.
        var text = Fixture();
        var whole = IfhTrace.Parse(text).Events.Select(e => (e.Name, TelegramRule.Hex(e.Input), e.Concept)).ToList();
        var cuts = 0;
        for (var i = text.IndexOf("SENDECUTEL", StringComparison.Ordinal); i >= 0; i = text.IndexOf("SENDECUTEL", i + 1, StringComparison.Ordinal))
        {
            var lineStart = text.LastIndexOf('\n', i) + 1;
            var cut = text.LastIndexOf('\n', lineStart - 2) + 1;
            var first = IfhTrace.Parse(text[..cut]);
            var second = IfhTrace.Parse(text[cut..], first.EndConcept);
            Assert.Empty(first.Problems);
            Assert.Empty(second.Problems);
            Assert.Equal(whole, first.Events.Concat(second.Events).Select(e => (e.Name, TelegramRule.Hex(e.Input), e.Concept)).ToList());
            cuts++;
        }

        Assert.Equal(6, cuts);
    }

    [Fact]
    public void SegmentStartingMidFile_NoHeader_UsesTheCarriedConcept()
    {
        var text = Lines(
            "_STD     [        16] SENDECUTEL",
            "_STD     [        16]  INPUT: 82 12 F1 1A 80 ",
            "_STD     [        32]  ERROR: NO ERROR");

        var carried = IfhTrace.Parse(text, TelegramRule.ConceptBmwFast);
        var fresh = IfhTrace.Parse(text);

        Assert.Empty(carried.Problems);
        Assert.Equal(TelegramRule.ConceptBmwFast, carried.Events.Single().Concept);
        Assert.Null(fresh.Events.Single().Concept);
        Assert.Equal(3, carried.TraceLines);
    }

    [Fact]
    public void SegmentStartingInsideACall_IsAProblem()
    {
        // The call's own line was before the segment: its INPUT belongs to no known call.
        var s = IfhTrace.Parse(Lines(
            "_STD     [        16]  INPUT: 82 12 F1 1A 80 ",
            "_STD     [        32]  ERROR: NO ERROR"));

        Assert.Empty(s.Events);
        Assert.Equal(2, s.Problems.Count);
    }

    [Fact]
    public void SegmentStartingMidLine_IsAProblem()
    {
        var s = IfhTrace.Parse("2 F1 1A 80 \r\n" + Lines("_STD     [        32]  ERROR: NO ERROR"));
        Assert.NotEmpty(s.Problems);
        Assert.Equal(1, s.Problems[0].Line);
    }

    [Fact]
    public void EmptySegment()
    {
        foreach (var text in new[] { "", "\r\n", Meta + Meta })
        {
            var s = IfhTrace.Parse(text, TelegramRule.ConceptBmwFast);
            Assert.Empty(s.Events);
            Assert.Empty(s.Problems);
            Assert.Equal(0, s.TraceLines);
            Assert.Equal(TelegramRule.ConceptBmwFast, s.EndConcept);
        }
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("\u0000\u0001\u0002")]
    [InlineData("_STD     [        16] lower case call")]
    [InlineData("_STD     [xx] SENDECUTEL")]
    [InlineData("_STD                          02 00 zz")]
    public void Garbage_IsAProblem_NeverSkipped(string line)
    {
        var s = IfhTrace.Parse(Lines("_STD     [        16] SETTELPARAMETER", line));
        Assert.Single(s.Problems);
        Assert.Equal(4, s.Problems[0].Line);
    }

    [Fact]
    public void InputThatIsNotHex_IsAProblem()
    {
        var s = IfhTrace.Parse(Lines("_STD     [        16] SENDECUTEL", "_STD     [        16]  INPUT: 82 12 G1 "));
        Assert.Single(s.Problems);
        Assert.Contains("not hex", s.Problems[0].Why);
    }

    [Fact]
    public void ContinuationAfterEmptyInput_IsAProblem()
    {
        var s = IfhTrace.Parse(Lines(
            "_STD     [        16] SENDECUTEL",
            "_STD     [        16]  INPUT: --",
            "_STD                          82 12 F1 1A 80 "));
        Assert.Single(s.Problems);
    }

    [Fact]
    public void MultiLineInput_LfOnly_AndContinuationOfOtherFieldsIgnored()
    {
        var text = string.Join("\n",
            "_STD     [        16] SETECUCOMM",
            "_STD     [        16]  INPUT: 0D 01 00 00 80 25 00 00 F4 01 00 00 19 00 00 00 14 00 00 00 ",
            "_STD                          02 00 00 00 88 13 00 00 ",
            "_STD     [        16]   I -> D: 05 1F 00 0D 01 00 00 80 25 00 00 F4 01 00 00 19 00 00 00 14 ",
            "_STD                            00 00 00 02 00 00 00 88 13 00 00 ",
            "_STD     [        16]  ERROR: NO ERROR",
            "_STD     [        16]  OUTPUT: --",
            "_STD     [        47] SENDECUTEL",
            "_STD     [        47]  INPUT: B8 12 F1 02 1A 80 ",
            "_STD     [        79]  ERROR: IFH_0003");

        var s = IfhTrace.Parse(text);

        Assert.Empty(s.Problems);
        Assert.Equal(28, s.Events[0].Input!.Length);
        Assert.Equal(TelegramRule.ConceptKwp2000Star, s.Events[1].Concept);
        Assert.Equal("B8 12 F1 02 1A 80", TelegramRule.Hex(s.Events[1].Input));
        Assert.Equal("IFH_0003", s.Events[1].Error);
        Assert.Equal(TelegramRule.ConceptKwp2000Star, s.EndConcept);
    }

    [Theory]
    [InlineData("IFH_0018", "0F 01 00 00")]
    [InlineData(null, "0F 01 00 00")]
    [InlineData("NO ERROR", "0F")]
    [InlineData("NO ERROR", "--")]
    public void SetEcuComm_ThatIsNotKnownToHaveSucceeded_ForgetsTheConcept(string? error, string input)
    {
        var lines = new List<string> { "_STD     [ 0] SETECUCOMM", "_STD     [ 0]  INPUT: " + input };
        if (error is not null)
        {
            lines.Add("_STD     [ 0]  ERROR: " + error);
        }

        lines.Add("_STD     [ 0] SENDECUTEL");
        lines.Add("_STD     [ 0]  INPUT: 82 12 F1 1A 80");

        var s = IfhTrace.Parse(Lines([.. lines]), TelegramRule.ConceptBmwFast);

        Assert.Null(s.Events[1].Concept);
    }

    [Fact]
    public void Open_ForgetsTheConcept()
    {
        var s = IfhTrace.Parse(Lines("_STD     [ 0] OPEN /Sun Nov 22 19:48:32 2026", "_STD     [ 0]  INPUT: --", "_STD     [ 0] SENDECUTEL", "_STD     [ 0]  INPUT: 82 12 F1 1A 80"), TelegramRule.ConceptBmwFast);

        Assert.Equal("OPEN", s.Events[0].Name);
        Assert.Equal(TelegramRule.ConceptBmwFast, s.Events[0].Concept);
        Assert.Null(s.Events[1].Concept);
    }

    [Fact]
    public void OtherInterfacePrefix_IsRead()
    {
        // With apiInitExt("GATE:OBD") the trace lines may carry another prefix.
        var s = IfhTrace.Parse(Lines("_GATE    [ 0] SENDECUTEL", "_GATE    [ 0]  INPUT: 82 12 F1 1A 80"));
        Assert.Empty(s.Problems);
        Assert.Equal("SENDECUTEL", s.Events.Single().Name);
    }

    [Fact]
    public void SecondInput_IsAProblem()
    {
        var s = IfhTrace.Parse(Lines("_STD [ 0] SENDECUTEL", "_STD [ 0]  INPUT: 82 12 F1 1A 80", "_STD [ 0]  INPUT: 82 12 F1 14 FF"));
        Assert.Single(s.Problems);
    }

    [Fact]
    public void MissingInput_IsNull()
    {
        var s = IfhTrace.Parse(Lines("_STD [ 0] SENDECUTEL", "_STD [ 0]  ERROR: NO ERROR"));
        Assert.Null(s.Events.Single().Input);
    }
}
