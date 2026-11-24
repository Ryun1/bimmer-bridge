using EdiabasAgent.Core;

namespace EdiabasAgent.Ediabas;

/// <summary>
/// The telegram check's reads of EDIABAS.INI and IFH.trc (<see cref="ITraceFiles"/>). Read-only:
/// each read opens the file existing-only, read access, shared read/write/delete, because
/// EDIABAS keeps IFH.trc open while it writes. The length is asked of the open handle, not the
/// folder entry, which Windows updates late for a file another process is writing.
/// </summary>
internal sealed class TraceFiles : ITraceFiles
{
    public long? Length(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var fs = Open(path);
            return fs.Length;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public byte[] Read(string path, long offset, int count)
    {
        using var fs = Open(path);
        if (offset >= fs.Length || count <= 0)
        {
            return [];
        }

        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[(int)Math.Min(count, fs.Length - offset)];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = fs.Read(buffer, read, buffer.Length - read);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return read == buffer.Length ? buffer : buffer[..read];
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
}
