using System.Reflection;
using MezzoRecovery.Agent.Api;
using MezzoRecovery.Agent.Configuration;
using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.Agent.Devices;
using MezzoRecovery.Agent.Identity;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MezzoRecovery.Agent.Runtime;

public sealed class AgentConnectionLoop(
    string configPath,
    string credentialPath,
    TapeDeviceDiscoveryService deviceDiscovery,
    DeviceDiscoveryOptions discoveryOptions,
    ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task RunAsync(CancellationToken ct)
    {
        using var _ = new ProcessLock(AgentPaths.DefaultLockPath);
        using var http = new HttpClient();

        var cfg = await AgentConfigLoader.LoadAsync(configPath, ct);
        var baseUri = new Uri(cfg.ApiBaseUrl.TrimEnd('/') + "/");
        var cred = await CredentialStore.TryLoadAsync(credentialPath, ct);
        if (cred is null)
        {
            _logger.LogError("No credentials at {Path}. Run 'mra enroll' first.", credentialPath);
            return;
        }

        var api = new AgentApiClient(http);
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                     ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                     ?? "0.0.0";
        var hostname = Environment.MachineName;
        var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
        var hubUri = new Uri(baseUri, "api/hubs/agent");

        var failureBackoff = TimeSpan.FromSeconds(2);
        const int maxFailureBackoffSeconds = 120;

        while (!ct.IsCancellationRequested)
        {
            HubConnection? hub = null;
            try
            {
                hub = new HubConnectionBuilder()
                    .WithUrl(hubUri, opts =>
                    {
                        opts.AccessTokenProvider = async () =>
                        {
                            var tokenResp = await api.GetTokenAsync(
                                baseUri,
                                new TokenApiRequest(cred.AgentId, cred.ClientSecret),
                                CancellationToken.None);
                            return tokenResp?.AccessToken;
                        };
                    })
                    .AddJsonProtocol(opts =>
                    {
                        opts.PayloadSerializerOptions.TypeInfoResolver = AgentJsonContext.Default;
                    })
                    .WithAutomaticReconnect(
                        Enumerable.Range(0, 8)
                            .Select(n => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, n))))
                            .ToArray())
                    .Build();

                hub.Reconnecting += error =>
                {
                    _logger.LogWarning(error, "SignalR reconnecting...");
                    return Task.CompletedTask;
                };
                hub.Reconnected += async connectionId =>
                {
                    _logger.LogInformation("SignalR reconnected. ConnectionId={Id}", connectionId);
                    try
                    {
                        await hub!.InvokeAsync("RegisterRuntime", hostname, os, arch, version, CancellationToken.None);
                        await ReportDevicesAsync(hub!, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "RegisterRuntime after reconnect failed.");
                    }
                };

                hub.On("RefreshTapeDevices", async () =>
                {
                    _logger.LogInformation("RefreshTapeDevices command received from server.");
                    try
                    {
                        await ReportDevicesAsync(hub!, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Device refresh on demand failed.");
                    }
                });

                var disconnectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                hub.Closed += _ =>
                {
                    disconnectTcs.TrySetResult();
                    return Task.CompletedTask;
                };

                await hub.StartAsync(ct);
                failureBackoff = TimeSpan.FromSeconds(2);
                _logger.LogInformation("Connected to MezzoRecovery (agent {AgentId}).", cred.AgentId);
                await hub.InvokeAsync("RegisterRuntime", hostname, os, arch, version, ct);
                await ReportDevicesAsync(hub, ct);

                using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var heartbeatTask = HeartbeatLoopAsync(hub, hostname, os, arch, version, heartbeatCts.Token);
                var deviceRefreshTask = discoveryOptions.Enabled
                    ? DeviceRefreshLoopAsync(hub, heartbeatCts.Token)
                    : Task.CompletedTask;

                await disconnectTcs.Task.WaitAsync(ct);

                await heartbeatCts.CancelAsync();
                try { await heartbeatTask; }
                catch (OperationCanceledException) { }
                try { await deviceRefreshTask; }
                catch (OperationCanceledException) { }

                await hub.StopAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent run loop error; backing off {Delay}.", failureBackoff);
                try
                {
                    await Task.Delay(failureBackoff, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                failureBackoff = TimeSpan.FromSeconds(Math.Min(maxFailureBackoffSeconds, failureBackoff.TotalSeconds * 2));
            }
            finally
            {
                if (hub is not null)
                    await hub.DisposeAsync();
            }
        }
    }

    private async Task ReportDevicesAsync(HubConnection hub, CancellationToken ct)
    {
        if (!discoveryOptions.Enabled)
            return;

        try
        {
            var devices = deviceDiscovery.DiscoverDevices();
            await hub.InvokeAsync("ReportTapeDevices", devices.ToArray(), ct);
            _logger.LogInformation("Reported {Count} tape device(s) to server.", devices.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to report tape devices to server.");
            try
            {
                await hub.InvokeAsync("ReportTapeDeviceDiscoveryFailed", ex.Message, ct);
            }
            catch
            {
                // ignore secondary failure
            }
        }
    }

    private async Task HeartbeatLoopAsync(
        HubConnection connection,
        string hostname,
        string os,
        string arch,
        string version,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
                await connection.InvokeAsync("Heartbeat", hostname, os, arch, version, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    private async Task DeviceRefreshLoopAsync(HubConnection connection, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(10, discoveryOptions.RefreshIntervalSeconds));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct);
                await ReportDevicesAsync(connection, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }
}
