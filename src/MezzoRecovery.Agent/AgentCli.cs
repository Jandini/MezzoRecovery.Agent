using System.CommandLine;
using MezzoRecovery.Agent.Commands.Enroll;
using MezzoRecovery.Agent.Commands.Run;
using MezzoRecovery.Agent.Commands.Status;
using MezzoRecovery.Agent.Commands.Update;
using MezzoRecovery.Agent.Commands.Version;
using Microsoft.Extensions.DependencyInjection;

namespace MezzoRecovery.Agent;

internal static class AgentCli
{
    public static RootCommand Build(IServiceProvider services)
    {
        var root = new RootCommand("mra - MezzoRecovery Linux recovery host agent");

        var enroll = new Command("enroll", "Enroll this machine using a short-lived code from the MezzoRecovery UI.");
        EnrollCommandHandler.AddArguments(enroll);
        EnrollCommandHandler.AddOptions(enroll);
        enroll.SetAction(async (parseResult, ct) =>
        {
            var handler = services.GetRequiredService<EnrollCommandHandler>();
            return await handler.RunAsync(parseResult, ct).ConfigureAwait(false);
        });
        root.Subcommands.Add(enroll);

        var run = new Command("run", "Start the agent and connect to the MezzoRecovery server.");
        RunCommandHandler.AddOptions(run);
        run.SetAction(async (parseResult, ct) =>
        {
            var handler = services.GetRequiredService<RunCommandHandler>();
            return await handler.RunAsync(parseResult, ct).ConfigureAwait(false);
        });
        root.Subcommands.Add(run);

        var status = new Command("status", "Show enrollment and credential status.");
        StatusCommandHandler.AddOptions(status);
        status.SetAction(async (parseResult, ct) =>
        {
            var handler = services.GetRequiredService<StatusCommandHandler>();
            return await handler.RunAsync(parseResult, ct).ConfigureAwait(false);
        });
        root.Subcommands.Add(status);

        var version = new Command("version", "Print the agent version.");
        version.SetAction((parseResult, ct) =>
        {
            var handler = services.GetRequiredService<VersionCommandHandler>();
            return Task.FromResult(handler.Run());
        });
        root.Subcommands.Add(version);

        var update = new Command("update", "Download and install the latest mra binary from mezzorecovery.com.");
        UpdateCommandHandler.AddOptions(update);
        update.SetAction(async (parseResult, ct) =>
        {
            var handler = services.GetRequiredService<UpdateCommandHandler>();
            return await handler.RunAsync(parseResult, ct).ConfigureAwait(false);
        });
        root.Subcommands.Add(update);

        return root;
    }
}
