namespace MezzoRecovery.Agent;

internal sealed class AgentMain(IServiceProvider serviceProvider)
{
    public async Task RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var root = AgentCli.Build(serviceProvider);
        root.WriteBanner(typeof(AgentMain).Assembly.GetInformationalVersion());

        var exit = await root.Parse(args).InvokeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        Environment.ExitCode = exit;
    }
}
