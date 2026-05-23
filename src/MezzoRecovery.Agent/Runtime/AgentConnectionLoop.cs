using System.Reflection;
using MezzoRecovery.Agent.Api;
using MezzoRecovery.Agent.Configuration;
using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.Agent.Devices;
using MezzoRecovery.Agent.Identity;
using MezzoRecovery.Agent.TapeOperations;
using MezzoRecovery.TapeDrive.Abstractions;
using MezzoRecovery.TapeDrive.Linux;
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
    IScsiTapeDeviceManager scsiTapeDeviceManager,
    TapeReadRunner tapeReadRunner,
    TapeMediaControlService tapeMediaControl,
    StopOperationHandler stopHandler,
    AgentDeviceStateStore deviceStore,
    TapeMediaIdentificationReporter identificationReporter,
    ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private volatile string? _tapeCacheDirectory = AgentPaths.DefaultCacheDirectory;

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
                            await ReportCacheStatusAsync(hub!, CancellationToken.None);
                            await reportPublisher.PublishFullDiscoveryAsync(hub!, CancellationToken.None);
                            await reportPublisher.PublishActiveOperationsAsync(hub!, CancellationToken.None);
                            // Retry any preflight-identification reports that were lost when
                            // the connection dropped. Runs after device discovery so the server
                            // already knows the devices before receiving their tape results.
                            await identificationReporter.RetryPendingAsync(hub!, CancellationToken.None);
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

                hub.On<AgentConfigCommand>("UpdateAgentConfig", async cmd =>
                {
                    _tapeCacheDirectory = string.IsNullOrEmpty(cmd.TapeCacheDirectory)
                        ? AgentPaths.DefaultCacheDirectory
                        : cmd.TapeCacheDirectory;
                    await ReportCacheStatusAsync(hub, CancellationToken.None);
                });

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

                hub.On<RefreshTapeDeviceCommand>("RefreshTapeDevice", async command =>
                {
                    _logger.LogInformation(
                        "RefreshTapeDevice command received for device {DeviceId}.",
                        command.TapeDeviceId);
                    try
                    {
                        await reportPublisher.PublishDeviceStateRefreshAsync(
                            hub!,
                            command.StableDeviceKey,
                            CancellationToken.None,
                            forcePreflight: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "Device state refresh failed for {DeviceId}.",
                            command.TapeDeviceId);
                    }
                });

                hub.On("RescanScsi", async () =>
                {
                    _logger.LogInformation("RescanScsi command received from server.");
                    try
                    {
                        RemoveStaleScsiTapeDevices();
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

                hub.On<UpdateTapeDeviceReadSettingsCommand>("UpdateTapeDeviceReadSettings", command =>
                {
                    var changed = deviceStore.UpdateReadSettings(
                        command.StableDeviceKey,
                        command.AutoDetect,
                        command.ReadBlockSizeBytes,
                        command.ReadBufferSizeBytes);
                    if (changed)
                        _logger.LogInformation(
                            "Read settings updated for device {Key}: autoDetect={Auto} block={Block} buffer={Buffer}.",
                            command.StableDeviceKey,
                            command.AutoDetect,
                            command.ReadBlockSizeBytes,
                            command.ReadBufferSizeBytes);
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
                await ReportCacheStatusAsync(hub, ct);
                await reportPublisher.PublishFullDiscoveryAsync(hub, ct);
                await reportPublisher.PublishActiveOperationsAsync(hub, ct);

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

    private void RemoveStaleScsiTapeDevices()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var devices = scsiTapeDeviceManager.GetScsiTapeDevices();
        if (devices.Count == 0)
            return;

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var device in devices)
        {
            if (!seen.Add(device.ScsiAddress))
                continue;

            var devPath = "/dev/" + device.DeviceName;
            var probe = LinuxTapeDriveStatus.Probe(devPath);
            if (probe.Ok || probe.Errno != 5)
                continue;

            _logger.LogInformation(
                "RescanScsi: removing stale tape device {DeviceName} at {ScsiAddress} (EIO).",
                device.DeviceName, device.ScsiAddress);

            if (!scsiTapeDeviceManager.TryDeleteScsiDevice(device.ScsiAddress))
                _logger.LogWarning(
                    "RescanScsi: failed to remove {ScsiAddress} - root access may be required.",
                    device.ScsiAddress);
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
                    await ReportCacheStatusAsync(connection, ct);
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

    private async Task ReportCacheStatusAsync(HubConnection hub, CancellationToken ct)
    {
        try
        {
            var (freeBytes, error) = ProbeDirectory(_tapeCacheDirectory);
            await hub.InvokeAsync("ReportCacheStatus", freeBytes, error, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cache status report failed.");
        }
    }

    private static (long? FreeBytes, string? Error) ProbeDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return (null, null);
        try
        {
            Directory.CreateDirectory(path);
            return (new DriveInfo(path).AvailableFreeSpace, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
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
