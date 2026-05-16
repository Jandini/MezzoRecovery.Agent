using System.CommandLine;
using System.Runtime.InteropServices;
using MezzoRecovery.Agent.Configuration;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.Commands.Update;

internal sealed class UpdateCommandHandler(ILoggerFactory loggerFactory)
{
    private const string DownloadBaseUrl = "https://mezzorecovery.com/agent";
    private const string ServiceName = "mra.service";
    private const string LegacyServiceName = "mezzorecovery-agent.service";
    private const string ServiceUnitPath = "/etc/systemd/system/mra.service";
    private const string InstallDir = "/opt/mezzorecovery-agent";
    private const string BinaryName = "mra";
    private const string SymlinkPath = "/usr/local/bin/mra";

    private static readonly string ServiceUnit = $"""
        [Unit]
        Description=MezzoRecovery Linux agent (mra)
        After=network-online.target
        Wants=network-online.target

        [Service]
        Type=simple
        ExecStart=/opt/mezzorecovery-agent/mra run --config /etc/mezzorecovery-agent/agent.json --credential /var/lib/mezzorecovery-agent/agent.credential
        Restart=on-failure
        RestartSec=5

        [Install]
        WantedBy=multi-user.target
        """;

    public static readonly Option<bool> NoRestartOption = new("--no-restart")
    {
        Description = "Download and replace the binary but do not restart the systemd service.",
    };

    public static void AddOptions(Command command) => command.Options.Add(NoRestartOption);

    public async Task<int> RunAsync(ParseResult parseResult, CancellationToken ct)
    {
        var noRestart = parseResult.GetValue(NoRestartOption);
        var logger = loggerFactory.CreateLogger<UpdateCommandHandler>();

        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("Update is only supported on Linux.");
            return 1;
        }

        if (Environment.IsPrivilegedProcess != true)
        {
            Console.Error.WriteLine("Update must run as root (use sudo).");
            return 1;
        }

        var rid = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "linux-x64",
            _ => null,
        };

        if (rid is null)
        {
            Console.Error.WriteLine($"Unsupported architecture: {RuntimeInformation.OSArchitecture}.");
            return 1;
        }

        var binFileName = $"{BinaryName}-{rid}";
        var checksumFileName = $"{binFileName}.sha256";
        var binUrl = $"{DownloadBaseUrl}/{binFileName}";
        var checksumUrl = $"{DownloadBaseUrl}/{checksumFileName}";
        var installPath = Path.Combine(InstallDir, BinaryName);

        // Temp file must be on the same filesystem as the target so File.Move
        // uses rename(2) rather than falling back to a byte-copy, which would
        // fail with ETXTBSY when the running binary is the update target.
        var tmp = Path.Combine(InstallDir, $".{BinaryName}.new");
        var tmpChecksum = Path.GetTempFileName();
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("mra-update/1.0");

            logger.LogInformation("Downloading {Url}...", binUrl);
            using (var binStream = await http.GetStreamAsync(binUrl, ct))
            using (var fileStream = File.Create(tmp))
                await binStream.CopyToAsync(fileStream, ct);

            logger.LogInformation("Downloading checksum...");
            using (var csStream = await http.GetStreamAsync(checksumUrl, ct))
            using (var csFile = File.Create(tmpChecksum))
                await csStream.CopyToAsync(csFile, ct);

            var expected = (await File.ReadAllTextAsync(tmpChecksum, ct)).Split(' ', 2)[0].Trim();
            var actual = await ComputeSha256Async(tmp, ct);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Checksum mismatch. Expected: {expected}  Got: {actual}");
                return 4;
            }

            logger.LogInformation("Checksum verified.");

            // Resolve which service name is active, trying current then legacy.
            var activeService = ResolveActiveService(logger);

            if (!noRestart && activeService is not null)
                RunSystemctl("stop", activeService, logger);

            // Set permissions on the temp file before the atomic rename,
            // so the live binary is never opened for writing.
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                     | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                     | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            // rename(2) - atomic on same filesystem, safe while mra is running.
            File.Move(tmp, installPath, overwrite: true);

            if (File.Exists(SymlinkPath) || File.Exists(SymlinkPath + ".tmp"))
            {
                var tmpLink = SymlinkPath + ".new";
                File.CreateSymbolicLink(tmpLink, installPath);
                File.Move(tmpLink, SymlinkPath, overwrite: true);
            }

            // If no service unit exists at all, write mra.service now.
            // This repairs installations where the service was never created,
            // or migrates from a legacy service name.
            if (activeService is null)
            {
                logger.LogInformation("No existing service unit found. Writing {Path}.", ServiceUnitPath);
                await File.WriteAllTextAsync(ServiceUnitPath, ServiceUnit, ct);
                RunSystemctl("daemon-reload", string.Empty, logger);
                RunSystemctl("enable", ServiceName, logger);
                activeService = ServiceName;
            }

            if (!noRestart)
            {
                RunSystemctl("start", activeService, logger);
                logger.LogInformation("mra updated and service {Service} started.", activeService);
            }
            else
            {
                logger.LogInformation("mra updated. Service not restarted (--no-restart).");
            }

            Console.WriteLine("Update complete.");
            return 0;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Download failed: {ex.Message}");
            return 2;
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            if (File.Exists(tmpChecksum)) File.Delete(tmpChecksum);
        }
    }

    private static string? ResolveActiveService(ILogger logger)
    {
        foreach (var name in new[] { ServiceName, LegacyServiceName })
        {
            var code = RunSystemctlExitCode("is-active", name);
            if (code == 0)
            {
                logger.LogInformation("Active service: {Service}.", name);
                return name;
            }

            // Exit code 3 = inactive (stopped but unit exists); still a valid target.
            if (code == 3)
            {
                logger.LogInformation("Service {Service} is inactive (stopped).", name);
                return name;
            }

            // Exit code 5 = unit not found; try next.
        }

        logger.LogInformation("No installed service unit found ({Service} or {Legacy}). A new unit will be written.", ServiceName, LegacyServiceName);
        return null;
    }

    private static int RunSystemctlExitCode(string action, string service)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("systemctl", $"{action} {service}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
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
            var args = string.IsNullOrEmpty(service) ? action : $"{action} {service}";
            var psi = new System.Diagnostics.ProcessStartInfo("systemctl", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(10_000);
            var code = proc?.ExitCode;
            if (code == 5)
            {
                logger.LogInformation("systemctl {Action} {Service}: unit not found (code 5) - skipping.", action, service);
            }
            else if (code != 0)
            {
                logger.LogWarning("systemctl {Action} {Service} exited with code {Code}.", action, service, code);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "systemctl {Action} {Service} failed.", action, service);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        await using var stream = File.OpenRead(path);
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
