using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.Tape.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.TapeOperations;

/// <summary>
/// Tracks per-file segment boundaries during a tape read and pushes segment lifecycle
/// events to the API over AgentHub. Stateful per read run — instantiate one per read.
///
/// Mapping: a tape "file" (delimited by file marks) is one segment. SegmentNumber is
/// the 1-based CurrentFileNumber reported by TapeVerifyService.
///
/// Only Read events are emitted here. Hashing and upload are wired separately when
/// the agent learns to cache + hash segment files.
/// </summary>
public sealed class TapeSegmentReporter
{
    private readonly Guid  _tapeId;
    private readonly Guid  _tapeDeviceId;
    private readonly Guid? _tapeJobId;
    private readonly ILogger _logger;

    // Serialises all hub operations so that:
    //   (a) OnProgressAsync is safe to call from concurrent SendProgressAsync tasks, and
    //   (b) ReportTapeSegmentCreated is always acknowledged before the same segment's
    //       ReportTapeSegmentReadCompleted can be sent (preventing the "not found" race).
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Set only AFTER the server has acknowledged ReportTapeSegmentCreated.
    // CompleteCurrent cannot run until _segmentId is non-null, so Completed
    // can never race ahead of Created on the API side.
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
    }

    /// <summary>
    /// Process a progress tick. Emits Created when entering a new file, Progress on
    /// each tick, and Completed for the prior file when crossing a file mark.
    /// Fire-and-forget over the hub — never blocks the read loop.
    /// </summary>
    public async Task OnProgressAsync(HubConnection hub, TapeCloneStats stats, DateTimeOffset now)
    {
        await _gate.WaitAsync();
        try
        {
            int file = Math.Max(1, stats.CurrentFileNumber);
            long bytesInFile  = (long)Math.Min(stats.BytesInCurrentFile,  long.MaxValue);
            long blocksInFile = (long)Math.Min(stats.BlocksInCurrentFile, long.MaxValue);
            long totalBytes   = (long)Math.Min(stats.BytesProcessed,      long.MaxValue);
            long totalBlocks  = (long)Math.Min(stats.BlocksProcessed,     long.MaxValue);

            // File transition: close out the previous segment before opening the next.
            if (_segmentId is not null && file != _segmentNumber)
                await CompleteCurrentAsync(hub, now,
                    bytesAtBoundary: totalBytes - _segmentStartBytes,
                    blocksAtBoundary: totalBlocks - _segmentStartBlocks);

            if (_segmentId is null && (bytesInFile > 0 || blocksInFile > 0))
                await StartNewAsync(hub, file, totalBytes, totalBlocks, now);

            if (_segmentId is null)
                return; // still waiting for the first data block of the first file

            long avg = SegmentAverage(now, bytesInFile);
            _ = SendAsync(hub, "ReportTapeSegmentReadProgress",
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
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Mark the currently-open segment as ReadCompleted (end-of-tape).</summary>
    public async Task OnReadFinishedAsync(HubConnection hub, TapeCloneStats finalStats, DateTimeOffset now)
    {
        await _gate.WaitAsync();
        try
        {
            if (_segmentId is null) return;
            long bytes  = (long)Math.Min(finalStats.BytesInCurrentFile,  long.MaxValue);
            long blocks = (long)Math.Min(finalStats.BlocksInCurrentFile, long.MaxValue);
            await CompleteCurrentAsync(hub, now, bytes, blocks);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Report an error against the currently-open segment, if any.</summary>
    public async Task OnReadFailedAsync(HubConnection hub, string errorMessage, DateTimeOffset now)
    {
        await _gate.WaitAsync();
        try
        {
            if (_segmentId is null) return;
            var id = _segmentId.Value;
            _segmentId = null;
            await SendAsync(hub, "ReportTapeSegmentReadFailed",
                new ReportTapeSegmentReadFailedMessage(
                    id, _tapeId, _segmentNumber, errorMessage, now));
        }
        finally
        {
            _gate.Release();
        }
    }

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

        // Assign the ID only after the server has acknowledged the creation.
        // CompleteCurrentAsync uses _segmentId, so it cannot send Completed
        // for this segment until Created has been fully processed by the API.
        _segmentId = id;
    }

    private async Task CompleteCurrentAsync(HubConnection hub, DateTimeOffset now, long bytesAtBoundary, long blocksAtBoundary)
    {
        // Clear _segmentId before the hub call. If the call fails, the next
        // OnProgressAsync invocation will not attempt to re-complete this segment.
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
