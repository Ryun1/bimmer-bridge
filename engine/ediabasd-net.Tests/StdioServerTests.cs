using System.Text.Json;

namespace Ediabasd.Tests;

public sealed class StdioServerTests : IDisposable
{
    readonly string _state = Directory.CreateTempSubdirectory("ediabasd-stdio-").FullName;
    readonly EdiabasdHost _host;

    public StdioServerTests()
    {
        _host = new EdiabasdHost(HostTests.Config(_state), () => throw new InvalidOperationException("no cable"));
    }

    public void Dispose()
    {
        _host.Dispose();
        Directory.Delete(_state, recursive: true);
    }

    (int Exit, List<JsonElement> Lines) Serve(string input)
    {
        var server = new StdioServer(new Front(_host, new AuditLog(Path.Combine(_state, "audit.log"))), _host);
        var output = new StringWriter();
        var exit = server.Run(new StringReader(input), output);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
        return (exit, lines);
    }

    [Fact]
    public void TwoRequests_TwoResponses_InOrder()
    {
        var (exit, lines) = Serve(
            "{\"id\":1,\"method\":\"describe\",\"params\":{\"ecu\":\"cmd_test2\",\"comments\":false}}\n" +
            "{\"id\":\"b\",\"method\":\"ecus\",\"params\":{}}\n");
        Assert.Equal(0, exit);
        Assert.Equal(2, lines.Count);
        Assert.Equal(1, lines[0].GetProperty("id").GetInt32());
        Assert.True(lines[0].GetProperty("ok").GetBoolean());
        Assert.Equal("CMD_TEST2", lines[0].GetProperty("result").GetProperty("ecu").GetString());
        Assert.Equal("b", lines[1].GetProperty("id").GetString());
    }

    [Fact]
    public void GarbageLine_IsBadRequest_AndServerCarriesOn()
    {
        var (exit, lines) = Serve("not json\n{\"id\":2,\"method\":\"health\"}\n");
        Assert.Equal(0, exit);
        Assert.Equal(JsonValueKind.Null, lines[0].GetProperty("id").ValueKind);
        Assert.False(lines[0].GetProperty("ok").GetBoolean());
        Assert.Equal("bad_request", lines[0].GetProperty("error").GetProperty("code").GetString());
        Assert.True(lines[1].GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void MissingMethod_IsBadRequest_WithId()
    {
        var (_, lines) = Serve("{\"id\":7}\n");
        Assert.Equal(7, lines[0].GetProperty("id").GetInt32());
        Assert.Equal("bad_request", lines[0].GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void ErrorResponse_HasNoResultKey()
    {
        var (_, lines) = Serve("{\"id\":3,\"method\":\"describe\",\"params\":{}}\n");
        Assert.False(lines[0].TryGetProperty("result", out _));
        Assert.True(lines[0].TryGetProperty("error", out _));
    }

    [Fact]
    public void EmptyInput_ExitsZero()
    {
        Assert.Equal(0, Serve("").Exit);
    }
}
