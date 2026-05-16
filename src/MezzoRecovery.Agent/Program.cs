using System.Reflection;
using MezzoRecovery.Agent.Api;
using MezzoRecovery.Agent.Configuration;
using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.Agent.Identity;
using MezzoRecovery.Agent.Runtime;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent;

internal static class Program
{
    private static string? GetOpt(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static string ResolveConfigPath(string[] args) =>
        GetOpt(args, "--config")
        ?? Environment.GetEnvironmentVariable("MEZZO_AGENT_CONFIG")
        ?? AgentPaths.DefaultConfigPath;

    private static string ResolveCredentialPath(string[] args) =>
        GetOpt(args, "--credential") ?? AgentPaths.DefaultCredentialPath;

    private static string ResolveMachineIdPath(string[] args) =>
        GetOpt(args, "--machine-id") ?? AgentPaths.DefaultMachineIdPath;

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            mezzorecovery-agent — MezzoRecovery Linux agent (Stage 3)

            Usage:
              mezzorecovery-agent enroll <CODE> [--config PATH] [--credential PATH] [--machine-id PATH]
              mezzorecovery-agent run [--config PATH] [--credential PATH]
              mezzorecovery-agent status [--credential PATH]
              mezzorecovery-agent version

            Environment:
              MEZZO_AGENT_CONFIG   Overrides default config path (/etc/mezzorecovery-agent/agent.json)
            """);
    }

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args.Any(a => a is "-h" or "--help" or "help"))
            {
                PrintHelp();
                return args.Length == 0 ? 1 : 0;
            }

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            var verb = args[0].ToLowerInvariant();
            return verb switch
            {
                "enroll" => await EnrollMainAsync(args, cts.Token),
                "status" => await StatusMainAsync(args, cts.Token),
                "version" or "--version" => VersionMain(),
                "run" => await RunMainAsync(args, cts.Token),
                _ => PrintHelpReturn(),
            };
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 10;
        }
    }

    private static async Task<int> EnrollMainAsync(string[] args, CancellationToken ct)
    {
        var code = args.SkipWhile(a => !string.Equals(a, "enroll", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(code) || code.StartsWith('-'))
        {
            PrintHelp();
            return 1;
        }

        var configPath = ResolveConfigPath(args);
        var credentialPath = ResolveCredentialPath(args);
        var machineIdPath = ResolveMachineIdPath(args);

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
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                     ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
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

    private static Task<int> StatusMainAsync(string[] args, CancellationToken ct)
    {
        _ = ct;
        var credentialPath = ResolveCredentialPath(args);
        if (!File.Exists(credentialPath))
        {
            Console.WriteLine("Not enrolled (no credential file).");
            return Task.FromResult(1);
        }

        Console.WriteLine($"Enrolled. Credential file: {credentialPath}");
        return Task.FromResult(0);
    }

    private static int VersionMain()
    {
        var v = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "0.0.0";
        Console.WriteLine(v);
        return 0;
    }

    private static async Task<int> RunMainAsync(string[] args, CancellationToken ct)
    {
        using var loggerFactory =
            LoggerFactory.Create(b => b.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            }));
        var log = loggerFactory.CreateLogger<AgentConnectionLoop>();

        var configPath = ResolveConfigPath(args);
        var credentialPath = ResolveCredentialPath(args);
        var loop = new AgentConnectionLoop(configPath, credentialPath, log);
        await loop.RunAsync(ct);
        return 0;
    }

    private static int PrintHelpReturn()
    {
        PrintHelp();
        return 1;
    }
}
