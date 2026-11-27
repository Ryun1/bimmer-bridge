using EdiabasAgent.Core;

namespace Ediabasd;

/// One AuditLine per request, appended to the state directory's audit.log. The remote address
/// is "stdio" (or "cli"); the method is the stdio method name. A write failure is reported on
/// stderr and never fails the request.
public sealed class AuditLog(string path, string origin = "stdio")
{
    readonly object _lock = new();

    public void Write(DateTimeOffset time, string method, int status, string? errorCode, long durationMs, string? ecu, string? job, string? rule)
    {
        var line = AuditLine.Format(time, origin, method, method, null, status, errorCode, durationMs, ecu, job, rule);
        try
        {
            lock (_lock)
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(path, line + "\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"ediabasd: cannot write the audit log {path}: {ex.Message}");
        }
    }
}
