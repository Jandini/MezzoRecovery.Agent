using System.CommandLine;
using System.Diagnostics;
using MezzoRecovery.Agent.Devices;
using MezzoRecovery.TapeDrive.Abstractions;
using MezzoRecovery.TapeDrive.Linux;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.Commands.Scan;

internal sealed class ScanCommandHandler(
    IScsiTapeDeviceManager scsiTapeDeviceManager,
    IScsiHostEnumerator scsiHostEnumerator,
    TapeDeviceDiscoveryService discoveryService,
    ILogger<ScanCommandHandler> logger)
{
    public static readonly Option<bool> ForceDeleteAll = new("--force-delete-tapes")
    {
        Description = "Delete ALL tape devices before rescanning, not just stale ones.",
    };

    public static void AddOptions(Command command) => command.Options.Add(ForceDeleteAll);

    public Task<int> RunAsync(ParseResult parseResult, CancellationToken ct)
    {
        var forceDelete = parseResult.GetValue(ForceDeleteAll);
        return RunScanAsync(forceDelete, ct);
    }

    private Task<int> RunScanAsync(bool forceDelete, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
        {
            logger.LogWarning("SCSI scan is only supported on Linux.");
            Console.WriteLine("SCSI scan is only supported on Linux.");
            return Task.FromResult(1);
        }

        var mode = forceDelete ? "force-delete" : "safe";
        logger.LogInformation("SCSI rescan starting (mode: {Mode}).", mode);
        Console.WriteLine($"SCSI rescan starting (mode: {mode}).");

        var deleted = DeleteTapeDevices(forceDelete);

        if (ct.IsCancellationRequested)
            return Task.FromResult(1);

        RunUdevSettle();

        if (ct.IsCancellationRequested)
            return Task.FromResult(1);

        RescanScsiHosts();

        if (ct.IsCancellationRequested)
            return Task.FromResult(1);

        PrintDiscoveryResult();

        logger.LogInformation("SCSI rescan complete. Deleted {Count} device(s).", deleted);
        Console.WriteLine($"SCSI rescan complete. Deleted {deleted} device(s).");
        return Task.FromResult(0);
    }

    private int DeleteTapeDevices(bool forceDelete)
    {
        var devices = scsiTapeDeviceManager.GetScsiTapeDevices();
        if (devices.Count == 0)
        {
            logger.LogInformation("No SCSI tape devices found in sysfs - nothing to remove.");
            return 0;
        }

        var deletedCount = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var device in devices)
        {
            if (!seen.Add(device.ScsiAddress))
                continue;

            var devPath = "/dev/" + device.DeviceName;

            if (forceDelete)
            {
                logger.LogInformation(
                    "Force-deleting SCSI tape device {DeviceName} at {ScsiAddress}.",
                    device.DeviceName, device.ScsiAddress);
                Console.WriteLine($"  Deleting (force): {devPath} [{device.ScsiAddress}]");
                if (TryDelete(device.DeviceName, device.ScsiAddress))
                    deletedCount++;
                continue;
            }

            var stale = IsStaleBySysfsState(device.ScsiAddress);
            if (!stale)
                stale = IsStalByProbe(devPath, device.DeviceName, device.ScsiAddress);

            if (stale)
            {
                logger.LogInformation(
                    "Removing stale SCSI tape device {DeviceName} at {ScsiAddress}.",
                    device.DeviceName, device.ScsiAddress);
                Console.WriteLine($"  Deleting (stale): {devPath} [{device.ScsiAddress}]");
                if (TryDelete(device.DeviceName, device.ScsiAddress))
                    deletedCount++;
            }
            else
            {
                logger.LogInformation(
                    "Skipping healthy device {DeviceName} at {ScsiAddress}.",
                    device.DeviceName, device.ScsiAddress);
                Console.WriteLine($"  Skipping (healthy): {devPath} [{device.ScsiAddress}]");
            }
        }

        return deletedCount;
    }

    private bool IsStaleBySysfsState(string scsiAddress)
    {
        var state = scsiTapeDeviceManager.GetSysfsState(scsiAddress);
        if (state is null)
            return false;

        var stale = state is "offline" or "blocked" or "deleted" or "transport-offline" or "missing";
        if (stale)
            logger.LogInformation("Device {ScsiAddress} sysfs state is '{State}' - classified as stale.", scsiAddress, state);

        return stale;
    }

    private bool IsStalByProbe(string devPath, string deviceName, string scsiAddress)
    {
        if (!discoveryService.CheckAccessible(devPath))
            return false;

        var probe = LinuxTapeDriveStatus.Probe(devPath);
        if (probe.Ok)
            return false;

        if (probe.Errno == 5)
        {
            logger.LogInformation(
                "Device {DeviceName} ({ScsiAddress}): MTIOCGET returned EIO - classified as stale.",
                deviceName, scsiAddress);
            return true;
        }

        return false;
    }

    private bool TryDelete(string deviceName, string scsiAddress)
    {
        var ok = scsiTapeDeviceManager.TryDeleteScsiDevice(scsiAddress);
        if (!ok)
        {
            logger.LogWarning(
                "Failed to delete SCSI device {DeviceName} at {ScsiAddress} - root access may be required.",
                deviceName, scsiAddress);
            Console.WriteLine($"    WARNING: deletion failed for {scsiAddress} - run as root.");
        }

        return ok;
    }

    private void RunUdevSettle()
    {
        try
        {
            var psi = new ProcessStartInfo("udevadm", "settle")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(15_000);
            logger.LogInformation("udevadm settle completed (exit {Code}).", proc?.ExitCode ?? -1);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "udevadm settle unavailable - continuing without it.");
        }
    }

    private void RescanScsiHosts()
    {
        var hosts = scsiHostEnumerator.GetScsiHosts();
        if (hosts.Count == 0)
        {
            logger.LogWarning("No SCSI hosts found in /sys/class/scsi_host - rescan skipped.");
            return;
        }

        logger.LogInformation("Rescanning {Count} SCSI host(s).", hosts.Count);
        foreach (var host in hosts)
            logger.LogInformation("  Scanning {HostName} ({Controller}).", host.HostName, host.ControllerName ?? "unknown");

        scsiHostEnumerator.ScanScsiHosts();
        logger.LogInformation("SCSI host rescan triggered.");
    }

    private void PrintDiscoveryResult()
    {
        var devices = discoveryService.DiscoverDevices();
        Console.WriteLine($"Devices after rescan: {devices.Count}");

        foreach (var dev in devices)
        {
            var path = dev.NonRewindingDevicePath ?? dev.LinuxDevicePath;
            var model = string.IsNullOrEmpty(dev.Model) ? "?" : dev.Model;
            Console.WriteLine($"  {path}  {dev.Vendor} {model}  {dev.Status}");
        }
    }
}
