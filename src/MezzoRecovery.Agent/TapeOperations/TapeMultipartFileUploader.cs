using MezzoRecovery.Agent.Api;
using MezzoRecovery.Agent.Contracts;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;

namespace MezzoRecovery.Agent.TapeOperations;

/// <summary>
/// Uploads a single .tic file to S3-compatible object storage using the multipart
/// upload session API. Handles resume, part-level retry, and stall detection.
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
    ILogger<TapeMultipartFileUploader> logger)
{
    private const int MaxPartRetries = 10;
    private const int MaxConcurrentPartsPerFile = 4;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(120);

    private long _uploadedBytes;
    private long _totalBytes;
    private bool _done;

    public long GetUploadedBytes() => Interlocked.Read(ref _uploadedBytes);
    public long GetTotalBytes() => _totalBytes;
    public Guid FileId { get; private set; }
    public Guid RunId { get; private set; }
    public bool IsDone => _done;

    public async Task UploadAsync(
        UploadWorkItem workItem,
        CancellationToken ct)
    {
        FileId = workItem.FileId;
        RunId = workItem.RunId;
        _totalBytes = workItem.FileSizeBytes;

        var token = await GetTokenAsync(ct);
        if (token is null)
        {
            logger.LogError("Could not obtain API token for upload of file {FileId}.", workItem.FileId);
            return;
        }

        // 1. Start or resume the upload session.
        var sessionReq = new StartUploadSessionApiRequest(
            FileName: Path.GetFileName(workItem.FilePath),
            TotalBytes: workItem.FileSizeBytes,
            ContentType: "application/octet-stream",
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
            session.UploadSessionId,
            session.TotalParts,
            session.PartSizeBytes / 1_048_576,
            workItem.FileId);

        // 2. Reconcile local checkpoint with server completed parts.
        var checkpoint = await checkpointStore.LoadAsync(workItem.RunId, workItem.FileId, ct)
                         ?? new UploadCheckpoint
                         {
                             RunId = workItem.RunId,
                             FileId = workItem.FileId,
                             UploadSessionId = session.UploadSessionId,
                             FilePath = workItem.FilePath,
                             FileName = Path.GetFileName(workItem.FilePath),
                             FileSizeBytes = workItem.FileSizeBytes,
                             PartSizeBytes = session.PartSizeBytes,
                         };

        // Merge: server is authoritative for ETags.
        var serverCompleted = session.CompletedParts
            .ToDictionary(p => p.PartNumber, p => p.ETag);

        var localCompleted = checkpoint.CompletedParts
            .ToDictionary(p => p.PartNumber, p => p.ETag);

        // Union (server wins for shared parts).
        var allCompleted = new Dictionary<int, string>(localCompleted);
        foreach (var (pn, etag) in serverCompleted)
            allCompleted[pn] = etag;

        // Update checkpoint with merged state.
        checkpoint.CompletedParts = allCompleted
            .Select(kv => new CheckpointPartDto { PartNumber = kv.Key, ETag = kv.Value })
            .ToArray();
        checkpoint.UploadSessionId = session.UploadSessionId;
        checkpoint.PartSizeBytes = session.PartSizeBytes;

        // Count already-uploaded bytes for accurate progress reporting.
        var completedBytes = (long)allCompleted.Count * session.PartSizeBytes;
        Interlocked.Exchange(ref _uploadedBytes, Math.Min(completedBytes, workItem.FileSizeBytes));

        // 3. Determine which parts are still missing.
        var totalParts = session.TotalParts;
        var missingParts = Enumerable.Range(1, totalParts)
            .Where(p => !allCompleted.ContainsKey(p))
            .ToList();

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

            // 4. Upload missing parts in parallel.
            var perFileSemaphore = new SemaphoreSlim(MaxConcurrentPartsPerFile);
            var partTasks = missingParts
                .Select(partNumber => UploadPartWithRetryAsync(
                    workItem, session, partNumber, checkpoint, allCompleted,
                    perFileSemaphore, token, ct))
                .ToList();

            await Task.WhenAll(partTasks);

            // Verify all parts are now complete.
            if (allCompleted.Count != totalParts)
            {
                logger.LogError(
                    "Upload incomplete: {Completed}/{Total} parts for file {FileId}.",
                    allCompleted.Count, totalParts, workItem.FileId);
                return;
            }
        }

        // 5. Complete the session.
        var result = await sessionClient.CompleteSessionAsync(
            baseUri, token, session.UploadSessionId, ct);

        if (result is not null)
        {
            logger.LogInformation(
                "Upload completed for file {FileId}. Object: {ObjectKey} Size: {Size} bytes.",
                workItem.FileId, result.ObjectKey, result.TotalBytes);
            checkpointStore.Delete(workItem.RunId, workItem.FileId);
        }
        else
        {
            logger.LogError(
                "CompleteSession call failed for upload session {SessionId}.", session.UploadSessionId);
        }

        _done = true;
    }

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

                try
                {
                    var etag = await UploadPartOnceAsync(workItem, session, partNumber, token, ct);
                    if (etag is null)
                    {
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

                    // Report part completion to API.
                    var offset = (long)(partNumber - 1) * session.PartSizeBytes;
                    var length = Math.Min(session.PartSizeBytes, workItem.FileSizeBytes - offset);
                    var currentToken = await GetTokenAsync(ct) ?? token;

                    await sessionClient.CompletePartAsync(
                        baseUri, currentToken, session.UploadSessionId, partNumber,
                        new CompletePartApiRequest(etag, length), ct);

                    // Update local tracking.
                    allCompleted[partNumber] = etag;
                    Interlocked.Add(ref _uploadedBytes, length);

                    // Save checkpoint.
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
                    throw;
                }
                catch (Exception ex)
                {
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
        CancellationToken ct)
    {
        // Request a signed URL for this part.
        var offset = (long)(partNumber - 1) * session.PartSizeBytes;
        var length = Math.Min(session.PartSizeBytes, workItem.FileSizeBytes - offset);

        var signResp = await sessionClient.SignPartsAsync(
            baseUri, token, session.UploadSessionId,
            new SignPartsApiRequest(
                [new PartToSignDto(partNumber, offset, length)]),
            ct);

        if (signResp is null || signResp.Parts.Length == 0)
        {
            logger.LogWarning("Failed to sign part {Part} for file {FileId}.", partNumber, workItem.FileId);
            return null;
        }

        var signedPart = signResp.Parts[0];

        // Open a read-only FileStream positioned at the part offset.
        await using var fs = new FileStream(
            workItem.FilePath,
            FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        fs.Seek(offset, SeekOrigin.Begin);

        // Wrap to prevent reading beyond the part boundary.
        using var bounded = new BoundedStream(fs, length);

        // PUT to the signed URL with stall detection.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(StallTimeout + TimeSpan.FromSeconds(length / (1024 * 1024) * 10));

        var request = new HttpRequestMessage(HttpMethod.Put, signedPart.UploadUrl);
        request.Content = new StreamContent(bounded);
        request.Content.Headers.ContentLength = length;
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

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

            // ETag is returned by the storage provider in the response header.
            var etag = response.Headers.ETag?.Tag
                       ?? (response.Headers.TryGetValues("ETag", out var vals)
                           ? vals.FirstOrDefault()
                           : null);

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
                "The storage endpoint is responding with non-TLS data. " +
                "Verify that TapeObjectStorage:PublicEndpoint uses the correct scheme (http vs https).",
                partNumber, workItem.FileId,
                request.RequestUri?.Scheme, request.RequestUri?.Host);
            throw;
        }
    }

    private async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        var resp = await apiClient.GetTokenAsync(
            baseUri,
            new TokenApiRequest(agentId, clientSecret),
            ct);

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

/// <summary>Stream wrapper that prevents reads beyond a fixed byte count.</summary>
internal sealed class BoundedStream(Stream inner, long length) : Stream
{
    private readonly long _length = length;
    private long _remaining = length;

    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => _length;
    public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0) return 0;
        var toRead = (int)Math.Min(count, _remaining);
        var bytesRead = inner.Read(buffer, offset, toRead);
        _remaining -= bytesRead;
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (_remaining <= 0) return 0;
        var toRead = (int)Math.Min(count, _remaining);
        var bytesRead = await inner.ReadAsync(buffer, offset, toRead, ct);
        _remaining -= bytesRead;
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_remaining <= 0) return 0;
        var toRead = (int)Math.Min(buffer.Length, _remaining);
        var bytesRead = await inner.ReadAsync(buffer[..toRead], ct);
        _remaining -= bytesRead;
        return bytesRead;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
