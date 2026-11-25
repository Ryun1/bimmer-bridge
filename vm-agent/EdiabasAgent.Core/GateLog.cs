using System.Text.RegularExpressions;

namespace EdiabasAgent.Core;

/// <summary>
/// The XGate32.dll interface handler's log (gate/README.md "Log format"), as the telegram check
/// reads it. The gate refuses a telegram before <c>XStd32.dll</c> sees it, so a refused telegram
/// never reaches EDIABAS's <c>IFH.trc</c>: the gate's own log is the only record of it.
/// </summary>
public static class GateLog
{
    /// <summary>The gate's default, used when <c>XGate.ini</c> has no <c>LogPath</c>
    /// (<c>gate/src/ini.zig</c>).</summary>
    public const string DefaultPath = @"C:\EDIABAS\TRACE\XGate.log";

    public const string IniName = "XGate.ini";

    /// <summary><c>XGate.ini</c> beside <c>api32.dll</c>, where the gate reads it.</summary>
    public static string IniBesideDll(string dllPath)
    {
        var cut = Math.Max(dllPath.LastIndexOf('\\'), dllPath.LastIndexOf('/'));
        return cut < 0 ? IniName : dllPath[..(cut + 1)] + IniName;
    }

    /// <summary>The log path the gate uses for this <c>XGate.ini</c> text (null: no file), read as
    /// the gate reads it: <c>key=value</c> lines, keys in any case, <c>;</c>, <c>#</c> and
    /// <c>[</c> lines skipped, the last <c>LogPath</c> wins, an empty value means the default.</summary>
    public static string LogPathFrom(string? iniText)
    {
        string? path = null;
        if (iniText is not null)
        {
            var first = true;
            foreach (var raw in iniText.Split('\n'))
            {
                var line = raw.Trim(' ', '\t', '\r');
                if (first)
                {
                    first = false;
                    line = line.TrimStart('\uFEFF').Trim(' ', '\t', '\r');
                }

                if (line.Length == 0 || line[0] is ';' or '#' or '[')
                {
                    continue;
                }

                var eq = line.IndexOf('=');
                if (eq < 0)
                {
                    continue;
                }

                if (string.Equals(line[..eq].Trim(' ', '\t'), "LogPath", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line[(eq + 1)..].Trim(' ', '\t');
                    path = value.Length == 0 ? null : value;
                }
            }
        }

        return path ?? DefaultPath;
    }

    /// <summary>A refusal line: <c>REFUSED</c> (enforce: not sent) or <c>WOULD_REFUSE</c> (log
    /// mode: forwarded all the same), the reason, the function number and the data as hex
    /// (<c>-</c> empty, <c>(null)</c> no pointer, <c>+n</c> bytes not logged).</summary>
    private static readonly Regex RefusalLine = new(
        @"^\S+ \S+ (REFUSED|WOULD_REFUSE) ([a-z_]+) fkt=(-?\d+) frame=(\S*)\r?$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>A call the gate handled (every dllCallIFH is logged as one).</summary>
    private static readonly Regex CallLine = new(@"^\S+ \S+ CALL ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public sealed record Refusal(bool Sent, string Reason, int Function, string? Frame);

    public sealed record Segment(IReadOnlyList<Refusal> Refusals, int Calls);

    /// <summary>The refusals and the number of calls in a part of the log.</summary>
    public static Segment Parse(string text)
    {
        var refusals = new List<Refusal>();
        var calls = 0;
        foreach (var line in text.Split('\n'))
        {
            if (CallLine.IsMatch(line))
            {
                calls++;
                continue;
            }

            var m = RefusalLine.Match(line);
            if (!m.Success)
            {
                continue;
            }

            refusals.Add(new Refusal(
                Sent: m.Groups[1].Value == "WOULD_REFUSE",
                Reason: m.Groups[2].Value,
                Function: int.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
                Frame: SpacedHex(m.Groups[4].Value)));
        }

        return new Segment(refusals, calls);
    }

    /// <summary><c>8457F1</c> → <c>84 57 F1</c>; anything else (<c>-</c>, <c>(null)</c>, a
    /// capped <c>…+n</c>) as logged, or null when empty.</summary>
    private static string? SpacedHex(string s)
    {
        if (s.Length == 0 || s == "-")
        {
            return null;
        }

        if (s.Length % 2 != 0 || !s.All(Uri.IsHexDigit))
        {
            return s;
        }

        return string.Join(' ', Enumerable.Range(0, s.Length / 2).Select(i => s.Substring(i * 2, 2)));
    }
}
