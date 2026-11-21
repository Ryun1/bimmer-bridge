namespace EdiabasAgent.Core;

/// <summary>
/// Matches process names against the configured conflict list: case-insensitive, one trailing
/// <c>.exe</c> ignored on either side, whole-name match only (never a substring).
/// </summary>
public sealed class ConflictMatcher
{
    private readonly HashSet<string> _names;

    public ConflictMatcher(IEnumerable<string?> configuredNames)
    {
        _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in configuredNames)
        {
            if (raw is null)
            {
                continue;
            }

            var n = Normalize(raw.Trim());
            if (n.Length > 0)
            {
                _names.Add(n);
            }
        }
    }

    public bool IsConflict(string? processName)
    {
        if (string.IsNullOrEmpty(processName))
        {
            return false;
        }

        var n = Normalize(processName);
        return n.Length > 0 && _names.Contains(n);
    }

    /// <summary>Removes one trailing <c>.exe</c> (any case).</summary>
    public static string Normalize(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}
