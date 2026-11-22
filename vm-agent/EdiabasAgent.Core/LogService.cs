using System.Globalization;
using System.Text.RegularExpressions;

namespace EdiabasAgent.Core;

public sealed record LogListQuery(int? Root, string? Glob, DateTimeOffset? Since, int Limit);

public sealed record LogReadQuery(string Path, LogReadRange Range, string? Grep);

/// <summary>
/// Everything the log endpoints decide, against <see cref="ILogFileSystem"/>. Read-only: the
/// file system seam has no call that writes.
/// <para>A read is allowed only when the path passes <see cref="PathRules"/> as text, and the
/// file actually opened, after following every link, lies under a log root as that root
/// resolves now. The check is made on the open handle and the same handle is read, so the
/// file cannot be swapped between the check and the read.</para>
/// </summary>
public sealed class LogService
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int MaxGlobLength = 200;

    /// <summary>Folders below a root that are searched: root\a\b\c\file at most.</summary>
    public const int MaxDepth = 3;

    /// <summary>Stop a listing after this many entries seen, so a huge tree cannot stall the agent.</summary>
    public const int MaxScannedEntries = 20000;

    private static readonly string[] SinceFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    private readonly IReadOnlyList<string> _roots;
    private readonly ILogFileSystem _fs;
    private readonly IRunnerClock _clock;

    public LogService(IReadOnlyList<string> roots, ILogFileSystem fs, IRunnerClock? clock = null)
    {
        _roots = roots.Select(PathRules.TrimRoot).ToList();
        _fs = fs;
        _clock = clock ?? new SystemRunnerClock();
    }

    // ------------------------------------------------------------------ query parsing

    public static Outcome<LogListQuery> ParseList(QueryValues q, int rootCount)
    {
        int? root = null;
        if (q.Get("root") is { } r)
        {
            if (LogText.ParseWhole(r, 0, int.MaxValue) is not { } n || n >= rootCount)
            {
                return RequestParsing.Bad("root", rootCount == 0
                    ? "No log roots are configured."
                    : $"root must be a root index, 0 to {rootCount - 1}.");
            }

            root = (int)n;
        }

        var glob = q.Get("glob");
        if (glob is not null && (glob.Length is < 1 or > MaxGlobLength
                                 || glob.Any(c => char.IsControl(c) || c is '\\' or '/' or ':' or '\u2028' or '\u2029')))
        {
            return RequestParsing.Bad("glob", $"glob must be a file-name pattern of 1 to {MaxGlobLength} characters, with * and ? only and no folder.");
        }

        DateTimeOffset? since = null;
        if (q.Get("since") is { } s)
        {
            if (!DateTimeOffset.TryParseExact(s, SinceFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
            {
                return RequestParsing.Bad("since", "since must be an ISO 8601 date or time, such as 2026-11-21T16:40:00Z.");
            }

            since = t;
        }

        var limit = DefaultLimit;
        if (q.Get("limit") is { } l)
        {
            if (LogText.ParseWhole(l, 1, MaxLimit) is not { } n)
            {
                return RequestParsing.Bad("limit", $"limit must be a whole number from 1 to {MaxLimit}.");
            }

            limit = (int)n;
        }

        return new LogListQuery(root, glob, since, limit);
    }

    public static Outcome<LogReadQuery> ParseRead(QueryValues q)
    {
        var path = q.Get("path");
        if (path is null)
        {
            return RequestParsing.Bad("path", "path is required.");
        }

        var tailText = q.Get("tailBytes");
        var offsetText = q.Get("offset");
        var lengthText = q.Get("length");
        if (tailText is not null && (offsetText is not null || lengthText is not null))
        {
            return RequestParsing.Bad("tailBytes", "Give either tailBytes, or offset and length, not both.");
        }

        LogReadRange range;
        if (offsetText is not null || lengthText is not null)
        {
            if (offsetText is null || lengthText is null)
            {
                return RequestParsing.Bad(offsetText is null ? "offset" : "length", "offset and length go together: give both.");
            }

            if (LogText.ParseWhole(offsetText, 0, long.MaxValue) is not { } offset)
            {
                return RequestParsing.Bad("offset", "offset must be a whole number of bytes, 0 or more.");
            }

            if (LogText.ParseWhole(lengthText, 1, long.MaxValue) is not { } length)
            {
                return RequestParsing.Bad("length", "length must be a whole number of bytes, 1 or more.");
            }

            range = LogReadRange.At(offset, length);
        }
        else if (tailText is not null)
        {
            if (LogText.ParseWhole(tailText, 1, long.MaxValue) is not { } tail)
            {
                return RequestParsing.Bad("tailBytes", "tailBytes must be a whole number of bytes, 1 or more.");
            }

            range = LogReadRange.Tail(tail);
        }
        else
        {
            range = LogReadRange.Tail(LogReadRange.DefaultTailBytes);
        }

        var grep = q.Get("grep");
        if (grep is not null)
        {
            LogText.CompileGrep(grep, out var bad);
            if (bad is not null)
            {
                return bad;
            }
        }

        return new LogReadQuery(path, range, grep);
    }

    // ------------------------------------------------------------------ /logs

    public int RootCount => _roots.Count;

    public LogListResponse List(LogListQuery query)
    {
        var roots = new List<LogRoot>(_roots.Count);
        var found = new List<LogFileEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var budget = MaxScannedEntries;
        var cut = false;

        // The same roots /logs/read accepts: a root that leads somewhere a read would refuse
        // (a share, a bare drive, another kind of drive) is shown missing and never walked.
        var usable = UsableRoots();
        for (var i = 0; i < _roots.Count; i++)
        {
            var root = _roots[i];
            var exists = usable.Any(r => r.Index == i);
            roots.Add(new LogRoot(i, root, exists));
            if (!exists || (query.Root is { } only && only != i))
            {
                continue;
            }

            cut |= !Walk(root, i, query, found, seen, ref budget);
        }

        var ordered = found
            .OrderByDescending(f => f.ModifiedAt, StringComparer.Ordinal)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var files = ordered.Take(query.Limit).ToList();
        return new LogListResponse(roots, files, cut || ordered.Count > files.Count);
    }

    /// <summary>Breadth-first below one root. False when the scan budget ran out.</summary>
    private bool Walk(string root, int index, LogListQuery query, List<LogFileEntry> found, HashSet<string> seen, ref int budget)
    {
        var level = new List<string> { root };
        for (var depth = 0; depth <= MaxDepth && level.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var dir in level)
            {
                var entries = _fs.ListDirectory(dir);
                if (entries is null)
                {
                    continue;
                }

                foreach (var e in entries)
                {
                    if (--budget < 0)
                    {
                        return false;
                    }

                    // Links are never followed and never listed: a link's size and date would
                    // describe a file that may lie outside the roots.
                    if (e.IsReparsePoint)
                    {
                        continue;
                    }

                    var path = dir + "\\" + e.Name;
                    if (PathRules.CheckSegment(e.Name) is not null || PathRules.Check(path) is not null)
                    {
                        // A name the read endpoint would refuse is not offered.
                        continue;
                    }

                    if (e.IsDirectory)
                    {
                        if (depth < MaxDepth)
                        {
                            next.Add(path);
                        }

                        continue;
                    }

                    if (query.Glob is { } g && !Glob.IsMatch(e.Name, g))
                    {
                        continue;
                    }

                    if (query.Since is { } since && e.ModifiedUtc < since)
                    {
                        continue;
                    }

                    if (seen.Add(path))
                    {
                        found.Add(new LogFileEntry(path, index, e.SizeBytes, ApiFormat.Timestamp(e.ModifiedUtc)));
                    }
                }
            }

            level = next;
        }

        return true;
    }

    // ------------------------------------------------------------------ /logs/read

    /// <summary>
    /// Reads part of a log file. <paramref name="refusal"/> is the <see cref="PathRules"/>
    /// reason when the path was refused (for the audit line).
    /// </summary>
    public Outcome<LogReadResponse> Read(LogReadQuery query, out string? refusal)
    {
        refusal = null;
        var path = query.Path;

        if (PathRules.Check(path) is { } textReason)
        {
            refusal = textReason;
            return NotAllowed(textReason);
        }

        var roots = UsableRoots();

        // The text must name a place under a usable root (as configured, or where it resolves)
        // before anything is opened. A path on another drive (a mapped network drive among
        // them), a link from elsewhere into a root, or a short-name spelling of a root is
        // refused here without being touched: opening it could make the agent, which runs
        // elevated, connect to a share or spin up a drive. The handle check below still decides
        // where an accepted path really leads.
        if (!roots.Any(r => PathRules.IsUnder(path, r.Configured) || PathRules.IsUnder(path, r.Resolved)))
        {
            refusal = PathRules.OutsideRoots;
            return NotAllowed(refusal);
        }

        var status = _fs.OpenRead(path, out var file);
        if (status != LogOpenStatus.Opened || file is null)
        {
            file?.Dispose();
            return status == LogOpenStatus.NotFound
                ? ApiFailure.Of(ApiErrors.FileNotFound, "The log file does not exist.")
                : ApiFailure.Of(ApiErrors.InternalError, "The log file exists but could not be opened for reading (in use or not readable).");
        }

        using (file)
        {
            var final = PathRules.NormalizeFinal(file.FinalPath);
            if (final is null)
            {
                refusal = PathRules.Unresolvable;
                return NotAllowed(refusal);
            }

            if (!roots.Any(r => PathRules.IsUnder(final, r.Resolved)))
            {
                refusal = PathRules.ResolvesOutsideRoots;
                return NotAllowed(refusal);
            }

            if (!file.IsRegularFile)
            {
                refusal = PathRules.NotAFile;
                return NotAllowed(refusal);
            }

            // A second name for a file is a way out that no path check can see: the other name
            // may be anywhere on the volume. Trace files never have one.
            if (file.LinkCount != 1)
            {
                refusal = PathRules.HardLink;
                return NotAllowed(refusal);
            }

            Regex? regex = null;
            if (query.Grep is not null)
            {
                regex = LogText.CompileGrep(query.Grep, out var bad);
                if (bad is not null)
                {
                    return bad;
                }
            }

            var size = file.Length;
            var range = LogText.Select(size, query.Range);
            var buffer = new byte[range.Length];
            var got = 0;
            while (got < buffer.Length)
            {
                var n = file.Read(range.Offset + got, buffer.AsSpan(got));
                if (n <= 0)
                {
                    break;
                }

                got += n;
            }

            var bytes = buffer.AsSpan(0, got);
            var endsAtFileEnd = range.Offset + got >= size;
            var text = LogText.Decode(bytes, range.Offset == 0, endsAtFileEnd, out var encoding, out var skipStart, out var skipEnd);
            var offset = range.Offset + skipStart;
            var length = got - skipStart - skipEnd;
            var truncated = query.Range.TailBytes is not null ? offset > 0 : offset + length < size;

            GrepInfo? grepInfo = null;
            if (regex is not null)
            {
                try
                {
                    text = LogText.Grep(text, regex, () => _clock.ElapsedMs, out var matched);
                    grepInfo = new GrepInfo(query.Grep!, matched);
                }
                catch (RegexMatchTimeoutException)
                {
                    return RequestParsing.Bad("grep", "grep took too long; use a simpler pattern or a smaller range.");
                }
            }

            return new LogReadResponse(final, size, ApiFormat.Timestamp(file.ModifiedUtc), offset, length, encoding, truncated, grepInfo, text);
        }
    }

    private sealed record ResolvedRoot(int Index, string Configured, string Resolved);

    /// <summary>
    /// Roots as they resolve now (a root may be a link itself), for both /logs and /logs/read.
    /// Unusable ones, missing ones, any whose resolved path is not a plain path (a share, a
    /// bare drive, a volume path), and any configured on or leading to a drive that is not a
    /// local fixed disk are dropped.
    /// </summary>
    private List<ResolvedRoot> UsableRoots()
    {
        var list = new List<ResolvedRoot>();
        for (var i = 0; i < _roots.Count; i++)
        {
            var root = _roots[i];
            // A root on a network, removable or optical drive is not opened and grants nothing.
            if (PathRules.Check(root) is not null || !_fs.IsLocalDisk(root[0]))
            {
                continue;
            }

            var resolved = PathRules.NormalizeFinal(_fs.ResolveDirectory(root));
            if (resolved is not null && _fs.IsLocalDisk(resolved[0]))
            {
                list.Add(new ResolvedRoot(i, root, resolved));
            }
        }

        return list;
    }

    private static ApiFailure NotAllowed(string reason) =>
        ApiFailure.Of(ApiErrors.PathNotAllowed, ReasonText(reason), new ReasonDetails(reason));

    private static string ReasonText(string reason) => reason switch
    {
        PathRules.OutsideRoots => "The path is not under a configured log root.",
        PathRules.ResolvesOutsideRoots => "The path leads outside the log roots (through a link, junction or short name).",
        PathRules.HardLink => "The file has more than one name (hard link) and is not read.",
        PathRules.NotAFile => "The path is not a regular file on a local disk.",
        PathRules.Unresolvable => "The real location of the file could not be determined.",
        _ => $"The path is refused ({reason}). Give an absolute path such as C:\\EDIABAS\\TRACE\\api.trc.",
    };
}

/// <summary>File-name patterns with <c>*</c> and <c>?</c> only, case-insensitive. No regex.</summary>
public static class Glob
{
    public static bool IsMatch(string name, string pattern)
    {
        int n = 0, p = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = n;
            }
            else if (p < pattern.Length && (pattern[p] == '?' || Same(pattern[p], name[n])))
            {
                n++;
                p++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    private static bool Same(char a, char b) => a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}
