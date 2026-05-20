namespace MezzoRecovery.Agent.Configuration;

/// <summary>
/// Controls the autonomous tape-media lifecycle loader: when a cartridge is detected
/// the loader runs preflight to identify its block size, and the result is published
/// back to the API alongside the device.
/// </summary>
public sealed class TapeMediaLoaderOptions
{
    public const string SectionName = "TapeMediaLoader";

    /// <summary>When false, no autonomous preflight is triggered; operators must initiate manually.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Number of leading blocks to read during preflight to estimate the block size.</summary>
    public int InitialBlockCount { get; set; } = 10;

    /// <summary>Rewind to BOT before preflight. Leave true unless caller proves tape is at BOT already.</summary>
    public bool RewindBeforeStart { get; set; } = true;
}
