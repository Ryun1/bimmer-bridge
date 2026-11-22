using System.Reflection;
using EdiabasAgent.Audit;
using EdiabasAgent.Core;
using EdiabasAgent.Ediabas;
using EdiabasAgent.Logging;
using EdiabasAgent.Logs;
using EdiabasAgent.Proc;

namespace EdiabasAgent;

/// <summary>Process-wide state shared by the host, the pipeline and the handlers.</summary>
internal sealed class AgentContext : IDisposable
{
    /// <summary>The endpoint groups this build has (docs/api.md /health <c>capabilities</c>).</summary>
    public static readonly IReadOnlyList<string> Capabilities = ["ediabas", "capture", "logs"];

    public AgentContext(AgentConfig config, string configPath, TokenAuth auth, AgentLog log, AuditLog audit, bool elevated, int sessionId)
    {
        Config = config;
        Auth = auth;
        Log = log;
        Audit = audit;
        Elevated = elevated;
        SessionId = sessionId;
        Conflicts = new ConflictCheck(config.ConflictProcesses, config.EdiabasDll);
        Policy = JobPolicy.Embedded;
        Logs = new LogService(config.LogRoots, new LogAccess());
        Approvals = new ApprovalState(new ApprovalList(config.ReviewedJobs));
        Runner = new EdiabasRunner(
            () => new EdiabasService(
                new Api32(config.EdiabasDll),
                Conflicts,
                new EcuFolder(config.EcuPath),
                Policy,
                new CommentScreen(Policy.ScreenTerms, Policy.ScreenIgnore, Approvals),
                new SystemRunnerClock(),
                CableUse,
                new EdiabasLogAdapter(log),
                DllState,
                config.JobTimeoutSeconds,
                config.MaxJobTimeoutSeconds,
                config.EdiabasIdleReleaseSeconds,
                new ApprovalReloader(new ConfigFile(configPath), Approvals, new EdiabasLogAdapter(log))),
            log);
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

    /// <summary>The job policy built into the agent; refuses everything if it did not load.</summary>
    public JobPolicy Policy { get; }

    public CableUse CableUse { get; } = new();

    /// <summary>The config's <c>reviewedJobs</c> as the runner uses them, for /health.</summary>
    public ApprovalState Approvals { get; }

    /// <summary>Whether the runner has api32.dll loaded right now, for /health.</summary>
    public DllLoadState DllState { get; } = new();

    /// <summary>Read-only access to the files under the config's <c>logRoots</c>.</summary>
    public LogService Logs { get; }

    public EdiabasRunner Runner { get; }

    public void Dispose() => Runner.Dispose();

    private sealed class EdiabasLogAdapter(AgentLog log) : IEdiabasLog
    {
        public void Info(string message) => log.Info(message);

        public void Warn(string message) => log.Warn(message);
    }
}
