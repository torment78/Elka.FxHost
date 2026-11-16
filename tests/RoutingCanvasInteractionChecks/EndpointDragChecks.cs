using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    /// <summary>
    /// Moving an FX In/Out node must not rebuild the canvas (it blinked): the card stays the
    /// same element, its cables follow during the drag, and the move becomes a Back step.
    /// Also checks that FX In/Out menus are built on right-click, not on every rebuild.
    /// </summary>
    internal void CheckEndpointDrag()
    {
        int checks = 0;
        void Check(bool condition, string message) { Assert(condition, message); checks++; }
        var savedNodes = _settings.PluginNodes;
        var savedConnections = _settings.CanvasConnections;
        var savedOffsets = new Dictionary<string, double>(_settings.EndpointCanvasYOffsets);
        var nativeBefore = CanvasInteractionChecks.NativeAudioAttempts;
        try
        {
            _workspaceView = WorkspaceView.Vst;
            _vstCanvasMode = CallbackMode.Input;
            var node = new PluginNodeSnapshot { Slot = 0, Name = "Drag EQ", X = 400, Y = 120 };
            _settings.PluginNodes = [node];
            var source = FxHostEdition.SideEndpoints(outputSide: false)[0];
            var channel = source.Range.ChannelAt(0);
            _settings.CanvasConnections =
            [
                new() { Kind = ConnectionEndpointToNode, FromKind = PinEndpointSource, FromMode = CallbackMode.Input,
                    FromChannel = channel, ToKind = PinNodeInput, ToMode = CallbackMode.Input, ToSlot = 0, ToPin = 0 }
            ];
            _settings.EndpointCanvasYOffsets.Clear();
            RebuildRoutingCanvas();
            UpdateLayout();

            var key = source.Key(CallbackMode.Input);
            var card = _endpointVisualElements[key].OfType<Border>().Single(b => b.Tag is EndpointDragInfo);
            var other = _endpointVisualElements[FxHostEdition.SideEndpoints(outputSide: false)[1].Key(CallbackMode.Input)]
                .OfType<Border>().First();
            var pin = _endpointVisualElements[key].OfType<Ellipse>().First();
            Check(card.ContextMenu is { Items.Count: 0 } && pin.ContextMenu is { Items.Count: 0 },
                "FX In/Out menus are placeholders until right-click (no menu building per rebuild)");
            card.RaiseEvent(new ContextMenuEventArgsFactory().Create(card));
            Check(card.ContextMenu is { Items.Count: > 0 }, "right-click builds the full FX In/Out menu");

            var cablePath = _canvasConnectionPaths.Values.Single();
            var startBefore = _pinPositions[EndpointSourceKey(CallbackMode.Input, channel)];
            var cardTop = Canvas.GetTop(card);
            int childrenBefore = RoutingCanvas.Children.Count;
            var historyReady = _canvasHistoryReady;
            _canvasHistoryReady = true;
            ResetCanvasHistory();
            _canvasHistory.ReplaceCurrent(CaptureCanvasEdit());

            EndpointCard_MouseLeftButtonDown(card, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            MoveDraggedEndpoint(30);
            MoveDraggedEndpoint(60);
            var cableNow = _canvasConnectionPaths.Values.Single();
            Check(Math.Abs(Canvas.GetTop(card) - (cardTop + 60)) < 0.01, "card follows the mouse");
            Check(Math.Abs(_pinPositions[EndpointSourceKey(CallbackMode.Input, channel)].Y - (startBefore.Y + 60)) < 0.01,
                "the cable anchor moves with the node");
            Check(cableNow.Data.Bounds.Top <= startBefore.Y + 60 + 0.5 && cableNow.Data.Bounds.Bottom >= startBefore.Y + 60 - 0.5,
                "the cable is redrawn to the moved contact during the drag");
            Check(_endpointVisualElements[key].Contains(card) && RoutingCanvas.Children.Contains(other),
                "cards are not rebuilt while dragging");

            EndpointCard_MouseLeftButtonUp(card, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Check(RoutingCanvas.Children.Contains(card) && RoutingCanvas.Children.Contains(other) &&
                  RoutingCanvas.Children.Count == childrenBefore, "dropping the node does not rebuild the canvas");
            Check(Math.Abs(_settings.EndpointCanvasYOffsets[key] - 60) < 0.01, "the new position is saved");
            CommitCanvasHistory();
            Check(_canvasHistory.Undo.Count == 1 && UndoCanvasButton.IsEnabled, "the move is a Back step and Back lights up");
            ApplyCanvasHistory();
            Check(Math.Abs(_settings.EndpointCanvasYOffsets.GetValueOrDefault(key)) < 0.01, "Back returns the node");
            Check(CanvasInteractionChecks.NativeAudioAttempts == nativeBefore, "moving a node makes no audio-engine calls");
            _canvasHistoryTimer?.Stop();
            _canvasHistoryReady = historyReady;
        }
        finally
        {
            _settings.PluginNodes = savedNodes;
            _settings.CanvasConnections = savedConnections;
            _settings.EndpointCanvasYOffsets.Clear();
            foreach (var (k, v) in savedOffsets) _settings.EndpointCanvasYOffsets[k] = v;
            ResetCanvasHistory();
            RebuildRoutingCanvas();
        }

        Console.WriteLine($"PASS: {checks} FX node drag checks (no rebuild, cables follow, Back step, menus on right-click).");
    }

    /// <summary>ContextMenuEventArgs has no public constructor.</summary>
    private sealed class ContextMenuEventArgsFactory
    {
        public ContextMenuEventArgs Create(object source)
        {
            var args = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
                [source, true], null)!;
            args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
            return args;
        }
    }
}
