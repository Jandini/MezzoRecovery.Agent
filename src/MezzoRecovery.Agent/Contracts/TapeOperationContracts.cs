using System.Text.Json.Serialization;

namespace MezzoRecovery.Agent.Contracts;

// Commands: API -> Agent

public sealed record StartTapeReadCommand(
    [property: JsonPropertyName("tapeDeviceId")] Guid TapeDeviceId,
    [property: JsonPropertyName("linuxDevicePath")] string LinuxDevicePath,
    [property: JsonPropertyName("nonRewindingDevicePath")] string NonRewindingDevicePath,
    [property: JsonPropertyName("stableDeviceKey")] string StableDeviceKey,
    [property: JsonPropertyName("tapeBlockSizeBytes")] int TapeBlockSizeBytes,
    [property: JsonPropertyName("bufferSizeBytes")] int BufferSizeBytes,
    [property: JsonPropertyName("requestedByUserId")] Guid RequestedByUserId,
    [property: JsonPropertyName("requestedAt")] DateTimeOffset RequestedAt);

public sealed record StopTapeOperationCommand(
    [property: JsonPropertyName("tapeDeviceId")] Guid TapeDeviceId,
    [property: JsonPropertyName("requestedAt")] DateTimeOffset RequestedAt);

public sealed record ExecuteTapeMediaActionCommand(
    [property: JsonPropertyName("tapeDeviceId")] Guid TapeDeviceId,
    [property: JsonPropertyName("nonRewindingDevicePath")] string NonRewindingDevicePath,
    [property: JsonPropertyName("stableDeviceKey")] string StableDeviceKey,
    [property: JsonPropertyName("operationType")] string OperationType,
    [property: JsonPropertyName("spaceCount")] int? SpaceCount,
    [property: JsonPropertyName("requestedByUserId")] Guid RequestedByUserId,
    [property: JsonPropertyName("requestedAt")] DateTimeOffset RequestedAt);

public sealed record RefreshTapeDeviceCommand(
    [property: JsonPropertyName("tapeDeviceId")] Guid TapeDeviceId,
    [property: JsonPropertyName("stableDeviceKey")] string StableDeviceKey);

public sealed record AgentConfigCommand(
    [property: JsonPropertyName("tapeCacheDirectory")] string? TapeCacheDirectory);

// Messages: Agent -> API

public sealed record TapeOperationStartedMessage(
    [property: JsonPropertyName("tapeDeviceId")] Guid TapeDeviceId,
    [property: JsonPropertyName("operationType")] string OperationType,
    [property: JsonPropertyName("requestedByUserId")] Guid RequestedByUserId,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("blockSizeBytes")] int BlockSizeBytes,
    [property: JsonPropertyName("bufferSizeBytes")] int BufferSizeBytes);

public sealed record TapeOperationProgressMessage(
    [property: JsonPropertyName("tapeDeviceId")] Guid TapeDeviceId,
    [property: JsonPropertyName("operationType")] string OperationType,
    [property: JsonPropertyName("bytesRead")] long BytesRead,
    [property: JsonPropertyName("blocksRead")] long BlocksRead,
    [property: JsonPropertyName("filemarksRead")] long FilemarksRead,
    [property: JsonPropertyName("throughputMbps")] double ThroughputMbps,
    [property: JsonPropertyName("throughputGbph")] double ThroughputGbph,
    [property: JsonPropertyName("elapsedSeconds")] long ElapsedSeconds,
    [property: JsonPropertyName("reportedAt")] DateTimeOffset ReportedAt);

public sealed record TapeOperationCompletedMessage(
    [property: JsonPropertyName("tapeDeviceId")] Guid TapeDeviceId,
    [property: JsonPropertyName("operationType")] string OperationType,
    [property: JsonPropertyName("requestedByUserId")] Guid RequestedByUserId,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("completedAt")] DateTimeOffset CompletedAt,
    [property: JsonPropertyName("bytesRead")] long BytesRead,
    [property: JsonPropertyName("blocksRead")] long BlocksRead,
    [property: JsonPropertyName("filemarksRead")] long FilemarksRead,
    [property: JsonPropertyName("throughputMbps")] double ThroughputMbps,
    [property: JsonPropertyName("elapsedSeconds")] long ElapsedSeconds,
    [property: JsonPropertyName("blockSizeBytes")] int BlockSizeBytes,
    [property: JsonPropertyName("bufferSizeBytes")] int BufferSizeBytes);

public sealed record TapeOperationFailedMessage(
    [property: JsonPropertyName("tapeDeviceId")] Guid TapeDeviceId,
    [property: JsonPropertyName("operationType")] string OperationType,
    [property: JsonPropertyName("requestedByUserId")] Guid RequestedByUserId,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("failedAt")] DateTimeOffset FailedAt,
    [property: JsonPropertyName("failureReason")] string FailureReason,
    [property: JsonPropertyName("failureMessage")] string? FailureMessage,
    [property: JsonPropertyName("bytesRead")] long BytesRead,
    [property: JsonPropertyName("blocksRead")] long BlocksRead,
    [property: JsonPropertyName("filemarksRead")] long FilemarksRead,
    [property: JsonPropertyName("throughputMbps")] double ThroughputMbps,
    [property: JsonPropertyName("elapsedSeconds")] long ElapsedSeconds,
    [property: JsonPropertyName("blockSizeBytes")] int BlockSizeBytes,
    [property: JsonPropertyName("bufferSizeBytes")] int BufferSizeBytes);

public sealed record TapeOperationCancelledMessage(
    [property: JsonPropertyName("tapeDeviceId")] Guid TapeDeviceId,
    [property: JsonPropertyName("operationType")] string OperationType,
    [property: JsonPropertyName("requestedByUserId")] Guid RequestedByUserId,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("cancelledAt")] DateTimeOffset CancelledAt,
    [property: JsonPropertyName("bytesRead")] long BytesRead,
    [property: JsonPropertyName("blocksRead")] long BlocksRead,
    [property: JsonPropertyName("filemarksRead")] long FilemarksRead,
    [property: JsonPropertyName("throughputMbps")] double ThroughputMbps,
    [property: JsonPropertyName("elapsedSeconds")] long ElapsedSeconds,
    [property: JsonPropertyName("blockSizeBytes")] int BlockSizeBytes,
    [property: JsonPropertyName("bufferSizeBytes")] int BufferSizeBytes);

public sealed record ActiveOperationSnapshot(
    [property: JsonPropertyName("tapeDeviceId")] Guid TapeDeviceId,
    [property: JsonPropertyName("operationType")] string OperationType,
    [property: JsonPropertyName("requestedByUserId")] Guid RequestedByUserId,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("bytesRead")] long BytesRead,
    [property: JsonPropertyName("blocksRead")] long BlocksRead,
    [property: JsonPropertyName("filemarksRead")] long FilemarksRead,
    [property: JsonPropertyName("throughputMbps")] double ThroughputMbps,
    [property: JsonPropertyName("throughputGbph")] double ThroughputGbph,
    [property: JsonPropertyName("elapsedSeconds")] long ElapsedSeconds,
    [property: JsonPropertyName("blockSizeBytes")] int BlockSizeBytes,
    [property: JsonPropertyName("bufferSizeBytes")] int BufferSizeBytes,
    [property: JsonPropertyName("lastProgressAt")] DateTimeOffset? LastProgressAt);

public static class TapeOperationTypes
{
    public const string Read = "Read";
    public const string Rewind = "Rewind";
    public const string Eject = "Eject";
    public const string Space = "Space";
}
