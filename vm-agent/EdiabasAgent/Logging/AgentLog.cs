using System.Globalization;
using Microsoft.Extensions.Logging;

namespace EdiabasAgent.Logging;

/// <summary>
/// <c>logs\agent.log</c>: startup, version, bind decisions and errors.
/// Never pass the token or an Authorization header to this class.
/// </summary>
public sealed class AgentLog
{
    private readonly RotatingFile _file;

    public AgentLog(string logDirectory)
    {
        Directory = logDirectory;
        _file = new RotatingFile(Path.Combine(logDirectory, "agent.log"));
    }

    public string Directory { get; }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    public void Error(string message, Exception ex) => Write("ERROR", message + Environment.NewLine + ex);

    private void Write(string level, string message)
    {
        var time = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        // Keep each entry on its own lines; indent continuation lines so entries stay readable.
        var body = message.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n    ", StringComparison.Ordinal);
        _file.AppendLine($"{time} [{pid}] {level,-5} {body}");
    }
}

/// <summary>Forwards framework (Kestrel/hosting) warnings and errors to agent.log.</summary>
public sealed class AgentLogProvider : ILoggerProvider
{
    private readonly AgentLog _log;

    public AgentLogProvider(AgentLog log)
    {
        _log = log;
    }

    public ILogger CreateLogger(string categoryName) => new Logger(_log, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger : ILogger
    {
        private readonly AgentLog _log;
        private readonly string _category;

        public Logger(AgentLog log, string category)
        {
            _log = log;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var msg = $"{_category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                _log.Error(msg, exception);
            }
            else if (logLevel >= LogLevel.Error)
            {
                _log.Error(msg);
            }
            else
            {
                _log.Warn(msg);
            }
        }
    }
}
