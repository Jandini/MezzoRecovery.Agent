using MezzoRecovery.TapeDrive.Models;

namespace MezzoRecovery.Agent.Devices;

internal static class TapeGstatLabels
{
    public static string FormatShort(TapeGstatFlags flags)
    {
        if (flags == TapeGstatFlags.None)
            return string.Empty;

        var parts = new List<string>(12);
        if (flags.HasFlag(TapeGstatFlags.Eof)) parts.Add("EOF");
        if (flags.HasFlag(TapeGstatFlags.BeginningOfTape)) parts.Add("BOT");
        if (flags.HasFlag(TapeGstatFlags.EndOfTape)) parts.Add("EOT");
        if (flags.HasFlag(TapeGstatFlags.Setmark)) parts.Add("SM");
        if (flags.HasFlag(TapeGstatFlags.EndOfData)) parts.Add("EOD");
        if (flags.HasFlag(TapeGstatFlags.WriteProtected)) parts.Add("WR_PROT");
        if (flags.HasFlag(TapeGstatFlags.Online)) parts.Add("ONLINE");
        if (flags.HasFlag(TapeGstatFlags.DoorOpen)) parts.Add("DR_OPEN");
        if (flags.HasFlag(TapeGstatFlags.ImmediateReport)) parts.Add("IM_REP_EN");
        if (flags.HasFlag(TapeGstatFlags.CleaningRequested)) parts.Add("CLN");
        if (flags.HasFlag(TapeGstatFlags.Density6250)) parts.Add("D_6250");
        if (flags.HasFlag(TapeGstatFlags.Density1600)) parts.Add("D_1600");
        if (flags.HasFlag(TapeGstatFlags.Density800)) parts.Add("D_800");

        return string.Join(", ", parts);
    }
}
