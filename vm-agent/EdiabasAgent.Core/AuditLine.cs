using System.Globalization;
using System.Text;

namespace EdiabasAgent.Core;

/// <summary>
/// Formats one audit line: tab-separated
/// <c>time  remote  method  path  status  errorCode  durationMs</c>.
/// Query strings are reduced to their keys. Control characters (including tabs and every line
/// break, also U+0085/U+2028/U+2029) are replaced so a request cannot forge or split a line.
/// </summary>
public static class AuditLine
{
    private const int MaxFieldLength = 512;

    public static string Format(
        DateTimeOffset time,
        string? remoteAddress,
        string? method,
        string? path,
        string? queryString,
        int status,
        string? errorCode,
        long durationMs)
    {
        var target = ReduceTarget(path, queryString);
        var sb = new StringBuilder(128);
        sb.Append(time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)).Append('\t');
        sb.Append(Clean(remoteAddress)).Append('\t');
        sb.Append(Clean(method)).Append('\t');
        sb.Append(Clean(target)).Append('\t');
        sb.Append(status.ToString(CultureInfo.InvariantCulture)).Append('\t');
        sb.Append(Clean(errorCode)).Append('\t');
        sb.Append(Math.Max(0, durationMs).ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>Path plus <c>?key1&amp;key2</c>; query values are never written.</summary>
    private static string ReduceTarget(string? path, string? queryString)
    {
        path ??= "";
        var query = queryString ?? "";

        // A path that still carries a query is split too, so values never leak.
        var q = path.IndexOf('?');
        if (q >= 0)
        {
            var embedded = path[(q + 1)..];
            path = path[..q];
            query = query.Length == 0 ? embedded : embedded + "&" + query.TrimStart('?');
        }

        query = query.TrimStart('?');
        if (query.Length == 0)
        {
            return path;
        }

        var keys = query.Split('&').Select(part =>
        {
            var eq = part.IndexOf('=');
            return eq >= 0 ? part[..eq] : part;
        });
        return path + "?" + string.Join("&", keys);
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "-";
        }

        var sb = new StringBuilder(Math.Min(value.Length, MaxFieldLength));
        foreach (var c in value)
        {
            if (sb.Length >= MaxFieldLength)
            {
                sb.Append("...");
                break;
            }

            sb.Append(char.IsControl(c) || c == '\u2028' || c == '\u2029' ? '_' : c);
        }

        return sb.ToString();
    }
}
