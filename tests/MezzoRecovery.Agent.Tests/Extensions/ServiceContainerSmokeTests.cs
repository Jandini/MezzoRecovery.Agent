using MezzoRecovery.Agent.Devices;
using MezzoRecovery.Agent.Extensions;
using MezzoRecovery.Agent.Runtime;
using MezzoRecovery.Agent.TapeOperations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MezzoRecovery.Agent.Tests.Extensions;

/// <summary>
/// Guards against future DI mistakes (circular dependencies, missing registrations) that
/// otherwise only surface at runtime when the agent starts up against real hardware.
/// </summary>
public sealed class ServiceContainerSmokeTests
{
    [Fact]
    public void AddAgentApp_resolves_every_registered_service_without_cycles()
    {
        using var sp = new ServiceCollection()
            .AddLogging()
            .AddAgentApp()
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

        // BuildServiceProvider with ValidateOnBuild only checks that each registered
        // service can be constructed at least once. Explicitly resolve the entry points
        // the agent actually uses, so anything DI-resolved on the hot path is exercised.
        Assert.NotNull(sp.GetRequiredService<TapeDeviceStatusPoller>());
        Assert.NotNull(sp.GetRequiredService<DeviceReportPublisher>());
        Assert.NotNull(sp.GetRequiredService<TapeMediaLoader>());
        Assert.NotNull(sp.GetRequiredService<TapePreflightRunner>());
        Assert.NotNull(sp.GetRequiredService<ITapePreflightTrigger>());
        Assert.NotNull(sp.GetRequiredService<TapeReadRunner>());
        Assert.NotNull(sp.GetRequiredService<TapeMediaControlService>());
        Assert.NotNull(sp.GetRequiredService<StopOperationHandler>());
    }
}
