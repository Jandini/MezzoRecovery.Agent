using System.Collections.Concurrent;
using System.Threading.Channels;
using MezzoRecovery.Agent.Api;
using MezzoRecovery.Agent.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.TapeOperations;

/// <summary>
/// Multi-run upload scheduler. Receives completed .tic file notifications, enforces
/// per-file and global concurrency limits, delegates each file upload to
/// <see cref="TapeMultipartFileUploader"/>, and publishes aggregate progress via SignalR.
///
/// Limits (configurable via constructor, defaults shown):
///   MaxConcurrentFileUploads = 2   — at most 2 files uploading simultaneously
///   MaxGlobalPartUploads     = 8   — at most 8 S3 part uploads in flight across all files
///
/// Public interface preserved for backward compatibility with AgentConnectionLoop:
///   Initialize / SetHub / Enqueue / StartAsync / CancelRunUploads
/// New:
///   PauseRunUpload / ResumeRunUpload
/// </summary>
public sealed class TapeFileUploader(
    ILogger<TapeFileUploader> logger,
    ILoggerFactory loggerFactory,
    int maxConcurrentFileUploads = 1,
    int maxGlobalPartUploads = 4,
    int maxConcurrentPartsPerFile = 4) : IDisposable
{
    // ── Work item (mirrors legacy shape for AgentConnectionLoop compatibility) ──

    public sealed record WorkItem(
        Guid   FileId,
        Guid   RunId,
        string FilePath,
        long   FileSizeBytes,
        Guid?  UploadOperationId,
        Guid?  ExistingUploadSessionId = null);

    // ── Scheduling state ──────────────────────────────────────────────────────

    private readonly Channel<WorkItem> _queue =
        Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    // Replaced atomically by UpdateConcurrency; captured before WaitAsync so Release pairs with the correct instance.
    private volatile SemaphoreSlim _fileUploadSemaphore = new(maxConcurrentFileUploads, maxConcurrentFileUploads);
    private volatile SemaphoreSlim _globalPartSemaphore = new(maxGlobalPartUploads, maxGlobalPartUploads);
    private volatile int _maxConcurrentPartsPerFile = maxConcurrentPartsPerFile;

    private readonly ConcurrentDictionary<Guid, byte> _pausedRunIds    = new();
    private readonly ConcurrentDictionary<Guid, byte> _cancelledRunIds = new();
    private readonly ConcurrentDictionary<Guid, TapeMultipartFileUploader> _activeUploaders = new();

    // ── Shared HTTP clients (one set per process — HttpClient is designed for reuse) ──
    //
    // Creating a new HttpClient per file upload leaks sockets and defeats connection pooling.
    // These three instances are shared across all concurrent file uploads for their lifetime.
    //
    // loggerFactory is a primary-constructor parameter; it is in scope for field initializers.

    // Control-plane API calls: token, session lifecycle, part completion reports.
    private readonly HttpClient _apiHttpClient     = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _sessionHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    // S3 part PUTs — 30-minute ceiling; the per-part stall watchdog always fires first.
    private readonly HttpClient _partHttpClient    = new() { Timeout = TimeSpan.FromMinutes(30) };

    // ── Credentials + cache ───────────────────────────────────────────────────

    private Uri?    _baseUri;
    private Guid    _agentId;
    private string? _clientSecret;
    private string  _cacheDirectory = "/opt/mezzorecovery-cache";

    // ── Hub ───────────────────────────────────────────────────────────────────

    private volatile HubConnection? _hub;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);

    // ── Public API ────────────────────────────────────────────────────────────

    public void Initialize(Uri baseUri, Guid agentId, string clientSecret, string? cacheDirectory = null)
    {
        _baseUri        = baseUri;
        _agentId        = agentId;
        _clientSecret   = clientSecret;
        _cacheDirectory = cacheDirectory ?? _cacheDirectory;
    }

    public void SetHub(HubConnection hub) => _hub = hub;

    public void CancelRunUploads(Guid runId)
    {
        _cancelledRunIds.TryAdd(runId, 0);
        logger.LogInformation("Upload cancellation registered for run {RunId}.", runId);
    }

    public void ResumeRunUploads(Guid runId)
    {
        _cancelledRunIds.TryRemove(runId, out _);
        logger.LogInformation("Upload cancellation cleared for run {RunId}.", runId);
    }

    public void PauseRunUpload(Guid runId)
    {
        _pausedRunIds.TryAdd(runId, 0);
        logger.LogInformation("Upload paused for run {RunId}.", runId);
    }

    public void ResumeRunUpload(Guid runId)
    {
        _pausedRunIds.TryRemove(runId, out _);
        logger.LogInformation("Upload resumed for run {RunId}.", runId);
    }

    /// <summary>
    /// Applies new concurrency limits at runtime. Takes effect for uploads that start after this call.
    /// In-flight uploads continue against the semaphore instances they originally acquired.
    /// </summary>
    public void UpdateConcurrency(int? maxConcurrentFileUploads, int? maxConcurrentPartsPerFile)
    {
        if (maxConcurrentPartsPerFile is { } parts and > 0)
            _maxConcurrentPartsPerFile = parts;

        if (maxConcurrentFileUploads is { } files and > 0)
        {
            var partsPerFile = _maxConcurrentPartsPerFile;
            _fileUploadSemaphore = new SemaphoreSlim(files, files);
            _globalPartSemaphore = new SemaphoreSlim(files * partsPerFile, files * partsPerFile);
            logger.LogInformation(
                "Upload concurrency updated: {MaxFiles} file(s), {MaxParts} parts/file.",
                files, partsPerFile);
        }
    }

    public void Enqueue(WorkItem item)
    {
        logger.LogInformation(
            "Upload enqueued for file {FileId} (run {RunId}, {Bytes} bytes).",
            item.FileId, item.RunId, item.FileSizeBytes);
        _queue.Writer.TryWrite(item);
    }

    /// <summary>
    /// Starts the scheduler loop. Returns a Task that completes when <paramref name="ct"/> is cancelled.
    /// Called once by AgentConnectionLoop. Does NOT wrap in Task.Run — the caller observes the task.
    /// </summary>
    public Task StartAsync(CancellationToken ct) => ScheduleAsync(ct);

    // ── Scheduler ─────────────────────────────────────────────────────────────

    private async Task ScheduleAsync(CancellationToken ct)
    {
        logger.LogInformation("Upload scheduler started.");

        // Progress publisher runs alongside the scheduler.
        var progressTask = PublishProgressAsync(ct);

        var activeTasks = new List<Task>();

        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(ct))
            {
                if (_cancelledRunIds.ContainsKey(item.RunId))
                {
                    logger.LogInformation(
                        "Upload skipped (cancelled run {RunId}) for file {FileId}.",
                        item.RunId, item.FileId);
                    continue;
                }

                if (_pausedRunIds.ContainsKey(item.RunId))
                {
                    logger.LogDebug(
                        "Upload deferred (paused run {RunId}) for file {FileId}. Re-enqueuing.",
                        item.RunId, item.FileId);
                    // Re-enqueue after a short delay so the scheduler can process other runs.
                    _ = ReEnqueueAfterDelayAsync(item, TimeSpan.FromSeconds(5), ct);
                    continue;
                }

                if (!File.Exists(item.FilePath))
                {
                    logger.LogWarning(
                        "Upload skipped for file {FileId}: local file not found at {Path}.",
                        item.FileId, item.FilePath);
                    continue;
                }

                // Prune completed tasks.
                activeTasks.RemoveAll(t => t.IsCompleted);

                // Capture the current semaphore before acquiring — UpdateConcurrency may replace
                // the field while we hold a slot; we must release exactly what we acquired.
                var fileSlot = _fileUploadSemaphore;
                await fileSlot.WaitAsync(ct);

                var uploader = CreateUploader();
                _activeUploaders.TryAdd(item.FileId, uploader);

                var uploadTask = RunUploadAsync(uploader, item, fileSlot, ct);
                activeTasks.Add(uploadTask);
            }

            // Wait for in-flight uploads to finish.
            await Task.WhenAll(activeTasks);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("Upload scheduler stopped.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Upload scheduler faulted.");
        }

        try { await progressTask; } catch { /* progress publisher also stops on cancel */ }
    }

    private async Task RunUploadAsync(
        TapeMultipartFileUploader uploader, WorkItem item, SemaphoreSlim fileSlot, CancellationToken ct)
    {
        try
        {
            await uploader.UploadAsync(
                new UploadWorkItem(item.FileId, item.RunId, item.FilePath, item.FileSizeBytes, item.ExistingUploadSessionId),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Upload failed for file {FileId}.", item.FileId);
        }
        finally
        {
            _activeUploaders.TryRemove(item.FileId, out _);
            fileSlot.Release();
        }
    }

    private async Task ReEnqueueAfterDelayAsync(WorkItem item, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            if (!_cancelledRunIds.ContainsKey(item.RunId))
                _queue.Writer.TryWrite(item);
        }
        catch (OperationCanceledException) { }
    }

    // ── Progress publisher ────────────────────────────────────────────────────

    // Heartbeat: fires every second. Calls ComputeThroughputSnapshot() on each active uploader
    // (consumes the interval byte accumulator and computes EWMA rate), then sends the current
    // byte position and speed to the API. This is the sole progress reporting path — there is
    // no per-part immediate push, which keeps all hub calls observable and cancellation-safe.
    private async Task PublishProgressAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(ProgressInterval, ct);

                var hub = _hub;
                if (hub is null) continue;

                foreach (var (fileId, uploader) in _activeUploaders)
                {
                    if (uploader.IsDone) continue;

                    try
                    {
                        var throughput = uploader.ComputeThroughputSnapshot();
                        await hub.SendAsync("ReportTapeFileUploadProgress",
                            new TapeFileUploadProgressReport(
                                FileId: fileId,
                                BytesUploaded: uploader.GetUploadedBytes(),
                                TotalBytes: uploader.GetTotalBytes(),
                                ThroughputBytesPerSecond: throughput),
                            ct);
                    }
                    catch { /* best-effort */ }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── Factory ───────────────────────────────────────────────────────────────

    private TapeMultipartFileUploader CreateUploader()
    {
        // API client wrappers are lightweight (stateless method dispatchers); creating one
        // per file is fine. The underlying HttpClient instances are shared across all uploads.
        return new TapeMultipartFileUploader(
            baseUri:                   _baseUri!,
            agentId:                   _agentId,
            clientSecret:              _clientSecret!,
            apiClient:                 new AgentApiClient(_apiHttpClient),
            sessionClient:             new AgentTapeUploadSessionApiClient(
                                           _sessionHttpClient,
                                           loggerFactory.CreateLogger<AgentTapeUploadSessionApiClient>()),
            checkpointStore:           new TapeUploadCheckpointStore(
                                           _cacheDirectory,
                                           loggerFactory.CreateLogger<TapeUploadCheckpointStore>()),
            globalPartSemaphore:       _globalPartSemaphore,
            partUploadHttpClient:      _partHttpClient,
            logger:                    loggerFactory.CreateLogger<TapeMultipartFileUploader>(),
            maxConcurrentPartsPerFile: _maxConcurrentPartsPerFile);
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _apiHttpClient.Dispose();
        _sessionHttpClient.Dispose();
        _partHttpClient.Dispose();
    }
}
