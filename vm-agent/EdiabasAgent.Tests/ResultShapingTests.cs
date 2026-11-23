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
    private static readonly CommentScreen Screen = new(JobPolicy.Embedded.ScreenTerms, JobPolicy.Embedded.ScreenIgnore, ["NVE_65:STATUS_X@b8ae0dd9d242"]);

    [Theory]
    [InlineData("Status Bordnetzspannung")]
    [InlineData("Read the battery voltage")]
    [InlineData("Status des Steuergeraets lesen")]
    [InlineData("Status des Steuergeräts lesen")]
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
    [InlineData("Programmierung", "program")]
    [InlineData("Program default value", "program")]
    [InlineData("Stellmotor auf Wert einstellen", "einstell")]
    [InlineData("Klappe verstellen", "verstell")]
    [InlineData("Wert aendern", "aender")]
    [InlineData("Wert ändern", "änder")]
    [InlineData("Adjust idle speed", "adjust")]
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

    // descriptionScreen.ignore: labels recorded from the VM that only looked like screen terms.
    [Theory]
    [InlineData("Jobbeschreibung: liest den Status")]
    [InlineData("Messwerte des AD-Umsetzers ausgelesen")]
    [InlineData("JOBBESCHREIBUNG: Status")]
    [InlineData("Jobbeschreibung:\nStatus der Klemme 30")]
    public void IgnoredLabels_Pass(string description)
    {
        var r = Screen.Screen(description);
        Assert.True(r.Passed, string.Join(",", r.Matched));
        Assert.Empty(r.Matched);
    }

    // Nothing broader than the two labels is ignored: these still fail on the term shown, and
    // `matched` lists only what is left after the ignore step.
    [Theory]
    [InlineData("EEPROM beschreiben", new[] { "schreib" })]
    [InlineData("Zaehler zuruecksetzen", new[] { "setz" })]
    [InlineData("Zähler rücksetzen", new[] { "setz" })]
    [InlineData("Jobbeschreibung: Start Routine", new[] { "start" })]
    [InlineData("Beschreibungschreiben", new[] { "schreib" })]
    [InlineData("Umsetzer setzen", new[] { "setz" })]
    public void IgnoreIsNarrow(string description, string[] matched)
    {
        var r = Screen.Screen(description);
        Assert.False(r.Passed);
        Assert.Equal(matched, r.Matched);
    }

    /// <summary>An ignore entry is replaced by a space, so the text around it cannot join into a
    /// new term.</summary>
    [Fact]
    public void IgnoredText_BecomesASpace()
    {
        var screen = new CommentScreen(["ab"], ["xx"], []);
        Assert.True(screen.Screen("Wert aXXb").Passed);
        var r = screen.Screen("Wert aXXbab");
        Assert.False(r.Passed);
        Assert.Equal(["ab"], r.Matched);
    }

    /// <summary>A description that is only labels is empty: it fails, with nothing matched. The
    /// "Job" left of "Jobbeschreibung" is part of the label, not a description.</summary>
    [Theory]
    [InlineData("Jobbeschreibung")]
    [InlineData("Jobbeschreibung:")]
    [InlineData(" JOBBESCHREIBUNG  ")]
    [InlineData("Beschreibung")]
    [InlineData("Beschreibung Umsetzer")]
    [InlineData("Jobbeschreibung: -")]
    public void BlankAfterIgnore_IsEmpty(string description)
    {
        var r = Screen.Screen(description);
        Assert.False(r.Passed);
        Assert.Empty(r.Matched);
    }

    /// <summary>"steuer" would match "Steuergerät" (control unit), which nearly every
    /// description names; "control" likewise. Neither is a term.</summary>
    [Fact]
    public void ControlUnitWords_AreNotTerms()
    {
        Assert.DoesNotContain("steuer", JobPolicy.Embedded.ScreenTerms);
        Assert.DoesNotContain("control", JobPolicy.Embedded.ScreenTerms);
        Assert.DoesNotContain("programm", JobPolicy.Embedded.ScreenTerms);
    }

    [Fact]
    public void NoTerms_NothingPasses()
    {
        Assert.False(new CommentScreen([], [], []).Screen("Status lesen").Passed);
    }

    [Fact]
    public void Reviewed_IsExact()
    {
        Assert.Equal(ApprovalKind.Pinned, Screen.Approval("NVE_65", "STATUS_X").Kind);
        Assert.Equal(ApprovalKind.None, Screen.Approval("NVE_65", "STATUS_XY").Kind);
        Assert.Equal(ApprovalKind.None, new CommentScreen([], [], ["nve_65:status_x@b8ae0dd9d242"]).Approval("NVE_65", "STATUS_X").Kind);
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
