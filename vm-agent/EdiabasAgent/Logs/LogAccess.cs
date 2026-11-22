using System.Runtime.InteropServices;
using EdiabasAgent.Core;
using Microsoft.Win32.SafeHandles;

namespace EdiabasAgent.Logs;

/// <summary>
/// The Windows side of the log endpoints (<see cref="ILogFileSystem"/>). Read-only: every
/// open is <c>OPEN_EXISTING</c> with read access only, shared read/write/delete so a tool still
/// writing its trace is not disturbed. Nothing here creates, writes, renames or deletes.
/// Every decision about whether a path may be read is made in <see cref="LogService"/> (Core).
/// </summary>
internal sealed class LogAccess : ILogFileSystem
{
    public bool IsLocalDisk(char drive)
    {
        // GetDriveType reads the drive letter's type from the system's drive table; it does not
        // open anything on the drive or contact the server behind a mapped drive.
        return char.IsAsciiLetter(drive) && LogNative.GetDriveTypeW($"{drive}:\\") == LogNative.DRIVE_FIXED;
    }

    public string? ResolveDirectory(string path)
    {
        // Attributes-only open of a folder (backup semantics is what lets CreateFile open a
        // folder at all); follows a root that is itself a link.
        using var h = LogNative.CreateFileW(path, LogNative.FILE_READ_ATTRIBUTES, LogNative.FILE_SHARE_ALL, IntPtr.Zero,
            LogNative.OPEN_EXISTING, LogNative.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid)
        {
            return null;
        }

        if (!LogNative.GetFileInformationByHandle(h, out var info) || (info.FileAttributes & LogNative.FILE_ATTRIBUTE_DIRECTORY) == 0)
        {
            return null;
        }

        return LogNative.FinalPath(h);
    }

    public IEnumerable<LogDirEntry>? ListDirectory(string path)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false,
                MatchType = MatchType.Simple,
            };

            // FindFirstFile-style enumeration: the attributes, size and time come from the
            // folder itself; no entry is opened and no link is followed.
            return Entries(new DirectoryInfo(path).EnumerateFileSystemInfos("*", options).GetEnumerator());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public LogOpenStatus OpenRead(string path, out ILogFile? file)
    {
        file = null;
        var h = LogNative.CreateFileW(path, LogNative.GENERIC_READ, LogNative.FILE_SHARE_ALL, IntPtr.Zero,
            LogNative.OPEN_EXISTING, LogNative.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (h.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            h.Dispose();
            return error switch
            {
                LogNative.ERROR_FILE_NOT_FOUND or LogNative.ERROR_PATH_NOT_FOUND or LogNative.ERROR_INVALID_NAME
                    or LogNative.ERROR_BAD_PATHNAME or LogNative.ERROR_DIRECTORY => LogOpenStatus.NotFound,

                // Opening a folder without backup semantics is refused with access denied.
                LogNative.ERROR_ACCESS_DENIED when IsDirectory(path) => LogOpenStatus.NotFound,
                _ => LogOpenStatus.Unreadable,
            };
        }

        file = new OpenedFile(h);
        return LogOpenStatus.Opened;
    }

    private static bool IsDirectory(string path)
    {
        var attributes = LogNative.GetFileAttributesW(path);
        return attributes != LogNative.INVALID_FILE_ATTRIBUTES && (attributes & LogNative.FILE_ATTRIBUTE_DIRECTORY) != 0;
    }

    private static IEnumerable<LogDirEntry> Entries(IEnumerator<FileSystemInfo> e)
    {
        using (e)
        {
            while (true)
            {
                LogDirEntry entry;
                try
                {
                    if (!e.MoveNext())
                    {
                        yield break;
                    }

                    var i = e.Current;
                    var attributes = i.Attributes;
                    entry = new LogDirEntry(
                        i.Name,
                        (attributes & FileAttributes.Directory) != 0,
                        (attributes & FileAttributes.ReparsePoint) != 0,
                        i is FileInfo f ? f.Length : 0,
                        new DateTimeOffset(i.LastWriteTimeUtc, TimeSpan.Zero));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    yield break;
                }

                yield return entry;
            }
        }
    }

    /// <summary>An open read-only handle; all facts come from the handle, not the path.</summary>
    private sealed class OpenedFile : ILogFile
    {
        private readonly SafeFileHandle _handle;

        public OpenedFile(SafeFileHandle handle)
        {
            _handle = handle;
            FinalPath = LogNative.FinalPath(handle);
            var isDisk = LogNative.GetFileType(handle) == LogNative.FILE_TYPE_DISK;
            if (LogNative.GetFileInformationByHandle(handle, out var info))
            {
                IsRegularFile = isDisk && (info.FileAttributes & LogNative.FILE_ATTRIBUTE_DIRECTORY) == 0;
                LinkCount = unchecked((int)info.NumberOfLinks);
                Length = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
                ModifiedUtc = new DateTimeOffset(DateTime.FromFileTimeUtc(((long)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow), TimeSpan.Zero);
            }
            else
            {
                // Nothing known about it: LogService refuses it as not a regular file.
                IsRegularFile = false;
                LinkCount = 0;
            }
        }

        public string? FinalPath { get; }
        public bool IsRegularFile { get; }
        public int LinkCount { get; }
        public long Length { get; }
        public DateTimeOffset ModifiedUtc { get; }

        public int Read(long offset, Span<byte> buffer) => RandomAccess.Read(_handle, buffer, offset);

        public void Dispose() => _handle.Dispose();
    }
}

/// <summary>kernel32 imports for <see cref="LogAccess"/>. Read-only calls only.</summary>
internal static class LogNative
{
    internal const uint GENERIC_READ = 0x80000000;
    internal const uint FILE_READ_ATTRIBUTES = 0x0080;
    internal const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4; // read | write | delete
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    internal const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    internal const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;
    internal const uint FILE_TYPE_DISK = 1;
    internal const uint DRIVE_FIXED = 3;

    // FILE_NAME_NORMALIZED (0: long names, 8.3 names expanded) with VOLUME_NAME_DOS (0: the
    // drive-letter form). Both are the zero value of their flag group.
    internal const uint FINAL_PATH_FLAGS = 0;

    internal const int ERROR_FILE_NOT_FOUND = 2;
    internal const int ERROR_PATH_NOT_FOUND = 3;
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_INVALID_NAME = 123;
    internal const int ERROR_BAD_PATHNAME = 161;
    internal const int ERROR_DIRECTORY = 267;

    [StructLayout(LayoutKind.Sequential)]
    internal struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint GetFileType(SafeFileHandle hFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint GetFileAttributesW(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint GetDriveTypeW(string lpRootPathName);

    /// <summary>The handle's final path (<c>\\?\C:\...</c>), or null if it cannot be had in full.</summary>
    internal static string? FinalPath(SafeFileHandle h)
    {
        var buffer = new char[1024];
        var n = GetFinalPathNameByHandleW(h, buffer, (uint)buffer.Length, FINAL_PATH_FLAGS);
        return n == 0 || n >= buffer.Length ? null : new string(buffer, 0, (int)n);
    }
}
