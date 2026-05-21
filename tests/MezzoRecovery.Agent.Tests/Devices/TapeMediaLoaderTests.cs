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
            lastDoorOpenAt: DateTimeOffset.UtcNow);

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
            lastDoorOpenAt: null);

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
            lastDoorOpenAt: null);

        Assert.Equal(TapeMediaStatus.Identifying, status);
    }

    [Theory]
    [InlineData(TapeOperationTypes.Read, TapeMediaStatus.Reading)]
    [InlineData(TapeOperationTypes.Rewind, TapeMediaStatus.Rewinding)]
    [InlineData(TapeOperationTypes.Eject, TapeMediaStatus.Ejecting)]
    [InlineData(TapeOperationTypes.Space, TapeMediaStatus.FastForwarding)]
    public void Active_operations_project_to_matching_media_status(string op, TapeMediaStatus expected)
    {
        var status = TapeMediaLoader.Compute(
            AgentTapeDeviceStatus.Ready,
            TapeGstatFlags.Online,
            activeOperationType: op,
            lastPreflightAt: DateTimeOffset.UtcNow,
            preflightError: null,
            lastDoorOpenAt: null);

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
            lastDoorOpenAt: null);

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
            lastDoorOpenAt: null);

        Assert.Equal(TapeMediaStatus.Ready, status);
    }

    [Fact]
    public void Ready_drive_with_successful_preflight_and_no_buffer_size_yields_Empty()
    {
        var preflightAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        // PreflightService leaves BlockBufferSize == 0 when no data block was read before
        // the first filemark / EOM — the runner stores this as null. That's the cartridge-
        // is-blank signal Compute uses to surface TapeMediaStatus.Empty.
        var status = TapeMediaLoader.Compute(
            AgentTapeDeviceStatus.Ready,
            TapeGstatFlags.Online,
            activeOperationType: null,
            lastPreflightAt: preflightAt,
            preflightError: null,
            detectedBlockBufferSizeBytes: null,
            lastDoorOpenAt: null);

        Assert.Equal(TapeMediaStatus.Empty, status);
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
            lastDoorOpenAt: null);

        Assert.Equal(TapeMediaStatus.Error, status);
    }

    [Fact]
    public void Door_open_after_preflight_marks_stale_history_and_returns_Loaded()
    {
        var preflightAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var doorOpenedAfter = preflightAt.AddMinutes(1);

        var status = TapeMediaLoader.Compute(
            AgentTapeDeviceStatus.Ready,
            TapeGstatFlags.Online,
            activeOperationType: null,
            lastPreflightAt: preflightAt,
            preflightError: null,
            lastDoorOpenAt: doorOpenedAfter);

        Assert.Equal(TapeMediaStatus.Loaded, status);
    }

    [Fact]
    public void Ready_drive_with_no_preflight_history_yields_Loaded()
    {
        var status = TapeMediaLoader.Compute(
            AgentTapeDeviceStatus.Ready,
            TapeGstatFlags.Online,
            activeOperationType: null,
            lastPreflightAt: null,
            preflightError: null,
            lastDoorOpenAt: null);

        Assert.Equal(TapeMediaStatus.Loaded, status);
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
            lastDoorOpenAt: null);

        Assert.Equal(TapeMediaStatus.Unknown, status);
    }

    [Fact]
    public void GMT_CLN_on_ready_drive_yields_CleaningRequired()
    {
        var status = TapeMediaLoader.Compute(
            AgentTapeDeviceStatus.Ready,
            TapeGstatFlags.Online | TapeGstatFlags.CleaningRequested,
            activeOperationType: null,
            lastPreflightAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            preflightError: null,
            lastDoorOpenAt: null);

        Assert.Equal(TapeMediaStatus.CleaningRequired, status);
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
            lastDoorOpenAt: null);

        Assert.Equal(TapeMediaStatus.Reading, status);
    }
}

public sealed class TapeMediaLoaderObserveTests
{
    private sealed class CapturingTrigger : ITapePreflightTrigger
    {
        public List<string> StartedFor { get; } = [];
        public void Start(HubConnection hub, AgentTapeDeviceDto device) =>
            StartedFor.Add(device.StableDeviceKey);
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
    public void Observe_updates_media_status_on_the_device_dto()
    {
        var (loader, _, store, _) = BuildLoader();
        var device = BuildDevice();
        store.ReplaceAll([device]);

        // First sighting → Loaded (preflight history is empty until the runner finishes)
        loader.Observe(hub: null!, store.GetByStableKey(device.StableDeviceKey)!, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready);
        Assert.Equal(TapeMediaStatus.Loaded, store.GetByStableKey(device.StableDeviceKey)!.MediaStatus);

        // Door open → NoMedia
        loader.Observe(hub: null!, store.GetByStableKey(device.StableDeviceKey)!, TapeGstatFlags.DoorOpen, AgentTapeDeviceStatus.NoMedia);
        Assert.Equal(TapeMediaStatus.NoMedia, store.GetByStableKey(device.StableDeviceKey)!.MediaStatus);
    }

    [Fact]
    public void ForgetDevice_drops_door_open_tracker()
    {
        var (loader, trigger, store, _) = BuildLoader();
        var device = BuildDevice();
        store.ReplaceAll([device]);
        store.UpdatePreflightResult(device.StableDeviceKey, 32768, 65536, null, DateTimeOffset.UtcNow);

        // Door open recorded.
        loader.Observe(hub: null!, store.GetByStableKey(device.StableDeviceKey)!, TapeGstatFlags.DoorOpen, AgentTapeDeviceStatus.NoMedia);
        // Forget — tracker is reset.
        loader.ForgetDevice(device.StableDeviceKey);

        // Now Ready should not trigger preflight because LastDoorOpenAt is gone and
        // LastPreflightAt is still present from the prior successful preflight.
        loader.Observe(hub: null!, store.GetByStableKey(device.StableDeviceKey)!, TapeGstatFlags.Online, AgentTapeDeviceStatus.Ready);

        Assert.Empty(trigger.StartedFor);
    }
}
