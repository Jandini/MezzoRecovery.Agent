using System.Collections.Concurrent;
using MezzoRecovery.Agent.Contracts;

namespace MezzoRecovery.Agent.TapeJobs;

public sealed class AgentJobStateStore
{
    private readonly ConcurrentDictionary<Guid, RunningJobState> _jobs = new();

    public bool TryRegister(RunningJobState state) => _jobs.TryAdd(state.JobId, state);

    public RunningJobState? Get(Guid jobId) =>
        _jobs.TryGetValue(jobId, out var state) ? state : null;

    public bool RequestCancel(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var state))
            return false;

        state.Cancel();
        return true;
    }

    public void Remove(Guid jobId) => _jobs.TryRemove(jobId, out _);

    public TapeJobStatusSnapshotMessage[] BuildSnapshots() =>
        _jobs.Values
            .Select(s => new TapeJobStatusSnapshotMessage(
                s.JobId,
                s.TapeDeviceId,
                s.StableDeviceKey,
                s.IsRunning,
                s.LastStats))
            .ToArray();

    public sealed class RunningJobState(
        Guid jobId,
        Guid tapeDeviceId,
        string stableDeviceKey,
        CancellationTokenSource linkedCts)
    {
        public Guid JobId { get; } = jobId;
        public Guid TapeDeviceId { get; } = tapeDeviceId;
        public string StableDeviceKey { get; } = stableDeviceKey;
        public CancellationTokenSource LinkedCts { get; } = linkedCts;
        public CancellationToken Token => LinkedCts.Token;
        public volatile bool IsRunning = true;
        public TapeJobProgressMessage? LastStats;

        public void Cancel()
        {
            if (!LinkedCts.IsCancellationRequested)
                LinkedCts.Cancel();
        }
    }
}
