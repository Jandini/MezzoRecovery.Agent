using MezzoRecovery.Agent.Contracts;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.TapeOperations;

/// <summary>
/// Maps a StopTapeOperation command to a cooperative cancel on the current operation
/// for that device. The runner observes the cancellation and emits Cancelled itself.
/// </summary>
public sealed class StopOperationHandler(
    TapeOperationStateStore state,
    ILogger<StopOperationHandler> logger)
{
    public void RequestStop(StopTapeOperationCommand command)
    {
        if (state.RequestStop(command.TapeDeviceId))
        {
            logger.LogInformation("Stop requested for device {DeviceId}.", command.TapeDeviceId);
        }
        else
        {
            logger.LogInformation(
                "Stop ignored for device {DeviceId}: no operation in progress.",
                command.TapeDeviceId);
        }
    }
}
