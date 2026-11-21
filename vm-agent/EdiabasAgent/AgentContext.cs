using System.Reflection;
using EdiabasAgent.Audit;
using EdiabasAgent.Core;
using EdiabasAgent.Logging;
using EdiabasAgent.Proc;

namespace EdiabasAgent;

/// <summary>Process-wide state shared by the host, the pipeline and the handlers.</summary>
internal sealed class AgentContext
{
    public AgentContext(AgentConfig config, TokenAuth auth, AgentLog log, AuditLog audit, bool elevated, int sessionId)
    {
        Config = config;
        Auth = auth;
        Log = log;
        Audit = audit;
        Elevated = elevated;
        SessionId = sessionId;
        Conflicts = new ConflictCheck(config.ConflictProcesses);
    }

    public static string Version { get; } =
        typeof(AgentContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AgentContext).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public AgentConfig Config { get; }
    public TokenAuth Auth { get; }
    public AgentLog Log { get; }
    public AuditLog Audit { get; }
    public ConflictCheck Conflicts { get; }
    public bool Elevated { get; }
    public int SessionId { get; }
}
