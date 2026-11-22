using EdiabasAgent.Audit;
using EdiabasAgent.Core;
using EdiabasAgent.Logging;
using EdiabasAgent.Windows;

namespace EdiabasAgent;

/// <summary>
/// Entry point.
/// <list type="bullet">
/// <item>no arguments: run the agent;</item>
/// <item><c>--version</c>: print the version;</item>
/// <item><c>--selftest</c>: run the checks, print PASS/FAIL, exit 0 or 1;</item>
/// <item><c>--config &lt;path&gt;</c>: use another config file.</item>
/// </list>
/// </summary>
internal static class Program
{
    private const string MutexName = @"Global\ediabas-bridge-agent";

    // Exit codes. A bad or missing config or token never exits (the agent waits and retries);
    // 3 and 4 are no longer used.
    private const int ExitOk = 0;
    private const int ExitSelfTestFailed = 1;
    private const int ExitUsage = 2;
    private const int ExitCrashed = 5;

    private static readonly TimeSpan StartupRetry = TimeSpan.FromSeconds(30);

    private static int Main(string[] args)
    {
        var showVersion = false;
        var selfTest = false;
        var configPath = AgentConfig.DefaultConfigPath;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--version":
                    showVersion = true;
                    break;
                case "--selftest":
                    selfTest = true;
                    break;
                case "--config" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                default:
                    ProcessInfo.AttachParentConsole();
                    Console.Error.WriteLine("usage: EdiabasAgent [--version] [--selftest] [--config <path>]");
                    new AgentLog(AgentConfig.DefaultLogDirectory).Error($"unknown or incomplete argument #{i + 1}; exiting");
                    return ExitUsage;
            }
        }

        if (showVersion)
        {
            ProcessInfo.AttachParentConsole();
            Console.WriteLine($"EdiabasAgent {AgentContext.Version}");
            return ExitOk;
        }

        try
        {
            configPath = Path.GetFullPath(configPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Keep the raw value; loading will report the problem.
        }

        var loaded = AgentConfig.Load(configPath);
        var log = new AgentLog(loaded.Config?.LogDirectory ?? AgentConfig.DefaultLogDirectory);

        if (selfTest)
        {
            ProcessInfo.AttachParentConsole();
            return SelfTest.Run(configPath, loaded, log) ? ExitOk : ExitSelfTestFailed;
        }

        try
        {
            return Run(configPath, loaded, log);
        }
        catch (Exception ex)
        {
            log.Error("agent crashed", ex);
            return ExitCrashed;
        }
    }

    private static int Run(string configPath, ConfigLoadResult loaded, AgentLog log)
    {
        var elevated = ProcessInfo.IsElevated();
        var sessionId = ProcessInfo.SessionId();
        log.Info($"EdiabasAgent {AgentContext.Version} starting: pid {Environment.ProcessId}, user {Environment.UserName}, " +
                 $"session {sessionId}, elevated {elevated}, config {configPath}");

        // Single instance across sessions. A second copy logs and exits 0. The mutex is taken
        // before config and token are checked, and held while waiting for them.
        Mutex? mutex = null;
        try
        {
            bool createdNew;
            try
            {
                mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
            }
            catch (UnauthorizedAccessException)
            {
                // Exists, created by a copy running under another account or elevation.
                createdNew = false;
            }

            if (!createdNew)
            {
                log.Info("another instance is already running; exiting");
                return ExitOk;
            }

            using var stop = new CancellationTokenSource();
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    stop.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            };

            // Task Scheduler does not restart a task whose process exits non-zero, so a bad or
            // missing config or token is never a reason to exit: log it (once per distinct
            // reason), wait, re-read both files and try again until stopped.
            var suppressor = new RepeatSuppressor();
            StartupState state;
            while (true)
            {
                state = Evaluate(loaded);
                if (state.Ready)
                {
                    break;
                }

                if (suppressor.ShouldReport(state.Problem))
                {
                    log.Error($"{state.Problem}; not starting. Re-reading config and token every {StartupRetry.TotalSeconds:0} s.");
                }

                if (stop.Token.WaitHandle.WaitOne(StartupRetry))
                {
                    log.Info("agent stopped while waiting for a valid config and token");
                    return ExitOk;
                }

                loaded = AgentConfig.Load(configPath);
            }

            var config = state.Config!;
            if (!string.Equals(config.LogDirectory, log.Directory, StringComparison.OrdinalIgnoreCase))
            {
                log = new AgentLog(config.LogDirectory);
                log.Info($"EdiabasAgent {AgentContext.Version} logging to {config.LogDirectory}");
            }

            if (suppressor.ShouldReport("ready"))
            {
                log.Info("config and token are valid");
            }

            using var ctx = new AgentContext(config, configPath, new TokenAuth(state.Token), log, new AuditLog(config.LogDirectory), elevated, sessionId);
            state = state with { Token = null };

            if (ctx.Policy.Loaded)
            {
                log.Info($"job policy version {ctx.Policy.Version} loaded");
            }
            else
            {
                log.Error($"job policy did not load ({ctx.Policy.LoadError}); every EDIABAS job will be refused");
            }

            new AgentHost(ctx).RunAsync(stop.Token).GetAwaiter().GetResult();
            log.Info("agent stopped");
            return ExitOk;
        }
        finally
        {
            if (mutex is not null)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Not owned (second instance path).
                }

                mutex.Dispose();
            }
        }
    }

    /// <summary>Reads the token file (only when the config is valid) and asks Core whether we can start.</summary>
    private static StartupState Evaluate(ConfigLoadResult loaded)
    {
        if (!loaded.Ok)
        {
            return StartupCheck.Evaluate(loaded, null, null);
        }

        try
        {
            return StartupCheck.Evaluate(loaded, File.ReadAllText(loaded.Config!.TokenFile), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return StartupCheck.Evaluate(loaded, null, ex.GetType().Name);
        }
    }
}
