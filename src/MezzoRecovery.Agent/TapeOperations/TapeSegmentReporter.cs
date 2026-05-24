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
    private readonly Guid _tapeId;
    private readonly Guid _tapeDeviceId;
    private readonly ILogger _logger;

    private Guid? _segmentId;
    private int   _segmentNumber;
    private DateTimeOffset _segmentStartedAt;
    private long _segmentStartBytes;
    private long _segmentStartBlocks;

    public TapeSegmentReporter(Guid tapeId, Guid tapeDeviceId, ILogger logger)
    {
        _tapeId       = tapeId;
        _tapeDeviceId = tapeDeviceId;
        _logger       = logger;
    }

    /// <summary>
    /// Process a progress tick. Emits Created when entering a new file, Progress on
    /// each tick, and Completed for the prior file when crossing a file mark.
    /// Fire-and-forget over the hub — never blocks the read loop.
    /// </summary>
    public void OnProgress(HubConnection hub, TapeCloneStats stats, DateTimeOffset now)
    {
        int file = Math.Max(1, stats.CurrentFileNumber);
        long bytesInFile  = (long)Math.Min(stats.BytesInCurrentFile,  long.MaxValue);
        long blocksInFile = (long)Math.Min(stats.BlocksInCurrentFile, long.MaxValue);
        long totalBytes   = (long)Math.Min(stats.BytesProcessed,      long.MaxValue);
        long totalBlocks  = (long)Math.Min(stats.BlocksProcessed,     long.MaxValue);

        // File transition: close out the previous segment before opening the next.
        if (_segmentId is not null && file != _segmentNumber)
            CompleteCurrent(hub, now,
                bytesAtBoundary: totalBytes - _segmentStartBytes,
                blocksAtBoundary: totalBlocks - _segmentStartBlocks);

        if (_segmentId is null && (bytesInFile > 0 || blocksInFile > 0))
            StartNew(hub, file, totalBytes, totalBlocks, now);

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

    /// <summary>Mark the currently-open segment as ReadCompleted (end-of-tape).</summary>
    public void OnReadFinished(HubConnection hub, TapeCloneStats finalStats, DateTimeOffset now)
    {
        if (_segmentId is null) return;
        long bytes  = (long)Math.Min(finalStats.BytesInCurrentFile,  long.MaxValue);
        long blocks = (long)Math.Min(finalStats.BlocksInCurrentFile, long.MaxValue);
        CompleteCurrent(hub, now, bytes, blocks);
    }

    /// <summary>Report an error against the currently-open segment, if any.</summary>
    public void OnReadFailed(HubConnection hub, string errorMessage, DateTimeOffset now)
    {
        if (_segmentId is null) return;
        _ = SendAsync(hub, "ReportTapeSegmentReadFailed",
            new ReportTapeSegmentReadFailedMessage(
                _segmentId.Value, _tapeId, _segmentNumber, errorMessage, now));
        _segmentId = null;
    }

    private void StartNew(HubConnection hub, int file, long totalBytes, long totalBlocks, DateTimeOffset now)
    {
        _segmentId          = Guid.NewGuid();
        _segmentNumber      = file;
        _segmentStartedAt   = now;
        _segmentStartBytes  = totalBytes;
        _segmentStartBlocks = totalBlocks;

        _ = SendAsync(hub, "ReportTapeSegmentCreated",
            new ReportTapeSegmentCreatedMessage(
                _segmentId.Value, _tapeId, _tapeDeviceId, _segmentNumber, now));
    }

    private void CompleteCurrent(HubConnection hub, DateTimeOffset now, long bytesAtBoundary, long blocksAtBoundary)
    {
        long avg = SegmentAverage(now, bytesAtBoundary);
        _ = SendAsync(hub, "ReportTapeSegmentReadCompleted",
            new ReportTapeSegmentReadCompletedMessage(
                _segmentId!.Value, _tapeId, _segmentNumber,
                SizeBytes: Math.Max(0, bytesAtBoundary),
                BlockCount: Math.Max(0, blocksAtBoundary),
                AverageThroughputBytesPerSecond: avg,
                CompletedAt: now));
        _segmentId = null;
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
