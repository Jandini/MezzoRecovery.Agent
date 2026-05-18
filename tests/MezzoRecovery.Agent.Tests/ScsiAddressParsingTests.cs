using MezzoRecovery.TapeDrive.Linux.Scsi;
using Xunit;

namespace MezzoRecovery.Agent.Tests;

public sealed class ScsiAddressParsingTests
{
    [Theory]
    [InlineData("/sys/devices/pci0000:00/0000:00:01.0/host6/target6:0:4/6:0:4:0", "6:0:4:0")]
    [InlineData("/sys/devices/platform/host0/target0:0:0/0:0:0:0", "0:0:0:0")]
    [InlineData("/sys/devices/pci0000:00/host12/target12:0:1/12:0:1:3", "12:0:1:3")]
    [InlineData("/sys/devices/host1/target1:0:0/1:0:0:0/", "1:0:0:0")]
    public void ParseScsiAddress_returns_hctl_segment(string sysfsPath, string expected)
    {
        var result = SysfsScsiTapeDeviceManager.ParseScsiAddress(sysfsPath);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/sys/devices/pci0000:00/host6/target6:0:4")]
    [InlineData("/sys/devices/pci0000:00/host6")]
    [InlineData("/sys/devices/pci0000:00/host6/target6:0:4/not-an-address")]
    [InlineData("/sys/devices/pci0000:00/host6/target6:0:4/6:0:4:x")]
    [InlineData("/sys/devices/pci0000:00/host6/target6:0:4/6:0:4")]
    public void ParseScsiAddress_returns_null_for_invalid_path(string sysfsPath)
    {
        var result = SysfsScsiTapeDeviceManager.ParseScsiAddress(sysfsPath);
        Assert.Null(result);
    }

    [Fact]
    public void ParseScsiAddress_returns_null_for_null_input()
    {
        var result = SysfsScsiTapeDeviceManager.ParseScsiAddress(null!);
        Assert.Null(result);
    }
}
