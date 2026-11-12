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
    // Every ASIO channel is its own mono node. Any channels can be linked into one node,
    // in the order they were Ctrl-clicked (A1+A2 stereo, A1-A6, or A1+A3+A5). The node
    // sits where its lowest channel was. FX In and FX Out link separately. Cables attach
    // per channel, so linking never changes routing.

    private static readonly List<int[]>[] Links = [[], []];
    private static readonly IReadOnlyList<IoEndpoint>?[] SideCache = [null, null];

    private static int SideIndex(bool outputSide) => outputSide ? 1 : 0;

    public static string LinkedEndpointName(IReadOnlyList<int> channels)
    {
        if (channels.Count == 1)
            return $"FX {ChannelShortName(channels[0])}";
        var contiguous = channels.Select((channel, index) => channel - index).Distinct().Count() == 1;
        return contiguous
            ? $"FX {ChannelShortName(channels[0])}-{ChannelShortName(channels[^1])}"
            : "FX " + string.Join("+", channels.Select(ChannelShortName));
    }

    /// <summary>The nodes on one side: mono channels, with linked groups merged.</summary>
    public static IReadOnlyList<IoEndpoint> SideEndpoints(bool outputSide)
    {
        var index = SideIndex(outputSide);
        if (SideCache[index] is { } cached) return cached;

        var links = Links[index];
        var linkedChannels = links.SelectMany(static link => link).ToHashSet();
        var endpoints = new List<IoEndpoint>();
        for (var channel = 0; channel < ChannelCount; channel++)
        {
            if (!linkedChannels.Contains(channel))
            {
                endpoints.Add(new IoEndpoint(LinkedEndpointName([channel]), new ChannelRange(channel, channel)));
                continue;
            }

            var link = links.FirstOrDefault(candidate => candidate.Min() == channel);
            if (link is not null)
                endpoints.Add(new IoEndpoint(LinkedEndpointName(link), ChannelRange.FromChannels(link)));
        }

        SideCache[index] = endpoints;
        return endpoints;
    }

    /// <summary>Both sides' nodes, for lookups by key.</summary>
    public static IEnumerable<IoEndpoint> AllEndpoints() =>
        SideEndpoints(false).Concat(SideEndpoints(true)).DistinctBy(static endpoint => (endpoint.Name, endpoint.Range));

    /// <summary>Saved as "0,2,4" (click order); older "start-end" entries still load.</summary>
    public static void LoadLinks(IEnumerable<string>? inLinks, IEnumerable<string>? outLinks)
    {
        Links[0].Clear();
        Links[1].Clear();
        foreach (var (side, values) in new[] { (0, inLinks), (1, outLinks) })
        {
            foreach (var value in values ?? [])
            {
                var channels = ParseLink(value);
                if (channels is null || channels.Length < 2 || channels.Any(channel => channel < 0 || channel >= ChannelCount) ||
                    channels.Distinct().Count() != channels.Length ||
                    Links[side].Any(existing => existing.Intersect(channels).Any()))
                    continue;
                Links[side].Add(channels);
            }
        }

        SideCache[0] = SideCache[1] = null;
    }

    private static int[]? ParseLink(string value)
    {
        var parts = value.Split('-');
        if (parts.Length == 2 && int.TryParse(parts[0], out var start) && int.TryParse(parts[1], out var end) && end > start)
            return Enumerable.Range(start, end - start + 1).ToArray();
        var list = value.Split(',');
        var channels = new int[list.Length];
        for (var index = 0; index < list.Length; index++)
            if (!int.TryParse(list[index], out channels[index])) return null;
        return channels;
    }

    public static List<string> SaveLinks(bool outputSide) =>
        Links[SideIndex(outputSide)].OrderBy(static link => link.Min()).Select(static link => string.Join(",", link)).ToList();

    /// <summary>Links the selected nodes on one side into one node, in selection order.</summary>
    public static bool TryLink(bool outputSide, IEnumerable<IoEndpoint> selected, out string message)
    {
        var channels = selected.SelectMany(static endpoint => endpoint.Range.Channels).Distinct().ToArray();
        var side = outputSide ? "FX Out" : "FX In";
        if (channels.Length < 2)
        {
            message = $"Select at least two {side} nodes to link.";
            return false;
        }

        // Selecting a linked node selects all of its channels, so it is absorbed whole.
        var links = Links[SideIndex(outputSide)];
        links.RemoveAll(link => link.Intersect(channels).Any());
        links.Add(channels);
        SideCache[SideIndex(outputSide)] = null;
        message = $"Linked {side} {LinkedEndpointName(channels)[3..]} ({channels.Length} channels).";
        return true;
    }

    public static bool Unlink(bool outputSide, IoEndpoint endpoint)
    {
        var channels = endpoint.Range.Channels.ToHashSet();
        var removed = Links[SideIndex(outputSide)].RemoveAll(link => channels.SetEquals(link)) > 0;
        if (removed) SideCache[SideIndex(outputSide)] = null;
        return removed;
    }

    /// <summary>Removes the given channels from a linked node. Fewer than two left = fully unlinked.</summary>
    public static bool RemoveFromLink(bool outputSide, IoEndpoint endpoint, IReadOnlyList<int> remove, out string message)
    {
        var side = outputSide ? "FX Out" : "FX In";
        var index = SideIndex(outputSide);
        var members = endpoint.Range.Channels.ToHashSet();
        var link = Links[index].FirstOrDefault(candidate => members.SetEquals(candidate));
        message = string.Empty;
        if (link is null || remove.Count == 0) return false;

        var kept = link.Where(channel => !remove.Contains(channel)).ToArray();
        Links[index].Remove(link);
        if (kept.Length >= 2) Links[index].Add(kept);
        SideCache[index] = null;
        var removed = string.Join(", ", link.Where(remove.Contains).Select(ChannelShortName));
        message = kept.Length >= 2
            ? $"Unlinked {side} {removed} from {LinkedEndpointName(link)[3..]}; {LinkedEndpointName(kept)[3..]} stays linked."
            : $"Unlinked {side} {LinkedEndpointName(link)[3..]} into mono nodes.";
        return true;
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
