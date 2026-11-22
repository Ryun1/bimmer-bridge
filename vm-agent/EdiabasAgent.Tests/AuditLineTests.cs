using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

public class AuditLineTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 21, 15, 4, 5, 123, TimeSpan.Zero);

    [Fact]
    public void Format_Basic()
    {
        var line = AuditLine.Format(T, "192.168.64.1", "GET", "/health", null, 200, null, 12);
        Assert.Equal("2026-11-21T15:04:05.123Z\t192.168.64.1\tGET\t/health\t200\t-\t12\t-\t-\t-\t-", line);
    }

    [Fact]
    public void Format_ConvertsToUtc()
    {
        var local = new DateTimeOffset(2026, 11, 21, 16, 4, 5, 0, TimeSpan.FromHours(1));
        var line = AuditLine.Format(local, "1.2.3.4", "GET", "/", null, 200, null, 0);
        Assert.StartsWith("2026-11-21T15:04:05.000Z\t", line);
    }

    [Fact]
    public void Format_ErrorCode()
    {
        var line = AuditLine.Format(T, "192.168.64.1", "GET", "/nope", null, 404, "not_found", 1);
        Assert.EndsWith("\t404\tnot_found\t1\t-\t-\t-\t-", line);
    }

    [Fact]
    public void Format_HasExactlyElevenFields()
    {
        var line = AuditLine.Format(T, "a", "GET", "/x", "?a=1", 200, "c", 5);
        Assert.Equal(11, line.Split('\t').Length);
    }

    [Theory]
    [InlineData("/health\tforged")]
    [InlineData("/health\nforged")]
    [InlineData("/health\r\n2026-01-01T00:00:00.000Z\t1.1.1.1\tGET\t/x\t200\t-\t1")]
    [InlineData("/health\u0000x")]
    [InlineData("/health\u0085x")]
    [InlineData("/health\u2028x")]
    [InlineData("/health\u2029x")]
    [InlineData("/health\u001bx")]
    [InlineData("/health\u202Ecrt.exe")]
    [InlineData("/health\u200Bx")]
    [InlineData("/health\u2066x\u2069")]
    [InlineData("/health\uFEFFx")]
    public void PathInjection_Neutralised(string path)
    {
        var line = AuditLine.Format(T, "192.168.64.1", "GET", path, null, 200, null, 1);
        AssertSingleSafeLine(line);
    }

    [Theory]
    [InlineData("192.168.64.1\tGET")]
    [InlineData("192.168.64.1\nX")]
    [InlineData("1.1.1.1\r\nY")]
    public void RemoteAddressInjection_Neutralised(string remote)
    {
        var line = AuditLine.Format(T, remote, "GET", "/health", null, 200, null, 1);
        AssertSingleSafeLine(line);
    }

    [Fact]
    public void MethodAndCodeInjection_Neutralised()
    {
        var line = AuditLine.Format(T, "a", "GE\tT\n", "/x", "?k\t=v\n&z", 500, "x\ty\nz", 1);
        AssertSingleSafeLine(line);
    }

    [Fact]
    public void NullOrEmptyFields_BecomeDash()
    {
        var line = AuditLine.Format(T, null, "", "", null, 401, "", 0);
        var parts = line.Split('\t');
        Assert.Equal("-", parts[1]);
        Assert.Equal("-", parts[2]);
        Assert.Equal("-", parts[3]);
        Assert.Equal("-", parts[5]);
    }

    [Theory]
    [InlineData(null, "/logs")]
    [InlineData("", "/logs")]
    [InlineData("?", "/logs")]
    [InlineData("?root=0", "/logs?root")]
    [InlineData("?root=0&glob=*.trc&since=2026", "/logs?root&glob&since")]
    [InlineData("?token=SECRET&x", "/logs?token&x")]
    [InlineData("root=1", "/logs?root")]
    [InlineData("?=secretvalue&a=b", "/logs?&a")]
    public void QueryString_ReducedToKeys(string? query, string expectedPath)
    {
        var line = AuditLine.Format(T, "a", "GET", "/logs", query, 200, null, 1);
        Assert.Equal(expectedPath, line.Split('\t')[3]);
        Assert.DoesNotContain("SECRET", line);
        Assert.DoesNotContain("secretvalue", line);
    }

    [Fact]
    public void PathWithEmbeddedQuery_ValuesStripped()
    {
        // If a caller passes a raw target that still contains the query, values are still removed.
        var line = AuditLine.Format(T, "a", "GET", "/logs?path=C:\\secret", null, 200, null, 1);
        Assert.Equal("/logs?path", line.Split('\t')[3]);
    }

    [Fact]
    public void NegativeDuration_ClampedToZero()
    {
        var line = AuditLine.Format(T, "a", "GET", "/", null, 200, null, -5);
        Assert.Equal("0", line.Split('\t')[6]);
    }

    [Fact]
    public void EcuJobRule()
    {
        var line = AuditLine.Format(T, "a", "POST", "/ediabas/job", null, 403, "job_not_allowed", 3, "NVE_65", "FS_LOESCHEN", "deny.substring:LOESCH");
        Assert.EndsWith("\t403\tjob_not_allowed\t3\tNVE_65\tFS_LOESCHEN\tdeny.substring:LOESCH\t-", line);
    }

    [Theory]
    [InlineData("FS_LESEN\tforged")]
    [InlineData("FS_LESEN\r\n2026-01-01T00:00:00.000Z\tx")]
    [InlineData("FS\u2028X")]
    public void RejectedJobNameInjection_Neutralised(string job)
    {
        var line = AuditLine.Format(T, "a", "POST", "/ediabas/job", null, 403, "job_not_allowed", 1, job, job, "shape.job");
        AssertSingleSafeLine(line);
    }

    [Fact]
    public void Target_LastField()
    {
        var line = AuditLine.Format(T, "a", "GET", "/logs/read", "?path=x", 403, "path_not_allowed", 2, rule: "dot_segment", target: @"C:\EDIABAS\TRACE\..\x");
        Assert.EndsWith("\t403\tpath_not_allowed\t2\t-\t-\tdot_segment\tC:\\EDIABAS\\TRACE\\..\\x", line);
        Assert.Equal("/logs/read?path", line.Split('\t')[3]);
    }

    [Theory]
    [InlineData("C:\\EDIABAS\\TRACE\\a.trc\tforged")]
    [InlineData("C:\\x\r\n2026-01-01T00:00:00.000Z\t1.1.1.1\tGET\t/x")]
    [InlineData("INPA\u2029x\u0085y")]
    public void TargetInjection_Neutralised(string target)
    {
        var line = AuditLine.Format(T, "a", "GET", "/logs/read", null, 200, null, 1, target: target);
        AssertSingleSafeLine(line);
    }

    private static void AssertSingleSafeLine(string line)
    {
        Assert.Equal(11, line.Split('\t').Length);
        foreach (var ch in line)
        {
            Assert.False(ch != '\t' && char.IsControl(ch), $"control char U+{(int)ch:X4} in line");
            Assert.NotEqual('\u2028', ch);
            Assert.NotEqual('\u2029', ch);
            Assert.False(char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.Format, $"format char U+{(int)ch:X4} in line");
        }
    }

    // A right-to-left override or zero-width space would make a line read differently from
    // what it holds when Ryan looks at the audit log.
    [Fact]
    public void FormatCharacters_ReplacedLikeControlCharacters()
    {
        var line = AuditLine.Format(T, "192.168.64.1", "GET", "/a\u202Eb\u200Bc", null, 200, null, 1);
        Assert.Contains("\t/a_b_c\t", line);
    }

    [Theory]
    [InlineData("GET /x\u202Ey", "GET /x_y")]
    [InlineData("GET /x\u200By\n", "GET /x_y_")]
    [InlineData("GET /x\u2028y\u2029", "GET /x_y_")]
    [InlineData("GET /plain", "GET /plain")]
    public void Neutralise_ForLogText(string text, string expected)
    {
        Assert.Equal(expected, AuditLine.Neutralise(text));
    }
}
