using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.Agent.Devices;
using MezzoRecovery.TapeDrive.Linux;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.TapeOperations;

/// <summary>
/// Synchronous media operations (rewind, eject, space). Each runs under the per-device lock
/// and reports a single terminal message -- no progress ticks.
/// </summary>
public sealed class TapeMediaControlService(
    TapeDeviceLockManager locks,
    TapeOperationStateStore state,
    TapeOperationReporter reporter,
    AgentDeviceStateStore deviceStore,
    DeviceReportPublisher publisher,
    ILogger<TapeMediaControlService> logger)
{
    public void Execute(HubConnection hub, ExecuteTapeMediaActionCommand command) =>
        _ = Task.Run(() => RunAsync(hub, command));

    private async Task RunAsync(HubConnection hub, ExecuteTapeMediaActionCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.NonRewindingDevicePath))
        {
            await reporter.FailedAsync(hub, BuildFailed(command, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                "InvalidPath", "Non-rewinding device path is required."), CancellationToken.None);
            return;
        }

        if (state.Get(command.TapeDeviceId) is not null)
        {
            var now = DateTimeOffset.UtcNow;
            await reporter.FailedAsync(hub, BuildFailed(command, now, now, "DeviceBusy",
                "Device already has an operation in progress."), CancellationToken.None);
            return;
        }

        // Hardware pre-flight: re-probe the drive *now* rather than trusting the cached
        // sweep. The cache may be many seconds stale, and acting on a stale "Ready" for a
        // drive whose cartridge was just pulled means OpenRead blocks long enough for the
        // UI to sit on "Ejecting · 00:28" before erroring with "No medium found". The
        // refresh path also pushes the corrected card back to the UX so the operator sees
        // the real state regardless of what we decide here.
        await publisher.PublishDeviceStateRefreshAsync(hub, command.StableDeviceKey, CancellationToken.None);

        var snapshot = deviceStore.GetByStableKey(command.StableDeviceKey);
        if (snapshot is not null)
        {
            if (snapshot.Status == AgentTapeDeviceStatus.Busy || snapshot.MediaStatus.IsBusy())
            {
                var now = DateTimeOffset.UtcNow;
                await reporter.FailedAsync(hub, BuildFailed(command, now, now, "DeviceBusy",
                    "Device is busy with another operation."), CancellationToken.None);
                return;
            }

            // Every media op (Eject, Rewind, Space) needs a cartridge present. Eject was
            // previously allowed through as a "harmless door unlock", but in practice
            // OpenRead blocks for tens of seconds on an empty drive before failing.
            if (snapshot.Status == AgentTapeDeviceStatus.NoMedia
                || snapshot.MediaStatus == TapeMediaStatus.NoMedia)
            {
                var now = DateTimeOffset.UtcNow;
                await reporter.FailedAsync(hub, BuildFailed(command, now, now, "NoMedia",
                    "No tape media loaded."), CancellationToken.None);
                return;
            }
        }

        using var deviceLock = await locks.AcquireAsync(command.StableDeviceKey, CancellationToken.None);

        var startedAt = DateTimeOffset.UtcNow;
        var cts = new CancellationTokenSource();
        var op = new TapeOperationStateStore.RunningOperation(
            command.TapeDeviceId,
            command.StableDeviceKey,
            command.OperationType,
            command.RequestedByUserId,
            startedAt,
            blockSizeBytes: 0,
            bufferSizeBytes: 0,
            cts);

        if (!state.TryRegister(op))
        {
            await reporter.FailedAsync(hub, BuildFailed(command, startedAt, DateTimeOffset.UtcNow,
                "DeviceBusy", "Race: another operation registered first."), CancellationToken.None);
            return;
        }

        if (deviceStore.UpdateStatus(command.StableDeviceKey, AgentTapeDeviceStatus.Busy, "BUSY"))
            await publisher.PublishCurrentAsync(hub, CancellationToken.None);

        try
        {
            await reporter.StartedAsync(
                hub,
                new TapeOperationStartedMessage(
                    command.TapeDeviceId,
                    command.OperationType,
                    command.RequestedByUserId,
                    startedAt,
                    BlockSizeBytes: 0,
                    BufferSizeBytes: 0),
                CancellationToken.None);

            await using var tape = LinuxTapeSession.OpenRead(command.NonRewindingDevicePath);
            var navigator = tape.Navigator;
            var ok = command.OperationType switch
            {
                TapeOperationTypes.Rewind => navigator.TryRewind(out _),
                TapeOperationTypes.Eject => navigator.TryEject(out _),
                TapeOperationTypes.Space => navigator.TrySpaceFilemarksForward(
                    Math.Max(1, command.SpaceCount ?? 1), out _),
                _ => false,
            };

            var completedAt = DateTimeOffset.UtcNow;
            if (!ok)
            {
                await reporter.FailedAsync(hub, BuildFailed(command, startedAt, completedAt,
                    "DeviceError", $"Media action {command.OperationType} failed."), CancellationToken.None);
                return;
            }

            await reporter.CompletedAsync(
                hub,
                new TapeOperationCompletedMessage(
                    command.TapeDeviceId,
                    command.OperationType,
                    command.RequestedByUserId,
                    startedAt,
                    completedAt,
                    BytesRead: 0,
                    BlocksRead: 0,
                    FilemarksRead: 0,
                    ThroughputMbps: 0,
                    ElapsedSeconds: (long)(completedAt - startedAt).TotalSeconds,
                    BlockSizeBytes: 0,
                    BufferSizeBytes: 0),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Media action {Action} on device {DeviceId} failed.",
                command.OperationType, command.TapeDeviceId);
            await reporter.FailedAsync(hub, BuildFailed(command, startedAt, DateTimeOffset.UtcNow,
                "DeviceError", ex.Message), CancellationToken.None);
        }
        finally
        {
            state.Remove(command.TapeDeviceId);
            cts.Dispose();

            try
            {
                await publisher.PublishActiveOperationsAsync(hub, CancellationToken.None);
                await publisher.PublishFullDiscoveryAsync(hub, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Post-operation device report failed for device {DeviceId}.", command.TapeDeviceId);
            }
        }
    }

    private static TapeOperationFailedMessage BuildFailed(
        ExecuteTapeMediaActionCommand command,
        DateTimeOffset startedAt,
        DateTimeOffset failedAt,
        string reason,
        string? message) =>
        new(
            command.TapeDeviceId,
            command.OperationType,
            command.RequestedByUserId,
            startedAt,
            failedAt,
            reason,
            message,
            BytesRead: 0,
            BlocksRead: 0,
            FilemarksRead: 0,
            ThroughputMbps: 0,
            ElapsedSeconds: 0,
            BlockSizeBytes: 0,
            BufferSizeBytes: 0);
}
