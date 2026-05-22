using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace MezzoRecovery.Agent.Logging;

public sealed class TerseConsoleFormatter(IOptionsMonitor<SimpleConsoleFormatterOptions> options)
    : ConsoleFormatter("terse")
{
    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        string? message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (message is null && logEntry.Exception is null)
            return;

        var opts = options.CurrentValue;

        if (opts.TimestampFormat is { } fmt)
            textWriter.Write(DateTime.Now.ToString(fmt));

        textWriter.Write(logEntry.LogLevel switch
        {
            LogLevel.Trace       => "trce ",
            LogLevel.Debug       => "dbug ",
            LogLevel.Information => "info ",
            LogLevel.Warning     => "warn ",
            LogLevel.Error       => "fail ",
            LogLevel.Critical    => "crit ",
            _                    => $"{logEntry.LogLevel} "
        });

        var category = logEntry.Category;
        var dot = category.LastIndexOf('.');
        textWriter.Write(dot >= 0 ? category.AsSpan(dot + 1) : category.AsSpan());
        textWriter.Write(": ");

        if (message is not null)
            textWriter.Write(message);

        if (logEntry.Exception is { } ex)
            textWriter.Write($" {ex}");

        textWriter.WriteLine();
    }
}
