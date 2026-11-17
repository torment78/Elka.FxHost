using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private readonly List<(EndpointMeterAddress Address, EndpointMeterView View, EndpointPeakState Peak, EndpointPeakIndicator Indicator)> _endpointMeters = [];
    private readonly Dictionary<EndpointMeterAddress, EndpointMeterView> _endpointMeterViewsForRebuild = [];
    private readonly Dictionary<(VoicemeeterKind Kind, EndpointMeterAddress Address), EndpointPeakState> _endpointPeakStates = [];
    private readonly float[] _endpointMeterPeaks = new float[8];
    private bool _endpointMetersInitialized;
    private volatile bool _endpointMeterRendering;
    private long _endpointMeterRenderBucket = -1;
    private EndpointMeterReader? _endpointMeterReader;
    private IEndpointPeakSource _endpointPeakSource = new NativeEndpointPeakSource();
    private bool _endpointMeterUpdateQueued;

    private void InitializeEndpointMeters()
    {
        if (_endpointMetersInitialized) return;
        _endpointMetersInitialized = true;
        IsVisibleChanged += (_, _) => UpdateEndpointMeterActivity();
        StateChanged += (_, _) => UpdateEndpointMeterActivity();
        RoutingCanvas.IsVisibleChanged += (_, _) => QueueEndpointMeterUpdate();
        UpdateEndpointMeterActivity();
    }

    private void RenderEndpointMeters(object? sender, EventArgs args)
    {
        if (!_endpointMeterRendering || _endpointMeterReader is not { } reader) return;
        if (reader.Failure is { } failure)
        {
            StopEndpointMeters();
            AppendLog($"Endpoint meters unavailable: {failure.Message}");
            return;
        }
        long timestamp = Stopwatch.GetTimestamp();
        long bucket = timestamp / Math.Max(1, Stopwatch.Frequency / EndpointMeterReader.RefreshHz);
        if (bucket == _endpointMeterRenderBucket) return;
        _endpointMeterRenderBucket = bucket;
        double now = timestamp / (double)Stopwatch.Frequency;
        for (int i = 0; i < _endpointMeters.Count; i++)
        {
            var item = _endpointMeters[i];
            bool fresh = reader.Take(i, _endpointMeterPeaks);
            item.View.Advance(fresh ? _endpointMeterPeaks : null, now);
            item.Indicator.Refresh();
        }
    }

    private void StopEndpointMeterRendering()
    {
        if (_endpointMeterRendering) CompositionTarget.Rendering -= RenderEndpointMeters;
        _endpointMeterRendering = false;
        _endpointMeterRenderBucket = -1;
    }

    private void QueueEndpointMeterUpdate()
    {
        if (!_endpointMetersInitialized || _endpointMeterUpdateQueued || _isShuttingDown) return;
        _endpointMeterUpdateQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _endpointMeterUpdateQueued = false;
            UpdateEndpointMeterActivity();
        }, DispatcherPriority.Background);
    }

    private void UpdateEndpointMeterActivity()
    {
        if (!_endpointMetersInitialized) return;
        SyncEndpointPeakWatches();
        _endpointPeakDisplayActive = !_isShuttingDown && IsVisible && WindowState != WindowState.Minimized &&
            RoutingCanvas.IsVisible && _workspaceView == WorkspaceView.Vst;
        PollEndpointPeakHolds();
        if (_endpointPeakDisplayActive)
        {
            RefreshEndpointPeakIndicators();
        }
        bool active = _settings.NodeVuMetersEnabled && !_isShuttingDown && IsVisible && WindowState != WindowState.Minimized &&
            RoutingCanvas.IsVisible && _workspaceView == WorkspaceView.Vst && _endpointMeters.Count > 0;
        if (!active) { StopEndpointMeters(); return; }
        var addresses = _endpointMeters.Select(item => item.Address).ToArray();
        if (_endpointMeterReader?.Matches(addresses) != true)
        {
            StopEndpointMeters();
            _endpointMeterReader = new EndpointMeterReader(addresses, _endpointPeakSource, PollEndpointPeakHolds);
        }
        if (!_endpointMeterRendering)
        {
            CompositionTarget.Rendering += RenderEndpointMeters;
            _endpointMeterRendering = true;
        }
    }

    private void StopEndpointMeters()
    {
        StopEndpointMeterRendering();
        _endpointMeterReader?.Dispose();
        _endpointMeterReader = null;
        Array.Clear(_endpointMeterPeaks);
        foreach (var item in _endpointMeters) item.View.Clear();
    }

    private void DrawEndpointMeters(IoEndpoint endpoint, CallbackMode mode, bool outputSide,
        int pinCount, double x, double y, Border card, List<FrameworkElement> elements)
    {
        if (!UsesBlockMeter(endpoint))
        {
            // Linked from scattered channels: one meter row per pin, each reading its own channel.
            for (var offset = 0; offset < pinCount; offset++)
                DrawEndpointMeterRow(endpoint, mode, outputSide, endpoint.Range.ChannelAt(offset), offset, x, y, card, elements,
                    withPeakIndicator: offset == 0);
            return;
        }

        int stream = _vstCanvasMode == CallbackMode.Main || IsInputDirectOutputVstView()
            ? 2 : mode == CallbackMode.Output ? 1 : 0;
        var address = new EndpointMeterAddress(stream, outputSide, endpoint.Range.Start, endpoint.ChannelCount);
        var hue = SignalMonitorHue(mode, endpoint, outputSide);
        double labelEdge = VstCardPinInset + EndpointPinLabelOffset + EndpointPinLabelWidth + EndpointMeterLabelGap;
        var view = _endpointMeterViewsForRebuild.TryGetValue(address, out var previous) && previous.Matches(pinCount, hue)
            ? previous : new EndpointMeterView(pinCount, hue);
        view.Width = VstEndpointCardWidth - labelEdge - EndpointMeterOuterInset;
        Canvas.SetLeft(view, x + (outputSide ? labelEdge : EndpointMeterOuterInset));
        Canvas.SetTop(view, VstCardPinY(y, 0) - EndpointMeterView.RowHeight / 2);
        if (_settings.NodeVuMetersEnabled)
        {
            RoutingCanvas.Children.Add(view);
            elements.Add(view);
        }
        var key = (_kind, address);
        if (!_endpointPeakStates.TryGetValue(key, out var peak))
            _endpointPeakStates[key] = peak = new EndpointPeakState();
        var indicator = new EndpointPeakIndicator(peak, card.Background, card.BorderBrush,
            $"{endpoint.Name} / {(outputSide ? "returned output" : "incoming input")}", () =>
            {
                _peakHoldSession.Reset(address);
                RefreshEndpointPeakIndicators();
            });
        Canvas.SetLeft(indicator, x + (outputSide ? 5 : VstEndpointCardWidth - EndpointPeakIndicator.Diameter - 5));
        Canvas.SetTop(indicator, y + 7);
        RoutingCanvas.Children.Add(indicator);
        elements.Add(indicator);
        _endpointMeters.Add((address, view, peak, indicator));
    }

    // The native peak reader handles up to 8 neighbouring channels per block.
    private static bool UsesBlockMeter(IoEndpoint endpoint) => endpoint.Range.IsContiguous && endpoint.ChannelCount <= 8;

    private void DrawEndpointMeterRow(IoEndpoint endpoint, CallbackMode mode, bool outputSide, int channel, int row,
        double x, double y, Border card, List<FrameworkElement> elements, bool withPeakIndicator)
    {
        int stream = _vstCanvasMode == CallbackMode.Main || IsInputDirectOutputVstView()
            ? 2 : mode == CallbackMode.Output ? 1 : 0;
        var address = new EndpointMeterAddress(stream, outputSide, channel, 1);
        var hue = SignalMonitorHue(mode, endpoint, outputSide);
        double labelEdge = VstCardPinInset + EndpointPinLabelOffset + EndpointPinLabelWidth + EndpointMeterLabelGap;
        // Keep the live meter (and its displayed level) across canvas rebuilds, as block meters do.
        var view = _endpointMeterViewsForRebuild.TryGetValue(address, out var previous) && previous.Matches(1, hue)
            ? previous : new EndpointMeterView(1, hue);
        view.Width = VstEndpointCardWidth - labelEdge - EndpointMeterOuterInset;
        Canvas.SetLeft(view, x + (outputSide ? labelEdge : EndpointMeterOuterInset));
        Canvas.SetTop(view, VstCardPinY(y, row) - EndpointMeterView.RowHeight / 2);
        if (_settings.NodeVuMetersEnabled)
        {
            RoutingCanvas.Children.Add(view);
            elements.Add(view);
        }
        var key = (_kind, address);
        if (!_endpointPeakStates.TryGetValue(key, out var peak))
            _endpointPeakStates[key] = peak = new EndpointPeakState();
        var indicator = new EndpointPeakIndicator(peak, card.Background, card.BorderBrush,
            $"{endpoint.Name} / {(outputSide ? "returned output" : "incoming input")}", () =>
            {
                _peakHoldSession.Reset(address);
                RefreshEndpointPeakIndicators();
            });
        if (withPeakIndicator)
        {
            Canvas.SetLeft(indicator, x + (outputSide ? 5 : VstEndpointCardWidth - EndpointPeakIndicator.Diameter - 5));
            Canvas.SetTop(indicator, y + 7);
            RoutingCanvas.Children.Add(indicator);
            elements.Add(indicator);
        }
        _endpointMeters.Add((address, view, peak, indicator));
    }

    private TextBlock EndpointCardTitle(IoEndpoint endpoint, int pinCount)
    {
        var title = new TextBlock { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Inlines.Add(new Run(endpoint.Name));
        return title;
    }
}
