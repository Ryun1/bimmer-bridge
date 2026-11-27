using System.Globalization;
using System.Text.Json;
using EdiabasLib;
using EdiabasLib.Native;

namespace Coverage;

/// <summary>
/// <code>
/// coverage run --out mac.jsonl [--jobs dev/coverage/jobs.json] [--ecu third_party/.../Test/Ecu]
/// coverage compare vm.jsonl mac.jsonl
/// </code>
/// <c>run</c> executes upstream's coverage job list on upstream's test SGBDs through ediabasd's
/// engine: a fresh EdiabasNet per run, KlineInterface on an empty libkline replay (an xconnect
/// opens no device; any send fails). <c>compare</c> exits 0 when every job matches.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return args.FirstOrDefault() switch
            {
                "run" => Run(Options(args.Skip(1))),
                "compare" when args.Length == 3 => Compare(args[1], args[2]),
                _ => Usage(),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException or JsonException or ArgumentException)
        {
            Console.Error.WriteLine("coverage: " + ex.Message);
            return 1;
        }
    }

    static int Usage()
    {
        Console.Error.WriteLine("usage: coverage run --out FILE [--jobs FILE] [--ecu DIR] | coverage compare VM.jsonl MAC.jsonl");
        return 2;
    }

    static Dictionary<string, string> Options(IEnumerable<string> args)
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (!list[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= list.Count)
            {
                throw new ArgumentException($"bad argument {list[i]}");
            }

            o[list[i]] = list[++i];
        }

        return o;
    }

    static string UpstreamTestEcu()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "third_party", "ediabaslib", "EdiabasLib", "Test", "Ecu");
            if (Directory.Exists(p)) return p;
        }

        throw new IOException("third_party/ediabaslib/EdiabasLib/Test/Ecu not found; run git submodule update --init");
    }

    static int Run(Dictionary<string, string> o)
    {
        if (!o.TryGetValue("--out", out var outPath)) return Usage();
        var runs = JobList.Load(o.GetValueOrDefault("--jobs") ?? JobList.DefaultPath());
        var ecu = o.GetValueOrDefault("--ecu") ?? UpstreamTestEcu();
        var empty = Path.Combine(Path.GetTempPath(), $"coverage-empty-{Environment.ProcessId}.jsonl");
        File.WriteAllText(empty, "");
        try
        {
            using var output = new StreamWriter(outPath, append: false);
            foreach (var run in runs)
            {
                foreach (var rec in RunOne(run, ecu, empty))
                {
                    output.WriteLine(Jsonl.Write(rec));
                }
            }
        }
        finally
        {
            File.Delete(empty);
        }

        Console.Error.WriteLine($"coverage: {runs.Sum(r => r.Jobs.Count)} jobs in {runs.Count} runs written to {outPath}");
        return 0;
    }

    static List<JobRecord> RunOne(CoverageRun run, string ecuPath, string emptyRecording)
    {
        Sgbds.Check(run);
        var records = new List<JobRecord>();
        var formats = Jsonl.FormatsByName(run.Formats);
        var iface = new KlineInterface(KlineTransport.Replay(emptyRecording)) { ComPort = "replay" };
        using var ed = EdiabasFactory.Create(ecuPath, iface, noInitForVJobs: false);
        foreach (var (k, v) in run.Config ?? [])
        {
            ed.SetConfigProperty(k, v);
        }

        for (var i = 0; i < run.Jobs.Count; i++)
        {
            var spec = JobString.Parse(run.Jobs[i]);
            var sgbd = spec.Sgbd ?? run.Sgbd;
            try
            {
                EdiabasFactory.Activate(ed);
                ed.ArgBinary = null;
                ed.ArgBinaryStd = null;
                ed.ResultsRequests = spec.Results;
                if (spec.ArgsBinary != null) ed.ArgBinary = spec.ArgsBinary;
                else if (spec.Args != null) ed.ArgString = spec.Args;
                if (spec.StdArgsBinary != null) ed.ArgBinaryStd = spec.StdArgsBinary;
                else if (spec.StdArgs != null) ed.ArgStringStd = spec.StdArgs;
                ed.ResolveSgbdFile(sgbd);
                ed.ExecuteJob(spec.Job);
                records.Add(new JobRecord(run.Run, i, sgbd, run.Jobs[i], true, null, Sets(ed.ResultSets, formats)));
            }
            catch (Exception ex)
            {
                var code = ed.ErrorCodeLast != EdiabasNet.ErrorCodes.EDIABAS_ERR_NONE
                    ? ed.ErrorCodeLast
                    : ex is EdiabasNet.EdiabasNetException ene ? ene.ErrorCode : EdiabasNet.ErrorCodes.EDIABAS_SYS_0000;
                records.Add(new JobRecord(run.Run, i, sgbd, run.Jobs[i], false,
                    new ErrorInfo((int)code, EdiabasNet.GetErrorDescription(code) ?? ex.Message), []));
            }
        }

        return records;
    }

    static List<List<ResultValue>> Sets(List<Dictionary<string, EdiabasNet.ResultData>> sets, Dictionary<string, List<string>> formats) =>
        sets.Select(set => set.Values.Select(rd => Value(rd, formats)).ToList()).ToList();

    /// The value as the EDIABAS API returns it for its format (apiResultFormat, then the
    /// matching apiResultXxx), as NativeEdiabasApi converts it.
    static ResultValue Value(EdiabasNet.ResultData rd, Dictionary<string, List<string>> formats)
    {
        object? v;
        string type;
        switch (rd.ResType)
        {
            case EdiabasNet.ResultType.TypeC: type = "char"; v = (sbyte)Int(rd); break;
            case EdiabasNet.ResultType.TypeB: type = "byte"; v = (byte)Int(rd); break;
            case EdiabasNet.ResultType.TypeI: type = "integer"; v = (short)Int(rd); break;
            case EdiabasNet.ResultType.TypeW: type = "word"; v = (ushort)Int(rd); break;
            case EdiabasNet.ResultType.TypeL: type = "long"; v = (int)Int(rd); break;
            case EdiabasNet.ResultType.TypeD: type = "dword"; v = (uint)Int(rd); break;
            case EdiabasNet.ResultType.TypeR: type = "real"; v = rd.OpData switch { double d => d, long l => (double)l, string s => EdiabasNet.StringToFloat(s), _ => null }; break;
            case EdiabasNet.ResultType.TypeS: type = "text"; v = rd.OpData as string ?? EdiabasNet.FormatResult(rd, ""); break;
            case EdiabasNet.ResultType.TypeY: type = "binary"; v = rd.OpData is byte[] b ? Hex.Of(b) : null; break;
            default: type = rd.ResType.ToString(); v = Convert.ToString(rd.OpData, CultureInfo.InvariantCulture); break;
        }

        var formatted = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (formats.TryGetValue(rd.Name, out var list))
        {
            foreach (var f in list)
            {
                formatted[f] = EdiabasNet.FormatResult(rd, f);
            }
        }

        return new ResultValue(rd.Name, type, JsonSerializer.SerializeToElement(v), formatted);
    }

    static long Int(EdiabasNet.ResultData rd) => rd.OpData switch
    {
        long l => l,
        double d => (long)d,
        string s => EdiabasNet.StringToValue(s),
        _ => 0,
    };

    static int Compare(string vmPath, string macPath)
    {
        var r = Comparison.Run(Jsonl.ReadFile(vmPath), Jsonl.ReadFile(macPath));
        foreach (var d in r.Differences)
        {
            Console.WriteLine(d);
        }

        Console.WriteLine($"matched {r.Matched} of {r.Total} jobs");
        return r.Matched == r.Total ? 0 : 1;
    }
}
