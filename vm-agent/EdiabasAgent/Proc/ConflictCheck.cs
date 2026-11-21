using System.ComponentModel;
using System.Diagnostics;
using EdiabasAgent.Core;

namespace EdiabasAgent.Proc;

/// <summary>
/// Processes that hold the cable: every running process whose name is on the conflict list,
/// plus (best effort) any other process that has the EDIABAS DLL loaded. The agent's own process
/// is never counted. A process whose modules cannot be read (64-bit processes cannot be inspected
/// from this 32-bit agent; protected ones refuse) is judged by its name only. Feeds both /health
/// and every EDIABAS endpoint.
/// </summary>
internal sealed class ConflictCheck : IConflictCheck
{
    private readonly ConflictMatcher _matcher;
    private readonly HashSet<string> _dllNames;

    public ConflictCheck(IEnumerable<string> conflictProcesses, string ediabasDll)
    {
        _matcher = new ConflictMatcher(conflictProcesses);
        _dllNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "api32.dll" };
        try
        {
            var configured = Path.GetFileName(ediabasDll);
            if (!string.IsNullOrEmpty(configured))
            {
                _dllNames.Add(configured);
            }
        }
        catch (ArgumentException)
        {
            // Keep api32.dll only.
        }
    }

    public IReadOnlyList<ConflictingProcess> Running()
    {
        var self = Environment.ProcessId;
        var found = new List<ConflictingProcess>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == self || p.Id is 0 or 4)
                {
                    continue;
                }

                string name;
                try
                {
                    name = p.ProcessName;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    continue;
                }

                if (_matcher.IsConflict(name) || HasEdiabasLoaded(p))
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

    private bool HasEdiabasLoaded(Process p)
    {
        try
        {
            foreach (ProcessModule m in p.Modules)
            {
                using (m)
                {
                    if (_dllNames.Contains(m.ModuleName))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            // Cannot be inspected (other bitness, protected, exited): judged by name only.
        }

        return false;
    }
}
