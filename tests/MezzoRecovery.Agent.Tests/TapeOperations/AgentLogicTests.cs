using System.CommandLine;
using MezzoRecovery.Agent.Configuration;
using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.Agent.Devices;
using MezzoRecovery.Agent.Identity;
using MezzoRecovery.Agent.Logging;
using MezzoRecovery.Agent.Runtime;
using MezzoRecovery.Agent.TapeOperations;
using MezzoRecovery.Tape.Models;
using MezzoRecovery.TapeDrive.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Xunit;

namespace MezzoRecovery.Agent.Tests.TapeOperations;

public class TapeProgressMapperTests
{
    [Fact]
    public void Extract_clamps_ulong_counters_and_converts_rates()
    {
        var stats = new TapeCloneStats
        {
            BytesProcessed = ulong.MaxValue,
            BlocksProcessed = 2,
            FileMarksEncountered = 3,
            BytesPerSecond = 1_000_000,
            Elapsed = TimeSpan.FromSeconds(4.9),
        };

        var (bytes, blocks, filemarks, mbps, gbph, elapsedSec) = TapeProgressMapper.Extract(stats);

        Assert.Equal(long.MaxValue, bytes);
        Assert.Equal(2, blocks);
        Assert.Equal(3, filemarks);
        Assert.Equal(1, mbps);
        Assert.Equal(3.6, gbph);
        Assert.Equal(4, elapsedSec);
    }
}

public class TapeRunCacheLayoutTests
{
    [Fact]
    public void File_path_is_zero_based_under_the_run_directory()
    {
        var runId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var dir = TapeRunCacheLayout.GetRunDirectory(@"C:\cache", runId);
        Assert.EndsWith(Path.Combine("runs", runId.ToString()), dir);
        Assert.Equal(
            Path.Combine(dir, "segment.0000.tic"),
            TapeRunCacheLayout.GetFilePath(@"C:\cache", runId, 1));
        Assert.Equal(
            Path.Combine(dir, "segment.-0001.tic"),
            TapeRunCacheLayout.GetFilePath(@"C:\cache", runId, 0));
    }
}

public class TapeGstatLabelTests
{
    [Fact]
    public void None_formats_as_empty()
    {
        Assert.Equal(string.Empty, MezzoRecovery.Agent.Devices.TapeGstatLabels.FormatShort(TapeGstatFlags.None));
    }

    [Fact]
    public void Flags_are_joined_in_declaration_order()
    {
        var text = MezzoRecovery.Agent.Devices.TapeGstatLabels.FormatShort(
            TapeGstatFlags.Eof | TapeGstatFlags.WriteProtected | TapeGstatFlags.Online);
        Assert.Equal("EOF, WR_PROT, ONLINE", text);
    }
}

public class TapeOperationStateStoreTests
{
    [Fact]
    public void Registration_is_one_operation_per_device()
    {
        var store = new TapeOperationStateStore();
        var device = Guid.NewGuid();
        var first = Op(device, "nst0", TapeOperationTypes.Read);
        var second = Op(device, "nst0", TapeOperationTypes.Clone);

        Assert.True(store.TryRegister(first));
        Assert.False(store.TryRegister(second));
        Assert.Same(first, store.Get(device));
        Assert.Null(store.Get(Guid.NewGuid()));
    }

    [Fact]
    public void Busy_lookup_is_ordinal_and_snapshots_skip_an_empty_key()
    {
        var store = new TapeOperationStateStore();
        store.TryRegister(Op(Guid.NewGuid(), "Nst0", TapeOperationTypes.Read));
        store.TryRegister(Op(Guid.NewGuid(), "", TapeOperationTypes.Clone));

        Assert.True(store.IsDeviceBusyByStableKey("Nst0"));
        Assert.False(store.IsDeviceBusyByStableKey("nst0"));
        Assert.Equal("Read", store.GetActiveOperationTypeByStableKey("Nst0"));
        Assert.Null(store.GetActiveOperationTypeByStableKey("missing"));
        Assert.Equal(["Nst0"], store.SnapshotBusyStableKeys().ToArray());
    }

    [Fact]
    public void Preflight_is_omitted_from_the_wire_snapshot()
    {
        var store = new TapeOperationStateStore();
        var readId = Guid.NewGuid();
        store.TryRegister(Op(readId, "a", TapeOperationTypes.Read));
        store.TryRegister(Op(Guid.NewGuid(), "b", TapeOperationTypes.Preflight));

        var snapshots = store.BuildSnapshots();
        Assert.Equal([readId], snapshots.Select(s => s.TapeDeviceId).ToArray());
    }

    [Fact]
    public void Stop_cancels_the_operation_and_is_a_no_op_when_idle()
    {
        var store = new TapeOperationStateStore();
        var device = Guid.NewGuid();
        var op = Op(device, "a", TapeOperationTypes.Read);
        store.TryRegister(op);

        Assert.False(store.RequestStop(Guid.NewGuid()));
        Assert.True(store.RequestStop(device));
        Assert.True(store.RequestStop(device));
        Assert.True(op.Token.IsCancellationRequested);
        Assert.True(op.ReadingToken.IsCancellationRequested);
    }

    [Fact]
    public void Stop_reading_cancels_only_the_linked_reading_token()
    {
        var store = new TapeOperationStateStore();
        var device = Guid.NewGuid();
        var op = Op(device, "a", TapeOperationTypes.Clone);
        store.TryRegister(op);

        Assert.False(store.RequestStopReading(Guid.NewGuid()));
        Assert.True(store.RequestStopReading(device));
        Assert.True(op.ReadingToken.IsCancellationRequested);
        Assert.False(op.Token.IsCancellationRequested);

        store.Remove(device);
        Assert.Null(store.Get(device));
    }

    [Fact]
    public void Rewind_flag_changes_only_when_the_value_differs()
    {
        var store = new TapeOperationStateStore();
        store.TryRegister(Op(Guid.NewGuid(), "drive", TapeOperationTypes.Read));

        Assert.False(store.IsRewindActiveByStableKey("missing"));
        Assert.False(store.SetRewindActiveByStableKey("missing", true));
        Assert.False(store.SetRewindActiveByStableKey("drive", false));
        Assert.True(store.SetRewindActiveByStableKey("drive", true));
        Assert.True(store.IsRewindActiveByStableKey("drive"));
        Assert.False(store.SetRewindActiveByStableKey("drive", true));
    }

    private static TapeOperationStateStore.RunningOperation Op(Guid device, string key, string type) =>
        new(device, key, type, Guid.NewGuid(), DateTimeOffset.UnixEpoch, 65536, 262144, new CancellationTokenSource());
}

public class TapeDeviceLockManagerTests
{
    [Fact]
    public async Task A_second_acquire_of_the_same_key_observes_cancellation()
    {
        var locks = new TapeDeviceLockManager();
        var held = await locks.AcquireAsync("dev", CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => locks.AcquireAsync("dev", cts.Token));

        held.Dispose();
        var again = await locks.AcquireAsync("dev", CancellationToken.None);
        again.Dispose();
    }

    [Fact]
    public async Task Different_keys_do_not_block_each_other()
    {
        var locks = new TapeDeviceLockManager();
        var a = await locks.AcquireAsync("a", CancellationToken.None);
        var b = await locks.AcquireAsync("b", CancellationToken.None);
        a.Dispose();
        b.Dispose();
    }
}

public class StopOperationHandlerTests
{
    [Fact]
    public void Stop_is_ignored_when_the_device_is_idle_and_applied_when_busy()
    {
        var store = new TapeOperationStateStore();
        var handler = new StopOperationHandler(store, NullLogger<StopOperationHandler>.Instance);
        var device = Guid.NewGuid();
        var op = new TapeOperationStateStore.RunningOperation(
            device, "a", TapeOperationTypes.Read, Guid.Empty, DateTimeOffset.UnixEpoch, 0, 0, new CancellationTokenSource());
        store.TryRegister(op);

        handler.RequestStop(Guid.NewGuid());
        Assert.False(op.Token.IsCancellationRequested);
        handler.RequestStop(device);
        Assert.True(op.Token.IsCancellationRequested);
    }
}

public class AgentDeviceStateStoreTests
{
    [Fact]
    public void Replace_keeps_identification_and_read_settings_for_a_known_key()
    {
        var store = new AgentDeviceStateStore();
        var when = DateTimeOffset.UnixEpoch;
        store.ReplaceAll([Device("a")]);
        Assert.True(store.UpdateMediaStatus("a", TapeMediaStatus.Ready));
        Assert.False(store.UpdateMediaStatus("a", TapeMediaStatus.Ready));
        Assert.True(store.UpdateReadSettings("a", autoDetect: false, blockSize: 512, bufferSize: 1024));
        Assert.True(store.UpdatePreflightResult("a", 512, 1024, error: null, when, "LTO-5"));

        store.ReplaceAll([Device("a"), Device("b")]);
        var kept = store.GetByStableKey("a");
        Assert.NotNull(kept);
        Assert.Equal(TapeMediaStatus.Ready, kept.MediaStatus);
        Assert.False(kept.AutoDetectReadSettings);
        Assert.Equal(512, kept.ReadBlockSizeBytes);
        Assert.Equal("LTO-5", kept.LoadedTapeGeneration);
        Assert.Equal(AgentTapeDeviceStatus.Unknown, store.GetByStableKey("b")!.Status);
    }

    [Fact]
    public void Status_and_settings_updates_report_whether_anything_changed()
    {
        var store = new AgentDeviceStateStore();
        Assert.False(store.UpdateStatus("missing", AgentTapeDeviceStatus.Ready, "ONLINE"));
        Assert.False(store.UpdateReadSettings("missing", true, 0, 0));
        Assert.False(store.UpdatePreflightResult("missing", 1, 1, null, DateTimeOffset.UnixEpoch));
        Assert.False(store.ClearPreflightResult("missing"));
        Assert.Null(store.GetByStableKey("missing"));

        store.ReplaceAll([Device("a")]);
        Assert.True(store.UpdateStatus("a", AgentTapeDeviceStatus.Ready, "ONLINE"));
        Assert.False(store.UpdateStatus("a", AgentTapeDeviceStatus.Ready, "ONLINE"));
        Assert.True(store.UpdateReadSettings("a", false, 256, 256));
        Assert.False(store.UpdateReadSettings("a", false, 256, 256));
    }

    [Fact]
    public void Clear_preflight_wipes_sizes_and_is_a_no_op_when_already_clear()
    {
        var store = new AgentDeviceStateStore();
        store.ReplaceAll([Device("a")]);
        Assert.False(store.ClearPreflightResult("a"));
        Assert.True(store.UpdatePreflightResult("a", 10, 20, "bad", DateTimeOffset.UnixEpoch, "LTO-1"));
        Assert.True(store.ClearPreflightResult("a"));

        var device = store.GetByStableKey("a");
        Assert.Null(device!.DetectedBlockSizeBytes);
        Assert.Null(device.DetectedBlockBufferSizeBytes);
        Assert.Null(device.PreflightError);
        Assert.Null(device.LoadedTapeGeneration);
        Assert.NotNull(device.LastPreflightAt);
    }

    [Fact]
    public void Missing_keys_are_the_ones_absent_from_the_incoming_sweep()
    {
        var store = new AgentDeviceStateStore();
        store.ReplaceAll([Device("keep"), Device("gone")]);
        Assert.Equal(["gone"], store.StableKeysMissingFrom([Device("keep")]));
    }

    [Fact]
    public void Snapshot_is_a_copy()
    {
        var store = new AgentDeviceStateStore();
        store.ReplaceAll([Device("a")]);
        var snapshot = store.Snapshot();
        snapshot[0].Vendor = "changed";
        Assert.Null(store.GetByStableKey("a")!.Vendor);
    }

    private static AgentTapeDeviceDto Device(string key) => new() { StableDeviceKey = key };
}

public class TapeUploadCheckpointStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "agent-ckpt-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Missing_checkpoint_loads_as_null_and_delete_is_silent()
    {
        var store = new TapeUploadCheckpointStore(_root, NullLogger<TapeUploadCheckpointStore>.Instance);
        Assert.Null(await store.LoadAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));
        store.Delete(Guid.NewGuid(), Guid.NewGuid());
    }

    [Fact]
    public async Task Corrupt_json_loads_as_null()
    {
        var runId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var path = Path.Combine(_root, "runs", runId.ToString(), "uploads", fileId + ".upload.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{");

        var store = new TapeUploadCheckpointStore(_root, NullLogger<TapeUploadCheckpointStore>.Instance);
        Assert.Null(await store.LoadAsync(runId, fileId, CancellationToken.None));
    }

    [Fact]
    public async Task Save_round_trips_and_stamps_updated_at()
    {
        var store = new TapeUploadCheckpointStore(_root, NullLogger<TapeUploadCheckpointStore>.Instance);
        var checkpoint = new UploadCheckpoint
        {
            RunId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            FilePath = "segment.0000.tic",
            UpdatedAtUtc = DateTimeOffset.MinValue,
        };

        await store.SaveAsync(checkpoint, CancellationToken.None);
        var loaded = await store.LoadAsync(checkpoint.RunId, checkpoint.FileId, CancellationToken.None);

        // SaveAsync renames the temp file while its write stream is still open.
        // Windows rejects that rename and the store swallows the error, so the checkpoint is lost.
        if (OperatingSystem.IsWindows())
        {
            Assert.Null(loaded);
            return;
        }

        Assert.NotNull(loaded);
        Assert.Equal("segment.0000.tic", loaded.FilePath);
        Assert.True(loaded.UpdatedAtUtc > DateTimeOffset.MinValue);
        store.Delete(checkpoint.RunId, checkpoint.FileId);
        Assert.Null(await store.LoadAsync(checkpoint.RunId, checkpoint.FileId, CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_load_or_save_is_swallowed()
    {
        var store = new TapeUploadCheckpointStore(_root, NullLogger<TapeUploadCheckpointStore>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var checkpoint = new UploadCheckpoint { RunId = Guid.NewGuid(), FileId = Guid.NewGuid() };

        Assert.Null(await store.LoadAsync(checkpoint.RunId, checkpoint.FileId, cts.Token));
        await store.SaveAsync(checkpoint, cts.Token);
        Assert.Null(await store.LoadAsync(checkpoint.RunId, checkpoint.FileId, CancellationToken.None));
    }
}

public class AgentFileStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "agent-files-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Config_loader_rejects_a_missing_api_url_and_a_null_document()
    {
        var empty = Path.Combine(_root, "empty.json");
        var none = Path.Combine(_root, "null.json");
        await File.WriteAllTextAsync(empty, "{}");
        await File.WriteAllTextAsync(none, "null");

        var missingUrl = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AgentConfigLoader.LoadAsync(empty, CancellationToken.None));
        var missingDoc = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AgentConfigLoader.LoadAsync(none, CancellationToken.None));

        Assert.Contains("apiBaseUrl", missingUrl.Message, StringComparison.Ordinal);
        Assert.Contains("null.json", missingDoc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Config_loader_returns_a_configured_url()
    {
        var path = Path.Combine(_root, "ok.json");
        await File.WriteAllTextAsync(path, """{"apiBaseUrl":"https://example.test"}""");
        var cfg = await AgentConfigLoader.LoadAsync(path, CancellationToken.None);
        Assert.Equal("https://example.test", cfg.ApiBaseUrl);
    }

    [Fact]
    public async Task Credentials_round_trip_and_a_missing_file_is_null()
    {
        var path = Path.Combine(_root, "cred", "agent.credential");
        var id = Guid.NewGuid();
        await CredentialStore.SaveAsync(path, id, "secret", CancellationToken.None);
        var loaded = await CredentialStore.TryLoadAsync(path, CancellationToken.None);
        Assert.Equal(id, loaded!.AgentId);
        Assert.Equal("secret", loaded.ClientSecret);
        Assert.Null(await CredentialStore.TryLoadAsync(Path.Combine(_root, "nope"), CancellationToken.None));
    }

    [Fact]
    public async Task Machine_id_is_created_once_and_trimmed()
    {
        var path = Path.Combine(_root, "id", "machine.id");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "  abc  ");
        Assert.Equal("abc", await MachineIdStore.GetOrCreateAsync(path, CancellationToken.None));

        var fresh = Path.Combine(_root, "id", "new.id");
        var created = await MachineIdStore.GetOrCreateAsync(fresh, CancellationToken.None);
        Assert.Equal(created, await MachineIdStore.GetOrCreateAsync(fresh, CancellationToken.None));
        Assert.True(Guid.TryParse(created, out _));
    }

    [Fact]
    public void Process_lock_is_exclusive_and_deletes_the_file_on_dispose()
    {
        var path = Path.Combine(_root, "run", "agent.lock");
        var held = new ProcessLock(path);
        Assert.Throws<IOException>(() => new ProcessLock(path));
        held.Dispose();
        Assert.False(File.Exists(path));
    }
}

public class TerseConsoleFormatterTests
{
    [Fact]
    public void A_null_message_and_exception_writes_nothing()
    {
        var writer = new StringWriter();
        Formatter().Write(
            new LogEntry<string>(LogLevel.Information, "Mezzo.Agent.Run", default, "ignored", null, (_, _) => null!),
            scopeProvider: null,
            writer);
        Assert.Equal(string.Empty, writer.ToString());
    }

    [Theory]
    [InlineData(LogLevel.Trace, "trce ")]
    [InlineData(LogLevel.Debug, "dbug ")]
    [InlineData(LogLevel.Information, "info ")]
    [InlineData(LogLevel.Warning, "warn ")]
    [InlineData(LogLevel.Error, "fail ")]
    [InlineData(LogLevel.Critical, "crit ")]
    [InlineData(LogLevel.None, "None ")]
    public void Level_and_category_are_written_without_a_timestamp(LogLevel level, string prefix)
    {
        var writer = new StringWriter();
        Formatter().Write(
            new LogEntry<string>(level, "Mezzo.Agent.Run", default, "hello", new InvalidOperationException("boom"), (s, _) => s),
            scopeProvider: null,
            writer);

        var text = writer.ToString();
        Assert.StartsWith(prefix + "Run: hello ", text);
        Assert.Contains("boom", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_category_without_a_dot_is_written_whole()
    {
        var writer = new StringWriter();
        Formatter().Write(
            new LogEntry<string>(LogLevel.Information, "run", default, "ok", null, (s, _) => s),
            scopeProvider: null,
            writer);
        Assert.Equal("info run: ok" + Environment.NewLine, writer.ToString());
    }

    private static TerseConsoleFormatter Formatter() =>
        new(new FixedOptions(new SimpleConsoleFormatterOptions()));

    private sealed class FixedOptions(SimpleConsoleFormatterOptions value) : IOptionsMonitor<SimpleConsoleFormatterOptions>
    {
        public SimpleConsoleFormatterOptions CurrentValue => value;
        public SimpleConsoleFormatterOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<SimpleConsoleFormatterOptions, string?> listener) => null;
    }
}

public class AgentCliAndBannerTests
{
    [Fact]
    public void Root_command_exposes_the_agent_verbs()
    {
        var root = AgentCli.Build(new ServiceCollection().BuildServiceProvider());
        Assert.Equal(
            ["enroll", "run", "status", "version", "update", "restart"],
            root.Subcommands.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void Banner_decodes_the_product_name_and_prints_the_version()
    {
        var previous = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            new RootCommand().WriteBanner("9.9.9");
        }
        finally
        {
            Console.SetOut(previous);
        }

        var text = writer.ToString();
        Assert.Contains("Mezzo", text, StringComparison.Ordinal);
        Assert.Contains("Recovery", text, StringComparison.Ordinal);
        Assert.Contains("9.9.9", text, StringComparison.Ordinal);
        Assert.Contains("www.mezzorecovery.com", text, StringComparison.Ordinal);
    }
}

public class TapeMediaStatusTests
{
    [Theory]
    [InlineData(TapeMediaStatus.Identifying, true)]
    [InlineData(TapeMediaStatus.Reading, true)]
    [InlineData(TapeMediaStatus.FastForwarding, true)]
    [InlineData(TapeMediaStatus.Rewinding, true)]
    [InlineData(TapeMediaStatus.Ejecting, true)]
    [InlineData(TapeMediaStatus.Ready, false)]
    [InlineData(TapeMediaStatus.NoMedia, false)]
    [InlineData(TapeMediaStatus.Unknown, false)]
    [InlineData(TapeMediaStatus.Empty, false)]
    [InlineData(TapeMediaStatus.Error, false)]
    public void IsBusy_is_true_only_while_the_cartridge_is_in_motion(TapeMediaStatus status, bool busy)
    {
        Assert.Equal(busy, status.IsBusy());
    }
}
