using System.Windows;
using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

/// <summary>
/// ASIO buffer size for the Elka FX Host driver. Matrix runs a non-master ASIO device at
/// the size the driver prefers, so it is set here (e.g. 512 to match Matrix) and Matrix
/// resets the device to use it. The driver runs on its own clock (the PC timer); the
/// sample rate always follows Matrix.
/// The driver can also follow another device's clock (DeviceClockFollower), but that is
/// not offered in the UI yet; any earlier choice is reset to the internal clock.
/// </summary>
public partial class MainWindow
{
    private static readonly int[] AsioBufferSizes = [32, 64, 128, 256, 512, 1024, 2048];
    private ComboBox? _asioBufferComboBox;
    private TextBlock? _asioClockStatusText;
    private int _lastControlPanelRequests = -1;

    private void AddAsioClockControls()
    {
        if (_asioBufferComboBox is not null) return;

        // Own clock only: undo a "follow device" choice made in 0.1.5.
        if (_engine.AsioClockSource(out _) != 0)
            AppendLog(_engine.SetAsioClockSource(0, string.Empty));

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

        _asioClockStatusText = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)FindResource("MutedText")
        };

        AsioLinkOptionsPanel.Children.Add(OptionLabel("Buffer size"));
        AsioLinkOptionsPanel.Children.Add(buffer);
        AsioLinkOptionsPanel.Children.Add(_asioClockStatusText);
        _asioBufferComboBox = buffer;
        RefreshAsioClockStatus();
    }

    private TextBlock OptionLabel(string text) => new()
    {
        Text = text,
        Style = (Style)FindResource("MutedText"),
        Margin = new Thickness(0, 12, 10, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    private void AsioBufferComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingAsioLinkControls || _asioBufferComboBox?.SelectedItem is not ComboBoxItem { Tag: int samples }) return;
        AppendLog(_engine.SetAsioBufferSize(samples));
        RefreshAsioClockStatus();
    }

    /// <summary>Called with the regular status poll: buffer state, and Matrix's ASIO panel button.</summary>
    private void RefreshAsioClockStatus()
    {
        if (_asioClockStatusText is null) return;
        var status = _engine.AsioClockStatusSnapshot();
        string text;
        if (status is null || !status.DriverPresent)
        {
            text = "The buffer size applies when Matrix runs the Elka FX Host device.";
        }
        else
        {
            text = status.ActiveBufferSize > 0
                ? $"Driver: running at {status.ActiveBufferSize} samples on its own clock."
                : "Driver: not running.";
            if (status.PreferredBufferSize > 0 && status.ActiveBufferSize > 0 && status.PreferredBufferSize != status.ActiveBufferSize)
                text += $" Set to {status.PreferredBufferSize}; restart the device in Matrix if it does not reset.";
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
        AppendLog("ASIO settings opened from the ASIO control panel.");
    }
}
