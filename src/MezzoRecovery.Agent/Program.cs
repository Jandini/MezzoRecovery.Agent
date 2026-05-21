using MezzoRecovery.Agent;
using MezzoRecovery.Agent.Extensions;
using MezzoRecovery.Agent.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

using var serviceProvider = new ServiceCollection()
    .AddLogging(b => b
        .AddConsole(o => o.FormatterName = "terse")
        .AddConsoleFormatter<TerseConsoleFormatter, SimpleConsoleFormatterOptions>(o =>
        {
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
