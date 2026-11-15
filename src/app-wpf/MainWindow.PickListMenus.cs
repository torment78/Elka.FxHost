using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    /// <summary>
    /// Adds a submenu (shows the ▸ arrow) whose first entry is "All", followed by one entry per item.
    /// Clicking ticks/unticks an entry and keeps the menu open; when the menu closes (click back on
    /// the canvas), every ticked item is passed to <paramref name="apply"/> at once.
    /// </summary>
    private static void AddPickListSubmenu<T>(ContextMenu menu, string header,
        IReadOnlyList<(string Label, T Item)> entries, Action<IReadOnlyList<T>> apply)
    {
        if (entries.Count == 0) return;
        var submenu = new MenuItem { Header = header };
        var all = new MenuItem { Header = "All", IsCheckable = true, StaysOpenOnClick = true };
        submenu.Items.Add(all);
        submenu.Items.Add(new Separator());
        var items = new List<(MenuItem Item, T Value)>();
        foreach (var (label, value) in entries)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, StaysOpenOnClick = true };
            submenu.Items.Add(item);
            items.Add((item, value));
        }

        var syncing = false;
        all.Click += (_, _) =>
        {
            syncing = true;
            foreach (var (item, _) in items) item.IsChecked = all.IsChecked;
            syncing = false;
        };
        foreach (var (item, _) in items)
            item.Click += (_, _) =>
            {
                if (!syncing) all.IsChecked = items.All(entry => entry.Item.IsChecked);
            };

        menu.Items.Add(submenu);

        // Ticked entries apply when the menu is closed by clicking back on the canvas.
        // Escape, or choosing another menu command, cancels them.
        var cancelled = false;
        menu.Opened += (_, _) => cancelled = false;
        menu.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) cancelled = true;
        };
        menu.AddHandler(MenuItem.ClickEvent, new System.Windows.RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is MenuItem clicked && !clicked.StaysOpenOnClick) cancelled = true;
        }));
        menu.Closed += (_, _) =>
        {
            var picked = items.Where(entry => entry.Item.IsChecked).Select(entry => entry.Value).ToList();
            foreach (var (item, _) in items) item.IsChecked = false;
            all.IsChecked = false;
            if (!cancelled && picked.Count > 0) apply(picked);
        };
    }

    /// <summary>Cables that start (FX In) or end (FX Out) on any channel of this node.</summary>
    private List<CanvasConnectionSnapshot> EndpointCableConnections(CallbackMode mode, IoEndpoint endpoint, bool outputSide) =>
        _settings.CanvasConnections.Where(connection => outputSide
                ? connection.ToKind == PinEndpointDestination && connection.ToMode == mode && endpoint.Range.Contains(connection.ToChannel)
                : connection.FromKind == PinEndpointSource && connection.FromMode == mode && endpoint.Range.Contains(connection.FromChannel))
            .ToList();

    private void DisconnectCanvasConnections(IReadOnlyList<CanvasConnectionSnapshot> connections)
    {
        // Disconnect all first, then refresh the canvas once (not once per cable).
        var removed = connections.Where(DisconnectCanvasConnectionCore).ToList();
        if (removed.Count == 0) return;
        AppendLog(removed.Count == 1
            ? $"Disconnected {CanvasConnectionLabel(removed[0])}."
            : $"Disconnected {removed.Count} cables.");
        RefreshEngineCallbackMode();
        RebuildVstNodeList();
        RebuildRoutingCanvas();
        QueueSave();
    }

    /// <summary>
    /// Takes the picked channels out of whatever linked node holds them now (the node may
    /// already have changed from an earlier click in the same open menu); the rest stay linked.
    /// </summary>
    private void UnlinkPickedChannels(bool outputSide, IReadOnlyList<int> channels)
    {
        var changed = false;
        foreach (var channel in channels)
        {
            var node = FxHostEdition.SideEndpoints(outputSide)
                .FirstOrDefault(candidate => candidate.ChannelCount > 1 && candidate.Range.Contains(channel));
            if (node is null || !FxHostEdition.RemoveFromLink(outputSide, node,
                    channels.Where(node.Range.Contains).ToList(), out var message))
                continue;
            AppendLog(message);
            changed = true;
        }

        if (!changed) return;
        SaveChannelLinks();
        RebuildRoutingCanvas();
    }
}
