using MezzoRecovery.Agent.Configuration;
using MezzoRecovery.Agent.TapeOperations;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.Devices;

/// <summary>
/// All paths that report tape device state to the API funnel through here so the
/// store and the wire stay in lockstep.
/// </summary>
public sealed class DeviceReportPublisher(
    TapeDeviceDiscoveryService discovery,
    AgentDeviceStateStore store,
    TapeOperationStateStore operationState,
    DeviceDiscoveryOptions discoveryOptions,
    ILogger<DeviceReportPublisher> logger)
{
    /// <summary>
    /// Runs a full discovery sweep (slow, opens devices, refreshes everything),
    /// stores the result, and reports it to the API. Skips probing devices that
    /// currently have an active operation so the sweep never disturbs a read.
    /// </summary>
    public async Task PublishFullDiscoveryAsync(HubConnection hub, CancellationToken ct)
    {
        if (!discoveryOptions.Enabled)
            return;

        try
        {
            var busyKeys = operationState.SnapshotBusyStableKeys();
            var devices = discovery.DiscoverDevices(busyKeys);
            store.ReplaceAll(devices);
            await hub.InvokeAsync("ReportTapeDevices", devices.ToArray(), ct);
            logger.LogInformation("Reported {Count} tape device(s) (full discovery).", devices.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to publish full device discovery.");
            try
            {
                await hub.InvokeAsync("ReportTapeDeviceDiscoveryFailed", ex.Message, ct);
            }
            catch
            {
                // ignore secondary failure
            }
        }
    }

    /// <summary>
    /// Sends the current cached state to the API without re-running discovery.
    /// Used by the status poller after it mutates one or more device statuses.
    /// </summary>
    public async Task PublishCurrentAsync(HubConnection hub, CancellationToken ct)
    {
        var snapshot = store.Snapshot();
        if (snapshot.Count == 0) return;

        try
        {
            await hub.InvokeAsync("ReportTapeDevices", snapshot.ToArray(), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish cached device state.");
        }
    }
}
