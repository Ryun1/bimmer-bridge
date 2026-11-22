using EdiabasLib;
using EdiabasLib.Native;

namespace Census;

/// One row per SGBD. Status is "ok" (INITIALISIERUNG set communication parameters),
/// "no_xsetpar" (it ran, or stopped, without setting any) or "error" (the file could not be
/// resolved or parsed).
public sealed record CensusRow(string File, string Status, uint? Concept, uint? Baud, uint[] Params, string[] Calls, string Error);

public static class ConceptCensus
{
    /// Runs every .prg's INITIALISIERUNG (any case of extension) against a CaptureInterface,
    /// in file-name order, one EdiabasNet at a time. Never opens a port.
    public static IReadOnlyList<CensusRow> Run(string ecuPath)
    {
        var rows = new List<CensusRow>();
        foreach (var path in SgbdFiles.Find(ecuPath, ".prg"))
            rows.Add(RunOne(ecuPath, Path.GetFileName(path)));
        return rows;
    }

    static CensusRow RunOne(string ecuPath, string file)
    {
        var cap = new CaptureInterface();
        string error = null;
        bool parsed = false;
        try
        {
            using var ed = EdiabasFactory.Create(ecuPath, cap, noInitForVJobs: true);
            try
            {
                ed.ResolveSgbdFile(Path.GetFileNameWithoutExtension(file));
                parsed = true;
                ed.ExecuteJob("INITIALISIERUNG");
            }
            catch (Exception ex)
            {
                error = Messages(ex);
                // An exception from the interpreter itself, before the SGBD reached the
                // interface or raised an EDIABAS error, means the file did not parse.
                if (parsed && !IsJobLevel(ex) && cap.Calls.Count == 0 && cap.ParameterSets == 0)
                    parsed = false;
            }
        }
        catch (Exception ex)
        {
            error = Messages(ex);
            parsed = false;
        }

        var p = cap.LastCommParameter;
        string status = !parsed ? "error" : cap.ParameterSets > 0 ? "ok" : "no_xsetpar";
        return new CensusRow(
            file,
            status,
            p is { Length: > 0 } ? p[0] : null,
            p is { Length: > 1 } ? p[1] : null,
            p ?? Array.Empty<uint>(),
            cap.Calls.ToArray(),
            error);
    }

    static bool IsJobLevel(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is CaptureStopException || e is EdiabasNet.EdiabasNetException)
                return true;
        return false;
    }

    static string Messages(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null; e = e.InnerException)
            parts.Add(e.GetType().Name + ": " + e.Message);
        return string.Join(" <- ", parts);
    }
}

static class SgbdFiles
{
    /// Regular files in dir whose extension matches one of exts, case-insensitively, sorted
    /// by name (ordinal, case-insensitive) so the order does not depend on the file system.
    public static IReadOnlyList<string> Find(string dir, params string[] exts) =>
        Directory.EnumerateFiles(dir)
            .Where(f => exts.Any(x => Path.GetExtension(f).Equals(x, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();
}
