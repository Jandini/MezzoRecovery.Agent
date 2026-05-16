using System.CommandLine;
using System.Reflection;
using MezzoRecovery.Agent.Api;
using MezzoRecovery.Agent.Configuration;
using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.Agent.Identity;

namespace MezzoRecovery.Agent.Commands.Enroll;

internal sealed class EnrollCommandHandler
{
    public static readonly Argument<string> Code = new("code")
    {
        Description = "Short-lived enrollment code generated in the MezzoRecovery UI.",
    };

    public static readonly Option<string?> Config = new("--config")
    {
        Description = $"Path to agent config file (default: {AgentPaths.DefaultConfigPath}).",
    };

    public static readonly Option<string?> Credential = new("--credential")
    {
        Description = $"Path to credential file (default: {AgentPaths.DefaultCredentialPath}).",
    };

    public static readonly Option<string?> MachineId = new("--machine-id")
    {
        Description = $"Path to machine-id file (default: {AgentPaths.DefaultMachineIdPath}).",
    };

    public static void AddArguments(Command command) => command.Arguments.Add(Code);

    public static void AddOptions(Command command)
    {
        command.Options.Add(Config);
        command.Options.Add(Credential);
        command.Options.Add(MachineId);
    }

    public async Task<int> RunAsync(ParseResult parseResult, CancellationToken ct)
    {
        var code = parseResult.GetValue(Code)!;
        var configPath = parseResult.GetValue(Config) ?? AgentPaths.DefaultConfigPath;
        var credentialPath = parseResult.GetValue(Credential) ?? AgentPaths.DefaultCredentialPath;
        var machineIdPath = parseResult.GetValue(MachineId) ?? AgentPaths.DefaultMachineIdPath;

        if (File.Exists(credentialPath))
        {
            Console.Error.WriteLine("Credential file already exists. Refusing to enroll again.");
            return 2;
        }

        using var http = new HttpClient();
        var cfg = await AgentConfigLoader.LoadAsync(configPath, ct);
        var baseUri = new Uri(cfg.ApiBaseUrl.TrimEnd('/') + "/");
        var api = new AgentApiClient(http);
        var machineId = await MachineIdStore.GetOrCreateAsync(machineIdPath, ct);
        var fingerprintMaterial = $"{machineId}|{Environment.MachineName}";
        var version = typeof(EnrollCommandHandler).Assembly
                          .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? typeof(EnrollCommandHandler).Assembly.GetName().Version?.ToString()
                      ?? "0.0.0";

        var req = new EnrollApiRequest(
            code.Trim(),
            fingerprintMaterial,
            Environment.MachineName,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            version);

        var resp = await api.EnrollAsync(baseUri, req, ct);
        if (resp is null)
        {
            Console.Error.WriteLine("Enrollment failed (invalid or expired code).");
            return 3;
        }

        await CredentialStore.SaveAsync(credentialPath, resp.AgentId, resp.ClientSecret, ct);
        Console.WriteLine("Enrollment succeeded. Start the service with: mezzorecovery-agent run");
        return 0;
    }
}
