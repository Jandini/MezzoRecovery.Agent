using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.TapeOperations;

/// <summary>
/// Cooperative cancel for the current operation on a given device.
/// The runner observes the cancellation and emits Cancelled itself.
/// </summary>
public sealed class StopOperationHandler(
    TapeOperationStateStore state,
    ILogger<StopOperationHandler> logger)
{
    public void RequestStop(Guid deviceId)
    {
        if (state.RequestStop(deviceId))
        {
            logger.LogInformation("Stop requested for device {DeviceId}.", deviceId);
        }
        else
        {
            logger.LogInformation(
                "Stop ignored for device {DeviceId}: no operation in progress.",
                deviceId);
        }
    }
}
