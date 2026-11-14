using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Elka.VoiceMeeterFxHost.App;

/// <summary>
/// ASIO buffer size and clock settings for the Elka FX Host driver.
/// Buffer size: Matrix runs a non-master ASIO device at the size the driver prefers, so
/// it is set here (e.g. 512 to match Matrix) and Matrix resets the device to use it.
/// Clock: "Internal" paces blocks with the PC timer; following a device paces them with
/// that sound card's own clock (normally the interface Matrix uses as master), so the
/// two never drift apart. The sample rate always follows whatever Matrix runs at.
/// </summary>
public partial class MainWindow
{
    private static readonly int[] AsioBufferSizes = [32, 64, 128, 256, 512, 1024, 2048];
    private ComboBox? _asioBufferComboBox;
    private ComboBox? _asioClockComboBox;
    private TextBlock? _asioClockStatusText;
    private int _lastControlPanelRequests = -1;

    private sealed record ClockChoice(int Mode, string DeviceId, string Label)
    {
        public override string ToString() => Label;
    }

    private void AddAsioClockControls()
    {
        if (_asioBufferComboBox is not null) return;

        var buffer = new ComboBox { Width = 120, Margin = new Thickness(0, 8, 16, 0), HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var size in AsioBufferSizes)
            buffer.Items.Add(new ComboBoxItem { Content = $"{size} samples", Tag = size });
        _updatingAsioLinkControls = true;
        var current = _engine.AsioBufferSize;
        buffer.SelectedItem = buffer.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (int)item.Tag == current);
        _updatingAsioLinkControls = false;
        buffer.ToolTip = "Buffer size the Elka FX Host ASIO device runs at in Matrix. Set it to the same size as Matrix (for example 512). " +
                         "Matrix resets the device when you change it.";
        buffer.SelectionChanged += AsioBufferComboBox_SelectionChanged;

        var clock = new ComboBox { Width = 280, Margin = new Thickness(0, 8, 16, 0), HorizontalAlignment = HorizontalAlignment.Left };
        clock.ToolTip = "Internal: the driver times its blocks with the PC timer.\n" +
                        "A device: the driver follows that sound card's clock (pick the interface Matrix uses as master), " +
                        "so FX Host never drifts against it. The sample rate always follows Matrix.";
        clock.DropDownOpened += (_, _) => FillAsioClockChoices(clock);
        FillAsioClockChoices(clock);
        clock.SelectionChanged += AsioClockComboBox_SelectionChanged;

        _asioClockStatusText = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)FindResource("MutedText")
        };

        AsioLinkOptionsPanel.Children.Add(OptionLabel("Buffer size"));
        AsioLinkOptionsPanel.Children.Add(buffer);
        AsioLinkOptionsPanel.Children.Add(OptionLabel("Clock"));
        AsioLinkOptionsPanel.Children.Add(clock);
        AsioLinkOptionsPanel.Children.Add(_asioClockStatusText);
        _asioBufferComboBox = buffer;
        _asioClockComboBox = clock;
        RefreshAsioClockStatus();
    }

    private TextBlock OptionLabel(string text) => new()
    {
        Text = text,
        Style = (Style)FindResource("MutedText"),
        Margin = new Thickness(0, 12, 10, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    private void FillAsioClockChoices(ComboBox clock)
    {
        var mode = _engine.AsioClockSource(out var deviceId);
        var choices = new List<ClockChoice> { new(0, string.Empty, "Internal (PC timer)") };
        choices.AddRange(_engine.AudioOutputDevices().Select(device => new ClockChoice(1, device.Id, $"Follow: {device.Name}")));
        if (mode == 1 && choices.All(choice => choice.DeviceId != deviceId))
            choices.Add(new ClockChoice(1, deviceId, "Follow: (device not connected)"));

        _updatingAsioLinkControls = true;
        clock.Items.Clear();
        foreach (var choice in choices) clock.Items.Add(choice);
        clock.SelectedItem = choices.FirstOrDefault(choice => choice.Mode == mode && (mode == 0 || choice.DeviceId == deviceId)) ?? choices[0];
        _updatingAsioLinkControls = false;
    }

    private void AsioBufferComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingAsioLinkControls || _asioBufferComboBox?.SelectedItem is not ComboBoxItem { Tag: int samples }) return;
        AppendLog(_engine.SetAsioBufferSize(samples));
        RefreshAsioClockStatus();
    }

    private void AsioClockComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingAsioLinkControls || _asioClockComboBox?.SelectedItem is not ClockChoice choice) return;
        AppendLog(_engine.SetAsioClockSource(choice.Mode, choice.DeviceId));
        RefreshAsioClockStatus();
    }

    /// <summary>Called with the regular status poll: clock state, and Matrix's ASIO panel button.</summary>
    private void RefreshAsioClockStatus()
    {
        if (_asioClockStatusText is null) return;
        var status = _engine.AsioClockStatusSnapshot();
        string text;
        if (status is null || !status.DriverPresent)
        {
            text = "Clock and buffer apply when Matrix runs the Elka FX Host device.";
        }
        else
        {
            var buffer = status.ActiveBufferSize > 0 ? $"running at {status.ActiveBufferSize} samples" : "not running";
            if (status.PreferredBufferSize > 0 && status.ActiveBufferSize > 0 && status.PreferredBufferSize != status.ActiveBufferSize)
                buffer += $" (set to {status.PreferredBufferSize}; restart the device in Matrix if it does not reset)";
            var ppm = (status.PartsPerBillion / 1000.0).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
            var clock = status.Mode != 1
                ? "internal clock"
                : status.FollowState switch
                {
                    1 => "measuring the device clock...",
                    2 => $"locked to the device clock ({ppm} ppm vs PC timer)",
                    3 => "device not found - using the PC timer",
                    4 => "device busy or unavailable in Windows - using the PC timer",
                    _ => "device clock starting..."
                };
            text = $"Driver: {buffer}, {clock}.";
            if (_lastControlPanelRequests >= 0 && status.ControlPanelRequests != _lastControlPanelRequests)
                ShowAsioSettingsFromControlPanel();
            _lastControlPanelRequests = status.ControlPanelRequests;
        }

        if (_asioClockStatusText.Text != text) _asioClockStatusText.Text = text;
    }

    /// <summary>Matrix's ASIO control panel button: bring FX Host up on its ASIO settings.</summary>
    private void ShowAsioSettingsFromControlPanel()
    {
        BringFxHostWindowToFront();
        AsioLinkContentGrid.Visibility = Visibility.Visible;
        AsioLinkContentGrid.BringIntoView();
        if (_asioClockComboBox is not null) FillAsioClockChoices(_asioClockComboBox);
        AppendLog("ASIO settings opened from the ASIO control panel.");
    }
}
