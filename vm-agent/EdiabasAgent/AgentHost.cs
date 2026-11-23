using System.Net;
using System.Net.NetworkInformation;
using EdiabasAgent.Core;
using EdiabasAgent.Logging;
using EdiabasAgent.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdiabasAgent;

/// <summary>
/// Resolves the bind address, runs Kestrel on exactly that one IPv4 address and port, and
/// rebinds when the network changes. If no address is available it waits and retries forever;
/// it never falls back to any other address.
/// </summary>
internal sealed class AgentHost
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PeriodicRecheck = TimeSpan.FromSeconds(30);

    private readonly AgentContext _ctx;
    private readonly SemaphoreSlim _networkChanged = new(0, int.MaxValue);

    public AgentHost(AgentContext ctx)
    {
        _ctx = ctx;
    }

    private AgentLog Log => _ctx.Log;

    public async Task RunAsync(CancellationToken stop)
    {
        // Config problems that do not stop the agent (an unknown key, an unusable log root).
        foreach (var warning in _ctx.Config.Warnings)
        {
            Log.Warn(warning);
        }

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        using var watchdog = new AgentWatchdog(_ctx);
        watchdog.Start();
        try
        {
            string? lastWaitReason = null;
            while (!stop.IsCancellationRequested)
            {
                // A probe that throws (adapters changing under it) must not end the bind loop:
                // the agent would stop listening until the VM restarts.
                BindSelection selection;
                try
                {
                    selection = AdapterProbe.Resolve(_ctx.Config);
                }
                catch (Exception ex)
                {
                    // Logged once per distinct failure, as the wait below is, so a lasting fault does not fill the log.
                    var reason = "adapter probe failed: " + ex.Message;
                    if (reason != lastWaitReason)
                    {
                        Log.Error($"could not read network adapters; retrying every {RetryDelay.TotalSeconds:0} s", ex);
                        lastWaitReason = reason;
                    }

                    await Delay(RetryDelay, stop);
                    continue;
                }

                if (!selection.Available)
                {
                    if (selection.Reason != lastWaitReason)
                    {
                        Log.Warn($"no bind address yet ({selection.Reason}); retrying every {RetryDelay.TotalSeconds:0} s. Not falling back to any other address.");
                        lastWaitReason = selection.Reason;
                    }

                    await Delay(RetryDelay, stop);
                    continue;
                }

                lastWaitReason = null;
                await RunOnce(selection, stop);
            }
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _networkChanged.Release();

    private async Task RunOnce(BindSelection selection, CancellationToken stop)
    {
        var address = selection.Address!;
        var port = _ctx.Config.Port;

        if (selection.Warning is not null)
        {
            Log.Warn(selection.Warning);
        }

        Log.Info($"binding to {address}:{port}, gateway {selection.Gateway?.ToString() ?? "(none)"}; {selection.Reason}");

        WebApplication? app = null;
        try
        {
            app = Build(selection);
            await app.StartAsync(stop);
            VerifyBoundAddresses(app, address, port);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            await StopQuietly(app);
            return;
        }
        catch (Exception ex)
        {
            Log.Error($"could not listen on {address}:{port}; retrying in {RetryDelay.TotalSeconds:0} s", ex);
            await StopQuietly(app);
            await Delay(RetryDelay, stop);
            return;
        }

        Log.Info($"listening on http://{address}:{port}/ (HTTP/1.1 only)");
        _ctx.Listening = new IPEndPoint(address, port);
        try
        {
            await WaitUntilBindingChanges(selection, stop);
        }
        finally
        {
            _ctx.Listening = null;
            Log.Info($"stopping listener on {address}:{port}");
            await StopQuietly(app);
        }
    }

    private async Task WaitUntilBindingChanges(BindSelection current, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            bool signalled;
            try
            {
                signalled = await _networkChanged.WaitAsync(PeriodicRecheck, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (signalled)
            {
                // Debounce: adapter changes arrive in bursts.
                await Delay(Debounce, stop);
                while (_networkChanged.CurrentCount > 0)
                {
                    _networkChanged.Wait(0);
                }
            }

            BindSelection now;
            try
            {
                now = AdapterProbe.Resolve(_ctx.Config);
            }
            catch (Exception ex)
            {
                Log.Error("could not re-read network adapters; keeping the current binding", ex);
                continue;
            }

            if (!now.SameBindingAs(current))
            {
                Log.Info(now.Available
                    ? $"network changed: now {now.Address} via {now.Gateway?.ToString() ?? "(none)"} (was {current.Address} via {current.Gateway?.ToString() ?? "(none)"}); rebinding"
                    : $"network changed: {current.Address} is no longer the right address ({now.Reason}); stopping listener");
                return;
            }
        }
    }

    internal WebApplication Build(BindSelection selection)
    {
        // Empty builder: no appsettings.json, no environment-variable or command-line
        // configuration, so nothing (ASPNETCORE_URLS, Kestrel:Endpoints, ...) can add a listener.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = "EdiabasAgent",
            EnvironmentName = "Production",
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddProvider(new AgentLogProvider(Log));

        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.AllowSynchronousIO = false;
            options.Limits.MaxRequestBodySize = 64 * 1024;
            options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            options.Limits.MaxRequestLineSize = 8 * 1024;
            options.Limits.MaxRequestHeaderCount = 50;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            options.Limits.MaxConcurrentConnections = 16;
            options.Limits.MaxConcurrentUpgradedConnections = 0;

            // Exactly one IPv4 address and port. Never a wildcard.
            options.Listen(selection.Address!, _ctx.Config.Port, listen => listen.Protocols = HttpProtocols.Http1);
        });

        var app = builder.Build();
        var endpoints = new Endpoints(_ctx, selection);
        var pipeline = new RequestPipeline(_ctx, selection, endpoints.Routes);
        app.Run(pipeline.Handle);
        return app;
    }

    /// <summary>Belt and braces: after start, the server must report exactly our one address.</summary>
    internal static void VerifyBoundAddresses(WebApplication app, IPAddress address, int port)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses.ToList() ?? [];
        var expected = $"http://{address}:{port}";
        if (addresses.Count != 1 || !string.Equals(addresses[0].TrimEnd('/'), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"server reports listening on [{string.Join(", ", addresses)}], expected exactly [{expected}]; refusing to run");
        }
    }

    private async Task StopQuietly(WebApplication? app)
    {
        if (app is null)
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Log.Warn($"error while stopping the listener: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            await app.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"error while disposing the listener: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task Delay(TimeSpan delay, CancellationToken stop)
    {
        try
        {
            await Task.Delay(delay, stop);
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested; callers check the token.
        }
    }
}
