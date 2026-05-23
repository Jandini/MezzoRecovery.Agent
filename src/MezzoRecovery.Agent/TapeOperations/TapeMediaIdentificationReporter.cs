using System.Collections.Concurrent;
using MezzoRecovery.Agent.Contracts;
using MezzoRecovery.Tape.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace MezzoRecovery.Agent.TapeOperations;

/// <summary>
/// Sends a <see cref="AgentTapePreflightResultDto"/> to the API after preflight completes,
/// including all preflight blocks so the API-side detector pipeline has the header data it needs
/// for media identification.
/// <para>
/// Reports that cannot be delivered (e.g. because the SignalR connection dropped mid-send) are
/// kept as pending and retried automatically when the connection is restored via
/// <see cref="RetryPendingAsync"/>. At most one pending report is kept per drive — a newer
/// preflight result always replaces an older one.
/// </para>
/// </summary>
public sealed class TapeMediaIdentificationReporter(ILogger<TapeMediaIdentificationReporter> logger)
{
    // Keyed by StableDeviceKey. ConcurrentDictionary so concurrent preflights on different
    // drives never race on the same bucket. A newer preflight overwrites a stale pending entry.
    private readonly ConcurrentDictionary<string, AgentTapePreflightResultDto> _pending =
        new(StringComparer.Ordinal);

    public async Task ReportAsync(
        HubConnection hub,
        AgentTapeDeviceDto device,
        PreflightResult? result,
        string? failureMessage,
        DateTimeOffset detectedAt,
        CancellationToken ct)
    {
        var dto = BuildDto(device, result, failureMessage, detectedAt);

        // Register as pending *before* sending so a connection drop between here and the
        // await cannot lose the report. If the send succeeds the entry is removed immediately;
        // if it fails RetryPendingAsync will pick it up on the next reconnect.
        _pending[device.StableDeviceKey] = dto;

        if (await TrySendAsync(hub, dto, ct))
            _pending.TryRemove(device.StableDeviceKey, out _);
    }

    /// <summary>
    /// Sends any reports that failed during the previous connection.
    /// Called by <c>AgentConnectionLoop</c> immediately after re-registration completes so
    /// tape-identification records on the server are updated even when the original send was
    /// lost during a transient disconnect.
    /// </summary>
    public async Task RetryPendingAsync(HubConnection hub, CancellationToken ct)
    {
        foreach (var (key, dto) in _pending)
        {
            if (await TrySendAsync(hub, dto, ct))
                _pending.TryRemove(key, out _);
        }
    }

    private async Task<bool> TrySendAsync(HubConnection hub, AgentTapePreflightResultDto dto, CancellationToken ct)
    {
        logger.LogDebug(
            "Sending preflight result: device={Key} succeeded={Succeeded} empty={Empty} blockSize={BlockSize} blocks={Blocks}.",
            dto.StableDeviceKey, dto.PreflightSucceeded, dto.IsEmpty,
            dto.BlockSize, dto.PreflightBlocks?.Length ?? 0);
        try
        {
            await hub.InvokeAsync("ReportTapePreflightResult", dto, ct).ConfigureAwait(false);
            logger.LogInformation(
                "Preflight result reported successfully for device {Key}.",
                dto.StableDeviceKey);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to report preflight result for device {Key} (succeeded={Succeeded} blocks={Blocks}); will retry on next reconnect.",
                dto.StableDeviceKey, dto.PreflightSucceeded, dto.PreflightBlocks?.Length ?? 0);
            return false;
        }
    }

    private static AgentTapePreflightResultDto BuildDto(
        AgentTapeDeviceDto device,
        PreflightResult? result,
        string? failureMessage,
        DateTimeOffset detectedAt) => new()
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
}
