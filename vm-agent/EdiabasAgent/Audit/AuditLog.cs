using EdiabasAgent.Core;
using EdiabasAgent.Logging;

namespace EdiabasAgent.Audit;

/// <summary><c>logs\audit.log</c>: one line per request, formatted by <see cref="AuditLine"/>.</summary>
public sealed class AuditLog
{
    private readonly RotatingFile _file;

    public AuditLog(string logDirectory)
    {
        _file = new RotatingFile(Path.Combine(logDirectory, "audit.log"));
    }

    public void Write(
        DateTimeOffset time,
        string? remoteAddress,
        string? method,
        string? path,
        string? queryString,
        int status,
        string? errorCode,
        long durationMs)
    {
        _file.AppendLine(AuditLine.Format(time, remoteAddress, method, path, queryString, status, errorCode, durationMs));
    }
}
