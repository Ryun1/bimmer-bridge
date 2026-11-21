using System.Diagnostics;

namespace EdiabasAgent.Windows;

/// <summary>Facts about the agent's own process.</summary>
internal static class ProcessInfo
{
    /// <summary>TokenElevation of the process token; false if it cannot be read.</summary>
    public static bool IsElevated()
    {
        if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), NativeMethods.TOKEN_QUERY, out var token))
        {
            return false;
        }

        try
        {
            return NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevation, out var elevation, sizeof(int), out _)
                && elevation != 0;
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

    public static int SessionId()
    {
        using var p = Process.GetCurrentProcess();
        return p.SessionId;
    }

    /// <summary>Attaches to the parent's console (if any) so a WinExe can print for --version/--selftest.</summary>
    public static bool AttachParentConsole()
    {
        try
        {
            return NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
