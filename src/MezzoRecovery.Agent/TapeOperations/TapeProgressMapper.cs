using MezzoRecovery.Tape.Models;

namespace MezzoRecovery.Agent.TapeOperations;

internal static class TapeProgressMapper
{
    public static (long bytes, long blocks, long filemarks, double mbps, double gbph, long elapsedSec) Extract(TapeCloneStats stats) =>
        (
            ToLong(stats.BytesProcessed),
            ToLong(stats.BlocksProcessed),
            ToLong(stats.FileMarksEncountered),
            MegabytesPerSecond(stats.BytesPerSecond),
            GigabytesPerHour(stats.BytesPerSecond),
            (long)stats.Elapsed.TotalSeconds);

    private static long ToLong(ulong value) =>
        value > long.MaxValue ? long.MaxValue : (long)value;

    private static double MegabytesPerSecond(double bytesPerSecond) => bytesPerSecond / 1_000_000.0;

    private static double GigabytesPerHour(double bytesPerSecond) => bytesPerSecond * 3600.0 / 1_000_000_000.0;
}
