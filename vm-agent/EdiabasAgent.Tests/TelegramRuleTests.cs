using System.Globalization;
using System.Text.Json;
using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

public class TelegramRuleTests
{
    private static string CasesPath => Path.Combine(JobPolicyTests.PolicyDir(), "telegram-cases.json");

    public static TheoryData<int, string> Cases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(CasesPath));
        var data = new TheoryData<int, string>();
        var i = 0;
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            data.Add(i++, (c.GetProperty("concept").GetString() ?? "none") + " " + c.GetProperty("frame").GetString());
        }

        return data;
    }

    /// <summary>policy/telegram-cases.json: the interface handler and the native engine run the
    /// same cases against the same file.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Fixture(int index, string label)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(CasesPath));
        var c = doc.RootElement.GetProperty("cases")[index];
        var conceptText = c.GetProperty("concept").GetString();
        ushort? concept = conceptText is null ? null : ushort.Parse(conceptText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        Assert.True(TelegramRule.TryParseHex(c.GetProperty("frame").GetString(), out var frame), label);

        var v = TelegramRule.Embedded.Evaluate(concept, frame);

        Assert.True(c.GetProperty("allowed").GetBoolean() == v.Allowed, $"{label}: allowed");
        Assert.Equal(c.GetProperty("reason").GetString(), v.Reason);
    }

    private static string ParseCasesPath => Path.Combine(JobPolicyTests.PolicyDir(), "telegrams-parse-cases.json");

    public static TheoryData<int, string> ParseCases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ParseCasesPath));
        var data = new TheoryData<int, string>();
        var i = 0;
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            data.Add(i++, c.GetProperty("name").GetString()!);
        }

        return data;
    }

    /// <summary>policy/telegrams-parse-cases.json: the gate's parser (allowlist_json.zig) runs the
    /// same texts, so a list loads in both or in neither.</summary>
    [Theory]
    [MemberData(nameof(ParseCases))]
    public void ParseFixture(int index, string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ParseCasesPath));
        var c = doc.RootElement.GetProperty("cases")[index];
        var rule = TelegramRule.Parse(c.GetProperty("text").GetString()!);
        Assert.True(c.GetProperty("valid").GetBoolean() == rule.Loaded, $"{name}: {rule.LoadError}");
    }

    [Fact]
    public void Fixture_HasEveryCase()
    {
        Assert.Equal(51, Cases().Count);
    }

    [Fact]
    public void Embedded_Loads()
    {
        Assert.True(TelegramRule.Embedded.Loaded, TelegramRule.Embedded.LoadError);
        Assert.Equal(1, TelegramRule.Embedded.Version);
    }

    [Fact]
    public void Embedded_IsTheRepoFile()
    {
        using var stream = typeof(TelegramRule).Assembly.GetManifestResourceStream(TelegramRule.ResourceName)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        Assert.Equal(File.ReadAllBytes(Path.Combine(JobPolicyTests.PolicyDir(), "telegrams.json")), ms.ToArray());
    }

    [Fact]
    public void Embedded_Sha256_MatchesFileBytes()
    {
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(JobPolicyTests.PolicyDir(), "telegrams.json"))));
        Assert.Equal(expected, TelegramRule.Embedded.Sha256);
    }

    [Fact]
    public void ParsedFromText_HasNoSha256()
    {
        var rule = TelegramRule.Parse(Valid());
        Assert.True(rule.Loaded, rule.LoadError);
        Assert.Null(rule.Sha256);
    }

    private static string Valid(string? readServices = null, string? exact = null, string version = "1", string extra = "") =>
        "{\"version\": " + version + ", \"readServices\": " + (readServices ?? "[\"1A\", \"22\"]") +
        ", \"defaultSession\": \"81\", \"adapterReads\": [\"FA\"], \"exact\": " +
        (exact ?? "[{\"address\": \"57\", \"data\": \"30 91 01 01\", \"note\": \"reviewed\"}]") + extra + "}";

    public static TheoryData<string, string> Invalid() => new()
    {
        { "not JSON", "{" },
        { "an array", "[]" },
        { "unknown top key", Valid(extra: ", \"writeServices\": []") },
        { "duplicate key", Valid(extra: ", \"version\": 1") },
        { "missing key", "{\"version\": 1, \"readServices\": [], \"defaultSession\": \"81\", \"adapterReads\": []}" },
        { "version 2", Valid(version: "2") },
        { "version 0", Valid(version: "0") },
        { "version 1.0", Valid(version: "1.0") },
        { "version as text", Valid(version: "\"1\"") },
        { "lower-case byte", Valid(readServices: "[\"1a\"]") },
        { "one hex digit", Valid(readServices: "[\"A\"]") },
        { "0x prefix", Valid(readServices: "[\"0x1A\"]") },
        { "byte as number", Valid(readServices: "[26]") },
        { "listed twice", Valid(readServices: "[\"1A\", \"1A\"]") },
        { "exact unknown key", Valid(exact: "[{\"address\": \"57\", \"data\": \"30\", \"note\": \"x\", \"mask\": \"FF\"}]") },
        { "exact without note", Valid(exact: "[{\"address\": \"57\", \"data\": \"30\"}]") },
        { "exact empty note", Valid(exact: "[{\"address\": \"57\", \"data\": \"30\", \"note\": \" \"}]") },
        { "exact empty data", Valid(exact: "[{\"address\": \"57\", \"data\": \"\", \"note\": \"x\"}]") },
        { "exact double space", Valid(exact: "[{\"address\": \"57\", \"data\": \"30  91\", \"note\": \"x\"}]") },
        { "exact trailing space", Valid(exact: "[{\"address\": \"57\", \"data\": \"30 \", \"note\": \"x\"}]") },
        { "exact twice", Valid(exact: "[{\"address\": \"57\", \"data\": \"30\", \"note\": \"x\"}, {\"address\": \"57\", \"data\": \"30\", \"note\": \"y\"}]") },
        { "exact not an array", Valid(exact: "{}") },
        { "comment", "// x\n" + Valid() },
        { "trailing comma", Valid(extra: ",") },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Parse_RefusesAndFailsClosed(string name, string json)
    {
        var rule = TelegramRule.Parse(json);
        Assert.False(rule.Loaded, name);
        Assert.NotNull(rule.LoadError);
        Assert.Null(rule.Version);

        // A rule that did not load allows nothing, not even a plain read.
        var v = rule.Evaluate(TelegramRule.ConceptBmwFast, [0x82, 0x12, 0xF1, 0x1A, 0x80]);
        Assert.False(v.Allowed);
        Assert.Equal(TelegramRule.ReasonUnavailable, v.Reason);
    }

    [Fact]
    public void Parse_RefusesLoneSurrogate()
    {
        // A Fact, not a theory row: the test runner's serialisation would replace the surrogate.
        var rule = TelegramRule.Parse(Valid(exact: "[{\"address\": \"57\", \"data\": \"30\", \"note\": \"\ud800\"}]"));
        Assert.False(rule.Loaded);
        Assert.Contains("surrogate", rule.LoadError);
    }

    [Fact]
    public void Parse_ValidText_Evaluates()
    {
        var rule = TelegramRule.Parse(Valid());
        Assert.True(rule.Evaluate(TelegramRule.ConceptBmwFast, [0x84, 0x57, 0xF1, 0x30, 0x91, 0x01, 0x01]).Allowed);
        Assert.Equal(TelegramRule.ReasonService, rule.Evaluate(TelegramRule.ConceptBmwFast, [0x82, 0x12, 0xF1, 0x21, 0x01]).Reason);
    }

    [Theory]
    [InlineData("82 12 F1 1A 80", true)]
    [InlineData("82 12 f1 1a 80", true)]
    [InlineData("", true)]
    [InlineData("82 12 F1 1A 8", false)]
    [InlineData("82 12 G1", false)]
    [InlineData("--", false)]
    [InlineData(null, false)]
    public void TryParseHex(string? text, bool ok)
    {
        Assert.Equal(ok, TelegramRule.TryParseHex(text, out _));
    }

    [Fact]
    public void Hex_And_ConceptHex()
    {
        Assert.Equal("82 12 F1", TelegramRule.Hex([0x82, 0x12, 0xF1]));
        Assert.Equal("010F", TelegramRule.ConceptHex(0x010F));
        Assert.Null(TelegramRule.ConceptHex(null));
    }
}
