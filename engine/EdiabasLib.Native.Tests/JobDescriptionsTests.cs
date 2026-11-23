using EdiabasLib.Native;

namespace EdiabasLib.Native.Tests;

/// EdiabasLib keys job descriptions by the name as the description block writes it and looks
/// them up in upper case (EdiabasNet.cs ReadDescriptions, VJobJobComments). Real EDIABAS finds
/// "initialisierung" when asked for INITIALISIERUNG; EdiabasLib alone does not.
public sealed class JobDescriptionsTests : IDisposable
{
    readonly MixedCaseSgbd _ecu = new();

    public void Dispose() => _ecu.Dispose();

    EdiabasNet Load()
    {
        var ed = EdiabasFactory.Create(_ecu.EcuPath, new OfflineInterface(), noInitForVJobs: true);
        ed.ResolveSgbdFile("cmd_test2");
        return ed;
    }

    static List<string> Values(EdiabasNet ed) =>
        ed.ResultSets.Skip(1).SelectMany(s => s.Values).Select(r => r.OpData as string).OfType<string>().ToList();

    [Fact]
    public void Upstream_LosesTheCommentsOfAJobDescribedInLowerCase()
    {
        using var ed = Load();
        ed.ArgString = "INITIALISIERUNG";
        ed.ExecuteJob("_JOBCOMMENTS");
        Assert.Empty(Values(ed));
    }

    [Fact]
    public void IgnoreCase_FindsTheCommentsOfAJobDescribedInLowerCase()
    {
        using var ed = Load();
        JobDescriptions.IgnoreCase(ed);
        ed.ArgString = "INITIALISIERUNG";
        ed.ExecuteJob("_JOBCOMMENTS");
        Assert.Contains("Initialisierung", Values(ed));
    }

    [Fact]
    public void IgnoreCase_FindsTheResultsOfAJobDescribedInLowerCase()
    {
        using var ed = Load();
        JobDescriptions.IgnoreCase(ed);
        ed.ArgString = "INITIALISIERUNG";
        ed.ExecuteJob("_RESULTS");
        Assert.Contains("DONE", Values(ed));
    }

    [Fact]
    public void IgnoreCase_KeepsUpperCaseNamesWorking()
    {
        using var ed = Load();
        JobDescriptions.IgnoreCase(ed);
        ed.ArgString = "TEST_DIVS_ZERO";
        ed.ExecuteJob("_JOBCOMMENTS");
        Assert.Contains("Test math zero division", Values(ed));
    }

    [Fact]
    public void IgnoreCase_AfterLoadingAnotherSgbd_AppliesToIt()
    {
        using var ed = Load();
        JobDescriptions.IgnoreCase(ed);
        ed.ResolveSgbdFile("cmd_test1");
        ed.ResolveSgbdFile("cmd_test2");
        JobDescriptions.IgnoreCase(ed);
        ed.ArgString = "INITIALISIERUNG";
        ed.ExecuteJob("_JOBCOMMENTS");
        Assert.Contains("Initialisierung", Values(ed));
    }
}
