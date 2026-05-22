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

    /// <summary>
    /// Replaces every entry; used after a full discovery sweep. Preflight history and the
    /// derived media status are carried forward by stable key so a routine re-discovery
    /// doesn't blow away the loader's identification of an already-known cartridge.
    /// </summary>
    public void ReplaceAll(IEnumerable<AgentTapeDeviceDto> devices)
    {
        lock (_gate)
        {
            var previous = _devices.ToDictionary(d => d.StableDeviceKey, StringComparer.Ordinal);
            var next = new List<AgentTapeDeviceDto>();
            foreach (var incoming in devices)
            {
                var clone = Clone(incoming);
                if (previous.TryGetValue(clone.StableDeviceKey, out var prev))
                {
                    clone.MediaStatus = prev.MediaStatus;
                    clone.DetectedBlockSizeBytes = prev.DetectedBlockSizeBytes;
                    clone.DetectedBlockBufferSizeBytes = prev.DetectedBlockBufferSizeBytes;
                    clone.LastPreflightAt = prev.LastPreflightAt;
                    clone.PreflightError = prev.PreflightError;
                    // Settings are server-owned; re-discovery on the agent must never reset them
                    // back to the DTO defaults, or the next preflight would auto-detect when the
                    // user had pinned the drive to manual mode.
                    clone.AutoDetectReadSettings = prev.AutoDetectReadSettings;
                    clone.ReadBlockSizeBytes = prev.ReadBlockSizeBytes;
                    clone.ReadBufferSizeBytes = prev.ReadBufferSizeBytes;
                }
                next.Add(clone);
            }
            _devices = next;
        }
    }

    /// <summary>Returns the stable keys that vanished between two discovery sweeps.</summary>
    public IReadOnlyList<string> StableKeysMissingFrom(IEnumerable<AgentTapeDeviceDto> incoming)
    {
        var incomingKeys = new HashSet<string>(incoming.Select(d => d.StableDeviceKey), StringComparer.Ordinal);
        lock (_gate)
            return _devices
                .Where(d => !incomingKeys.Contains(d.StableDeviceKey))
                .Select(d => d.StableDeviceKey)
                .ToList();
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

    /// <summary>
    /// Persists the result of a preflight run on this device. <paramref name="blockSize"/> and
    /// <paramref name="blockBufferSize"/> are stored as nullable; pass null on failure so the
    /// API/UI clearly distinguishes "never identified" from "tried and failed".
    /// </summary>
    public bool UpdatePreflightResult(
        string stableDeviceKey,
        int? blockSize,
        int? blockBufferSize,
        string? error,
        DateTimeOffset completedAt)
    {
        lock (_gate)
        {
            var existing = _devices.FirstOrDefault(d => d.StableDeviceKey == stableDeviceKey);
            if (existing is null) return false;
            if (existing.DetectedBlockSizeBytes == blockSize
                && existing.DetectedBlockBufferSizeBytes == blockBufferSize
                && existing.PreflightError == error
                && existing.LastPreflightAt == completedAt)
                return false;

            existing.DetectedBlockSizeBytes = blockSize;
            existing.DetectedBlockBufferSizeBytes = blockBufferSize;
            existing.PreflightError = error;
            existing.LastPreflightAt = completedAt;
            return true;
        }
    }

    /// <summary>
    /// Clears stale preflight-derived media state after another operation proves the
    /// cartridge is usable or absent.
    /// </summary>
    public bool ClearPreflightResult(string stableDeviceKey)
    {
        lock (_gate)
        {
            var existing = _devices.FirstOrDefault(d => d.StableDeviceKey == stableDeviceKey);
            if (existing is null) return false;
            if (existing.DetectedBlockSizeBytes is null
                && existing.DetectedBlockBufferSizeBytes is null
                && existing.LastPreflightAt is null
                && existing.PreflightError is null)
                return false;

            existing.DetectedBlockSizeBytes = null;
            existing.DetectedBlockBufferSizeBytes = null;
            existing.LastPreflightAt = null;
            existing.PreflightError = null;
            return true;
        }
    }

    /// <summary>
    /// Applies user-configured read settings pushed from the API (initial sync after
    /// ReportTapeDevices, or live UpdateTapeDeviceReadSettings commands). Returns true if any value changed.
    /// </summary>
    public bool UpdateReadSettings(string stableDeviceKey, bool autoDetect, int blockSize, int bufferSize)
    {
        lock (_gate)
        {
            var existing = _devices.FirstOrDefault(d => d.StableDeviceKey == stableDeviceKey);
            if (existing is null) return false;
            if (existing.AutoDetectReadSettings == autoDetect
                && existing.ReadBlockSizeBytes == blockSize
                && existing.ReadBufferSizeBytes == bufferSize)
                return false;

            existing.AutoDetectReadSettings = autoDetect;
            existing.ReadBlockSizeBytes = blockSize;
            existing.ReadBufferSizeBytes = bufferSize;
            return true;
        }
    }

    /// <summary>
    /// Sets the derived media lifecycle status. Returns true when it changed.
    /// </summary>
    public bool UpdateMediaStatus(string stableDeviceKey, TapeMediaStatus mediaStatus)
    {
        lock (_gate)
        {
            var existing = _devices.FirstOrDefault(d => d.StableDeviceKey == stableDeviceKey);
            if (existing is null) return false;
            if (existing.MediaStatus == mediaStatus) return false;

            existing.MediaStatus = mediaStatus;
            return true;
        }
    }

    // Keep in sync with AgentTapeDeviceDto: any new field on the DTO must be copied here.
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
        MediaStatus = d.MediaStatus,
        DetectedBlockSizeBytes = d.DetectedBlockSizeBytes,
        DetectedBlockBufferSizeBytes = d.DetectedBlockBufferSizeBytes,
        LastPreflightAt = d.LastPreflightAt,
        PreflightError = d.PreflightError,
        AutoDetectReadSettings = d.AutoDetectReadSettings,
        ReadBlockSizeBytes = d.ReadBlockSizeBytes,
        ReadBufferSizeBytes = d.ReadBufferSizeBytes,
    };
}
