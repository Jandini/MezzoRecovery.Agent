using MezzoRecovery.Agent;
using MezzoRecovery.Agent.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using var serviceProvider = new ServiceCollection()
    .AddLogging(b => b.AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    }))
    .AddAgentApp()
    .BuildServiceProvider();

try
{
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    await serviceProvider.GetRequiredService<AgentMain>().RunAsync(args, cts.Token);
}
catch (OperationCanceledException)
{
    Environment.ExitCode = 0;
}
catch (Exception ex)
{
    serviceProvider.GetService<ILogger<Program>>()?
        .LogCritical(ex, "Agent failed.");
    Environment.ExitCode = 10;
}
