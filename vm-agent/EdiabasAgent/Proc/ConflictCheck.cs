using System.Diagnostics;
using EdiabasAgent.Core;

namespace EdiabasAgent.Proc;

/// <summary>Lists running processes whose name is on the conflict list (by name only in Phase 1).</summary>
internal sealed class ConflictCheck
{
    private readonly ConflictMatcher _matcher;

    public ConflictCheck(IEnumerable<string> conflictProcesses)
    {
        _matcher = new ConflictMatcher(conflictProcesses);
    }

    public IReadOnlyList<ConflictingProcess> Running()
    {
        var found = new List<ConflictingProcess>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                string name;
                try
                {
                    name = p.ProcessName;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    continue;
                }

                if (_matcher.IsConflict(name))
                {
                    found.Add(new ConflictingProcess(ConflictMatcher.Normalize(name), p.Id));
                }
            }
        }

        return found
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Pid)
            .ToList();
    }
}
