using MezzoRecovery.Agent.Commands.Enroll;
using MezzoRecovery.Agent.Commands.Restart;
using MezzoRecovery.Agent.Commands.Run;
using MezzoRecovery.Agent.Commands.Status;
using MezzoRecovery.Agent.Commands.Update;
using MezzoRecovery.Agent.Commands.Version;
using MezzoRecovery.Agent.Configuration;
using MezzoRecovery.Agent.Devices;
using MezzoRecovery.Agent.TapeOperations;
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
            .AddSingleton<TapeOperationOptions>()
            .AddSingleton<IOptions<TapeOperationOptions>>(sp => Options.Create(sp.GetRequiredService<TapeOperationOptions>()))
            .AddSingleton<TapeDeviceStatusOptions>()
            .AddSingleton<IOptions<TapeDeviceStatusOptions>>(sp => Options.Create(sp.GetRequiredService<TapeDeviceStatusOptions>()))
            .AddSingleton<TapeMediaLoaderOptions>()
            .AddSingleton<IOptions<TapeMediaLoaderOptions>>(sp => Options.Create(sp.GetRequiredService<TapeMediaLoaderOptions>()))
            .AddSingleton<ITapeDriveEnumerator, SysfsTapeDriveEnumerator>()
            .AddSingleton<IScsiHostEnumerator, SysfsScsiHostScanner>()
            .AddSingleton<IScsiTapeDeviceManager, SysfsScsiTapeDeviceManager>()
            .AddSingleton<TapeDeviceLockManager>()
            .AddSingleton<TapeOperationStateStore>()
            .AddSingleton<TapeOperationReporter>()
            .AddSingleton<ITapeVerifyService, TapeVerifyService>()
            .AddSingleton<IPreflightService, PreflightService>()
            .AddSingleton<AgentDeviceStateStore>()
            .AddSingleton<TapeDeviceDiscoveryService>()
            .AddSingleton<TapePreflightRunner>()
            .AddSingleton<ITapePreflightTrigger>(sp => sp.GetRequiredService<TapePreflightRunner>())
            .AddSingleton<TapeMediaLoader>()
            .AddSingleton<DeviceReportPublisher>()
            .AddSingleton<TapeDeviceStatusPoller>()
            .AddSingleton<TapeReadRunner>()
            .AddSingleton<TapeMediaControlService>()
            .AddSingleton<StopOperationHandler>();
}
