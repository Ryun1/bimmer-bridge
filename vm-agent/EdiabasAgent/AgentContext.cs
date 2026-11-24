using System.Net;
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
        ApprovalReloader = new ApprovalReloader(new ConfigFile(configPath), Approvals, new EdiabasLogAdapter(log));
        IniPath = EdiabasIni.BesideDll(config.EdiabasDll);
        Runner = new EdiabasRunner(
            () => new EdiabasService(
                new Api32(config.EdiabasDll, NativeCalls, config.EdiabasInterface),
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
                ApprovalReloader,
                telegrams: config.TelegramCheck == TelegramCheckModes.Off
                    ? null
                    : new TelegramCheck(config.TelegramCheck, IniPath, new TraceFiles(), Telegrams, TelegramLatch, new EdiabasLogAdapter(log), new SystemRunnerClock()),
                ediabasInterface: config.EdiabasInterface,
                requireGate: config.RequireGate),
            log,
            HandlerWait(config.MaxJobTimeoutSeconds));
    }

    /// <summary>How long a handler waits for the EDIABAS runner: the longest a request can take
    /// (its budget and the apiBreak grace) and a margin. Only a stuck runner reaches it.</summary>
    internal static TimeSpan HandlerWait(int maxJobTimeoutSeconds) =>
        TimeSpan.FromMilliseconds((Math.Max(1, maxJobTimeoutSeconds) * 1000L) + EdiabasService.BreakGraceMs + 20_000);

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

    /// <summary>The telegram rule built into the agent (policy/telegrams.json).</summary>
    public TelegramRule Telegrams { get; } = TelegramRule.Embedded;

    /// <summary>The telegram check's latch, for /health; it outlives a restart of the runner thread.</summary>
    public TelegramLatch TelegramLatch { get; } = new();

    /// <summary>EDIABAS.INI beside api32.dll, where the telegram check reads IfhTrace and TracePath.</summary>
    public string IniPath { get; }

    /// <summary>The config's <c>reviewedJobs</c> as the runner uses them, for /health.</summary>
    public ApprovalState Approvals { get; }

    /// <summary>Re-reads <c>reviewedJobs</c> from config.json; the runner calls it before every
    /// EDIABAS request and /health before it answers, so both see the file as it is now.</summary>
    public ApprovalReloader ApprovalReloader { get; }

    /// <summary>Whether the runner has api32.dll loaded right now, for /health.</summary>
    public DllLoadState DllState { get; } = new();

    /// <summary>Read-only access to the files under the config's <c>logRoots</c>.</summary>
    public LogService Logs { get; }

    public EdiabasRunner Runner { get; }

    /// <summary>The api32.dll call the runner is in or last made, for the watchdog's log line.</summary>
    public NativeCallTracker NativeCalls { get; } = new();

    /// <summary>The address and port the agent listens on now; null while it binds or rebinds.
    /// The watchdog probes it.</summary>
    public IPEndPoint? Listening
    {
        get => Volatile.Read(ref _listening);
        set => Volatile.Write(ref _listening, value);
    }

    private IPEndPoint? _listening;

    public void Dispose() => Runner.Dispose();

    private sealed class EdiabasLogAdapter(AgentLog log) : IEdiabasLog
    {
        public void Info(string message) => log.Info(message);

        public void Warn(string message) => log.Warn(message);
    }
}
