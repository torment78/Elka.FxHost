using System.Windows;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{

    internal void ActivateFromSecondInstance()
    {
        BringFxHostWindowToFront();
    }

    private void BringFxHostWindowToFront()
    {
        if (!IsVisible || !ShowInTaskbar)
        {
            RestoreFromTray();
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = _windowStateBeforeTray == WindowState.Maximized
                ? WindowState.Maximized
                : WindowState.Normal;
        }

        Activate();
        Focus();
    }
}
