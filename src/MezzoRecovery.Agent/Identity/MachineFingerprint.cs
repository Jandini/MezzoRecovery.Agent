using System.Security.Cryptography;
using System.Text;

namespace MezzoRecovery.Agent.Identity;

public static class MachineFingerprint
{
    public static string Compute(string machineId, string hostname)
    {
        var bytes = Encoding.UTF8.GetBytes($"{machineId}|{hostname}");
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
