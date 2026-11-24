namespace EdiabasAgent.Core;

/// <summary>The SGBD (<c>.prg</c>) and group (<c>.grp</c>) files EDIABAS can load.</summary>
public interface IEcuCatalog
{
    /// <summary>Whether <c>name.prg</c> or <c>name.grp</c> exists. <paramref name="ecu"/> has
    /// already passed the policy's name shape, so it cannot carry a path.</summary>
    bool Exists(string ecu);

    /// <summary>Null when the folder cannot be read.</summary>
    EcusResponse? List(string? filter, int max);

    /// <summary>SHA-256 (lower-case hex) of <c>name.prg</c>, the SGBD that answered a describe,
    /// for <see cref="DescriptionCache"/>. Null when there is no such file, it cannot be read, or
    /// the name is not a plain SGBD name. <paramref name="name"/> may come from EDIABAS (the
    /// variant), not only from the policy-checked request.</summary>
    string? PrgHash(string name) => null;
}

/// <summary>The configured Ecu folder on disk. Lists files; opens one only to hash it for
/// <see cref="PrgHash"/>, read-only.</summary>
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

    public string? PrgHash(string name)
    {
        if (!PlainName.IsMatch(name))
        {
            return null;
        }

        try
        {
            var path = Path.Combine(EcuPath, name + ".prg");
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            // Hashing a large SGBD takes a few milliseconds; a file that has not changed since
            // (same size and write time) keeps its hash.
            var stamp = (info.Length, info.LastWriteTimeUtc);
            lock (_hashes)
            {
                if (_hashes.TryGetValue(path, out var known) && known.Stamp == stamp)
                {
                    return known.Hash;
                }
            }

            string hash;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(fs));
            }

            lock (_hashes)
            {
                _hashes[path] = (stamp, hash);
            }

            return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex PlainName = new("^[A-Za-z0-9_]{1,64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private readonly Dictionary<string, ((long Length, DateTime Written) Stamp, string Hash)> _hashes = new(StringComparer.OrdinalIgnoreCase);

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
