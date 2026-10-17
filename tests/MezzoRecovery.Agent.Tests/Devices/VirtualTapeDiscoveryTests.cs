using MezzoRecovery.Agent.Devices;
using MezzoRecovery.TapeDrive.Abstractions;
using MezzoRecovery.TapeDrive.Linux.Discovery;
using MezzoRecovery.TapeDrive.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MezzoRecovery.Agent.Tests.Devices;

public sealed class VirtualTapeDiscoveryTests
{
    private sealed class FakeEnumerator(params TapeDriveInfo[] drives) : ITapeDriveEnumerator
    {
        public IReadOnlyList<TapeDriveInfo> GetTapeDrives() => drives;
    }

    private static TapeDriveInfo Virtual(string name) => new()
    {
        Vendor = VirtualTapeDrive.Vendor,
        Model = VirtualTapeDrive.Model,
        LinuxDevices = [name],
        IsVirtual = true,
    };

    [Fact]
    public void VirtualDrive_DevicePath_is_dev_name()
    {
        Assert.Equal("/dev/vtape0", Virtual("vtape0").DevicePath);
    }

    [Fact]
    public void VirtualDrive_is_published_as_non_rewinding_only_without_sysfs_fields()
    {
        var service = new TapeDeviceDiscoveryService(
            new FakeEnumerator(Virtual("vtape0")), null, null, NullLogger<TapeDeviceDiscoveryService>.Instance);

        var device = Assert.Single(service.DiscoverDevices());

        Assert.Equal("dev/vtape0", device.StableDeviceKey);
        Assert.Equal("/dev/vtape0", device.LinuxDevicePath);
        Assert.Equal("/dev/vtape0", device.NonRewindingDevicePath);
        Assert.Null(device.RewindingDevicePath);
        Assert.Equal("MezzoRecovery", device.Vendor);
        Assert.Equal("VirtualTape", device.Model);
        Assert.Null(device.Revision);
        Assert.Null(device.SerialNumber);
        Assert.Null(device.SysfsPath);
        Assert.Null(device.ScsiAddress);
        Assert.Null(device.SupportedTapeGenerations);
    }

    [Fact]
    public void Enumerator_without_run_dir_adds_nothing()
    {
        var inner = new FakeEnumerator(new TapeDriveInfo { LinuxDevices = ["nst0"] });
        var enumerator = new VirtualTapeDriveEnumerator(inner, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        Assert.Single(enumerator.GetTapeDrives());
    }
}
