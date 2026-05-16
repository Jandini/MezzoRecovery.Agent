using System.Reflection;
using MezzoRecovery.Agent.Api;
using MezzoRecovery.Agent.Configuration;
using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.Agent.Identity;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MezzoRecovery.Agent.Runtime;

public sealed class AgentConnectionLoop(string configPath, string credentialPath, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task RunAsync(CancellationToken ct)
    {
        using var _ = new ProcessLock(AgentPaths.DefaultLockPath);
        using var http = new HttpClient();

        var cfg = await AgentConfigLoader.LoadAsync(configPath, ct);
        var baseUri = new Uri(cfg.ApiBaseUrl.TrimEnd('/') + "/");
        var cred = await CredentialStore.TryLoadAsync(credentialPath, ct);
        if (cred is null)
        {
            _logger.LogError("No credentials at {Path}. Run enroll first.", credentialPath);
            return;
        }

        var api = new AgentApiClient(http);
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                     ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                     ?? "0.0.0";
        var hostname = Environment.MachineName;
        var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();

        var failureBackoff = TimeSpan.FromSeconds(2);
        const int maxFailureBackoffSeconds = 120;

        while (!ct.IsCancellationRequested)
        {
            HubConnection? hub = null;
            try
            {
                var tokenResp = await api.GetTokenAsync(
                    baseUri,
                    new TokenApiRequest(cred.AgentId, cred.ClientSecret),
                    ct);
                if (tokenResp is null)
                {
                    _logger.LogWarning("Token request failed; retrying after {Delay}.", failureBackoff);
                    await Task.Delay(failureBackoff, ct);
                    failureBackoff = TimeSpan.FromSeconds(Math.Min(maxFailureBackoffSeconds, failureBackoff.TotalSeconds * 2));
                    continue;
                }

                failureBackoff = TimeSpan.FromSeconds(2);
                var hubUri = new Uri(baseUri, "api/hubs/agent");
                var hubUrl = $"{hubUri}?access_token={Uri.EscapeDataString(tokenResp.AccessToken)}";
                hub = new HubConnectionBuilder()
                    .WithUrl(hubUrl)
                    .WithAutomaticReconnect(
                        Enumerable.Range(0, 8).Select(n => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, n))))
                            .ToArray())
                    .Build();

                hub.Reconnecting += error =>
                {
                    _logger.LogWarning(error, "SignalR reconnecting…");
                    return Task.CompletedTask;
                };
                hub.Reconnected += async connectionId =>
                {
                    _logger.LogInformation("SignalR reconnected. ConnectionId={Id}", connectionId);
                    try
                    {
                        await hub!.InvokeAsync("RegisterRuntime", hostname, os, arch, version, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "RegisterRuntime after reconnect failed.");
                    }
                };

                var disconnectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                hub.Closed += async _ =>
                {
                    disconnectTcs.TrySetResult();
                    await Task.CompletedTask;
                };

                await hub.StartAsync(ct);
                _logger.LogInformation("Connected to MezzoRecovery (agent {AgentId}).", cred.AgentId);
                await hub.InvokeAsync("RegisterRuntime", hostname, os, arch, version, ct);

                using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var heartbeatTask = HeartbeatLoopAsync(hub, hostname, os, arch, version, heartbeatCts.Token);

                await disconnectTcs.Task.WaitAsync(ct);

                await heartbeatCts.CancelAsync();
                try
                {
                    await heartbeatTask;
                }
                catch (OperationCanceledException)
                {
                    // normal
                }

                await hub.StopAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent run loop error; backing off {Delay}.", failureBackoff);
                try
                {
                    await Task.Delay(failureBackoff, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                failureBackoff = TimeSpan.FromSeconds(Math.Min(maxFailureBackoffSeconds, failureBackoff.TotalSeconds * 2));
            }
            finally
            {
                if (hub is not null)
                    await hub.DisposeAsync();
            }
        }
    }

    private async Task HeartbeatLoopAsync(
        HubConnection connection,
        string hostname,
        string os,
        string arch,
        string version,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
                await connection.InvokeAsync("Heartbeat", hostname, os, arch, version, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }
}
