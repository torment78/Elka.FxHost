using System.Windows;
using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed class MfxCommandsWindow : Window
{
    private readonly TextBox _commandsTextBox;

    public MfxCommandsWindow()
    {
        Title = "MFX Text Commands";
        Width = 820;
        Height = 680;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid
        {
            Margin = new Thickness(18)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock
        {
            Text = "MFX Text Command Reference",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        });

        _commandsTextBox = new TextBox
        {
            Text = CommandReference,
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 13,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(_commandsTextBox, 1);
        root.Children.Add(_commandsTextBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var copyButton = new Button
        {
            Content = "Copy",
            Width = 88,
            Margin = new Thickness(0, 0, 8, 0)
        };
        copyButton.Click += (_, _) => Clipboard.SetText(_commandsTextBox.Text);
        buttons.Children.Add(copyButton);

        var closeButton = new Button
        {
            Content = "Close",
            Width = 88
        };
        closeButton.Click += (_, _) => Close();
        buttons.Children.Add(closeButton);

        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
    }

    private const string CommandReference = """
Elka FX Host MFX Text Commands

MacroButtons sends these over VBAN-TEXT:

SendText("vban1", MFX.VST(0).Enable=Toggle;);

The MacroButtons VBAN-TEXT slot must use the same port and stream name as the app.
Default app settings are port 6981 and stream Command1.

Targets

In(...)  = a node on the FX In side:  In(A1), In(A1-A2), or its number from the top (0 = first).
Out(...) = a node on the FX Out side: Out(A1), Out(A1-A2), or its number from the top.
VST(...) = VST instance ID shown in the node's right-click menu and editor title.

Use node names: numbers move when nodes are linked or unlinked, names do not.

Channel selection is one-based:
Ch(1)
Ch(1-2)
Ch(1,3,5)
Ch(All)
Ch(*)

VST power and bypass by ID:
SendText("vban1", MFX.VST(0).Enable=1;);
SendText("vban1", MFX.VST(0).Enable=0;);
SendText("vban1", MFX.VST(0).Enable=Toggle;);
SendText("vban1", MFX.VST(0).Bypass=1;);
SendText("vban1", MFX.VST(0).Bypass=0;);
SendText("vban1", MFX.VST(0).Bypass=Toggle;);

Open Editor:
SendText("vban1", MFX.VST(0).Editor=Open;);

Close Editor:
SendText("vban1", MFX.VST(0).Editor=Close;);

Reload VST:
SendText("vban1", MFX.VST(0).Reload=1;);

Enable=0 stops VST processing and blocks the node output.
Bypass=1 sends dry audio around the node. If the VST is enabled, it still receives
audio so its meters can keep moving, but its processed output is discarded.
Enable=0 plus Bypass=1 keeps the VST off while dry audio passes around it.

Exposed VST controls (only when a matching host parameter exists):
SendText("vban1", MFX.VST(0).InputGain=-6 dB;);
SendText("vban1", MFX.VST(0).OutputGain=-3 dB;);
SendText("vban1", MFX.VST(0).MainGain=0 dB;);
SendText("vban1", MFX.VST(0).GainScale=100%;);
SendText("vban1", MFX.VST(0).DryGain=-12 dB;);
SendText("vban1", MFX.VST(0).WetGain=0 dB;);
SendText("vban1", MFX.VST(0).Mix=50%;);
SendText("vban1", MFX.VST(0).Width=100%;);
SendText("vban1", MFX.VST(0).InputPan=25%;);
SendText("vban1", MFX.VST(0).OutputPan=-25%;);
SendText("vban1", MFX.VST(0).DryPan=-25%;);
SendText("vban1", MFX.VST(0).WetPan=25%;);
SendText("vban1", MFX.VST(0).Threshold=-18 dB;);
SendText("vban1", MFX.VST(0).Ratio=4.0;);
SendText("vban1", MFX.VST(0).Attack=10 ms;);
SendText("vban1", MFX.VST(0).Release=100 ms;);
SendText("vban1", MFX.VST(0).Hold=25 ms;);
SendText("vban1", MFX.VST(0).Knee=6 dB;);
SendText("vban1", MFX.VST(0).Range=24 dB;);
SendText("vban1", MFX.VST(0).MakeupGain=3 dB;);
SendText("vban1", MFX.VST(0).Ceiling=-1 dB;);
SendText("vban1", MFX.VST(0).Lookahead=2 ms;);
SendText("vban1", MFX.VST(0).Rate=1.5 Hz;);
SendText("vban1", MFX.VST(0).Depth=50%;);
SendText("vban1", MFX.VST(0).Feedback=25%;);
SendText("vban1", MFX.VST(0).Drive=6 dB;);
SendText("vban1", MFX.VST(0).Tone=50%;);
SendText("vban1", MFX.VST(0).Mode=Next;);
SendText("vban1", MFX.VST(0).AB=B;);
SendText("vban1", MFX.VST(0).AB=Toggle;);

Host programs/presets when the VST exposes more than one:
SendText("vban1", MFX.VST(0).Program=1;);
SendText("vban1", MFX.VST(0).Program=Next;);
SendText("vban1", MFX.VST(0).Program=Previous;);

Any exposed host parameter by index:
SendText("vban1", MFX.VST(0).Parameter(580)=-6 dB;);

Relative numeric VST adjustments:
SendText("vban1", MFX.VST(0).InputGain+=1 dB;);
SendText("vban1", MFX.VST(0).OutputGain-=1 dB;);
SendText("vban1", MFX.VST(0).Parameter(580)+=0.5 dB;);

+= adds to the current displayed value and -= subtracts from it. The forms =5,
=+5, and =-5 are absolute assignments and do not accumulate on repeated presses.
Relative values are clamped to the limits reported by the VST. Ratio and unit values
use their displayed numbers: =4.5 sets 4.5:1, +=0.05 adds 0.05 to the ratio, and
Parameter(<attack index>)=10 ms sets ten milliseconds. Named stepped values are numbered from 1 in Info,
for example 1=Clean and 2=Vocal. They also accept Next, Previous, and Default.
Two-position parameters accept Toggle. Minimum and Maximum commands are not provided.

Use right-click Info on the loaded VST to find its parameter indexes. Indexes belong
to that exact plugin version and may change when the plugin is updated.

Enable delay/volume processing:
SendText("vban1", MFX.In(A1).Ch(1).Enable=1;);
SendText("vban1", MFX.In(A1).Ch(1).Enable=0;);
SendText("vban1", MFX.Out(A1).Ch(1).Enable=1;);

Delay:
SendText("vban1", MFX.In(A1).Ch(1).Delay=25;);
SendText("vban1", MFX.In(A1).Ch(1).Delay+=10;);
SendText("vban1", MFX.In(A1).Ch(1).Delay-=10;);
SendText("vban1", MFX.Out(A1).Ch(1).Delay=25;);

Volume:
SendText("vban1", MFX.In(A1).Ch(1).Volume=100;);
SendText("vban1", MFX.In(A1).Ch(1).Volume+=5;);
SendText("vban1", MFX.In(A1).Ch(1).Volume-=5;);
SendText("vban1", MFX.Out(A1-A2).Ch(All).Volume=100;);

Direct routing, FX In to FX Out:
SendText("vban1", MFX.In(A1).Ch(1).Route=Out(B1).Ch(1););
SendText("vban1", MFX.In(A1).Ch(1).Route+=Out(B2).Ch(1););
SendText("vban1", MFX.In(A1).Ch(1).Route-=Out(B1).Ch(1););

Enable saved route destinations:
SendText("vban1", MFX.In(A1).Ch(1).RouteEnable=1;);
SendText("vban1", MFX.In(A1).Ch(1).RouteEnable=0;);

Mute standard routing for a routed FX In channel:
SendText("vban1", MFX.In(A1).Ch(1).MuteNormal=1;);
SendText("vban1", MFX.In(A1).Ch(1).MuteNormal=0;);

Combined commands:
SendText("vban1", MFX.In(A6).Ch(1).Route=Out(B1).Ch(1); MFX.In(A6).Ch(1).MuteNormal=1; MFX.In(A6).Ch(1).Delay=20;);

Aliases

Enable: Enable, Enabled
Delay: Delay, DelayMs, Ms
Volume: Volume, Vol, Gain
Route enable: RouteEnable, RouteEnabled
Mute normal: MuteNormal, RouteMute, MuteRoute, RouteMuteNormal
VST bypass: Bypass, Bypassed
VST program: Program, Preset
VST parameter aliases: InGain, OutGain, Gain, PluginGain, Scale, DryWet, StereoWidth, OutputWidth, InPan, OutPan, Thresh, CompressionRatio, AttackTime, ReleaseTime, HoldTime, KneeWidth, ReductionRange, Makeup, OutputCeiling, LookaheadTime, Speed, Compare
Boolean values: 1, 0, true, false, on, off, yes, no

Right-click a loaded VST and choose Info to see the friendly VBAN-TEXT commands
for that exact VST ID followed by every host-automatable parameter and its indexed
Parameter(index) command. Unsupported friendly controls are omitted. Values
use the VST's displayed format. The wrapper verifies the conversion and resolves numeric
display values when a plugin's text conversion is broken. GainScale uses the plugin's
displayed percentage format, such as 100% or 200%. A/B is available only
when the plugin exposes A/B as a host parameter.

This command surface controls delay, volume, direct routing, mute-standard routing,
VST power/bypass, editor visibility, targeted VST reload, exposed host programs,
friendly dynamics/effect controls, and indexed exposed parameters by stable ID. It does not load
new plugins or alter VST node wiring.
""";
}
