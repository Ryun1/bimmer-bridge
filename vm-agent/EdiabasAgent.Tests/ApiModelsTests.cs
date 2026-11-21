using System.Text.Json;
using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

public class ApiModelsTests
{
    [Fact]
    public void Error_Shape()
    {
        var json = ApiJson.Serialize(ApiErrors.Create(ApiErrors.Unauthorized, "Missing or invalid bearer token."));
        Assert.Equal("""{"error":{"code":"unauthorized","message":"Missing or invalid bearer token."}}""", json);
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
            User: "ryan",
            Elevated: false,
            SessionId: 1,
            BindAddress: "192.168.64.3",
            Port: 8765,
            Gateway: null,
            ConflictingProcesses: [new ConflictingProcess("INPALOAD", 4312)],
            Ediabas: new EdiabasInfo(@"C:\EDIABAS\BIN\api32.dll", true),
            LastCableUseAt: null);

        using var doc = JsonDocument.Parse(ApiJson.Serialize(h));
        var root = doc.RootElement;
        string[] expected =
        [
            "agent", "version", "startedAt", "uptimeSeconds", "os", "user", "elevated", "sessionId",
            "bindAddress", "port", "gateway", "conflictingProcesses", "ediabas", "lastCableUseAt",
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
