using System.Windows;
using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

/// <summary>
/// The FX Host's only audio path: the Elka FX Host virtual ASIO driver. The link starts
/// with the app, so as soon as Matrix opens "Elka FX Host" audio flows through the canvas.
/// </summary>
public partial class MainWindow
{
    private ComboBox? _asioChannelCountComboBox;
    private bool _updatingAsioLinkControls;
    private bool _lastAsioDriverStreaming;

    // Every canvas is the single in/out canvas (FX In -> VST -> FX Out).
    private static bool IsSingleAsioCanvas() => true;

    /// <summary>Keeps the workspace on the one FX In -> VST -> FX Out canvas.</summary>
    private bool CoerceSingleAsioCanvas()
    {
        var changed = _vstCanvasMode != CallbackMode.Input ||
                      _vstInputCanvasRouteView != VstInputCanvasRouteView.InputReturn ||
                      _selectedMode != CallbackMode.Input;
        _vstCanvasMode = CallbackMode.Input;
        _vstInputCanvasRouteView = VstInputCanvasRouteView.InputReturn;
        if (_selectedMode != CallbackMode.Input)
            SetSelectedSideMode(CallbackMode.Input);
        return changed;
    }

    // Runs once at startup; the link then stays up for the life of the app. The driver
    // itself is always listed in Matrix, whether or not FX Host is running.
    private void StartAsioLinkFromUi()
    {
        var status = _engine.StartAsioLink();
        AsioLinkStatusTextBlock.Text = status;
        AppendLog(status);
        RefreshAfterAsioLinkStateChange();
    }


    private void AsioLinkToggleButton_Click(object sender, RoutedEventArgs e) => ToggleCardContent(AsioLinkContentGrid);

    private void RefreshAfterAsioLinkStateChange()
    {
        UpdateAsioDriverDisplay();
        RefreshEngineCallbackMode();
        if (_workspaceView == WorkspaceView.Vst)
            RebuildRoutingCanvas();
        UpdateLiveStatusText();
    }

    private void UpdateAsioDriverDisplay()
    {
        var registered = _engine.IsAsioDriverRegistered;
        var streaming = _engine.IsAsioDriverStreaming;
        var lastBank = FxHostEdition.BankLetters(FxHostEdition.BankCount - 1);
        var text = !registered
            ? "Driver: Elka FX Host is not registered. Run scripts\\register-asio-driver.ps1 as administrator."
            : $"Driver: Elka FX Host | {FxHostEdition.ChannelCount} in / {FxHostEdition.ChannelCount} out " +
              $"(FX In A1-{lastBank}8, FX Out A1-{lastBank}8) | " +
              (streaming ? "Matrix is streaming" : "waiting for Matrix to open the device");
        if (AsioLinkDriverTextBlock.Text != text) AsioLinkDriverTextBlock.Text = text;
        _lastAsioDriverStreaming = streaming;
        EnsureAsioChannelCountSelector();
    }

    private void PollAsioLinkState()
    {
        if (_engine.IsAsioDriverStreaming != _lastAsioDriverStreaming)
        {
            UpdateAsioDriverDisplay();
            AppendLog(_lastAsioDriverStreaming ? "Matrix started the Elka FX Host ASIO device." : "Matrix stopped the Elka FX Host ASIO device.");
        }

        var status = _engine.AsioLinkStatus();
        if (AsioLinkStatusTextBlock.Text != status) AsioLinkStatusTextBlock.Text = status;
        RefreshAsioClockStatus();
    }

    /// <summary>
    /// Ctrl-click two or more nodes on ONE side (FX In or FX Out), then Enter: they become
    /// one linked node (stereo, or a wider group). Nothing else may be selected.
    /// </summary>
    private bool TryCompleteQuickChannelLink()
    {
        if (_quickGroupNodeSlots.Count > 0 || _quickGroupIds.Count > 0 || _quickGroupPluginChoices.Count > 0)
            return false;

        List<QuickGroupEndpoint> picked;
        bool outputSide;
        if (_quickConnectSourceEndpoints.Count >= 2 && _quickConnectDestinationEndpoints.Count == 0)
        {
            picked = _quickConnectSourceEndpoints;
            outputSide = false;
        }
        else if (_quickConnectDestinationEndpoints.Count >= 2 && _quickConnectSourceEndpoints.Count == 0)
        {
            picked = _quickConnectDestinationEndpoints;
            outputSide = true;
        }
        else
        {
            return false;
        }

        var linked = FxHostEdition.TryLink(outputSide, picked.Select(static item => item.Endpoint).ToList(), out var message);
        AppendLog(message);
        ClearQuickGroupChain(log: false);
        if (linked)
            SaveChannelLinks();
        RebuildRoutingCanvas();
        return true;
    }

    private void UnlinkChannels(IoEndpoint endpoint, bool outputSide)
    {
        if (!FxHostEdition.Unlink(outputSide, endpoint))
            return;
        AppendLog($"Unlinked {FxHostEdition.SideName(endpoint.Name, outputSide)} into mono nodes.");
        SaveChannelLinks();
        RebuildRoutingCanvas();
    }

    private void SaveChannelLinks()
    {
        _settings.FxInLinks = FxHostEdition.SaveLinks(outputSide: false);
        _settings.FxOutLinks = FxHostEdition.SaveLinks(outputSide: true);
        RefreshEngineCallbackMode();
        QueueSave();
    }

    private void EnsureAsioChannelCountSelector()
    {
        if (_asioChannelCountComboBox is not null) return;

        var combo = new ComboBox { Width = 210, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        var choices = new List<int> { FxHostEdition.MatrixChannels, FxHostEdition.MatrixCoconutChannels };
        for (var channels = FxHostEdition.MinChannels; channels <= FxHostEdition.MaxChannels; channels += FxHostEdition.ChannelsPerBank)
            if (!choices.Contains(channels)) choices.Add(channels);
        foreach (var channels in choices)
            combo.Items.Add(new ComboBoxItem { Content = FxHostEdition.PresetLabel(channels), Tag = channels });

        _updatingAsioLinkControls = true;
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (int)item.Tag == FxHostEdition.ChannelCount);
        _updatingAsioLinkControls = false;
        combo.SelectionChanged += AsioChannelCountComboBox_SelectionChanged;
        combo.ToolTip = "Channels the Elka FX Host ASIO driver shows in Matrix. Matrix = 32, Matrix Coconut = 64, custom up to 128 in banks of 8.";

        AsioLinkOptionsPanel.Children.Clear();
        AsioLinkOptionsPanel.Children.Add(new TextBlock
        {
            Text = "ASIO channels",
            Style = (Style)FindResource("MutedText"),
            Margin = new Thickness(0, 12, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        AsioLinkOptionsPanel.Children.Add(combo);
        _asioChannelCountComboBox = combo;
        AddAsioClockControls();
    }

    private void AsioChannelCountComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingAsioLinkControls || _asioChannelCountComboBox?.SelectedItem is not ComboBoxItem { Tag: int channels })
            return;
        if (channels == FxHostEdition.ChannelCount)
            return;

        var answer = MessageBox.Show(this,
            $"Change the Elka FX Host ASIO driver to {channels} channels?\n\n" +
            "FX Host restarts so the canvas shows the new banks, and Matrix resets the ASIO device. " +
            "Cables to channels that no longer exist are removed.",
            FxHostEdition.ProductName, MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
        {
            _updatingAsioLinkControls = true;
            _asioChannelCountComboBox.SelectedItem = _asioChannelCountComboBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(item => (int)item.Tag == FxHostEdition.ChannelCount);
            _updatingAsioLinkControls = false;
            return;
        }

        // Save first: if that fails, the driver must keep the count the app is running with.
        if (!SaveSettings())
        {
            AppendLog("Could not save the session; the channel count was not changed.");
            _updatingAsioLinkControls = true;
            _asioChannelCountComboBox.SelectedItem = _asioChannelCountComboBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(item => (int)item.Tag == FxHostEdition.ChannelCount);
            _updatingAsioLinkControls = false;
            return;
        }

        AppendLog(_engine.SetAsioChannelCount(channels));

        ApplicationRestart.StartAfterCurrentProcessExits();
        ShutdownFromTray();
    }
}
