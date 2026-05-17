using System.Reflection;
using MezzoRecovery.Agent.Api;
using MezzoRecovery.Agent.Configuration;
using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.Agent.Devices;
using MezzoRecovery.Agent.Identity;
using MezzoRecovery.Agent.TapeOperations;
using MezzoRecovery.TapeDrive.Abstractions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MezzoRecovery.Agent.Runtime;

public sealed class AgentConnectionLoop(
    string configPath,
    string credentialPath,
    DeviceReportPublisher reportPublisher,
    TapeDeviceStatusPoller statusPoller,
    DeviceDiscoveryOptions discoveryOptions,
    IScsiHostEnumerator scsiEnumerator,
    TapeReadRunner tapeReadRunner,
    TapeMediaControlService tapeMediaControl,
    StopOperationHandler stopHandler,
    TapeOperationStateStore operationState,
    ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task RunAsync(CancellationToken ct)
    {
        using var _ = new ProcessLock(AgentPaths.DefaultLockPath);

        var cfg = await AgentConfigLoader.LoadAsync(configPath, ct);
        var baseUri = new Uri(cfg.ApiBaseUrl.TrimEnd('/') + "/");
        var cred = await CredentialStore.TryLoadAsync(credentialPath, ct);
        if (cred is null)
        {
            _logger.LogError("No credentials at {Path}. Run 'mra enroll' first.", credentialPath);
            return;
        }

        AgentApiClient api = null!;
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
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            api = new AgentApiClient(http);
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
                    .WithAutomaticReconnect(new UnboundedRetryPolicy())
                    .Build();

                var reconnectAttempts = 0;

                hub.Reconnecting += error =>
                {
                    var attempt = Interlocked.Increment(ref reconnectAttempts);
                    _logger.LogWarning(error, "SignalR reconnecting (attempt {Attempt}).", attempt);
                    return Task.CompletedTask;
                };
                hub.Reconnected += async connectionId =>
                {
                    Interlocked.Exchange(ref reconnectAttempts, 0);
                    _logger.LogInformation("SignalR reconnected (ConnectionId={Id}). Re-registering with server.", connectionId);
                    for (var attempt = 1; attempt <= 5; attempt++)
                    {
                        try
                        {
                            await hub!.InvokeAsync("RegisterRuntime", hostname, os, arch, version, CancellationToken.None);
                            await reportPublisher.PublishFullDiscoveryAsync(hub!, CancellationToken.None);
                            await ReportActiveOperationsAsync(hub!, CancellationToken.None);
                            _logger.LogInformation("Re-registration completed after reconnect.");
                            return;
                        }
                        catch (Exception ex)
                        {
                            if (attempt == 5)
                                _logger.LogError(ex, "Re-registration after reconnect failed after {Attempts} attempts - agent may appear offline.", attempt);
                            else
                            {
                                _logger.LogWarning(ex, "Re-registration attempt {Attempt} failed, retrying in {Delay}s.", attempt, attempt * 2);
                                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), CancellationToken.None);
                            }
                        }
                    }
                };

                hub.On("RefreshTapeDevices", async () =>
                {
                    _logger.LogInformation("RefreshTapeDevices command received from server.");
                    try
                    {
                        await reportPublisher.PublishFullDiscoveryAsync(hub!, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Device refresh on demand failed.");
                    }
                });

                hub.On("RescanScsi", async () =>
                {
                    _logger.LogInformation("RescanScsi command received from server.");
                    try
                    {
                        scsiEnumerator.ScanScsiHosts();
                        _logger.LogInformation("SCSI host scan completed. Re-discovering devices.");
                        await reportPublisher.PublishFullDiscoveryAsync(hub!, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "SCSI rescan failed.");
                    }
                });

                hub.On<StartTapeReadCommand>("StartTapeRead", command =>
                {
                    _logger.LogInformation("StartTapeRead received for device {DeviceId}.", command.TapeDeviceId);
                    tapeReadRunner.Start(hub!, command);
                    return Task.CompletedTask;
                });

                hub.On<StopTapeOperationCommand>("StopTapeOperation", command =>
                {
                    _logger.LogInformation("StopTapeOperation received for device {DeviceId}.", command.TapeDeviceId);
                    stopHandler.RequestStop(command);
                    return Task.CompletedTask;
                });

                hub.On<ExecuteTapeMediaActionCommand>("ExecuteTapeMediaAction", command =>
                {
                    _logger.LogInformation(
                        "ExecuteTapeMediaAction {Action} received for device {DeviceId}.",
                        command.OperationType,
                        command.TapeDeviceId);
                    tapeMediaControl.Execute(hub!, command);
                    return Task.CompletedTask;
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
                await reportPublisher.PublishFullDiscoveryAsync(hub, ct);
                await ReportActiveOperationsAsync(hub, ct);

                using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var heartbeatTask = HeartbeatLoopAsync(hub, hostname, os, arch, version, heartbeatCts.Token);
                var deviceRefreshTask = discoveryOptions.Enabled
                    ? DeviceRefreshLoopAsync(hub, heartbeatCts.Token)
                    : Task.CompletedTask;
                var statusPollerTask = discoveryOptions.Enabled
                    ? statusPoller.RunAsync(hub, heartbeatCts.Token)
                    : Task.CompletedTask;

                await disconnectTcs.Task.WaitAsync(ct);

                await heartbeatCts.CancelAsync();
                try { await heartbeatTask; }
                catch (OperationCanceledException) { }
                try { await deviceRefreshTask; }
                catch (OperationCanceledException) { }
                try { await statusPollerTask; }
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
                try
                {
                    await connection.InvokeAsync("Heartbeat", hostname, os, arch, version, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Heartbeat send failed (connection may be reconnecting).");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    private async Task ReportActiveOperationsAsync(HubConnection hub, CancellationToken ct)
    {
        try
        {
            var snapshots = operationState.BuildSnapshots();
            await hub.InvokeAsync("ReportActiveOperations", snapshots, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to report active tape operations.");
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
                await reportPublisher.PublishFullDiscoveryAsync(connection, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    private sealed class UnboundedRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            var seconds = Math.Min(60.0, Math.Pow(2, retryContext.PreviousRetryCount));
            return TimeSpan.FromSeconds(seconds);
        }
    }
}
