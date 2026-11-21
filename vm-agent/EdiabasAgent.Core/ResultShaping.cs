using System.Globalization;

namespace EdiabasAgent.Core;

/// <summary>Every result set of a finished job, read through <see cref="IEdiabasApi"/>.</summary>
public sealed record ReadResults(ResultSet SystemResults, IReadOnlyList<ResultSet> Sets)
{
    /// <summary>System result <c>VARIANTE</c> (what a group name resolved to), else null.</summary>
    public string? Variant => SystemResults.TryGetValue("VARIANTE", out var v) ? v as string : null;

    /// <summary><c>JOB_STATUS</c> of the first set that has one (set 0 first), else null.</summary>
    public string? JobStatus =>
        new[] { SystemResults }.Concat(Sets)
            .Select(s => s.TryGetValue("JOB_STATUS", out var v) ? v as string : null)
            .FirstOrDefault(v => v is not null);
}

/// <summary>
/// Turns EDIABAS result sets into the JSON shape of docs/api.md: set 0 is the system results,
/// sets 1..n follow in order; text as strings, char/byte/integer/word/long/dword/real as
/// numbers, binary as upper-case hex; a result that cannot be read is null.
/// </summary>
public static class ResultShaping
{
    /// <summary>Reads every set. Null when EDIABAS cannot say how many sets there are.</summary>
    public static ReadResults? ReadAll(IEdiabasApi api)
    {
        if (!api.ResultSets(out var count) || count < 0)
        {
            return null;
        }

        var system = ReadSet(api, 0);
        var sets = new List<ResultSet>(count);
        for (var s = 1; s <= count; s++)
        {
            sets.Add(ReadSet(api, s));
        }

        return new ReadResults(system, sets);
    }

    public static ResultSet ReadSet(IEdiabasApi api, int set)
    {
        var result = new ResultSet();
        if (!api.ResultNumber(set, out var n))
        {
            return result;
        }

        for (var i = 1; i <= n; i++)
        {
            if (!api.ResultName(i, set, out var name) || string.IsNullOrEmpty(name))
            {
                continue;
            }

            // A name seen twice in one set keeps its first value.
            if (!result.ContainsKey(name))
            {
                result.Add(name, ReadValue(api, name, set));
            }
        }

        return result;
    }

    public static object? ReadValue(IEdiabasApi api, string name, int set)
    {
        if (!api.ResultFormat(name, set, out var format))
        {
            return null;
        }

        switch (format)
        {
            case ApiResultFormat.Char:
                return api.ResultChar(name, set, out var c) ? c : null;
            case ApiResultFormat.Byte:
                return api.ResultByte(name, set, out var b) ? b : null;
            case ApiResultFormat.Integer:
                return api.ResultInt(name, set, out var i) ? i : null;
            case ApiResultFormat.Word:
                return api.ResultWord(name, set, out var w) ? w : null;
            case ApiResultFormat.Long:
                return api.ResultLong(name, set, out var l) ? l : null;
            case ApiResultFormat.DWord:
                return api.ResultDWord(name, set, out var d) ? d : null;
            case ApiResultFormat.Real:
                // JSON has no NaN or infinity.
                return api.ResultReal(name, set, out var r) && double.IsFinite(r) ? r : null;
            case ApiResultFormat.Text:
                return api.ResultText(name, set, out var t) ? t : null;
            case ApiResultFormat.Binary:
                return api.ResultBinary(name, set, out var bin) ? Convert.ToHexString(bin) : null;
            default:
                return null;
        }
    }

    /// <summary>A value as text, for descriptions and argument lists.</summary>
    public static string? AsText(object? value) => value switch
    {
        null => null,
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
