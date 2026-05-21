using System.Collections.Concurrent;
using MezzoRecovery.Agent.Configuration;
using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.Agent.TapeOperations;
using MezzoRecovery.TapeDrive.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MezzoRecovery.Agent.Devices;

/// <summary>
/// Owns the tape-media lifecycle for every known device:
/// <list type="bullet">
///   <item>tracks the last time <c>DR_OPEN</c> was observed (eject signal),</item>
///   <item>derives <see cref="TapeMediaStatus"/> from drive flags + active operation + preflight history,</item>
///   <item>autonomously triggers preflight (via <see cref="TapePreflightRunner"/>) on first sighting and after every eject cycle.</item>
/// </list>
/// <para>
/// Called by the status poller every tick and by the discovery publisher after every sweep —
/// both busy and idle devices, so the derived <see cref="TapeMediaStatus"/> always reflects
/// reality even when the poller skips probing a busy device.
/// </para>
/// </summary>
public sealed class TapeMediaLoader(
    ITapePreflightTrigger trigger,
    TapeOperationStateStore operationState,
    AgentDeviceStateStore deviceStore,
    IOptions<TapeMediaLoaderOptions> options,
    ILogger<TapeMediaLoader> logger)
{
    // Per-device timestamp of the last DR_OPEN observation. Compared against the device's
    // LastPreflightAt to decide whether a new preflight is warranted.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastDoorOpenAt = new(StringComparer.Ordinal);

    /// <summary>
    /// Feeds one observation into the loader. Updates the door-open tracker, recomputes
    /// <see cref="TapeMediaStatus"/> for the device, and may fire a fire-and-forget preflight.
    /// Returns <c>true</c> when the device's <see cref="AgentTapeDeviceDto.MediaStatus"/> changed
    /// (the caller can use this to decide whether to publish).
    /// </summary>
    public bool Observe(
        HubConnection hub,
        AgentTapeDeviceDto device,
        TapeGstatFlags? flags,
        AgentTapeDeviceStatus driveStatus)
    {
        var stableKey = device.StableDeviceKey;
        if (string.IsNullOrEmpty(stableKey))
            return false;

        // Stamp DR_OPEN so the policy can compare LastDoorOpenAt vs LastPreflightAt.
        if (flags is { } f && f.HasFlag(TapeGstatFlags.DoorOpen))
            _lastDoorOpenAt[stableKey] = DateTimeOffset.UtcNow;

        _lastDoorOpenAt.TryGetValue(stableKey, out var lastDoorOpenAt);
        var activeOp = operationState.GetActiveOperationTypeByStableKey(stableKey);

        var computed = Compute(
            driveStatus,
            flags,
            activeOp,
            device.LastPreflightAt,
            device.PreflightError,
            lastDoorOpenAt == default ? null : lastDoorOpenAt);

        var changed = deviceStore.UpdateMediaStatus(stableKey, computed);

        if (ShouldTriggerPreflight(driveStatus, flags, activeOp, device, lastDoorOpenAt))
        {
            logger.LogInformation(
                "Triggering preflight for device {Key} (lastPreflightAt={LastPreflight}, lastDoorOpenAt={LastDoorOpen}).",
                stableKey, device.LastPreflightAt, lastDoorOpenAt == default ? null : (DateTimeOffset?)lastDoorOpenAt);
            trigger.Start(hub, device);
        }

        return changed;
    }

    /// <summary>Drops the door-open tracker entry for a device that no longer exists.</summary>
    public void ForgetDevice(string stableDeviceKey)
    {
        _lastDoorOpenAt.TryRemove(stableDeviceKey, out _);
    }

    /// <summary>
    /// Pure projection of the media lifecycle from its three inputs. Exposed for testing
    /// and so callers (e.g. <see cref="TapePreflightRunner"/>) can stay consistent.
    /// Resolution order is documented in the implementation.
    /// </summary>
    public static TapeMediaStatus Compute(
        AgentTapeDeviceStatus driveStatus,
        TapeGstatFlags? flags,
        string? activeOperationType,
        DateTimeOffset? lastPreflightAt,
        string? preflightError,
        DateTimeOffset? lastDoorOpenAt)
    {
        // 1. DR_OPEN (or the equivalent NoMedia drive status) dominates everything else —
        //    no cartridge means no lifecycle.
        if (flags is { } f && f.HasFlag(TapeGstatFlags.DoorOpen))
            return TapeMediaStatus.NoMedia;
        if (driveStatus == AgentTapeDeviceStatus.NoMedia)
            return TapeMediaStatus.NoMedia;

        // 2. An active operation projects directly onto the media-lifecycle state.
        if (activeOperationType is not null)
            return activeOperationType switch
            {
                TapeOperationTypes.Preflight => TapeMediaStatus.Identifying,
                TapeOperationTypes.Read => TapeMediaStatus.Reading,
                TapeOperationTypes.Rewind => TapeMediaStatus.Rewinding,
                TapeOperationTypes.Space => TapeMediaStatus.FastForwarding,
                TapeOperationTypes.Eject => TapeMediaStatus.Ejecting,
                _ => TapeMediaStatus.Unknown,
            };

        // 3. Drive must be online & ready for any cartridge-aware state.
        if (driveStatus != AgentTapeDeviceStatus.Ready)
            return TapeMediaStatus.Unknown;

        // 3a. GMT_CLN: drive is requesting a cleaning tape — surface this before any
        //     preflight result so the operator sees the warning immediately.
        if (flags is { } clnFlags && clnFlags.HasFlag(TapeGstatFlags.CleaningRequested))
            return TapeMediaStatus.CleaningRequired;

        // 4. Preflight history: a result is "current" iff no DR_OPEN happened since.
        var preflightIsCurrent = lastPreflightAt is not null
                                 && (lastDoorOpenAt is null || lastDoorOpenAt <= lastPreflightAt);

        if (preflightIsCurrent && preflightError is not null)
            return TapeMediaStatus.Error;
        if (preflightIsCurrent)
            return TapeMediaStatus.Ready;

        // 5. Cartridge present but never identified (or stale after an eject).
        return TapeMediaStatus.Loaded;
    }

    private bool ShouldTriggerPreflight(
        AgentTapeDeviceStatus driveStatus,
        TapeGstatFlags? flags,
        string? activeOp,
        AgentTapeDeviceDto device,
        DateTimeOffset lastDoorOpenAt)
    {
        if (!options.Value.Enabled) return false;
        if (driveStatus != AgentTapeDeviceStatus.Ready) return false;
        if (flags is { } f && f.HasFlag(TapeGstatFlags.DoorOpen)) return false;
        if (activeOp is not null) return false;
        if (string.IsNullOrWhiteSpace(device.NonRewindingDevicePath ?? device.LinuxDevicePath)) return false;

        // Fire when:
        //  a) the device has never been identified in this agent process (first sighting), OR
        //  b) a DR_OPEN sighting has happened since the last preflight (cartridge swap possible).
        if (device.LastPreflightAt is null) return true;
        if (lastDoorOpenAt != default && lastDoorOpenAt > device.LastPreflightAt) return true;

        return false;
    }
}
