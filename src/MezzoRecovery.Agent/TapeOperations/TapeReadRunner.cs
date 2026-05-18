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
/// Fire-and-forget tape read. Press Read -> the runner sends Started immediately and starts
/// streaming progress; press Stop -> the runner's CTS is cancelled and Cancelled is sent.
/// </summary>
public sealed class TapeReadRunner(
    ITapeVerifyService verifyService,
    TapeDeviceLockManager locks,
    TapeOperationStateStore state,
    TapeOperationReporter reporter,
    AgentDeviceStateStore deviceStore,
    DeviceReportPublisher publisher,
    IOptions<TapeOperationOptions> options,
    ILogger<TapeReadRunner> logger)
{
    public void Start(HubConnection hub, StartTapeReadCommand command) =>
        _ = RunAsync(hub, command);

    private async Task RunAsync(HubConnection hub, StartTapeReadCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.NonRewindingDevicePath))
        {
            await reporter.FailedAsync(
                hub,
                BuildFailedMessage(
                    command,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    "InvalidPath",
                    "Non-rewinding device path is required.",
                    TapeCloneStats.Empty),
                CancellationToken.None);
            return;
        }

        if (state.Get(command.TapeDeviceId) is not null)
        {
            var now = DateTimeOffset.UtcNow;
            await reporter.FailedAsync(
                hub,
                BuildFailedMessage(command, now, now, "DeviceBusy",
                    "Device already has an operation in progress.", TapeCloneStats.Empty),
                CancellationToken.None);
            return;
        }

        using var deviceLock = await locks.AcquireAsync(command.StableDeviceKey, CancellationToken.None);

        var startedAt = DateTimeOffset.UtcNow;
        var cts = new CancellationTokenSource();
        var op = new TapeOperationStateStore.RunningOperation(
            command.TapeDeviceId,
            command.StableDeviceKey,
            TapeOperationTypes.Read,
            command.RequestedByUserId,
            startedAt,
            command.TapeBlockSizeBytes,
            command.BufferSizeBytes,
            cts);

        if (!state.TryRegister(op))
        {
            await reporter.FailedAsync(
                hub,
                BuildFailedMessage(command, startedAt, DateTimeOffset.UtcNow, "DeviceBusy",
                    "Race: another operation registered first.", TapeCloneStats.Empty),
                CancellationToken.None);
            return;
        }

        // Flip the device's live status to Busy so the UI badge follows the operation,
        // then push the change so it lands without waiting for the next poll tick.
        if (deviceStore.UpdateStatus(command.StableDeviceKey, AgentTapeDeviceStatus.Busy, "BUSY"))
            await publisher.PublishCurrentAsync(hub, CancellationToken.None);

        try
        {
            await reporter.StartedAsync(
                hub,
                new TapeOperationStartedMessage(
                    command.TapeDeviceId,
                    TapeOperationTypes.Read,
                    command.RequestedByUserId,
                    startedAt,
                    command.TapeBlockSizeBytes,
                    command.BufferSizeBytes),
                CancellationToken.None);

            var request = new TapeVerifyRequest
            {
                TapeDevicePath = command.NonRewindingDevicePath,
                TapeBlockSizeBytes = command.TapeBlockSizeBytes,
                BufferSizeBytes = command.BufferSizeBytes,
                ProgressIntervalSeconds = Math.Max(0, options.Value.ProgressReportIntervalSeconds),
                RewindAfterComplete = false,
                EjectAfterComplete = false,
            };

            var progress = new Progress<TapeCloneProgress>(p =>
            {
                var (bytes, blocks, filemarks, mbps, gbph, elapsedSec) = TapeProgressMapper.Extract(p.Stats);
                op.LastBytesRead = bytes;
                op.LastBlocksRead = blocks;
                op.LastFilemarksRead = filemarks;
                op.LastThroughputMbps = mbps;
                op.LastThroughputGbph = gbph;
                op.LastElapsedSeconds = elapsedSec;
                op.LastProgressAt = DateTimeOffset.UtcNow;

                _ = reporter.ProgressAsync(
                    hub,
                    new TapeOperationProgressMessage(
                        command.TapeDeviceId,
                        TapeOperationTypes.Read,
                        bytes,
                        blocks,
                        filemarks,
                        mbps,
                        gbph,
                        elapsedSec,
                        op.LastProgressAt.Value),
                    CancellationToken.None);
            });

            TapeCloneResult result;
            try
            {
                result = await verifyService.VerifyTapeAsync(request, progress, cts.Token);
            }
            catch (OperationCanceledException)
            {
                var cancelledAt = DateTimeOffset.UtcNow;
                await reporter.CancelledAsync(
                    hub,
                    new TapeOperationCancelledMessage(
                        command.TapeDeviceId,
                        TapeOperationTypes.Read,
                        command.RequestedByUserId,
                        startedAt,
                        cancelledAt,
                        op.LastBytesRead,
                        op.LastBlocksRead,
                        op.LastFilemarksRead,
                        op.LastThroughputMbps,
                        op.LastElapsedSeconds,
                        command.TapeBlockSizeBytes,
                        command.BufferSizeBytes),
                    CancellationToken.None);
                return;
            }

            var (fBytes, fBlocks, fFilemarks, fMbps, _, fElapsed) = TapeProgressMapper.Extract(result.FinalStats);
            if (result.IsSuccess)
            {
                await reporter.CompletedAsync(
                    hub,
                    new TapeOperationCompletedMessage(
                        command.TapeDeviceId,
                        TapeOperationTypes.Read,
                        command.RequestedByUserId,
                        startedAt,
                        DateTimeOffset.UtcNow,
                        fBytes,
                        fBlocks,
                        fFilemarks,
                        fMbps,
                        fElapsed,
                        command.TapeBlockSizeBytes,
                        command.BufferSizeBytes),
                    CancellationToken.None);
                return;
            }

            await reporter.FailedAsync(
                hub,
                BuildFailedMessage(command, startedAt, DateTimeOffset.UtcNow,
                    result.FailureReason.ToString(), result.ErrorMessage, result.FinalStats),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tape read on device {DeviceId} failed unexpectedly.", command.TapeDeviceId);
            await reporter.FailedAsync(
                hub,
                BuildFailedMessage(command, startedAt, DateTimeOffset.UtcNow, "UnexpectedError", ex.Message,
                    TapeCloneStats.Empty),
                CancellationToken.None);
        }
        finally
        {
            state.Remove(command.TapeDeviceId);
            cts.Dispose();

            // Operation is over. Push the authoritative active-ops snapshot first so
            // the API drops any stale live entry for this device (heals the case where
            // the TapeOperationCancelled/Completed frame was delayed or dropped), then
            // run a full discovery so the device status (Ready / NoMedia / ...) is
            // also fresh on the UI.
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

    private static TapeOperationFailedMessage BuildFailedMessage(
        StartTapeReadCommand command,
        DateTimeOffset startedAt,
        DateTimeOffset failedAt,
        string reason,
        string? message,
        TapeCloneStats stats)
    {
        var (bytes, blocks, filemarks, mbps, _, elapsed) = TapeProgressMapper.Extract(stats);
        return new TapeOperationFailedMessage(
            command.TapeDeviceId,
            TapeOperationTypes.Read,
            command.RequestedByUserId,
            startedAt,
            failedAt,
            reason,
            message,
            bytes,
            blocks,
            filemarks,
            mbps,
            elapsed,
            command.TapeBlockSizeBytes,
            command.BufferSizeBytes);
    }
}
