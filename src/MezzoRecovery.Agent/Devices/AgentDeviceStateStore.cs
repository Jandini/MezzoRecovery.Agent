using MezzoRecovery.Agent.Contracts;

namespace MezzoRecovery.Agent.Devices;

/// <summary>
/// Single source of truth on the agent for the current set of tape devices and
/// their live status (Ready / Busy / NoMedia / ...). Full discovery replaces all,
/// the status poller mutates the live fields, and tape operations push transient
/// Busy state. Always thread-safe; readers get an immutable snapshot.
/// </summary>
public sealed class AgentDeviceStateStore
{
    private readonly object _gate = new();
    private List<AgentTapeDeviceDto> _devices = [];

    public IReadOnlyList<AgentTapeDeviceDto> Snapshot()
    {
        lock (_gate)
            return _devices.Select(Clone).ToList();
    }

    /// <summary>Replaces every entry; used after a full discovery sweep.</summary>
    public void ReplaceAll(IEnumerable<AgentTapeDeviceDto> devices)
    {
        lock (_gate)
            _devices = devices.Select(Clone).ToList();
    }

    /// <summary>
    /// Updates the live status fields for the device with the given stable key.
    /// Returns <c>true</c> when something actually changed (so the caller can decide
    /// to publish), <c>false</c> if the device is unknown or the values match.
    /// </summary>
    public bool UpdateStatus(string stableDeviceKey, AgentTapeDeviceStatus status, string? mtStatusLabels)
    {
        lock (_gate)
        {
            var existing = _devices.FirstOrDefault(d => d.StableDeviceKey == stableDeviceKey);
            if (existing is null) return false;
            if (existing.Status == status && existing.MtStatusLabels == mtStatusLabels) return false;

            existing.Status = status;
            existing.MtStatusLabels = mtStatusLabels;
            return true;
        }
    }

    public AgentTapeDeviceDto? GetByStableKey(string stableDeviceKey)
    {
        lock (_gate)
        {
            var existing = _devices.FirstOrDefault(d => d.StableDeviceKey == stableDeviceKey);
            return existing is null ? null : Clone(existing);
        }
    }

    private static AgentTapeDeviceDto Clone(AgentTapeDeviceDto d) => new()
    {
        StableDeviceKey = d.StableDeviceKey,
        LinuxDevicePath = d.LinuxDevicePath,
        NonRewindingDevicePath = d.NonRewindingDevicePath,
        RewindingDevicePath = d.RewindingDevicePath,
        SysfsPath = d.SysfsPath,
        ScsiAddress = d.ScsiAddress,
        Vendor = d.Vendor,
        Model = d.Model,
        Revision = d.Revision,
        MtStatusLabels = d.MtStatusLabels,
        SerialNumber = d.SerialNumber,
        Status = d.Status,
        IsPresent = d.IsPresent,
        IsAccessible = d.IsAccessible,
        LastError = d.LastError,
    };
}
