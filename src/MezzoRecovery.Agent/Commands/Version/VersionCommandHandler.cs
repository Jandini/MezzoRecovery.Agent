using System.Reflection;

namespace MezzoRecovery.Agent.Commands.Version;

internal sealed class VersionCommandHandler
{
    public int Run()
    {
        var v = typeof(VersionCommandHandler).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(VersionCommandHandler).Assembly.GetName().Version?.ToString()
                ?? "0.0.0";
        Console.WriteLine(v);
        return 0;
    }
}
