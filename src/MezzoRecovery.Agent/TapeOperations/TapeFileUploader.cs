using System.Net.Http.Headers;
using System.Threading.Channels;
using MezzoRecovery.Agent.Api;
using MezzoRecovery.Agent.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.TapeOperations;

/// <summary>
/// Singleton background worker that uploads completed .tic files to the API via
/// HTTP PUT and reports upload events over the AgentHub.
///
/// Retries failed uploads with exponential back-off (10 s → 20 s → 40 s … capped at 300 s).
/// Each retry attempt is a separate <c>tape.file_upload_attempts</c> record on the server.
/// The worker is single-threaded: files are uploaded one at a time in arrival order.
/// </summary>
public sealed class TapeFileUploader(ILogger<TapeFileUploader> logger)
{
    private const int MaxServerErrorAttempts = 10;
    public sealed record WorkItem(
        Guid  FileId,
        Guid  RunId,
        string FilePath,
        long  FileSizeBytes,
        Guid? UploadOperationId);

    private readonly Channel<WorkItem> _queue =
        Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly HashSet<Guid> _cancelledRunIds = [];
    private readonly object _cancelLock = new();

    private volatile HubConnection? _hub;

    // Initialised once by AgentConnectionLoop after credentials are loaded.
    private Uri?    _baseUri;
    private Guid    _agentId;
    private string? _clientSecret;

    public void Initialize(Uri baseUri, Guid agentId, string clientSecret)
    {
        _baseUri      = baseUri;
        _agentId      = agentId;
        _clientSecret = clientSecret;
    }

    public void SetHub(HubConnection hub) => _hub = hub;

    public void CancelRunUploads(Guid runId)
    {
        lock (_cancelLock) _cancelledRunIds.Add(runId);
        logger.LogInformation("Upload cancellation registered for run {RunId}.", runId);
    }

    public void Enqueue(WorkItem item)
    {
        logger.LogInformation(
            "Upload enqueued for file {FileId} (run {RunId}, {Bytes} bytes, path {FilePath}).",
            item.FileId, item.RunId, item.FileSizeBytes, item.FilePath);
        _queue.Writer.TryWrite(item);
    }

    /// <summary>
    /// Starts the background consumer. Called once from the DI-composed run loop.
    /// </summary>
    public Task StartAsync(CancellationToken ct) => Task.Run(() => ConsumeAsync(ct), ct);

    // ── Consumer ───────────────────────────────────────────────────────────────

    private async Task ConsumeAsync(CancellationToken ct)
    {
        logger.LogInformation("Upload consumer started.");
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(ct))
            {
                bool cancelled;
                lock (_cancelLock) cancelled = _cancelledRunIds.Contains(item.RunId);
                if (cancelled)
                {
                    logger.LogInformation(
                        "Upload skipped for file {FileId}: run {RunId} was cancelled.",
                        item.FileId, item.RunId);
                    continue;
                }

                logger.LogInformation(
                    "Upload dequeued for file {FileId} (run {RunId}).",
                    item.FileId, item.RunId);
                await UploadWithRetryAsync(item, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("Upload consumer stopped (shutdown).");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Upload consumer faulted — uploads will not proceed.");
        }
    }

    private async Task UploadWithRetryAsync(WorkItem item, CancellationToken ct)
    {
        if (_baseUri is null || _clientSecret is null)
        {
            logger.LogWarning(
                "Uploader not initialised; skipping upload for file {FileId}.", item.FileId);
            return;
        }

        for (var attempt = 0; ; attempt++)
        {
            if (ct.IsCancellationRequested) return;

            if (attempt > 0)
            {
                // Exponential back-off: 10 s, 20 s, 40 s … capped at 300 s
                var delaySec = Math.Min(300, 10 * (1 << (attempt - 1)));
                logger.LogInformation(
                    "Upload retry {Attempt} for file {FileId} in {Delay}s.",
                    attempt, item.FileId, delaySec);
                try { await Task.Delay(TimeSpan.FromSeconds(delaySec), ct); }
                catch (OperationCanceledException) { return; }
            }

            if (!File.Exists(item.FilePath))
            {
                logger.LogWarning(
                    "Upload skipped for file {FileId}: local file not found at {Path}.",
                    item.FileId, item.FilePath);
                return;
            }

            try
            {
                var token = await GetTokenAsync(ct);
                if (token is null)
                {
                    logger.LogWarning("Could not obtain JWT; will retry upload for file {FileId}.", item.FileId);
                    continue;
                }

                var url = new Uri(_baseUri,
                    $"api/agent/tape/runs/{item.RunId}/files/{item.FileId}/upload");

                using var http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
                var totalBytes = item.FileSizeBytes;

                // Stall watchdog: cancel if no bytes flow for 60 s. Handles silently dead TCP
                // connections that would otherwise block the consumer for up to 2 hours.
                using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                watchdogCts.CancelAfter(TimeSpan.FromSeconds(60));

                var nextLogAt    = 0L;
                var lastLogBytes = 0L;
                var lastLogAt    = DateTimeOffset.UtcNow;
                const long logEvery = 1 * 1024 * 1024; // report every 1 MB

                // ProgressStream fires every 16 KB → watchdog is reset on each callback.
                // Log lines and hub reports are throttled to every 1 MB; throughput is
                // calculated from the bytes/time delta between consecutive reports.
                using var stream = new ProgressStream(
                    File.OpenRead(item.FilePath),
                    bytesRead =>
                    {
                        watchdogCts.CancelAfter(TimeSpan.FromSeconds(60));
                        if (bytesRead < nextLogAt) return;
                        var now      = DateTimeOffset.UtcNow;
                        var elapsed  = (now - lastLogAt).TotalSeconds;
                        var throughput = elapsed > 0.001
                            ? (long?)((bytesRead - lastLogBytes) / elapsed)
                            : null;
                        lastLogBytes = bytesRead;
                        lastLogAt    = now;
                        nextLogAt    = bytesRead + logEvery;
                        logger.LogInformation(
                            "Uploading {FileId}: {Sent:F1} / {Total:F1} MB.",
                            item.FileId, bytesRead / 1_048_576.0, totalBytes / 1_048_576.0);
                        ReportUploadProgress(item, bytesRead, totalBytes, throughput);
                    },
                    progressIntervalBytes: 16 * 1024);

                var req = new HttpRequestMessage(HttpMethod.Put, url)
                {
                    Content = new StreamContent(stream),
                };
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                logger.LogInformation(
                    "Uploading file {FileId} ({TotalMB:F1} MB) attempt {Attempt}.",
                    item.FileId, totalBytes / 1_048_576.0, attempt + 1);

                ReportUploadProgress(item, 0, totalBytes, null);

                using var resp = await http.SendAsync(
                    req, HttpCompletionOption.ResponseHeadersRead, watchdogCts.Token);

                if (resp.IsSuccessStatusCode)
                {
                    logger.LogInformation(
                        "Upload succeeded for file {FileId} ({TotalMB:F1} MB).",
                        item.FileId, totalBytes / 1_048_576.0);
                    return;   // success — done
                }

                var body = await resp.Content.ReadAsStringAsync(ct);
                var statusCode = (int)resp.StatusCode;
                logger.LogWarning(
                    "Upload HTTP {Status} for file {FileId}: {Body}.",
                    statusCode, item.FileId, body);

                if (IsNonRetryableUploadFailure(statusCode, body))
                {
                    await ReportUploadFailedAsync(item, "StorageError",
                        $"HTTP {statusCode}: {body}");
                    return;
                }

                // 4xx errors are not retryable (bad request / not found)
                if (statusCode is >= 400 and < 500)
                {
                    await ReportUploadFailedAsync(item, "HttpError",
                        $"HTTP {statusCode}: {body}");
                    return;
                }

                // 5xx → retry with cap
                if (attempt + 1 >= MaxServerErrorAttempts)
                {
                    logger.LogError(
                        "Upload failed for file {FileId} after {Attempts} server-error attempts.",
                        item.FileId, MaxServerErrorAttempts);
                    await ReportUploadFailedAsync(item, "HttpError",
                        $"HTTP {statusCode} after {MaxServerErrorAttempts} attempts: {body}");
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Watchdog fired — connection stalled with no progress for 60 s.
                logger.LogWarning(
                    "Upload stalled (no progress for 60 s) for file {FileId} attempt {Attempt}. Will retry.",
                    item.FileId, attempt + 1);

                if (attempt + 1 >= MaxServerErrorAttempts)
                {
                    await ReportUploadFailedAsync(item, "NetworkError",
                        "Upload stalled — no progress for 60 s.");
                    return;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Upload attempt {Attempt} threw for file {FileId}.", attempt + 1, item.FileId);

                if (attempt + 1 >= MaxServerErrorAttempts)
                {
                    await ReportUploadFailedAsync(item, "NetworkError", ex.Message);
                    return;
                }
            }
        }
    }

    private static bool IsNonRetryableUploadFailure(int statusCode, string body) =>
        statusCode == 507
        || body.Contains("\"retryable\":false", StringComparison.OrdinalIgnoreCase)
        || body.Contains("\"retryable\": false", StringComparison.OrdinalIgnoreCase);

    private void ReportUploadProgress(WorkItem item, long bytesUploaded, long totalBytes, long? throughput)
    {
        var hub = _hub;
        if (hub is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await hub.SendAsync("ReportTapeFileUploadProgress",
                    new TapeFileUploadProgressReport(
                        FileId:                   item.FileId,
                        BytesUploaded:             bytesUploaded,
                        TotalBytes:                totalBytes,
                        ThroughputBytesPerSecond: throughput));
            }
            catch { /* best-effort */ }
        });
    }

    private async Task ReportUploadFailedAsync(WorkItem item, string reason, string message)
    {
        var hub = _hub;
        if (hub is null) return;
        try
        {
            await hub.SendAsync("ReportTapeFileUploadFailed",
                new TapeFileUploadFailedReport(
                    FileId:        item.FileId,
                    FailureReason: reason,
                    FailureMessage: message));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ReportTapeFileUploadFailed hub call failed for file {FileId}.", item.FileId);
        }
    }

    private async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var api = new AgentApiClient(http);
            var resp = await api.GetTokenAsync(
                _baseUri!,
                new TokenApiRequest(_agentId, _clientSecret!),
                ct);
            return resp?.AccessToken;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to obtain JWT for file upload.");
            return null;
        }
    }

    /// <summary>
    /// Transparent read-only stream wrapper that invokes a callback every
    /// <paramref name="progressIntervalBytes"/> bytes so the caller can report upload progress.
    /// Delegates CanSeek/Length/Position/Seek to the inner stream so that
    /// <see cref="StreamContent"/> can still compute and send a Content-Length header.
    /// Compatible with Native AOT — no reflection used.
    /// </summary>
    private sealed class ProgressStream(
        Stream inner,
        Action<long> onProgress,
        long progressIntervalBytes) : Stream
    {
        private long _bytesRead;
        private long _lastReported;

        // Delegate all stream capabilities to the inner stream so StreamContent
        // can set Content-Length (requires CanSeek = true + Length/Position).
        public override bool CanRead  => inner.CanRead;
        public override bool CanSeek  => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length   => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            Advance(n);
            return n;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await inner.ReadAsync(buffer, ct);
            Advance(n);
            return n;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var n = await inner.ReadAsync(buffer, offset, count, ct);
            Advance(n);
            return n;
        }

        private void Advance(int n)
        {
            if (n <= 0) return;
            _bytesRead += n;
            if (_bytesRead - _lastReported >= progressIntervalBytes)
            {
                _lastReported = _bytesRead;
                onProgress(_bytesRead);
            }
        }

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
