using System.CommandLine;
using MezzoRecovery.Agent.Configuration;
using MezzoRecovery.Agent.Devices;
using MezzoRecovery.Agent.Runtime;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.Commands.Run;

internal sealed class RunCommandHandler(
    ILoggerFactory loggerFactory,
    TapeDeviceDiscoveryService deviceDiscovery,
    DeviceDiscoveryOptions discoveryOptions)
{
    public static readonly Option<string?> Config = new("--config")
    {
        Description = $"Path to agent config file (default: {AgentPaths.DefaultConfigPath}).",
    };

    public static readonly Option<string?> Credential = new("--credential")
    {
        Description = $"Path to credential file (default: {AgentPaths.DefaultCredentialPath}).",
    };

    public static void AddOptions(Command command)
    {
        command.Options.Add(Config);
        command.Options.Add(Credential);
    }

    public async Task<int> RunAsync(ParseResult parseResult, CancellationToken ct)
    {
        var configPath = parseResult.GetValue(Config) ?? AgentPaths.DefaultConfigPath;
        var credentialPath = parseResult.GetValue(Credential) ?? AgentPaths.DefaultCredentialPath;

        var loop = new AgentConnectionLoop(
            configPath,
            credentialPath,
            deviceDiscovery,
            discoveryOptions,
            loggerFactory.CreateLogger<AgentConnectionLoop>());
        await loop.RunAsync(ct);
        return 0;
    }
}
