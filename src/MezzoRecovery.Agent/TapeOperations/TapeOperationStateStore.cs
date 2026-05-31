using System.Collections.Concurrent;
using MezzoRecovery.Agent.Contracts;

namespace MezzoRecovery.Agent.TapeOperations;

/// <summary>
/// Per-device live state. Key is the device id, never an operation id -- the UI never sees an op id.
/// </summary>
public sealed class TapeOperationStateStore
{
    private readonly ConcurrentDictionary<Guid, RunningOperation> _byDevice = new();

    public bool TryRegister(RunningOperation op) => _byDevice.TryAdd(op.TapeDeviceId, op);

    public RunningOperation? Get(Guid tapeDeviceId) =>
        _byDevice.TryGetValue(tapeDeviceId, out var op) ? op : null;

    public bool IsDeviceBusyByStableKey(string stableDeviceKey)
    {
        foreach (var op in _byDevice.Values)
            if (string.Equals(op.StableDeviceKey, stableDeviceKey, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    /// Returns the active operation type for the given stable key, or null when the device
    /// is idle. Used by <c>TapeMediaLoader</c> to project the current operation into the
    /// derived <c>TapeMediaStatus</c>.
    /// </summary>
    public string? GetActiveOperationTypeByStableKey(string stableDeviceKey)
    {
        foreach (var op in _byDevice.Values)
            if (string.Equals(op.StableDeviceKey, stableDeviceKey, StringComparison.Ordinal))
                return op.OperationType;
        return null;
    }

    /// <summary>
    /// True when the active operation for <paramref name="stableDeviceKey"/> is currently
    /// in a rewind sub-step (set by the runner around <c>TryRewind</c> calls inside
    /// Preflight or Read). Lets <c>TapeMediaLoader</c> surface <c>TapeMediaStatus.Rewinding</c>
    /// even though the wrapping operation is Preflight/Read.
    /// </summary>
    public bool IsRewindActiveByStableKey(string stableDeviceKey)
    {
        foreach (var op in _byDevice.Values)
            if (string.Equals(op.StableDeviceKey, stableDeviceKey, StringComparison.Ordinal))
                return op.IsRewindActive;
        return false;
    }

    /// <summary>
    /// Flips the active operation's rewind sub-phase flag. Returns true when the flag
    /// actually changed value (so the caller can avoid republishing on no-op writes).
    /// </summary>
    public bool SetRewindActiveByStableKey(string stableDeviceKey, bool isRewindActive)
    {
        foreach (var op in _byDevice.Values)
        {
            if (string.Equals(op.StableDeviceKey, stableDeviceKey, StringComparison.Ordinal))
            {
                if (op.IsRewindActive == isRewindActive) return false;
                op.IsRewindActive = isRewindActive;
                return true;
            }
        }
        return false;
    }

    public IReadOnlySet<string> SnapshotBusyStableKeys()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in _byDevice.Values)
            if (!string.IsNullOrEmpty(op.StableDeviceKey))
                set.Add(op.StableDeviceKey);
        return set;
    }

    public bool RequestStop(Guid tapeDeviceId)
    {
        if (!_byDevice.TryGetValue(tapeDeviceId, out var op))
            return false;

        op.RequestStop();
        return true;
    }

    public bool RequestStopReading(Guid tapeDeviceId)
    {
        if (!_byDevice.TryGetValue(tapeDeviceId, out var op))
            return false;

        op.RequestStopReading();
        return true;
    }

    public void Remove(Guid tapeDeviceId) => _byDevice.TryRemove(tapeDeviceId, out _);

    public ActiveOperationSnapshot[] BuildSnapshots() =>
        _byDevice.Values
            // Preflight is agent-initiated and uses a synthetic TapeDeviceId Guid.
            // The API never issued it and has no record of the Guid, so omit it from the
            // wire snapshot. The UI learns about preflight via TapeMediaStatus.Identifying instead.
            .Where(s => !string.Equals(s.OperationType, TapeOperationTypes.Preflight, StringComparison.Ordinal))
            .Select(s => new ActiveOperationSnapshot(
                s.TapeDeviceId,
                s.OperationType,
                s.RequestedByUserId,
                s.StartedAt,
                s.LastBytesRead,
                s.LastBlocksRead,
                s.LastFilemarksRead,
                s.LastThroughputMbps,
                s.LastThroughputGbph,
                s.LastElapsedSeconds,
                s.BlockSizeBytes,
                s.BufferSizeBytes,
                s.LastProgressAt))
            .ToArray();

    public sealed class RunningOperation(
        Guid tapeDeviceId,
        string stableDeviceKey,
        string operationType,
        Guid requestedByUserId,
        DateTimeOffset startedAt,
        int blockSizeBytes,
        int bufferSizeBytes,
        CancellationTokenSource cts)
    {
        public Guid TapeDeviceId { get; } = tapeDeviceId;
        public string StableDeviceKey { get; } = stableDeviceKey;
        public string OperationType { get; } = operationType;
        public Guid RequestedByUserId { get; } = requestedByUserId;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public int BlockSizeBytes { get; } = blockSizeBytes;
        public int BufferSizeBytes { get; } = bufferSizeBytes;
        public CancellationTokenSource Cts { get; } = cts;
        public CancellationToken Token => Cts.Token;

        // Linked to Cts — cancelling Cts also cancels this one.
        // Cancelled independently by StopTapeRunReading (stop reading, continue uploads).
        public CancellationTokenSource ReadingCts { get; } =
            CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        public CancellationToken ReadingToken => ReadingCts.Token;

        // Last progress numbers -- updated on every progress tick; used for the
        // reconnect snapshot and to compose final stats if a stop arrives early.
        public long LastBytesRead;
        public long LastBlocksRead;
        public long LastFilemarksRead;
        public double LastThroughputMbps;
        public double LastThroughputGbph;
        public long LastElapsedSeconds;
        public DateTimeOffset? LastProgressAt;

        // Set by the runner while the underlying service is in a rewind sub-step.
        // volatile because flips happen on a worker thread but are read from the
        // status poller thread via TapeMediaLoader.Observe.
        public volatile bool IsRewindActive;

        public void RequestStop()
        {
            if (!Cts.IsCancellationRequested)
                Cts.Cancel();
        }

        public void RequestStopReading()
        {
            if (!ReadingCts.IsCancellationRequested)
                ReadingCts.Cancel();
        }
    }
}
