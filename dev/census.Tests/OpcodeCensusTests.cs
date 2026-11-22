namespace Census.Tests;

public class OpcodeCensusTests
{
    [Fact]
    public void UnimplementedTable_Has16Entries_FromBestdis()
        => Assert.Equal(16, OpcodeCensus.UnimplementedOpcodes.Count);

    [Fact]
    public void UnimplementedTable_UsesBestdisMnemonics()
    {
        Assert.Equal("jtsr", OpcodeCensus.UnimplementedOpcodes[0x0C]);
        Assert.Equal("xrecvex", OpcodeCensus.UnimplementedOpcodes[0xB4]);
    }

    [Fact]
    public void TestSgbds_DisassembleWithoutErrors_AndCountMove()
    {
        var r = OpcodeCensus.Run(TestFiles.TempCopy("cmd_test1.prg", "cmd_test2.prg", "base1.prg", "base2.prg"));
        Assert.Empty(r.Errors);
        Assert.True(r.MnemonicCounts["move"] > 0);
        Assert.Empty(r.Unimplemented);
    }

    [Fact]
    public void CorruptFile_IsReportedAsError_NotThrown()
    {
        var dir = TestFiles.TempCopy("cmd_test1.prg");
        File.WriteAllBytes(Path.Combine(dir, "junk.prg"), new byte[] { 1, 2, 3 });
        Assert.Single(OpcodeCensus.Run(dir).Errors);
    }

    [Fact] // Review Focus 2: truncated, not just junk
    public async Task TruncatedFile_IsReportedAsError_NotThrownOrHung()
    {
        var dir = TestFiles.TempCopy("cmd_test2.prg");
        var path = Path.Combine(dir, "cmd_test2.prg");
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes.Take(bytes.Length / 2).ToArray());
        var run = Task.Run(() => OpcodeCensus.Run(dir));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30)); // TimeoutException if it hangs
        Assert.Single(result.Errors);
    }

    [Fact]
    public void Run_LeavesTheEcuFolderUntouched()
    {
        var dir = TestFiles.TempCopy("cmd_test1.prg", "cmd_ident.grp");
        OpcodeCensus.Run(dir);
        Assert.Equal(new[] { "cmd_ident.grp", "cmd_test1.prg" },
            Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Run_IncludesGroupFiles()
    {
        var r = OpcodeCensus.Run(TestFiles.TempCopy("cmd_ident.grp"));
        Assert.Empty(r.Errors);
        Assert.NotEmpty(r.MnemonicCounts);
    }
}
