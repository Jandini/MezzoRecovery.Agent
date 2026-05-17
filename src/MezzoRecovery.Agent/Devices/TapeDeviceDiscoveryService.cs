using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.TapeDrive.Abstractions;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.Devices;

public sealed class TapeDeviceDiscoveryService(
    ITapeDriveEnumerator enumerator,
    ILogger<TapeDeviceDiscoveryService> logger)
{
    public IReadOnlyList<AgentTapeDeviceDto> DiscoverDevices()
    {
        logger.LogInformation("Tape device discovery started.");

        try
        {
            var drives = enumerator.GetTapeDrives();
            var result = new List<AgentTapeDeviceDto>(drives.Count);

            foreach (var drive in drives)
            {
                var devicePath = drive.DevicePath;
                if (devicePath is null)
                    continue;

                var nonRewinding = drive.LinuxDevices
                    .FirstOrDefault(d => d.StartsWith("nst", StringComparison.Ordinal) && IsBaseDeviceName(d, "nst"));
                var rewinding = drive.LinuxDevices
                    .FirstOrDefault(d => d.StartsWith("st", StringComparison.Ordinal) && !d.StartsWith("nst", StringComparison.Ordinal) && IsBaseDeviceName(d, "st"));

                var nonRewindingPath = nonRewinding is not null ? "/dev/" + nonRewinding : null;
                var rewindingPath = rewinding is not null ? "/dev/" + rewinding : null;

                var stableKey = BuildStableKey(devicePath, nonRewindingPath);

                var accessible = CheckAccessible(devicePath);
                var status = accessible ? AgentTapeDeviceStatus.Present : AgentTapeDeviceStatus.Unavailable;

                result.Add(new AgentTapeDeviceDto
                {
                    StableDeviceKey = stableKey,
                    LinuxDevicePath = devicePath,
                    NonRewindingDevicePath = nonRewindingPath,
                    RewindingDevicePath = rewindingPath,
                    Vendor = NullIfEmpty(drive.Vendor),
                    Model = NullIfEmpty(drive.Model),
                    Revision = NullIfEmpty(drive.Revision),
                    Status = status,
                    IsPresent = true,
                    IsAccessible = accessible,
                });
            }

            logger.LogInformation("Tape device discovery completed: {Count} device(s) found.", result.Count);
            return result;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Permission denied during tape device discovery.");
            return [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tape device discovery failed.");
            return [];
        }
    }

    private bool CheckAccessible(string devicePath)
    {
        try
        {
            return File.Exists(devicePath);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Accessibility check failed for {Path}.", devicePath);
            return false;
        }
    }

    private static string BuildStableKey(string devicePath, string? nonRewindingPath)
    {
        var path = nonRewindingPath ?? devicePath;
        return path.TrimStart('/');
    }

    private static bool IsBaseDeviceName(string name, string prefix)
    {
        var suffix = name.AsSpan(prefix.Length);
        if (suffix.IsEmpty)
            return false;
        foreach (var c in suffix)
        {
            if (c < '0' || c > '9')
                return false;
        }
        return true;
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
