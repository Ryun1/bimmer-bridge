using System.Text.Json;
using Coverage;

namespace CoverageVm;

/// <summary>
/// <code>
/// CoverageVm.exe --ecu C:\bimmer-bridge-m1\Ecu --jobs jobs.json --out vm.jsonl [--ifh STD:OBD] [--dll C:\EDIABAS\BIN\api32.dll]
/// </code>
/// Runs every run of the job list through real EDIABAS, as upstream's EdiabasCall does: one
/// apiInitExt … apiEnd per run, EcuPath (and the run's config) set for this process only, never
/// in EDIABAS.INI. Only upstream's test SGBDs are accepted. Run it at the VM's desktop with the
/// cable unplugged from the car.
/// </summary>
public static class Program
{
    static readonly string[] Formats = ["char", "byte", "integer", "word", "long", "dword", "text", "binary", "real"];

    public static int Main(string[] args)
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Length; i += 2) o[args[i]] = args[i + 1];
        if (!o.TryGetValue("--ecu", out var ecu) || !o.TryGetValue("--jobs", out var jobs) || !o.TryGetValue("--out", out var outPath))
        {
            Console.Error.WriteLine("usage: CoverageVm.exe --ecu DIR --jobs jobs.json --out vm.jsonl [--ifh STD:OBD] [--dll C:\\EDIABAS\\BIN\\api32.dll]");
            return 2;
        }

        var ifh = o.GetValueOrDefault("--ifh") ?? "STD:OBD";
        var runs = JobList.Load(jobs);
        using var api = new Api32Coverage(o.GetValueOrDefault("--dll") ?? @"C:\EDIABAS\BIN\api32.dll");
        using var output = new StreamWriter(outPath, append: false);
        foreach (var run in runs)
        {
            Sgbds.Check(run);
            foreach (var rec in RunOne(api, run, ifh, Path.GetFullPath(ecu)))
            {
                output.WriteLine(Jsonl.Write(rec));
            }

            output.Flush();
            Console.Error.WriteLine(run.Run);
        }

        return 0;
    }

    static List<JobRecord> RunOne(Api32Coverage api, CoverageRun run, string ifh, string ecu)
    {
        var records = new List<JobRecord>();
        var formats = Jsonl.FormatsByName(run.Formats);
        var config = run.Config is { Count: > 0 }
            ? string.Join(";", run.Config.Select(kv => kv.Key + "=" + kv.Value))
            : "EcuPath=" + ecu;
        try
        {
            if (!api.InitExt(ifh, config))
            {
                var e = new ErrorInfo(api.ErrorCode(), "apiInitExt: " + api.ErrorText());
                for (var i = 0; i < run.Jobs.Count; i++) records.Add(new JobRecord(run.Run, i, run.Sgbd, run.Jobs[i], false, e, []));
                return records;
            }

            if (run.Config is { Count: > 0 })
            {
                api.SetConfig("EcuPath", ecu);
            }

            for (var i = 0; i < run.Jobs.Count; i++)
            {
                var spec = JobString.Parse(run.Jobs[i]);
                var sgbd = spec.Sgbd ?? run.Sgbd;
                if (spec.StdArgs != null || spec.StdArgsBinary != null)
                {
                    api.JobExt(sgbd, spec.Job, spec.StdArgsBinary ?? Hex.Win1252(spec.StdArgs!), spec.ArgsBinary ?? Hex.Win1252(spec.Args ?? ""), spec.Results);
                }
                else if (spec.Args != null)
                {
                    api.Job(sgbd, spec.Job, spec.Args, spec.Results);
                }
                else
                {
                    api.JobData(sgbd, spec.Job, spec.ArgsBinary ?? [], spec.Results);
                }

                int state;
                while ((state = api.State()) == Api32Coverage.Busy)
                {
                    Thread.Sleep(10);
                }

                if (state != Api32Coverage.Ready)
                {
                    records.Add(new JobRecord(run.Run, i, sgbd, run.Jobs[i], false, new ErrorInfo(api.ErrorCode(), api.ErrorText()), []));
                    continue;
                }

                records.Add(new JobRecord(run.Run, i, sgbd, run.Jobs[i], true, null, Sets(api, formats)));
            }
        }
        finally
        {
            api.End();
        }

        return records;
    }

    static List<List<ResultValue>> Sets(Api32Coverage api, Dictionary<string, List<string>> formats)
    {
        var all = new List<List<ResultValue>>();
        if (!api.ResultSets(out var n)) return all;
        for (var s = 0; s <= n; s++)
        {
            var set = new List<ResultValue>();
            api.ResultNumber(s, out var count);
            for (var i = 1; i <= count; i++)
            {
                if (!api.ResultName(i, s, out var name) || name.Length == 0) continue;
                api.ResultFormat(name, s, out var format);
                var type = format is >= 0 and < 9 ? Formats[format] : format.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var formatted = new Dictionary<string, string?>(StringComparer.Ordinal);
                if (formats.TryGetValue(name, out var list))
                {
                    foreach (var f in list) formatted[f] = api.Text(name, s, f);
                }

                set.Add(new ResultValue(name, type, JsonSerializer.SerializeToElement(api.Value(name, s, format)), formatted));
            }

            all.Add(set);
        }

        return all;
    }
}
