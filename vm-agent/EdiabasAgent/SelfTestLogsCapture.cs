using System.Diagnostics;
using System.Runtime.InteropServices;
using EdiabasAgent.Capture;
using EdiabasAgent.Core;
using EdiabasAgent.Logs;
using EdiabasAgent.Windows;

namespace EdiabasAgent;

/// <summary>
/// Self-test checks for the log path rules on the real Windows file system, and a desktop
/// capture. The log checks build their cases (a junction and a symbolic link out of a root,
/// an alternate data stream, 8.3 short names, device names, a hard link) inside a temporary
/// folder of their own, with a temporary root, and delete it afterwards. They write nowhere
/// else. The capture check keeps the image in memory only.
/// </summary>
internal static partial class SelfTest
{
    private static IEnumerable<Check> LogPathChecks()
    {
        const string name = "log paths";
        string temp;
        try
        {
            temp = Path.Combine(Path.GetTempPath(), "ediabas-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
        }
        catch (Exception ex)
        {
            return [new Check(name, Outcome.Fail, $"cannot create a temporary folder: {ex.GetType().Name}: {ex.Message}")];
        }

        var checks = new List<Check>();
        var links = new List<string>();
        try
        {
            var root = Path.Combine(temp, "logroot");
            var outside = Path.Combine(temp, "outside-longfoldername");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);
            var inside = Path.Combine(root, "inside-longfilename.trc");
            var secret = Path.Combine(outside, "secret-longfilename.txt");
            File.WriteAllText(inside, "inside\n");
            File.WriteAllText(secret, "outside\n");

            var logs = new LogService([root], new LogAccess());

            checks.Add(Guard($"{name}: plain file", () => ExpectRead($"{name}: plain file", logs, inside)));
            checks.Add(Guard($"{name}: .. escape", () =>
                ExpectRefused($"{name}: .. escape", logs, root + @"\..\outside-longfoldername\secret-longfilename.txt", PathRules.DotSegment)));

            checks.Add(Guard($"{name}: junction escape", () =>
            {
                var jn = Path.Combine(root, "jn");
                if (!MakeJunction(jn, outside, out var why))
                {
                    return new Check($"{name}: junction escape", Outcome.Fail, $"could not create the test junction: {why}");
                }

                links.Add(jn);
                return ExpectRefused($"{name}: junction escape", logs, Path.Combine(jn, "secret-longfilename.txt"), PathRules.ResolvesOutsideRoots);
            }));

            checks.Add(Guard($"{name}: symlink escape", () =>
            {
                var link = Path.Combine(root, "link.trc");
                try
                {
                    File.CreateSymbolicLink(link, secret);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new Check($"{name}: symlink escape", Outcome.Skip, $"cannot create a symbolic link here ({ex.Message})");
                }

                links.Add(link);
                return ExpectRefused($"{name}: symlink escape", logs, link, PathRules.ResolvesOutsideRoots);
            }));

            checks.Add(Guard($"{name}: alternate data stream", () =>
            {
                var stream = inside + ":hidden";
                var made = true;
                try
                {
                    File.WriteAllText(stream, "stream\n");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    made = false;
                }

                var c = ExpectRefused($"{name}: alternate data stream", logs, stream, PathRules.AlternateDataStream);
                return made ? c : c with { Detail = c.Detail + " (the stream itself could not be created)" };
            }));

            checks.Add(Guard($"{name}: 8.3 short names", () =>
            {
                var shortSecret = ShortName(secret);
                var shortInside = ShortName(inside);
                if (shortSecret is null || shortInside is null
                    || string.Equals(shortSecret, secret, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(shortInside, inside, StringComparison.OrdinalIgnoreCase))
                {
                    return new Check($"{name}: 8.3 short names", Outcome.Skip, "this volume does not make 8.3 names");
                }

                var outsideCheck = ExpectRefused($"{name}: 8.3 short names", logs, shortSecret, PathRules.OutsideRoots, PathRules.ResolvesOutsideRoots);
                if (outsideCheck.Outcome != Outcome.Pass)
                {
                    return outsideCheck;
                }

                var insideCheck = ExpectRead($"{name}: 8.3 short names", logs, shortInside);
                return insideCheck.Outcome == Outcome.Pass
                    ? new Check($"{name}: 8.3 short names", Outcome.Pass, $"{shortSecret} refused, {shortInside} read")
                    : insideCheck;
            }));

            checks.Add(Guard($"{name}: device names", () =>
            {
                foreach (var device in new[] { "NUL", "con.trc", "COM1", "lpt1.log", "AUX" })
                {
                    var c = ExpectRefused($"{name}: device names", logs, Path.Combine(root, device), PathRules.DeviceName);
                    if (c.Outcome != Outcome.Pass)
                    {
                        return c;
                    }
                }

                return new Check($"{name}: device names", Outcome.Pass, "NUL, con.trc, COM1, lpt1.log, AUX refused");
            }));

            checks.Add(Guard($"{name}: hard link", () =>
            {
                var hard = Path.Combine(root, "hard.trc");
                if (!SelfTestNative.CreateHardLinkW(hard, secret, IntPtr.Zero))
                {
                    return new Check($"{name}: hard link", Outcome.Skip, $"cannot create a hard link here (error {Marshal.GetLastPInvokeError()})");
                }

                return ExpectRefused($"{name}: hard link", logs, hard, PathRules.HardLink);
            }));

            checks.Add(Guard($"{name}: listing", () =>
            {
                var listed = logs.List(new LogListQuery(null, null, null, LogService.MaxLimit)).Files.Select(f => f.Path).ToList();
                // The junction and the symbolic link are links: neither they nor anything behind them may be listed.
                var leaks = listed.Where(p => p.Contains("secret", StringComparison.OrdinalIgnoreCase)
                                              || p.Contains(@"\jn", StringComparison.OrdinalIgnoreCase)
                                              || p.EndsWith("link.trc", StringComparison.OrdinalIgnoreCase)).ToList();
                if (leaks.Count > 0)
                {
                    return new Check($"{name}: listing", Outcome.Fail, $"listed a file outside the root: {string.Join(", ", leaks)}");
                }

                return listed.Any(p => p.EndsWith("inside-longfilename.trc", StringComparison.OrdinalIgnoreCase))
                    ? new Check($"{name}: listing", Outcome.Pass, $"{listed.Count} file(s) listed, none through a link")
                    : new Check($"{name}: listing", Outcome.Fail, $"the plain file was not listed ({listed.Count} listed)");
            }));
        }
        catch (Exception ex)
        {
            checks.Add(new Check(name, Outcome.Fail, $"setup threw {ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            checks.Add(CleanUp(temp, links));
        }

        return checks;
    }

    private static Check ExpectRead(string name, LogService logs, string path)
    {
        var r = logs.Read(new LogReadQuery(path, LogReadRange.Tail(4096), null), out var refusal);
        return r.Ok
            ? new Check(name, Outcome.Pass, $"{path} read ({r.Value!.Length} bytes)")
            : new Check(name, Outcome.Fail, $"{path}: expected to be readable, got {r.Failure!.Error.Code} {refusal}");
    }

    private static Check ExpectRefused(string name, LogService logs, string path, params string[] reasons)
    {
        var r = logs.Read(new LogReadQuery(path, LogReadRange.Tail(4096), null), out var refusal);
        if (!r.Ok && r.Failure!.Error.Code == ApiErrors.PathNotAllowed && reasons.Contains(refusal))
        {
            return new Check(name, Outcome.Pass, $"{path} refused ({refusal})");
        }

        var got = r.Ok ? "the file's content" : $"{r.Failure!.Error.Code} ({refusal ?? "-"})";
        return new Check(name, Outcome.Fail, $"{path}: expected path_not_allowed ({string.Join(" or ", reasons)}), got {got}");
    }

    private static bool MakeJunction(string link, string target, out string why)
    {
        // mklink /J needs no privilege. Both paths are inside the self-test's temp folder.
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "/d", "/c", "mklink", "/J", link, target })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi);
        if (p is null)
        {
            why = "cmd.exe did not start";
            return false;
        }

        var stderr = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(10000))
        {
            p.Kill();
            why = "mklink did not finish";
            return false;
        }

        why = $"mklink exit code {p.ExitCode}: {stderr.Result.Trim()}";
        return p.ExitCode == 0 && Directory.Exists(link);
    }

    private static string? ShortName(string path)
    {
        var buffer = new char[1024];
        var n = SelfTestNative.GetShortPathNameW(path, buffer, (uint)buffer.Length);
        return n == 0 || n >= buffer.Length ? null : new string(buffer, 0, (int)n);
    }

    /// <summary>Links first (the link only, never its target), then the folder.</summary>
    private static Check CleanUp(string temp, List<string> links)
    {
        const string name = "log paths: clean-up";
        try
        {
            foreach (var link in links)
            {
                // Directory.Delete without recursion removes a junction itself; File.Delete a file link.
                if (Directory.Exists(link))
                {
                    Directory.Delete(link, recursive: false);
                }
                else if (File.Exists(link))
                {
                    File.Delete(link);
                }
            }

            // Every link target is inside the temp folder too, so nothing outside it can be reached.
            Directory.Delete(temp, recursive: true);
            return new Check(name, Outcome.Pass, $"removed {temp}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Check(name, Outcome.Fail, $"could not remove {temp}: {ex.Message}");
        }
    }

    private static Check CheckCapture()
    {
        const string name = "capture";
        var session = ProcessInfo.SessionId();
        if (session == 0)
        {
            return new Check(name, Outcome.Skip, "not in an interactive session (session 0): screenshots cannot work here");
        }

        var r = WindowCapture.Capture(new CaptureRequest(CaptureTarget.Desktop, CaptureRules.DefaultMaxEdge, CaptureFormat.Png), session);
        if (r.Ok)
        {
            var i = r.Value!;
            return new Check(name, Outcome.Pass, $"desktop {i.Width}x{i.Height} via {i.Method}, {i.Bytes.Length} bytes as PNG (kept in memory only)");
        }

        return r.Failure!.Error.Code == ApiErrors.DesktopUnavailable
            ? new Check(name, Outcome.Skip, r.Failure.Error.Message)
            : new Check(name, Outcome.Fail, $"{r.Failure.Error.Code}: {r.Failure.Error.Message}");
    }

    private static class SelfTestNative
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetShortPathNameW(string lpszLongPath, [Out] char[] lpszShortPath, uint cchBuffer);

        /// <summary>Self-test only, inside its own temp folder: a second name for a file outside the test root.</summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
    }
}
