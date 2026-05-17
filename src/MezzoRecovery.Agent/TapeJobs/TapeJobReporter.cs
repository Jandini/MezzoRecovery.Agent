using MezzoRecovery.Agent.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.TapeJobs;

public sealed class TapeJobReporter(ILogger<TapeJobReporter> logger)
{
    public Task AcceptedAsync(HubConnection hub, Guid jobId, CancellationToken ct) =>
        InvokeAsync(hub, "TapeJobAccepted", new TapeJobAcceptedMessage(jobId, DateTimeOffset.UtcNow), ct);

    public Task RejectedAsync(HubConnection hub, Guid jobId, string reason, string? message, CancellationToken ct) =>
        InvokeAsync(
            hub,
            "TapeJobRejected",
            new TapeJobRejectedMessage(jobId, reason, message, DateTimeOffset.UtcNow),
            ct);

    public Task StartedAsync(HubConnection hub, Guid jobId, CancellationToken ct) =>
        InvokeAsync(hub, "TapeJobStarted", new TapeJobStartedMessage(jobId, DateTimeOffset.UtcNow), ct);

    public Task ProgressAsync(HubConnection hub, TapeJobProgressMessage message, CancellationToken ct) =>
        InvokeAsync(hub, "TapeJobProgress", message, ct);

    public Task CompletedAsync(HubConnection hub, Guid jobId, TapeJobProgressMessage finalStats, CancellationToken ct) =>
        InvokeAsync(
            hub,
            "TapeJobCompleted",
            new TapeJobCompletedMessage(jobId, finalStats, DateTimeOffset.UtcNow),
            ct);

    public Task FailedAsync(
        HubConnection hub,
        Guid jobId,
        string reason,
        string? message,
        TapeJobProgressMessage? finalStats,
        CancellationToken ct) =>
        InvokeAsync(
            hub,
            "TapeJobFailed",
            new TapeJobFailedMessage(jobId, reason, message, finalStats, DateTimeOffset.UtcNow),
            ct);

    public Task CancelledAsync(
        HubConnection hub,
        Guid jobId,
        TapeJobProgressMessage? finalStats,
        CancellationToken ct) =>
        InvokeAsync(
            hub,
            "TapeJobCancelled",
            new TapeJobCancelledMessage(jobId, finalStats, DateTimeOffset.UtcNow),
            ct);

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
