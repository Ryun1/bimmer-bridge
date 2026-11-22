// census: offline surveys of an SGBD folder. Nothing here opens a port or talks to a car.
//
//   census concepts <ecuPath> [--json <out>]   what each .prg's INITIALISIERUNG asks of the cable
//   census opcodes  <ecuPath> [--json <out>]   opcode counts over every .prg/.grp, flagging the
//                                              ones EdiabasLib does not implement

using System.Globalization;
using System.Text.Json;

namespace Census;

public static class Program
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static int Main(string[] args)
    {
        if (args.Length < 2) return Usage();
        string json = null;
        var pos = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--json" && i + 1 < args.Length) { json = ExpandHome(args[++i]); continue; }
            pos.Add(args[i]);
        }
        if (pos.Count != 2) return Usage();
        var ecuPath = Path.GetFullPath(ExpandHome(pos[1]));
        if (!Directory.Exists(ecuPath))
        {
            Console.Error.WriteLine("census: no such folder: " + ecuPath);
            return 1;
        }
        switch (pos[0])
        {
            case "concepts": return Concepts(ecuPath, json);
            case "opcodes": return Opcodes(ecuPath, json);
            default: return Usage();
        }
    }

    static int Usage()
    {
        Console.Error.WriteLine("usage: census concepts <ecuPath> [--json <out>]");
        Console.Error.WriteLine("       census opcodes  <ecuPath> [--json <out>]");
        return 2;
    }

    static int Concepts(string ecuPath, string json)
    {
        var rows = ConceptCensus.Run(ecuPath);
        Console.WriteLine($"{"FILE",-24} {"STATUS",-10} {"CONCEPT",-8} {"BAUD",-7} PARAMS");
        foreach (var r in rows)
        {
            Console.WriteLine($"{r.File,-24} {r.Status,-10} {Hex(r.Concept),-8} {r.Baud?.ToString(CultureInfo.InvariantCulture) ?? "-",-7} {string.Join(",", r.Params)}");
            if (r.Status == "error") Console.WriteLine("    " + r.Error);
        }
        Console.WriteLine();
        Console.WriteLine("TALLY");
        foreach (var g in rows.GroupBy(r => r.Status == "ok" ? Hex(r.Concept) : r.Status).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var bauds = g.Where(r => r.Baud.HasValue).Select(r => r.Baud.Value).Distinct().OrderBy(b => b);
            var baudText = string.Join(",", bauds);
            Console.WriteLine($"{g.Key,-12} {g.Count(),5}" + (baudText.Length > 0 ? "  baud " + baudText : ""));
        }
        Console.WriteLine($"{"total",-12} {rows.Count,5}");
        if (json != null) File.WriteAllText(json, JsonSerializer.Serialize(rows, Json));
        return 0;
    }

    static int Opcodes(string ecuPath, string json)
    {
        var r = OpcodeCensus.Run(ecuPath);
        Console.WriteLine("MNEMONIC     COUNT");
        foreach (var kv in r.MnemonicCounts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
            Console.WriteLine($"{kv.Key,-10} {kv.Value,7}");
        Console.WriteLine();
        Console.WriteLine("UNIMPLEMENTED IN EDIABASLIB: " + (r.Unimplemented.Count == 0 ? "none" : r.Unimplemented.Count + " (file, mnemonic)"));
        foreach (var (file, mnemonic) in r.Unimplemented)
            Console.WriteLine($"  {file,-24} {mnemonic}");
        Console.WriteLine("ERRORS: " + (r.Errors.Count == 0 ? "none" : r.Errors.Count.ToString(CultureInfo.InvariantCulture)));
        foreach (var (file, error) in r.Errors)
            Console.WriteLine($"  {file,-24} {error}");
        if (json != null)
        {
            var doc = new
            {
                mnemonicCounts = r.MnemonicCounts,
                unimplemented = r.Unimplemented.Select(u => new { file = u.File, mnemonic = u.Mnemonic }),
                errors = r.Errors.Select(e => new { file = e.File, error = e.Error }),
            };
            File.WriteAllText(json, JsonSerializer.Serialize(doc, Json));
        }
        return 0;
    }

    static string Hex(uint? v) => v.HasValue ? "0x" + v.Value.ToString("X4", CultureInfo.InvariantCulture) : "-";

    static string ExpandHome(string p) =>
        p == "~" || p.StartsWith("~/", StringComparison.Ordinal)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p.Substring(1)
            : p;
}
