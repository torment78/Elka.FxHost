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

    /// <summary>Mono channel short name as the driver reports it: A1, A2 ... P8.</summary>
    public static string ChannelShortName(int channel) =>
        $"{BankLetters(channel / ChannelsPerBank)}{channel % ChannelsPerBank + 1}";

    /// <summary>Full channel name as Matrix shows it: "FX In A1" / "FX Out A1".</summary>
    public static string ChannelName(int channel, bool outputSide) =>
        (outputSide ? "FX Out " : "FX In ") + ChannelShortName(channel);

    // ---- Mono nodes and linked groups ---------------------------------------------
    // Every ASIO channel is its own mono node. Adjacent channels can be linked into one
    // node (stereo A1-A2, or A1-A6 ...). The FX In and FX Out sides are linked
    // separately. Cables always attach per channel, so linking never changes routing.

    private static readonly List<(int Start, int End)>[] Links = [[], []];
    private static readonly IReadOnlyList<IoEndpoint>?[] SideCache = [null, null];

    private static int SideIndex(bool outputSide) => outputSide ? 1 : 0;

    public static string LinkedEndpointName(int start, int end) =>
        start == end ? $"FX {ChannelShortName(start)}" : $"FX {ChannelShortName(start)}-{ChannelShortName(end)}";

    /// <summary>The nodes on one side: mono channels, with linked ranges merged.</summary>
    public static IReadOnlyList<IoEndpoint> SideEndpoints(bool outputSide)
    {
        var index = SideIndex(outputSide);
        if (SideCache[index] is { } cached) return cached;

        var endpoints = new List<IoEndpoint>();
        var links = Links[index];
        var channel = 0;
        while (channel < ChannelCount)
        {
            var link = links.FirstOrDefault(range => range.Start == channel);
            var end = link.End > link.Start ? Math.Min(link.End, ChannelCount - 1) : channel;
            endpoints.Add(new IoEndpoint(LinkedEndpointName(channel, end), new ChannelRange(channel, end)));
            channel = end + 1;
        }

        SideCache[index] = endpoints;
        return endpoints;
    }

    /// <summary>Both sides' nodes, for lookups by key.</summary>
    public static IEnumerable<IoEndpoint> AllEndpoints() =>
        SideEndpoints(false).Concat(SideEndpoints(true)).DistinctBy(static endpoint => (endpoint.Name, endpoint.Range));

    public static void LoadLinks(IEnumerable<string>? inLinks, IEnumerable<string>? outLinks)
    {
        Links[0].Clear();
        Links[1].Clear();
        foreach (var (side, values) in new[] { (0, inLinks), (1, outLinks) })
        {
            foreach (var value in values ?? [])
            {
                var parts = value.Split('-');
                if (parts.Length == 2 && int.TryParse(parts[0], out var start) && int.TryParse(parts[1], out var end) &&
                    start >= 0 && end > start && end < ChannelCount &&
                    !Links[side].Any(range => start <= range.End && end >= range.Start))
                {
                    Links[side].Add((start, end));
                }
            }
        }

        SideCache[0] = SideCache[1] = null;
    }

    public static List<string> SaveLinks(bool outputSide) =>
        Links[SideIndex(outputSide)].OrderBy(static range => range.Start).Select(static range => $"{range.Start}-{range.End}").ToList();

    /// <summary>Links the selected nodes on one side into one node. They must be adjacent.</summary>
    public static bool TryLink(bool outputSide, IEnumerable<IoEndpoint> selected, out string message)
    {
        var channels = selected.SelectMany(static endpoint => Enumerable.Range(endpoint.Range.Start, endpoint.ChannelCount))
            .Distinct().OrderBy(static channel => channel).ToList();
        var side = outputSide ? "FX Out" : "FX In";
        if (channels.Count < 2)
        {
            message = $"Select at least two {side} nodes to link.";
            return false;
        }

        var start = channels[0];
        var end = channels[^1];
        if (end - start + 1 != channels.Count)
        {
            message = $"{side} nodes must be next to each other to link ({ChannelShortName(start)} to {ChannelShortName(end)} has gaps).";
            return false;
        }

        var links = Links[SideIndex(outputSide)];
        links.RemoveAll(range => range.Start >= start && range.End <= end);
        if (links.Any(range => start <= range.End && end >= range.Start))
        {
            message = $"{side} {ChannelShortName(start)}-{ChannelShortName(end)} overlaps another linked node. Unlink it first.";
            return false;
        }

        links.Add((start, end));
        SideCache[SideIndex(outputSide)] = null;
        message = $"Linked {side} {ChannelShortName(start)}-{ChannelShortName(end)} ({channels.Count} channels).";
        return true;
    }

    public static bool Unlink(bool outputSide, IoEndpoint endpoint)
    {
        var removed = Links[SideIndex(outputSide)].RemoveAll(range => range.Start == endpoint.Range.Start && range.End == endpoint.Range.End) > 0;
        if (removed) SideCache[SideIndex(outputSide)] = null;
        return removed;
    }

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
