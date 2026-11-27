namespace Ediabasd;

/// What a group file (.grp) resolved to at the car, keyed by group name and the .grp file's
/// SHA-256, so a describe can use the variant without reaching the car.
public interface IVariantCache
{
    string? Lookup(string group, string grpSha256);

    void Store(string group, string grpSha256, string variant);
}

/// A cache that never holds anything.
public sealed class NoVariantCache : IVariantCache
{
    public string? Lookup(string group, string grpSha256) => null;

    public void Store(string group, string grpSha256, string variant) { }
}

/// The variant cache on disk: <c>{"&lt;GROUP&gt;": {"sha256": "...", "variant": "..."}}</c>.
/// Group names are upper-cased. A changed .grp hash is a miss, and the entry is replaced at
/// the next live resolution. A file that cannot be read counts as empty. Writes are atomic
/// (temp file, then rename).
public sealed class VariantCache : IVariantCache
{
    sealed record Entry(string Sha256, string Variant);

    static readonly System.Text.Json.JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    readonly string _path;
    readonly object _lock = new();

    public VariantCache(string path) => _path = path;

    public string? Lookup(string group, string grpSha256)
    {
        lock (_lock)
        {
            return Read().TryGetValue(group.ToUpperInvariant(), out var e) &&
                   string.Equals(e.Sha256, grpSha256, StringComparison.OrdinalIgnoreCase)
                ? e.Variant
                : null;
        }
    }

    public void Store(string group, string grpSha256, string variant)
    {
        lock (_lock)
        {
            var all = Read();
            all[group.ToUpperInvariant()] = new Entry(grpSha256.ToLowerInvariant(), variant);
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(all, Json));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    Dictionary<string, Entry> Read()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            var all = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path), Json);
            return all?.Where(kv => kv.Value is { Sha256: not null, Variant: not null })
                       .ToDictionary(kv => kv.Key.ToUpperInvariant(), kv => kv.Value) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new();
        }
    }
}
