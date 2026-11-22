namespace EdiabasAgent.Core;

/// <summary>Outcome of screening one job description.</summary>
public sealed record ScreenResult(bool Passed, IReadOnlyList<string> Matched);

/// <summary>
/// The description screen for jobs allowed only by a pattern (docs/api.md "Description
/// screen"): every ignore entry is first replaced by a space, case-insensitively; then the
/// screen refuses when what is left mentions any screen term, case-insensitively, or when the
/// description is empty or nothing but words holding an ignore entry.
/// A job listed as <c>ECU:JOB</c> in <c>reviewedJobs</c> skips the screen, never the name rules.
/// </summary>
public sealed class CommentScreen
{
    private readonly IReadOnlyList<string> _terms;
    private readonly IReadOnlyList<string> _ignore;
    private readonly HashSet<string> _reviewed;

    /// <param name="terms">From policy.json <c>descriptionScreen.terms</c>.</param>
    /// <param name="ignore">From policy.json <c>descriptionScreen.ignore</c>: labels such as
    /// "Jobbeschreibung" that contain a term but say nothing about what the job does.</param>
    /// <param name="reviewedJobs">From the agent config. Matched exactly: the contract says
    /// entries are upper-case <c>ECU:JOB</c>, and a near miss must not approve anything.</param>
    public CommentScreen(IReadOnlyList<string> terms, IReadOnlyList<string> ignore, IEnumerable<string> reviewedJobs)
    {
        _terms = terms;
        _ignore = ignore;
        _reviewed = new HashSet<string>(reviewedJobs, StringComparer.Ordinal);
    }

    /// <summary>True when Ryan approved this job. Takes the upper-cased names from the verdict.</summary>
    public bool IsReviewed(string ecu, string job) => _reviewed.Contains(ecu + ":" + job);

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
