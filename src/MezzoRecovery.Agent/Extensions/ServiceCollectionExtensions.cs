using MezzoRecovery.Agent.Commands.Enroll;
using MezzoRecovery.Agent.Commands.Restart;
using MezzoRecovery.Agent.Commands.Run;
using MezzoRecovery.Agent.Commands.Status;
using MezzoRecovery.Agent.Commands.Update;
using MezzoRecovery.Agent.Commands.Version;
using MezzoRecovery.Agent.Configuration;
using MezzoRecovery.Agent.Devices;
using MezzoRecovery.Agent.TapeJobs;
using MezzoRecovery.Tape.Services;
using MezzoRecovery.TapeDrive.Abstractions;
using MezzoRecovery.TapeDrive.Linux.Discovery;
using MezzoRecovery.TapeDrive.Linux.Scsi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
            .AddSingleton<TapeJobOptions>()
            .AddSingleton<IOptions<TapeJobOptions>>(sp => Options.Create(sp.GetRequiredService<TapeJobOptions>()))
            .AddSingleton<ITapeDriveEnumerator, SysfsTapeDriveEnumerator>()
            .AddSingleton<IScsiHostEnumerator, SysfsScsiHostScanner>()
            .AddSingleton<TapeDeviceLockManager>()
            .AddSingleton<AgentJobStateStore>()
            .AddSingleton<TapeJobReporter>()
            .AddSingleton<ITapeVerifyService, TapeVerifyService>()
            .AddSingleton<TapeJobRunner>()
            .AddSingleton<TapeMediaControlService>()
            .AddTransient<TapeDeviceDiscoveryService>();
}
