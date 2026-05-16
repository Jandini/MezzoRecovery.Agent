using System.Text.Json;
using MezzoRecovery.Agent.Contracts;

namespace MezzoRecovery.Agent.Identity;

public static class CredentialStore
{
    public static async Task SaveAsync(string path, Guid agentId, string clientSecret, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new AgentCredentialFile { AgentId = agentId, ClientSecret = clientSecret };
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(data, AgentJsonContext.Default.AgentCredentialFile),
            ct);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static async Task<AgentCredentialFile?> TryLoadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(stream, AgentJsonContext.Default.AgentCredentialFile, ct);
    }
}
