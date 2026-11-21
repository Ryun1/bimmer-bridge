namespace EdiabasAgent.Core;

/// <summary>Outcome of screening one job description.</summary>
public sealed record ScreenResult(bool Passed, IReadOnlyList<string> Matched);

/// <summary>
/// The description screen for jobs allowed only by a pattern (docs/api.md "Description
/// screen"): refuse when the description is empty or mentions any screen term,
/// case-insensitively. A job listed as <c>ECU:JOB</c> in <c>reviewedJobs</c> skips the screen,
/// never the name rules.
/// </summary>
public sealed class CommentScreen
{
    private readonly IReadOnlyList<string> _terms;
    private readonly HashSet<string> _reviewed;

    /// <param name="terms">From policy.json <c>descriptionScreen.terms</c>.</param>
    /// <param name="reviewedJobs">From the agent config. Matched exactly: the contract says
    /// entries are upper-case <c>ECU:JOB</c>, and a near miss must not approve anything.</param>
    public CommentScreen(IReadOnlyList<string> terms, IEnumerable<string> reviewedJobs)
    {
        _terms = terms;
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

        var matched = _terms
            .Where(t => description.Contains(t, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new ScreenResult(matched.Count == 0, matched);
    }
}
