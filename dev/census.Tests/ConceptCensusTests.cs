namespace Census.Tests;

public class ConceptCensusTests
{
    [Fact]
    public void Census_RowsForEveryPrgOnce_NoGrp()
    {
        var rows = ConceptCensus.Run(TestFiles.TempCopy("cmd_test1.prg", "cmd_test2.prg", "base1.prg", "base2.prg", "cmd_ident.grp"));
        Assert.Equal(4, rows.Count);
        Assert.DoesNotContain(rows, r => r.File.EndsWith(".grp", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("no_xsetpar", rows.Single(r => r.File.Equals("cmd_test1.prg", StringComparison.OrdinalIgnoreCase)).Status);
    }

    [Fact] // Review Focus 1
    public void Census_FindsUpperCaseExtension()
    {
        var dir = TestFiles.TempCopyAs("cmd_test1.prg", "CMD_TEST1.PRG");
        Assert.Single(ConceptCensus.Run(dir));
    }

    [Fact] // Review Focus 2
    public void Census_CorruptFile_IsErrorRow_AndOthersStillRun()
    {
        var dir = TestFiles.TempCopy("cmd_test1.prg");
        File.WriteAllBytes(Path.Combine(dir, "junk.prg"), new byte[] { 1, 2, 3 });
        var rows = ConceptCensus.Run(dir);
        Assert.Equal("error", rows.Single(r => r.File == "junk.prg").Status);
        Assert.Equal("no_xsetpar", rows.Single(r => r.File.Equals("cmd_test1.prg", StringComparison.OrdinalIgnoreCase)).Status);
    }

    [Fact]
    public void Census_RowsAreInNameOrder()
    {
        var rows = ConceptCensus.Run(TestFiles.TempCopy("cmd_test2.prg", "base1.prg", "cmd_test1.prg"));
        Assert.Equal(new[] { "base1.prg", "cmd_test1.prg", "cmd_test2.prg" }, rows.Select(r => r.File).ToArray());
    }
}
