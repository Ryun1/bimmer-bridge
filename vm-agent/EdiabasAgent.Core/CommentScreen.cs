using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EdiabasAgent.Core;

/// <summary>Outcome of screening one job description.</summary>
public sealed record ScreenResult(bool Passed, IReadOnlyList<string> Matched);

/// <summary>How a job is listed in <c>reviewedJobs</c>.</summary>
public enum ApprovalKind
{
    None,

    /// <summary><c>ECU:JOB@hash</c>: approved only while the description hashes to one of
    /// <see cref="JobApproval.Hashes"/>.</summary>
    Pinned,
}

public sealed record JobApproval(ApprovalKind Kind, IReadOnlySet<string> Hashes)
{
    public static readonly JobApproval None = new(ApprovalKind.None, new HashSet<string>());

    /// <summary>True when a pinned approval holds for this description (as the screen receives
    /// it). An empty or unread description never matches.</summary>
    public bool Matches(string? description) =>
        Kind == ApprovalKind.Pinned && !string.IsNullOrWhiteSpace(description) && Hashes.Contains(CommentScreen.DescriptionHash(description));
}

/// <summary>
/// The parsed <c>reviewedJobs</c> list. Immutable: a re-read builds a new one. Only
/// <c>ECU:JOB@hhhhhhhhhhhh</c> entries approve anything (12 lower-case hex digits, see
/// <see cref="CommentScreen.DescriptionHash"/>); any of several pinned hashes approves. An
/// unpinned <c>ECU:JOB</c> entry (0.4.0 and before) or a malformed one approves nothing and is
/// counted as ignored. ECU and JOB are matched exactly: the contract says upper case, and a near
/// miss must not approve anything.
/// </summary>
public sealed class ApprovalList
{
    public const int HashLength = 12;

    // Names: no whitespace, ':' or '@'. Their case is not checked here; a lower-case entry simply
    // never matches, as before.
    private static readonly Regex EntryShape = new(
        @"\A(?<key>[^\s:@]+:[^\s:@]+)(?:@(?<hash>[0-9a-f]{12}))?\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly Dictionary<string, HashSet<string>> _pinned = new(StringComparer.Ordinal);

    // Every distinct entry as written, pinned or ignored: what SameAs compares.
    private readonly HashSet<string> _all = new(StringComparer.Ordinal);

    public static readonly ApprovalList Empty = new([]);

    /// <param name="entries">From the agent config; an entry that is not pinned approves nothing.</param>
    public ApprovalList(IEnumerable<string> entries)
    {
        var pinnedEntries = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (e is null)
            {
                continue;
            }

            _all.Add(e);
            if (!TryParseEntry(e, out var key, out var hash) || hash is null)
            {
                continue;
            }

            pinnedEntries.Add(e);
            if (!_pinned.TryGetValue(key, out var set))
            {
                _pinned[key] = set = new HashSet<string>(StringComparer.Ordinal);
            }

            set.Add(hash);
        }

        Entries = [.. pinnedEntries];
        PinnedCount = Entries.Count;
        IgnoredCount = _all.Count - PinnedCount;
    }

    /// <summary>Distinct pinned entries.</summary>
    public int PinnedCount { get; }

    /// <summary>Distinct entries that approve nothing: unpinned or malformed.</summary>
    public int IgnoredCount { get; }

    /// <summary>The distinct pinned entries as written, sorted ordinal.</summary>
    public IReadOnlyList<string> Entries { get; }

    /// <summary>For /health.</summary>
    public ApprovalCounts Counts => new(PinnedCount, IgnoredCount);

    /// <summary>True for <c>ECU:JOB</c> and <c>ECU:JOB@</c> plus 12 lower-case hex digits; only
    /// the second, with a hash, approves anything.</summary>
    public static bool TryParseEntry(string? entry, out string key, out string? hash)
    {
        key = "";
        hash = null;
        if (entry is null)
        {
            return false;
        }

        Match m;
        try
        {
            m = EntryShape.Match(entry);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        if (!m.Success)
        {
            return false;
        }

        key = m.Groups["key"].Value;
        hash = m.Groups["hash"].Success ? m.Groups["hash"].Value : null;
        return true;
    }

    /// <summary>Takes the upper-cased names from the policy verdict.</summary>
    public JobApproval Lookup(string ecu, string job) =>
        _pinned.TryGetValue(ecu + ":" + job, out var hashes)
            ? new JobApproval(ApprovalKind.Pinned, hashes)
            : JobApproval.None;

    /// <summary>Same entries, ignored ones included, whatever their order.</summary>
    public bool SameAs(ApprovalList other) => _all.SetEquals(other._all);
}

/// <summary>The current <see cref="ApprovalList"/>, replaced whole when config.json is re-read.
/// Thread-safe: /health reads it while the runner may replace it.</summary>
public sealed class ApprovalState(ApprovalList initial)
{
    private volatile ApprovalList _current = initial;

    public ApprovalList Current
    {
        get => _current;
        set => _current = value;
    }
}

/// <summary>
/// The description screen for jobs allowed only by a pattern (docs/api.md "Description
/// screen"): every ignore entry is first replaced by a space, case-insensitively; then the
/// screen refuses when what is left mentions any screen term, case-insensitively, or when the
/// description is empty or nothing but words holding an ignore entry.
/// A job pinned in <c>reviewedJobs</c> skips this screen while its description is unchanged; it
/// never skips the name rules or the argument screen.
/// </summary>
public sealed class CommentScreen
{
    private readonly IReadOnlyList<string> _terms;
    private readonly IReadOnlyList<string> _ignore;
    private readonly ApprovalState _approvals;

    /// <param name="terms">From policy.json <c>descriptionScreen.terms</c>.</param>
    /// <param name="ignore">From policy.json <c>descriptionScreen.ignore</c>: labels such as
    /// "Jobbeschreibung" that contain a term but say nothing about what the job does.</param>
    /// <param name="approvals">From the agent config's <c>reviewedJobs</c>; the host re-reads it.</param>
    public CommentScreen(IReadOnlyList<string> terms, IReadOnlyList<string> ignore, ApprovalState approvals)
    {
        _terms = terms;
        _ignore = ignore;
        _approvals = approvals;
    }

    public CommentScreen(IReadOnlyList<string> terms, IReadOnlyList<string> ignore, IEnumerable<string> reviewedJobs)
        : this(terms, ignore, new ApprovalState(new ApprovalList(reviewedJobs)))
    {
    }

    /// <summary>How the user approved this job, if at all. Takes the upper-cased names from the verdict.</summary>
    public JobApproval Approval(string ecu, string job) => _approvals.Current.Lookup(ecu, job);

    /// <summary>
    /// What a pinned <c>reviewedJobs</c> entry holds: the first 12 hex digits (lower case) of the
    /// SHA-256 of the UTF-8 bytes of the description, trimmed. The description is the text the
    /// screen receives (every value of the <c>_JOBCOMMENTS</c> sets, one per line). 48 bits is
    /// plenty to notice a change; it is not meant to resist someone forging a description.
    /// </summary>
    public static string DescriptionHash(string description)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(description.Trim()));
        return Convert.ToHexStringLower(bytes)[..ApprovalList.HashLength];
    }

    public ScreenResult Screen(string? description)
    {
        // No terms means the policy did not load: nothing passes.
        if (string.IsNullOrWhiteSpace(description) || _terms.Count == 0)
        {
            return new ScreenResult(false, []);
        }

        // A space, not nothing: removing an entry must not join the text around it into a term.
        var text = description;
        foreach (var i in _ignore)
        {
            text = text.Replace(i, " ", StringComparison.OrdinalIgnoreCase);
        }

        var matched = _terms
            .Where(t => text.Contains(t, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matched.Count > 0)
        {
            return new ScreenResult(false, matched);
        }

        // Only a label ("Jobbeschreibung:"): as good as no description. Judged by whole words, so
        // the "Job" left of "Jobbeschreibung" does not count as something the job says it does.
        return new ScreenResult(HasTextBeyondIgnored(description), []);
    }

    /// <summary>True when a letter or digit remains once every whitespace-separated word that
    /// contains an ignore entry is set aside.</summary>
    private bool HasTextBeyondIgnored(string description) =>
        description
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !_ignore.Any(i => w.Contains(i, StringComparison.OrdinalIgnoreCase)))
            .Any(w => w.Any(char.IsLetterOrDigit));
}
