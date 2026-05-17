using MezzoRecovery.Agent.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.TapeOperations;

/// <summary>
/// Thin wrapper around the five AgentHub server methods. No business logic.
/// </summary>
public sealed class TapeOperationReporter(ILogger<TapeOperationReporter> logger)
{
    public Task StartedAsync(HubConnection hub, TapeOperationStartedMessage message, CancellationToken ct) =>
        InvokeAsync(hub, "TapeOperationStarted", message, ct);

    public Task ProgressAsync(HubConnection hub, TapeOperationProgressMessage message, CancellationToken ct) =>
        InvokeAsync(hub, "TapeOperationProgress", message, ct);

    public Task CompletedAsync(HubConnection hub, TapeOperationCompletedMessage message, CancellationToken ct) =>
        InvokeAsync(hub, "TapeOperationCompleted", message, ct);

    public Task FailedAsync(HubConnection hub, TapeOperationFailedMessage message, CancellationToken ct) =>
        InvokeAsync(hub, "TapeOperationFailed", message, ct);

    public Task CancelledAsync(HubConnection hub, TapeOperationCancelledMessage message, CancellationToken ct) =>
        InvokeAsync(hub, "TapeOperationCancelled", message, ct);

    private async Task InvokeAsync<T>(HubConnection hub, string method, T payload, CancellationToken ct)
    {
        try
        {
            await hub.InvokeAsync(method, payload, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to invoke AgentHub method {Method}.", method);
        }
    }
}
