namespace EdiabasAgent.Core;

// Response records for /logs and /logs/read (docs/api.md "Logs"), and the file-system seam the
// Windows host fills in. Property order here is the JSON property order.

public sealed record LogRoot(int Index, string Path, bool Exists);

public sealed record LogFileEntry(string Path, int Root, long SizeBytes, string ModifiedAt);

public sealed record LogListResponse(IReadOnlyList<LogRoot> Roots, IReadOnlyList<LogFileEntry> Files, bool Truncated);

public sealed record GrepInfo(string Pattern, int MatchedLines);

public sealed record LogReadResponse(
    string Path,
    long SizeBytes,
    string ModifiedAt,
    long Offset,
    long Length,
    string Encoding,
    bool Truncated,
    GrepInfo? Grep,
    string Text);

/// <summary>One entry of a folder listing, as the folder itself reports it (no link followed).</summary>
public sealed record LogDirEntry(string Name, bool IsDirectory, bool IsReparsePoint, long SizeBytes, DateTimeOffset ModifiedUtc);

public enum LogOpenStatus
{
    Opened,

    /// <summary>No such file (or the path names a folder).</summary>
    NotFound,

    /// <summary>It exists but could not be opened for reading (locked, access denied, I/O error).</summary>
    Unreadable,
}

/// <summary>A file opened read-only. Everything about it comes from the open handle.</summary>
public interface ILogFile : IDisposable
{
    /// <summary>GetFinalPathNameByHandle of the open handle: the real file after every link.</summary>
    string? FinalPath { get; }

    /// <summary>A file on a local disk (GetFileType is FILE_TYPE_DISK), not a folder.</summary>
    bool IsRegularFile { get; }

    /// <summary>Number of hard links to the file.</summary>
    int LinkCount { get; }

    long Length { get; }

    DateTimeOffset ModifiedUtc { get; }

    /// <summary>Reads at <paramref name="offset"/>; returns the bytes read (fewer at end of file).</summary>
    int Read(long offset, Span<byte> buffer);
}

/// <summary>The Windows file system, reduced to what the log endpoints need. Read-only by design.</summary>
public interface ILogFileSystem
{
    /// <summary>The final path of an existing folder after every link, else null.</summary>
    string? ResolveDirectory(string path);

    /// <summary>
    /// The entries of one folder, without following any link; null when it cannot be read.
    /// Enumerated lazily so a caller can stop early.
    /// </summary>
    IEnumerable<LogDirEntry>? ListDirectory(string path);

    /// <summary>
    /// Opens an existing file for reading only, following links, shared so a writer is not
    /// disturbed. Never creates or truncates anything.
    /// </summary>
    LogOpenStatus OpenRead(string path, out ILogFile? file);
}
