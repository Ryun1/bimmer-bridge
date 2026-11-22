using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Census;

public sealed record OpcodeReport(
    IReadOnlyDictionary<string, int> MnemonicCounts,
    IReadOnlyList<(string File, string Mnemonic)> Unimplemented,
    IReadOnlyList<(string File, string Error)> Errors);

/// Disassembles every SGBD with upstream's BESTDIS (linked unmodified, called by reflection)
/// and counts the opcodes, flagging the ones EdiabasLib does not implement.
public static partial class OpcodeCensus
{
    /// The bytes EdiabasLib's opcode table has no handler for (EdiabasNet.cs, ", null)"), named
    /// as BESTDIS's own opcode table names them.
    static readonly byte[] UnimplementedBytes =
        { 0x0C, 0x0D, 0x6F, 0x70, 0x78, 0x84, 0x85, 0x86, 0x8D, 0xA0, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3, 0xB4 };

    static readonly Type Bestdis = typeof(BESTDIS.Program);

    public static IReadOnlyDictionary<byte, string> UnimplementedOpcodes { get; } = BuildUnimplemented();

    static IReadOnlyDictionary<byte, string> BuildUnimplemented()
    {
        RegisterEncodings();
        var list = (IEnumerable)Bestdis.GetField("ocList", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var table = new Dictionary<byte, string>();
        foreach (var oc in list)
        {
            var t = oc.GetType();
            var code = (byte)t.GetField("oc")!.GetValue(oc)!;
            var name = (string)t.GetField("pneumonic")!.GetValue(oc)!;
            // BESTDIS lists 0x0E and 0x0F twice; its disassembler uses the first entry.
            if (Array.IndexOf(UnimplementedBytes, code) >= 0 && !table.ContainsKey(code))
                table[code] = name;
        }
        return table;
    }

    public static OpcodeReport Run(string ecuPath)
    {
        RegisterEncodings();
        var unimplementedNames = new HashSet<string>(UnimplementedOpcodes.Values, StringComparer.Ordinal);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var unimplemented = new List<(string, string)>();
        var errors = new List<(string, string)>();

        foreach (var path in SgbdFiles.Find(ecuPath, ".prg", ".grp"))
        {
            var file = Path.GetFileName(path);
            string text;
            try { text = Disassemble(path); }
            catch (Exception ex) { errors.Add((file, Messages(ex))); continue; }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mnemonic in Mnemonics(text))
            {
                counts[mnemonic] = counts.TryGetValue(mnemonic, out var n) ? n + 1 : 1;
                if (unimplementedNames.Contains(mnemonic) && seen.Add(mnemonic))
                    unimplemented.Add((file, mnemonic));
            }
        }
        return new OpcodeReport(counts, unimplemented, errors);
    }

    /// BESTDIS writes <name>.b1v (or .b1g) beside its input, so the input is copied into a
    /// fresh temporary folder first: the census never writes into the SGBD folder.
    static string Disassemble(string sgbd)
    {
        CheckLayout(sgbd);
        var tmp = Path.Combine(Path.GetTempPath(), "census-bestdis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var input = Path.Combine(tmp, Path.GetFileName(sgbd));
            File.Copy(sgbd, input);
            var main = Bestdis.GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static)!;
            int rc;
            try { rc = (int)main.Invoke(null, new object[] { new[] { input } })!; }
            catch (TargetInvocationException tie) when (tie.InnerException != null) { throw tie.InnerException; }
            finally { CloseOutput(); }
            if (rc != 0) throw new InvalidOperationException("BESTDIS returned " + rc);
            var ext = Path.GetExtension(input).Equals(".grp", StringComparison.OrdinalIgnoreCase) ? ".b1g" : ".b1v";
            var output = Path.Combine(tmp, Path.GetFileNameWithoutExtension(input) + ext);
            return File.ReadAllText(output, Encoding.GetEncoding(1252));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// BESTDIS reads with FileStream.Read and ignores short reads, so a truncated file
    /// "disassembles" from stale buffers without an error. Check first that the header is
    /// whole, that every section pointer (0x78-0x98, -1 for absent) lies inside the file, and
    /// that the job table fits. A file cut off inside one job's code can still pass this.
    internal static void CheckLayout(string sgbd)
    {
        var b = File.ReadAllBytes(sgbd);
        const int header = 0x9C;
        if (b.Length < header)
            throw new InvalidDataException($"truncated: {b.Length} bytes, shorter than the 0x{header:X} byte header");
        for (int at = 0x78; at < header; at += 4)
        {
            int ptr = BitConverter.ToInt32(b, at);
            if (ptr != -1 && (ptr < 0 || ptr >= b.Length))
                throw new InvalidDataException($"truncated or corrupt: pointer at 0x{at:X2} is 0x{ptr:X}, file is 0x{b.Length:X} bytes");
        }
        int jobList = BitConverter.ToInt32(b, 0x88);
        if (jobList == -1 || jobList + 4 > b.Length)
            throw new InvalidDataException("corrupt: no job table");
        long jobsEnd = jobList + 4L + (long)BitConverter.ToInt32(b, jobList) * 0x44;
        if (BitConverter.ToInt32(b, jobList) < 0 || jobsEnd > b.Length)
            throw new InvalidDataException($"truncated or corrupt: job table ends at 0x{jobsEnd:X}, file is 0x{b.Length:X} bytes");
    }

    /// After an exception BESTDIS leaves its static output writer open; close it so the
    /// temporary folder can go.
    static void CloseOutput()
    {
        var f = Bestdis.GetField("output", BindingFlags.NonPublic | BindingFlags.Static)!;
        (f.GetValue(null) as IDisposable)?.Dispose();
        f.SetValue(null, null);
    }

    // disassembleJob writes each instruction as 20 spaces then the line, or, at a jump
    // target, "__XXXXXXXX: " plus 8 spaces then the line. Nothing else BESTDIS writes starts
    // that way: job names end in '#', comments start with ';', tables with TBEG/HEAD/LINE/TEND.
    [GeneratedRegex(@"^(?: {20}|__[0-9A-F]{8}: {8})([a-z0-9_]+)", RegexOptions.Multiline)]
    private static partial Regex InstructionLine();

    static IEnumerable<string> Mnemonics(string text) =>
        InstructionLine().Matches(text).Select(m => m.Groups[1].Value);

    static void RegisterEncodings() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    static string Messages(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null; e = e.InnerException)
            parts.Add(e.GetType().Name + ": " + e.Message);
        return string.Join(" <- ", parts);
    }
}
