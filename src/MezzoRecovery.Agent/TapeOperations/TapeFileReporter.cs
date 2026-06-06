using System.Threading.Channels;
using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.Tape.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.TapeOperations;

/// <summary>
/// Tracks per-file boundaries during a tape run and sends file lifecycle events to the
/// API over AgentHub. One instance per run — dispose after the run completes.
///
/// Design mirrors the old <c>TapeSegmentReporter</c>:
///   - All hub work is enqueued into an unbounded channel consumed by a single task.
///   - The progress callback (hot path) is never blocked.
///   - Hub messages are sent in strict enqueue order (Created before Completed).
///   - In Clone mode, completed files are handed off to <c>TapeFileHasher</c>.
/// </summary>
internal sealed class TapeFileReporter : IAsyncDisposable
{
    private readonly StartTapeRunCommand _command;
    private readonly Guid                _cacheRunId;
    private readonly TapeFileHasher      _hasher;
    private readonly ILogger             _logger;
    private readonly bool                _isClone;
    private readonly string              _cacheDirectory;

    private readonly Channel<Func<Task>> _queue =
        Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly Task _consumer;

    // ── Consumer-only state ───────────────────────────────────────────────────
    private Guid?         _currentFileId;
    private int           _currentFileNumber;
    private DateTimeOffset _fileStartedAt;
    private long           _fileStartBytesTotal;
    private long           _fileStartBlocksTotal;
    private long           _lastKnownTotalBytes;
    private long           _lastKnownTotalBlocks;

    public TapeFileReporter(
        StartTapeRunCommand command,
        Guid cacheRunId,
        TapeFileHasher hasher,
        ILogger logger)
    {
        _command        = command;
        _cacheRunId     = cacheRunId;
        _hasher         = hasher;
        _logger         = logger;
        _isClone        = command.RunType.Equals("Clone", StringComparison.OrdinalIgnoreCase);
        _cacheDirectory = command.CacheDirectory ?? Configuration.AgentPaths.DefaultCacheDirectory;
        _consumer       = ConsumeAsync();
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Called from the progress callback (hot path). Enqueues work; never blocks.
    /// </summary>
    public void OnProgress(HubConnection hub, TapeCloneStats stats, DateTimeOffset now)
    {
        int  file        = Math.Max(1, stats.CurrentFileNumber);
        long bytesInFile = ToLong(stats.BytesInCurrentFile);
        long blocksInFile = ToLong(stats.BlocksInCurrentFile);
        long totalBytes  = ToLong(stats.BytesProcessed);
        long totalBlocks = ToLong(stats.BlocksProcessed);

        _queue.Writer.TryWrite(() =>
            ProcessProgressAsync(hub, file, bytesInFile, blocksInFile, totalBytes, totalBlocks, now));
    }

    /// <summary>Completes the last open file at end-of-tape and awaits the send.</summary>
    public Task OnReadFinishedAsync(HubConnection hub, TapeCloneStats finalStats, DateTimeOffset now)
    {
        int  file         = Math.Max(1, finalStats.CurrentFileNumber);
        long bytesInFile  = ToLong(finalStats.BytesInCurrentFile);
        long blocksInFile = ToLong(finalStats.BlocksInCurrentFile);
        long totalBytes   = ToLong(finalStats.BytesProcessed);
        long totalBlocks  = ToLong(finalStats.BlocksProcessed);
        return EnqueueAndWaitAsync(() =>
            ProcessReadFinishedAsync(hub, file, bytesInFile, blocksInFile, totalBytes, totalBlocks, now));
    }

    public Task OnReadFailedAsync(HubConnection hub, string errorMessage, DateTimeOffset now) =>
        EnqueueAndWaitAsync(() => ProcessStoppedAsync(hub, now, errorMessage));

    public Task OnReadAbortedAsync(HubConnection hub, DateTimeOffset now) =>
        EnqueueAndWaitAsync(() => ProcessStoppedAsync(hub, now, "Tape run was cancelled."));

    // ── IAsyncDisposable ───────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _consumer;
    }

    // ── Consumer ───────────────────────────────────────────────────────────────

    private async Task ConsumeAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync())
        {
            try   { await work(); }
            catch (Exception ex) { _logger.LogWarning(ex, "TapeFileReporter work item threw."); }
        }
    }

    // ── Work items ─────────────────────────────────────────────────────────────

    private async Task ProcessProgressAsync(
        HubConnection hub,
        int  file, long bytesInFile, long blocksInFile,
        long totalBytes, long totalBlocks,
        DateTimeOffset now)
    {
        _lastKnownTotalBytes  = totalBytes;
        _lastKnownTotalBlocks = totalBlocks;

        // File transition: complete the outgoing file before opening the next.
        // At transition, bytesInFile/blocksInFile belong to the NEW file (N+1), so subtracting
        // them from the running totals gives the cumulative through the end of the old file.
        if (_currentFileId is not null && file != _currentFileNumber)
            await CompleteCurrentAsync(hub, now,
                bytesAtBoundary:  (totalBytes  - bytesInFile)  - _fileStartBytesTotal,
                blocksAtBoundary: (totalBlocks - blocksInFile) - _fileStartBlocksTotal,
                filemarkAfter: true);

        // Start a new file on the first data block.
        if (_currentFileId is null && (bytesInFile > 0 || blocksInFile > 0))
            await StartNewAsync(hub, file, bytesInFile, blocksInFile, totalBytes, totalBlocks, now);

        if (_currentFileId is null)
            return;

        long avg = ComputeAvg(now, bytesInFile);
        await SendAsync(hub, "ReportTapeFileReadProgress",
            new TapeFileReadProgressReport(
                FileId:                   _currentFileId.Value,
                SizeBytes:                bytesInFile,
                CurrentBlock:             totalBlocks,
                ThroughputBytesPerSecond: avg));
    }

    private async Task ProcessReadFinishedAsync(
        HubConnection hub, int file, long bytesInFile, long blocksInFile,
        long totalBytes, long totalBlocks, DateTimeOffset now)
    {
        // Create the last file if it was too short to appear in any throttled progress tick.
        if (_currentFileId is null && (bytesInFile > 0 || blocksInFile > 0))
            await StartNewAsync(hub, file, bytesInFile, blocksInFile, totalBytes, totalBlocks, now);

        if (_currentFileId is null) return;

        // At EndOfMedia, totalBytes is the cumulative total through the last file's final block
        // (no trailing new-file bytes to subtract, unlike the mid-tape transition case).
        await CompleteCurrentAsync(hub, now,
            bytesAtBoundary:  totalBytes  - _fileStartBytesTotal,
            blocksAtBoundary: totalBlocks - _fileStartBlocksTotal,
            filemarkAfter: false);
    }

    private async Task ProcessTerminalAsync(
        HubConnection hub, bool succeeded, string? errorMessage, DateTimeOffset now)
    {
        if (_currentFileId is null) return;
        var id = _currentFileId.Value;
        _currentFileId = null;

        await SendAsync(hub, "ReportTapeFileReadCompleted",
            new TapeFileReadCompletedReport(
                FileId:                   id,
                SizeBytes:                0,
                EndBlock:                 null,
                BlockCount:               null,
                FilemarkAfter:            false,
                ThroughputBytesPerSecond: null,
                Succeeded:                succeeded,
                ErrorMessage:             errorMessage));
    }

    private async Task ProcessStoppedAsync(HubConnection hub, DateTimeOffset now, string? errorMessage)
    {
        if (_currentFileId is null) return;

        var bytesAtBoundary  = Math.Max(0, _lastKnownTotalBytes  - _fileStartBytesTotal);
        var blocksAtBoundary = Math.Max(0, _lastKnownTotalBlocks - _fileStartBlocksTotal);

        // In Clone mode the .tic file is already closed before this runs — the clone
        // service flushes/closes on cancellation before the OCE propagates out of
        // CloneToImageAsync. Read the actual size so the upload record is exact.
        if (_isClone)
        {
            var localPath = TapeRunCacheLayout.GetFilePath(_cacheDirectory, _cacheRunId, _currentFileNumber);
            try
            {
                var actualBytes = new FileInfo(localPath).Length;
                if (actualBytes > 0)
                    bytesAtBoundary = actualBytes;
            }
            catch (Exception) { /* absent or unreadable: fall back to tick-based estimate */ }
        }

        if (bytesAtBoundary > 0)
            await CompleteCurrentAsync(hub, now, bytesAtBoundary, blocksAtBoundary, filemarkAfter: false);
        else
            await ProcessTerminalAsync(hub, succeeded: false, errorMessage, now);
    }

    // ── File lifecycle helpers ─────────────────────────────────────────────────

    private async Task StartNewAsync(
        HubConnection hub, int file,
        long bytesInFile, long blocksInFile,
        long totalBytes, long totalBlocks, DateTimeOffset now)
    {
        _currentFileNumber    = file;
        _fileStartedAt        = now;
        _fileStartBytesTotal  = totalBytes  - bytesInFile;   // bytes before this file started
        _fileStartBlocksTotal = totalBlocks - blocksInFile;  // blocks before this file started

        var localPath = _isClone
            ? TapeRunCacheLayout.GetFilePath(_cacheDirectory, _cacheRunId, file)
            : null;

        var fileName = _isClone
            ? $"segment.{file - 1:D4}.tic"
            : $"file-{file:D4}";

        var operationId = _command.CloneOperationId ?? _command.ReadOperationId;

        var report = new TapeFileCreatedReport(
            TapeRunId:          _command.RunId,
            TapeDeviceId:       _command.TapeDeviceId,
            CreatedByOperationId: operationId,
            TapeFileNumber:     file,
            SegmentNumber:      file,
            StartBlock:         totalBlocks,
            FilemarkBefore:     file > 1,
            FileName:           fileName,
            LocalPath:          localPath);

        Guid fileId;
        try
        {
            fileId = await hub.InvokeAsync<Guid>("ReportTapeFileCreated", report, _command.RunType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ReportTapeFileCreated failed for file {FileNumber} in run {RunId}; skipping hub tracking.",
                file, _command.RunId);
            return;
        }

        if (fileId == Guid.Empty)
        {
            _logger.LogWarning(
                "Server returned empty fileId for file {FileNumber} in run {RunId}.",
                file, _command.RunId);
            return;
        }

        _currentFileId = fileId;
    }

    private async Task CompleteCurrentAsync(
        HubConnection hub, DateTimeOffset now,
        long bytesAtBoundary, long blocksAtBoundary,
        bool filemarkAfter)
    {
        var id = _currentFileId!.Value;
        _currentFileId = null;

        long avg     = ComputeAvg(now, bytesAtBoundary);
        long endBlock = _fileStartBlocksTotal + blocksAtBoundary;

        // Use InvokeAsync (not SendAsync) so the server has committed the TapeFileUpload
        // record before we hand off to the hasher. Without this, a fast hash completes
        // and the uploader PUTs before the upload row exists → 404 non-retryable failure.
        try
        {
            await hub.InvokeAsync("ReportTapeFileReadCompleted",
                new TapeFileReadCompletedReport(
                    FileId:                   id,
                    SizeBytes:                Math.Max(0, bytesAtBoundary),
                    EndBlock:                 endBlock,
                    BlockCount:               Math.Max(0, blocksAtBoundary),
                    FilemarkAfter:            filemarkAfter,
                    ThroughputBytesPerSecond: avg,
                    Succeeded:                true,
                    ErrorMessage:             null));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReportTapeFileReadCompleted failed for file {FileId}.", id);
        }

        // In Clone mode, hand the completed .tic file off to the hasher.
        if (_isClone)
        {
            var localPath = TapeRunCacheLayout.GetFilePath(
                _cacheDirectory, _cacheRunId, _currentFileNumber);
            _hasher.Enqueue(new TapeFileHasher.WorkItem(
                FileId:           id,
                RunId:            _command.RunId,
                FilePath:         localPath,
                FileSizeBytes:    Math.Max(0, bytesAtBoundary),
                UploadOperationId: _command.UploadOperationId));
        }
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private Task EnqueueAndWaitAsync(Func<Task> work)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Writer.TryWrite(async () =>
        {
            try   { await work(); }
            catch (Exception ex) { _logger.LogWarning(ex, "TapeFileReporter terminal work item failed."); }
            finally { tcs.TrySetResult(); }
        });
        return tcs.Task;
    }

    private long ComputeAvg(DateTimeOffset now, long bytes)
    {
        var elapsed = (now - _fileStartedAt).TotalSeconds;
        return elapsed > 0 && bytes > 0 ? (long)(bytes / elapsed) : 0L;
    }

    private async Task SendAsync<T>(HubConnection hub, string method, T payload)
    {
        try   { await hub.SendAsync(method, payload); }
        catch (Exception ex) { _logger.LogWarning(ex, "File hub call {Method} failed.", method); }
    }

    private static long ToLong(ulong value) =>
        value > (ulong)long.MaxValue ? long.MaxValue : (long)value;
}
