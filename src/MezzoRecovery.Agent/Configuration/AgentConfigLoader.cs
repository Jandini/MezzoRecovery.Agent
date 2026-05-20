using System.Text.Json;
using MezzoRecovery.Agent.Contracts;

namespace MezzoRecovery.Agent.Configuration;

public static class AgentPaths
{
    public const string DefaultConfigPath = "/etc/mezzorecovery-agent/agent.json";
    public const string DefaultCredentialPath = "/var/lib/mezzorecovery-agent/agent.credential";
    public const string DefaultMachineIdPath = "/var/lib/mezzorecovery-agent/machine.id";
    public const string DefaultLockPath = "/run/mezzorecovery-agent.lock";
    public const string DefaultCacheDirectory = "/opt/mezzorecovery-cache";
}

public static class AgentConfigLoader
{
    public static async Task<AgentConfigFile> LoadAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var cfg = await JsonSerializer.DeserializeAsync(stream, AgentJsonContext.Default.AgentConfigFile, ct)
                  ?? throw new InvalidOperationException($"Invalid agent config: {path}");
        if (string.IsNullOrWhiteSpace(cfg.ApiBaseUrl))
            throw new InvalidOperationException("agent.json must set apiBaseUrl.");
        return cfg;
    }
}
