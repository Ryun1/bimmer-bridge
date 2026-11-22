using System.Text.Json;
using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

public class ApiModelsTests
{
    [Fact]
    public void Error_Shape()
    {
        var json = ApiJson.Serialize(ApiErrors.Create(ApiErrors.Unauthorized, "Missing or invalid bearer token."));
        Assert.Equal("""{"error":{"code":"unauthorized","message":"Missing or invalid bearer token.","details":null}}""", json);
    }

    [Fact]
    public void Error_WithDetails()
    {
        var json = ApiJson.Serialize(ApiErrors.Create(ApiErrors.JobNotAllowed, "m", new JobRefusalDetails("NVE_65", "FS_LOESCHEN", "deny.substring:LOESCH")));
        Assert.Equal("""{"error":{"code":"job_not_allowed","message":"m","details":{"ecu":"NVE_65","job":"FS_LOESCHEN","rule":"deny.substring:LOESCH"}}}""", json);
    }

    [Fact]
    public void EdiabasError_ResultsUnread_OnlyWhenSet()
    {
        // An ordinary ediabas_error keeps its shape byte for byte; only unreadable results add the flag.
        Assert.Equal(
            """{"error":{"code":"ediabas_error","message":"m","details":{"errorCode":19,"errorText":"SYS-0008"}}}""",
            ApiJson.Serialize(ApiErrors.Create(ApiErrors.EdiabasError, "m", new EdiabasErrorDetails(19, "SYS-0008"))));
        Assert.Equal(
            """{"error":{"code":"ediabas_error","message":"m","details":{"errorCode":0,"errorText":"","resultsUnread":true}}}""",
            ApiJson.Serialize(ApiErrors.Create(ApiErrors.EdiabasError, "m", new EdiabasErrorDetails(0, "", ResultsUnread: true))));
    }

    [Theory]
    [InlineData("bad_request", 400)]
    [InlineData("job_not_allowed", 403)]
    [InlineData("job_needs_review", 403)]
    [InlineData("ecu_not_found", 404)]
    [InlineData("job_not_found", 404)]
    [InlineData("cable_in_use", 409)]
    [InlineData("ediabas_error", 502)]
    [InlineData("ediabas_unavailable", 503)]
    [InlineData("job_timeout", 504)]
    [InlineData("internal_error", 500)]
    [InlineData("path_not_allowed", 403)]
    [InlineData("file_not_found", 404)]
    [InlineData("window_not_found", 404)]
    [InlineData("window_minimized", 409)]
    [InlineData("desktop_unavailable", 503)]
    [InlineData("capture_busy", 503)]
    [InlineData("capture_failed", 500)]
    [InlineData("something_new", 500)]
    public void Status(string code, int status)
    {
        Assert.Equal(status, ApiErrors.Status(code));
    }

    [Fact]
    public void Batch_And_Vehicle_Shapes()
    {
        var item = new BatchItem(false, null, new ErrorDetail("not_run", "m", null));
        Assert.Equal(
            """{"items":[{"ok":false,"result":null,"error":{"code":"not_run","message":"m","details":null}}],"durationMs":3}""",
            ApiJson.Serialize(new BatchResponse([item], 3)));

        var v = new VehicleResponse(item, item, [new VehicleVoltage("NVE_65", "STATUS_BORDNETZSPANNUNG", false, null, null)], item, item, 9);
        using var doc = JsonDocument.Parse(ApiJson.Serialize(v));
        Assert.Equal(["ignition", "ubatt", "voltages", "cableKl30", "cableKl15", "durationMs"], doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(["ecu", "job", "ok", "result", "error"], doc.RootElement.GetProperty("voltages")[0].EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Describe_Shapes()
    {
        var list = new DescribeListResponse("NVE_65", null, 1, [new DescribeJobEntry("FS_LESEN", null, new JobVerdict(true, "allow.global", Review.NotRequired))], 4);
        Assert.Equal(
            """{"ecu":"NVE_65","variant":null,"jobCount":1,"jobs":[{"name":"FS_LESEN","comment":null,"verdict":{"allowed":true,"rule":"allow.global","review":"not_required"}}],"durationMs":4}""",
            ApiJson.Serialize(list));

        var one = new DescribeJobResponse("NVE_65", "NVE_65", "FS_LESEN", "c", "84a516841ba7", new JobVerdict(true, "allow.global", Review.NotRequired), [new JobParameterInfo("A", "int", null)], [], 5);
        Assert.Equal(
            """{"ecu":"NVE_65","variant":"NVE_65","job":"FS_LESEN","comment":"c","descriptionHash":"84a516841ba7","verdict":{"allowed":true,"rule":"allow.global","review":"not_required"},"arguments":[{"name":"A","type":"int","comment":null}],"results":[],"durationMs":5}""",
            ApiJson.Serialize(one));

        Assert.Equal(
            """{"ecuPath":"C:\\EDIABAS\\ECU","count":1,"truncated":false,"files":[{"name":"NVE_65","type":"prg"}]}""",
            ApiJson.Serialize(new EcusResponse(@"C:\EDIABAS\ECU", 1, false, [new EcuFile("NVE_65", "prg")])));
    }

    [Fact]
    public void ErrorCodes()
    {
        Assert.Equal("unauthorized", ApiErrors.Unauthorized);
        Assert.Equal("forbidden", ApiErrors.Forbidden);
        Assert.Equal("not_found", ApiErrors.NotFound);
        Assert.Equal("method_not_allowed", ApiErrors.MethodNotAllowed);
        Assert.Equal("internal_error", ApiErrors.InternalError);
    }

    [Fact]
    public void Health_Shape_CamelCaseAndExplicitNulls()
    {
        var h = new HealthResponse(
            Agent: HealthResponse.AgentName,
            Version: "0.1.0",
            StartedAt: ApiFormat.Timestamp(new DateTimeOffset(2026, 11, 21, 15, 4, 5, 999, TimeSpan.Zero)),
            UptimeSeconds: 512,
            Os: new OsInfo("Microsoft Windows 10.0.26100", "Arm64", "X86"),
            User: "user",
            Elevated: false,
            SessionId: 1,
            BindAddress: "192.168.64.3",
            Port: 8765,
            Gateway: null,
            ConflictingProcesses: [new ConflictingProcess("INPALOAD", 4312)],
            Ediabas: new EdiabasInfo(@"C:\EDIABAS\BIN\api32.dll", true, false, true),
            LastCableUseAt: null,
            PolicyVersion: 1,
            Capabilities: ["ediabas"],
            Approvals: new ApprovalCounts(2, 1));

        using var doc = JsonDocument.Parse(ApiJson.Serialize(h));
        var root = doc.RootElement;
        string[] expected =
        [
            "agent", "version", "startedAt", "uptimeSeconds", "os", "user", "elevated", "sessionId",
            "bindAddress", "port", "gateway", "conflictingProcesses", "ediabas", "lastCableUseAt",
            "policyVersion", "capabilities", "approvals",
        ];
        Assert.Equal(expected, root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("ediabas-bridge-agent", root.GetProperty("agent").GetString());
        Assert.Equal("2026-11-21T15:04:05Z", root.GetProperty("startedAt").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lastCableUseAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("gateway").ValueKind);
        Assert.Equal(512, root.GetProperty("uptimeSeconds").GetInt64());
        Assert.Equal(JsonValueKind.False, root.GetProperty("elevated").ValueKind);
        var os = root.GetProperty("os");
        Assert.Equal(["description", "osArchitecture", "processArchitecture"], os.EnumerateObject().Select(p => p.Name).ToArray());
        var cp = root.GetProperty("conflictingProcesses")[0];
        Assert.Equal("INPALOAD", cp.GetProperty("name").GetString());
        Assert.Equal(4312, cp.GetProperty("pid").GetInt32());
        var ed = root.GetProperty("ediabas");
        Assert.Equal(@"C:\EDIABAS\BIN\api32.dll", ed.GetProperty("dllPath").GetString());
        Assert.True(ed.GetProperty("dllFound").GetBoolean());
        Assert.Equal(["dllPath", "dllFound", "loaded", "stuck"], ed.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(JsonValueKind.False, ed.GetProperty("loaded").ValueKind);
        Assert.Equal(JsonValueKind.True, ed.GetProperty("stuck").ValueKind);
        Assert.Equal("""{"pinned":2,"ignored":1}""", root.GetProperty("approvals").GetRawText());
    }

    [Fact]
    public void Windows_Shape()
    {
        var w = new WindowsResponse(
        [
            new WindowInfo("0x000A0B2C", "INPA - BMW Group", "INPALOAD", 4312, new Bounds(0, 0, 1280, 800), false),
            new WindowInfo("0x00000001", "x", null, 7, new Bounds(-32000, -32000, 160, 28), true),
        ]);
        var json = ApiJson.Serialize(w);
        Assert.Equal(
            """{"windows":[{"handle":"0x000A0B2C","title":"INPA - BMW Group","processName":"INPALOAD","pid":4312,"bounds":{"x":0,"y":0,"width":1280,"height":800},"minimized":false},{"handle":"0x00000001","title":"x","processName":null,"pid":7,"bounds":{"x":-32000,"y":-32000,"width":160,"height":28},"minimized":true}]}""",
            json);
    }

    [Theory]
    [InlineData(0x000A0B2CL, "0x000A0B2C")]
    [InlineData(0L, "0x00000000")]
    [InlineData(0xFFFFFFFFL, "0xFFFFFFFF")]
    [InlineData(0xABCL, "0x00000ABC")]
    public void Handle_Format(long value, string expected)
    {
        Assert.Equal(expected, ApiFormat.Handle(value));
    }

    [Fact]
    public void Timestamp_IsUtcSecondsWithZ()
    {
        var t = new DateTimeOffset(2026, 11, 21, 16, 4, 5, 500, TimeSpan.FromHours(1));
        Assert.Equal("2026-11-21T15:04:05Z", ApiFormat.Timestamp(t));
    }

    [Fact]
    public void Serialize_NonAsciiTitle_IsValidJson()
    {
        var w = new WindowsResponse([new WindowInfo("0x00000001", "Fahrzeug \u00fc \"x\" \\ </script>", null, 1, new Bounds(0, 0, 1, 1), false)]);
        using var doc = JsonDocument.Parse(ApiJson.Serialize(w));
        Assert.Equal("Fahrzeug \u00fc \"x\" \\ </script>", doc.RootElement.GetProperty("windows")[0].GetProperty("title").GetString());
    }
}
