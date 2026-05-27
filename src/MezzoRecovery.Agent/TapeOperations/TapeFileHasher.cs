using System.Security.Cryptography;
using System.Threading.Channels;
using MezzoRecovery.Agent.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.TapeOperations;

/// <summary>
/// Singleton background worker that computes SHA-256 hashes for completed .tic files
/// and reports results to the API via the AgentHub.
/// After hashing, each file is forwarded to <see cref="TapeFileUploader"/>.
///
/// The worker is started when the first item is enqueued and runs until the
/// application shuts down. The hub reference is updated on each new connection so
/// hub calls always use the current connection.
/// </summary>
public sealed class TapeFileHasher(
    TapeFileUploader uploader,
    ILogger<TapeFileHasher> logger)
{
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
            await ProcessAsync(item);
        }
    }

    private async Task ProcessAsync(WorkItem item)
    {
        logger.LogInformation(
            "Hashing file {FileId} ({FilePath}).", item.FileId, item.FilePath);

        string? hashHex    = null;
        string? failReason = null;

        try
        {
            hashHex = await ComputeSha256HexAsync(item.FilePath);
        }
        catch (Exception ex)
        {
            failReason = ex.Message;
            logger.LogError(ex, "SHA-256 failed for file {FileId} ({FilePath}).", item.FileId, item.FilePath);
        }

        // Report to server (best-effort: log on failure but continue).
        var hub = _hub;
        if (hub is not null)
        {
            try
            {
                await hub.SendAsync("ReportTapeFileHashCompleted",
                    new TapeFileHashCompletedReport(
                        FileId:        item.FileId,
                        Succeeded:     hashHex is not null,
                        HashValue:     hashHex,
                        FailureReason: failReason is null ? null : "HashError",
                        FailureMessage: failReason));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "ReportTapeFileHashCompleted hub call failed for file {FileId}.", item.FileId);
            }
        }
        else
        {
            logger.LogWarning(
                "Hub not connected; skipping ReportTapeFileHashCompleted for file {FileId}.", item.FileId);
        }

        // Always forward to uploader, even if hash failed — the file may still be uploadable.
        uploader.Enqueue(new TapeFileUploader.WorkItem(
            FileId:           item.FileId,
            RunId:            item.RunId,
            FilePath:         item.FilePath,
            FileSizeBytes:    item.FileSizeBytes,
            UploadOperationId: item.UploadOperationId));
    }

    private static async Task<string> ComputeSha256HexAsync(string path)
    {
        await using var fs = File.OpenRead(path);
        var hashBytes = await SHA256.HashDataAsync(fs);
        return Convert.ToHexStringLower(hashBytes);
    }
}
