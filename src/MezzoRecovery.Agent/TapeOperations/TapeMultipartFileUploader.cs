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
///   GetUploadedBytes()   = completed-part bytes + bytes currently in-flight across all active part PUTs
///   ComputeThroughputSnapshot() = interval-based EWMA throughput; call from heartbeat timer (~1 s)
///   OnPartCompleted      = fires immediately when a part finishes, with per-part measured throughput
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

    // EWMA-smoothed throughput in bytes/second. Written only by ComputeThroughputSnapshot.
    private long _smoothedThroughput;

    private long _totalBytes;
    private bool _done;

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

        var smoothed = (long)(_smoothedThroughput * 0.7 + instantRate * 0.3);
        _smoothedThroughput = smoothed;

        return smoothed > 100 ? smoothed : null;
    }

    /// <summary>
    /// Fired immediately after each part upload completes.
    /// Args: (totalBytesUploaded, throughputBytesPerSecond).
    /// Set before calling UploadAsync. Fire-and-forget — caller swallows exceptions.
    /// </summary>
    public Func<long, long, Task>? OnPartCompleted { get; set; }

    // ── Upload entry point ────────────────────────────────────────────────────

    public async Task UploadAsync(UploadWorkItem workItem, CancellationToken ct)
    {
        FileId      = workItem.FileId;
        RunId       = workItem.RunId;
        _totalBytes = workItem.FileSizeBytes;

        var token = await GetTokenAsync(ct);
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

        var serverCompleted = session.CompletedParts.ToDictionary(p => p.PartNumber, p => p.ETag);
        var localCompleted  = checkpoint.CompletedParts.ToDictionary(p => p.PartNumber, p => p.ETag);

        var allCompleted = new Dictionary<int, string>(localCompleted);
        foreach (var (pn, etag) in serverCompleted)
            allCompleted[pn] = etag;

        checkpoint.CompletedParts   = allCompleted.Select(kv => new CheckpointPartDto { PartNumber = kv.Key, ETag = kv.Value }).ToArray();
        checkpoint.UploadSessionId  = session.UploadSessionId;
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

            var perFileSemaphore = new SemaphoreSlim(maxConcurrentPartsPerFile);
            var partTasks = missingParts
                .Select(partNumber => UploadPartWithRetryAsync(
                    workItem, session, partNumber, checkpoint, allCompleted,
                    perFileSemaphore, token, ct))
                .ToList();

            await Task.WhenAll(partTasks);

            if (allCompleted.Count != totalParts)
            {
                logger.LogError(
                    "Upload incomplete: {Completed}/{Total} parts for file {FileId}.",
                    allCompleted.Count, totalParts, workItem.FileId);
                return;
            }
        }

        // ── Complete session ──────────────────────────────────────────────────

        var result = await sessionClient.CompleteSessionAsync(baseUri, token, session.UploadSessionId, ct);
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

    // ── Part upload ───────────────────────────────────────────────────────────

    private async Task UploadPartWithRetryAsync(
        UploadWorkItem workItem,
        StartUploadSessionApiResponse session,
        int partNumber,
        UploadCheckpoint checkpoint,
        Dictionary<int, string> allCompleted,
        SemaphoreSlim perFileSemaphore,
        string token,
        CancellationToken ct)
    {
        await perFileSemaphore.WaitAsync(ct);
        await globalPartSemaphore.WaitAsync(ct);
        try
        {
            for (int attempt = 1; attempt <= MaxPartRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                // Reset in-flight counter for this attempt (covers retries too).
                _partAttemptBytes[partNumber] = 0;
                long attemptBytes = 0;

                // Callback: called by ProgressStream on every network read.
                // Updates per-part in-flight bytes and the interval accumulator.
                void OnBytes(int n)
                {
                    attemptBytes += n;
                    _partAttemptBytes[partNumber] = attemptBytes;
                    Interlocked.Add(ref _bytesInCurrentInterval, n);
                }

                try
                {
                    var partStartMs = Environment.TickCount64;
                    var etag = await UploadPartOnceAsync(workItem, session, partNumber, token, OnBytes, ct);
                    if (etag is null)
                    {
                        _partAttemptBytes[partNumber] = 0;
                        if (attempt < MaxPartRetries)
                        {
                            var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, attempt)));
                            logger.LogWarning(
                                "Part {Part} of file {FileId} failed attempt {Attempt}/{Max}. Retrying in {Delay}s.",
                                partNumber, workItem.FileId, attempt, MaxPartRetries, delay.TotalSeconds);
                            await Task.Delay(delay, ct);
                        }
                        continue;
                    }

                    // ── Part succeeded ────────────────────────────────────────

                    var offset = (long)(partNumber - 1) * session.PartSizeBytes;
                    var length = Math.Min(session.PartSizeBytes, workItem.FileSizeBytes - offset);
                    var currentToken = await GetTokenAsync(ct) ?? token;

                    await sessionClient.CompletePartAsync(
                        baseUri, currentToken, session.UploadSessionId, partNumber,
                        new CompletePartApiRequest(etag, length), ct);

                    // Move bytes from in-flight → completed.
                    _partAttemptBytes.TryRemove(partNumber, out _);
                    Interlocked.Add(ref _completedBytes, length);

                    // Per-part throughput for the immediate push (excludes signing overhead
                    // on large parts, which is negligible).
                    var partElapsedSec = Math.Max(0.001, (Environment.TickCount64 - partStartMs) / 1000.0);
                    var partThroughput = (long)(length / partElapsedSec);

                    // Blend into the EWMA so the heartbeat starts from a good value.
                    _smoothedThroughput = (long)(_smoothedThroughput * 0.7 + partThroughput * 0.3);

                    // Save checkpoint.
                    allCompleted[partNumber] = etag;
                    checkpoint.CompletedParts = allCompleted
                        .Select(kv => new CheckpointPartDto { PartNumber = kv.Key, ETag = kv.Value })
                        .ToArray();
                    await checkpointStore.SaveAsync(checkpoint, ct);

                    logger.LogDebug(
                        "Part {Part}/{Total} completed for file {FileId}. ETag: {ETag}",
                        partNumber, session.TotalParts, workItem.FileId, etag);

                    // Immediate progress push on part completion.
                    var callback = OnPartCompleted;
                    if (callback is not null)
                        _ = Task.Run(() => callback(GetUploadedBytes(), partThroughput));

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

                    var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, attempt)));
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
        StartUploadSessionApiResponse session,
        int partNumber,
        string token,
        Action<int> onBytesRead,
        CancellationToken ct)
    {
        var offset = (long)(partNumber - 1) * session.PartSizeBytes;
        var length = Math.Min(session.PartSizeBytes, workItem.FileSizeBytes - offset);

        var signResp = await sessionClient.SignPartsAsync(
            baseUri, token, session.UploadSessionId,
            new SignPartsApiRequest([new PartToSignDto(partNumber, offset, length)]),
            ct);

        if (signResp is null || signResp.Parts.Length == 0)
        {
            logger.LogWarning("Failed to sign part {Part} for file {FileId}.", partNumber, workItem.FileId);
            return null;
        }

        var signedPart = signResp.Parts[0];

        await using var fs = new FileStream(
            workItem.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        fs.Seek(offset, SeekOrigin.Begin);

        // ProgressStream reports each read to the caller so in-flight bytes stay current.
        using var progress = new ProgressStream(fs, length, onBytesRead);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(StallTimeout + TimeSpan.FromSeconds(length / (1024 * 1024) * 10));

        var request = new HttpRequestMessage(HttpMethod.Put, signedPart.UploadUrl);
        request.Content = new StreamContent(progress);
        request.Content.Headers.ContentLength = length;
        request.Content.Headers.ContentType   = new MediaTypeHeaderValue("application/octet-stream");

        try
        {
            using var response = await partUploadHttpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                logger.LogWarning(
                    "Part {Part} of file {FileId} PUT returned 403. MinIO error: {Body}",
                    partNumber, workItem.FileId, body);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                logger.LogWarning(
                    "Part {Part} of file {FileId} PUT returned {StatusCode}. Body: {Body}",
                    partNumber, workItem.FileId, (int)response.StatusCode, body);
                return null;
            }

            var etag = response.Headers.ETag?.Tag
                       ?? (response.Headers.TryGetValues("ETag", out var vals) ? vals.FirstOrDefault() : null);
            return etag;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
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

    private async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        var resp = await apiClient.GetTokenAsync(baseUri, new TokenApiRequest(agentId, clientSecret), ct);
        return resp?.AccessToken;
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
