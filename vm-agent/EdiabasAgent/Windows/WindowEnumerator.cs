using System.Diagnostics;
using EdiabasAgent.Core;

namespace EdiabasAgent.Windows;

/// <summary>
/// Lists visible top-level windows with a non-empty title, front first (EnumWindows order).
/// Read-only: queries window state only; never activates, moves, focuses or messages a window.
/// </summary>
internal static class WindowEnumerator
{
    private const int MaxTitleChars = 1024;

    public static IReadOnlyList<WindowInfo> List()
    {
        var handles = new List<IntPtr>();
        NativeMethods.EnumWindowsProc callback = (hWnd, _) =>
        {
            handles.Add(hWnd);
            return true;
        };

        if (!NativeMethods.EnumWindows(callback, IntPtr.Zero))
        {
            // EnumWindows can return FALSE with partial results; keep what we got.
        }

        GC.KeepAlive(callback);

        var processNames = new Dictionary<uint, string?>();
        var result = new List<WindowInfo>();

        foreach (var hWnd in handles)
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
            {
                continue;
            }

            if (IsCloaked(hWnd))
            {
                continue;
            }

            var title = GetTitle(hWnd);
            if (string.IsNullOrEmpty(title))
            {
                continue;
            }

            _ = NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
            if (!processNames.TryGetValue(pid, out var processName))
            {
                processName = TryGetProcessName(pid);
                processNames[pid] = processName;
            }

            var bounds = NativeMethods.GetWindowRect(hWnd, out var r)
                ? new Bounds(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)
                : new Bounds(0, 0, 0, 0);

            result.Add(new WindowInfo(
                ApiFormat.Handle(hWnd.ToInt64()),
                title,
                processName,
                unchecked((int)pid),
                bounds,
                NativeMethods.IsIconic(hWnd)));
        }

        return result;
    }

    private static bool IsCloaked(IntPtr hWnd)
    {
        var hr = NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    private static string GetTitle(IntPtr hWnd)
    {
        // GetWindowTextW does not send WM_GETTEXT to windows of other processes; the agent
        // itself owns no windows.
        var length = NativeMethods.GetWindowTextLengthW(hWnd);
        if (length <= 0)
        {
            return "";
        }

        var buffer = new char[Math.Min(length, MaxTitleChars) + 1];
        var copied = NativeMethods.GetWindowTextW(hWnd, buffer, buffer.Length);
        return copied <= 0 ? "" : new string(buffer, 0, Math.Min(copied, buffer.Length - 1));
    }

    private static string? TryGetProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById(unchecked((int)pid));
            var name = p.ProcessName;
            return string.IsNullOrEmpty(name) ? null : ConflictMatcher.Normalize(name);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}
