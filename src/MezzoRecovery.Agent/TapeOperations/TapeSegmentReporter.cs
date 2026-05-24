using System.Threading.Channels;
using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.Tape.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.TapeOperations;

/// <summary>
/// Tracks per-file segment boundaries during a tape read and pushes segment lifecycle
/// events to the API over AgentHub. Stateful per read run — instantiate one per read,
/// dispose when the read is complete.
///
/// Mapping: a tape "file" (delimited by file marks) is one segment. SegmentNumber is
/// the 1-based CurrentFileNumber reported by TapeVerifyService.
///
/// Design: all hub work is enqueued into an unbounded channel and consumed by a single
/// dedicated background task.  This means:
///   - The progress callback (hot path, read loop) is never blocked by hub latency.
///   - Hub messages are sent strictly in enqueue order — ReportTapeSegmentCreated is
///     always processed by the API before ReportTapeSegmentReadCompleted for the same
///     segment, eliminating the "not found" race that left segments at ReadStatus=0.
///   - All segment state (_segmentId etc.) is accessed only on the consumer task, so
///     no additional synchronisation is needed.
/// </summary>
public sealed class TapeSegmentReporter : IAsyncDisposable
{
    private readonly Guid  _tapeId;
    private readonly Guid  _tapeDeviceId;
    private readonly Guid? _tapeJobId;
    private readonly ILogger _logger;

    // Unbounded so critical terminal messages (Completed / Failed) are never dropped.
    // In practice the queue is near-empty: one progress tick every N seconds, each
    // processed in < 1 ms by the consumer.
    private readonly Channel<Func<Task>> _queue =
        Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly Task _consumer;

    // -----------------------------------------------------------------
    // All fields below are touched ONLY from within consumer-dispatched
    // lambdas — no locking required.
    // -----------------------------------------------------------------
    private Guid? _segmentId;
    private int   _segmentNumber;
    private DateTimeOffset _segmentStartedAt;
    private long _segmentStartBytes;
    private long _segmentStartBlocks;

    public TapeSegmentReporter(Guid tapeId, Guid tapeDeviceId, ILogger logger, Guid? tapeJobId = null)
    {
        _tapeId       = tapeId;
        _tapeDeviceId = tapeDeviceId;
        _tapeJobId    = tapeJobId;
        _logger       = logger;
        _consumer     = ConsumeAsync();
    }

    // ── Public API ────────────────────────────────────────────────────

    /// <summary>
    /// Process a progress tick. Enqueues work items for the consumer — never blocks
    /// the calling thread regardless of hub latency.
    /// </summary>
    public void OnProgress(HubConnection hub, TapeCloneStats stats, DateTimeOffset now)
    {
        // Capture all values from the stats struct before enqueueing so the lambda
        // closes over snapshot data, not a potentially mutated reference.
        int  file         = Math.Max(1, stats.CurrentFileNumber);
        long bytesInFile  = (long)Math.Min(stats.BytesInCurrentFile,  long.MaxValue);
        long blocksInFile = (long)Math.Min(stats.BlocksInCurrentFile, long.MaxValue);
        long totalBytes   = (long)Math.Min(stats.BytesProcessed,      long.MaxValue);
        long totalBlocks  = (long)Math.Min(stats.BlocksProcessed,     long.MaxValue);

        _queue.Writer.TryWrite(() =>
            ProcessProgressAsync(hub, file, bytesInFile, blocksInFile, totalBytes, totalBlocks, now));
    }

    /// <summary>
    /// Enqueues a Completed message for the currently-open segment and waits for it
    /// to be sent. Called after the tape read finishes (end-of-tape).
    /// </summary>
    public Task OnReadFinishedAsync(HubConnection hub, TapeCloneStats finalStats, DateTimeOffset now)
    {
        long bytes  = (long)Math.Min(finalStats.BytesInCurrentFile,  long.MaxValue);
        long blocks = (long)Math.Min(finalStats.BlocksInCurrentFile, long.MaxValue);
        return EnqueueAndWaitAsync(() => ProcessReadFinishedAsync(hub, bytes, blocks, now));
    }

    /// <summary>
    /// Enqueues a Failed message for the currently-open segment and waits for it to
    /// be sent. Called when the tape read terminates with an error.
    /// </summary>
    public Task OnReadFailedAsync(HubConnection hub, string errorMessage, DateTimeOffset now) =>
        EnqueueAndWaitAsync(() => ProcessReadFailedAsync(hub, errorMessage, now));

    // ── IAsyncDisposable ──────────────────────────────────────────────

    /// <summary>
    /// Signals the consumer to drain remaining items and stop, then awaits its exit.
    /// Call this after OnReadFinishedAsync / OnReadFailedAsync has returned so that
    /// all hub messages have been sent before the connection is released.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _consumer;
    }

    // ── Consumer ──────────────────────────────────────────────────────

    private async Task ConsumeAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Segment reporter work item threw unexpectedly.");
            }
        }
    }

    // ── Work items (run exclusively on the consumer task) ─────────────

    private async Task ProcessProgressAsync(
        HubConnection hub,
        int file, long bytesInFile, long blocksInFile,
        long totalBytes, long totalBlocks,
        DateTimeOffset now)
    {
        // File transition: complete the outgoing segment before opening the next.
        if (_segmentId is not null && file != _segmentNumber)
            await CompleteCurrentAsync(hub, now,
                bytesAtBoundary:  totalBytes  - _segmentStartBytes,
                blocksAtBoundary: totalBlocks - _segmentStartBlocks);

        // Start a new segment on first data block of a new file.
        if (_segmentId is null && (bytesInFile > 0 || blocksInFile > 0))
            await StartNewAsync(hub, file, totalBytes, totalBlocks, now);

        if (_segmentId is null)
            return; // still waiting for the first data block

        long avg = SegmentAverage(now, bytesInFile);
        await SendAsync(hub, "ReportTapeSegmentReadProgress",
            new ReportTapeSegmentReadProgressMessage(
                _segmentId.Value,
                _tapeId,
                _segmentNumber,
                CurrentBlock: blocksInFile,
                CurrentFile:  file,
                SizeBytes:    bytesInFile,
                BlockCount:   blocksInFile,
                AverageThroughputBytesPerSecond: avg,
                ReportedAt:   now));
    }

    private async Task ProcessReadFinishedAsync(HubConnection hub, long bytes, long blocks, DateTimeOffset now)
    {
        if (_segmentId is null) return;
        await CompleteCurrentAsync(hub, now, bytes, blocks);
    }

    private async Task ProcessReadFailedAsync(HubConnection hub, string errorMessage, DateTimeOffset now)
    {
        if (_segmentId is null) return;
        var id = _segmentId.Value;
        _segmentId = null;
        await SendAsync(hub, "ReportTapeSegmentReadFailed",
            new ReportTapeSegmentReadFailedMessage(
                id, _tapeId, _segmentNumber, errorMessage, now));
    }

    // ── Segment lifecycle helpers ─────────────────────────────────────

    private async Task StartNewAsync(HubConnection hub, int file, long totalBytes, long totalBlocks, DateTimeOffset now)
    {
        var id = Guid.NewGuid();
        _segmentNumber      = file;
        _segmentStartedAt   = now;
        _segmentStartBytes  = totalBytes;
        _segmentStartBlocks = totalBlocks;

        await SendAsync(hub, "ReportTapeSegmentCreated",
            new ReportTapeSegmentCreatedMessage(
                id, _tapeId, _tapeDeviceId, file, now, TapeJobId: _tapeJobId));

        // Assign the ID only after the server has acknowledged creation.
        // Because the consumer is a single task, CompleteCurrentAsync cannot
        // run for this segment until StartNewAsync returns and _segmentId is set —
        // Completed is guaranteed to arrive after Created on the server.
        _segmentId = id;
    }

    private async Task CompleteCurrentAsync(HubConnection hub, DateTimeOffset now,
        long bytesAtBoundary, long blocksAtBoundary)
    {
        // Clear _segmentId before the hub call so that if the send fails and the
        // consumer moves on, the next progress tick starts fresh rather than trying
        // to re-complete a segment the server may not have received.
        var id = _segmentId!.Value;
        _segmentId = null;

        long avg = SegmentAverage(now, bytesAtBoundary);
        await SendAsync(hub, "ReportTapeSegmentReadCompleted",
            new ReportTapeSegmentReadCompletedMessage(
                id, _tapeId, _segmentNumber,
                SizeBytes: Math.Max(0, bytesAtBoundary),
                BlockCount: Math.Max(0, blocksAtBoundary),
                AverageThroughputBytesPerSecond: avg,
                CompletedAt: now));
    }

    // ── Utilities ─────────────────────────────────────────────────────

    /// <summary>
    /// Enqueues a work item and returns a Task that completes when the consumer
    /// finishes executing it. Used for terminal operations that the caller must
    /// await before declaring the segment done.
    /// </summary>
    private Task EnqueueAndWaitAsync(Func<Task> work)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Writer.TryWrite(async () =>
        {
            try   { await work(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Segment terminal work item failed."); }
            finally { tcs.TrySetResult(); }
        });
        return tcs.Task;
    }

    private long SegmentAverage(DateTimeOffset now, long bytes)
    {
        var elapsed = (now - _segmentStartedAt).TotalSeconds;
        return elapsed > 0 && bytes > 0 ? (long)(bytes / elapsed) : 0L;
    }

    private async Task SendAsync<T>(HubConnection hub, string method, T payload)
    {
        try
        {
            await hub.InvokeAsync(method, payload);
        }
        catch (Exception ex)
        {
            // Never let segment reporting break the live read. Connectivity hiccups
            // resolve themselves on the next progress tick.
            _logger.LogWarning(ex, "Segment hub call {Method} failed.", method);
        }
    }
}
