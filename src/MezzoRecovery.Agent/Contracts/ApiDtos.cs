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

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(EnrollApiRequest))]
[JsonSerializable(typeof(EnrollApiResponse))]
[JsonSerializable(typeof(TokenApiRequest))]
[JsonSerializable(typeof(TokenApiResponse))]
[JsonSerializable(typeof(AgentConfigFile))]
[JsonSerializable(typeof(AgentCredentialFile))]
internal partial class AgentJsonContext : JsonSerializerContext;
