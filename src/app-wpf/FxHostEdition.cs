using Microsoft.Win32;

namespace Elka.VoiceMeeterFxHost.App;

/// <summary>
/// Elka FX Host: the VST host sits behind its own virtual ASIO driver ("Elka FX Host")
/// instead of VoiceMeeter. Matrix (or any ASIO host) sends audio to "FX In A1".."P8",
/// the canvas routes it through VSTs, and anything wired to the right side comes back
/// on "FX Out A1".."P8". There is one in/out canvas; the VoiceMeeter callback, Patch.insert
/// and the delay workspace are not used.
/// </summary>
internal static class FxHostEdition
{
    public const bool AsioOnly = true;
    public const string ProductName = "Elka FX Host";
    public const int ChannelsPerBank = 8;
    public const int MinChannels = 8;
    public const int MaxChannels = 128;
    public const int MatrixChannels = 32;
    public const int MatrixCoconutChannels = 64;

    private const string SettingsKey = @"Software\ElkaSoft\FxHost\Asio";
    private const string ChannelCountValue = "ChannelCount";

    /// <summary>Channel count read once at startup; changing it restarts the app.</summary>
    public static int ChannelCount { get; } = ReadChannelCount();

    public static int BankCount => ChannelCount / ChannelsPerBank;

    public static int Sanitize(int channels)
    {
        if (channels <= 0) return MatrixCoconutChannels;
        var rounded = (channels + ChannelsPerBank - 1) / ChannelsPerBank * ChannelsPerBank;
        return Math.Clamp(rounded, MinChannels, MaxChannels);
    }

    public static string PresetLabel(int channels) => channels switch
    {
        MatrixChannels => "32 ch - Matrix",
        MatrixCoconutChannels => "64 ch - Matrix Coconut",
        _ => $"{channels} ch - Custom"
    };

    /// <summary>Bank letters A..Z, then AA, AB, ... (same scheme as the driver).</summary>
    public static string BankLetters(int bank)
    {
        var letters = string.Empty;
        var value = bank;
        do
        {
            letters = (char)('A' + value % 26) + letters;
            value = value / 26 - 1;
        } while (value >= 0);
        return letters;
    }

    public static string BankEndpointName(int bank) => $"FX {BankLetters(bank)}";

    /// <summary>Left side shows what Matrix sends in, right side what goes back out.</summary>
    public static string SideName(string endpointName, bool outputSide) =>
        endpointName.StartsWith("FX ", StringComparison.Ordinal)
            ? (outputSide ? "FX Out " : "FX In ") + endpointName[3..]
            : endpointName;

    public static int ReadChannelCount()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(SettingsKey);
                if (key?.GetValue(ChannelCountValue) is int value)
                    return Sanitize(value);
            }
            catch
            {
                // Fall through to the default.
            }
        }

        return MatrixCoconutChannels;
    }
}
