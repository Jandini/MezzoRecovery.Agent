using System.CommandLine;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace MezzoRecovery.Agent;

internal static class Banner
{
    private const string Gold = "\u001b[1;38;5;220m";
    private const string BrightWhite = "\u001b[97m";
    private const string NormalWhite = "\u001b[37m";
    private const string CodeThemeString = "\u001b[38;5;216m";
    private const string LightBlue = "\u001b[94m";
    private const string Reset = "\u001b[0m";

    private const string UrlEncoded = "d3d3Lm1lenpvcmVjb3ZlcnkuY29t";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string Decode(string base64)
        => Encoding.UTF8.GetString(Convert.FromBase64String(base64));

    internal static void WriteBanner(this RootCommand _, string version)
    {
        Console.WriteLine(
            $"{Gold}{Decode("TWV6em8=")}{Reset}{BrightWhite}{Decode("UmVjb3Zlcnk=")}{Reset}" +
            $"{NormalWhite} Agent {Reset}{CodeThemeString}{version}{Reset}\n" +
            $"{LightBlue}{Decode(UrlEncoded)}{Reset}\n");
    }

    internal static string GetInformationalVersion(this Assembly assembly)
        => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? assembly.GetName().Version?.ToString()
           ?? "?";
}
