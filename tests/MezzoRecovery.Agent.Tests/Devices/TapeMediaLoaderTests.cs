using MezzoRecovery.Agent.Configuration;
using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.Agent.Devices;
using MezzoRecovery.Agent.TapeOperations;
using MezzoRecovery.TapeDrive.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MezzoRecovery.Agent.Tests.Devices;

public sealed class TapeMediaLoaderComputeTests
{
    [Fact]
    public void DoorOpenFlag_overrides_everything()
    {
        var status = TapeMediaLoader.Compute(
            AgentTapeDeviceStatus.Ready,
            TapeGstatFlags.DoorOpen,
            activeOperationType: TapeOperationTypes.Read,
            lastPreflightAt: DateTimeOffset.UtcNow,
            preflightError: null,
            lastNoMediaAt: DateTimeOffset.UtcNow);

        Assert.Equal(TapeMediaStatus.NoMedia, status);
    }

    [Fact]
    public void DriveNoMedia_with_no_flags_maps_to_NoMedia()
    {
        var status = TapeMediaLoader.Compute(
            AgentTapeDeviceStatus.NoMedia,
            flags: null,
            activeOperationType: null,
            lastPreflightAt: null,
            preflightError: null,
            lastNoMediaAt: null);

        Assert.Equal(TapeMediaStatus.NoMedia, status);
    }

    [Fact]
    public void Active_preflight_operation_projects_to_Identifying()
    {
        var status = TapeMediaLoader.Compute(
            AgentTapeDeviceStatus.Ready,
            TapeGstatFlags.Online,
            activeOperationType: TapeOperationTypes.Preflight,
            lastPreflightAt: null,
            preflightError: null,
            lastNoMediaAt: null);

        Assert.Equal(TapeMediaStatus.Identifying, status);
    }

    [Theory]
    [InlineData(TapeOperationTypes.Read, TapeMediaStatus.Reading)]
    [InlineData(TapeOperationTypes.Rewind, TapeMediaStatus.Rewinding)]
    [InlineData(TapeOperationTypes.Eject, TapeMediaStatus.Ejecting)]
    [InlineData(TapeOperationTypes.Eod, TapeMediaStatus.FastForwarding)]
    public void Active_operations_project_to_matching_media_status(string op, TapeMediaStatus expected)
    {
        var status = TapeMediaLoader.Compute(
            AgentTapeDeviceStatus.Ready,
            TapeGstatFlags.Online,
            activeOperationType: op,
            lastPreflightAt: DateTimeOffset.UtcNow,
            preflightError: null,
            lastNoMediaAt: null);

        Assert.Equal(expected, status);
    }

    [Fact]
    public void Active_operation_overrides_preflight_history()
    {
        var status = TapeMediaLoader.Compute(
            AgentTapeDeviceStatus.Ready,
            TapeGstatFlags.Online,
            activeOperationType: TapeOperationTypes.Read,
            lastPreflightAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            preflightError: null,
            lastNoMediaAt: null);

        Assert.Equal(TapeMediaStatus.Reading, status);
    }

    [Fact]
    public void Ready_drive_with_successful_preflight_and_no_door_open_yields_Ready()
    {
        var preflightAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        var status = TapeMediaLoader.Compute(
            AgentTapeDeviceStatus.Ready,
            TapeGstatFlags.Online,
            activeOperationType: null,
            lastPreflightAt: preflightAt,
            preflightError: null,
            detectedBlockBufferSizeBytes: 65536,
            lastNoMediaAt: null);

        Assert.Equal(TapeMediaStatus.Ready, status);
    }

    [Fact]
    public void Ready_drive_with_blank_tape_preflight_yields_Empty()
    {
        var preflightAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        // TapePreflightRunner stores 0 (not null) when no data block was read — that is
        // the confirmed-blank-tape sentinel.  Compute maps 0 → Empty.
        var status = TapeMediaLoader.Compute(
            AgentTapeDeviceStatus.Ready,
            TapeGstatFlags.Online,
            activeOperationType: null,
            lastPreflightAt: preflightAt,
            preflightError: null,
            detectedBlockBufferSizeBytes: 0,
            lastNoMediaAt: null);

        Assert.Equal(TapeMediaStatus.Empty, status);
    }

    [Fact]
    public void Ready_drive_with_null_buffer_size_and_current_preflight_yields_Identifying()
    {
        var preflightAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        // Ambiguous preflight result (null buffer, no error, no no-media since) — treated
        // as needing re-identification → Identifying.
        var status = TapeMediaLoader.Compute(
            AgentTapeDeviceStatus.Ready,
            TapeGstatFlags.Online,
            activeOperationType: null,
            lastPreflightAt: preflightAt,
            preflightError: null,
            detectedBlockBufferSizeBytes: null,
            lastNoMediaAt: null);

        Assert.Equal(TapeMediaStatus.Identifying, status);
    }

    [Fact]
    public void Ready_drive_with_preflight_error_yields_Error()
    {
        var status = TapeMediaLoader.Compute(
            AgentTapeDeviceStatus.Ready,
            TapeGstatFlags.Online,
            activeOperationType: null,
            lastPreflightAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            preflightError: "buffer exhausted",
            lastNoMediaAt: null);

        Assert.Equal(TapeMediaStatus.Error, status);
    }

    [Fact]
    public void No_media_after_preflight_marks_stale_history_and_returns_Identifying()
    {
        var preflightAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var noMediaAfter = preflightAt.AddMinutes(1);

        // no-media was observed after the last preflight → preflight history is stale
        // (tape was absent) → Identifying (re-identification will be triggered).
        var status = TapeMediaLoader.Compute(
            AgentTapeDeviceStatus.Ready,
            TapeGstatFlags.Online,
            activeOperationType: null,
            lastPreflightAt: preflightAt,
            preflightError: null,
            lastNoMediaAt: noMediaAfter);

        Assert.Equal(TapeMediaStatus.Identifying, status);
    }

    [Fact]
    public void Ready_drive_with_no_preflight_history_yields_Identifying()
    {
        var status = TapeMediaLoader.Compute(
            AgentTapeDeviceStatus.Ready,
            TapeGstatFlags.Online,
            activeOperationType: null,
            lastPreflightAt: null,
            preflightError: null,
            lastNoMediaAt: null);

        Assert.Equal(TapeMediaStatus.Identifying, status);
    }

    [Fact]
    public void Non_ready_drive_with_no_op_yields_Unknown()
    {
        var status = TapeMediaLoader.Compute(
            AgentTapeDeviceStatus.Unavailable,
            flags: null,
            activeOperationType: null,
            lastPreflightAt: null,
            preflightError: null,
            lastNoMediaAt: null);

        Assert.Equal(TapeMediaStatus.Unknown, status);
    }

    [Fact]
    public void GMT_CLN_on_ready_drive_is_ignored_falls_through_to_preflight_history()
    {
        // GMT_CLN (cleaning required) is unreliable on Linux — the flag is ignored.
        // The state machine falls through to preflight history: current preflight with
        // no error and no detected buffer size returns Identifying (preflight will re-run).
        var status = TapeMediaLoader.Compute(
            AgentTapeDeviceStatus.Ready,
            TapeGstatFlags.Online | TapeGstatFlags.CleaningRequested,
            activeOperationType: null,
            lastPreflightAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            preflightError: null,
            lastNoMediaAt: null);

        Assert.Equal(TapeMediaStatus.Identifying, status);
    }

    [Fact]
    public void GMT_CLN_does_not_override_active_operation()
    {
        var status = TapeMediaLoader.Compute(
            AgentTapeDeviceStatus.Ready,
            TapeGstatFlags.Online | TapeGstatFlags.CleaningRequested,
            activeOperationType: TapeOperationTypes.Read,
            lastPreflightAt: null,
            preflightError: null,
            lastNoMediaAt: null);

        Assert.Equal(TapeMediaStatus.Reading, status);
    }

    [Theory]
    [InlineData(TapeOperationTypes.Preflight)]
    [InlineData(TapeOperationTypes.Read)]
    public void Rewind_sub_phase_overrides_wrapping_op_type(string wrappingOp)
    {
        var status = TapeMediaLoader.Compute(
            AgentTapeDeviceStatus.Ready,
            TapeGstatFlags.Online,
            activeOperationType: wrappingOp,
            lastPreflightAt: null,
            preflightError: null,
            lastNoMediaAt: null,
            isRewindActive: true);

        Assert.Equal(TapeMediaStatus.Rewinding, status);
    }

    [Fact]
    public void Rewind_sub_phase_does_not_apply_when_no_op_is_active()
    {
        // No active op: a stale rewind flag must not flip an otherwise-idle device.
        var status = TapeMediaLoader.Compute(
            AgentTapeDeviceStatus.Ready,
            TapeGstatFlags.Online,
            activeOperationType: null,
            lastPreflightAt: DateTimeOffset.UtcNow,
            preflightError: null,
            detectedBlockBufferSizeBytes: 65536,
            lastNoMediaAt: null,
            isRewindActive: true);

        Assert.Equal(TapeMediaStatus.Ready, status);
    }
}

public sealed class TapeMediaLoaderObserveTests
{
    private sealed class CapturingTrigger : ITapePreflightTrigger
    {
        public List<string> StartedFor { get; } = [];
        public List<bool> RewindFlags { get; } = [];

        public void Start(HubConnection hub, AgentTapeDeviceDto device, bool rewindBeforeStart = false)
        {
            StartedFor.Add(device.StableDeviceKey);
            RewindFlags.Add(rewindBeforeStart);
        }
    }

    private static (TapeMediaLoader loader, CapturingTrigger trigger, AgentDeviceStateStore store, TapeOperationStateStore opState)
        BuildLoader(bool enabled = true)
    {
        var trigger = new CapturingTrigger();
        var deviceStore = new AgentDeviceStateStore();
        var opState = new TapeOperationStateStore();
        var options = Options.Create(new TapeMediaLoaderOptions { Enabled = enabled });
        var loader = new TapeMediaLoader(trigger, opState, deviceStore, options, NullLogger<TapeMediaLoader>.Instance);
        return (loader, trigger, deviceStore, opState);
    }

    private static AgentTapeDeviceDto BuildDevice(string key = "dev/nst0") => new()
    {
        StableDeviceKey = key,
        LinuxDevicePath = "/dev/st0",
        NonRewindingDevicePath = "/dev/nst0",
        Status = AgentTapeDeviceStatus.Ready,
    };

    [Fact]
    public void First_sighting_of_Ready_device_triggers_preflight()
    {
        var (loader, trigger, store, _) = BuildLoader();
        var device = BuildDevice();
        store.ReplaceAll([device]);

        loader.Observe(hub: null!, store.GetByStableKey(device.StableDeviceKey)!, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready);

        Assert.Single(trigger.StartedFor);
        Assert.Equal(device.StableDeviceKey, trigger.StartedFor[0]);
    }

    [Fact]
    public void Subsequent_Ready_sightings_without_door_open_do_not_retrigger()
    {
        var (loader, trigger, store, _) = BuildLoader();
        var device = BuildDevice();
        store.ReplaceAll([device]);
        // Simulate completed preflight.
        store.UpdatePreflightResult(device.StableDeviceKey, 32768, 65536, null, DateTimeOffset.UtcNow);

        var fresh = store.GetByStableKey(device.StableDeviceKey)!;
        loader.Observe(hub: null!, fresh, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready);
        loader.Observe(hub: null!, fresh, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready);
        loader.Observe(hub: null!, fresh, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready);

        Assert.Empty(trigger.StartedFor);
    }

    [Fact]
    public void Door_open_then_Ready_retriggers_preflight()
    {
        var (loader, trigger, store, _) = BuildLoader();
        var device = BuildDevice();
        store.ReplaceAll([device]);
        // Successful preflight in the past.
        store.UpdatePreflightResult(device.StableDeviceKey, 32768, 65536, null, DateTimeOffset.UtcNow.AddMinutes(-10));

        var fresh = store.GetByStableKey(device.StableDeviceKey)!;
        loader.Observe(hub: null!, fresh, TapeGstatFlags.DoorOpen, AgentTapeDeviceStatus.NoMedia);
        Assert.Empty(trigger.StartedFor); // DR_OPEN → no preflight

        loader.Observe(hub: null!, fresh, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready);
        Assert.Single(trigger.StartedFor);
    }

    [Fact]
    public void Failed_preflight_without_door_open_does_not_retrigger()
    {
        var (loader, trigger, store, _) = BuildLoader();
        var device = BuildDevice();
        store.ReplaceAll([device]);
        // Previous preflight failed; PreflightError set; no door-open since.
        store.UpdatePreflightResult(device.StableDeviceKey, null, null, "unreadable", DateTimeOffset.UtcNow.AddMinutes(-1));

        var fresh = store.GetByStableKey(device.StableDeviceKey)!;
        loader.Observe(hub: null!, fresh, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready);
        loader.Observe(hub: null!, fresh, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready);

        Assert.Empty(trigger.StartedFor);
    }

    [Fact]
    public void Clearing_preflight_result_stops_error_projection()
    {
        var (loader, _, store, _) = BuildLoader(enabled: false);
        var device = BuildDevice();
        store.ReplaceAll([device]);
        store.UpdatePreflightResult(device.StableDeviceKey, null, null, "unreadable", DateTimeOffset.UtcNow.AddMinutes(-1));

        loader.Observe(hub: null!, store.GetByStableKey(device.StableDeviceKey)!, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready);
        Assert.Equal(TapeMediaStatus.Error, store.GetByStableKey(device.StableDeviceKey)!.MediaStatus);

        Assert.True(store.ClearPreflightResult(device.StableDeviceKey));

        loader.Observe(hub: null!, store.GetByStableKey(device.StableDeviceKey)!, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready);
        var cleared = store.GetByStableKey(device.StableDeviceKey)!;
        // ClearPreflightResult stamps LastPreflightAt=UtcNow and wipes detected sizes.
        // No no-media was observed (loader is disabled so preflight did not run),
        // so Compute sees a current-but-ambiguous preflight → Identifying.
        Assert.Equal(TapeMediaStatus.Identifying, cleared.MediaStatus);
        Assert.NotNull(cleared.LastPreflightAt);
        Assert.Null(cleared.PreflightError);
        Assert.Null(cleared.DetectedBlockSizeBytes);
        Assert.Null(cleared.DetectedBlockBufferSizeBytes);
    }

    [Fact]
    public void Failed_preflight_clears_after_door_open_cycle()
    {
        var (loader, trigger, store, _) = BuildLoader();
        var device = BuildDevice();
        store.ReplaceAll([device]);
        store.UpdatePreflightResult(device.StableDeviceKey, null, null, "unreadable", DateTimeOffset.UtcNow.AddMinutes(-5));

        var fresh = store.GetByStableKey(device.StableDeviceKey)!;
        loader.Observe(hub: null!, fresh, TapeGstatFlags.DoorOpen, AgentTapeDeviceStatus.NoMedia);
        loader.Observe(hub: null!, fresh, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready);

        Assert.Single(trigger.StartedFor);
    }

    [Fact]
    public void Transport_operation_without_no_media_does_not_retrigger_preflight()
    {
        // After a rewind or fast-forward, no no-media is observed — the tape never left
        // the drive. Preflight must NOT re-trigger even though the tape position changed.
        var (loader, trigger, store, _) = BuildLoader();
        var device = BuildDevice();
        store.ReplaceAll([device]);
        store.UpdatePreflightResult(device.StableDeviceKey, 32768, 65536, null, DateTimeOffset.UtcNow.AddMinutes(-2));

        // Simulate status-poller ticks after a rewind: drive stays Ready, no no-media.
        var fresh = store.GetByStableKey(device.StableDeviceKey)!;
        loader.Observe(hub: null!, fresh, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready);
        loader.Observe(hub: null!, fresh, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready);
        loader.Observe(hub: null!, fresh, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready);

        Assert.Empty(trigger.StartedFor);
    }

    [Fact]
    public void NoMedia_drive_status_without_DoorOpen_flag_still_records_no_media_and_retriggers()
    {
        // Linux drives that don't set DR_OPEN still report NoMedia via drive status.
        // The loader must track this and trigger preflight when the drive returns to Ready.
        var (loader, trigger, store, _) = BuildLoader();
        var device = BuildDevice();
        store.ReplaceAll([device]);
        store.UpdatePreflightResult(device.StableDeviceKey, 32768, 65536, null, DateTimeOffset.UtcNow.AddMinutes(-10));

        var fresh = store.GetByStableKey(device.StableDeviceKey)!;
        // NoMedia without DR_OPEN flag (drive status only).
        loader.Observe(hub: null!, fresh, flags: null, AgentTapeDeviceStatus.NoMedia);
        Assert.Empty(trigger.StartedFor);

        loader.Observe(hub: null!, fresh, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready);
        Assert.Single(trigger.StartedFor);
    }

    [Fact]
    public void Busy_device_is_not_preflighted()
    {
        var (loader, trigger, store, opState) = BuildLoader();
        var device = BuildDevice();
        store.ReplaceAll([device]);

        // Register an unrelated active operation on this device.
        opState.TryRegister(new TapeOperationStateStore.RunningOperation(
            tapeDeviceId: Guid.NewGuid(),
            stableDeviceKey: device.StableDeviceKey,
            operationType: TapeOperationTypes.Read,
            requestedByUserId: Guid.Empty,
            startedAt: DateTimeOffset.UtcNow,
            blockSizeBytes: 0,
            bufferSizeBytes: 0,
            cts: new CancellationTokenSource()));

        loader.Observe(hub: null!, store.GetByStableKey(device.StableDeviceKey)!, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready);

        Assert.Empty(trigger.StartedFor);
    }

    [Fact]
    public void Disabled_loader_does_not_trigger()
    {
        var (loader, trigger, store, _) = BuildLoader(enabled: false);
        var device = BuildDevice();
        store.ReplaceAll([device]);

        loader.Observe(hub: null!, store.GetByStableKey(device.StableDeviceKey)!, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready);

        Assert.Empty(trigger.StartedFor);
    }

    [Fact]
    public void ForcePreflight_triggers_even_when_preflight_history_is_present()
    {
        var (loader, trigger, store, _) = BuildLoader();
        var device = BuildDevice();
        store.ReplaceAll([device]);
        // Simulate a recent successful preflight so the normal auto-trigger would be suppressed.
        store.UpdatePreflightResult(device.StableDeviceKey, 32768, 65536, null, DateTimeOffset.UtcNow.AddMinutes(-1));

        var fresh = store.GetByStableKey(device.StableDeviceKey)!;
        loader.Observe(hub: null!, fresh, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready, forcePreflight: true);

        Assert.Single(trigger.StartedFor);
        Assert.Equal(device.StableDeviceKey, trigger.StartedFor[0]);
    }

    [Fact]
    public void ForcePreflight_is_suppressed_when_device_is_busy()
    {
        var (loader, trigger, store, opState) = BuildLoader();
        var device = BuildDevice();
        store.ReplaceAll([device]);

        opState.TryRegister(new TapeOperationStateStore.RunningOperation(
            tapeDeviceId: Guid.NewGuid(),
            stableDeviceKey: device.StableDeviceKey,
            operationType: TapeOperationTypes.Read,
            requestedByUserId: Guid.Empty,
            startedAt: DateTimeOffset.UtcNow,
            blockSizeBytes: 0,
            bufferSizeBytes: 0,
            cts: new CancellationTokenSource()));

        var fresh = store.GetByStableKey(device.StableDeviceKey)!;
        loader.Observe(hub: null!, fresh, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready, forcePreflight: true);

        Assert.Empty(trigger.StartedFor);
    }

    [Fact]
    public void ForcePreflight_passes_rewindBeforeStart_true_to_trigger()
    {
        var (loader, trigger, store, _) = BuildLoader();
        var device = BuildDevice();
        store.ReplaceAll([device]);
        // History present — normal auto-trigger would be suppressed; force bypasses it.
        store.UpdatePreflightResult(device.StableDeviceKey, 32768, 65536, null, DateTimeOffset.UtcNow.AddMinutes(-1));

        var fresh = store.GetByStableKey(device.StableDeviceKey)!;
        loader.Observe(hub: null!, fresh, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready, forcePreflight: true);

        Assert.Single(trigger.StartedFor);
        Assert.True(trigger.RewindFlags[0]);
    }

    [Fact]
    public void AutoPreflight_passes_rewindBeforeStart_false_to_trigger()
    {
        var (loader, trigger, store, _) = BuildLoader();
        var device = BuildDevice();
        store.ReplaceAll([device]);

        loader.Observe(hub: null!, store.GetByStableKey(device.StableDeviceKey)!, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready);

        Assert.Single(trigger.StartedFor);
        // Auto-trigger defers rewind decision to the runner's configured default — no force.
        Assert.False(trigger.RewindFlags[0]);
    }

    [Fact]
    public void Observe_updates_media_status_on_the_device_dto()
    {
        var (loader, _, store, _) = BuildLoader();
        var device = BuildDevice();
        store.ReplaceAll([device]);

        // First sighting → Identifying (preflight triggered on this tick)
        loader.Observe(hub: null!, store.GetByStableKey(device.StableDeviceKey)!, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready);
        Assert.Equal(TapeMediaStatus.Identifying, store.GetByStableKey(device.StableDeviceKey)!.MediaStatus);

        // Door open → NoMedia
        loader.Observe(hub: null!, store.GetByStableKey(device.StableDeviceKey)!, TapeGstatFlags.DoorOpen, AgentTapeDeviceStatus.NoMedia);
        Assert.Equal(TapeMediaStatus.NoMedia, store.GetByStableKey(device.StableDeviceKey)!.MediaStatus);
    }

    [Fact]
    public void ForgetDevice_drops_no_media_tracker()
    {
        var (loader, trigger, store, _) = BuildLoader();
        var device = BuildDevice();
        store.ReplaceAll([device]);
        store.UpdatePreflightResult(device.StableDeviceKey, 32768, 65536, null, DateTimeOffset.UtcNow);

        // No-media recorded (door open implies no media).
        loader.Observe(hub: null!, store.GetByStableKey(device.StableDeviceKey)!, TapeGstatFlags.DoorOpen, AgentTapeDeviceStatus.NoMedia);
        // Forget — no-media tracker is cleared.
        loader.ForgetDevice(device.StableDeviceKey);

        // Ready after forget: no no-media in tracker and LastPreflightAt is present
        // from the prior successful preflight → preflight should NOT re-trigger.
        loader.Observe(hub: null!, store.GetByStableKey(device.StableDeviceKey)!, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready);

        Assert.Empty(trigger.StartedFor);
    }
}
