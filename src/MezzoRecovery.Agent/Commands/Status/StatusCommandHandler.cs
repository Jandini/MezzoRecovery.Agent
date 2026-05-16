using System.CommandLine;
using MezzoRecovery.Agent.Configuration;

namespace MezzoRecovery.Agent.Commands.Status;

internal sealed class StatusCommandHandler
{
    public static readonly Option<string?> Credential = new("--credential")
    {
        Description = $"Path to credential file (default: {AgentPaths.DefaultCredentialPath}).",
    };

    public static void AddOptions(Command command) => command.Options.Add(Credential);

    public Task<int> RunAsync(ParseResult parseResult, CancellationToken ct)
    {
        _ = ct;
        var credentialPath = parseResult.GetValue(Credential) ?? AgentPaths.DefaultCredentialPath;

        if (!File.Exists(credentialPath))
        {
            Console.WriteLine("Not enrolled (no credential file).");
            return Task.FromResult(1);
        }

        Console.WriteLine($"Enrolled. Credential file: {credentialPath}");
        return Task.FromResult(0);
    }
}
