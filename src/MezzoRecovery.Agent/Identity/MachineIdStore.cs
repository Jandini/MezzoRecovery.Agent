namespace MezzoRecovery.Agent.Identity;

public static class MachineIdStore
{
    public static async Task<string> GetOrCreateAsync(string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path))
        {
            var existing = (await File.ReadAllTextAsync(path, ct)).Trim();
            if (existing.Length > 0)
                return existing;
        }

        var id = Guid.NewGuid().ToString("D");
        await File.WriteAllTextAsync(path, id, ct);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        return id;
    }
}
