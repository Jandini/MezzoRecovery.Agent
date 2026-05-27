using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.Agent.Devices;
using MezzoRecovery.TapeDrive.Linux;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.TapeOperations;

/// <summary>
/// Synchronous media operations (Rewind, Eject, Space). Each runs under the per-device lock.
/// These operations are device-level utilities and do not report to the new tape-run pipeline.
/// Results are logged locally and reflected in the device status refresh at the end.
/// </summary>
public sealed class TapeMediaControlService(
    TapeDeviceLockManager locks,
    TapeOperationStateStore state,
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
            logger.LogWarning(
                "ExecuteTapeMediaAction {Action}: non-rewinding device path is required.",
                command.OperationType);
            return;
        }

        if (state.Get(command.TapeDeviceId) is not null)
        {
            logger.LogWarning(
                "ExecuteTapeMediaAction {Action}: device {DeviceId} already busy.",
                command.OperationType, command.TapeDeviceId);
            return;
        }

        // Re-probe the drive before acting so we never act on a stale Busy/NoMedia cache.
        await publisher.PublishDeviceStateRefreshAsync(hub, command.StableDeviceKey, CancellationToken.None);

        var snapshot = deviceStore.GetByStableKey(command.StableDeviceKey);
        if (snapshot is not null)
        {
            if (snapshot.Status == AgentTapeDeviceStatus.Busy || snapshot.MediaStatus.IsBusy())
            {
                logger.LogWarning(
                    "ExecuteTapeMediaAction {Action}: device {DeviceId} is busy.",
                    command.OperationType, command.TapeDeviceId);
                return;
            }

            if (snapshot.Status == AgentTapeDeviceStatus.NoMedia
                || snapshot.MediaStatus == TapeMediaStatus.NoMedia)
            {
                logger.LogWarning(
                    "ExecuteTapeMediaAction {Action}: no media loaded on device {DeviceId}.",
                    command.OperationType, command.TapeDeviceId);
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
            logger.LogWarning(
                "ExecuteTapeMediaAction {Action}: race — another op registered first.",
                command.OperationType);
            return;
        }

        if (deviceStore.UpdateStatus(command.StableDeviceKey, AgentTapeDeviceStatus.Busy, "BUSY"))
            await publisher.PublishCurrentAsync(hub, CancellationToken.None);

        try
        {
            await using var tape = LinuxTapeSession.OpenRead(command.NonRewindingDevicePath);
            var navigator = tape.Navigator;
            var ok = command.OperationType switch
            {
                TapeOperationTypes.Rewind => navigator.TryRewind(out _),
                TapeOperationTypes.Eject  => navigator.TryEject(out _),
                TapeOperationTypes.Space  => navigator.TrySpaceFilemarksForward(
                    Math.Max(1, command.SpaceCount ?? 1), out _),
                _ => false,
            };

            if (ok)
            {
                deviceStore.ClearPreflightResult(command.StableDeviceKey);
                logger.LogInformation(
                    "ExecuteTapeMediaAction {Action} succeeded on device {DeviceId}.",
                    command.OperationType, command.TapeDeviceId);
            }
            else
            {
                logger.LogWarning(
                    "ExecuteTapeMediaAction {Action} failed on device {DeviceId}.",
                    command.OperationType, command.TapeDeviceId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Media action {Action} on device {DeviceId} threw.",
                command.OperationType, command.TapeDeviceId);
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
                logger.LogWarning(ex,
                    "Post-op device report failed for device {DeviceId}.", command.TapeDeviceId);
            }
        }
    }
}
