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
/// Fire-and-forget entry point that triggers preflight for a device. Exposed as an
/// interface so <c>TapeMediaLoader</c> can be tested without standing up the full runner.
/// </summary>
public interface ITapePreflightTrigger
{
    void Start(HubConnection hub, AgentTapeDeviceDto device);
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
    IOptions<TapeMediaLoaderOptions> options,
    ILogger<TapePreflightRunner> logger) : ITapePreflightTrigger
{
    // Inlined to avoid pulling DeviceReportPublisher in here: that would close a DI cycle
    // (runner → publisher → loader → trigger → runner), which Microsoft.Extensions.DependencyInjection
    // doesn't catch at BuildServiceProvider time and crashes the agent on first resolution.
    private async Task PublishDeviceSnapshotAsync(HubConnection hub, CancellationToken ct)
    {
        var snapshot = deviceStore.Snapshot();
        if (snapshot.Count == 0) return;
        try
        {
            await hub.InvokeAsync("ReportTapeDevices", snapshot.ToArray(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to publish device snapshot.");
        }
    }
    public void Start(HubConnection hub, AgentTapeDeviceDto device) =>
        _ = Task.Run(() => RunAsync(hub, device));

    private async Task RunAsync(HubConnection hub, AgentTapeDeviceDto device)
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

        // Flip media status to Identifying immediately so the UI doesn't wait
        // for the next 5s poll tick to learn that preflight has started.
        if (deviceStore.UpdateMediaStatus(stableKey, TapeMediaStatus.Identifying))
            await PublishDeviceSnapshotAsync(hub, CancellationToken.None);

        PreflightResult? result = null;
        string? failureMessage = null;
        try
        {
            var request = new PreflightRequest
            {
                TapeDevicePath = probePath,
                // Seed with last known buffer size to skip the doubling probe loop on the
                // re-identification of a known cartridge.
                InitialBufferSizeBytes = device.DetectedBlockBufferSizeBytes ?? 0,
                InitialBlockCount = Math.Max(1, options.Value.InitialBlockCount),
                RewindBeforeStart = options.Value.RewindBeforeStart,
            };

            logger.LogInformation("Preflight starting for device {Key} at {Path}.", stableKey, probePath);
            result = await preflightService.RunAsync(request, cts.Token).ConfigureAwait(false);
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
            detectedBufferSize = result.BlockBufferSize > 0 ? result.BlockBufferSize : null;
            error = null;
            terminal = TapeMediaStatus.Ready;
            logger.LogInformation(
                "Preflight completed for device {Key}: block size {BlockSize}, buffer {BufferSize}.",
                stableKey, result.BlockSize, result.BlockBufferSize);
        }
        else
        {
            error ??= result?.ErrorMessage ?? "Preflight failed.";
            terminal = TapeMediaStatus.Error;
            logger.LogWarning("Preflight failed for device {Key}: {Message}", stableKey, error);
        }

        // Order matters for callers observing state concurrently:
        // 1) record the result (so MediaStatus computed elsewhere sees it),
        // 2) release the busy flag,
        // 3) set the terminal MediaStatus directly (overrides any racey Identifying writes),
        // 4) publish a single snapshot to the API.
        deviceStore.UpdatePreflightResult(stableKey, detectedBlockSize, detectedBufferSize, error, completedAt);
        state.Remove(op.TapeDeviceId);
        cts.Dispose();
        deviceStore.UpdateMediaStatus(stableKey, terminal);

        await PublishDeviceSnapshotAsync(hub, CancellationToken.None);
    }
}
