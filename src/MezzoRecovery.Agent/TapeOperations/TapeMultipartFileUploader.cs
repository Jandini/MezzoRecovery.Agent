using MezzoRecovery.Agent.Api;
using MezzoRecovery.Agent.Contracts;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.Http.Headers;

namespace MezzoRecovery.Agent.TapeOperations;

/// <summary>
/// Uploads a single .tic file to S3-compatible object storage using the multipart
/// upload session API. Handles resume, part-level retry, and stall detection.
///
/// Progress model:
///   GetUploadedBytes()          = completed-part bytes + bytes currently in-flight across all active part PUTs
///   ComputeThroughputSnapshot() = interval-based EWMA throughput; call from heartbeat timer (~1 s)
///
/// Progress is published solely by the scheduler's 1 Hz heartbeat loop — no fire-and-forget
/// callbacks are used so that all hub calls remain observable and cancellable.
/// </summary>
internal sealed class TapeMultipartFileUploader(
    Uri baseUri,
    Guid agentId,
    string clientSecret,
    AgentApiClient apiClient,
    AgentTapeUploadSessionApiClient sessionClient,
    TapeUploadCheckpointStore checkpointStore,
    SemaphoreSlim globalPartSemaphore,
    HttpClient partUploadHttpClient,
    ILogger<TapeMultipartFileUploader> logger,
    int maxConcurrentPartsPerFile = 4)
{
    private const int MaxPartRetries = 10;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(120);

    // ── Progress tracking ─────────────────────────────────────────────────────

    // Bytes from fully completed parts.
    private long _completedBytes;

    // Bytes uploaded in the CURRENT attempt for each in-flight part.
    // Key = partNumber (1-based). Reset to 0 at the start of each retry attempt.
    private readonly ConcurrentDictionary<int, long> _partAttemptBytes = new();

    // Accumulates bytes from ProgressStream callbacks between heartbeat snapshots.
    private long _bytesInCurrentInterval;
    private long _lastIntervalTickMs = Environment.TickCount64;

    // EWMA-smoothed throughput in bytes/second.
    // Written from part tasks and the heartbeat thread; use Interlocked for memory-fence correctness.
    private long _smoothedThroughput;

    private long _totalBytes;
    private bool _done;

    // ── Token cache ───────────────────────────────────────────────────────────

    // Cached JWT so we don't issue a new token request after every part upload.
    // Protected by double-checked locking via _tokenLock.
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiresAt;
    private static readonly TimeSpan TokenRefreshBuffer = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    // ── Signed-URL cache ──────────────────────────────────────────────────────

    // Pre-signed URLs are batch-obtained before the upload loop starts, eliminating
    // one API round-trip per part while a concurrency slot is held.
    // ConcurrentDictionary because multiple part tasks access it simultaneously.
    private static readonly TimeSpan SignedUrlRefreshBuffer = TimeSpan.FromMinutes(5);

    // ── Public surface ────────────────────────────────────────────────────────

    public Guid FileId { get; private set; }
    public Guid RunId  { get; private set; }
    public bool IsDone => _done;

    /// <summary>
    /// Bytes uploaded so far: completed parts plus bytes actively streaming through open PUT requests.
    /// Safe to call from any thread.
    /// </summary>
    public long GetUploadedBytes()
    {
        var inFlight = 0L;
        foreach (var v in _partAttemptBytes.Values) inFlight += v;
        return Interlocked.Read(ref _completedBytes) + inFlight;
    }

    public long GetTotalBytes() => _totalBytes;

    /// <summary>
    /// Computes current EWMA throughput from bytes accumulated since the last call.
    /// Must be called by ONE thread only (the heartbeat timer). Returns null when
    /// no data has flowed recently.
    /// </summary>
    public long? ComputeThroughputSnapshot()
    {
        var now        = Environment.TickCount64;
        var elapsedMs  = Math.Max(100L, now - _lastIntervalTickMs);
        _lastIntervalTickMs = now;

        var bytes       = Interlocked.Exchange(ref _bytesInCurrentInterval, 0);
        var instantRate = bytes * 1000L / elapsedMs;

        var smoothed = (long)(Interlocked.Read(ref _smoothedThroughput) * 0.7 + instantRate * 0.3);
        Interlocked.Exchange(ref _smoothedThroughput, smoothed);

        return smoothed > 100 ? smoothed : null;
    }

    // ── Upload entry point ────────────────────────────────────────────────────

    public async Task UploadAsync(UploadWorkItem workItem, CancellationToken runCt, CancellationToken globalCt)
    {
        FileId      = workItem.FileId;
        RunId       = workItem.RunId;
        _totalBytes = workItem.FileSizeBytes;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(runCt, globalCt);
        var ct = linkedCts.Token;

        // Seeds the token cache so the first part cycle doesn't need to fetch a token.
        var token = await GetOrRefreshTokenAsync(ct);
        if (token is null)
        {
            logger.LogError("Could not obtain API token for upload of file {FileId}.", workItem.FileId);
            return;
        }

        var sessionReq = new StartUploadSessionApiRequest(
            FileName:              Path.GetFileName(workItem.FilePath),
            TotalBytes:            workItem.FileSizeBytes,
            ContentType:           "application/octet-stream",
            PreferredPartSizeBytes: 67_108_864L);

        var session = await sessionClient.StartOrResumeSessionAsync(
            baseUri, token, workItem.RunId, workItem.FileId, sessionReq, ct);

        if (session is null)
        {
            logger.LogError(
                "Failed to start upload session for file {FileId} run {RunId} path {FilePath}.",
                workItem.FileId, workItem.RunId, workItem.FilePath);
            return;
        }

        logger.LogInformation(
            "Upload session {SessionId}: {TotalParts} parts of {PartSize} MB each for file {FileId}.",
            session.UploadSessionId, session.TotalParts, session.PartSizeBytes / 1_048_576, workItem.FileId);

        // ── Reconcile checkpoint ──────────────────────────────────────────────

        var checkpoint = await checkpointStore.LoadAsync(workItem.RunId, workItem.FileId, ct)
                         ?? new UploadCheckpoint
                         {
                             RunId           = workItem.RunId,
                             FileId          = workItem.FileId,
                             UploadSessionId = session.UploadSessionId,
                             FilePath        = workItem.FilePath,
                             FileName        = Path.GetFileName(workItem.FilePath),
                             FileSizeBytes   = workItem.FileSizeBytes,
                             PartSizeBytes   = session.PartSizeBytes,
                         };

        // Discard stale part ETags when the S3 session changed (e.g. after stop+resume).
        // Old ETags are invalid for the new session — merging them would make the agent
        // think all parts are done and call CompleteMultipartUpload with wrong ETags.
        // Check both the DB session GUID and the underlying S3 upload ID: a new S3 upload
        // can be created for the same DB session (same GUID) when a stopped run resumes.
        if (checkpoint.UploadSessionId != session.UploadSessionId
            || (session.ProviderUploadId is not null && checkpoint.ProviderUploadId != session.ProviderUploadId))
            checkpoint.CompletedParts = [];
        checkpoint.ProviderUploadId = session.ProviderUploadId;

        var serverCompleted = session.CompletedParts.ToDictionary(p => p.PartNumber, p => p.ETag);
        var localCompleted  = checkpoint.CompletedParts.ToDictionary(p => p.PartNumber, p => p.ETag);

        var allCompleted = new Dictionary<int, string>(localCompleted);
        foreach (var (pn, etag) in serverCompleted)
            allCompleted[pn] = etag;

        checkpoint.CompletedParts   = allCompleted.Select(kv => new CheckpointPartDto { PartNumber = kv.Key, ETag = kv.Value }).ToArray();
        checkpoint.UploadSessionId  = session.UploadSessionId;
        checkpoint.ProviderUploadId = session.ProviderUploadId;
        checkpoint.PartSizeBytes    = session.PartSizeBytes;

        // Seed _completedBytes from already-uploaded parts so GetUploadedBytes() is accurate from the start.
        var seedBytes = (long)allCompleted.Count * session.PartSizeBytes;
        Interlocked.Exchange(ref _completedBytes, Math.Min(seedBytes, workItem.FileSizeBytes));

        // ── Upload missing parts ──────────────────────────────────────────────

        var totalParts   = session.TotalParts;
        var missingParts = Enumerable.Range(1, totalParts).Where(p => !allCompleted.ContainsKey(p)).ToList();

        if (missingParts.Count == 0)
        {
            logger.LogInformation(
                "All {TotalParts} parts already completed for file {FileId}. Completing session.",
                totalParts, workItem.FileId);
        }
        else
        {
            logger.LogInformation(
                "Uploading {Missing}/{Total} missing parts for file {FileId}.",
                missingParts.Count, totalParts, workItem.FileId);

            // Batch-sign all missing parts in a single API call before starting concurrent
            // uploads, eliminating per-part sign latency while a concurrency slot is held.
            var signedUrlCache = await BatchSignPartsAsync(session, workItem, missingParts, ct);

            var perFileSemaphore = new SemaphoreSlim(maxConcurrentPartsPerFile);
            var partTasks = missingParts
                .Select(partNumber => UploadPartWithRetryAsync(
                    workItem, session, partNumber, checkpoint, allCompleted,
                    signedUrlCache, perFileSemaphore, ct))
                .ToList();

            try
            {
                await Task.WhenAll(partTasks);
            }
            catch (OperationCanceledException) when (runCt.IsCancellationRequested && !globalCt.IsCancellationRequested)
            {
                logger.LogInformation("Upload for file {FileId} stopped by run cancellation. Aborting session.", workItem.FileId);
                await AbortSessionSilentlyAsync(session.UploadSessionId, workItem.FileId);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var failedCount = partTasks.Count(t => t.IsFaulted);
                logger.LogError(ex,
                    "Upload failed: {Failed}/{Total} part task(s) faulted for file {FileId}.",
                    failedCount, partTasks.Count, workItem.FileId);

                // Tell the server so the record shows Failed instead of staying Uploading.
                var reportToken = await GetOrRefreshTokenAsync(CancellationToken.None);
                if (reportToken is not null)
                {
                    await sessionClient.ReportFailedAsync(
                        baseUri, reportToken, session.UploadSessionId,
                        new FailUploadApiRequest("PartUploadFailed", ex.Message),
                        CancellationToken.None);
                }
                return;
            }

            // Defensive check: all part tasks completed without throwing but allCompleted
            // is short — indicates a logic bug rather than a transient failure.
            if (allCompleted.Count != totalParts)
            {
                logger.LogError(
                    "Upload incomplete after WhenAll: {Completed}/{Total} parts for file {FileId}. " +
                    "This is a bug — some part tasks succeeded without recording completion.",
                    allCompleted.Count, totalParts, workItem.FileId);
                return;
            }
        }

        // ── Complete session ──────────────────────────────────────────────────

        var finalToken = await GetOrRefreshTokenAsync(ct) ?? token;
        var result = await sessionClient.CompleteSessionAsync(baseUri, finalToken, session.UploadSessionId, ct);
        if (result is not null)
        {
            logger.LogInformation(
                "Upload completed for file {FileId}. Object: {ObjectKey} Size: {Size} bytes.",
                workItem.FileId, result.ObjectKey, result.TotalBytes);
            checkpointStore.Delete(workItem.RunId, workItem.FileId);
        }
        else
        {
            logger.LogError("CompleteSession call failed for upload session {SessionId}.", session.UploadSessionId);
        }

        _done = true;
    }

    // ── Batch-sign all missing parts upfront ──────────────────────────────────

    private async Task<ConcurrentDictionary<int, SignedPartDto>> BatchSignPartsAsync(
        StartUploadSessionApiResponse session,
        UploadWorkItem workItem,
        List<int> partNumbers,
        CancellationToken ct)
    {
        var cache = new ConcurrentDictionary<int, SignedPartDto>();

        var currentToken = await GetOrRefreshTokenAsync(ct);
        if (currentToken is null) return cache;

        var partsToSign = partNumbers
            .Select(pn =>
            {
                var offset = (long)(pn - 1) * session.PartSizeBytes;
                var length = Math.Min(session.PartSizeBytes, workItem.FileSizeBytes - offset);
                return new PartToSignDto(pn, offset, length);
            })
            .ToArray();

        var signResp = await sessionClient.SignPartsAsync(
            baseUri, currentToken, session.UploadSessionId,
            new SignPartsApiRequest(partsToSign),
            ct);

        if (signResp is null)
        {
            logger.LogWarning(
                "Batch sign failed for session {SessionId}. Parts will be signed individually on demand.",
                session.UploadSessionId);
            return cache;
        }

        foreach (var part in signResp.Parts)
            cache[part.PartNumber] = part;

        logger.LogDebug(
            "Batch-signed {Count} parts for file {FileId}.",
            signResp.Parts.Length, workItem.FileId);

        return cache;
    }

    // ── Get or refresh a signed URL for a part ────────────────────────────────

    private async Task<SignedPartDto?> GetOrRefreshSignedUrlAsync(
        Guid uploadSessionId,
        int partNumber,
        long offset,
        long length,
        ConcurrentDictionary<int, SignedPartDto> cache,
        CancellationToken ct)
    {
        if (cache.TryGetValue(partNumber, out var cached) &&
            cached.ExpiresAt - DateTimeOffset.UtcNow > SignedUrlRefreshBuffer)
            return cached;

        var currentToken = await GetOrRefreshTokenAsync(ct);
        if (currentToken is null) return null;

        var resp = await sessionClient.SignPartsAsync(
            baseUri, currentToken, uploadSessionId,
            new SignPartsApiRequest([new PartToSignDto(partNumber, offset, length)]),
            ct);

        if (resp is null || resp.Parts.Length == 0)
        {
            logger.LogWarning("Failed to sign part {Part} for session {SessionId}.", partNumber, uploadSessionId);
            return null;
        }

        cache[partNumber] = resp.Parts[0];
        return resp.Parts[0];
    }

    // ── Part upload ───────────────────────────────────────────────────────────

    private async Task UploadPartWithRetryAsync(
        UploadWorkItem workItem,
        StartUploadSessionApiResponse session,
        int partNumber,
        UploadCheckpoint checkpoint,
        Dictionary<int, string> allCompleted,
        ConcurrentDictionary<int, SignedPartDto> signedUrlCache,
        SemaphoreSlim perFileSemaphore,
        CancellationToken ct)
    {
        await perFileSemaphore.WaitAsync(ct);
        await globalPartSemaphore.WaitAsync(ct);
        try
        {
            var offset = (long)(partNumber - 1) * session.PartSizeBytes;
            var length = Math.Min(session.PartSizeBytes, workItem.FileSizeBytes - offset);

            for (int attempt = 1; attempt <= MaxPartRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                // Reset in-flight counter for this attempt (covers retries too).
                _partAttemptBytes[partNumber] = 0;
                long attemptBytes = 0;

                // Sliding-window stall CTS: each byte arrival resets the deadline to StallTimeout
                // from now, so a slow-but-active upload is never cancelled — only genuine silence
                // (no bytes for StallTimeout) triggers the stall path.
                using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stallCts.CancelAfter(StallTimeout);

                // Callback: called by ProgressStream on every network read.
                // Updates per-part in-flight bytes, the interval accumulator, and extends
                // the stall deadline so the timer only fires on true silence.
                void OnBytes(int n)
                {
                    attemptBytes += n;
                    _partAttemptBytes[partNumber] = attemptBytes;
                    Interlocked.Add(ref _bytesInCurrentInterval, n);
                    stallCts.CancelAfter(StallTimeout);
                }

                // Resolve signed URL before each attempt. Uses the batch-signed cache;
                // re-signs on-demand only if the cached URL is near expiry or missing.
                var signedPart = await GetOrRefreshSignedUrlAsync(
                    session.UploadSessionId, partNumber, offset, length, signedUrlCache, ct);

                if (signedPart is null)
                {
                    _partAttemptBytes[partNumber] = 0;
                    if (attempt < MaxPartRetries)
                    {
                        var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, attempt)) * (0.8 + Random.Shared.NextDouble() * 0.4));
                        logger.LogWarning(
                            "Part {Part} of file {FileId}: could not obtain signed URL on attempt {Attempt}/{Max}. Retrying in {Delay}s.",
                            partNumber, workItem.FileId, attempt, MaxPartRetries, delay.TotalSeconds);
                        await Task.Delay(delay, ct);
                    }
                    continue;
                }

                try
                {
                    var partStartMs = Environment.TickCount64;
                    var etag = await UploadPartOnceAsync(
                        workItem, partNumber, offset, length, signedPart.UploadUrl, OnBytes, stallCts.Token, ct);

                    if (etag is null)
                    {
                        _partAttemptBytes[partNumber] = 0;
                        // Evict the cached URL on PUT failure — a 403 may mean the URL expired.
                        signedUrlCache.TryRemove(partNumber, out _);
                        if (attempt < MaxPartRetries)
                        {
                            var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, attempt)) * (0.8 + Random.Shared.NextDouble() * 0.4));
                            logger.LogWarning(
                                "Part {Part} of file {FileId} failed attempt {Attempt}/{Max}. Retrying in {Delay}s.",
                                partNumber, workItem.FileId, attempt, MaxPartRetries, delay.TotalSeconds);
                            await Task.Delay(delay, ct);
                        }
                        continue;
                    }

                    // ── Part succeeded ────────────────────────────────────────

                    var currentToken = await GetOrRefreshTokenAsync(ct);
                    var partConfirmed = await sessionClient.CompletePartAsync(
                        baseUri, currentToken ?? string.Empty, session.UploadSessionId, partNumber,
                        new CompletePartApiRequest(etag, length), ct);

                    if (!partConfirmed)
                    {
                        _partAttemptBytes[partNumber] = 0;
                        signedUrlCache.TryRemove(partNumber, out _);
                        if (attempt < MaxPartRetries)
                        {
                            var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, attempt)) * (0.8 + Random.Shared.NextDouble() * 0.4));
                            logger.LogWarning(
                                "Part {Part} of file {FileId}: CompletePartAsync failed on attempt {Attempt}/{Max}. Retrying in {Delay:F0}s.",
                                partNumber, workItem.FileId, attempt, MaxPartRetries, delay.TotalSeconds);
                            await Task.Delay(delay, ct);
                        }
                        continue;
                    }

                    // Move bytes from in-flight → completed.
                    _partAttemptBytes.TryRemove(partNumber, out _);
                    Interlocked.Add(ref _completedBytes, length);

                    // Per-part throughput for the immediate push (excludes signing overhead
                    // on large parts, which is negligible).
                    var partElapsedSec = Math.Max(0.001, (Environment.TickCount64 - partStartMs) / 1000.0);
                    var partThroughput = (long)(length / partElapsedSec);

                    // Blend into the EWMA so the heartbeat starts from a good value.
                    Interlocked.Exchange(ref _smoothedThroughput,
                        (long)(Interlocked.Read(ref _smoothedThroughput) * 0.7 + partThroughput * 0.3));

                    // Save checkpoint.
                    allCompleted[partNumber] = etag;
                    checkpoint.CompletedParts = allCompleted
                        .Select(kv => new CheckpointPartDto { PartNumber = kv.Key, ETag = kv.Value })
                        .ToArray();
                    await checkpointStore.SaveAsync(checkpoint, ct);

                    logger.LogDebug(
                        "Part {Part}/{Total} completed for file {FileId}. ETag: {ETag}",
                        partNumber, session.TotalParts, workItem.FileId, etag);

                    return;
                }
                catch (OperationCanceledException)
                {
                    _partAttemptBytes.TryRemove(partNumber, out _);
                    throw;
                }
                catch (Exception ex)
                {
                    _partAttemptBytes[partNumber] = 0;
                    logger.LogWarning(ex,
                        "Part {Part} of file {FileId} threw on attempt {Attempt}/{Max}.",
                        partNumber, workItem.FileId, attempt, MaxPartRetries);

                    if (attempt >= MaxPartRetries)
                        throw;

                    var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, attempt)) * (0.8 + Random.Shared.NextDouble() * 0.4));
                    await Task.Delay(delay, ct);
                }
            }
        }
        finally
        {
            globalPartSemaphore.Release();
            perFileSemaphore.Release();
        }
    }

    private async Task<string?> UploadPartOnceAsync(
        UploadWorkItem workItem,
        int partNumber,
        long offset,
        long length,
        string uploadUrl,
        Action<int> onBytesRead,
        CancellationToken stallToken,
        CancellationToken ct)
    {
        await using var fs = new FileStream(
            workItem.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        fs.Seek(offset, SeekOrigin.Begin);

        // ProgressStream reports each read to the caller so in-flight bytes stay current.
        // The caller's OnBytes callback also resets stallToken's deadline on every read,
        // so the token only fires when no bytes have moved for StallTimeout.
        using var progress = new ProgressStream(fs, length, onBytesRead);

        var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
        request.Content = new StreamContent(progress);
        request.Content.Headers.ContentLength = length;
        request.Content.Headers.ContentType   = new MediaTypeHeaderValue("application/octet-stream");

        try
        {
            using var response = await partUploadHttpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, stallToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var body = await response.Content.ReadAsStringAsync(stallToken);
                logger.LogWarning(
                    "Part {Part} of file {FileId} PUT returned 403. MinIO error: {Body}",
                    partNumber, workItem.FileId, body);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(stallToken);
                logger.LogWarning(
                    "Part {Part} of file {FileId} PUT returned {StatusCode}. Body: {Body}",
                    partNumber, workItem.FileId, (int)response.StatusCode, body);
                return null;
            }

            var rawEtag = response.Headers.ETag?.Tag
                          ?? (response.Headers.TryGetValues("ETag", out var vals) ? vals.FirstOrDefault() : null);
            // S3 CompleteMultipartUpload requires quoted ETags (e.g. "\"abc123\"").
            // Some S3-compatible providers return the value without surrounding quotes; normalise here.
            var etag = rawEtag is not null && !rawEtag.StartsWith('"')
                ? $"\"{rawEtag}\""
                : rawEtag;
            return etag;
        }
        catch (OperationCanceledException) when (stallToken.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Part {Part} upload stalled (no data for {Timeout}s).", partNumber, StallTimeout.TotalSeconds);
            return null;
        }
        catch (HttpRequestException hre) when (hre.InnerException is System.Security.Authentication.AuthenticationException)
        {
            logger.LogError(
                "TLS handshake failed uploading part {Part} of file {FileId} to {Scheme}://{Host}. " +
                "Verify that TapeObjectStorage:PublicEndpoint uses the correct scheme (http vs https).",
                partNumber, workItem.FileId,
                request.RequestUri?.Scheme, request.RequestUri?.Host);
            throw;
        }
    }

    // ── Token refresh (cached with double-checked locking) ────────────────────

    private async Task AbortSessionSilentlyAsync(Guid uploadSessionId, Guid fileId)
    {
        try
        {
            var token = await GetOrRefreshTokenAsync(CancellationToken.None);
            if (token is not null)
                await sessionClient.AbortSessionAsync(baseUri, token, uploadSessionId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to abort upload session for file {FileId}.", fileId);
        }
    }

    private async Task<string?> GetOrRefreshTokenAsync(CancellationToken ct)
    {
        // Fast path: cached token is still valid.
        if (_cachedToken is { } t && DateTimeOffset.UtcNow < _tokenExpiresAt - TokenRefreshBuffer)
            return t;

        await _tokenLock.WaitAsync(ct);
        try
        {
            // Re-check after acquiring the lock — another task may have refreshed already.
            if (_cachedToken is { } t2 && DateTimeOffset.UtcNow < _tokenExpiresAt - TokenRefreshBuffer)
                return t2;

            var resp = await apiClient.GetTokenAsync(baseUri, new TokenApiRequest(agentId, clientSecret), ct);
            if (resp is null) return null;

            _cachedToken    = resp.AccessToken;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(resp.ExpiresInSeconds);
            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }
}

/// <summary>Work item enqueued by the tape run producer (hasher / clone runner).</summary>
internal sealed record UploadWorkItem(
    Guid FileId,
    Guid RunId,
    string FilePath,
    long FileSizeBytes,
    Guid? ExistingUploadSessionId = null);

/// <summary>
/// Bounded stream that limits reads to <paramref name="length"/> bytes and reports
/// each successful read to <paramref name="onBytesRead"/> for live progress tracking.
/// </summary>
internal sealed class ProgressStream(Stream inner, long length, Action<int>? onBytesRead = null) : Stream
{
    private readonly long _length    = length;
    private long          _remaining = length;

    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => _length;
    public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0) return 0;
        var toRead    = (int)Math.Min(count, _remaining);
        var bytesRead = inner.Read(buffer, offset, toRead);
        if (bytesRead > 0) { _remaining -= bytesRead; onBytesRead?.Invoke(bytesRead); }
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (_remaining <= 0) return 0;
        var toRead    = (int)Math.Min(count, _remaining);
        var bytesRead = await inner.ReadAsync(buffer, offset, toRead, ct);
        if (bytesRead > 0) { _remaining -= bytesRead; onBytesRead?.Invoke(bytesRead); }
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_remaining <= 0) return 0;
        var toRead    = (int)Math.Min(buffer.Length, _remaining);
        var bytesRead = await inner.ReadAsync(buffer[..toRead], ct);
        if (bytesRead > 0) { _remaining -= bytesRead; onBytesRead?.Invoke(bytesRead); }
        return bytesRead;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
