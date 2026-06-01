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
    TapeRunRunner tapeRunRunner,
    TapeMediaControlService tapeMediaControl,
    AgentDeviceStateStore deviceStore,
    TapeFileHasher fileHasher,
    TapeFileUploader fileUploader,
    ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private volatile string? _tapeCacheDirectory = AgentPaths.DefaultCacheDirectory;

    public async Task RunAsync(CancellationToken ct)
    {
        using var _lock = new ProcessLock(AgentPaths.DefaultLockPath);

        var cfg = await AgentConfigLoader.LoadAsync(configPath, ct);
        var baseUri = new Uri(cfg.ApiBaseUrl.TrimEnd('/') + "/");
        var cred = await CredentialStore.TryLoadAsync(credentialPath, ct);
        if (cred is null)
        {
            _logger.LogError("No credentials at {Path}. Run 'mra enroll' first.", credentialPath);
            return;
        }

        // Initialise the uploader with credentials (once — hub updated on each connect).
        fileUploader.Initialize(baseUri, cred.AgentId, cred.ClientSecret,
            cacheDirectory: _tapeCacheDirectory ?? AgentPaths.DefaultCacheDirectory);

        // Start background workers. They run for the lifetime of the process.
        ObserveWorker(fileHasher.StartAsync(ct), "hasher");
        ObserveWorker(fileUploader.StartAsync(ct), "uploader");

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

                    // Update hub reference for background workers so they can report
                    // again after the connection is restored.
                    fileHasher.SetHub(hub!);
                    fileUploader.SetHub(hub!);

                    // Physical devices may have changed while we were disconnected —
                    // rescan SCSI hosts before republishing so new/removed drives are detected.
                    try
                    {
                        RemoveStaleScsiTapeDevices();
                        scsiEnumerator.ScanScsiHosts();
                        _logger.LogInformation("Post-reconnect SCSI scan completed.");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Post-reconnect SCSI rescan failed.");
                    }

                    for (var attempt = 1; attempt <= 5; attempt++)
                    {
                        try
                        {
                            await hub!.InvokeAsync("RegisterRuntime", hostname, os, arch, version, CancellationToken.None);
                            await ReportCacheStatusAsync(hub!, CancellationToken.None);
                            await reportPublisher.PublishFullDiscoveryAsync(hub!, CancellationToken.None);
                            await reportPublisher.PublishActiveOperationsAsync(hub!, CancellationToken.None);
                            await ResumePendingUploadsAsync(baseUri, cred, CancellationToken.None);
                            // Fire preflight for any tape that was loaded while we were offline —
                            // we can't assume the same cartridge is still present.
                            _ = TriggerReconnectPreflightAsync(hub!);
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

                // ── Hub handlers ───────────────────────────────────────────────

                hub.On<AgentConfigCommand>("UpdateAgentConfig", async cmd =>
                {
                    _tapeCacheDirectory = string.IsNullOrEmpty(cmd.TapeCacheDirectory)
                        ? AgentPaths.DefaultCacheDirectory
                        : cmd.TapeCacheDirectory;
                    fileUploader.UpdateConcurrency(cmd.MaxConcurrentFileUploads, cmd.MaxConcurrentPartsPerFile);
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

                hub.On<StartTapeRunCommand>("StartTapeRun", command =>
                {
                    _logger.LogInformation(
                        "StartTapeRun received: run {RunId} type={RunType} device {DeviceId}.",
                        command.RunId, command.RunType, command.TapeDeviceId);
                    tapeRunRunner.Start(hub!, command);
                    return Task.CompletedTask;
                });

                hub.On<CancelTapeRunCommand>("CancelTapeRun", async command =>
                {
                    _logger.LogInformation("CancelTapeRun received for run {RunId}.", command.RunId);
                    tapeRunRunner.RequestCancel(command.RunId);
                    // Immediately report the updated operation snapshot so the API and UI
                    // know the cancel was received even before the run terminates.
                    await reportPublisher.PublishActiveOperationsAsync(hub!, CancellationToken.None);
                });

                hub.On<CancelTapeRunUploadsCommand>("CancelTapeRunUploads", command =>
                {
                    _logger.LogInformation("CancelTapeRunUploads received for run {RunId}.", command.RunId);
                    fileUploader.CancelRunUploads(command.RunId);
                    return Task.CompletedTask;
                });

                hub.On<ResumeRunUploadsCommand>("ResumeRunUploads", async command =>
                {
                    _logger.LogInformation("ResumeRunUploads received for run {RunId}.", command.RunId);
                    fileUploader.ResumeRunUploads(command.RunId);
                    await ResumePendingUploadsAsync(baseUri, cred, ct, runId: command.RunId);
                });

                hub.On<PauseTapeRunUploadCommand>("PauseTapeRunUpload", command =>
                {
                    _logger.LogInformation("PauseTapeRunUpload received for run {RunId}.", command.RunId);
                    fileUploader.PauseRunUpload(command.RunId);
                    return Task.CompletedTask;
                });

                hub.On<ResumeTapeRunUploadCommand>("ResumeTapeRunUpload", command =>
                {
                    _logger.LogInformation("ResumeTapeRunUpload received for run {RunId}.", command.RunId);
                    fileUploader.ResumeRunUpload(command.RunId);
                    return Task.CompletedTask;
                });

                hub.On<StopTapeRunReadingCommand>("StopTapeRunReading", async command =>
                {
                    _logger.LogInformation("StopTapeRunReading received for run {RunId}.", command.RunId);
                    tapeRunRunner.RequestStopReading(command.RunId);
                    await reportPublisher.PublishActiveOperationsAsync(hub!, CancellationToken.None);
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

                // Wire hub reference into background workers immediately after connect.
                fileHasher.SetHub(hub);
                fileUploader.SetHub(hub);

                _logger.LogInformation("Connected to MezzoRecovery (agent {AgentId}).", cred.AgentId);
                await hub.InvokeAsync("RegisterRuntime", hostname, os, arch, version, ct);
                await ReportCacheStatusAsync(hub, ct);
                await reportPublisher.PublishFullDiscoveryAsync(hub, ct);
                await reportPublisher.PublishActiveOperationsAsync(hub, ct);
                await ResumePendingUploadsAsync(baseUri, cred, ct);
                var startupRescan = RescanOnStartupAsync(hub, ct);

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

    private async Task RescanOnStartupAsync(HubConnection hub, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Post-startup SCSI rescan started.");
            RemoveStaleScsiTapeDevices();
            scsiEnumerator.ScanScsiHosts();
            _logger.LogInformation("Post-startup SCSI scan completed. Re-discovering devices.");
            await reportPublisher.PublishFullDiscoveryAsync(hub, ct);
        }
        catch (OperationCanceledException)
        {
            // shutdown before rescan completed
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Post-startup SCSI rescan failed.");
        }
    }

    private async Task TriggerReconnectPreflightAsync(HubConnection hub)
    {
        try
        {
            // Brief settling delay so PublishFullDiscoveryAsync store updates are visible.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            foreach (var device in deviceStore.Snapshot())
            {
                // Only devices with a tape loaded need preflight — we can't assume the
                // same cartridge is present as before the disconnect.
                if (device.MediaStatus is TapeMediaStatus.NoMedia or TapeMediaStatus.Unknown)
                    continue;
                // PublishDeviceStateRefreshAsync skips busy devices internally.
                await reportPublisher.PublishDeviceStateRefreshAsync(
                    hub, device.StableDeviceKey, CancellationToken.None, forcePreflight: true);
            }
            _logger.LogInformation("Post-reconnect preflight complete.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-reconnect preflight trigger failed.");
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

    /// <summary>
    /// Fetches pending uploads from the server and re-enqueues any whose local
    /// .tic file still exists. Called on initial connect and every reconnect so
    /// orphaned uploads (from a crashed run or broken network window) are recovered.
    /// </summary>
    private async Task ResumePendingUploadsAsync(Uri baseUri, AgentCredentialFile cred, CancellationToken ct, Guid? runId = null)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var api = new AgentApiClient(http);

            var token = await api.GetTokenAsync(baseUri, new TokenApiRequest(cred.AgentId, cred.ClientSecret), ct);
            if (token is null)
            {
                _logger.LogWarning("ResumePendingUploads: could not obtain JWT; skipping.");
                return;
            }

            var pending = await api.GetPendingUploadsAsync(baseUri, token.AccessToken, ct, runId);
            if (pending is null || pending.Length == 0)
                return;

            _logger.LogInformation(
                "ResumePendingUploads: {Count} pending upload(s) returned by server.", pending.Length);

            var cacheDir = _tapeCacheDirectory ?? AgentPaths.DefaultCacheDirectory;
            var enqueued = 0;
            foreach (var item in pending)
            {
                // Skip uploads that are paused — they will remain paused until explicitly resumed.
                if (item.IsPaused)
                {
                    _logger.LogInformation(
                        "ResumePendingUploads: file {FileId} is paused; not re-enqueuing.", item.FileId);
                    continue;
                }

                var localPath = TapeRunCacheLayout.GetFilePath(cacheDir, item.RunId, item.TapeFileNumber);

                if (!File.Exists(localPath))
                {
                    _logger.LogWarning(
                        "ResumePendingUploads: local file not found for file {FileId} at {Path}; skipping.",
                        item.FileId, localPath);
                    continue;
                }

                fileUploader.Enqueue(new TapeFileUploader.WorkItem(
                    FileId:                    item.FileId,
                    RunId:                     item.RunId,
                    FilePath:                  localPath,
                    FileSizeBytes:             item.TotalBytes,
                    UploadOperationId:         null,
                    ExistingUploadSessionId:   item.UploadSessionId));
                enqueued++;
            }

            if (enqueued > 0)
                _logger.LogInformation(
                    "ResumePendingUploads: {Enqueued}/{Total} upload(s) re-enqueued.",
                    enqueued, pending.Length);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ResumePendingUploads failed; uploads will retry on next reconnect.");
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
            var fullPath = Path.GetFullPath(path);
            var drive = DriveInfo.GetDrives()
                .Where(d =>
                {
                    var name = d.Name.TrimEnd(Path.DirectorySeparatorChar);
                    return fullPath.StartsWith(name + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                           || fullPath == name;
                })
                .OrderByDescending(d => d.Name.Length)
                .FirstOrDefault();
            return drive is not null
                ? (drive.AvailableFreeSpace, null)
                : (null, $"No mounted drive found for path: {fullPath}");
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private void ObserveWorker(Task task, string name)
    {
        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted)
                _logger.LogError(t.Exception, "Background worker '{Name}' faulted unexpectedly.", name);
            else if (t.IsCanceled)
                _logger.LogDebug("Background worker '{Name}' was cancelled.", name);
        }, TaskScheduler.Default);
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
