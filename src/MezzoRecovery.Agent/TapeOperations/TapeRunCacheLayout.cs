namespace MezzoRecovery.Agent.TapeOperations;

/// <summary>
/// Centralises the on-disk directory/filename conventions for a tape run.
/// <para>
/// Layout: <c>{cacheDirectory}/runs/{runId}/segment.{fileNumber-1:D4}.tic</c>
/// </para>
/// The segment index is 0-based (matching <c>SegmentedWriteStream</c>) while
/// <c>TapeCloneStats.CurrentFileNumber</c> is 1-based, so every call subtracts 1.
/// </summary>
internal static class TapeRunCacheLayout
{
    public static string GetRunDirectory(string cacheDirectory, Guid runId) =>
        Path.Combine(cacheDirectory, "runs", runId.ToString());

    /// <param name="fileNumber">1-based file number from <c>TapeCloneStats.CurrentFileNumber</c>.</param>
    public static string GetFilePath(string cacheDirectory, Guid runId, int fileNumber) =>
        Path.Combine(GetRunDirectory(cacheDirectory, runId), $"segment.{fileNumber - 1:D4}.tic");

    /// <summary>
    /// Deletes the run directory and all its .tic segments. Best-effort; errors are swallowed.
    /// </summary>
    public static void TryDeleteRunDirectory(string cacheDirectory, Guid runId)
    {
        try
        {
            var dir = GetRunDirectory(cacheDirectory, runId);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }
}
