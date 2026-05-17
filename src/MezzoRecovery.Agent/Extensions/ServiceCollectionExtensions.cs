using MezzoRecovery.Agent.Commands.Enroll;
using MezzoRecovery.Agent.Commands.Restart;
using MezzoRecovery.Agent.Commands.Run;
using MezzoRecovery.Agent.Commands.Status;
using MezzoRecovery.Agent.Commands.Update;
using MezzoRecovery.Agent.Commands.Version;
using MezzoRecovery.Agent.Configuration;
using MezzoRecovery.Agent.Devices;
using MezzoRecovery.TapeDrive.Abstractions;
using MezzoRecovery.TapeDrive.Linux.Discovery;
using MezzoRecovery.TapeDrive.Linux.Scsi;
using Microsoft.Extensions.DependencyInjection;

namespace MezzoRecovery.Agent.Extensions;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentApp(this IServiceCollection services) =>
        services
            .AddTransient<AgentMain>()
            .AddTransient<EnrollCommandHandler>()
            .AddTransient<RestartCommandHandler>()
            .AddTransient<RunCommandHandler>()
            .AddTransient<StatusCommandHandler>()
            .AddTransient<UpdateCommandHandler>()
            .AddTransient<VersionCommandHandler>()
            .AddSingleton<DeviceDiscoveryOptions>()
            .AddSingleton<ITapeDriveEnumerator, SysfsTapeDriveEnumerator>()
            .AddSingleton<IScsiHostEnumerator, SysfsScsiHostScanner>()
            .AddTransient<TapeDeviceDiscoveryService>();
}
