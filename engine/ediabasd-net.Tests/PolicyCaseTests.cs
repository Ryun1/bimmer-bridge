using System.Text.Json;
using EdiabasAgent.Core;
using EdiabasLib.Native.Tests;

namespace Ediabasd.Tests;

/// policy/policy-cases.json and policy/policy-parse-cases.json against the policy the native
/// host actually uses (the same assertions as vm-agent's JobPolicyTests).
public sealed class PolicyCaseTests : IDisposable
{
    readonly EdiabasdHost _host;
    readonly string _state = Directory.CreateTempSubdirectory("ediabasd-state-").FullName;

    public PolicyCaseTests()
    {
        _host = new EdiabasdHost(HostTests.Config(_state));
    }

    public void Dispose()
    {
        _host.Dispose();
        Directory.Delete(_state, recursive: true);
    }

    static string PolicyDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "policy", "policy-cases.json");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate)!;
        }
        throw new InvalidOperationException("policy/policy-cases.json not found");
    }

    [Fact]
    public void PolicyCases_AllPass()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(PolicyDir(), "policy-cases.json")));
        var count = 0;
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var ecu = c.GetProperty("ecu").GetString();
            var job = c.GetProperty("job").GetString();
            var args = c.TryGetProperty("args", out var a) ? a.GetString() : null;
            var results = c.TryGetProperty("results", out var r) ? r.GetString() : null;
            var v = _host.Policy.Evaluate(ecu, job, args, results);
            Assert.True(c.GetProperty("allowed").GetBoolean() == v.Allowed, $"{ecu}.{job}: allowed {v.Allowed} ({v.Rule})");
            Assert.Equal(c.GetProperty("rule").GetString(), v.Rule);
            Assert.Equal(c.GetProperty("screen").GetBoolean(), v.Screen);
            count++;
        }
        Assert.True(count > 50);
    }

    [Fact]
    public void PolicyParseCases_AllPass()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(PolicyDir(), "policy-parse-cases.json")));
        var count = 0;
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var p = JobPolicy.Parse(c.GetProperty("text").GetString()!);
            Assert.True(c.GetProperty("valid").GetBoolean() == p.Loaded, $"{c.GetProperty("name").GetString()}: {p.LoadError ?? "loaded"}");
            count++;
        }
        Assert.True(count > 0);
    }
}
