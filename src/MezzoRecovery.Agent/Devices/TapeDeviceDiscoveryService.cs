using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.TapeDrive.Abstractions;
using MezzoRecovery.TapeDrive.Linux;
using MezzoRecovery.TapeDrive.Models;
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
                var probePath = nonRewindingPath ?? devicePath;
                var accessible = CheckAccessible(devicePath);
                var (status, mtStatusLabels) = ProbeTapeStatus(probePath, accessible);

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
                    MtStatusLabels = mtStatusLabels,
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

    private static (AgentTapeDeviceStatus Status, string? MtStatusLabels) ProbeTapeStatus(string probePath, bool accessible)
    {
        if (!accessible)
            return (AgentTapeDeviceStatus.Unavailable, null);

        if (!OperatingSystem.IsLinux())
            return (AgentTapeDeviceStatus.Present, null);

        try
        {
            var probe = LinuxTapeDriveStatus.Probe(probePath);
            if (probe.Ok)
            {
                var labels = NullIfEmpty(TapeGstatLabels.FormatShort(probe.Status.GstatFlags));
                var flags = probe.Status.GstatFlags;
                if (flags.HasFlag(TapeGstatFlags.DoorOpen) || !flags.HasFlag(TapeGstatFlags.Online))
                    return (AgentTapeDeviceStatus.NoMedia, labels);

                return (AgentTapeDeviceStatus.Ready, labels);
            }

            var mtLabels = probe.FailureCategory switch
            {
                TapeDriveMtProbeFailureCategory.Busy => "BUSY",
                TapeDriveMtProbeFailureCategory.NotReady => "NOT_READY",
                _ => null,
            };

            var status = probe.FailureCategory switch
            {
                TapeDriveMtProbeFailureCategory.Busy => AgentTapeDeviceStatus.Busy,
                TapeDriveMtProbeFailureCategory.NotReady => AgentTapeDeviceStatus.NoMedia,
                _ when probe.Errno is 13 or 1 => AgentTapeDeviceStatus.PermissionDenied,
                _ => AgentTapeDeviceStatus.Present,
            };

            return (status, mtLabels);
        }
        catch
        {
            return (AgentTapeDeviceStatus.Present, null);
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
