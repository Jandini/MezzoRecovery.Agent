namespace MezzoRecovery.Agent.Configuration;

public sealed class DeviceDiscoveryOptions
{
    public const string SectionName = "DeviceDiscovery";

    public bool Enabled { get; set; } = true;

    public int RefreshIntervalSeconds { get; set; } = 60;

    public bool IncludeStatusProbe { get; set; } = true;
}
