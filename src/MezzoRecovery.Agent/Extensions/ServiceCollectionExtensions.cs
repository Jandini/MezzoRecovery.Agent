using MezzoRecovery.Agent.Commands.Enroll;
using MezzoRecovery.Agent.Commands.Run;
using MezzoRecovery.Agent.Commands.Status;
using MezzoRecovery.Agent.Commands.Version;
using Microsoft.Extensions.DependencyInjection;

namespace MezzoRecovery.Agent.Extensions;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentApp(this IServiceCollection services) =>
        services
            .AddTransient<AgentMain>()
            .AddTransient<EnrollCommandHandler>()
            .AddTransient<RunCommandHandler>()
            .AddTransient<StatusCommandHandler>()
            .AddTransient<VersionCommandHandler>();
}
