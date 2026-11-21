using System.Text;

namespace EdiabasAgent.Logging;

/// <summary>
/// Append-only text file that rotates once it reaches <see cref="MaxBytes"/>: the current file
/// becomes <c>name.1</c> (replacing any older <c>.1</c>) and a fresh file is started.
/// Thread-safe within the process. Write failures are swallowed: logging must never take the
/// agent down.
/// </summary>
public sealed class RotatingFile
{
    public const long MaxBytes = 5L * 1024 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly object _gate = new();

    public RotatingFile(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public void AppendLine(string line)
    {
        lock (_gate)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                RotateIfNeeded();
                using var fs = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
                var bytes = Utf8NoBom.GetBytes(line + "\r\n");
                fs.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nowhere better to report it; never throw from logging.
            }
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(Path);
        if (!info.Exists || info.Length < MaxBytes)
        {
            return;
        }

        var rotated = Path + ".1";
        File.Move(Path, rotated, overwrite: true);
    }
}
