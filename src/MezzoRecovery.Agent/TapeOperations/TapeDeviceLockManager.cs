using System.Collections.Concurrent;

namespace MezzoRecovery.Agent.TapeOperations;

/// <summary>
/// Per-device serialisation. Exactly one operation may hold a given stable device key at a time.
/// </summary>
public sealed class TapeDeviceLockManager
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<IDisposable> AcquireAsync(string stableDeviceKey, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(stableDeviceKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Release(gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
