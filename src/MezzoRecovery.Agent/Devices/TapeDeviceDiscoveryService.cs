using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.TapeDrive.Abstractions;
using MezzoRecovery.TapeDrive.Linux;
using MezzoRecovery.TapeDrive.Models;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.Devices;

public sealed class TapeDeviceDiscoveryService(
    ITapeDriveEnumerator enumerator,
    IScsiTapeDeviceManager? scsiTapeDeviceManager,
    ILogger<TapeDeviceDiscoveryService> logger)
{
    /// <summary>
    /// Enumerates tape drives and probes status. Devices whose stable key is in
    /// <paramref name="busyStableKeys"/> have their probe skipped and are reported
    /// as Busy / BUSY so the sweep never contends with an in-flight operation.
    /// </summary>
    public IReadOnlyList<AgentTapeDeviceDto> DiscoverDevices(IReadOnlySet<string>? busyStableKeys = null)
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

                AgentTapeDeviceStatus status;
                string? mtStatusLabels;
                if (busyStableKeys is not null && busyStableKeys.Contains(stableKey))
                {
                    status = AgentTapeDeviceStatus.Busy;
                    mtStatusLabels = "BUSY";
                }
                else
                {
                    (status, mtStatusLabels, _) = ProbeTapeStatus(probePath, accessible);
                }

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

    /// <summary>
    /// Lightweight, non-blocking status probe used by the status poller. Same
    /// classification as the discovery sweep but expressed as a single call so
    /// the poller never re-enumerates SCSI devices for the live tick.
    /// <para>
    /// Returns the raw <see cref="TapeGstatFlags"/> from MTIOCGET as the third tuple slot so
    /// callers (<c>TapeMediaLoader</c>) can detect <c>DR_OPEN</c> transitions precisely
    /// rather than re-parsing the label string. <c>null</c> when MTIOCGET did not run
    /// (non-Linux, EIO recovery, or probe failure).
    /// </para>
    /// </summary>
    public (AgentTapeDeviceStatus Status, string? MtStatusLabels, TapeGstatFlags? Flags) ProbeStatus(AgentTapeDeviceDto device)
    {
        var probePath = device.NonRewindingDevicePath ?? device.LinuxDevicePath;
        var accessible = CheckAccessible(device.LinuxDevicePath);
        return ProbeTapeStatus(probePath, accessible);
    }

    private (AgentTapeDeviceStatus Status, string? MtStatusLabels, TapeGstatFlags? Flags) ProbeTapeStatus(string probePath, bool accessible)
    {
        if (!accessible)
            return (AgentTapeDeviceStatus.Unavailable, null, null);

        if (!OperatingSystem.IsLinux())
            return (AgentTapeDeviceStatus.Present, null, null);

        try
        {
            var probe = LinuxTapeDriveStatus.Probe(probePath);
            if (probe.Ok)
            {
                var labels = NullIfEmpty(TapeGstatLabels.FormatShort(probe.Status.GstatFlags));
                var flags = probe.Status.GstatFlags;
                if (flags.HasFlag(TapeGstatFlags.DoorOpen))
                    return (AgentTapeDeviceStatus.NoMedia, labels, flags);

                if (!flags.HasFlag(TapeGstatFlags.Online))
                    return (AgentTapeDeviceStatus.Busy, labels, flags);

                if (flags.HasFlag(TapeGstatFlags.CleaningRequested))
                    return (AgentTapeDeviceStatus.Ready, labels, flags);

                return (AgentTapeDeviceStatus.Ready, labels, flags);
            }

            // EIO (errno 5): the device node exists but MTIOCGET failed - likely a stale/disconnected
            // device. Attempt a one-shot SCSI removal and re-probe. If the device was genuinely
            // disconnected it will disappear; if it recovers the second probe will succeed.
            if (probe.Errno == 5 && scsiTapeDeviceManager is not null)
                return TryRecoverEioDevice(probePath);

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

            return (status, mtLabels, null);
        }
        catch
        {
            return (AgentTapeDeviceStatus.Present, null, null);
        }
    }

    /// <summary>
    /// Called exactly once when MTIOCGET returns EIO for <paramref name="probePath"/>.
    /// Attempts to remove the stale SCSI device via sysfs, then re-probes.
    /// The device will either recover (probe succeeds) or disappear (Unavailable).
    /// </summary>
    private (AgentTapeDeviceStatus Status, string? MtStatusLabels, TapeGstatFlags? Flags) TryRecoverEioDevice(string probePath)
    {
        var nstName = Path.GetFileName(probePath); // e.g. "nst2"
        logger.LogWarning(
            "MTIOCGET returned EIO for {DevicePath} - device may be stale/disconnected. Attempting SCSI removal.",
            probePath);

        var devices = scsiTapeDeviceManager!.GetScsiTapeDevices();
        var match = devices.FirstOrDefault(d => string.Equals(d.DeviceName, nstName, StringComparison.Ordinal));

        if (match is null)
        {
            logger.LogWarning(
                "Could not find SCSI address for {DeviceName} in sysfs - skipping removal, classifying as disconnected.",
                nstName);
            return (AgentTapeDeviceStatus.Unavailable, "DISCONNECTED", null);
        }

        logger.LogInformation(
            "Removing stale SCSI device {DeviceName} at address {ScsiAddress}.",
            match.DeviceName, match.ScsiAddress);

        var deleted = scsiTapeDeviceManager.TryDeleteScsiDevice(match.ScsiAddress);
        if (!deleted)
        {
            logger.LogWarning(
                "Failed to remove SCSI device {ScsiAddress} - root access may be required. Classifying {DeviceName} as disconnected.",
                match.ScsiAddress, match.DeviceName);
            return (AgentTapeDeviceStatus.Unavailable, "DISCONNECTED", null);
        }

        logger.LogInformation(
            "SCSI device {ScsiAddress} ({DeviceName}) removed. Re-probing {DevicePath}.",
            match.ScsiAddress, match.DeviceName, probePath);

        // Re-probe: the device node may now be gone (ENOENT -> Unavailable) or, if the
        // driver re-attached it, the probe may now succeed.
        var accessible = CheckAccessible(probePath);
        if (!accessible)
        {
            logger.LogInformation(
                "Device {DevicePath} is no longer accessible after SCSI removal - treated as removed.",
                probePath);
            return (AgentTapeDeviceStatus.Unavailable, null, null);
        }

        try
        {
            var retry = LinuxTapeDriveStatus.Probe(probePath);
            if (retry.Ok)
            {
                var labels = NullIfEmpty(TapeGstatLabels.FormatShort(retry.Status.GstatFlags));
                var flags = retry.Status.GstatFlags;
                logger.LogInformation("Re-probe of {DevicePath} succeeded after SCSI removal.", probePath);
                if (flags.HasFlag(TapeGstatFlags.DoorOpen))
                    return (AgentTapeDeviceStatus.NoMedia, labels, flags);
                if (!flags.HasFlag(TapeGstatFlags.Online))
                    return (AgentTapeDeviceStatus.Busy, labels, flags);
                if (flags.HasFlag(TapeGstatFlags.CleaningRequested))
                    return (AgentTapeDeviceStatus.Ready, labels, flags);
                return (AgentTapeDeviceStatus.Ready, labels, flags);
            }

            logger.LogInformation(
                "Re-probe of {DevicePath} still failed (errno {Errno}) after SCSI removal - classifying as unavailable.",
                probePath, retry.Errno);
            return (AgentTapeDeviceStatus.Unavailable, null, null);
        }
        catch
        {
            return (AgentTapeDeviceStatus.Unavailable, null, null);
        }
    }

    public bool CheckAccessible(string devicePath)
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
