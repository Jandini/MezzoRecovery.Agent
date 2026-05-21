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

        // Hardware pre-flight: if the drive is already mid-motion (typically because the
        // operator pressed the physical eject/load button), don't try to run on top of
        // it — the mt ioctl would either hang until the cartridge settles or fail with
        // an unhelpful error. Reject up front so the UI clears the request cleanly.
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

            if (NeedsMedia(command.OperationType)
                && (snapshot.Status == AgentTapeDeviceStatus.NoMedia
                    || snapshot.MediaStatus == TapeMediaStatus.NoMedia))
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

    // Eject is allowed against an empty drive (no-op door unlock); Rewind / Space
    // need a cartridge to operate on.
    private static bool NeedsMedia(string operationType) =>
        operationType is TapeOperationTypes.Rewind or TapeOperationTypes.Space;

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
