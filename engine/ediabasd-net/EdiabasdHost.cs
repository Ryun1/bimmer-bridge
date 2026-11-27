using EdiabasAgent.Core;
using EdiabasLib.Native;

namespace Ediabasd;

/// Builds EdiabasAgent.Core's EdiabasService over the native API, as the VM agent's
/// AgentContext does over api32.dll. No process conflict check: the cable lock does that job.
public sealed class EdiabasdHost : IDisposable
{
    public EdiabasdHost(NativeConfig config, Func<ILiveCable>? liveCable = null)
    {
        Config = config;
        Policy = JobPolicy.Embedded;
        Approvals = new ApprovalState(new ApprovalList(config.ReviewedJobs));
        Api = new NativeEdiabasApi(
            config.EcuPath,
            liveCable ?? (() => LiveCable.Device(ResolvePort() ?? "", config.RecordDir, config.LatencyMs)),
            new VariantCache(config.VariantsPath),
            config.TraceDir);
        Service = new EdiabasService(
            Api,
            new NoConflicts(),
            new EcuFolder(config.EcuPath),
            Policy,
            new CommentScreen(Policy.ScreenTerms, Policy.ScreenIgnore, Approvals),
            new SystemRunnerClock(),
            CableUse,
            Log,
            Dll,
            config.JobTimeoutSeconds,
            config.MaxJobTimeoutSeconds,
            AgentConfig.MinIdleReleaseSeconds);
    }

    public NativeConfig Config { get; }

    public JobPolicy Policy { get; }

    public ApprovalState Approvals { get; }

    public NativeEdiabasApi Api { get; }

    public EdiabasService Service { get; }

    public CableUse CableUse { get; } = new();

    public DllLoadState Dll { get; } = new();

    public StderrLog Log { get; } = new();

    /// A job did not stop after Break and its grace time: the process must exit.
    public bool Stuck => Dll.Stuck;

    /// The configured port, else the single FTDI cable present, else null (then opening the
    /// port fails with no_device; the reason is logged).
    public string? ResolvePort()
    {
        if (!string.IsNullOrEmpty(Config.Port))
        {
            return Config.Port;
        }

        var found = KlineLibrary.FindCable(out var count);
        if (found == null)
        {
            Log.Warn(count == 0
                ? "no FTDI K+DCAN cable found"
                : $"{count} FTDI cables found; set \"port\" in config.json to choose one");
        }

        return found;
    }

    public void Dispose() => Api.Dispose();
}

public sealed class NoConflicts : IConflictCheck
{
    public IReadOnlyList<ConflictingProcess> Running() => [];
}

/// The service's log, on stderr (stdout carries the protocol).
public sealed class StderrLog : IEdiabasLog
{
    public void Info(string message) => Console.Error.WriteLine("ediabasd: " + message);

    public void Warn(string message) => Console.Error.WriteLine("ediabasd: warning: " + message);
}
