namespace MezzoRecovery.Agent.Configuration;

public sealed class TapeDeviceStatusOptions
{
    public const string SectionName = "TapeDeviceStatus";

    /// <summary>
    /// How often the live status poller probes idle devices. The poll skips any
    /// device that currently has an active tape operation (read/rewind/eject/space),
    /// so it never contends with an in-flight read.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 5;
}
