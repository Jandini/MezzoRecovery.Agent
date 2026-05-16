using System.CommandLine;
using System.Runtime.InteropServices;
using MezzoRecovery.Agent.Configuration;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.Commands.Update;

internal sealed class UpdateCommandHandler(ILoggerFactory loggerFactory)
{
    private const string DownloadBaseUrl = "https://mezzorecovery.com/agent";
    private const string ServiceName = "mra.service";
    private const string InstallDir = "/opt/mezzorecovery-agent";
    private const string BinaryName = "mra";
    private const string SymlinkPath = "/usr/local/bin/mra";

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

        var tmp = Path.GetTempFileName();
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

            if (!noRestart)
                RunSystemctl("stop", ServiceName, logger);

            File.Move(tmp, installPath, overwrite: true);
            File.SetUnixFileMode(installPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                             | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                             | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            if (File.Exists(SymlinkPath) || File.Exists(SymlinkPath + ".tmp"))
            {
                var tmpLink = SymlinkPath + ".new";
                File.CreateSymbolicLink(tmpLink, installPath);
                File.Move(tmpLink, SymlinkPath, overwrite: true);
            }

            if (!noRestart)
            {
                RunSystemctl("start", ServiceName, logger);
                logger.LogInformation("mra updated and service restarted.");
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

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        await using var stream = File.OpenRead(path);
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void RunSystemctl(string action, string service, ILogger logger)
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
            if (proc?.ExitCode != 0)
                logger.LogWarning("systemctl {Action} {Service} exited with code {Code}.", action, service, proc?.ExitCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "systemctl {Action} {Service} failed.", action, service);
        }
    }
}
