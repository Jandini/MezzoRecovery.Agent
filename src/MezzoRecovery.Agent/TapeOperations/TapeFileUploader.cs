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

    public void Enqueue(WorkItem item) => _queue.Writer.TryWrite(item);

    /// <summary>
    /// Starts the background consumer. Called once from the DI-composed run loop.
    /// </summary>
    public Task StartAsync(CancellationToken ct) => Task.Run(() => ConsumeAsync(ct), ct);

    // ── Consumer ───────────────────────────────────────────────────────────────

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(ct))
        {
            await UploadWithRetryAsync(item, ct);
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

                using var http   = new HttpClient { Timeout = TimeSpan.FromHours(2) };
                using var stream = File.OpenRead(item.FilePath);

                var req = new HttpRequestMessage(HttpMethod.Put, url)
                {
                    Content = new StreamContent(stream),
                };
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var totalBytes = item.FileSizeBytes;

                logger.LogInformation(
                    "Uploading file {FileId} ({Bytes} bytes) attempt {Attempt}.",
                    item.FileId, totalBytes, attempt + 1);

                ReportUploadProgress(item, 0, totalBytes);

                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                if (resp.IsSuccessStatusCode)
                {
                    logger.LogInformation(
                        "Upload succeeded for file {FileId} ({Bytes} bytes).",
                        item.FileId, totalBytes);
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

    private void ReportUploadProgress(WorkItem item, long bytesUploaded, long totalBytes)
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
                        ThroughputBytesPerSecond: null));
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
}
