namespace MezzoRecovery.Agent.Configuration;

public sealed class TapeOperationOptions
{
    public const string SectionName = "Agent:TapeOperation";

    /// <summary>How often the agent emits a TapeOperationProgress message.</summary>
    public int ProgressReportIntervalSeconds { get; set; } = 2;
}
