namespace EdiabasLib.Native.Tests;

/// A temporary Ecu folder holding upstream's test SGBDs, with cmd_test2's description block
/// rewritten so that INITIALISIERUNG is described as "initialisierung", the way many real BMW
/// SGBDs write it. The job table keeps the upper-case name. Built at test time from the
/// submodule's own file (nothing is committed) and deleted on Dispose.
public sealed class MixedCaseSgbd : IDisposable
{
    public string EcuPath { get; }

    public MixedCaseSgbd()
    {
        EcuPath = Path.Combine(Path.GetTempPath(), "mixedcase-sgbd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(EcuPath);
        foreach (var f in new[] { "cmd_test1.prg", "base1.prg", "base2.prg", "cmd_ident.grp" })
        {
            File.Copy(Path.Combine(TestPaths.UpstreamTestEcu, f), Path.Combine(EcuPath, f));
        }

        var bytes = File.ReadAllBytes(Path.Combine(TestPaths.UpstreamTestEcu, "cmd_test2.prg"));
        var from = Encrypt("JOBNAME:INITIALISIERUNG\n");
        var to = Encrypt("JOBNAME:initialisierung\n");
        var at = IndexOf(bytes, from);
        if (at < 0 || IndexOf(bytes, from, at + 1) >= 0)
        {
            throw new InvalidOperationException("cmd_test2.prg: expected exactly one described INITIALISIERUNG");
        }

        to.CopyTo(bytes, at);
        File.WriteAllBytes(Path.Combine(EcuPath, "cmd_test2.prg"), bytes);
    }

    public void Dispose()
    {
        try { Directory.Delete(EcuPath, recursive: true); } catch (IOException) { }
    }

    /// SGBD text sections are stored XOR 0xF7.
    static byte[] Encrypt(string s) => s.Select(c => (byte)(c ^ 0xF7)).ToArray();

    static int IndexOf(byte[] hay, byte[] needle, int start = 0)
    {
        for (var i = start; i <= hay.Length - needle.Length; i++)
        {
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        }

        return -1;
    }
}
