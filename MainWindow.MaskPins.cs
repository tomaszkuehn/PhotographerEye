using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ImgViewer.Controls;
using ImgViewer.Imaging;
using ImgViewer.Services;

namespace ImgViewer;

/// <summary>
/// Markers of the photo's masks (radial centre, middle of a linear gradient, a point of the brush paint) drawn on the
/// photo while a mask is being edited, or always with the markers toggle at the top of the Adjustments panel.
/// Clicking a marker opens that mask for editing: its section is expanded and scrolled into view.
/// </summary>
public partial class MainWindow
{
    private bool? _showMaskMarkers;
    /// <summary>The linked tool a shared mask edit was opened from ("Edit that mask"), or null.</summary>
    private string? _maskEditVia;
    private ToggleButton? _maskMarkersToggle;

    private bool ShowMaskMarkersAlways
    {
        get => _showMaskMarkers ??= SettingsStore.Load().ShowMaskMarkers == true;
        set
        {
            _showMaskMarkers = value;
            try { SettingsStore.Save(SettingsStore.Load() with { ShowMaskMarkers = value }); }
            catch (Exception ex) { ImgViewer.Debug.Log("mask markers setting: " + ex.Message); }
        }
    }

    private static UIElement MaskMarkersIcon() =>
        IconCanvas(
            Fg(new Ellipse { Width = 7, Height = 7, StrokeThickness = 1.4 }.At(1, 1.5), false),
            Fg(new Ellipse { Width = 2.4, Height = 2.4 }.At(3.3, 3.8), true),
            Fg(new Ellipse { Width = 7, Height = 7, StrokeThickness = 1.4 }.At(8, 7.5), false),
            Fg(new Ellipse { Width = 2.4, Height = 2.4 }.At(10.3, 9.8), true));

    private ToggleButton BuildMaskMarkersToggle()
    {
        var b = IconToggle(MaskMarkersIcon(),
            "Mask markers — show where the masks of all tools and plugins are on the photo; click a marker to edit that mask (while a mask is being edited the other markers are always shown)",
            "BtnMaskMarkers", ShowMaskMarkersAlways);
        b.Click += (_, _) =>
        {
            ShowMaskMarkersAlways = b.IsChecked == true;
            RefreshMaskPins();
            if (ShowMaskMarkersAlways)
                SetStatus(MaskOverlay.PinCount > 0
                    ? "Mask markers: click one on the photo to edit that mask"
                    : "Mask markers: this photo has no linear, radial or brush masks yet", error: false);
        };
        _maskMarkersToggle = b;
        return b;
    }

    /// <summary>Label of a mask marker: kind and owner (with the area of a repeat group).</summary>
    private string? MaskPinLabel(string key, MaskSpec spec)
    {
        if (MaskTarget(key) is not { } t) return null;
        string kind = spec.Kind switch
        {
            MaskRender.Radial => "Radial",
            MaskRender.Linear => "Linear gradient",
            MaskRender.Brush => "Brush",
            MaskRender.Luma => "Luminance range",
            MaskRender.Link => "Linked mask",
            _ when spec.Kind.StartsWith(MaskRender.PluginPrefix, StringComparison.Ordinal) =>
                _pluginReg.FindMask(spec.Kind.Substring(MaskRender.PluginPrefix.Length)) is { } pm ? pm.D.Label : "Plugin mask",
            _ => spec.Kind
        };
        string owner = t.Label;
        if (key != AdjustMask.Key && _pluginReg.FindFilter(key) is { } f)
        {
            if (f.D.RepeatGroup.Length > 0)
            {
                var inst = RepeatInstances(f);
                int i = inst.IndexOf(f);
                if (i >= 0) owner += $" — {f.D.RepeatGroup} {i + 1}";
            }
            if (!string.Equals(f.PluginName, t.Label, StringComparison.CurrentCultureIgnoreCase) && !f.IsCore)
                owner = f.PluginName + ": " + owner;
        }
        return $"{kind} — {owner}" + (spec.Invert ? " (inverted)" : "");
    }

    /// <summary>Owner of a mask for short texts: "Adjustments", "Vignette", "B&amp;W profiles: B&amp;W — Area 2".</summary>
    private string MaskOwnerLabel(string key)
    {
        if (MaskTarget(key) is not { } t) return key;
        var spec = _pluginState.Masks.TryGetValue(key, out var s) ? s : new MaskSpec { Kind = "x" };
        var full = MaskPinLabel(key, spec with { Invert = false }) ?? t.Label;
        int i = full.IndexOf(" — ", StringComparison.Ordinal);
        return i >= 0 ? full[(i + 3)..] : t.Label;
    }

    /// <summary>Masks of other tools and filters that <paramref name="key"/> can use (not links themselves).</summary>
    private List<(string Key, string Label)> LinkableMasks(string key) =>
        _pluginState.Masks
            .Where(kv => kv.Key != key && kv.Value is { } v && !string.IsNullOrEmpty(v.Kind) && v.Kind != MaskRender.Link && MaskTarget(kv.Key) is not null)
            .OrderBy(kv => kv.Key == AdjustMask.Key ? 0 : 1).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Key, MaskPinLabel(kv.Key, kv.Value) ?? kv.Key))
            .ToList();

    /// <summary>
    /// Link icon at the end of the mask scope row: use the mask of another tool or filter (e.g. the radial made in
    /// Adjustments) instead of drawing a new one; both then follow the same shape. Null when there is nothing to link to.
    /// </summary>
    private ToggleButton? BuildMaskLinkToggle(string key, string label, string current)
    {
        bool linked = current == MaskRender.Link;
        if (!linked && LinkableMasks(key).Count == 0) return null;
        var b = IconToggle(MaskIcon(MaskRender.Link),
            $"Use a mask of another tool for {label}: share it (edit once, both change) or copy it (an own mask you can change separately)",
            "PluginMask_" + key + "_" + MaskRender.Link, linked);
        b.Click += (_, _) =>
        {
            b.IsChecked = linked;
            var choices = LinkableMasks(key);
            var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
            if (choices.Count == 0)
                menu.Items.Add(new MenuItem { Header = "No other masks on this photo yet", IsEnabled = false });
            var cur = _pluginState.Masks.TryGetValue(key, out var cm) ? cm : null;
            if (choices.Count > 0)
            {
                menu.Items.Add(new MenuItem { Header = "Use the same mask — edit once, both change", IsEnabled = false, FontWeight = FontWeights.SemiBold });
                foreach (var (src, text) in choices)
                {
                    var item = new MenuItem { Header = text, IsCheckable = true, IsChecked = linked && cur?.Ref == src, Padding = new Thickness(14, 0, 0, 0) };
                    AutomationProperties.SetAutomationId(item, "PluginMaskLinkTo_" + src);
                    item.Click += (_, _) => LinkMask(key, src);
                    menu.Items.Add(item);
                }
                menu.Items.Add(new Separator());
                menu.Items.Add(new MenuItem { Header = "Copy the mask — an own mask, changed separately", IsEnabled = false, FontWeight = FontWeights.SemiBold });
                foreach (var (src, text) in choices)
                {
                    var item = new MenuItem { Header = text, Padding = new Thickness(14, 0, 0, 0) };
                    AutomationProperties.SetAutomationId(item, "PluginMaskCopyFrom_" + src);
                    item.Click += (_, _) => CopyMaskFrom(key, src);
                    menu.Items.Add(item);
                }
            }
            menu.IsOpen = true;
        };
        return b;
    }

    /// <summary>Makes the mask of <paramref name="key"/> use the mask of <paramref name="src"/>.</summary>
    private void LinkMask(string key, string src)
    {
        if (key == src || !_pluginState.Masks.TryGetValue(src, out var s) || s.Kind == MaskRender.Link) return;
        bool inv = _pluginState.Masks.TryGetValue(key, out var old) && old.Kind == MaskRender.Link && old.Invert;
        _pluginState.DetachLinksTo(key); // no chains: masks that used this one keep their own copy
        if (_maskEditFilter == key) EndMaskEdit();
        _pluginState.Masks[key] = new MaskSpec { Kind = MaskRender.Link, Ref = src, Invert = inv, Adjust = PluginEditState.CopyAdjust(old?.Adjust) };
        RebuildPluginUi();
        OnPluginUiChanged();
        RefreshMaskPins();
        SetStatus($"{MaskOwnerLabel(key)} now uses the mask of {MaskOwnerLabel(src)}", error: false);
    }

    /// <summary>
    /// Gives <paramref name="key"/> an own copy of the mask of <paramref name="src"/> (same place, shape and invert) and
    /// starts editing it, so it can be changed without changing the original.
    /// </summary>
    private void CopyMaskFrom(string key, string src)
    {
        if (key == src || !_pluginState.Masks.TryGetValue(src, out var s) || _pluginState.Resolve(s) is not { } r) return;
        _pluginState.DetachLinksTo(key);
        if (_maskEditFilter is not null) EndMaskEdit();
        var own = _pluginState.Masks.TryGetValue(key, out var was) ? PluginEditState.CopyAdjust(was.Adjust) : null;
        var copy = PluginEditState.CloneMask(r) with { Ref = null, Adjust = own }; // the Adjustments values stay with the panel
        _pluginState.Masks[key] = copy;
        if (IsInteractiveMask(copy.Kind)) BeginMaskEdit(key);
        RebuildPluginUi();
        OnPluginUiChanged();
        RefreshMaskPins();
        SetStatus($"{MaskOwnerLabel(key)} has a copy of the mask of {MaskOwnerLabel(src)} — change it here, the original stays as it is", error: false);
    }

    /// <summary>
    /// Mask bar line while a mask used by other tools is edited: who shares it, and a button to edit it for one tool only
    /// (the tool the edit was opened from, else the owner of the mask, the others keeping the current shape).
    /// </summary>
    private void UpdateMaskBarShared(string key)
    {
        var links = _pluginState.LinksTo(key);
        if (links.Count == 0)
        {
            MaskBarShared.Visibility = Visibility.Collapsed;
            BtnMaskOnlyHere.Visibility = Visibility.Collapsed;
            return;
        }
        var users = links.Select(MaskOwnerLabel).ToList();
        MaskBarShared.Text = $"Shared with {string.Join(", ", users)} — changes apply to {(links.Count == 1 ? "both" : "all of them")}";
        MaskBarShared.Visibility = Visibility.Visible;
        bool fromLink = _maskEditVia is { } via && links.Contains(via);
        string only = fromLink ? MaskOwnerLabel(_maskEditVia!) : MaskOwnerLabel(key);
        BtnMaskOnlyHere.Content = $"Edit only for {only}";
        BtnMaskOnlyHere.ToolTip = fromLink
            ? $"{only} gets its own copy of this mask and you go on editing that copy; {MaskOwnerLabel(key)} keeps this shape"
            : $"The other tools ({string.Join(", ", users)}) keep the current shape as their own copy; your changes apply only to {only}";
        BtnMaskOnlyHere.Visibility = Visibility.Visible;
    }

    private void OnMaskOnlyHereClick(object sender, RoutedEventArgs e)
    {
        if (_maskEditFilter is not { } key) return;
        var links = _pluginState.LinksTo(key);
        if (links.Count == 0) return;
        if (_maskEditVia is { } via && links.Contains(via) && _pluginState.Masks.TryGetValue(via, out var l) && _pluginState.Resolve(l) is { } r)
        {
            // the edit was opened from the linked tool: it gets the copy, editing continues there
            _pluginState.Masks[via] = PluginEditState.CloneMask(r) with { Ref = null, Adjust = PluginEditState.CopyAdjust(l.Adjust) };
            OnPluginUiChanged();
            EditMaskFromPin(via);
            SetStatus($"{MaskOwnerLabel(via)} has its own copy of the mask now — changes no longer affect {MaskOwnerLabel(key)}", error: false);
            return;
        }
        _pluginState.DetachLinksTo(key);
        OnPluginUiChanged();
        UpdateMaskBar();
        RebuildPluginUi();
        RefreshMaskPins();
        SetStatus($"Only {MaskOwnerLabel(key)} follows this mask now; the others keep their own copy", error: false);
    }

    /// <summary>Options of a linked mask: which mask it uses, invert, edit that mask, unlink.</summary>
    private FrameworkElement BuildMaskLinkOptions(string key, MaskSpec link, Brush muted)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        bool ok = _pluginState.Resolve(link) is not null;
        string? src = link.Ref;
        var info = new TextBlock
        {
            Text = ok && src is not null
                ? $"Uses the mask of {MaskOwnerLabel(src)}"
                : "The linked mask no longer exists — the whole photo is used",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        };
        if (ok) info.Foreground = muted;
        else info.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
        AutomationProperties.SetAutomationId(info, "PluginMaskLinkInfo_" + key);
        sp.Children.Add(info);

        var opts = new WrapPanel();
        var inv = new CheckBox
        {
            Content = "Invert", IsChecked = link.Invert, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Use the outside of the linked mask (relative to that mask, also when it is inverted itself)"
        };
        inv.Click += (_, _) =>
        {
            if (_pluginState.Masks.TryGetValue(key, out var m)) _pluginState.Masks[key] = m with { Invert = inv.IsChecked == true };
            OnPluginUiChanged();
        };
        inv.SetResourceReference(ForegroundProperty, "ForegroundBrush"); // readable on the panel background
        opts.Children.Add(inv);
        if (ok && src is not null)
        {
            var edit = new Button { Content = "Edit that mask", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 6, 0), ToolTip = $"Opens the mask of {MaskOwnerLabel(src)} for editing; this one follows" };
            edit.SetResourceReference(StyleProperty, "ToolButton");
            AutomationProperties.SetAutomationId(edit, "PluginMaskLinkEdit_" + key);
            edit.Click += (_, _) =>
            {
                EditMaskFromPin(src);
                if (_maskEditFilter == src)
                {
                    _maskEditVia = key; // "Edit only for <this tool>" in the mask bar copies the mask for this tool
                    UpdateMaskBar();
                }
            };
            opts.Children.Add(edit);
        }
        var unlink = new Button
        {
            Content = "Unlink", Padding = new Thickness(8, 1, 8, 1),
            ToolTip = "Make this an own copy of the mask, to change it without changing the other one"
        };
        unlink.SetResourceReference(StyleProperty, "ToolButton");
        AutomationProperties.SetAutomationId(unlink, "PluginMaskUnlink_" + key);
        unlink.Click += (_, _) =>
        {
            if (!_pluginState.Masks.TryGetValue(key, out var m)) return;
            if (_pluginState.Resolve(m) is { } r) _pluginState.Masks[key] = PluginEditState.CloneMask(r) with { Ref = null, Adjust = PluginEditState.CopyAdjust(m.Adjust) };
            else _pluginState.Masks.Remove(key);
            RebuildPluginUi();
            OnPluginUiChanged();
            RefreshMaskPins();
            SetStatus($"{MaskOwnerLabel(key)} has its own mask now", error: false);
        };
        opts.Children.Add(unlink);
        sp.Children.Add(opts);
        return sp;
    }

    /// <summary>Markers of every mask except the one being edited; shown while editing or with the markers toggle.</summary>
    private void RefreshMaskPins()
    {
        if (MaskOverlay is null) return;
        bool editing = _maskEditFilter is not null;
        bool show = !_cropMode && !_presenting && !_compareOn && _baseInfo is ImageInfo && RightPanel.Visibility == Visibility.Visible
                    && (editing || ShowMaskMarkersAlways);
        var pins = new List<MaskOverlay.MaskPin>();
        if (show)
        {
            foreach (var (key, spec) in _pluginState.Masks.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (key == _maskEditFilter || spec is null) continue;
                if (MaskRender.PinPoint(spec) is not { } pt) continue;
                if (MaskPinLabel(key, spec) is not { } label) continue;
                var users = _pluginState.LinksTo(key).Select(MaskOwnerLabel).ToList();
                if (users.Count > 0) label += "\nAlso used by: " + string.Join(", ", users);
                pins.Add(new MaskOverlay.MaskPin(key, label, spec, new Point(pt.X, pt.Y)));
            }
        }
        if (!editing)
        {
            bool visible = show && pins.Count > 0;
            if (visible && _baseInfo is ImageInfo bi) MaskOverlay.SetMapping(CurrentDisplayGeo(), bi.Width, bi.Height);
            var v = visible ? Visibility.Visible : Visibility.Collapsed;
            if (MaskOverlay.Visibility != v) MaskOverlay.Visibility = v;
        }
        MaskOverlay.SetPins(pins, show);
    }

    private void OnMaskPinClicked(IReadOnlyList<MaskOverlay.MaskPin> pins, Point at)
    {
        if (pins.Count == 0) return;
        if (pins.Count == 1)
        {
            EditMaskFromPin(pins[0].Key);
            return;
        }
        var menu = new ContextMenu
        {
            PlacementTarget = MaskOverlay,
            Placement = PlacementMode.RelativePoint,
            HorizontalOffset = at.X + 8,
            VerticalOffset = at.Y + 8
        };
        foreach (var p in pins)
        {
            var item = new MenuItem { Header = p.Label };
            item.Click += (_, _) => EditMaskFromPin(p.Key);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>Starts editing the mask of <paramref name="key"/>: opens its section (and repeat area) and scrolls it into view.</summary>
    private void EditMaskFromPin(string key)
    {
        if (!_pluginState.Masks.TryGetValue(key, out var spec) || MaskTarget(key) is not { } t) return;
        if (_maskEditFilter == key) return;
        if (_maskEditFilter is not null) EndMaskEdit();
        if (key != AdjustMask.Key && _pluginReg.FindFilter(key) is { } f)
        {
            if (f.D.RepeatGroup.Length > 0) _repeatSelected[RepeatKey(f)] = f.FullId;
            if (!IsSectionOpenNow(f.PluginId)) SetSectionExpanded(f.PluginId, true);
        }
        BeginMaskEdit(key);
        RebuildPluginUi();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var root = key == AdjustMask.Key ? (DependencyObject)AdjMaskHost : PluginPanelHost;
            if (FindByAutomationId(root, "PluginMask_" + key) is FrameworkElement el) el.BringIntoView();
        });
        SetStatus($"Editing mask: {MaskPinLabel(key, spec) ?? t.Label}", error: false);
    }

    private static DependencyObject? FindByAutomationId(DependencyObject root, string id)
    {
        if (AutomationProperties.GetAutomationId(root) == id) return root;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            if (FindByAutomationId(VisualTreeHelper.GetChild(root, i), id) is { } hit) return hit;
        return null;
    }
}
