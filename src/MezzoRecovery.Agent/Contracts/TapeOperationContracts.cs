using System.Text.Json.Serialization;

namespace MezzoRecovery.Agent.Contracts;

// ── Commands: API → Agent ─────────────────────────────────────────────────────

public sealed record StartTapeRunCommand(
    [property: JsonPropertyName("runId")] Guid RunId,
    [property: JsonPropertyName("runType")] string RunType,
    [property: JsonPropertyName("tapeDeviceId")] Guid TapeDeviceId,
    [property: JsonPropertyName("stableDeviceKey")] string StableDeviceKey,
    [property: JsonPropertyName("nonRewindingDevicePath")] string NonRewindingDevicePath,
    [property: JsonPropertyName("blockSizeBytes")] int? BlockSizeBytes,
    [property: JsonPropertyName("bufferSizeBytes")] int? BufferSizeBytes,
    [property: JsonPropertyName("autoDetect")] bool AutoDetect,
    [property: JsonPropertyName("cacheDirectory")] string? CacheDirectory,
    [property: JsonPropertyName("preflightOperationId")] Guid PreflightOperationId,
    [property: JsonPropertyName("readOperationId")] Guid? ReadOperationId,
    [property: JsonPropertyName("cloneOperationId")] Guid? CloneOperationId,
    [property: JsonPropertyName("hashOperationId")] Guid? HashOperationId,
    [property: JsonPropertyName("uploadOperationId")] Guid? UploadOperationId);

public sealed record CancelTapeRunCommand(
    [property: JsonPropertyName("runId")] Guid RunId);

public sealed record CancelTapeRunUploadsCommand(
    [property: JsonPropertyName("runId")] Guid RunId);

public sealed record PauseTapeRunUploadCommand(
    [property: JsonPropertyName("runId")] Guid RunId);

public sealed record ResumeTapeRunUploadCommand(
    [property: JsonPropertyName("runId")] Guid RunId);

public sealed record ResumeRunUploadsCommand(
    [property: JsonPropertyName("runId")] Guid RunId);

public sealed record StopTapeRunReadingCommand(
    [property: JsonPropertyName("runId")] Guid RunId);

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
    [property: JsonPropertyName("tapeCacheDirectory")] string? TapeCacheDirectory,
    [property: JsonPropertyName("maxConcurrentFileUploads")] int? MaxConcurrentFileUploads = null,
    [property: JsonPropertyName("maxConcurrentPartsPerFile")] int? MaxConcurrentPartsPerFile = null);

// ── Active-operation snapshot (Agent → API) ───────────────────────────────────

/// <summary>
/// Snapshot published at connect / reconnect and after each op terminates.
/// Used by the API to reconcile its live-state view. Sent to the
/// legacy ReportActiveOperations hub method that forwards it to the app hub.
/// </summary>
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

/// <summary>
/// Wire DTO for the ReportTapeDevices hub method.
/// Matches the server-side TapeDeviceReport positional record (camelCase JSON fields).
/// Replaces the old AgentTapeDeviceDto, which serialised Status as an integer and
/// used "status" rather than "deviceStatus" — causing silent deserialization mismatches.
/// </summary>
public sealed record TapeDeviceWireDto(
    [property: JsonPropertyName("stableDeviceKey")] string StableDeviceKey,
    [property: JsonPropertyName("linuxDevicePath")] string LinuxDevicePath,
    [property: JsonPropertyName("nonRewindingDevicePath")] string? NonRewindingDevicePath,
    [property: JsonPropertyName("rewindingDevicePath")] string? RewindingDevicePath,
    [property: JsonPropertyName("vendor")] string? Vendor,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("revision")] string? Revision,
    [property: JsonPropertyName("serialNumber")] string? SerialNumber,
    [property: JsonPropertyName("sysfsPath")] string? SysfsPath,
    [property: JsonPropertyName("scsiAddress")] string? ScsiAddress,
    [property: JsonPropertyName("mtStatusLabels")] string? MtStatusLabels,
    [property: JsonPropertyName("isPresent")] bool IsPresent,
    [property: JsonPropertyName("isAccessible")] bool IsAccessible,
    [property: JsonPropertyName("readBlockSizeBytes")] int ReadBlockSizeBytes,
    [property: JsonPropertyName("readBufferSizeBytes")] int ReadBufferSizeBytes,
    [property: JsonPropertyName("deviceStatus")] string DeviceStatus,
    [property: JsonPropertyName("mediaStatus")] string MediaStatus,
    [property: JsonPropertyName("supportedTapeGenerations")] string? SupportedTapeGenerations = null,
    [property: JsonPropertyName("loadedTapeGeneration")] string? LoadedTapeGeneration = null);

// ── Media detection (Agent → API) ─────────────────────────────────────────────

public sealed record MediaDetectionReport(
    [property: JsonPropertyName("tapeDeviceId")] Guid TapeDeviceId,
    [property: JsonPropertyName("tapeRunId")] Guid? TapeRunId,
    [property: JsonPropertyName("preflightOperationId")] Guid? PreflightOperationId,
    [property: JsonPropertyName("deviceStatus")] string DeviceStatus,
    [property: JsonPropertyName("mediaStatus")] string MediaStatus,
    [property: JsonPropertyName("mediaFormat")] string? MediaFormat,
    [property: JsonPropertyName("detectorName")] string? DetectorName,
    [property: JsonPropertyName("detectedBlockSizeBytes")] int? DetectedBlockSizeBytes,
    [property: JsonPropertyName("detectedBufferSizeBytes")] int? DetectedBufferSizeBytes,
    [property: JsonPropertyName("mediaHeaderHash")] string? MediaHeaderHash,
    [property: JsonPropertyName("mediaSetHash")] string? MediaSetHash,
    [property: JsonPropertyName("mediaFingerprintHash")] string? MediaFingerprintHash,
    [property: JsonPropertyName("headerBytes")] byte[]? HeaderBytes,
    [property: JsonPropertyName("headerPreviewText")] string? HeaderPreviewText,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage,
    [property: JsonPropertyName("linuxDevicePath")] string? LinuxDevicePath,
    [property: JsonPropertyName("nonRewindingDevicePath")] string? NonRewindingDevicePath,
    [property: JsonPropertyName("tapeGeneration")] string? TapeGeneration = null);

// ── Media action result (Agent → API) ────────────────────────────────────────

public sealed record TapeMediaActionResultReport(
    [property: JsonPropertyName("stableDeviceKey")] string  StableDeviceKey,
    [property: JsonPropertyName("operationType")]   string  OperationType,
    [property: JsonPropertyName("succeeded")]       bool    Succeeded,
    [property: JsonPropertyName("errorMessage")]    string? ErrorMessage,
    [property: JsonPropertyName("blockCount")]      long?   BlockCount = null);

// ── Run lifecycle (Agent → API) ───────────────────────────────────────────────

public sealed record TapeRunProgressReport(
    [property: JsonPropertyName("runId")] Guid RunId,
    [property: JsonPropertyName("bytesRead")] long BytesRead,
    [property: JsonPropertyName("blocksRead")] long BlocksRead,
    [property: JsonPropertyName("filemarksRead")] long FilemarksRead,
    [property: JsonPropertyName("tapeFilesCreated")] int TapeFilesCreated,
    [property: JsonPropertyName("currentBlock")] long? CurrentBlock,
    [property: JsonPropertyName("currentFileIndex")] int? CurrentFileIndex,
    [property: JsonPropertyName("currentOperationType")] string? CurrentOperationType,
    [property: JsonPropertyName("readThroughputBytesPerSecond")] long? ReadThroughputBytesPerSecond,
    [property: JsonPropertyName("uploadThroughputBytesPerSecond")] long? UploadThroughputBytesPerSecond);

public sealed record TapeRunCompletedReport(
    [property: JsonPropertyName("runId")] Guid RunId,
    [property: JsonPropertyName("succeeded")] bool Succeeded,
    [property: JsonPropertyName("failureReason")] string? FailureReason,
    [property: JsonPropertyName("failureMessage")] string? FailureMessage);

// ── Operation lifecycle (Agent → API) ─────────────────────────────────────────

public sealed record TapeOperationStartedReport(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt);

public sealed record TapeOperationProgressReport(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("bytesProcessed")] long BytesProcessed,
    [property: JsonPropertyName("blocksProcessed")] long BlocksProcessed,
    [property: JsonPropertyName("filesProcessed")] int FilesProcessed,
    [property: JsonPropertyName("currentBlock")] long? CurrentBlock,
    [property: JsonPropertyName("currentFileIndex")] int? CurrentFileIndex,
    [property: JsonPropertyName("throughputBytesPerSecond")] long? ThroughputBytesPerSecond);

public sealed record TapeOperationCompletedReport(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("succeeded")] bool Succeeded,
    [property: JsonPropertyName("failureReason")] string? FailureReason,
    [property: JsonPropertyName("failureMessage")] string? FailureMessage);

public sealed record TapeOperationEventReport(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("message")] string? Message);

// ── File lifecycle (Agent → API) ──────────────────────────────────────────────

public sealed record TapeFileCreatedReport(
    [property: JsonPropertyName("tapeRunId")] Guid TapeRunId,
    [property: JsonPropertyName("tapeDeviceId")] Guid TapeDeviceId,
    [property: JsonPropertyName("createdByOperationId")] Guid? CreatedByOperationId,
    [property: JsonPropertyName("tapeFileNumber")] int TapeFileNumber,
    [property: JsonPropertyName("segmentNumber")] int SegmentNumber,
    [property: JsonPropertyName("startBlock")] long? StartBlock,
    [property: JsonPropertyName("filemarkBefore")] bool FilemarkBefore,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("localPath")] string? LocalPath);

public sealed record TapeFileReadProgressReport(
    [property: JsonPropertyName("fileId")] Guid FileId,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("currentBlock")] long? CurrentBlock,
    [property: JsonPropertyName("throughputBytesPerSecond")] long? ThroughputBytesPerSecond);

public sealed record TapeFileReadCompletedReport(
    [property: JsonPropertyName("fileId")] Guid FileId,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("endBlock")] long? EndBlock,
    [property: JsonPropertyName("blockCount")] long? BlockCount,
    [property: JsonPropertyName("filemarkAfter")] bool FilemarkAfter,
    [property: JsonPropertyName("throughputBytesPerSecond")] long? ThroughputBytesPerSecond,
    [property: JsonPropertyName("succeeded")] bool Succeeded,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage);

public sealed record TapeFileHashProgressReport(
    [property: JsonPropertyName("fileId")] Guid FileId,
    [property: JsonPropertyName("bytesHashed")] long BytesHashed,
    [property: JsonPropertyName("throughputBytesPerSecond")] long? ThroughputBytesPerSecond);

public sealed record TapeFileHashCompletedReport(
    [property: JsonPropertyName("fileId")] Guid FileId,
    [property: JsonPropertyName("succeeded")] bool Succeeded,
    [property: JsonPropertyName("hashValue")] string? HashValue,
    [property: JsonPropertyName("failureReason")] string? FailureReason,
    [property: JsonPropertyName("failureMessage")] string? FailureMessage);

public sealed record TapeFileUploadProgressReport(
    [property: JsonPropertyName("fileId")] Guid FileId,
    [property: JsonPropertyName("bytesUploaded")] long BytesUploaded,
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("throughputBytesPerSecond")] long? ThroughputBytesPerSecond);

public sealed record TapeFileUploadFailedReport(
    [property: JsonPropertyName("fileId")] Guid FileId,
    [property: JsonPropertyName("failureReason")] string? FailureReason,
    [property: JsonPropertyName("failureMessage")] string? FailureMessage);

// ── Constants ─────────────────────────────────────────────────────────────────

public static class TapeOperationTypes
{
    public const string Preflight = "Preflight";
    public const string Read      = "Read";
    public const string Clone     = "Clone";
    public const string Hash      = "Hash";
    public const string Upload    = "Upload";
    public const string Rewind    = "Rewind";
    public const string Eject     = "Eject";
    public const string Space     = "Space";
    public const string Eod       = "Eod";
}
