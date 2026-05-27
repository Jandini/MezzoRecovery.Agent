using System.Text.Json.Serialization;

namespace MezzoRecovery.Agent.Contracts;

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
    CleaningRequired = 10,
}

/// <summary>
/// Cartridge lifecycle status for the UI device card. Derived from drive flags,
/// active tape operation, and the loader's preflight history. See <c>TapeMediaLoader</c>.
/// </summary>
public enum TapeMediaStatus
{
    Unknown = 0,
    NoMedia = 1,
    Loaded = 2,
    Identifying = 3,
    Ready = 4,
    Error = 5,
    Reading = 6,
    FastForwarding = 7,
    Rewinding = 8,
    Ejecting = 9,
    CleaningRequired = 10,
    Empty = 11,
}

public static class TapeMediaStatusExtensions
{
    // True while the cartridge is mid-motion. Used as a pre-flight gate so the
    // agent rejects a new operation when the hardware is already doing something
    // (e.g. the operator pressed the physical eject button on the drive).
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
