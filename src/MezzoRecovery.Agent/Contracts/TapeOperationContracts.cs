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
    [property: JsonPropertyName("requestedAt")] DateTimeOffset RequestedAt,
    [property: JsonPropertyName("tapeId")] Guid? TapeId = null,
    // Internal TapeJob tracking id. Nullable so old API builds continue to work
    // during a rolling deploy. The agent echoes this back in all lifecycle messages.
    [property: JsonPropertyName("tapeJobId")] Guid? TapeJobId = null);

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

public sealed record UpdateTapeDeviceReadSettingsCommand(
    [property: JsonPropertyName("stableDeviceKey")] string StableDeviceKey,
    [property: JsonPropertyName("autoDetect")] bool AutoDetect,
    [property: JsonPropertyName("readBlockSizeBytes")] int ReadBlockSizeBytes,
    [property: JsonPropertyName("readBufferSizeBytes")] int ReadBufferSizeBytes);

public sealed record AgentConfigCommand(
    [property: JsonPropertyName("tapeCacheDirectory")] string? TapeCacheDirectory);

// Messages: Agent -> API

public sealed record TapeOperationStartedMessage(
    [property: JsonPropertyName("tapeDeviceId")] Guid TapeDeviceId,
    [property: JsonPropertyName("operationType")] string OperationType,
    [property: JsonPropertyName("requestedByUserId")] Guid RequestedByUserId,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("blockSizeBytes")] int BlockSizeBytes,
    [property: JsonPropertyName("bufferSizeBytes")] int BufferSizeBytes,
    [property: JsonPropertyName("tapeJobId")] Guid? TapeJobId = null);

public sealed record TapeOperationProgressMessage(
    [property: JsonPropertyName("tapeDeviceId")] Guid TapeDeviceId,
    [property: JsonPropertyName("operationType")] string OperationType,
    [property: JsonPropertyName("bytesRead")] long BytesRead,
    [property: JsonPropertyName("blocksRead")] long BlocksRead,
    [property: JsonPropertyName("filemarksRead")] long FilemarksRead,
    [property: JsonPropertyName("throughputMbps")] double ThroughputMbps,
    [property: JsonPropertyName("throughputGbph")] double ThroughputGbph,
    [property: JsonPropertyName("elapsedSeconds")] long ElapsedSeconds,
    [property: JsonPropertyName("reportedAt")] DateTimeOffset ReportedAt,
    [property: JsonPropertyName("tapeJobId")] Guid? TapeJobId = null);

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
    [property: JsonPropertyName("bufferSizeBytes")] int BufferSizeBytes,
    [property: JsonPropertyName("tapeJobId")] Guid? TapeJobId = null);

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
    [property: JsonPropertyName("bufferSizeBytes")] int BufferSizeBytes,
    [property: JsonPropertyName("tapeJobId")] Guid? TapeJobId = null);

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
    [property: JsonPropertyName("bufferSizeBytes")] int BufferSizeBytes,
    [property: JsonPropertyName("tapeJobId")] Guid? TapeJobId = null);

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

// Segment lifecycle: Agent -> API (AgentHub). Mirrors
// MezzoRecovery.Application.TapeSegments.Models.ReportTapeSegment*Message.

public sealed record ReportTapeSegmentCreatedMessage(
    [property: JsonPropertyName("segmentId")] Guid SegmentId,
    [property: JsonPropertyName("tapeId")] Guid TapeId,
    [property: JsonPropertyName("tapeDeviceId")] Guid TapeDeviceId,
    [property: JsonPropertyName("segmentNumber")] int SegmentNumber,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("tapeJobId")] Guid? TapeJobId = null);

public sealed record ReportTapeSegmentReadProgressMessage(
    [property: JsonPropertyName("segmentId")] Guid SegmentId,
    [property: JsonPropertyName("tapeId")] Guid TapeId,
    [property: JsonPropertyName("segmentNumber")] int SegmentNumber,
    [property: JsonPropertyName("currentBlock")] long CurrentBlock,
    [property: JsonPropertyName("currentFile")] int CurrentFile,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("blockCount")] long BlockCount,
    [property: JsonPropertyName("averageThroughputBytesPerSecond")] long AverageThroughputBytesPerSecond,
    [property: JsonPropertyName("reportedAt")] DateTimeOffset ReportedAt);

public sealed record ReportTapeSegmentReadCompletedMessage(
    [property: JsonPropertyName("segmentId")] Guid SegmentId,
    [property: JsonPropertyName("tapeId")] Guid TapeId,
    [property: JsonPropertyName("segmentNumber")] int SegmentNumber,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("blockCount")] long BlockCount,
    [property: JsonPropertyName("averageThroughputBytesPerSecond")] long AverageThroughputBytesPerSecond,
    [property: JsonPropertyName("completedAt")] DateTimeOffset CompletedAt);

public sealed record ReportTapeSegmentReadFailedMessage(
    [property: JsonPropertyName("segmentId")] Guid SegmentId,
    [property: JsonPropertyName("tapeId")] Guid TapeId,
    [property: JsonPropertyName("segmentNumber")] int SegmentNumber,
    [property: JsonPropertyName("errorMessage")] string ErrorMessage,
    [property: JsonPropertyName("failedAt")] DateTimeOffset FailedAt);

public static class TapeOperationTypes
{
    public const string Read = "Read";
    public const string Rewind = "Rewind";
    public const string Eject = "Eject";
    public const string Space = "Space";
    public const string Preflight = "Preflight";
}
