namespace EdiabasAgent.Core;

/// <summary>The SGBD (<c>.prg</c>) and group (<c>.grp</c>) files EDIABAS can load.</summary>
public interface IEcuCatalog
{
    /// <summary>Whether <c>name.prg</c> or <c>name.grp</c> exists. <paramref name="ecu"/> has
    /// already passed the policy's name shape, so it cannot carry a path.</summary>
    bool Exists(string ecu);

    /// <summary>Null when the folder cannot be read.</summary>
    EcusResponse? List(string? filter, int max);
}

/// <summary>The configured Ecu folder on disk. File listing only; never opens a file.</summary>
public sealed class EcuFolder : IEcuCatalog
{
    public const int MaxEntries = 5000;

    public EcuFolder(string ecuPath)
    {
        EcuPath = ecuPath;
    }

    public string EcuPath { get; }

    public bool Exists(string ecu)
    {
        try
        {
            return Find(ecu).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public EcusResponse? List(string? filter, int max = MaxEntries)
    {
        List<EcuFile> all;
        try
        {
            all = Directory.EnumerateFiles(EcuPath)
                .Select(ToEcuFile)
                .OfType<EcuFile>()
                .Where(f => string.IsNullOrEmpty(filter) || f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Type, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        var files = all.Take(max).ToList();
        return new EcusResponse(EcuPath, files.Count, all.Count > files.Count, files);
    }

    private IEnumerable<EcuFile> Find(string ecu) =>
        Directory.EnumerateFiles(EcuPath, ecu + ".*")
            .Select(ToEcuFile)
            .OfType<EcuFile>()
            .Where(f => string.Equals(f.Name, ecu, StringComparison.OrdinalIgnoreCase));

    private static EcuFile? ToEcuFile(string path)
    {
        var ext = Path.GetExtension(path);
        var type = ext.Equals(".prg", StringComparison.OrdinalIgnoreCase) ? "prg"
            : ext.Equals(".grp", StringComparison.OrdinalIgnoreCase) ? "grp"
            : null;
        return type is null ? null : new EcuFile(Path.GetFileNameWithoutExtension(path), type);
    }
}
