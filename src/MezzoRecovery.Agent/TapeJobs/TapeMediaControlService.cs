using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.TapeDrive.Linux;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.TapeJobs;

public sealed class TapeMediaControlService(
    TapeDeviceLockManager locks,
    ILogger<TapeMediaControlService> logger)
{
    public async Task ExecuteAsync(HubConnection hub, ExecuteTapeMediaActionCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.NonRewindingDevicePath))
        {
            await ReportFailedAsync(hub, command, "InvalidPath", "Non-rewinding device path is required.", ct);
            return;
        }

        using var deviceLock = await locks.AcquireAsync(command.StableDeviceKey, ct);
        try
        {
            await using var tape = LinuxTapeSession.OpenRead(command.NonRewindingDevicePath);
            var navigator = tape.Navigator;
            var ok = command.Action switch
            {
                TapeMediaActions.Rewind => navigator.TryRewind(out _),
                TapeMediaActions.Eject => navigator.TryEject(out _),
                TapeMediaActions.SpaceFilemarksForward => navigator.TrySpaceFilemarksForward(
                    Math.Max(1, command.SpaceCount ?? 1),
                    out _),
                _ => false,
            };

            if (!ok)
            {
                await ReportFailedAsync(hub, command, "DeviceError", $"Media action {command.Action} failed.", ct);
                return;
            }

            await hub.InvokeAsync(
                "TapeMediaActionCompleted",
                new TapeMediaActionCompletedMessage(
                    command.CommandId,
                    command.TapeDeviceId,
                    command.Action,
                    DateTimeOffset.UtcNow),
                ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Media action {Action} failed for device {DeviceId}.", command.Action, command.TapeDeviceId);
            await ReportFailedAsync(hub, command, "DeviceError", ex.Message, ct);
        }
    }

    private static Task ReportFailedAsync(
        HubConnection hub,
        ExecuteTapeMediaActionCommand command,
        string code,
        string? message,
        CancellationToken ct) =>
        hub.InvokeAsync(
            "TapeMediaActionFailed",
            new TapeMediaActionFailedMessage(
                command.CommandId,
                command.TapeDeviceId,
                command.Action,
                code,
                message,
                DateTimeOffset.UtcNow),
            ct);
}
