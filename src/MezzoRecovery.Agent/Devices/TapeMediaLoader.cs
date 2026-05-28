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
///   <item>tracks the last no-media observation per device (DR_OPEN flag or NoMedia drive status),</item>
///   <item>derives <see cref="TapeMediaStatus"/> from drive flags + active operation + preflight history,</item>
///   <item>autonomously triggers preflight on first sighting and on no-media → ready transitions (tape insertion).</item>
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
    // Per-device timestamp of the last no-media observation (DR_OPEN flag OR NoMedia drive status).
    // Compared against LastPreflightAt to detect tape insertions: any no-media seen after the
    // last preflight means a (possibly new) tape was subsequently inserted → re-identify.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastNoMediaAt = new(StringComparer.Ordinal);

    /// <summary>
    /// Feeds one observation into the loader. Updates the door-open tracker, recomputes
    /// <see cref="TapeMediaStatus"/> for the device, and may fire a fire-and-forget preflight.
    /// Returns <c>true</c> when the device's <see cref="AgentTapeDeviceDto.MediaStatus"/> changed
    /// (the caller can use this to decide whether to publish).
    /// </summary>
    /// <param name="forcePreflight">
    /// When <c>true</c> the normal history-based suppression is bypassed and preflight is
    /// started as long as the drive is ready and not already busy (e.g. explicit Refresh).
    /// </param>
    public bool Observe(
        HubConnection hub,
        AgentTapeDeviceDto device,
        TapeGstatFlags? flags,
        AgentTapeDeviceStatus driveStatus,
        bool forcePreflight = false)
    {
        var stableKey = device.StableDeviceKey;
        if (string.IsNullOrEmpty(stableKey))
            return false;

        // Track any no-media observation regardless of whether DR_OPEN is set.
        // Linux does not reliably set DR_OPEN on all drives, so we also watch the
        // drive status directly. Either source stamps the timestamp used by the
        // trigger policy to detect tape insertion (no-media → ready transition).
        var isNoMedia = (flags is { } f && f.HasFlag(TapeGstatFlags.DoorOpen))
                     || driveStatus == AgentTapeDeviceStatus.NoMedia;
        if (isNoMedia)
            _lastNoMediaAt[stableKey] = DateTimeOffset.UtcNow;

        _lastNoMediaAt.TryGetValue(stableKey, out var lastNoMediaAt);
        var activeOp = operationState.GetActiveOperationTypeByStableKey(stableKey);
        var isRewindActive = operationState.IsRewindActiveByStableKey(stableKey);

        var computed = Compute(
            driveStatus,
            flags,
            activeOp,
            device.LastPreflightAt,
            device.PreflightError,
            device.DetectedBlockBufferSizeBytes,
            lastNoMediaAt == default ? null : lastNoMediaAt,
            isRewindActive);

        var changed = deviceStore.UpdateMediaStatus(stableKey, computed);

        var shouldStart = forcePreflight
            ? CanStartPreflight(driveStatus, flags, activeOp, device)
            : ShouldTriggerPreflight(driveStatus, flags, activeOp, device, lastNoMediaAt);

        if (shouldStart)
        {
            logger.LogInformation(
                "Triggering preflight for device {Key} (forced={Forced}, lastPreflightAt={LastPreflight}, lastNoMediaAt={LastNoMedia}).",
                stableKey, forcePreflight, device.LastPreflightAt, lastNoMediaAt == default ? null : (DateTimeOffset?)lastNoMediaAt);
            // Refresh (forcePreflight) rewinds before start: the tape may be anywhere.
            // Auto-trigger leaves the decision to the configured default (tape is usually at BOT on first sighting).
            trigger.Start(hub, device, rewindBeforeStart: forcePreflight);
        }

        return changed;
    }

    /// <summary>Drops the no-media tracker entry for a device that no longer exists.</summary>
    public void ForgetDevice(string stableDeviceKey)
    {
        _lastNoMediaAt.TryRemove(stableDeviceKey, out _);
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
        int? detectedBlockBufferSizeBytes = null,
        DateTimeOffset? lastNoMediaAt = null,
        bool isRewindActive = false)
    {
        // 1. DR_OPEN (or the equivalent NoMedia drive status) dominates everything else —
        //    no cartridge means no lifecycle.
        if (flags is { } f && f.HasFlag(TapeGstatFlags.DoorOpen))
            return TapeMediaStatus.NoMedia;
        if (driveStatus == AgentTapeDeviceStatus.NoMedia)
            return TapeMediaStatus.NoMedia;

        // 2. An active operation projects directly onto the media-lifecycle state.
        //    When the runner has flagged a rewind sub-step (e.g. the rewind that wraps a
        //    Preflight or a Read), surface Rewinding regardless of the wrapping op type —
        //    the operator cares about what the cartridge is *doing*, not which command
        //    triggered it.
        if (activeOperationType is not null)
        {
            if (isRewindActive)
                return TapeMediaStatus.Rewinding;

            return activeOperationType switch
            {
                TapeOperationTypes.Preflight => TapeMediaStatus.Identifying,
                TapeOperationTypes.Read      => TapeMediaStatus.Reading,
                TapeOperationTypes.Clone     => TapeMediaStatus.Reading,
                TapeOperationTypes.Rewind    => TapeMediaStatus.Rewinding,
                TapeOperationTypes.Space     => TapeMediaStatus.FastForwarding,
                TapeOperationTypes.Eject     => TapeMediaStatus.Ejecting,
                _                            => TapeMediaStatus.Unknown,
            };
        }

        // 3. Drive must be online & ready for any cartridge-aware state.
        if (driveStatus != AgentTapeDeviceStatus.Ready)
            return TapeMediaStatus.Unknown;

        // 4. Preflight history: a result is "current" iff no no-media observation happened since.
        var preflightIsCurrent = lastPreflightAt is not null
                                 && (lastNoMediaAt is null || lastNoMediaAt <= lastPreflightAt);

        if (preflightIsCurrent && preflightError is not null)
            return TapeMediaStatus.Error;
        if (preflightIsCurrent)
            return detectedBlockBufferSizeBytes switch
            {
                > 0 => TapeMediaStatus.Ready,
                0   => TapeMediaStatus.Empty,
                _   => TapeMediaStatus.Identifying,  // ambiguous result — preflight will re-run
            };

        // 5. Cartridge present but not (or no longer) identified — preflight will fire
        //    on the same tick (first sighting or tape re-inserted after no-media).
        return TapeMediaStatus.Identifying;
    }

    private bool ShouldTriggerPreflight(
        AgentTapeDeviceStatus driveStatus,
        TapeGstatFlags? flags,
        string? activeOp,
        AgentTapeDeviceDto device,
        DateTimeOffset lastNoMediaAt)
    {
        if (!CanStartPreflight(driveStatus, flags, activeOp, device)) return false;

        // Fire when:
        //  a) the device has never been identified in this agent process (first sighting), OR
        //  b) no-media was observed after the last preflight — the tape was physically absent
        //     (ejected or never present) and has now come back, so we must re-identify.
        //     This is the only signal used: transport ops (rewind / FF) do NOT trigger
        //     re-identification because no no-media observation occurs between them.
        if (device.LastPreflightAt is null) return true;
        if (lastNoMediaAt != default && lastNoMediaAt > device.LastPreflightAt) return true;

        return false;
    }

    /// <summary>
    /// Checks whether the drive is currently in a state where preflight CAN be started
    /// (ready, idle, has a path, and preflight is enabled). Used by both the automatic
    /// trigger policy and the forced Refresh path.
    /// </summary>
    private bool CanStartPreflight(
        AgentTapeDeviceStatus driveStatus,
        TapeGstatFlags? flags,
        string? activeOp,
        AgentTapeDeviceDto device)
    {
        if (!options.Value.Enabled) return false;
        if (driveStatus != AgentTapeDeviceStatus.Ready) return false;
        if (flags is { } f && f.HasFlag(TapeGstatFlags.DoorOpen)) return false;
        if (activeOp is not null) return false;
        if (string.IsNullOrWhiteSpace(device.NonRewindingDevicePath ?? device.LinuxDevicePath)) return false;
        return true;
    }
}
