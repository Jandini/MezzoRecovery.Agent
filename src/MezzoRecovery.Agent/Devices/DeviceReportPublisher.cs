using MezzoRecovery.Agent.Configuration;
using MezzoRecovery.Agent.TapeOperations;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.Devices;

/// <summary>
/// All paths that report tape device state and live operation state to the API
/// funnel through here so the store, the in-memory mirror, and the wire stay in
/// lockstep.
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

    /// <summary>
    /// Sends the agent's authoritative live-operation snapshot to the API. The
    /// API uses this to reconcile its in-memory <c>IDeviceLiveStateService</c>:
    /// any live entry the agent no longer claims is cleared and broadcast to
    /// the UI. Called after every operation terminates and on every poll tick,
    /// so a single dropped <c>TapeOperationCancelled</c> can never leave a
    /// ghost "still reading" badge on a card.
    /// </summary>
    public async Task PublishActiveOperationsAsync(HubConnection hub, CancellationToken ct)
    {
        try
        {
            var snapshots = operationState.BuildSnapshots();
            await hub.InvokeAsync("ReportActiveOperations", snapshots, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to publish active operations snapshot.");
        }
    }

    /// <summary>
    /// On-demand refresh for one drive: probe MT status when idle, push device
    /// cache, then reconcile live operations with the API.
    /// </summary>
    public async Task PublishDeviceStateRefreshAsync(
        HubConnection hub,
        string stableDeviceKey,
        CancellationToken ct)
    {
        if (!operationState.IsDeviceBusyByStableKey(stableDeviceKey))
        {
            var device = store.GetByStableKey(stableDeviceKey);
            if (device is not null)
            {
                var (status, labels) = discovery.ProbeStatus(device);
                if (store.UpdateStatus(stableDeviceKey, status, labels))
                    await PublishCurrentAsync(hub, ct);
            }
        }

        await PublishActiveOperationsAsync(hub, ct);
    }
}
