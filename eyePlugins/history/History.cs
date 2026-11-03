using System;
using System.Collections.Generic;
using ImgViewer.Plugins;

[assembly: PluginApi(2)]

namespace ImgViewer.Plugins.History;

// ---------------------------------------------------------------------
//  Edit history panel: every saved step of the current photo, select one to show it
// ---------------------------------------------------------------------

public sealed class HistoryPlugin : IEyePlugin
{
    private IEditHistory _history;
    private IListPanel _panel;

    public PluginManifest Manifest { get; } = new PluginManifest
    {
        Id = "photographereye.history",
        Name = "History",
        Version = "1.0",
        Author = "PhotographerEye",
        Description = "Lists every saved edit step of the photo; click a step or move with the arrow keys to show that version. Editing an earlier version drops the steps after it."
    };

    public void Initialize(IPluginHost host)
    {
        _history = host.History;
        _panel = host.AddListPanel(new ListPanelDescriptor
        {
            Id = "steps",
            Label = "History",
            Description = "Click a step or use the arrow keys to show that version. An edit made on an earlier version removes the steps after it.",
            Items = Items,
            Selected = () => _history.Position,
            Select = i => _history.GoTo(i),
            VisibleRows = 12
        });
        _history.Changed += (s, e) => _panel.Refresh();
    }

    private IReadOnlyList<ListPanelItem> Items()
    {
        var steps = _history.Steps;
        var items = new List<ListPanelItem>(steps.Count);
        foreach (var s in steps)
        {
            items.Add(new ListPanelItem
            {
                Text = s.Label,
                Detail = s.Index.ToString()
            });
        }
        return items;
    }
}
