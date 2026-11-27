namespace EdiabasLib.Native.Tests;

public static class TestPaths
{
    const string EcuRelative = "third_party/ediabaslib/EdiabasLib/Test/Ecu";

    /// Absolute path to upstream's test SGBD folder in the submodule, found by walking up
    /// from the test binary to the repo root. Tests use only cmd_test1, cmd_test2, base1,
    /// base2 and cmd_ident from it, never the BMW-derived files beside them.
    public static string UpstreamTestEcu { get; } = Find();

    static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, EcuRelative);
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException(
            EcuRelative + " not found above " + AppContext.BaseDirectory + "; run `git submodule update --init`");
    }
}
