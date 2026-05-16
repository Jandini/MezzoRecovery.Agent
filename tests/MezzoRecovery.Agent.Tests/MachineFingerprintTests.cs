using MezzoRecovery.Agent.Identity;
using Xunit;

namespace MezzoRecovery.Agent.Tests;

public sealed class MachineFingerprintTests
{
    [Fact]
    public void Compute_is_deterministic()
    {
        var a = MachineFingerprint.Compute("machine-1", "host-a");
        var b = MachineFingerprint.Compute("machine-1", "host-a");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_changes_with_host()
    {
        var a = MachineFingerprint.Compute("machine-1", "host-a");
        var b = MachineFingerprint.Compute("machine-1", "host-b");
        Assert.NotEqual(a, b);
    }
}
