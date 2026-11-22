// The job list, job strings, the JSONL record and the comparator of the M1 coverage comparison.
// Shared by the Mac runner (dev/coverage) and the VM runner (dev/coverage-vm), so it depends on
// nothing but the BCL.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coverage;

/// One job string as upstream's EdiabasTest and EdiabasCall read it (EdiabasTest/Program.cs:214-251):
/// <c>job#args#results#stdargs#sgbd</c>; an argument starting with <c>|</c> is hex binary.
public sealed record JobSpec(string Job, string? Args, byte[]? ArgsBinary, string Results, string? StdArgs, byte[]? StdArgsBinary, string? Sgbd);

public static class JobString
{
    public static JobSpec Parse(string s)
    {
        var parts = s.Split('#');
        if (parts[0].Length == 0)
        {
            throw new FormatException($"empty job name in \"{s}\"");
        }

        string? args = null, stdArgs = null, sgbd = null;
        byte[]? argsBin = null, stdBin = null;
        var results = "";
        if (parts.Length >= 2) Arg(parts[1], out args, out argsBin);
        if (parts.Length >= 3) results = parts[2];
        if (parts.Length >= 4) Arg(parts[3], out stdArgs, out stdBin);
        if (parts.Length >= 5) sgbd = Path.GetFileNameWithoutExtension(parts[4]);
        return new JobSpec(parts[0], args, argsBin, results, stdArgs, stdBin, sgbd);
    }

    static void Arg(string text, out string? s, out byte[]? bin)
    {
        if (text.Length > 0 && text[0] == '|')
        {
            s = null;
            bin = Convert.FromHexString(text[1..]);
        }
        else
        {
            s = text;
            bin = null;
        }
    }
}

/// Only upstream's own test SGBDs, never the BMW-derived files beside them.
public static class Sgbds
{
    public static readonly string[] Allowed = ["cmd_test1", "cmd_test2", "base1", "base2", "cmd_ident"];

    public static bool IsAllowed(string name)
    {
        if (name.IndexOfAny(['/', '\\']) >= 0)
        {
            return false;
        }

        var ext = Path.GetExtension(name);
        var bare = ext.Equals(".prg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".grp", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(name)
            : name;
        return Allowed.Contains(bare, StringComparer.OrdinalIgnoreCase);
    }

    /// Throws when a run would load anything else.
    public static void Check(CoverageRun run)
    {
        if (!IsAllowed(run.Sgbd))
        {
            throw new InvalidOperationException($"{run.Run}: {run.Sgbd} is not one of upstream's test SGBDs ({string.Join(", ", Allowed)})");
        }

        foreach (var j in run.Jobs)
        {
            var sgbd = JobString.Parse(j).Sgbd;
            if (sgbd != null && !IsAllowed(sgbd))
            {
                throw new InvalidOperationException($"{run.Run}: {j} names {sgbd}, which is not one of upstream's test SGBDs");
            }
        }
    }
}

public sealed record CoverageRun(
    string Run,
    string Sgbd,
    List<string> Jobs,
    List<string>? Formats = null,
    Dictionary<string, string>? Config = null,
    string? Note = null);

public static class JobList
{
    sealed record Doc(List<CoverageRun>? Runs);

    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// dev/coverage/jobs.json, found by walking up from the binary.
    public static string DefaultPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "dev", "coverage", "jobs.json");
            if (File.Exists(p)) return p;
        }

        throw new FileNotFoundException("dev/coverage/jobs.json not found above " + AppContext.BaseDirectory);
    }

    public static IReadOnlyList<CoverageRun> Load(string path)
    {
        var doc = JsonSerializer.Deserialize<Doc>(File.ReadAllText(path), Options)
                  ?? throw new InvalidOperationException($"{path}: not a job list");
        var runs = doc.Runs ?? throw new InvalidOperationException($"{path}: no \"runs\"");
        foreach (var r in runs)
        {
            Sgbds.Check(r);
        }

        return runs;
    }
}

public sealed record ErrorInfo(int Code, string Text);

/// One result. Type is the API format name (char, byte, integer, word, long, dword, real, text,
/// binary); Value a JSON number, string, or upper-case hex for binary; Formatted maps each
/// format asked for this result to its text (null when formatting failed).
public sealed record ResultValue(string Name, string Type, JsonElement Value, Dictionary<string, string?> Formatted);

/// One job of one run. Index is the job's position in its run (job strings repeat).
public sealed record JobRecord(string Run, int Index, string Sgbd, string Job, bool Ok, ErrorInfo? Error, List<List<ResultValue>> Sets);

public static class Jsonl
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public static string Write(JobRecord r) => JsonSerializer.Serialize(r, Options);

    public static JobRecord Read(string line) =>
        JsonSerializer.Deserialize<JobRecord>(line, Options) ?? throw new FormatException("not a coverage record");

    public static IEnumerable<JobRecord> ReadFile(string path) =>
        File.ReadLines(path).Where(l => l.Trim().Length > 0).Select(Read);

    /// The formats a run asks for, by result name (upper-case): "ERGB=6.4W" gives ERGB → 6.4W.
    public static Dictionary<string, List<string>> FormatsByName(IEnumerable<string>? formats)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in formats ?? [])
        {
            var eq = f.IndexOf('=');
            if (eq <= 0) continue;
            var name = f[..eq];
            if (!map.TryGetValue(name, out var list)) map[name] = list = [];
            list.Add(f[(eq + 1)..]);
        }

        return map;
    }
}

public sealed record ComparisonReport(int Matched, int Total, List<string> Differences);

/// Compares VM records with Mac records job by job (run and index): ok, error code (not its
/// text, which EDIABAS and EdiabasLib word differently), and every set's results by name
/// (order inside a set ignored): type, value and each formatted text.
public static class Comparison
{
    public static ComparisonReport Run(IEnumerable<JobRecord> vm, IEnumerable<JobRecord> mac)
    {
        var a = vm.ToDictionary(Key);
        var b = mac.ToDictionary(Key);
        var diffs = new List<string>();
        var matched = 0;
        foreach (var key in a.Keys.Union(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!a.TryGetValue(key, out var x))
            {
                diffs.Add($"{key}: missing on the VM side");
                continue;
            }

            if (!b.TryGetValue(key, out var y))
            {
                diffs.Add($"{key}: missing on the Mac side");
                continue;
            }

            var d = Diff(x, y);
            if (d.Count == 0) matched++;
            diffs.AddRange(d.Select(s => $"{key} {x.Job}: {s}"));
        }

        return new ComparisonReport(matched, a.Keys.Union(b.Keys).Count(), diffs);
    }

    static string Key(JobRecord r) => $"{r.Run}#{r.Index.ToString(CultureInfo.InvariantCulture)}";

    static List<string> Diff(JobRecord x, JobRecord y)
    {
        var d = new List<string>();
        if (x.Ok != y.Ok) d.Add($"ok vm={x.Ok} mac={y.Ok}");
        if ((x.Error?.Code) != (y.Error?.Code)) d.Add($"error vm={Show(x.Error)} mac={Show(y.Error)}");
        if (x.Sets.Count != y.Sets.Count) d.Add($"sets vm={x.Sets.Count} mac={y.Sets.Count}");
        for (var s = 0; s < Math.Min(x.Sets.Count, y.Sets.Count); s++)
        {
            var sx = x.Sets[s].ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
            var sy = y.Sets[s].ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var name in sx.Keys.Union(sy.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.Ordinal))
            {
                if (!sx.TryGetValue(name, out var vx)) { d.Add($"set {s} {name}: only on the Mac"); continue; }
                if (!sy.TryGetValue(name, out var vy)) { d.Add($"set {s} {name}: only on the VM"); continue; }
                if (vx.Type != vy.Type) d.Add($"set {s} {name}: type vm={vx.Type} mac={vy.Type}");
                if (vx.Value.GetRawText() != vy.Value.GetRawText()) d.Add($"set {s} {name}: value vm={vx.Value.GetRawText()} mac={vy.Value.GetRawText()}");
                foreach (var f in vx.Formatted.Keys.Union(vy.Formatted.Keys).OrderBy(k => k, StringComparer.Ordinal))
                {
                    vx.Formatted.TryGetValue(f, out var fx);
                    vy.Formatted.TryGetValue(f, out var fy);
                    if (fx != fy) d.Add($"set {s} {name} format {f}: vm={Quote(fx)} mac={Quote(fy)}");
                }
            }
        }

        return d;
    }

    static string Show(ErrorInfo? e) => e == null ? "none" : $"{e.Code} ({e.Text})";

    static string Quote(string? s) => s == null ? "null" : "'" + s + "'";
}

/// Hex for binary values, as the VM agent writes them.
public static class Hex
{
    public static string Of(byte[] b) => Convert.ToHexString(b);

    public static byte[] Win1252(string s)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252).GetBytes(s);
    }
}
