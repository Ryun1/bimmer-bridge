using EdiabasLib.Native.Tests;

namespace Census.Tests;

/// Copies named upstream test SGBDs into fresh temp directories. Only cmd_test1, cmd_test2,
/// base1, base2 and cmd_ident are ever copied, so the census never sees upstream's
/// BMW-derived files. The directories are deleted when the test process exits.
public static class TestFiles
{
    static readonly string[] Allowed = { "cmd_test1", "cmd_test2", "base1", "base2", "cmd_ident" };
    static readonly List<string> Created = new();

    static TestFiles()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            lock (Created)
                foreach (var d in Created)
                    try { Directory.Delete(d, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };
    }

    public static string TempCopy(params string[] names)
    {
        var dir = NewDir();
        foreach (var n in names) CopyInto(dir, n, n);
        return dir;
    }

    public static string TempCopyAs(string name, string newName)
    {
        var dir = NewDir();
        CopyInto(dir, name, newName);
        return dir;
    }

    static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "census-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        lock (Created) Created.Add(dir);
        return dir;
    }

    static void CopyInto(string dir, string name, string newName)
    {
        if (!Allowed.Contains(Path.GetFileNameWithoutExtension(name), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(name + " is not one of the allowed upstream test SGBDs");
        File.Copy(Path.Combine(TestPaths.UpstreamTestEcu, name), Path.Combine(dir, newName));
    }
}
