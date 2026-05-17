using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.Tape.Models;

namespace MezzoRecovery.Agent.TapeJobs;

internal static class TapeJobProgressMapper
{
    public static TapeJobProgressMessage FromProgress(Guid jobId, TapeCloneProgress progress)
    {
        var stats = progress.Stats;
        return new TapeJobProgressMessage(
            jobId,
            stats.BytesProcessed,
            stats.BlocksProcessed,
            stats.FileMarksEncountered,
            stats.CurrentFileNumber,
            stats.CurrentBlockSizeBytes,
            MegabytesPerSecond(stats.BytesPerSecond),
            GigabytesPerHour(stats.BytesPerSecond),
            (long)stats.Elapsed.TotalSeconds,
            progress.Phase.ToString(),
            DateTimeOffset.UtcNow);
    }

    public static TapeJobProgressMessage FromStats(Guid jobId, TapeCloneStats stats, string phase) =>
        new(
            jobId,
            stats.BytesProcessed,
            stats.BlocksProcessed,
            stats.FileMarksEncountered,
            stats.CurrentFileNumber,
            stats.CurrentBlockSizeBytes,
            MegabytesPerSecond(stats.BytesPerSecond),
            GigabytesPerHour(stats.BytesPerSecond),
            (long)stats.Elapsed.TotalSeconds,
            phase,
            DateTimeOffset.UtcNow);

    private static double MegabytesPerSecond(double bytesPerSecond) => bytesPerSecond / 1_000_000.0;

    private static double GigabytesPerHour(double bytesPerSecond) => bytesPerSecond * 3600.0 / 1_000_000_000.0;
}
