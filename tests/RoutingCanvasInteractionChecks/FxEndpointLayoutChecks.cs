using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    /// <summary>
    /// FX In / FX Out cards after the compact-node update: 112 DIPs wide, unchanged heights and
    /// 12-DIP pin inset, with matching pin/label/meter gaps on both sides.
    /// </summary>
    internal void CheckFxEndpointLayout()
    {
        var savedInLinks = FxHostEdition.SaveLinks(outputSide: false);
        var savedOutLinks = FxHostEdition.SaveLinks(outputSide: true);
        int checks = 0;
        void Check(bool condition, string message) { Assert(condition, message); checks++; }
        try
        {
            _workspaceView = WorkspaceView.Vst;
            _vstCanvasMode = CallbackMode.Input;
            _settings.NodeVuMetersEnabled = true;
            FxHostEdition.LoadLinks(["0,1"], ["0,1"]);
            RebuildRoutingCanvas();
            UpdateLayout();
            Check(_endpointMeters.Count > 0, "FX In and FX Out cards show meters");
            RenderCompactRegion(RoutingCanvas, new Rect(0, 0, Math.Min(RoutingCanvas.ActualWidth, 1500), 420), "fx-endpoints.png");
            foreach (var entry in _endpointMeters)
            {
                var endpoint = FxHostEdition.SideEndpoints(entry.Address.OutputSide)
                    .First(e => e.Range.Contains(entry.Address.FirstChannel));
                var elements = _endpointVisualElements[endpoint.Key(CallbackMode.Input)];
                var border = elements.OfType<Border>().Single(b => b.Tag is EndpointDragInfo info && info.OutputSide == entry.Address.OutputSide);
                Check(border.Width == 112, "FX endpoint card is 112 DIPs wide");
                Check(entry.View.Width == 112 - VstCardPinInset - EndpointPinLabelOffset - EndpointPinLabelWidth - EndpointMeterLabelGap - EndpointMeterOuterInset,
                    "meter fills the space between its label and the outer edge");
                var pin = elements.OfType<Ellipse>().First(p => p.Tag is CanvasPinInfo info &&
                    info.Channel == entry.Address.FirstChannel && (info.Kind == PinEndpointDestination) == entry.Address.OutputSide);
                Check(pin.Width == 10 && pin.Height == 10 && pin.StrokeThickness == 1.5, "endpoint contacts are 10 DIPs with a 1.5-DIP outline");
                double pinX = Canvas.GetLeft(pin) + pin.Width / 2;
                double left = Canvas.GetLeft(border);
                Check(Math.Abs((entry.Address.OutputSide ? pinX - left : left + border.Width - pinX) - VstCardPinInset) < 0.01,
                    "endpoint contacts keep their 12-DIP inset (not moved to the card edge)");
                Check(Math.Abs(Canvas.GetTop(pin) + pin.Height / 2 - Canvas.GetTop(border) - VstCardPinTopOffset) < 0.01,
                    "first endpoint contact keeps its vertical position");
                double rowY = Canvas.GetTop(pin) + pin.Height / 2;
                var label = elements.OfType<TextBlock>().FirstOrDefault(t => t.Width == EndpointPinLabelWidth &&
                    Math.Abs(Canvas.GetTop(t) + 7 - rowY) < 1.01 &&
                    Canvas.GetLeft(t) >= left && Canvas.GetLeft(t) < left + border.Width);
                Check(label is not null, "first channel label sits on the contact's row");
                double labelX = Canvas.GetLeft(label!);
                double meterX = Canvas.GetLeft(entry.View);
                Check(Math.Abs((entry.Address.OutputSide ? labelX - pinX : pinX - labelX - label!.Width) - EndpointPinLabelOffset) < 0.01,
                    "labels sit equally close to source and destination contacts");
                Check(Math.Abs((entry.Address.OutputSide ? meterX - labelX - label!.Width : labelX - meterX - entry.View.Width) - EndpointMeterLabelGap) < 0.01,
                    "meter and label have matching compact gaps on both sides");
                Check(Math.Abs(Math.Abs(labelX + label!.Width / 2 - pinX) - 14) < 0.01, "label centres stay 14 DIPs from the contact");
                foreach (var widest in new[] { label.Text, "P8", "M8" })
                {
                    var text = new System.Windows.Media.FormattedText(widest, System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, new System.Windows.Media.Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
                        label.FontSize, System.Windows.Media.Brushes.White, 1);
                    Check(text.WidthIncludingTrailingWhitespace <= label.Width, $"two-character channel label {widest} is not clipped");
                }
            }
        }
        finally
        {
            FxHostEdition.LoadLinks(savedInLinks, savedOutLinks);
            RebuildRoutingCanvas();
        }

        Console.WriteLine($"PASS: {checks} FX endpoint layout checks (112-DIP cards, inset contacts, label and meter gaps).");
    }
}
