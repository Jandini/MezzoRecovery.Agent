using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.Agent.Devices;
using MezzoRecovery.TapeDrive.Linux;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.TapeOperations;

/// <summary>
/// Synchronous media operations (Rewind, Eject, EOD). Each runs under the per-device lock.
/// These operations are device-level utilities and do not report to the new tape-run pipeline.
/// Results are logged locally and reflected in the device status refresh at the end.
/// </summary>
public sealed class TapeMediaControlService(
    TapeDeviceLockManager locks,
    TapeOperationStateStore state,
    AgentDeviceStateStore deviceStore,
    DeviceReportPublisher publisher,
    TapeMediaLoader mediaLoader,
    ILogger<TapeMediaControlService> logger)
{
    public void Execute(HubConnection hub, ExecuteTapeMediaActionCommand command) =>
        _ = Task.Run(() => RunAsync(hub, command));

    private async Task RunAsync(HubConnection hub, ExecuteTapeMediaActionCommand command)
    {
        logger.LogInformation(
            "ExecuteTapeMediaAction {Action}: RunAsync started for device {DeviceId}.",
            command.OperationType, command.TapeDeviceId);

        if (string.IsNullOrWhiteSpace(command.NonRewindingDevicePath))
        {
            logger.LogWarning(
                "ExecuteTapeMediaAction {Action}: non-rewinding device path is required.",
                command.OperationType);
            return;
        }

        // State-store guard: blocks concurrent ops tracked by the agent.
        if (state.Get(command.TapeDeviceId) is not null)
        {
            logger.LogWarning(
                "ExecuteTapeMediaAction {Action}: device {DeviceId} already busy (state store).",
                command.OperationType, command.TapeDeviceId);
            return;
        }

        // Cache-based guard: avoids sending commands to a physically absent or busy tape
        // without the latency of an MTIOCGET ioctl. Hardware errors (drive actually busy,
        // no media) are caught in the try block below.
        var cacheSnapshot = deviceStore.GetByStableKey(command.StableDeviceKey);
        if (cacheSnapshot is not null)
        {
            if (cacheSnapshot.Status == AgentTapeDeviceStatus.Busy || cacheSnapshot.MediaStatus.IsBusy())
            {
                logger.LogWarning(
                    "ExecuteTapeMediaAction {Action}: device {DeviceId} busy per cached state; rejecting.",
                    command.OperationType, command.TapeDeviceId);
                return;
            }
            if (cacheSnapshot.Status == AgentTapeDeviceStatus.NoMedia
                || cacheSnapshot.MediaStatus == TapeMediaStatus.NoMedia)
            {
                logger.LogWarning(
                    "ExecuteTapeMediaAction {Action}: no media on device {DeviceId} per cached state; rejecting.",
                    command.OperationType, command.TapeDeviceId);
                return;
            }
        }

        // Immediately surface the in-motion state before acquiring the lock so the UI
        // responds without waiting for lock acquisition or tape hardware latency.
        var pendingMediaStatus = command.OperationType switch
        {
            TapeOperationTypes.Rewind => TapeMediaStatus.Rewinding,
            TapeOperationTypes.Eod    => TapeMediaStatus.FastForwarding,
            TapeOperationTypes.Eject  => TapeMediaStatus.Ejecting,
            _                          => TapeMediaStatus.Unknown,
        };
        deviceStore.UpdateMediaStatus(command.StableDeviceKey, pendingMediaStatus);
        deviceStore.UpdateStatus(command.StableDeviceKey, AgentTapeDeviceStatus.Busy, "PENDING");
        await publisher.PublishCurrentAsync(hub, CancellationToken.None);
        logger.LogInformation(
            "ExecuteTapeMediaAction {Action}: published Busy+{MediaStatus} for device {DeviceId}.",
            command.OperationType, pendingMediaStatus, command.TapeDeviceId);

        logger.LogInformation(
            "ExecuteTapeMediaAction {Action}: acquiring lock for device {DeviceId}.",
            command.OperationType, command.TapeDeviceId);
        using var deviceLock = await locks.AcquireAsync(command.StableDeviceKey, CancellationToken.None);
        logger.LogInformation(
            "ExecuteTapeMediaAction {Action}: lock acquired for device {DeviceId}.",
            command.OperationType, command.TapeDeviceId);

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
            // Correct the pending Busy state we published; fire-and-forget is fine here
            // since the race winner will publish its own state imminently.
            _ = publisher.PublishDeviceStateRefreshAsync(hub, command.StableDeviceKey, CancellationToken.None);
            return;
        }

        // Confirm MediaStatus via Observe now that the op is registered in the state store,
        // so the published value is backed by the canonical active-op derivation path.
        var prePublish = deviceStore.GetByStableKey(command.StableDeviceKey);
        if (prePublish is not null)
            mediaLoader.Observe(hub, prePublish, flags: null, AgentTapeDeviceStatus.Busy);

        deviceStore.UpdateStatus(command.StableDeviceKey, AgentTapeDeviceStatus.Busy, "BUSY");
        await publisher.PublishCurrentAsync(hub, CancellationToken.None);

        bool actionOk = false;
        string? actionError = null;

        try
        {
            logger.LogInformation(
                "ExecuteTapeMediaAction {Action}: opening tape session on {Path}.",
                command.OperationType, command.NonRewindingDevicePath);
            await using var tape = LinuxTapeSession.OpenRead(command.NonRewindingDevicePath);

            logger.LogInformation(
                "ExecuteTapeMediaAction {Action}: executing tape command on device {DeviceId}.",
                command.OperationType, command.TapeDeviceId);

            bool ok;
            TapeDrive.Models.TapeDeviceDiagnostics? diag;
            switch (command.OperationType)
            {
                case TapeOperationTypes.Rewind:
                    ok = tape.Navigator.TryRewind(out diag);
                    break;
                case TapeOperationTypes.Eject:
                    ok = tape.Navigator.TryEject(out diag);
                    break;
                case TapeOperationTypes.Eod:
                    ok = tape.Navigator.TrySeekEndOfRecordedMedia(out diag);
                    break;
                default:
                    ok = false;
                    diag = null;
                    break;
            }

            if (ok)
            {
                actionOk = true;
                logger.LogInformation(
                    "ExecuteTapeMediaAction {Action}: tape command succeeded on device {DeviceId}.",
                    command.OperationType, command.TapeDeviceId);
            }
            else
            {
                actionError = diag?.Detail ?? $"{command.OperationType} failed.";
                logger.LogWarning(
                    "ExecuteTapeMediaAction {Action}: tape command failed on device {DeviceId}: {Detail}",
                    command.OperationType, command.TapeDeviceId, actionError);
            }
        }
        catch (Exception ex)
        {
            actionError = ex.Message;
            logger.LogError(ex,
                "ExecuteTapeMediaAction {Action}: tape command threw on device {DeviceId}.",
                command.OperationType, command.TapeDeviceId);
        }
        finally
        {
            logger.LogInformation(
                "ExecuteTapeMediaAction {Action}: cleaning up for device {DeviceId}.",
                command.OperationType, command.TapeDeviceId);
            state.Remove(command.TapeDeviceId);
            cts.Dispose();

            try
            {
                await hub.SendAsync("ReportTapeMediaActionResult",
                    new TapeMediaActionResultReport(
                        command.StableDeviceKey,
                        command.OperationType,
                        actionOk,
                        actionOk ? null : actionError),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "ExecuteTapeMediaAction {Action}: failed to send result report for device {DeviceId}.",
                    command.OperationType, command.TapeDeviceId);
            }

            try
            {
                // Probe and publish just this device immediately so the UX updates
                // without waiting for the next scheduled full discovery sweep.
                await publisher.PublishDeviceStateRefreshAsync(hub, command.StableDeviceKey, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "ExecuteTapeMediaAction {Action}: post-op device report failed for device {DeviceId}.",
                    command.OperationType, command.TapeDeviceId);
            }
        }
    }
}
