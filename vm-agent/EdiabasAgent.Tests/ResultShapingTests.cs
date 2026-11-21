using System.Text.Json;
using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

public class ResultShapingTests
{
    private static ReadResults Read(FakeJob job)
    {
        var api = new FakeEdiabasApi().On("X J", job);
        api.Job("X", "J", "", "");
        return ResultShaping.ReadAll(api)!;
    }

    [Fact]
    public void EveryFormat()
    {
        var r = Read(new FakeJob
        {
            Sets =
            [
                [new("VARIANTE", ApiResultFormat.Text, "NVE_65"), new("SAETZE", ApiResultFormat.Word, (ushort)1)],
                [
                    new("C", ApiResultFormat.Char, (sbyte)-5),
                    new("B", ApiResultFormat.Byte, (byte)200),
                    new("I", ApiResultFormat.Integer, (short)-300),
                    new("W", ApiResultFormat.Word, (ushort)65535),
                    new("L", ApiResultFormat.Long, -70000),
                    new("D", ApiResultFormat.DWord, 4000000000u),
                    new("R", ApiResultFormat.Real, 12.25),
                    new("T", ApiResultFormat.Text, "Ölstand"),
                    new("BIN", ApiResultFormat.Binary, new byte[] { 0x0a, 0xff, 0x00 }),
                    new("NAN", ApiResultFormat.Real, double.NaN),
                    new("UNREADABLE", ApiResultFormat.Long, null),
                    new("ODD", 42, "x"),
                    new("JOB_STATUS", ApiResultFormat.Text, "OKAY"),
                ],
            ],
        });

        Assert.Equal("NVE_65", r.Variant);
        Assert.Equal("OKAY", r.JobStatus);
        var json = ApiJson.Serialize(new JobResult("NVE_65", "J", r.Variant, r.JobStatus, r.SystemResults, r.Sets, 5));
        Assert.Equal(
            """{"ecu":"NVE_65","job":"J","variant":"NVE_65","jobStatus":"OKAY","systemResults":{"VARIANTE":"NVE_65","SAETZE":1},"sets":[{"C":-5,"B":200,"I":-300,"W":65535,"L":-70000,"D":4000000000,"R":12.25,"T":"\u00D6lstand","BIN":"0AFF00","NAN":null,"UNREADABLE":null,"ODD":null,"JOB_STATUS":"OKAY"}],"durationMs":5}""",
            json);
    }

    [Fact]
    public void NoVariantOrStatus_AreNull()
    {
        var r = Read(new FakeJob { Sets = [[], [new("X", ApiResultFormat.Text, "y")]] });
        Assert.Null(r.Variant);
        Assert.Null(r.JobStatus);
        var json = ApiJson.Serialize(new JobResult("A", "B", r.Variant, r.JobStatus, r.SystemResults, r.Sets, 0));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("variant").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("jobStatus").ValueKind);
    }

    [Fact]
    public void JobStatus_FirstSetThatHasOne()
    {
        var r = Read(new FakeJob
        {
            Sets = [[], [new("A", ApiResultFormat.Text, "1")], [new("JOB_STATUS", ApiResultFormat.Text, "ERROR_X")], [new("JOB_STATUS", ApiResultFormat.Text, "OKAY")]],
        });
        Assert.Equal("ERROR_X", r.JobStatus);
        Assert.Equal(3, r.Sets.Count);
    }

    [Fact]
    public void DuplicateName_KeepsFirst()
    {
        var r = Read(new FakeJob { Sets = [[], [new("A", ApiResultFormat.Text, "1"), new("A", ApiResultFormat.Text, "2")]] });
        Assert.Equal("1", r.Sets[0]["A"]);
    }

    [Fact]
    public void ResultSetsFails_IsNull()
    {
        Assert.Null(ResultShaping.ReadAll(new FakeEdiabasApi()));
    }
}

public class CommentScreenTests
{
    private static readonly CommentScreen Screen = new(JobPolicy.Embedded.ScreenTerms, ["NVE_65:STATUS_X"]);

    [Theory]
    [InlineData("Status Bordnetzspannung")]
    [InlineData("Read the battery voltage")]
    public void Passes(string description)
    {
        Assert.True(Screen.Screen(description).Passed);
    }

    [Theory]
    [InlineData("STARTEN", "start")]
    [InlineData("Ansteuerung Luefter", "ansteuer")]
    [InlineData("Löschen", "lösch")]
    [InlineData("LÖSCHEN", "lösch")]
    [InlineData("Wert SETZEN", "setz")]
    [InlineData("Write data", "write")]
    [InlineData("Erase memory", "erase")]
    [InlineData("Re-learn", "learn")]
    [InlineData("Programmierung", "programm")]
    public void Fails(string description, string term)
    {
        var r = Screen.Screen(description);
        Assert.False(r.Passed);
        Assert.Contains(term, r.Matched);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void EmptyDescription_Fails(string? description)
    {
        var r = Screen.Screen(description);
        Assert.False(r.Passed);
        Assert.Empty(r.Matched);
    }

    [Fact]
    public void NoTerms_NothingPasses()
    {
        Assert.False(new CommentScreen([], []).Screen("Status lesen").Passed);
    }

    [Fact]
    public void Reviewed_IsExact()
    {
        Assert.True(Screen.IsReviewed("NVE_65", "STATUS_X"));
        Assert.False(Screen.IsReviewed("NVE_65", "STATUS_XY"));
        Assert.False(new CommentScreen([], ["nve_65:status_x"]).IsReviewed("NVE_65", "STATUS_X"));
    }
}

public class Win1252Tests
{
    [Fact]
    public void RoundTrip()
    {
        var bytes = Win1252.ToCString("Öl €");
        Assert.Equal(new byte[] { 0xD6, (byte)'l', (byte)' ', 0x80, 0 }, bytes);
        Assert.Equal("Öl €", Win1252.FromCString(bytes));
    }

    [Fact]
    public void FromCString_StopsAtNul_AndHandlesNoNul()
    {
        Assert.Equal("AB", Win1252.FromCString([(byte)'A', (byte)'B', 0, (byte)'C']));
        Assert.Equal("AB", Win1252.FromCString([(byte)'A', (byte)'B']));
    }

    [Fact]
    public void NoBestFit()
    {
        // U+0141 has no 1252 byte; best-fit would map it to 'L'.
        Assert.Equal((byte)'?', Win1252.ToCString("Ł")[0]);
    }

    [Fact]
    public void EmbeddedNul_Refused()
    {
        Assert.Throws<ArgumentException>(() => Win1252.ToCString("A\0B"));
    }
}
