namespace EdiabasAgent.Core;

/// <summary>
/// Why <c>--selftest</c> skips loading api32.dll, from what the conflict check found. The running
/// agent holding the DLL itself is not "a BMW tool": it releases the DLL once idle.
/// </summary>
public static class DllProbeSkip
{
    /// <summary>The agent's process name as the conflict check reports it (no <c>.exe</c>).</summary>
    public const string AgentProcessName = "EdiabasAgent";

    /// <summary>The SKIP detail for the self-test's EDIABAS DLL check.</summary>
    public static string Reason(IReadOnlyList<ConflictingProcess> running, int idleReleaseSeconds)
    {
        var others = running.Where(p => !IsAgent(p)).ToList();
        if (others.Count == 0)
        {
            return $"not loaded because the running agent has api32.dll loaded itself ({List(running)}); " +
                   $"it releases it after {idleReleaseSeconds} s without an EDIABAS request, so run the self-test again then";
        }

        return $"not loaded because a BMW tool is running ({List(others)}); close it and run the self-test again";
    }

    private static bool IsAgent(ConflictingProcess p) =>
        string.Equals(p.Name, AgentProcessName, StringComparison.OrdinalIgnoreCase);

    private static string List(IEnumerable<ConflictingProcess> processes) =>
        string.Join(", ", processes.Select(p => $"{p.Name} pid {p.Pid}"));
}
