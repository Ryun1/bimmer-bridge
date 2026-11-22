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
