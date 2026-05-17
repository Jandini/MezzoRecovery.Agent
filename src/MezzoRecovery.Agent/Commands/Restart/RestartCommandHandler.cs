using System.CommandLine;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.Commands.Restart;

internal sealed class RestartCommandHandler(ILoggerFactory loggerFactory)
{
    private const string ServiceName = "mra.service";
    private const string LegacyServiceName = "mezzorecovery-agent.service";

    public static void AddOptions(Command _) { }

    public Task<int> RunAsync(ParseResult _, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger<RestartCommandHandler>();

        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("Restart is only supported on Linux.");
            return Task.FromResult(1);
        }

        if (Environment.IsPrivilegedProcess != true)
        {
            Console.Error.WriteLine("Restart must run as root (use sudo).");
            return Task.FromResult(1);
        }

        var service = ResolveActiveService(logger);
        if (service is null)
        {
            Console.Error.WriteLine($"No installed service unit found ({ServiceName} or {LegacyServiceName}).");
            return Task.FromResult(1);
        }

        logger.LogInformation("Restarting {Service}.", service);
        RunSystemctl("restart", service, logger);
        Console.WriteLine($"Service {service} restart requested.");
        return Task.FromResult(0);
    }

    private static string? ResolveActiveService(ILogger logger)
    {
        foreach (var name in new[] { ServiceName, LegacyServiceName })
        {
            var code = RunSystemctlExitCode("is-active", name);
            if (code is 0 or 3)
            {
                logger.LogInformation("Found service: {Service}.", name);
                return name;
            }
        }

        return null;
    }

    private static int RunSystemctlExitCode(string action, string service)
    {
        try
        {
            var psi = new ProcessStartInfo("systemctl", $"{action} {service}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(10_000);
            return proc?.ExitCode ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    private static void RunSystemctl(string action, string service, ILogger logger)
    {
        try
        {
            var psi = new ProcessStartInfo("systemctl", $"{action} {service}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(15_000);
            var code = proc?.ExitCode;
            if (code != 0)
                logger.LogWarning("systemctl {Action} {Service} exited with code {Code}.", action, service, code);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to run systemctl {Action} {Service}.", action, service);
        }
    }
}
