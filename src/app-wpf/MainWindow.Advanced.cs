using System.Windows;
using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    // Elka FX Host has a single feature set; the old Simple/Advanced switch is gone.
    private readonly bool _advancedModeActive = true;

    private void ApplyAdvancedModeVisibility()
    {
        // Elka FX Host has one canvas and no delay/volume channel workspace.
        SideIoCard.Visibility = Visibility.Collapsed;
        ChannelsWorkspaceButton.Visibility = Visibility.Collapsed;
        VstInputDirectButton.Visibility = Visibility.Collapsed;
        VstRouteViewPanel.Visibility = Visibility.Collapsed;
    }
}
