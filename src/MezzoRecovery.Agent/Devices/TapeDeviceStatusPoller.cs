using MezzoRecovery.Agent.Configuration;
using MezzoRecovery.Agent.TapeOperations;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MezzoRecovery.Agent.Devices;

/// <summary>
/// Live status poller. Every <see cref="TapeDeviceStatusOptions.PollIntervalSeconds"/>
/// seconds, snapshots the device cache, probes each idle device with a non-blocking
/// MTIOCGET (ONLINE / BUSY / NOT_READY), and publishes the cache if anything changed.
/// Devices with an active tape operation are skipped so the poller never disturbs a read.
/// </summary>
public sealed class TapeDeviceStatusPoller(
    AgentDeviceStateStore deviceStore,
    TapeOperationStateStore operationState,
    TapeDeviceDiscoveryService discovery,
    DeviceReportPublisher publisher,
    IOptions<TapeDeviceStatusOptions> options,
    ILogger<TapeDeviceStatusPoller> logger)
{
    public async Task RunAsync(HubConnection hub, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds));

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    if (await PollOnceAsync(ct))
                        await publisher.PublishCurrentAsync(hub, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Status poll iteration failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    private Task<bool> PollOnceAsync(CancellationToken ct)
    {
        var snapshot = deviceStore.Snapshot();
        if (snapshot.Count == 0)
            return Task.FromResult(false);

        var anyChanged = false;
        foreach (var device in snapshot)
        {
            if (ct.IsCancellationRequested) break;
            if (operationState.IsDeviceBusyByStableKey(device.StableDeviceKey))
                continue;

            var (status, labels) = discovery.ProbeStatus(device);
            if (deviceStore.UpdateStatus(device.StableDeviceKey, status, labels))
                anyChanged = true;
        }

        return Task.FromResult(anyChanged);
    }
}
