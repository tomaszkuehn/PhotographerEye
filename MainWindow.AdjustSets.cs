using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ImgViewer.Imaging;
using ImgViewer.Services;

namespace ImgViewer;

/// <summary>
/// The sets of Adjustments panel values: whole photo (the .eye core fields) and the own values of each mask
/// (<see cref="MaskSpec.Adjust"/>, added to the whole-photo values inside that mask). Any mask can have them: the
/// Adjustments mask or the mask of any filter, whichever tool made it. The panel sliders show one set at a time; while
/// they show a mask's set (<see cref="_adjMaskKey"/>) the whole-photo values wait in <see cref="_adjGlobal"/>.
/// </summary>
public partial class MainWindow
{
    /// <summary>Whole-photo values of the panel sliders while the sliders show a mask's set; null otherwise.</summary>
    private Dictionary<string, double>? _adjGlobal;
    private bool _adjSwitching;

    /// <summary>The mask whose own values the sliders show while <see cref="AdjEditMask"/>.</summary>
    private string? _adjMaskKey;

    /// <summary>The sliders show (and change) the own values of a mask (<see cref="AdjSetKey"/>).</summary>
    private bool AdjEditMask => _adjGlobal is not null;

    private string AdjSetKey => _adjMaskKey ?? AdjustMask.Key;

    /// <summary>A panel-only slider (STANDARD / DETAIL): it has a value of its own inside a mask.</summary>
    private static bool IsMaskableCore(CoreControl c) => ReferenceEquals(c.Panel, c.Slider);

    private CoreControl CoreCtl(string key) => CoreUi().First(c => c.P.Key == key);

    /// <summary>Whole-photo value of a slider, whichever set the sliders show.</summary>
    private double GlobalCore(CoreControl c) =>
        _adjGlobal is { } g && IsMaskableCore(c) ? g.GetValueOrDefault(c.P.Key, c.P.Default) : c.Slider.Value;

    private bool HasMask(string key) => _pluginState.Masks.TryGetValue(key, out var m) && !string.IsNullOrEmpty(m.Kind);

    private bool HasAdjMask() => HasMask(AdjustMask.Key);

    /// <summary>Panel values (slider units per key) to the renderer's adjustments; null when neutral unless <paramref name="force"/>.</summary>
    private PhotoAdjustments? BuildExtras(Func<CoreControl, double> v, bool force)
    {
        double V(string key) => v(CoreCtl(key));
        var a = PhotoAdjustments.FromUi(V("Exposure"), V("Contrast"), V("Temperature"), V("Saturation"), V("Clarity"), V("Sharpen"),
            V("Highlights"), V("Whites"), V("Blacks"), V("Dehaze"), V("Denoise"), V("ColorNoise"));
        a.Tint = V("Tint") / 100.0;
        a.Curves = HistogramPanel.CurvesSnapshot();
        return a.IsDefault && !force ? null : a;
    }

    /// <summary>
    /// Values inside every mask that has values of its own (whole photo + mask), the Adjustments mask first; null when no
    /// mask has values.
    /// </summary>
    private IReadOnlyList<(string Key, PhotoAdjustments Values)>? MaskExtras()
    {
        if (ExpSlider is null) return null;
        List<(string Key, PhotoAdjustments Values)>? list = null;
        foreach (var key in _pluginState.Masks.Keys.OrderBy(k => k == AdjustMask.Key ? 0 : 1).ThenBy(k => k, StringComparer.Ordinal))
        {
            if (!HasMask(key) || AdjustMask.For(_pluginState, key) is null || AdjustMask.Values(_pluginState, key) is not { } local) continue;
            if (BuildExtras(c => AdjustMask.Combine(c.P, GlobalCore(c), local), force: true) is { } v) (list ??= new()).Add((key, v));
        }
        return list;
    }

    /// <summary>
    /// Shows the whole-photo set or the own values of the mask of <paramref name="key"/> (default: the Adjustments mask)
    /// on the panel sliders; a mask's set only while that mask exists. Switching from one mask's set to another's goes
    /// through the whole-photo values.
    /// </summary>
    private void ShowAdjSet(bool mask, string? key = null)
    {
        key ??= AdjustMask.Key;
        if (mask && !HasMask(key)) mask = false;
        if (mask ? AdjEditMask && _adjMaskKey == key : !AdjEditMask) return;
        _adjSwitching = true;
        try
        {
            if (AdjEditMask)
            {
                var g = _adjGlobal!;
                _adjGlobal = null;
                _adjMaskKey = null;
                foreach (var c in CoreUi().Where(IsMaskableCore))
                    c.Slider.Value = Math.Clamp(g.GetValueOrDefault(c.P.Key, c.P.Default), c.Slider.Minimum, c.Slider.Maximum);
            }
            if (mask)
            {
                var local = _pluginState.Masks[key].Adjust;
                var g = new Dictionary<string, double>();
                foreach (var c in CoreUi().Where(IsMaskableCore)) g[c.P.Key] = c.Slider.Value;
                _adjGlobal = g;
                _adjMaskKey = key;
                foreach (var c in CoreUi().Where(IsMaskableCore))
                    c.Slider.Value = Math.Clamp(local?.GetValueOrDefault(c.P.Key, c.P.Default) ?? c.P.Default, c.Slider.Minimum, c.Slider.Maximum);
            }
        }
        finally
        {
            _adjSwitching = false;
        }
        UpdatePanelLabels();
    }

    /// <summary>The sliders go back to the whole-photo values when the mask whose values they show is gone (undo, area removed).</summary>
    private void CheckAdjSet()
    {
        if (AdjEditMask && !HasMask(AdjSetKey)) ShowAdjSet(false);
    }

    /// <summary>While the sliders show a mask's set: their values become that mask's own values.</summary>
    private void StoreMaskSliders()
    {
        if (!AdjEditMask || _adjSwitching || !_pluginState.Masks.TryGetValue(AdjSetKey, out var m)) return;
        var d = new Dictionary<string, double>();
        foreach (var c in CoreUi().Where(IsMaskableCore))
            if (Math.Abs(c.Slider.Value - c.P.Default) > 1e-9) d[c.P.Key] = c.Slider.Value;
        _pluginState.Masks[AdjSetKey] = m with { Adjust = d.Count > 0 ? d : null };
    }

    /// <summary>"Exposure +0.50, Temperature -20" for the own values of the mask of <paramref name="key"/> (default: Adjustments), or null.</summary>
    private string? MaskValuesSummary(string? key = null)
    {
        if (!_pluginState.Masks.TryGetValue(key ?? AdjustMask.Key, out var m) || m.Adjust is not { Count: > 0 } a) return null;
        var parts = CoreUi().Where(IsMaskableCore).Where(c => a.ContainsKey(c.P.Key)).Select(c => $"{c.P.Label} {c.P.Format(a[c.P.Key])}");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// "Sliders change: Whole photo | Inside the mask" under the mask icons (Adjustments panel and the mask of every
    /// filter): the STANDARD / DETAIL sliders set the whole photo or the own values of the mask of <paramref name="key"/>.
    /// </summary>
    private FrameworkElement BuildAdjSetToggle(Brush muted, string? key = null)
    {
        key ??= AdjustMask.Key;
        bool core = key == AdjustMask.Key;
        bool mine = AdjEditMask && _adjMaskKey == key;
        string sfx = core ? "" : "_" + key;
        var sp = new StackPanel { Margin = new Thickness(0, 5, 0, 2) };
        var row = new WrapPanel();
        row.Children.Add(new TextBlock
        {
            Text = core ? "Sliders change" : "Adjustments sliders change",
            Foreground = muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 3)
        });
        ToggleButton Seg(string text, string id, bool mask, string tip)
        {
            var b = new ToggleButton { Content = text, IsChecked = mask ? mine : !AdjEditMask, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 4, 3), ToolTip = tip };
            b.SetResourceReference(StyleProperty, "ToolToggle");
            AutomationProperties.SetAutomationId(b, id + sfx);
            b.Click += (_, _) =>
            {
                b.IsChecked = true;
                if (mask ? AdjEditMask && _adjMaskKey == key : !AdjEditMask) return;
                ShowAdjSet(mask, key);
                DeferRebuild();
                SetStatus(mask
                    ? $"Sliders now set the own values of the {MaskOwnerLabel(key)} mask — inside it they add to the whole-photo values"
                    : "Sliders now set the whole-photo values", error: false);
            };
            return b;
        }
        row.Children.Add(Seg("Whole photo", "BtnAdjSetWhole", false, "The STANDARD / DETAIL sliders change the whole photo (also inside the mask)"));
        row.Children.Add(Seg("Inside the mask", "BtnAdjSetMask", true,
            "The STANDARD / DETAIL sliders set this mask's own values: inside the mask they add to the whole-photo values, outside it nothing changes"));
        sp.Children.Add(row);
        string? summary = MaskValuesSummary(key);
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) };
        AutomationProperties.SetAutomationId(hint, "AdjSetHint" + sfx);
        if (mine)
        {
            hint.Text = "Mask values — added to the whole-photo values inside the mask";
            hint.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        }
        else
        {
            hint.Text = summary is null ? "The mask has no values of its own yet — choose Inside the mask to set them" : "In the mask: " + summary;
            if (AdjEditMask) hint.Text += $" — the sliders now set the {MaskOwnerLabel(AdjSetKey)} mask";
            hint.Foreground = muted;
        }
        sp.Children.Add(hint);
        return sp;
    }
}
