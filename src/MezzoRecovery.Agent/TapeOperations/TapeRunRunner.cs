using System.Collections.Concurrent;
using MezzoRecovery.Agent.Configuration;
using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.Agent.Devices;
using MezzoRecovery.Tape.Models;
using MezzoRecovery.Tape.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MezzoRecovery.Agent.TapeOperations;

/// <summary>
/// Executes a tape run (Read or Clone) dispatched by a <see cref="StartTapeRunCommand"/>.
///
/// One run at a time per device. The runner:
///   1. Validates device state.
///   2. Runs a preflight step using cached device data.
///   3. Runs <c>ITapeCloneService</c> (Clone) or <c>ITapeVerifyService</c> (Read).
///   4. Reports run / operation / file lifecycle events to the API over AgentHub.
///   5. Forwards completed .tic files to <see cref="TapeFileHasher"/> (Clone only).
/// </summary>
public sealed class TapeRunRunner(
    ITapeCloneService cloneService,
    ITapeVerifyService verifyService,
    TapeDeviceLockManager locks,
    TapeOperationStateStore state,
    AgentDeviceStateStore deviceStore,
    DeviceReportPublisher publisher,
    TapeMediaLoader mediaLoader,
    TapeFileHasher hasher,
    IOptions<TapeOperationOptions> options,
    ILogger<TapeRunRunner> logger)
{
    // Lets CancelTapeRun map RunId → DeviceId without the device ever knowing.
    private readonly ConcurrentDictionary<Guid, Guid> _runToDevice = new();

    public void Start(HubConnection hub, StartTapeRunCommand command) =>
        _ = RunAsync(hub, command);

    /// <summary>
    /// Cancels the run whose ID matches <paramref name="runId"/>. Safe to call from
    /// any thread; no-op if the run is not active.
    /// </summary>
    public void RequestCancel(Guid runId)
    {
        if (_runToDevice.TryGetValue(runId, out var deviceId))
        {
            if (state.RequestStop(deviceId))
                logger.LogInformation("Cancel requested for run {RunId} (device {DeviceId}).", runId, deviceId);
        }
        else
        {
            logger.LogInformation("CancelTapeRun {RunId}: run not active.", runId);
        }
    }

    // ── Main run ───────────────────────────────────────────────────────────────

    private async Task RunAsync(HubConnection hub, StartTapeRunCommand command)
    {
        // ── Pre-flight guards ──────────────────────────────────────────────────

        if (string.IsNullOrWhiteSpace(command.NonRewindingDevicePath))
        {
            await SendRunCompletedAsync(hub, command.RunId, false, "InvalidPath",
                "Non-rewinding device path is required.", CancellationToken.None);
            return;
        }

        if (state.Get(command.TapeDeviceId) is not null)
        {
            await SendRunCompletedAsync(hub, command.RunId, false, "DeviceBusy",
                "Device already has an operation in progress.", CancellationToken.None);
            return;
        }

        var snapshot = deviceStore.GetByStableKey(command.StableDeviceKey);
        if (snapshot is not null)
        {
            if (snapshot.Status == AgentTapeDeviceStatus.Busy || snapshot.MediaStatus.IsBusy())
            {
                await SendRunCompletedAsync(hub, command.RunId, false, "DeviceBusy",
                    "Device is busy with another operation.", CancellationToken.None);
                return;
            }
            if (snapshot.Status == AgentTapeDeviceStatus.NoMedia
                || snapshot.MediaStatus == TapeMediaStatus.NoMedia)
            {
                await SendRunCompletedAsync(hub, command.RunId, false, "NoMedia",
                    "No tape media loaded.", CancellationToken.None);
                return;
            }
        }

        using var deviceLock = await locks.AcquireAsync(command.StableDeviceKey, CancellationToken.None);

        var startedAt          = DateTimeOffset.UtcNow;
        var cts                = new CancellationTokenSource();
        var effectiveBlockSize  = command.BlockSizeBytes  ?? snapshot?.ReadBlockSizeBytes ?? 0;
        var effectiveBufferSize = command.BufferSizeBytes ?? snapshot?.ReadBufferSizeBytes ?? 65536;

        var opType = command.RunType.Equals("Clone", StringComparison.OrdinalIgnoreCase)
            ? TapeOperationTypes.Clone
            : TapeOperationTypes.Read;

        var runOp = new TapeOperationStateStore.RunningOperation(
            command.TapeDeviceId,
            command.StableDeviceKey,
            opType,
            requestedByUserId: Guid.Empty,
            startedAt,
            effectiveBlockSize,
            effectiveBufferSize,
            cts);

        if (!state.TryRegister(runOp))
        {
            await SendRunCompletedAsync(hub, command.RunId, false, "DeviceBusy",
                "Race: another operation registered first.", CancellationToken.None);
            return;
        }

        _runToDevice[command.RunId] = command.TapeDeviceId;

        // Re-derive MediaStatus now that the operation is registered so PublishCurrentAsync
        // sends Busy+Reading rather than Busy+Ready (same fix as TapeMediaControlService).
        var prePublish = deviceStore.GetByStableKey(command.StableDeviceKey);
        if (prePublish is not null)
            mediaLoader.Observe(hub, prePublish, flags: null, AgentTapeDeviceStatus.Busy);

        deviceStore.UpdateStatus(command.StableDeviceKey, AgentTapeDeviceStatus.Busy, "BUSY");
        await publisher.PublishCurrentAsync(hub, CancellationToken.None);

        var fileReporter = new TapeFileReporter(command, hasher, logger);

        try
        {
            // ── Step 1: Preflight ──────────────────────────────────────────────
            await RunPreflightStepAsync(hub, command, snapshot, effectiveBlockSize, effectiveBufferSize);

            // ── Step 2: Clone / Read ───────────────────────────────────────────
            var mainOpId = command.CloneOperationId ?? command.ReadOperationId;
            if (mainOpId.HasValue)
                await HubSendAsync(hub, "ReportTapeOperationStarted",
                    new TapeOperationStartedReport(mainOpId.Value, DateTimeOffset.UtcNow));

            TapeCloneResult result;
            if (opType == TapeOperationTypes.Clone)
            {
                result = await RunCloneAsync(hub, command, runOp, fileReporter, cts.Token);
            }
            else
            {
                result = await RunReadAsync(hub, command, runOp, fileReporter, cts.Token);
            }

            if (mainOpId.HasValue)
                await HubSendAsync(hub, "ReportTapeOperationCompleted",
                    new TapeOperationCompletedReport(
                        mainOpId.Value,
                        result.IsSuccess,
                        result.IsSuccess ? null : result.FailureReason.ToString(),
                        result.IsSuccess ? null : result.ErrorMessage));

            if (result.IsSuccess)
                await fileReporter.OnReadFinishedAsync(hub, result.FinalStats, DateTimeOffset.UtcNow);
            else
            {
                var summary = result.ErrorMessage ?? result.FailureReason.ToString();
                RecordReadError(command, summary);
                await fileReporter.OnReadFailedAsync(hub, summary, DateTimeOffset.UtcNow);
            }

            await SendRunCompletedAsync(hub, command.RunId, result.IsSuccess,
                result.IsSuccess ? null : result.FailureReason.ToString(),
                result.IsSuccess ? null : result.ErrorMessage,
                CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await fileReporter.OnReadAbortedAsync(hub, DateTimeOffset.UtcNow);
            await SendRunCompletedAsync(hub, command.RunId, false, "Cancelled",
                "Run was cancelled.", CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tape run {RunId} failed unexpectedly.", command.RunId);
            RecordReadError(command, ex.Message);
            await fileReporter.OnReadFailedAsync(hub, ex.Message, DateTimeOffset.UtcNow);
            await SendRunCompletedAsync(hub, command.RunId, false, "UnexpectedError",
                ex.Message, CancellationToken.None);
        }
        finally
        {
            await fileReporter.DisposeAsync();
            _runToDevice.TryRemove(command.RunId, out _);
            state.Remove(command.TapeDeviceId);
            cts.Dispose();

            try
            {
                // Probe and publish just this device immediately so the UX reflects the
                // completed state without waiting for the next scheduled full discovery sweep.
                await publisher.PublishDeviceStateRefreshAsync(hub, command.StableDeviceKey, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Post-run device report failed for run {RunId}.", command.RunId);
            }
        }
    }

    // ── Preflight step ─────────────────────────────────────────────────────────

    private async Task RunPreflightStepAsync(
        HubConnection hub,
        StartTapeRunCommand command,
        AgentTapeDeviceDto? snapshot,
        int blockSize,
        int bufferSize)
    {
        var now = DateTimeOffset.UtcNow;

        await HubSendAsync(hub, "ReportTapeOperationStarted",
            new TapeOperationStartedReport(command.PreflightOperationId, now));

        // Use cached detection data — the clone service rewinds on its own,
        // so running IPreflightService here would cause a double-rewind.
        var deviceStatus = snapshot?.Status.ToString() ?? "Unknown";
        var mediaStatus  = snapshot?.MediaStatus.ToString() ?? "Unknown";

        // Effective block / buffer — prefer command values over cached
        int? detectedBlock  = blockSize  > 0 ? blockSize  : snapshot?.DetectedBlockSizeBytes;
        int? detectedBuffer = bufferSize > 0 ? bufferSize : snapshot?.DetectedBlockBufferSizeBytes;

        var detection = new MediaDetectionReport(
            TapeDeviceId:          command.TapeDeviceId,
            TapeRunId:             command.RunId,
            PreflightOperationId:  command.PreflightOperationId,
            DeviceStatus:          deviceStatus,
            MediaStatus:           mediaStatus,
            MediaFormat:           null,
            DetectorName:          null,
            DetectedBlockSizeBytes: detectedBlock,
            DetectedBufferSizeBytes: detectedBuffer,
            MediaHeaderHash:       null,
            MediaSetHash:          null,
            MediaFingerprintHash:  null,
            HeaderBytes:           null,
            HeaderPreviewText:     null,
            Status:                "Started",
            ErrorMessage:          null,
            LinuxDevicePath:       command.NonRewindingDevicePath,
            NonRewindingDevicePath: command.NonRewindingDevicePath);

        await HubSendAsync(hub, "ReportMediaDetection", detection);

        await HubSendAsync(hub, "ReportTapeOperationCompleted",
            new TapeOperationCompletedReport(
                command.PreflightOperationId, true, null, null));
    }

    // ── Clone run ──────────────────────────────────────────────────────────────

    private async Task<TapeCloneResult> RunCloneAsync(
        HubConnection hub,
        StartTapeRunCommand command,
        TapeOperationStateStore.RunningOperation op,
        TapeFileReporter fileReporter,
        CancellationToken ct)
    {
        var cacheDir = command.CacheDirectory ?? AgentPaths.DefaultCacheDirectory;
        var runDir   = TapeRunCacheLayout.GetRunDirectory(cacheDir, command.RunId);
        Directory.CreateDirectory(runDir);

        var effectiveBlockSize  = command.BlockSizeBytes  ?? 0;
        var effectiveBufferSize = Math.Max(512, command.BufferSizeBytes ?? 65536);

        var request = new TapeToImageRequest
        {
            TapeDevicePath    = command.NonRewindingDevicePath,
            ImageFilePath     = runDir,
            SplitByFile       = true,
            Force             = true,   // allow retry of same run
            TapeBlockSizeBytes = effectiveBlockSize,
            BufferSizeBytes   = effectiveBufferSize,
            RewindAfterComplete = true,
            EjectAfterComplete  = false,
        };

        var staleCleared = 0;
        var progress = new Progress<TapeCloneProgress>(p =>
        {
            if (ct.IsCancellationRequested) return;

            UpdateOpStats(op, p.Stats);
            UpdateDevicePhase(hub, command, p.Phase, p.Stats);

            if ((p.Stats.BytesProcessed > 0 || p.Stats.BlocksProcessed > 0)
                && Interlocked.Exchange(ref staleCleared, 1) == 0)
                ClearStalePreflightError(hub, command);

            _ = ReportProgressAsync(hub, command, op, p.Stats, TapeOperationTypes.Clone);
            fileReporter.OnProgress(hub, p.Stats, DateTimeOffset.UtcNow);
        });

        return await cloneService.CloneToImageAsync(request, progress, ct);
    }

    // ── Read-only (verify) run ─────────────────────────────────────────────────

    private async Task<TapeCloneResult> RunReadAsync(
        HubConnection hub,
        StartTapeRunCommand command,
        TapeOperationStateStore.RunningOperation op,
        TapeFileReporter fileReporter,
        CancellationToken ct)
    {
        var effectiveBlockSize  = command.BlockSizeBytes  ?? 0;
        var effectiveBufferSize = Math.Max(512, command.BufferSizeBytes ?? 65536);

        var request = new TapeVerifyRequest
        {
            TapeDevicePath         = command.NonRewindingDevicePath,
            TapeBlockSizeBytes     = effectiveBlockSize,
            BufferSizeBytes        = effectiveBufferSize,
            ProgressIntervalSeconds = Math.Max(0, options.Value.ProgressReportIntervalSeconds),
            RewindAfterComplete    = true,
            EjectAfterComplete     = false,
        };

        var staleCleared = 0;
        var progress = new Progress<TapeCloneProgress>(p =>
        {
            if (ct.IsCancellationRequested) return;

            UpdateOpStats(op, p.Stats);
            UpdateDevicePhase(hub, command, p.Phase, p.Stats);

            if ((p.Stats.BytesProcessed > 0 || p.Stats.BlocksProcessed > 0)
                && Interlocked.Exchange(ref staleCleared, 1) == 0)
                ClearStalePreflightError(hub, command);

            _ = ReportProgressAsync(hub, command, op, p.Stats, TapeOperationTypes.Read);
            fileReporter.OnProgress(hub, p.Stats, DateTimeOffset.UtcNow);
        });

        return await verifyService.VerifyTapeAsync(request, progress, ct);
    }

    // ── Progress helpers ───────────────────────────────────────────────────────

    private static void UpdateOpStats(
        TapeOperationStateStore.RunningOperation op, TapeCloneStats s)
    {
        op.LastBytesRead       = ToLong(s.BytesProcessed);
        op.LastBlocksRead      = ToLong(s.BlocksProcessed);
        op.LastFilemarksRead   = ToLong(s.FileMarksEncountered);
        op.LastThroughputMbps  = s.BytesPerSecond / 1_000_000.0;
        op.LastThroughputGbph  = s.BytesPerSecond * 3600.0 / 1_000_000_000.0;
        op.LastElapsedSeconds  = (long)s.Elapsed.TotalSeconds;
        op.LastProgressAt      = DateTimeOffset.UtcNow;
    }

    private void UpdateDevicePhase(
        HubConnection hub,
        StartTapeRunCommand command,
        TapeClonePhase phase,
        TapeCloneStats stats)
    {
        var rewinding = phase == TapeClonePhase.Rewinding;
        if (!state.SetRewindActiveByStableKey(command.StableDeviceKey, rewinding))
            return;

        var newMedia = rewinding ? TapeMediaStatus.Rewinding : TapeMediaStatus.Reading;
        if (deviceStore.UpdateMediaStatus(command.StableDeviceKey, newMedia))
            _ = publisher.PublishCurrentAsync(hub, CancellationToken.None);
    }

    private void ClearStalePreflightError(HubConnection hub, StartTapeRunCommand command)
    {
        if (deviceStore.UpdatePreflightResult(
                command.StableDeviceKey,
                command.BlockSizeBytes  > 0 ? command.BlockSizeBytes  : null,
                command.BufferSizeBytes > 0 ? command.BufferSizeBytes : null,
                null,
                DateTimeOffset.UtcNow))
            _ = publisher.PublishCurrentAsync(hub, CancellationToken.None);
    }

    private async Task ReportProgressAsync(
        HubConnection hub,
        StartTapeRunCommand command,
        TapeOperationStateStore.RunningOperation op,
        TapeCloneStats stats,
        string operationType)
    {
        var throughput = (long)stats.BytesPerSecond;

        await HubSendAsync(hub, "ReportTapeRunProgress",
            new TapeRunProgressReport(
                RunId:                       command.RunId,
                BytesRead:                   op.LastBytesRead,
                BlocksRead:                  op.LastBlocksRead,
                FilemarksRead:               op.LastFilemarksRead,
                TapeFilesCreated:            Math.Max(0, stats.CurrentFileNumber - 1),
                BytesUploaded:               0,
                FilesUploaded:               0,
                CurrentBlock:                op.LastBlocksRead,
                CurrentFileIndex:            stats.CurrentFileNumber,
                CurrentOperationType:        operationType,
                ReadThroughputBytesPerSecond: throughput > 0 ? throughput : null,
                UploadThroughputBytesPerSecond: null));

        var mainOpId = command.CloneOperationId ?? command.ReadOperationId;
        if (mainOpId.HasValue)
            await HubSendAsync(hub, "ReportTapeOperationProgress",
                new TapeOperationProgressReport(
                    mainOpId.Value,
                    op.LastBytesRead,
                    op.LastBlocksRead,
                    Math.Max(0, stats.CurrentFileNumber - 1),
                    CurrentBlock:             op.LastBlocksRead,
                    CurrentFileIndex:         stats.CurrentFileNumber,
                    ThroughputBytesPerSecond: throughput > 0 ? throughput : null));
    }

    private void RecordReadError(StartTapeRunCommand command, string? error)
    {
        var message = string.IsNullOrWhiteSpace(error) ? "Tape run failed." : error;
        var existing = deviceStore.GetByStableKey(command.StableDeviceKey);
        if (deviceStore.UpdatePreflightResult(
                command.StableDeviceKey,
                existing?.DetectedBlockSizeBytes,
                existing?.DetectedBlockBufferSizeBytes,
                message,
                DateTimeOffset.UtcNow))
            logger.LogDebug("Recorded read error on device {Key}.", command.StableDeviceKey);
    }

    // ── Hub helpers ────────────────────────────────────────────────────────────

    private static Task SendRunCompletedAsync(
        HubConnection hub,
        Guid runId, bool succeeded,
        string? reason, string? message,
        CancellationToken ct) =>
        HubSendCoreAsync(hub, "ReportTapeRunCompleted",
            new TapeRunCompletedReport(runId, succeeded, reason, message), ct);

    private static Task HubSendAsync<T>(HubConnection hub, string method, T payload) =>
        HubSendCoreAsync(hub, method, payload, CancellationToken.None);

    private static async Task HubSendCoreAsync<T>(
        HubConnection hub, string method, T payload, CancellationToken ct)
    {
        try   { await hub.SendAsync(method, payload, ct); }
        catch (Exception) { /* best-effort: hub may be reconnecting */ }
    }

    private static long ToLong(ulong value) =>
        value > (ulong)long.MaxValue ? long.MaxValue : (long)value;
}
