namespace EdiabasAgent.Core;

/// <summary>
/// The two settings of <c>EDIABAS.INI</c> the telegram check needs: <c>IfhTrace</c> (the
/// interface trace level; 3 or more writes every call) and <c>TracePath</c> (where
/// <c>IFH.trc</c> is written). Read from the <c>[Configuration]</c> section; keys are matched
/// without regard to case; the first of a repeated key counts, as the Windows profile functions
/// read it; a line starting with <c>;</c> is a comment. Pure: text in, values out.
/// </summary>
public sealed record EdiabasIni(string? IfhTrace, string? TracePath)
{
    public const string FileName = "EDIABAS.INI";
    public const string TraceFileName = "IFH.trc";

    /// <summary>The level the check needs: 3 writes every interface call with its INPUT.</summary>
    public const int RequiredIfhTrace = 3;

    public static EdiabasIni Parse(string text)
    {
        string? ifhTrace = null;
        string? tracePath = null;
        var inConfiguration = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';')
            {
                continue;
            }

            if (line[0] == '[')
            {
                inConfiguration = string.Equals(line.TrimEnd(), "[Configuration]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            var eq = line.IndexOf('=');
            if (!inConfiguration || eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..];
            var comment = value.IndexOf(';');
            value = (comment >= 0 ? value[..comment] : value).Trim();
            if (string.Equals(key, "IfhTrace", StringComparison.OrdinalIgnoreCase))
            {
                ifhTrace ??= value;
            }
            else if (string.Equals(key, "TracePath", StringComparison.OrdinalIgnoreCase))
            {
                tracePath ??= value;
            }
        }

        return new EdiabasIni(ifhTrace, tracePath);
    }

    /// <summary>The <c>IfhTrace</c> level, or null when it is missing or not a whole number.</summary>
    public int? IfhTraceLevel =>
        int.TryParse(IfhTrace, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary><c>IFH.trc</c> in <see cref="TracePath"/>; null unless <c>TracePath</c> is a full
    /// local path (<c>C:\EDIABAS\TRACE</c>): a relative one would depend on EDIABAS's working folder.</summary>
    public string? TraceFile
    {
        get
        {
            var p = TracePath?.Trim();
            if (p is null || p.Length < 3 || !char.IsAsciiLetter(p[0]) || p[1] != ':' || (p[2] != '\\' && p[2] != '/'))
            {
                return null;
            }

            return p.Replace('/', '\\').TrimEnd('\\') + "\\" + TraceFileName;
        }
    }

    /// <summary><c>EDIABAS.INI</c> beside <c>api32.dll</c> (both are in <c>C:\EDIABAS\BIN</c>).</summary>
    public static string BesideDll(string dllPath)
    {
        var cut = Math.Max(dllPath.LastIndexOf('\\'), dllPath.LastIndexOf('/'));
        return cut < 0 ? FileName : dllPath[..(cut + 1)] + FileName;
    }
}
