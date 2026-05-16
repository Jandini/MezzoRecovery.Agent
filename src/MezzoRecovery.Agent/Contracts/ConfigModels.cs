namespace MezzoRecovery.Agent.Contracts;

public sealed class AgentConfigFile
{
    public string ApiBaseUrl { get; set; } = string.Empty;
}

public sealed class AgentCredentialFile
{
    public Guid AgentId { get; set; }

    public string ClientSecret { get; set; } = string.Empty;
}
