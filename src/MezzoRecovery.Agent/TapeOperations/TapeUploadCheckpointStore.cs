using MezzoRecovery.Agent.Contracts;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace MezzoRecovery.Agent.TapeOperations;

/// <summary>
/// Persists upload progress for a single file to a JSON checkpoint file so that
/// agent restart does not lose knowledge of which S3 parts are already completed.
/// Path: <c>{cacheDirectory}/runs/{runId}/uploads/{fileId}.upload.json</c>
/// </summary>
internal sealed class TapeUploadCheckpointStore(string cacheDirectory, ILogger<TapeUploadCheckpointStore> logger)
{
    private string CheckpointPath(Guid runId, Guid fileId) =>
        Path.Combine(cacheDirectory, "runs", runId.ToString(), "uploads", $"{fileId}.upload.json");

    public async Task<UploadCheckpoint?> LoadAsync(Guid runId, Guid fileId, CancellationToken ct)
    {
        var path = CheckpointPath(runId, fileId);
        if (!File.Exists(path))
            return null;

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync(
                stream, AgentJsonContext.Default.UploadCheckpoint, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to load upload checkpoint for file {FileId} run {RunId}. Starting fresh.",
                fileId, runId);
            return null;
        }
    }

    public async Task SaveAsync(UploadCheckpoint checkpoint, CancellationToken ct)
    {
        var path = CheckpointPath(checkpoint.RunId, checkpoint.FileId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        checkpoint.UpdatedAtUtc = DateTimeOffset.UtcNow;

        var tmp = path + ".tmp";
        try
        {
            await using var stream = File.Create(tmp);
            await JsonSerializer.SerializeAsync(
                stream, checkpoint, AgentJsonContext.Default.UploadCheckpoint, ct);
            await stream.FlushAsync(ct);

            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to save upload checkpoint for file {FileId} run {RunId}.",
                checkpoint.FileId, checkpoint.RunId);
            try { File.Delete(tmp); } catch { }
        }
    }

    public void Delete(Guid runId, Guid fileId)
    {
        var path = CheckpointPath(runId, fileId);
        try { File.Delete(path); } catch { /* best-effort */ }
    }
}
