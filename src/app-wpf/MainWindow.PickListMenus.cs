using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    /// <summary>
    /// Adds a submenu (shows the ▸ arrow): "All" first, then one entry per item. A click acts at
    /// once (All = every item still there) and greys out what it did; the menu stays open for
    /// more clicks until you click outside or press Escape.
    /// </summary>
    private static void AddPickListSubmenu<T>(ContextMenu menu, string header,
        IReadOnlyList<(string Label, T Item)> entries, Action<IReadOnlyList<T>> apply)
    {
        if (entries.Count == 0) return;
        var submenu = new MenuItem { Header = header };
        var all = new MenuItem { Header = "All", StaysOpenOnClick = true };
        submenu.Items.Add(all);
        submenu.Items.Add(new Separator());
        var items = new List<(MenuItem Item, T Value)>();
        foreach (var (label, value) in entries)
        {
            var item = new MenuItem { Header = label, StaysOpenOnClick = true };
            submenu.Items.Add(item);
            items.Add((item, value));
            item.Click += (_, _) =>
            {
                item.IsEnabled = false;
                if (items.All(entry => !entry.Item.IsEnabled)) all.IsEnabled = false;
                apply([value]);
            };
        }

        all.Click += (_, _) =>
        {
            var remaining = items.Where(entry => entry.Item.IsEnabled).Select(entry => entry.Value).ToList();
            all.IsEnabled = false;
            foreach (var (item, _) in items) item.IsEnabled = false;
            if (remaining.Count > 0) apply(remaining);
        };

        menu.Items.Add(submenu);
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
