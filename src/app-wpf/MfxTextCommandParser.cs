using System.Globalization;
using System.Text.RegularExpressions;

namespace Elka.VoiceMeeterFxHost.App;

internal enum MfxTextCommandTargetKind
{
    Strip,
    Bus,
    Vst
}

internal enum MfxTextCommandProperty
{
    Enable,
    Delay,
    Volume,
    Route,
    RouteEnable,
    RouteMuteNormal,
    Bypass,
    InputGain,
    OutputGain,
    MainGain,
    GainScale,
    DryGain,
    WetGain,
    Mix,
    Width,
    InputPan,
    OutputPan,
    DryPan,
    WetPan,
    Threshold,
    Ratio,
    Attack,
    Release,
    Hold,
    Knee,
    Range,
    MakeupGain,
    Ceiling,
    Lookahead,
    Rate,
    Depth,
    Feedback,
    Drive,
    Tone,
    Mode,
    Ab,
    Program,
    Editor,
    Reload,
    Parameter
}

internal enum MfxTextCommandOperator
{
    Set,
    Add,
    Subtract
}

internal sealed record MfxTextCommand(
    MfxTextCommandTargetKind TargetKind,
    string Target,
    MfxTextCommandChannelSelection Channels,
    MfxTextCommandProperty Property,
    MfxTextCommandOperator Operator,
    string ValueText,
    string SourceText,
    int? PluginParameterIndex = null);

internal sealed record MfxTextCommandChannelSelection(bool IsAll, IReadOnlyList<int> OneBasedChannels)
{
    public IEnumerable<int> GetZeroBasedChannels(int channelCount)
    {
        if (IsAll)
        {
            return Enumerable.Range(0, channelCount);
        }

        return OneBasedChannels
            .Where(channel => channel >= 1 && channel <= channelCount)
            .Select(channel => channel - 1)
            .Distinct();
    }
}

internal sealed record MfxRouteDestinationText(string BusTarget, int OneBasedChannel);

internal static partial class MfxTextCommandParser
{
    public static IReadOnlyList<MfxTextCommand> ParseScript(string script)
    {
        var commands = new List<MfxTextCommand>();
        foreach (var rawCommand in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(rawCommand))
            {
                continue;
            }

            commands.Add(ParseCommand(rawCommand));
        }

        return commands;
    }

    public static double ParseNumber(string valueText, string valueName)
    {
        var text = valueText.Trim().TrimEnd('%');
        if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^2].Trim();
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            throw new InvalidOperationException($"{valueName} must be a number.");
        }

        return value;
    }

    public static bool ParseBoolean(string valueText)
    {
        return valueText.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "on" or "yes" => true,
            "0" or "false" or "off" or "no" => false,
            _ => throw new InvalidOperationException("Value must be 1/0, true/false, or on/off.")
        };
    }

    public static MfxRouteDestinationText ParseRouteDestination(string valueText)
    {
        var match = RouteDestinationPattern().Match(valueText);
        if (!match.Success)
        {
            throw new InvalidOperationException("Route destination must be Bus(B1).Ch(3).");
        }

        var busTarget = match.Groups["bus"].Value.Trim();
        if (!int.TryParse(match.Groups["channel"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel)
            || channel < 1)
        {
            throw new InvalidOperationException("Route destination channel must be 1 or higher.");
        }

        return new MfxRouteDestinationText(busTarget, channel);
    }

    private static MfxTextCommand ParseCommand(string rawCommand)
    {
        var vstMatch = VstCommandPattern().Match(rawCommand);
        if (vstMatch.Success)
        {
            int? parameterIndex = null;
            if (vstMatch.Groups["parameterIndex"].Success)
            {
                if (!int.TryParse(vstMatch.Groups["parameterIndex"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedIndex))
                {
                    throw new InvalidOperationException("VST parameter index must be a non-negative whole number.");
                }

                parameterIndex = parsedIndex;
            }

            return new MfxTextCommand(
                MfxTextCommandTargetKind.Vst,
                vstMatch.Groups["target"].Value.Trim(),
                new MfxTextCommandChannelSelection(IsAll: true, []),
                ParseProperty(vstMatch.Groups["property"].Value),
                ParseOperator(vstMatch.Groups["op"].Value),
                vstMatch.Groups["value"].Value.Trim(),
                rawCommand,
                parameterIndex);
        }

        var match = CommandPattern().Match(rawCommand);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Unsupported MFX command: {rawCommand}");
        }

        var targetKind = ParseTargetKind(match.Groups["targetKind"].Value);
        var target = match.Groups["target"].Value.Trim();
        var channels = ParseChannels(match.Groups["channels"].Value);
        var property = ParseProperty(match.Groups["property"].Value);
        var op = ParseOperator(match.Groups["op"].Value);
        var valueText = match.Groups["value"].Value.Trim();

        return new MfxTextCommand(targetKind, target, channels, property, op, valueText, rawCommand);
    }

    private static MfxTextCommandTargetKind ParseTargetKind(string value)
    {
        return value.Equals("Strip", StringComparison.OrdinalIgnoreCase)
            ? MfxTextCommandTargetKind.Strip
            : MfxTextCommandTargetKind.Bus;
    }

    private static MfxTextCommandProperty ParseProperty(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "enable" or "enabled" => MfxTextCommandProperty.Enable,
            "delay" or "delayms" or "ms" => MfxTextCommandProperty.Delay,
            "volume" or "vol" or "gain" => MfxTextCommandProperty.Volume,
            "route" => MfxTextCommandProperty.Route,
            "routeenable" or "routeenabled" => MfxTextCommandProperty.RouteEnable,
            "routemute" or "muteroute" or "mutenormal" or "routemutenormal" => MfxTextCommandProperty.RouteMuteNormal,
            "bypass" or "bypassed" => MfxTextCommandProperty.Bypass,
            "inputgain" or "ingain" => MfxTextCommandProperty.InputGain,
            "outputgain" or "outgain" => MfxTextCommandProperty.OutputGain,
            "maingain" or "plugingain" or "gain" => MfxTextCommandProperty.MainGain,
            "gainscale" or "scale" => MfxTextCommandProperty.GainScale,
            "drygain" => MfxTextCommandProperty.DryGain,
            "wetgain" => MfxTextCommandProperty.WetGain,
            "mix" or "drywet" => MfxTextCommandProperty.Mix,
            "width" or "stereowidth" or "outputwidth" => MfxTextCommandProperty.Width,
            "inputpan" or "inpan" => MfxTextCommandProperty.InputPan,
            "outputpan" or "outpan" => MfxTextCommandProperty.OutputPan,
            "drypan" => MfxTextCommandProperty.DryPan,
            "wetpan" => MfxTextCommandProperty.WetPan,
            "threshold" or "thresh" => MfxTextCommandProperty.Threshold,
            "ratio" or "compressionratio" => MfxTextCommandProperty.Ratio,
            "attack" or "attacktime" => MfxTextCommandProperty.Attack,
            "release" or "releasetime" => MfxTextCommandProperty.Release,
            "hold" or "holdtime" => MfxTextCommandProperty.Hold,
            "knee" or "kneewidth" => MfxTextCommandProperty.Knee,
            "range" or "reductionrange" => MfxTextCommandProperty.Range,
            "makeupgain" or "makeup" => MfxTextCommandProperty.MakeupGain,
            "ceiling" or "outputceiling" => MfxTextCommandProperty.Ceiling,
            "lookahead" or "lookaheadtime" => MfxTextCommandProperty.Lookahead,
            "rate" or "speed" => MfxTextCommandProperty.Rate,
            "depth" => MfxTextCommandProperty.Depth,
            "feedback" => MfxTextCommandProperty.Feedback,
            "drive" => MfxTextCommandProperty.Drive,
            "tone" => MfxTextCommandProperty.Tone,
            "mode" => MfxTextCommandProperty.Mode,
            "ab" or "compare" => MfxTextCommandProperty.Ab,
            "program" or "preset" => MfxTextCommandProperty.Program,
            "editor" => MfxTextCommandProperty.Editor,
            "reload" => MfxTextCommandProperty.Reload,
            "parameter" => MfxTextCommandProperty.Parameter,
            _ => throw new InvalidOperationException($"Unsupported MFX property: {value}")
        };
    }

    private static MfxTextCommandOperator ParseOperator(string value)
    {
        return value switch
        {
            "+=" => MfxTextCommandOperator.Add,
            "-=" => MfxTextCommandOperator.Subtract,
            _ => MfxTextCommandOperator.Set
        };
    }

    private static MfxTextCommandChannelSelection ParseChannels(string value)
    {
        var text = value.Trim();
        if (text.Equals("all", StringComparison.OrdinalIgnoreCase) || text == "*")
        {
            return new MfxTextCommandChannelSelection(IsAll: true, []);
        }

        var channels = new List<int>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var rangeParts = part.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (rangeParts.Length == 2
                && int.TryParse(rangeParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)
                && int.TryParse(rangeParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var end)
                && start <= end)
            {
                channels.AddRange(Enumerable.Range(start, end - start + 1));
                continue;
            }

            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel))
            {
                throw new InvalidOperationException($"Channel must be a number, range, or All: {value}");
            }

            channels.Add(channel);
        }

        if (channels.Count == 0)
        {
            throw new InvalidOperationException("At least one channel must be selected.");
        }

        return new MfxTextCommandChannelSelection(IsAll: false, channels);
    }

    [GeneratedRegex(
        @"^\s*MFX\.VST\((?<target>\d+)\)\.(?:(?<property>Parameter)\((?<parameterIndex>\d+)\)|(?<property>Enable|Enabled|Bypass|Bypassed|InputGain|InGain|OutputGain|OutGain|MainGain|PluginGain|Gain|GainScale|Scale|DryGain|WetGain|Mix|DryWet|Width|StereoWidth|OutputWidth|InputPan|InPan|OutputPan|OutPan|DryPan|WetPan|Threshold|Thresh|Ratio|CompressionRatio|Attack|AttackTime|Release|ReleaseTime|Hold|HoldTime|Knee|KneeWidth|Range|ReductionRange|MakeupGain|Makeup|Ceiling|OutputCeiling|Lookahead|LookaheadTime|Rate|Speed|Depth|Feedback|Drive|Tone|Mode|AB|Compare|Program|Preset|Editor|Reload))\s*(?<op>\+=|-=|=)\s*(?<value>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VstCommandPattern();

    [GeneratedRegex(
        @"^\s*MFX\.(?<targetKind>Strip|Bus)\((?<target>[^)]+)\)\.Ch\((?<channels>[^)]+)\)\.(?<property>Enable|Enabled|Delay|DelayMs|Ms|Volume|Vol|Gain|Route|RouteEnable|RouteEnabled|RouteMute|MuteRoute|MuteNormal|RouteMuteNormal)\s*(?<op>\+=|-=|=)\s*(?<value>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandPattern();

    [GeneratedRegex(
        @"^\s*Bus\((?<bus>[^)]+)\)\.Ch\((?<channel>\d+)\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RouteDestinationPattern();
}
