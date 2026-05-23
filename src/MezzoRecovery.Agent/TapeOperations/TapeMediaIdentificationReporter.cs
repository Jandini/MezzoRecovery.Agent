using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.Tape.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.TapeOperations;

/// <summary>
/// Sends a <see cref="AgentTapePreflightResultDto"/> to the API after preflight completes,
/// including all preflight blocks so the API-side detector pipeline has the header data it needs
/// for media identification.
/// </summary>
public sealed class TapeMediaIdentificationReporter(ILogger<TapeMediaIdentificationReporter> logger)
{
    public async Task ReportAsync(
        HubConnection hub,
        AgentTapeDeviceDto device,
        PreflightResult? result,
        string? failureMessage,
        DateTimeOffset detectedAt,
        CancellationToken ct)
    {
        var dto = new AgentTapePreflightResultDto
        {
            StableDeviceKey    = device.StableDeviceKey,
            LinuxDevicePath    = device.LinuxDevicePath,
            PreflightSucceeded = result?.IsReadable == true,
            IsEmpty            = result?.IsEmpty == true,
            BlockSize          = result?.BlockSize > 0 ? result.BlockSize : null,
            PreflightBlocks    = result?.PreflightBlocks?.Count > 0
                                     ? result.PreflightBlocks.ToArray()
                                     : null,
            ErrorMessage       = result?.IsReadable == true
                                     ? null
                                     : (failureMessage ?? result?.ErrorMessage ?? "Preflight failed."),
            DetectedAt         = detectedAt,
        };

        try
        {
            await hub.InvokeAsync("ReportTapePreflightResult", dto, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to report preflight result for device {Key}.", device.StableDeviceKey);
        }
    }
}
