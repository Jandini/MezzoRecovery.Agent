using MezzoRecovery.Agent.Configuration;
using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.Tape.Models;
using MezzoRecovery.Tape.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MezzoRecovery.Agent.TapeJobs;

public sealed class TapeJobRunner(
    ITapeVerifyService verifyService,
    TapeDeviceLockManager locks,
    AgentJobStateStore jobState,
    TapeJobReporter reporter,
    IOptions<TapeJobOptions> tapeJobOptions,
    ILogger<TapeJobRunner> logger)
{
    public void Start(HubConnection hub, StartTapeReadJobCommand command) =>
        _ = RunJobAsync(hub, command);

    public void Cancel(CancelTapeJobCommand command) =>
        jobState.RequestCancel(command.JobId);

    private async Task RunJobAsync(HubConnection hub, StartTapeReadJobCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.NonRewindingDevicePath))
        {
            await reporter.RejectedAsync(hub, command.JobId, "DeviceNotFound",
                "Non-rewinding device path is required.", CancellationToken.None);
            return;
        }

        using var deviceLock = await locks.AcquireAsync(command.StableDeviceKey, CancellationToken.None);

        if (jobState.Get(command.JobId) is not null)
        {
            await reporter.RejectedAsync(hub, command.JobId, "DeviceBusy",
                "A job is already active for this device.", CancellationToken.None);
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource();
        var state = new AgentJobStateStore.RunningJobState(
            command.JobId,
            command.TapeDeviceId,
            command.StableDeviceKey,
            cts);

        if (!jobState.TryRegister(state))
        {
            await reporter.RejectedAsync(hub, command.JobId, "DeviceBusy",
                "Could not register job state.", CancellationToken.None);
            return;
        }

        try
        {
            await reporter.AcceptedAsync(hub, command.JobId, cts.Token);
            await reporter.StartedAsync(hub, command.JobId, cts.Token);

            var request = new TapeVerifyRequest
            {
                TapeDevicePath = command.NonRewindingDevicePath,
                TapeBlockSizeBytes = command.TapeBlockSizeBytes,
                BufferSizeBytes = command.BufferSizeBytes,
                ProgressIntervalSeconds = Math.Max(0, tapeJobOptions.Value.ProgressReportIntervalSeconds),
                RewindAfterComplete = false,
                EjectAfterComplete = false,
            };

            var progress = new Progress<TapeCloneProgress>(p =>
            {
                var message = TapeJobProgressMapper.FromProgress(command.JobId, p);
                state.LastStats = message;
                _ = reporter.ProgressAsync(hub, message, CancellationToken.None);
            });

            TapeCloneResult result;
            try
            {
                result = await verifyService.VerifyTapeAsync(request, progress, cts.Token);
            }
            catch (OperationCanceledException)
            {
                await reporter.CancelledAsync(hub, command.JobId, state.LastStats, CancellationToken.None);
                return;
            }

            var finalStats = TapeJobProgressMapper.FromStats(
                command.JobId,
                result.FinalStats,
                result.IsSuccess ? TapeClonePhase.Completed.ToString() : TapeClonePhase.Failed.ToString());
            state.LastStats = finalStats;

            if (result.IsSuccess)
            {
                await reporter.CompletedAsync(hub, command.JobId, finalStats, CancellationToken.None);
                return;
            }

            await reporter.FailedAsync(
                hub,
                command.JobId,
                result.FailureReason.ToString(),
                result.ErrorMessage,
                finalStats,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tape read job {JobId} failed unexpectedly.", command.JobId);
            await reporter.FailedAsync(
                hub,
                command.JobId,
                "UnexpectedError",
                ex.Message,
                state.LastStats,
                CancellationToken.None);
        }
        finally
        {
            state.IsRunning = false;
            jobState.Remove(command.JobId);
        }
    }
}
