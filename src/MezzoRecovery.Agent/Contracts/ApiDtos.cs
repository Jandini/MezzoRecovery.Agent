using System.Text.Json.Serialization;

namespace MezzoRecovery.Agent.Contracts;

/// <summary>
/// Returned by <c>GET api/agent/tape/uploads/pending</c>. Carries enough
/// information for the agent to reconstruct the local .tic path and resume or
/// re-enqueue an orphaned multipart upload after a restart or reconnect.
/// </summary>
public sealed record PendingUploadItem(
    [property: JsonPropertyName("fileId")]         Guid                      FileId,
    [property: JsonPropertyName("runId")]          Guid                      RunId,
    [property: JsonPropertyName("tapeFileNumber")] int                       TapeFileNumber,
    [property: JsonPropertyName("totalBytes")]     long                      TotalBytes,
    [property: JsonPropertyName("uploadSessionId")] Guid?                    UploadSessionId = null,
    [property: JsonPropertyName("uploadStatus")]   string?                   UploadStatus    = null,
    [property: JsonPropertyName("isPaused")]       bool                      IsPaused        = false,
    [property: JsonPropertyName("completedParts")] CompletedPartItemDto[]?   CompletedParts  = null);

// ── Upload session API DTOs ───────────────────────────────────────────────────

public sealed record CompletedPartItemDto(
    [property: JsonPropertyName("partNumber")]     int     PartNumber,
    [property: JsonPropertyName("offsetBytes")]    long    OffsetBytes,
    [property: JsonPropertyName("lengthBytes")]    long    LengthBytes,
    [property: JsonPropertyName("etag")]           string  ETag,
    [property: JsonPropertyName("checksumSha256")] string? ChecksumSha256);

public sealed record StartUploadSessionApiRequest(
    [property: JsonPropertyName("fileName")]              string  FileName,
    [property: JsonPropertyName("totalBytes")]            long    TotalBytes,
    [property: JsonPropertyName("contentType")]           string  ContentType,
    [property: JsonPropertyName("preferredPartSizeBytes")] long   PreferredPartSizeBytes,
    [property: JsonPropertyName("fileSha256")]            string? FileSha256 = null);

public sealed record StartUploadSessionApiResponse(
    [property: JsonPropertyName("uploadSessionId")] Guid                    UploadSessionId,
    [property: JsonPropertyName("runId")]           Guid                    RunId,
    [property: JsonPropertyName("fileId")]          Guid                    FileId,
    [property: JsonPropertyName("status")]          string                  Status,
    [property: JsonPropertyName("storageProvider")] string                  StorageProvider,
    [property: JsonPropertyName("bucketName")]      string                  BucketName,
    [property: JsonPropertyName("objectKey")]       string                  ObjectKey,
    [property: JsonPropertyName("partSizeBytes")]   long                    PartSizeBytes,
    [property: JsonPropertyName("totalBytes")]      long                    TotalBytes,
    [property: JsonPropertyName("totalParts")]      int                     TotalParts,
    [property: JsonPropertyName("uploadedBytes")]   long                    UploadedBytes,
    [property: JsonPropertyName("completedParts")]  CompletedPartItemDto[]  CompletedParts,
    [property: JsonPropertyName("expiresAt")]       DateTimeOffset?         ExpiresAt);

public sealed record SignPartsApiRequest(
    [property: JsonPropertyName("parts")] PartToSignDto[] Parts);

public sealed record PartToSignDto(
    [property: JsonPropertyName("partNumber")]  int  PartNumber,
    [property: JsonPropertyName("offsetBytes")] long OffsetBytes,
    [property: JsonPropertyName("lengthBytes")] long LengthBytes);

public sealed record SignPartsApiResponse(
    [property: JsonPropertyName("parts")] SignedPartDto[] Parts);

public sealed record SignedPartDto(
    [property: JsonPropertyName("partNumber")]  int            PartNumber,
    [property: JsonPropertyName("offsetBytes")] long           OffsetBytes,
    [property: JsonPropertyName("lengthBytes")] long           LengthBytes,
    [property: JsonPropertyName("uploadUrl")]   string         UploadUrl,
    [property: JsonPropertyName("expiresAt")]   DateTimeOffset ExpiresAt);

public sealed record CompletePartApiRequest(
    [property: JsonPropertyName("etag")]           string  ETag,
    [property: JsonPropertyName("sizeBytes")]       long    SizeBytes,
    [property: JsonPropertyName("checksumSha256")]  string? ChecksumSha256 = null);

public sealed record CompleteUploadApiResponse(
    [property: JsonPropertyName("remotePath")]      string RemotePath,
    [property: JsonPropertyName("storageProvider")] string StorageProvider,
    [property: JsonPropertyName("bucketName")]      string BucketName,
    [property: JsonPropertyName("objectKey")]       string ObjectKey,
    [property: JsonPropertyName("totalBytes")]      long   TotalBytes);

public sealed record FailUploadApiRequest(
    [property: JsonPropertyName("failureReason")]  string FailureReason,
    [property: JsonPropertyName("failureMessage")] string FailureMessage);

// ── Local upload checkpoint (written to disk for restart recovery) ────────────

public sealed class UploadCheckpoint
{
    [JsonPropertyName("runId")]          public Guid                RunId           { get; set; }
    [JsonPropertyName("fileId")]         public Guid                FileId          { get; set; }
    [JsonPropertyName("uploadSessionId")] public Guid               UploadSessionId { get; set; }
    [JsonPropertyName("filePath")]       public string              FilePath        { get; set; } = string.Empty;
    [JsonPropertyName("fileName")]       public string              FileName        { get; set; } = string.Empty;
    [JsonPropertyName("fileSizeBytes")]  public long                FileSizeBytes   { get; set; }
    [JsonPropertyName("partSizeBytes")]  public long                PartSizeBytes   { get; set; }
    [JsonPropertyName("completedParts")] public CheckpointPartDto[] CompletedParts  { get; set; } = [];
    [JsonPropertyName("updatedAtUtc")]   public DateTimeOffset      UpdatedAtUtc    { get; set; }
}

public sealed class CheckpointPartDto
{
    [JsonPropertyName("partNumber")]    public int     PartNumber    { get; set; }
    [JsonPropertyName("etag")]          public string  ETag          { get; set; } = string.Empty;
    [JsonPropertyName("checksumSha256")] public string? ChecksumSha256 { get; set; }
}

public sealed record EnrollApiRequest(
    [property: JsonPropertyName("enrollmentCode")] string EnrollmentCode,
    [property: JsonPropertyName("machineFingerprint")] string MachineFingerprint,
    [property: JsonPropertyName("hostname")] string Hostname,
    [property: JsonPropertyName("osDescription")] string OsDescription,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("agentVersion")] string AgentVersion);

public sealed record EnrollApiResponse(
    [property: JsonPropertyName("agentId")] Guid AgentId,
    [property: JsonPropertyName("clientSecret")] string ClientSecret);

public sealed record TokenApiRequest(
    [property: JsonPropertyName("agentId")] Guid AgentId,
    [property: JsonPropertyName("clientSecret")] string ClientSecret);

public sealed record TokenApiResponse(
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("expiresInSeconds")] int ExpiresInSeconds);

public enum AgentTapeDeviceStatus
{
    Unknown = 0,
    Present = 1,
    Ready = 2,
    NoMedia = 3,
    Busy = 4,
    PermissionDenied = 5,
    Unavailable = 6,
    Error = 7,
    Removed = 8,
}

/// <summary>
/// Cartridge lifecycle status for the UI device card. Derived from drive flags,
/// active tape operation, and the loader's preflight history. See <c>TapeMediaLoader</c>.
/// </summary>
public enum TapeMediaStatus
{
    Unknown        = 0,
    NoMedia        = 1,
    Identifying    = 3,
    Ready          = 4,
    Error          = 5,
    Reading        = 6,
    FastForwarding = 7,
    Rewinding      = 8,
    Ejecting       = 9,
    Empty          = 10,
}

public static class TapeMediaStatusExtensions
{
    // True while the cartridge is mid-motion or being identified.
    // Used as a pre-flight gate so the agent rejects a new operation when the
    // hardware is already doing something.
    public static bool IsBusy(this TapeMediaStatus status) => status is
        TapeMediaStatus.Identifying
        or TapeMediaStatus.Reading
        or TapeMediaStatus.FastForwarding
        or TapeMediaStatus.Rewinding
        or TapeMediaStatus.Ejecting;
}

public sealed class AgentTapeDeviceDto
{
    [JsonPropertyName("stableDeviceKey")]
    public string StableDeviceKey { get; set; } = string.Empty;

    [JsonPropertyName("linuxDevicePath")]
    public string LinuxDevicePath { get; set; } = string.Empty;

    [JsonPropertyName("nonRewindingDevicePath")]
    public string? NonRewindingDevicePath { get; set; }

    [JsonPropertyName("rewindingDevicePath")]
    public string? RewindingDevicePath { get; set; }

    [JsonPropertyName("sysfsPath")]
    public string? SysfsPath { get; set; }

    [JsonPropertyName("scsiAddress")]
    public string? ScsiAddress { get; set; }

    [JsonPropertyName("vendor")]
    public string? Vendor { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("revision")]
    public string? Revision { get; set; }

    [JsonPropertyName("mtStatusLabels")]
    public string? MtStatusLabels { get; set; }

    [JsonPropertyName("serialNumber")]
    public string? SerialNumber { get; set; }

    [JsonPropertyName("status")]
    public AgentTapeDeviceStatus Status { get; set; }

    [JsonPropertyName("isPresent")]
    public bool IsPresent { get; set; }

    [JsonPropertyName("isAccessible")]
    public bool IsAccessible { get; set; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }

    [JsonPropertyName("mediaStatus")]
    public TapeMediaStatus MediaStatus { get; set; }

    [JsonPropertyName("detectedBlockSizeBytes")]
    public int? DetectedBlockSizeBytes { get; set; }

    [JsonPropertyName("detectedBlockBufferSizeBytes")]
    public int? DetectedBlockBufferSizeBytes { get; set; }

    [JsonPropertyName("lastPreflightAt")]
    public DateTimeOffset? LastPreflightAt { get; set; }

    [JsonPropertyName("preflightError")]
    public string? PreflightError { get; set; }

    // User-configured per-drive read preferences. Pushed by the API after ReportTapeDevices
    // and on every UpdateTapeDeviceReadSettings command; never sent agent->API.
    [JsonPropertyName("autoDetectReadSettings")]
    public bool AutoDetectReadSettings { get; set; } = true;

    [JsonPropertyName("readBlockSizeBytes")]
    public int ReadBlockSizeBytes { get; set; }

    [JsonPropertyName("readBufferSizeBytes")]
    public int ReadBufferSizeBytes { get; set; } = 65536;
}

public sealed class AgentTapePreflightResultDto
{
    [JsonPropertyName("stableDeviceKey")]    public string StableDeviceKey    { get; set; } = string.Empty;
    [JsonPropertyName("linuxDevicePath")]    public string LinuxDevicePath    { get; set; } = string.Empty;
    [JsonPropertyName("preflightSucceeded")] public bool   PreflightSucceeded { get; set; }
    [JsonPropertyName("isEmpty")]            public bool   IsEmpty            { get; set; }
    [JsonPropertyName("blockSize")]          public int?   BlockSize          { get; set; }
    [JsonPropertyName("blockBufferSize")]    public int?   BlockBufferSize    { get; set; }
    /// <summary>
    /// All blocks returned by <see cref="IPreflightService"/> (up to InitialBlockCount).
    /// Detectors that need only the first block read <c>PreflightBlocks[0]</c>;
    /// future detectors may inspect further blocks.
    /// </summary>
    [JsonPropertyName("preflightBlocks")]    public byte[][]? PreflightBlocks { get; set; }
    [JsonPropertyName("errorMessage")]       public string? ErrorMessage      { get; set; }
    [JsonPropertyName("detectedAt")]         public DateTimeOffset DetectedAt { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(PendingUploadItem))]
[JsonSerializable(typeof(PendingUploadItem[]))]
[JsonSerializable(typeof(CompletedPartItemDto))]
[JsonSerializable(typeof(CompletedPartItemDto[]))]
[JsonSerializable(typeof(StartUploadSessionApiRequest))]
[JsonSerializable(typeof(StartUploadSessionApiResponse))]
[JsonSerializable(typeof(SignPartsApiRequest))]
[JsonSerializable(typeof(PartToSignDto))]
[JsonSerializable(typeof(PartToSignDto[]))]
[JsonSerializable(typeof(SignPartsApiResponse))]
[JsonSerializable(typeof(SignedPartDto))]
[JsonSerializable(typeof(SignedPartDto[]))]
[JsonSerializable(typeof(CompletePartApiRequest))]
[JsonSerializable(typeof(CompleteUploadApiResponse))]
[JsonSerializable(typeof(FailUploadApiRequest))]
[JsonSerializable(typeof(UploadCheckpoint))]
[JsonSerializable(typeof(CheckpointPartDto))]
[JsonSerializable(typeof(CheckpointPartDto[]))]
[JsonSerializable(typeof(EnrollApiRequest))]
[JsonSerializable(typeof(EnrollApiResponse))]
[JsonSerializable(typeof(TokenApiRequest))]
[JsonSerializable(typeof(TokenApiResponse))]
[JsonSerializable(typeof(AgentConfigFile))]
[JsonSerializable(typeof(AgentCredentialFile))]
[JsonSerializable(typeof(AgentTapeDeviceDto))]
[JsonSerializable(typeof(AgentTapeDeviceDto[]))]
[JsonSerializable(typeof(TapeMediaStatus))]
[JsonSerializable(typeof(AgentTapePreflightResultDto))]
[JsonSerializable(typeof(byte[][]))]
// ── Commands received from server ─────────────────────────────────────────────
[JsonSerializable(typeof(StartTapeRunCommand))]
[JsonSerializable(typeof(CancelTapeRunCommand))]
[JsonSerializable(typeof(CancelTapeRunUploadsCommand))]
[JsonSerializable(typeof(PauseTapeRunUploadCommand))]
[JsonSerializable(typeof(ResumeTapeRunUploadCommand))]
[JsonSerializable(typeof(ResumeRunUploadsCommand))]
[JsonSerializable(typeof(StopTapeRunReadingCommand))]
[JsonSerializable(typeof(ExecuteTapeMediaActionCommand))]
[JsonSerializable(typeof(AgentConfigCommand))]
[JsonSerializable(typeof(RefreshTapeDeviceCommand))]
[JsonSerializable(typeof(UpdateTapeDeviceReadSettingsCommand))]
// ── Device reporting ──────────────────────────────────────────────────────────
[JsonSerializable(typeof(TapeDeviceWireDto))]
[JsonSerializable(typeof(TapeDeviceWireDto[]))]
// ── Active operation snapshot ─────────────────────────────────────────────────
[JsonSerializable(typeof(ActiveOperationSnapshot))]
[JsonSerializable(typeof(ActiveOperationSnapshot[]))]
// ── Media detection ───────────────────────────────────────────────────────────
[JsonSerializable(typeof(MediaDetectionReport))]
// ── Media action result ────────────────────────────────────────────────────────
[JsonSerializable(typeof(TapeMediaActionResultReport))]
// ── Run lifecycle ─────────────────────────────────────────────────────────────
[JsonSerializable(typeof(TapeRunProgressReport))]
[JsonSerializable(typeof(TapeRunCompletedReport))]
// ── Operation lifecycle ───────────────────────────────────────────────────────
[JsonSerializable(typeof(TapeOperationStartedReport))]
[JsonSerializable(typeof(TapeOperationProgressReport))]
[JsonSerializable(typeof(TapeOperationCompletedReport))]
[JsonSerializable(typeof(TapeOperationEventReport))]
// ── File lifecycle ────────────────────────────────────────────────────────────
[JsonSerializable(typeof(TapeFileCreatedReport))]
[JsonSerializable(typeof(TapeFileReadProgressReport))]
[JsonSerializable(typeof(TapeFileReadCompletedReport))]
[JsonSerializable(typeof(TapeFileHashProgressReport))]
[JsonSerializable(typeof(TapeFileHashCompletedReport))]
[JsonSerializable(typeof(TapeFileUploadProgressReport))]
[JsonSerializable(typeof(TapeFileUploadFailedReport))]
internal partial class AgentJsonContext : JsonSerializerContext;
