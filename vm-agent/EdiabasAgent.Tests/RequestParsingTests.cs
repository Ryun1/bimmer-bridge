using System.Text;
using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

public class RequestParsingTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static string? Param(ApiFailure f) => ((ParameterDetails)f.Error.Details!).Parameter;

    [Fact]
    public void Job_Minimal()
    {
        var r = RequestParsing.ParseJob(B("""{"ecu":"NVE_65","job":"FS_LESEN"}"""));
        Assert.Equal(new JobBody(new JobRequest("NVE_65", "FS_LESEN"), null), r.Value);
    }

    [Fact]
    public void Job_Full_AndNullsMeanAbsent()
    {
        Assert.Equal(
            new JobBody(new JobRequest("A", "B", "1;2", "X"), 30),
            RequestParsing.ParseJob(B("""{"ecu":"A","job":"B","args":"1;2","results":"X","timeoutSeconds":30}""")).Value);
        Assert.Equal(
            new JobBody(new JobRequest("A", "B"), null),
            RequestParsing.ParseJob(B("""{"ecu":"A","job":"B","args":null,"results":null,"timeoutSeconds":null}""")).Value);
    }

    [Theory]
    [InlineData("", "body")]
    [InlineData("[]", "body")]
    [InlineData("null", "body")]
    [InlineData("{", "body")]
    [InlineData("""{"ecu":"A","job":"B",}""", "body")]
    [InlineData("""{"ecu":"A","job":"B"} // c""", "body")]
    [InlineData("""{"ecu":"A","job":"B"}{}""", "body")]
    [InlineData("""{"job":"B"}""", "ecu")]
    [InlineData("""{"ecu":null,"job":"B"}""", "ecu")]
    [InlineData("""{"ecu":1,"job":"B"}""", "ecu")]
    [InlineData("""{"ecu":"A"}""", "job")]
    [InlineData("""{"ecu":"A","job":["B"]}""", "job")]
    [InlineData("""{"ecu":"A","job":"B","args":5}""", "args")]
    [InlineData("""{"ecu":"A","job":"B","results":{}}""", "results")]
    [InlineData("""{"ecu":"A","job":"B","raw":true}""", "raw")]
    [InlineData("""{"ecu":"A","job":"B","Job":"C"}""", "Job")]
    [InlineData("""{"ecu":"A","job":"B","job":"FS_LOESCHEN"}""", "job")]
    [InlineData("""{"ecu":"A","job":"B","timeoutSeconds":0}""", "timeoutSeconds")]
    [InlineData("""{"ecu":"A","job":"B","timeoutSeconds":-1}""", "timeoutSeconds")]
    [InlineData("""{"ecu":"A","job":"B","timeoutSeconds":1.5}""", "timeoutSeconds")]
    [InlineData("""{"ecu":"A","job":"B","timeoutSeconds":"30"}""", "timeoutSeconds")]
    [InlineData("""{"ecu":"A","job":"B","timeoutSeconds":99999999999}""", "timeoutSeconds")]
    public void Job_BadRequests(string body, string parameter)
    {
        var f = RequestParsing.ParseJob(B(body)).Failure!;
        Assert.Equal(400, f.Status);
        Assert.Equal("bad_request", f.Error.Code);
        Assert.Equal(parameter, Param(f));
    }

    [Fact]
    public void Body_InvalidUtf8_Bom_TooLarge()
    {
        Assert.Equal("body", Param(RequestParsing.ParseJob([(byte)'{', 0xC3, (byte)'}']).Failure!));
        Assert.Equal("body", Param(RequestParsing.ParseJob([0xEF, 0xBB, 0xBF, .. B("""{"ecu":"A","job":"B"}""")]).Failure!));
        var big = B("""{"ecu":"A","job":"B","args":" """ + new string('x', RequestParsing.MaxBodyBytes) + "\"}");
        Assert.Equal("body", Param(RequestParsing.ParseJob(big).Failure!));
    }

    [Fact]
    public void Body_DeepNesting()
    {
        var deep = new string('[', 100) + new string(']', 100);
        Assert.Equal("body", Param(RequestParsing.ParseJob(B(deep)).Failure!));
    }

    [Fact]
    public void Jobs_Ok()
    {
        var r = RequestParsing.ParseJobs(B("""{"items":[{"ecu":"A","job":"B"},{"ecu":"C","job":"D","args":"1"}],"timeoutSeconds":5}""")).Value!;
        Assert.Equal([new JobRequest("A", "B"), new JobRequest("C", "D", "1")], r.Items);
        Assert.Equal(5, r.TimeoutSeconds);
    }

    [Theory]
    [InlineData("""{}""", "items")]
    [InlineData("""{"items":{}}""", "items")]
    [InlineData("""{"items":[]}""", "items")]
    [InlineData("""{"items":[1]}""", "items[0]")]
    [InlineData("""{"items":[{"ecu":"A","job":"B"},{"ecu":"A"}]}""", "items[1].job")]
    [InlineData("""{"items":[{"ecu":"A","job":"B","timeoutSeconds":5}]}""", "items[0].timeoutSeconds")]
    [InlineData("""{"items":[{"ecu":"A","job":"B"}],"extra":1}""", "extra")]
    public void Jobs_BadRequests(string body, string parameter)
    {
        Assert.Equal(parameter, Param(RequestParsing.ParseJobs(B(body)).Failure!));
    }

    [Fact]
    public void Jobs_TooMany()
    {
        var items = string.Join(",", Enumerable.Repeat("""{"ecu":"A","job":"B"}""", 21));
        Assert.Equal("items", Param(RequestParsing.ParseJobs(B("{\"items\":[" + items + "]}")).Failure!));
    }

    [Fact]
    public void Query()
    {
        static KeyValuePair<string, IReadOnlyList<string?>> Kv(string k, params string?[] v) => new(k, v);

        var ok = RequestParsing.ParseQuery([Kv("ecu", "NVE_65"), Kv("comments", "false")], "ecu", "job", "filter", "comments").Value!;
        Assert.Equal("NVE_65", ok.Get("ecu"));
        Assert.Null(ok.Get("job"));

        Assert.Equal("x", Param(RequestParsing.ParseQuery([Kv("x", "1")], "ecu").Failure!));
        Assert.Equal("ecu", Param(RequestParsing.ParseQuery([Kv("ecu", "A", "B")], "ecu").Failure!));
        Assert.Equal("ECU", Param(RequestParsing.ParseQuery([Kv("ECU", "A")], "ecu").Failure!));
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData("true", false, true)]
    [InlineData("false", true, false)]
    [InlineData("TRUE", true, null)]
    [InlineData("1", true, null)]
    [InlineData("", true, null)]
    public void ParseBool(string? value, bool absent, bool? expected)
    {
        Assert.Equal(expected, RequestParsing.ParseBool(value, absent));
    }

    [Fact]
    public void Filter()
    {
        Assert.Null(RequestParsing.CheckFilter(null));
        Assert.Null(RequestParsing.CheckFilter("status_"));
        Assert.NotNull(RequestParsing.CheckFilter(new string('a', 65)));
        Assert.NotNull(RequestParsing.CheckFilter("a\nb"));
        Assert.NotNull(RequestParsing.CheckFilter("ä"));
    }
}

public class EcuFolderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ecu-test-").FullName;

    public EcuFolderTests()
    {
        foreach (var f in new[] { "NVE_65.PRG", "D_MOTOR.GRP", "TMODE.prg", "readme.txt", "N62_TUE.PRG", "N62_TUE.GRP" })
        {
            File.WriteAllText(Path.Combine(_dir, f), "");
        }
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void List_SortedFilteredTyped()
    {
        var r = new EcuFolder(_dir).List(null, EcuFolder.MaxEntries)!;
        Assert.Equal(
            [new EcuFile("D_MOTOR", "grp"), new EcuFile("N62_TUE", "grp"), new EcuFile("N62_TUE", "prg"), new EcuFile("NVE_65", "prg"), new EcuFile("TMODE", "prg")],
            r.Files);
        Assert.Equal(5, r.Count);
        Assert.False(r.Truncated);

        var f = new EcuFolder(_dir).List("nve", EcuFolder.MaxEntries)!;
        Assert.Equal([new EcuFile("NVE_65", "prg")], f.Files);
    }

    [Fact]
    public void List_Truncates()
    {
        var r = new EcuFolder(_dir).List(null, 2)!;
        Assert.Equal(2, r.Count);
        Assert.True(r.Truncated);
    }

    [Fact]
    public void Exists()
    {
        var f = new EcuFolder(_dir);
        Assert.True(f.Exists("NVE_65"));
        Assert.True(f.Exists("D_MOTOR"));
        Assert.False(f.Exists("README"));
        Assert.False(f.Exists("NVE"));
    }

    [Fact]
    public void MissingFolder()
    {
        var f = new EcuFolder(Path.Combine(_dir, "nope"));
        Assert.Null(f.List(null, 10));
        Assert.False(f.Exists("NVE_65"));
    }
}
