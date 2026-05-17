namespace MezzoRecovery.Agent.Configuration;

public sealed class TapeJobOptions
{
    public const string SectionName = "Agent:TapeJob";

    public int ProgressReportIntervalSeconds { get; set; } = 2;
}
