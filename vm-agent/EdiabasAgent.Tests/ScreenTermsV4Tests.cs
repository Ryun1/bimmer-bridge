using System.Text.Json;
using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

/// <summary>Policy version 4 screen terms (D8): switching and triggering words. The Mac does not
/// screen, so the screen-text cases live here and not in policy-cases.json.</summary>
public class ScreenTermsV4Tests
{
    // The version 3 list, kept as the baseline the flips are measured against.
    private static readonly string[] V3Terms =
    [
        "start", "ansteuer", "aktivier", "schreib", "loesch", "lösch", "setz", "program", "einstell",
        "verstell", "aender", "änder", "adjust", "reset", "lernen", "abgleich", "write", "clear",
        "erase", "delete", "activate", "learn",
    ];

    private static CommentScreen Embedded() =>
        new(JobPolicy.Embedded.ScreenTerms, JobPolicy.Embedded.ScreenIgnore, []);

    [Theory]
    [InlineData("Schaltet Relais ein", false)]
    [InlineData("Ausloesen Airbag Test", false)]
    [InlineData("Status Schalter lesen", true)]
    [InlineData("Einschalten der Beleuchtung", false)]
    [InlineData("Ausschalten der Pumpe", false)]
    [InlineData("Umschalten der Betriebsart", false)]
    [InlineData("Auslösen des Tests", false)]
    [InlineData("Trigger fuer Messung", false)]
    [InlineData("Klappe oeffnen", false)]
    [InlineData("Klappe öffnen", false)]
    public void NewTerms(string description, bool passes) =>
        Assert.Equal(passes, Embedded().Screen(description).Passed);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "policy")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("repo root (the folder holding policy/) not found above the test directory");
    }

    /// <summary>(fixture file, job, description the screen would receive).</summary>
    private static IEnumerable<(string File, string Job, string Comment)> DescribeJobs(out List<string> jobless)
    {
        var fixtures = Path.Combine(RepoRoot(), "dev", "fixtures");
        var files = Directory.GetFiles(Path.Combine(fixtures, "idle"), "*describe*.json")
            .Concat(Directory.GetFiles(Path.Combine(fixtures, "inpa-open"), "*describe-NVE_65*.json"))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(files);

        var rows = new List<(string, string, string)>();
        jobless = [];
        foreach (var f in files)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            var body = doc.RootElement.GetProperty("response").GetProperty("body");
            if (!body.TryGetProperty("jobs", out var jobs))
            {
                jobless.Add(Path.GetFileName(f));
                continue;
            }

            foreach (var j in jobs.EnumerateArray())
            {
                rows.Add((Path.GetFileName(f), j.GetProperty("name").GetString()!, j.GetProperty("comment").GetString() ?? ""));
            }
        }

        return rows;
    }

    /// <summary>The jobs in the recorded describe fixtures whose screen verdict differs between
    /// the version 3 terms and the embedded version 4 terms. Pinned so a policy change that
    /// moves a recorded job shows up here. The inpa-open NVE_65 recording is a 409
    /// (cable_in_use) with no jobs, so it adds none; if it is re-recorded the test notices.</summary>
    [Fact]
    public void RecordedDescribeFixtures_FlipExactlyThese()
    {
        var ignore = JobPolicy.Embedded.ScreenIgnore;
        var v3 = new CommentScreen(V3Terms, ignore, []);
        var v4 = Embedded();

        var rows = DescribeJobs(out var jobless).ToList();
        Assert.Equal(["20261121T181457643Z-0007-GET-ediabas_describe-NVE_65.json"], jobless);

        var flips = rows
            .Where(r => v3.Screen(r.Comment).Passed != v4.Screen(r.Comment).Passed)
            .Select(r => $"{r.Job}: v3 {(v3.Screen(r.Comment).Passed ? "passes" : "needs review")} -> v4 {(v4.Screen(r.Comment).Passed ? "passes" : "needs review")}")
            .ToList();

        Assert.Equal(25, rows.Count);
        Assert.Equal(EXPECTED_FLIPS, flips);
    }

    private static readonly string[] EXPECTED_FLIPS = [];
}
