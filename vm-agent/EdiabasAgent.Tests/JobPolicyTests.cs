using System.Text.Json;
using System.Text.Json.Nodes;
using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

public class JobPolicyTests
{
    /// <summary>The repo's policy/ folder, found by walking up from the test binaries.</summary>
    internal static string PolicyDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "policy", "policy-cases.json");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "policy");
            }
        }

        throw new InvalidOperationException("policy/policy-cases.json not found above the test directory");
    }

    public static TheoryData<int, string> Cases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(PolicyDir(), "policy-cases.json")));
        var data = new TheoryData<int, string>();
        var i = 0;
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            // Index plus a readable label; the case itself is re-read in the test.
            data.Add(i++, c.GetProperty("ecu").GetString() + " " + c.GetProperty("job").GetString());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Fixture(int index, string label)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(PolicyDir(), "policy-cases.json")));
        var c = doc.RootElement.GetProperty("cases")[index];
        string? Opt(string name) => c.TryGetProperty(name, out var v) ? v.GetString() : null;

        var verdict = JobPolicy.Embedded.Evaluate(c.GetProperty("ecu").GetString(), c.GetProperty("job").GetString(), Opt("args"), Opt("results"));

        Assert.True(c.GetProperty("allowed").GetBoolean() == verdict.Allowed, $"{label}: allowed");
        Assert.Equal(c.GetProperty("rule").GetString(), verdict.Rule);
        Assert.Equal(c.GetProperty("screen").GetBoolean(), verdict.Screen);
    }

    public static TheoryData<int, string> ParseCases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(PolicyDir(), "policy-parse-cases.json")));
        var data = new TheoryData<int, string>();
        var i = 0;
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            data.Add(i++, c.GetProperty("name").GetString()!);
        }

        return data;
    }

    /// <summary>policy/policy-parse-cases.json: the Mac's parser runs the same cases, so both
    /// sides accept and refuse exactly the same policy texts.</summary>
    [Theory]
    [MemberData(nameof(ParseCases))]
    public void ParseFixture(int index, string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(PolicyDir(), "policy-parse-cases.json")));
        var c = doc.RootElement.GetProperty("cases")[index];

        var p = JobPolicy.Parse(c.GetProperty("text").GetString()!);

        Assert.True(c.GetProperty("valid").GetBoolean() == p.Loaded, $"{name}: {p.LoadError ?? "loaded"}");
    }

    [Fact]
    public void ParseFixture_HasBothKinds()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(PolicyDir(), "policy-parse-cases.json")));
        var valid = doc.RootElement.GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("valid").GetBoolean()).ToList();
        Assert.Contains(true, valid);
        Assert.True(valid.Count(v => !v) > 30);
    }

    [Fact]
    public void Fixture_IsNotEmpty()
    {
        Assert.True(Cases().Count > 50);
    }

    [Fact]
    public void Embedded_IsTheRepoFile()
    {
        using var stream = typeof(JobPolicy).Assembly.GetManifestResourceStream(JobPolicy.ResourceName)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        Assert.Equal(File.ReadAllBytes(Path.Combine(PolicyDir(), "policy.json")), ms.ToArray());
    }

    [Fact]
    public void Embedded_Loads()
    {
        Assert.True(JobPolicy.Embedded.Loaded, JobPolicy.Embedded.LoadError);
        Assert.Equal(3, JobPolicy.Embedded.Version);
        Assert.Contains("lösch", JobPolicy.Embedded.ScreenTerms);
        Assert.Equal(["beschreibung", "umsetzer"], JobPolicy.Embedded.ScreenIgnore);
    }

    [Fact]
    public void AllowedVerdict_CarriesUpperCasedNames()
    {
        var v = JobPolicy.Embedded.Evaluate("nve_65", "fs_lesen");
        Assert.Equal("NVE_65", v.Ecu);
        Assert.Equal("FS_LESEN", v.Job);
    }

    [Fact]
    public void ShapeFailure_HasNoNames()
    {
        var v = JobPolicy.Embedded.Evaluate("NVE_65", "FS_LESEN\n");
        Assert.Null(v.Ecu);
        Assert.Null(v.Job);
    }

    [Fact]
    public void Null_IsShapeFailure()
    {
        Assert.Equal("shape.ecu", JobPolicy.Embedded.Evaluate(null, "IDENT").Rule);
        Assert.Equal("shape.job", JobPolicy.Embedded.Evaluate("NVE_65", null).Rule);
    }

    [Fact]
    public void TrailingNewline_NeverPassesAPattern()
    {
        // The STATUS_ pattern alone, not just the name shape, must refuse a trailing newline.
        var policy = JobPolicy.Parse(Mutate(n => n["namePattern"] = "^[\\s\\S]{1,80}$"));
        Assert.True(policy.Loaded, policy.LoadError);
        Assert.Equal("default_deny", policy.Evaluate("NVE_65", "STATUS_X\n").Rule);
        Assert.Equal("shape.ecu", JobPolicy.Embedded.Evaluate("NVE_65\n", "IDENT").Rule);
        Assert.Equal("shape.ecu", JobPolicy.Embedded.Evaluate("TMODE\n", "INFO").Rule);
    }

    // ---- fail closed ----

    public static TheoryData<string> BrokenPolicies() =>
    [
        "",
        "not json",
        "[]",
        "{}",
        "{ \"version\": 1 }",
        "null",
        Mutate(n => n["version"] = "1"),
        Mutate(n => n["version"] = 0),
        Mutate(n => n["namePattern"] = "("),
        Mutate(n => n["namePattern"] = ""),
        Mutate(n => n["maxArgLength"] = -1),
        Mutate(n => n["extra"] = 1),
        Mutate(n => n["deny"]!["patterns"] = new JsonArray("X")),
        Mutate(n => n["deny"]!.AsObject().Remove("tokens")),
        Mutate(n => n["deny"]!["substrings"] = new JsonArray("SCHREIB", 5)),
        Mutate(n => n["deny"]!["substrings"] = new JsonArray("SCHREIB", "")),
        Mutate(n => n["allow"]!["patterns"] = new JsonArray("[")),
        Mutate(n => n["allow"]!["restrictedEcus"] = new JsonArray()),
        Mutate(n => n["descriptionScreen"]!["terms"] = new JsonArray()),
        Mutate(n => n.Remove("descriptionScreen")),
        Mutate(n => n["descriptionScreen"]!.AsObject().Remove("ignore")),
        Mutate(n => n["descriptionScreen"]!["ignore"] = new JsonArray("beschreibung", "Umsetzer")),
        Mutate(n => n["descriptionScreen"]!["ignore"] = new JsonArray("umsetzer", "umsetzer")),
        Mutate(n => n["descriptionScreen"]!["ignore"] = new JsonArray("umsetzer", "")),
        Mutate(n => n["descriptionScreen"]!["ignore"] = "umsetzer"),
        Text().Replace("\"version\": 3,", "\"version\": 3, \"version\": 4,", StringComparison.Ordinal),
        Text().Replace("\"version\": 3,", "// comment\n\"version\": 3,", StringComparison.Ordinal),
    ];

    [Theory]
    [MemberData(nameof(BrokenPolicies))]
    public void BrokenPolicy_RefusesEverything(string json)
    {
        var policy = JobPolicy.Parse(json);
        Assert.False(policy.Loaded);
        Assert.NotNull(policy.LoadError);
        Assert.Null(policy.Version);
        Assert.Empty(policy.ScreenTerms);
        Assert.Empty(policy.ScreenIgnore);

        foreach (var (ecu, job) in new[] { ("NVE_65", "IDENT"), ("TMODE", "INFO"), ("NVE_65", "STATUS_X"), ("NVE_65", "_JOBS") })
        {
            var v = policy.Evaluate(ecu, job);
            Assert.False(v.Allowed);
            Assert.Equal(JobPolicy.RuleUnavailable, v.Rule);
        }
    }

    [Fact]
    public void RepoPolicy_Parses()
    {
        Assert.True(JobPolicy.Parse(Text()).Loaded);
    }

    private static string Text() => File.ReadAllText(Path.Combine(PolicyDir(), "policy.json"));

    private static string Mutate(Action<JsonObject> change)
    {
        var node = JsonNode.Parse(Text())!.AsObject();
        change(node);
        return node.ToJsonString();
    }
}
