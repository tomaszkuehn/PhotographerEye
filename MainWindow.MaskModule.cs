using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ImgViewer.Plugins;
using ImgViewer.Services;

namespace ImgViewer;

/// <summary>
/// The MASK module: the single source of the photo's masks. Masks are created here as independent objects
/// ("mask:N" keys in <see cref="PluginEditState.Masks"/>) — creating one never changes another mask or a tool.
/// A tool (or the STANDARD / DETAIL sliders) is limited to a mask by an explicit choice in its Mask row; the tool
/// then follows the mask (a link), so editing the mask's shape changes it everywhere it is used.
/// </summary>
public partial class MainWindow
{
    /// <summary>The masks a new one can be drawn with.</summary>
    private static (string Kind, string Name, string Tip)[] NewMaskKinds() => new[]
    {
        (MaskRender.Radial, "Radial", "A soft circle in the middle of the view — drag its handles to size it, the outer outline to set the feather"),
        (MaskRender.Linear, "Linear gradient", "A gradient from one line to another"),
        (MaskRender.Brush, "Brush", "Paint the area by hand; Alt erases"),
        (MaskRender.Luma, "Luminance range", "The dark (or bright) part of the photo")
    };

    /// <summary>Body of the MASK section: the New mask button, every mask of the photo, the STANDARD / DETAIL binding.</summary>
    private FrameworkElement BuildMaskModuleBody(Brush muted)
    {
        var sp = new StackPanel();
        sp.Children.Add(BuildNewMaskButton(muted));
        if (LibraryMaskOrder().Any())
        {
            sp.Children.Add(new TextBlock { Text = "Masks of this photo", Foreground = muted, Margin = new Thickness(0, 10, 0, 0) });
            AutomationProperties.SetAutomationId(sp, "MaskList");
            foreach (var (key, spec) in LibraryMaskOrder())
                sp.Children.Add(BuildMaskListRow(key, spec, muted));
        }
        else
        {
            var none = new TextBlock
            {
                Text = "No masks yet. A mask limits a tool (or the STANDARD / DETAIL sliders) to part of the photo; it stays independent of the other masks.",
                Foreground = muted, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 8, 0, 0)
            };
            AutomationProperties.SetAutomationId(none, "MaskListEmpty");
            sp.Children.Add(none);
        }
        sp.Children.Add(new TextBlock { Text = "STANDARD / DETAIL sliders", Foreground = muted, Margin = new Thickness(0, 10, 0, 0) });
        sp.Children.Add(BuildMaskControls(AdjustMask.Key, AdjustMask.Label, muted));
        return sp;
    }

    /// <summary>Every mask of the photo, MASK-module masks first, then the panel's and the tools' own masks.</summary>
    private IOrderedEnumerable<KeyValuePair<string, MaskSpec>> LibraryMaskOrder() =>
        _pluginState.Masks
            .Where(kv => kv.Value is { } v && !string.IsNullOrEmpty(v.Kind))
            .OrderBy(kv => PluginEditState.IsLibraryKey(kv.Key) ? 0 : kv.Key == AdjustMask.Key ? 1 : 2)
            .ThenBy(kv => PluginEditState.LibraryIndex(kv.Key))
            .ThenBy(kv => kv.Key, StringComparer.Ordinal);

    /// <summary>+ New mask, greyed out while the mask bar is up (one mask banner at a time; Esc or Done frees creating).</summary>
    private Button? _newMaskBtn;

    private UIElement BuildNewMaskButton(Brush muted)
    {
        var b = new Button
        {
            Content = "+  New mask",
            Padding = new Thickness(8, 1, 8, 1),
            ToolTip = "Adds an independent mask: nothing else on the photo changes. Choose the shape, edit it on the photo, then limit tools to it in their Mask row"
        };
        b.SetResourceReference(StyleProperty, "ToolButton");
        AutomationProperties.SetAutomationId(b, "BtnMaskNew");
        b.IsEnabled = _maskEditFilter is null;
        _newMaskBtn = b;
        b.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
            foreach (var (kind, name, tip) in NewMaskKinds())
            {
                var k = kind;
                var item = new MenuItem { Header = name, ToolTip = tip };
                AutomationProperties.SetAutomationId(item, "BtnMaskNew_" + k);
                item.Click += (_, _) => CreateLibraryMask(k);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        return b;
    }

    /// <summary>Creates an independent mask of <paramref name="kind"/> in the MASK module and opens it for editing on the photo.</summary>
    private void CreateLibraryMask(string kind)
    {
        if (_baseInfo is not ImageInfo)
        {
            SetStatus("Open a photo first", error: true);
            return;
        }
        if (_maskEditFilter is not null)
        {
            // a mask is in use (the bar at the bottom of the screen): creating another one must wait until it is done
            SetStatus($"Finish {MaskOwnerLabel(_maskEditFilter)} first (Esc or the bar's Done) — then create a new mask", error: true);
            return;
        }
        var key = _pluginState.NewLibraryMaskKey();
        _pluginState.Masks[key] = NewMaskSpec(kind); // a pure add: no other mask, no tool is touched
        SetSectionExpanded(AdjMaskSectionKey, true);
        RebuildPluginUi();
        BeginMaskEdit(key);
        SetStatus($"Mask {PluginEditState.LibraryIndex(key)} created — independent of the others; tools can be limited to it in their Mask row (Ctrl+Z removes it)", error: false);
    }

    /// <summary>Gives <paramref name="key"/> a mask that is not a library mask: a plugin-computed one. It shows in the MASK module with its owner.</summary>
    private void SetToolMask(string key, MaskSpec spec)
    {
        if (_maskEditFilter is not null) EndMaskEdit();
        _pluginState.DetachLinksTo(key);
        _pluginState.Masks[key] = spec;
        RebuildPluginUi();
        BeginMaskEdit(key);
        OnPluginUiChanged();
    }

    /// <summary>The tool of <paramref name="key"/> works on the whole photo again: its mask is removed (followers keep their own copy).</summary>
    private void RemoveToolMask(string key)
    {
        _scopeChosen.Add(key); // an explicit whole photo: enabling the tool again must not bring a mask back
        bool hadValues = MaskValuesSummary(key) is not null;
        var links = _pluginState.LinksTo(key);
        if (AdjEditMask && _adjMaskKey == key) ShowAdjSet(false);
        _pluginState.DetachLinksTo(key);
        _pluginState.Masks.Remove(key);
        if (_maskEditFilter == key) EndMaskEdit();
        RebuildPluginUi();
        OnPluginUiChanged();
        RefreshMaskPins();
        SetStatus(links.Count == 0
            ? $"Mask removed — {MaskOwnerLabel(key)} works on the whole photo again"
            : $"Mask removed — {MaskOwnerLabel(key)} works on the whole photo again; {string.Join(", ", links.Select(MaskOwnerLabel))} keep the current shape as their own copy", error: false);
        if (hadValues) SetStatus("The mask's own slider values are gone with it — Ctrl+Z brings them back", error: false);
    }

    /// <summary>Removes the mask of <paramref name="key"/> from the MASK module; tools that follow it keep the shape as their own copy.</summary>
    private void DeleteMask(string key)
    {
        if (!_pluginState.Masks.ContainsKey(key)) return;
        bool library = PluginEditState.IsLibraryKey(key);
        string label = MaskOwnerLabel(key);
        var links = _pluginState.LinksTo(key);
        bool hadValues = MaskValuesSummary(key) is not null;
        if (AdjEditMask && _adjMaskKey == key) ShowAdjSet(false);
        _pluginState.DetachLinksTo(key);
        _pluginState.Masks.Remove(key);
        if (_maskEditFilter == key) EndMaskEdit();
        RebuildPluginUi();
        OnPluginUiChanged();
        RefreshMaskPins();
        string msg = library
            ? links.Count == 0 ? $"{label} removed" : $"{label} removed — {string.Join(", ", links.Select(MaskOwnerLabel))} keep the current shape as their own copy"
            : links.Count == 0
                ? $"Mask of {label} removed — it works on the whole photo now"
                : $"Mask of {label} removed — it works on the whole photo now; {string.Join(", ", links.Select(MaskOwnerLabel))} keep the current shape as their own copy";
        if (!library) _scopeChosen.Add(key); // its tool falls back to the whole photo — that stands when it is enabled again
        if (hadValues) msg += "; its own slider values are gone with it (Ctrl+Z brings them back)";
        SetStatus(msg, error: false);
    }

    /// <summary>One row of the mask list: name, who uses it, edit and remove.</summary>
    private FrameworkElement BuildMaskListRow(string key, MaskSpec spec, Brush muted)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = true };
        AutomationProperties.SetAutomationId(row, "MaskListRow_" + key);

        var del = SmallButton("", $"Remove this mask" + (_pluginState.LinksTo(key).Count > 0 ? " — the tools that use it keep the current shape as their own copy" : ""));
        AutomationProperties.SetAutomationId(del, "MaskListRemove_" + key);
        DockPanel.SetDock(del, Dock.Right);
        del.Click += (_, _) => DeleteMask(key);
        row.Children.Add(del);

        var edit = new Button
        {
            Content = _maskEditFilter == key ? "Done" : "Edit",
            Padding = new Thickness(8, 1, 8, 1),
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = _maskEditFilter == key ? "Finish editing the mask (Esc or Enter)" : "Edit this mask on the photo: drag its handles",
            VerticalAlignment = VerticalAlignment.Center
        };
        edit.SetResourceReference(StyleProperty, "ToolButton");
        AutomationProperties.SetAutomationId(edit, "MaskListEdit_" + key);
        DockPanel.SetDock(edit, Dock.Right);
        edit.Click += (_, _) =>
        {
            if (_maskEditFilter == key) FinishMaskEdit();
            else EditMaskFromPin(key);
        };
        row.Children.Add(edit);

        var text = new StackPanel();
        var name = new TextBlock { Text = MaskPinLabel(key, spec) ?? key, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(name, "MaskListName_" + key);
        var users = _pluginState.LinksTo(key);
        string info = users.Count == 0
            ? PluginEditState.IsLibraryKey(key) ? "Not used by any tool yet" : "The own mask of this tool"
            : "Used by: " + string.Join(", ", users.Select(MaskOwnerLabel));
        if (MaskValuesSummary(key) is { } vals) info += " · Values: " + vals;
        var infoTb = new TextBlock { Text = info, Foreground = muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
        AutomationProperties.SetAutomationId(infoTb, "MaskListInfo_" + key);
        text.Children.Add(name);
        text.Children.Add(infoTb);
        if (spec.Kind == MaskRender.Brush) text.Children.Add(BuildBrushControls(key, muted));
        row.Children.Add(text);
        return row;
    }

    /// <summary>Text of the scope button of <paramref name="key"/>: what limits the tool right now.</summary>
    private string MaskScopeLabel(string key, MaskSpec? mask)
    {
        if (mask is null || string.IsNullOrEmpty(mask.Kind)) return "Whole photo";
        if (mask.Kind == MaskRender.Link)
        {
            if (mask.Ref is { } r && _pluginState.Masks.TryGetValue(r, out var src) && !string.IsNullOrEmpty(src.Kind) && src.Kind != MaskRender.Link)
                return PluginEditState.IsLibraryKey(r) ? $"Inside {MaskOwnerLabel(r)}" : $"Same as {MaskOwnerLabel(r)}";
            return "Missing mask";
        }
        if (mask.Kind.StartsWith(MaskRender.PluginPrefix, StringComparison.Ordinal))
            return _pluginReg.FindMask(mask.Kind.Substring(MaskRender.PluginPrefix.Length)) is { } pm ? pm.D.Label : "Plugin mask";
        return "Own " + MaskKindName(mask.Kind);
    }

    private FrameworkElement BuildMaskScopeButton(string key, string label, MaskSpec? mask)
    {
        string cur = MaskScopeLabel(key, mask);
        var b = new Button
        {
            Content = cur,
            Padding = new Thickness(8, 1, 8, 1),
            ToolTip = $"Where {label} works: the whole photo, or one of the photo's masks (created in the MASK section; shared: edit once, all change)"
        };
        b.SetResourceReference(StyleProperty, "ToolButton");
        AutomationProperties.SetAutomationId(b, "PluginMaskScope_" + key);
        AutomationProperties.SetName(b, "Mask scope: " + cur);
        b.Click += (_, _) => OpenMaskScopeMenu(b, key, label, mask);
        return b;
    }

    /// <summary>Menu of the scope button: whole photo, or one of the photo's masks — the same (shared) or a copy. Masks are
    /// created only in the MASK section, never here.</summary>
    private void OpenMaskScopeMenu(Button target, string key, string label, MaskSpec? mask)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        bool whole = mask is null || string.IsNullOrEmpty(mask.Kind);
        var wholeItem = new MenuItem { Header = "Whole photo", IsCheckable = true, IsChecked = whole, ToolTip = "No mask: the tool works everywhere" };
        AutomationProperties.SetAutomationId(wholeItem, "PluginMaskScopeWhole_" + key);
        wholeItem.Click += (_, _) => { if (!whole) RemoveToolMask(key); };
        menu.Items.Add(wholeItem);

        foreach (var m in _pluginReg.Masks)
        {
            var item = new MenuItem { Header = $"{m.D.Label} ({m.PluginName})", ToolTip = "A mask computed by the plugin — set it up below" };
            item.Click += (_, _) => SetToolMask(key, NewMaskSpec(MaskRender.PluginPrefix + m.FullId));
            menu.Items.Add(item);
        }

        var choices = LinkableMasks(key);
        if (choices.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Use the same mask — edit once, all change", IsEnabled = false, FontWeight = FontWeights.SemiBold });
            foreach (var (src, text) in choices)
            {
                var item = new MenuItem { Header = text, IsCheckable = true, IsChecked = mask?.Kind == MaskRender.Link && mask?.Ref == src, Padding = new Thickness(14, 0, 0, 0) };
                AutomationProperties.SetAutomationId(item, "PluginMaskLinkTo_" + src);
                item.Click += (_, _) => LinkMask(key, src);
                HighlightOnHover(item, src);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Copy an existing mask — an own mask, changed separately", IsEnabled = false, FontWeight = FontWeights.SemiBold });
            foreach (var (src, text) in choices)
            {
                var item = new MenuItem { Header = text, Padding = new Thickness(14, 0, 0, 0) };
                AutomationProperties.SetAutomationId(item, "PluginMaskCopyFrom_" + src);
                item.Click += (_, _) => CopyMaskFrom(key, src);
                HighlightOnHover(item, src);
                menu.Items.Add(item);
            }
        }
        menu.Closed += (_, _) => HighlightMask(null);
        menu.IsOpen = true;
    }

    /// <summary>True when the mask being edited can limit <paramref name="key"/>: it has no mask of its own and can use one.</summary>
    private bool CanApplyInsideEdited(string key)
    {
        if (_maskEditFilter is not { } src || src == key) return false;
        if (!_pluginState.Masks.TryGetValue(src, out var spec) || string.IsNullOrEmpty(spec.Kind) || spec.Kind == MaskRender.Link) return false;
        if (_pluginState.Masks.ContainsKey(key)) return false;
        if (key == AdjustMask.Key) return true;
        var f = _pluginReg.FindFilter(key);
        return f is not null && f.D.SupportsMask && f.D.Stage != FilterStage.Output && !IsFaulted(f);
    }

    /// <summary>Visible, explicit replacement of the old silent auto-scope: a button in the tool's Mask row while another mask is edited.</summary>
    private FrameworkElement BuildApplyInsideEditedButton(string key)
    {
        string src = _maskEditFilter!;
        var b = new Button
        {
            Content = $"Work inside {MaskOwnerLabel(src)}",
            Padding = new Thickness(8, 1, 8, 1),
            Margin = new Thickness(0, 5, 0, 0),
            ToolTip = $"Limits this tool to the mask being edited ({MaskOwnerLabel(src)}): it follows that mask, so editing the shape changes both"
        };
        b.SetResourceReference(StyleProperty, "ToolButton");
        AutomationProperties.SetAutomationId(b, "PluginMaskInsideEdited_" + key);
        b.Click += (_, _) =>
        {
            if (!CanApplyInsideEdited(key)) return;
            // the tool usually has no mask yet (then a plain link is set); with an existing link the move carries the
            // mask-follow semantics: the mask left behind keeps a complete own set, the picked one dissolves or resets
            var link = _pluginReg.FindFilter(key);
            if (link is { } lf && lf.D.SupportsMask) _pluginState.MoveToolMaskTo(lf, src);
            if (!_pluginState.Masks.TryGetValue(key, out var cur) || cur.Kind != MaskRender.Link || cur.Ref != src)
                _pluginState.Masks[key] = new MaskSpec { Kind = MaskRender.Link, Ref = src };
            RefreshMaskBox(key);
            RefreshMaskPins();
            OnPluginUiChanged();
            SetStatus($"{MaskOwnerLabel(key)} applies inside the {MaskOwnerLabel(src)} mask now — its Mask row changes or removes that", error: false);
        };
        return b;
    }

    /// <summary>Replaces the mask row of every maskable tool (and the panel's) without a full rebuild: the Work-inside buttons follow the edit state.</summary>
    private void RefreshAllMaskRows()
    {
        if (PluginPanelHost is null || AdjMaskHost is null) return;
        foreach (var f in _pluginReg.Filters)
            if (f.D.SupportsMask && f.D.Stage != FilterStage.Output)
                RefreshMaskBox(f.FullId);
        RefreshMaskBox(AdjustMask.Key);
    }
}
