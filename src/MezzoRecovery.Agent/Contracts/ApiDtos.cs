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
internal partial class AgentJsonContext : JsonSerializerContext;
