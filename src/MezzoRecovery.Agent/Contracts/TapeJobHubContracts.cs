using System.Text.Json.Serialization;

namespace MezzoRecovery.Agent.Contracts;

public sealed record StartTapeReadJobCommand(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("tapeDeviceId")] Guid TapeDeviceId,
    [property: JsonPropertyName("linuxDevicePath")] string LinuxDevicePath,
    [property: JsonPropertyName("nonRewindingDevicePath")] string NonRewindingDevicePath,
    [property: JsonPropertyName("stableDeviceKey")] string StableDeviceKey,
    [property: JsonPropertyName("tapeBlockSizeBytes")] int TapeBlockSizeBytes,
    [property: JsonPropertyName("bufferSizeBytes")] int BufferSizeBytes,
    [property: JsonPropertyName("requestedByUserId")] Guid RequestedByUserId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

public sealed record CancelTapeJobCommand(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("tapeDeviceId")] Guid TapeDeviceId,
    [property: JsonPropertyName("stableDeviceKey")] string StableDeviceKey,
    [property: JsonPropertyName("requestedAt")] DateTimeOffset RequestedAt);

public sealed record ExecuteTapeMediaActionCommand(
    [property: JsonPropertyName("commandId")] Guid CommandId,
    [property: JsonPropertyName("tapeDeviceId")] Guid TapeDeviceId,
    [property: JsonPropertyName("nonRewindingDevicePath")] string NonRewindingDevicePath,
    [property: JsonPropertyName("stableDeviceKey")] string StableDeviceKey,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("spaceCount")] int? SpaceCount,
    [property: JsonPropertyName("requestedByUserId")] Guid RequestedByUserId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

public sealed record TapeJobAcceptedMessage(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("acceptedAt")] DateTimeOffset AcceptedAt);

public sealed record TapeJobRejectedMessage(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("rejectedAt")] DateTimeOffset RejectedAt);

public sealed record TapeJobStartedMessage(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt);

public sealed record TapeJobProgressMessage(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("bytesRead")] ulong BytesRead,
    [property: JsonPropertyName("blocksRead")] ulong BlocksRead,
    [property: JsonPropertyName("filemarksRead")] ulong FilemarksRead,
    [property: JsonPropertyName("currentFileNumber")] int CurrentFileNumber,
    [property: JsonPropertyName("currentBlockSizeBytes")] int CurrentBlockSizeBytes,
    [property: JsonPropertyName("throughputMegabytesPerSecond")] double ThroughputMegabytesPerSecond,
    [property: JsonPropertyName("throughputGigabytesPerHour")] double ThroughputGigabytesPerHour,
    [property: JsonPropertyName("elapsedSeconds")] long ElapsedSeconds,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("reportedAt")] DateTimeOffset ReportedAt);

public sealed record TapeJobCompletedMessage(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("finalStats")] TapeJobProgressMessage FinalStats,
    [property: JsonPropertyName("completedAt")] DateTimeOffset CompletedAt);

public sealed record TapeJobFailedMessage(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("failureReason")] string FailureReason,
    [property: JsonPropertyName("failureMessage")] string? FailureMessage,
    [property: JsonPropertyName("finalStats")] TapeJobProgressMessage? FinalStats,
    [property: JsonPropertyName("failedAt")] DateTimeOffset FailedAt);

public sealed record TapeJobCancelledMessage(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("finalStats")] TapeJobProgressMessage? FinalStats,
    [property: JsonPropertyName("cancelledAt")] DateTimeOffset CancelledAt);

public sealed record TapeJobStatusSnapshotMessage(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("tapeDeviceId")] Guid TapeDeviceId,
    [property: JsonPropertyName("stableDeviceKey")] string StableDeviceKey,
    [property: JsonPropertyName("isRunning")] bool IsRunning,
    [property: JsonPropertyName("lastStats")] TapeJobProgressMessage? LastStats);

public sealed record TapeMediaActionCompletedMessage(
    [property: JsonPropertyName("commandId")] Guid CommandId,
    [property: JsonPropertyName("tapeDeviceId")] Guid TapeDeviceId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("completedAt")] DateTimeOffset CompletedAt);

public sealed record TapeMediaActionFailedMessage(
    [property: JsonPropertyName("commandId")] Guid CommandId,
    [property: JsonPropertyName("tapeDeviceId")] Guid TapeDeviceId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("failureCode")] string FailureCode,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("failedAt")] DateTimeOffset FailedAt);

public static class TapeMediaActions
{
    public const string Rewind = "Rewind";
    public const string Eject = "Eject";
    public const string SpaceFilemarksForward = "SpaceFilemarksForward";
}
