using System.Text.Json;
using EdiabasAgent.Core;

namespace Ediabasd.Tests;

public sealed class FrontTests : IDisposable
{
    readonly string _state = Directory.CreateTempSubdirectory("ediabasd-front-").FullName;
    readonly EdiabasdHost _host;
    readonly Front _front;
    int _cables;

    public FrontTests()
    {
        _host = new EdiabasdHost(HostTests.Config(_state), () =>
        {
            _cables++;
            throw new InvalidOperationException("no live cable in these tests");
        });
        _front = new Front(_host, new AuditLog(Path.Combine(_state, "audit.log")));
    }

    public void Dispose()
    {
        _host.Dispose();
        Directory.Delete(_state, recursive: true);
    }

    FrontResponse Call(string method, string? json = null) =>
        _front.Handle(method, json == null ? null : JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Describe_Ok()
    {
        var r = Call("describe", "{\"ecu\":\"cmd_test2\"}");
        Assert.True(r.Ok, r.Error?.Message);
        var list = Assert.IsType<DescribeListResponse>(r.Result);
        Assert.Contains(list.Jobs, j => j.Name == "TEST_IFACE_FLAGS");
        Assert.Equal(RequestMode.Offline, _host.Api.Mode);
    }

    [Fact]
    public void DescribeOneJob_Ok()
    {
        var r = Call("describe", "{\"ecu\":\"cmd_test2\",\"job\":\"TEST_IFACE_FLAGS\"}");
        Assert.True(r.Ok, r.Error?.Message);
        Assert.IsType<DescribeJobResponse>(r.Result);
    }

    [Fact]
    public void Describe_WithoutEcu_IsBadRequest()
    {
        var r = Call("describe", "{}");
        Assert.False(r.Ok);
        Assert.Equal(ApiErrors.BadRequest, r.Error!.Code);
        Assert.Contains("ecu", r.Error.Message);
    }

    [Fact]
    public void Describe_UnknownParam_IsBadRequest()
    {
        var r = Call("describe", "{\"ecu\":\"cmd_test2\",\"verbose\":true}");
        Assert.Equal(ApiErrors.BadRequest, r.Error!.Code);
    }

    [Fact]
    public void Describe_CommentsNotBool_IsBadRequest()
    {
        var r = Call("describe", "{\"ecu\":\"cmd_test2\",\"comments\":\"yes\"}");
        Assert.Equal(ApiErrors.BadRequest, r.Error!.Code);
    }

    [Fact]
    public void Job_RefusedByPolicy_CreatesNoCable()
    {
        var r = Call("job", "{\"ecu\":\"cmd_test2\",\"job\":\"TEST_IFACE_FLAGS\"}");
        Assert.False(r.Ok);
        Assert.Equal(ApiErrors.JobNotAllowed, r.Error!.Code);
        Assert.Equal(0, _cables);
        Assert.Equal(RequestMode.Live, _host.Api.Mode);
    }

    [Fact]
    public void Jobs_ItemsAnswered()
    {
        var r = Call("jobs", "{\"items\":[{\"ecu\":\"cmd_test2\",\"job\":\"TEST_IFACE_FLAGS\"}]}");
        Assert.True(r.Ok, r.Error?.Message);
        var batch = Assert.IsType<BatchResponse>(r.Result);
        Assert.Equal(ApiErrors.JobNotAllowed, Assert.Single(batch.Items).Error!.Code);
    }

    [Fact]
    public void Ecus_Filtered()
    {
        var r = Call("ecus", "{\"filter\":\"cmd_test\"}");
        Assert.True(r.Ok, r.Error?.Message);
        var json = JsonSerializer.Serialize(r.Result, ApiJson.Options);
        Assert.Contains("CMD_TEST1", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeGroup_NoCachedVariant_NeedsCar()
    {
        var r = Call("describe", "{\"ecu\":\"cmd_ident\"}");
        Assert.False(r.Ok);
        Assert.Equal(ApiErrors.NeedsCar, r.Error!.Code);
        Assert.Equal(0, _cables);
    }

    [Fact]
    public void UnknownMethod_IsBadRequest()
    {
        Assert.Equal(ApiErrors.BadRequest, Call("clear_faults", "{}").Error!.Code);
    }

    [Fact]
    public void Vehicle_IsAnUnknownMethod()
    {
        // Vehicle status is a jobs batch from the MCP server (main, 0.5.0).
        var r = Call("vehicle", "{}");
        Assert.Equal(ApiErrors.BadRequest, r.Error!.Code);
        Assert.DoesNotContain("vehicle", r.Error.Message.Split("use ")[^1]);
    }

    [Fact]
    public void Health_SaysEngineAndEcuPath()
    {
        var r = Call("health");
        Assert.True(r.Ok);
        var json = JsonSerializer.Serialize(r.Result, ApiJson.Options);
        Assert.Contains("\"engine\":\"ediabasd-net\"", json);
        Assert.Contains("\"ecuPath\"", json);
        Assert.Contains("\"cable\"", json);
    }

    [Fact]
    public void Health_ReportsCableStateLatencyAndPolicySha()
    {
        _host.Config.Port = "/dev/does-not-exist-ediabasd";
        var r = Call("health");
        Assert.True(r.Ok);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(r.Result, ApiJson.Options));
        var cable = doc.RootElement.GetProperty("cable");
        Assert.Equal("absent", cable.GetProperty("state").GetString());
        Assert.Equal("/dev/does-not-exist-ediabasd", cable.GetProperty("port").GetString());
        Assert.Equal(JsonValueKind.Null, cable.GetProperty("latency").ValueKind);
        var sha = doc.RootElement.GetProperty("policySha256").GetString();
        Assert.Matches("^[0-9a-f]{64}$", sha);
        Assert.Equal(JobPolicy.Embedded.Sha256, sha);
    }

    [Fact]
    public void EveryRequest_IsAudited()
    {
        Call("describe", "{\"ecu\":\"cmd_test2\",\"comments\":false}");
        Call("job", "{\"ecu\":\"cmd_test2\",\"job\":\"TEST_IFACE_FLAGS\"}");
        var lines = File.ReadAllLines(Path.Combine(_state, "audit.log"));
        Assert.Equal(2, lines.Length);
        Assert.Contains("job_not_allowed", lines[1]);
    }
}
