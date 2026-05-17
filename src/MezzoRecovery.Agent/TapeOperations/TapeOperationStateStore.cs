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

    public bool RequestStop(Guid tapeDeviceId)
    {
        if (!_byDevice.TryGetValue(tapeDeviceId, out var op))
            return false;

        op.RequestStop();
        return true;
    }

    public void Remove(Guid tapeDeviceId) => _byDevice.TryRemove(tapeDeviceId, out _);

    public ActiveOperationSnapshot[] BuildSnapshots() =>
        _byDevice.Values
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
        string operationType,
        Guid requestedByUserId,
        DateTimeOffset startedAt,
        int blockSizeBytes,
        int bufferSizeBytes,
        CancellationTokenSource cts)
    {
        public Guid TapeDeviceId { get; } = tapeDeviceId;
        public string OperationType { get; } = operationType;
        public Guid RequestedByUserId { get; } = requestedByUserId;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public int BlockSizeBytes { get; } = blockSizeBytes;
        public int BufferSizeBytes { get; } = bufferSizeBytes;
        public CancellationTokenSource Cts { get; } = cts;
        public CancellationToken Token => Cts.Token;

        // Last progress numbers -- updated on every progress tick; used for the
        // reconnect snapshot and to compose final stats if a stop arrives early.
        public long LastBytesRead;
        public long LastBlocksRead;
        public long LastFilemarksRead;
        public double LastThroughputMbps;
        public double LastThroughputGbph;
        public long LastElapsedSeconds;
        public DateTimeOffset? LastProgressAt;

        public void RequestStop()
        {
            if (!Cts.IsCancellationRequested)
                Cts.Cancel();
        }
    }
}
