using MezzoRecovery.Agent.Configuration;
using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.Agent.Devices;
using MezzoRecovery.Tape.Models;
using MezzoRecovery.Tape.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;

namespace MezzoRecovery.Agent.TapeOperations;

/// <summary>
/// Fire-and-forget entry point that triggers preflight for a device. Exposed as an
/// interface so <c>TapeMediaLoader</c> can be tested without standing up the full runner.
/// </summary>
public interface ITapePreflightTrigger
{
    /// <param name="rewindBeforeStart">
    /// When <c>true</c> the preflight rewinds to BOT before reading, regardless of the
    /// configured default. Pass <c>true</c> for operator-initiated Refresh (tape may be
    /// anywhere); leave <c>false</c> for auto-triggered preflight on first cartridge sighting
    /// (tape is already at BOT).
    /// </param>
    void Start(HubConnection hub, AgentTapeDeviceDto device, bool rewindBeforeStart = false);
}

/// <summary>
/// Runs <see cref="IPreflightService"/> against a freshly-loaded cartridge to identify its
/// block size, and publishes the result back via the device store. Triggered by
/// <c>TapeMediaLoader</c>, never by an API command, so the operation is registered with a
/// synthetic <c>TapeDeviceId</c> Guid and is omitted from the wire-level active-ops snapshot
/// (the UI learns about preflight via <see cref="TapeMediaStatus.Identifying"/> on the DTO).
/// </summary>
public sealed class TapePreflightRunner(
    IPreflightService preflightService,
    TapeDeviceLockManager locks,
    TapeOperationStateStore state,
    AgentDeviceStateStore deviceStore,
    TapeDeviceDiscoveryService discovery,
    TapeMediaIdentificationReporter reporter,
    IOptions<TapeMediaLoaderOptions> options,
    ILogger<TapePreflightRunner> logger) : ITapePreflightTrigger
{
    // Inlined to avoid pulling DeviceReportPublisher in here: that would close a DI cycle
    // (runner → publisher → loader → trigger → runner), which Microsoft.Extensions.DependencyInjection
    // doesn't catch at BuildServiceProvider time and crashes the agent on first resolution.
    // The mapping helpers are internal static on DeviceReportPublisher so we can reuse them.
    private async Task PublishDeviceSnapshotAsync(HubConnection hub, CancellationToken ct)
    {
        var snapshot = deviceStore.Snapshot();
        if (snapshot.Count == 0) return;
        try
        {
            await hub.InvokeAsync(
                "ReportTapeDevices",
                snapshot.Select(DeviceReportPublisher.MapToWire).ToArray(),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to publish device snapshot.");
        }
    }
    public void Start(HubConnection hub, AgentTapeDeviceDto device, bool rewindBeforeStart = false) =>
        _ = Task.Run(() => RunAsync(hub, device, rewindBeforeStart));

    private async Task RunAsync(HubConnection hub, AgentTapeDeviceDto device, bool rewindBeforeStart = false)
    {
        var stableKey = device.StableDeviceKey;
        var probePath = device.NonRewindingDevicePath ?? device.LinuxDevicePath;

        if (string.IsNullOrWhiteSpace(probePath))
        {
            logger.LogWarning("Preflight aborted: device {Key} has no usable path.", stableKey);
            return;
        }

        // Re-check busy state under the lock — the loader's pre-trigger check is racey.
        if (state.IsDeviceBusyByStableKey(stableKey))
        {
            logger.LogDebug("Preflight skipped for {Key}: device already busy.", stableKey);
            return;
        }

        using var deviceLock = await locks.AcquireAsync(stableKey, CancellationToken.None);

        // After acquiring the lock, re-check that nobody snuck an op in front of us.
        if (state.IsDeviceBusyByStableKey(stableKey))
        {
            logger.LogDebug("Preflight skipped for {Key}: another operation registered first.", stableKey);
            return;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var cts = new CancellationTokenSource();
        var op = new TapeOperationStateStore.RunningOperation(
            tapeDeviceId: Guid.NewGuid(), // synthetic — the API never sees this for Preflight
            stableDeviceKey: stableKey,
            operationType: TapeOperationTypes.Preflight,
            requestedByUserId: Guid.Empty,
            startedAt: startedAt,
            blockSizeBytes: 0,
            bufferSizeBytes: 0,
            cts: cts);

        if (!state.TryRegister(op))
        {
            logger.LogDebug("Preflight could not register operation for {Key}.", stableKey);
            return;
        }

        // Flip both hardware Status (the chip) and MediaStatus (the lifecycle row) immediately
        // so the UI doesn't wait for the next 5s poll tick. Mirrors the pattern in
        // TapeReadRunner / TapeMediaControlService — every operation that holds the drive
        // surfaces as Busy so the chip never lies.
        var statusChanged = deviceStore.UpdateStatus(stableKey, AgentTapeDeviceStatus.Busy, "BUSY");
        var mediaChanged = deviceStore.UpdateMediaStatus(stableKey, TapeMediaStatus.Identifying);
        if (statusChanged || mediaChanged)
            await PublishDeviceSnapshotAsync(hub, CancellationToken.None);

        PreflightResult? result = null;
        string? failureMessage = null;
        try
        {
            // Always seed the buffer from the user-configured READ SETTINGS. When AutoDetect is
            // off, we also pass the fixed block size and the Tape preflight service skips its
            // doubling probe loop entirely (still reads up to InitialBlockCount blocks so the
            // media-identification bytes come back).
            var request = new PreflightRequest
            {
                TapeDevicePath = probePath,
                AutoDetect = device.AutoDetectReadSettings,
                InitialBufferSizeBytes = device.ReadBufferSizeBytes > 0
                    ? device.ReadBufferSizeBytes
                    : (device.DetectedBlockBufferSizeBytes ?? 0),
                FixedBlockSizeBytes = device.AutoDetectReadSettings ? 0 : device.ReadBlockSizeBytes,
                InitialBlockCount = Math.Max(1, options.Value.InitialBlockCount),
                // Refresh forces a rewind to BOT regardless of the configured default so
                // identification always reads from the start of the tape.
                RewindBeforeStart = rewindBeforeStart || options.Value.RewindBeforeStart,
            };

            logger.LogInformation("Preflight starting for device {Key} at {Path}.", stableKey, probePath);

            // While the preflight service is mid-rewind, surface MediaStatus.Rewinding even
            // though the wrapping op is Preflight. The flag is read on the next loader
            // observation tick; we also publish immediately so the UI doesn't wait.
            var phaseProgress = new Progress<TapeClonePhase>(phase =>
            {
                var rewinding = phase == TapeClonePhase.Rewinding;
                if (!state.SetRewindActiveByStableKey(stableKey, rewinding))
                    return;

                var newStatus = rewinding ? TapeMediaStatus.Rewinding : TapeMediaStatus.Identifying;
                if (deviceStore.UpdateMediaStatus(stableKey, newStatus))
                    _ = PublishDeviceSnapshotAsync(hub, CancellationToken.None);
            });

            result = await preflightService.RunAsync(request, cts.Token, phaseProgress).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            failureMessage = "Preflight cancelled.";
            logger.LogInformation("Preflight cancelled for device {Key}.", stableKey);
        }
        catch (Exception ex)
        {
            failureMessage = ex.Message;
            logger.LogError(ex, "Preflight crashed for device {Key}.", stableKey);
        }

        var completedAt = DateTimeOffset.UtcNow;
        int? detectedBlockSize = null;
        int? detectedBufferSize = null;
        string? error = failureMessage;
        TapeMediaStatus terminal;

        if (result is not null && result.IsReadable)
        {
            detectedBlockSize = result.BlockSize > 0 ? result.BlockSize : null;
            // Use 0 (not null) for blank tape so TapeMediaLoader.Compute can distinguish
            // "confirmed blank cartridge" (0) from "state cleared by transport" (null).
            detectedBufferSize = result.IsEmpty ? 0 : (result.BlockBufferSize > 0 ? result.BlockBufferSize : null);
            error = null;
            terminal = result.IsEmpty ? TapeMediaStatus.Empty : TapeMediaStatus.Ready;
            logger.LogInformation(
                "Preflight completed for device {Key}: media={Media}, block size {BlockSize}, buffer {BufferSize}.",
                stableKey, terminal, result.BlockSize, result.BlockBufferSize);
        }
        else
        {
            error ??= result?.ErrorMessage ?? "Preflight failed.";
            terminal = TapeMediaStatus.Error;
            logger.LogWarning("Preflight failed for device {Key}: {Message}", stableKey, error);
        }

        // Re-probe the drive now that preflight has released the device file. We do this
        // *before* removing the op from operationState so the status poller can't race in
        // and observe the device idle with a stale Busy status (it skips probing while the
        // op is registered). The lock is still held so no other operation can collide.
        var refreshed = deviceStore.GetByStableKey(stableKey) ?? device;
        var (probedStatus, probedLabels, _) = discovery.ProbeStatus(refreshed);

        // Order matters for callers observing state concurrently:
        // 1) record the preflight result (so MediaStatus computed elsewhere sees it),
        // 2) restore the real hardware status (clears the Busy chip),
        // 3) release the busy flag,
        // 4) set the terminal MediaStatus directly (overrides any racey Identifying writes),
        // 5) publish a single snapshot to the API.
        deviceStore.UpdatePreflightResult(stableKey, detectedBlockSize, detectedBufferSize, error, completedAt);
        deviceStore.UpdateStatus(stableKey, probedStatus, probedLabels);
        state.Remove(op.TapeDeviceId);
        cts.Dispose();
        deviceStore.UpdateMediaStatus(stableKey, terminal);

        await PublishDeviceSnapshotAsync(hub, CancellationToken.None);
        await reporter.ReportAsync(hub, device, result, failureMessage, completedAt, CancellationToken.None);
    }
}
