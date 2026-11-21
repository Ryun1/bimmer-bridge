using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

/// <summary>A file opened by <see cref="FakeLogFileSystem"/>.</summary>
public sealed class FakeLogFile : ILogFile
{
    public FakeLogFile(string finalPath, byte[] bytes)
    {
        FinalPath = finalPath;
        Bytes = bytes;
    }

    public byte[] Bytes { get; set; }
    public string? FinalPath { get; set; }
    public bool IsRegularFile { get; set; } = true;
    public int LinkCount { get; set; } = 1;
    public long Length => Bytes.Length;
    public DateTimeOffset ModifiedUtc { get; set; } = new(2026, 11, 21, 16, 40, 2, TimeSpan.Zero);
    public int Reads { get; private set; }
    public bool Disposed { get; private set; }

    public int Read(long offset, Span<byte> buffer)
    {
        Reads++;
        if (offset >= Bytes.Length)
        {
            return 0;
        }

        var n = (int)Math.Min(buffer.Length, Bytes.Length - offset);
        Bytes.AsSpan((int)offset, n).CopyTo(buffer);
        return n;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// The Windows file system as data: folders and what they resolve to, folder listings, and
/// files with the final path their handle reports. Records every open.
/// </summary>
public sealed class FakeLogFileSystem : ILogFileSystem
{
    /// <summary>Folder path → its final path (itself unless it is a link).</summary>
    public Dictionary<string, string> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<LogDirEntry>> Listings { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, FakeLogFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, LogOpenStatus> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Opened { get; } = [];

    public List<string> Listed { get; } = [];

    /// <summary>Folders passed to <see cref="ResolveDirectory"/>.</summary>
    public List<string> Resolved { get; } = [];

    /// <summary>Drive letters that are not a local fixed disk (network, removable, optical).</summary>
    public HashSet<char> NonLocalDrives { get; } = [];

    /// <summary>Every drive asked about in <see cref="IsLocalDisk"/>.</summary>
    public List<char> DriveQueries { get; } = [];

    public FakeLogFileSystem Dir(string path, string? final = null)
    {
        Dirs[path] = final ?? path;
        return this;
    }

    public FakeLogFile File(string path, string text, string? final = null)
    {
        var f = new FakeLogFile(final ?? path, System.Text.Encoding.UTF8.GetBytes(text));
        Files[path] = f;
        return f;
    }

    public string? ResolveDirectory(string path)
    {
        Resolved.Add(path);
        return Dirs.TryGetValue(path, out var final) ? @"\\?\" + final : null;
    }

    public bool IsLocalDisk(char drive)
    {
        DriveQueries.Add(char.ToUpperInvariant(drive));
        return !NonLocalDrives.Contains(char.ToUpperInvariant(drive));
    }

    public IEnumerable<LogDirEntry>? ListDirectory(string path)
    {
        Listed.Add(path);
        return Listings.TryGetValue(path, out var entries) ? entries : null;
    }

    public LogOpenStatus OpenRead(string path, out ILogFile? file)
    {
        Opened.Add(path);
        file = null;
        if (Failures.TryGetValue(path, out var failure))
        {
            return failure;
        }

        if (Files.TryGetValue(path, out var f))
        {
            file = f;
            return LogOpenStatus.Opened;
        }

        return LogOpenStatus.NotFound;
    }
}
