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
/// The two sets of Adjustments panel values: whole photo (the .eye core fields) and the Adjustments mask's own
/// (<see cref="MaskSpec.Adjust"/>, added to the whole-photo values inside the mask). The panel sliders show one set at a
/// time; while they show the mask's set the whole-photo values wait in <see cref="_adjGlobal"/>.
/// </summary>
public partial class MainWindow
{
    /// <summary>Whole-photo values of the panel sliders while the sliders show the mask's set; null otherwise.</summary>
    private Dictionary<string, double>? _adjGlobal;
    private bool _adjSwitching;

    /// <summary>The sliders show (and change) the values of the Adjustments mask.</summary>
    private bool AdjEditMask => _adjGlobal is not null;

    /// <summary>A panel-only slider (STANDARD / DETAIL): it has a value of its own inside the mask.</summary>
    private static bool IsMaskableCore(CoreControl c) => ReferenceEquals(c.Panel, c.Slider);

    private CoreControl CoreCtl(string key) => CoreUi().First(c => c.P.Key == key);

    /// <summary>Whole-photo value of a slider, whichever set the sliders show.</summary>
    private double GlobalCore(CoreControl c) =>
        _adjGlobal is { } g && IsMaskableCore(c) ? g.GetValueOrDefault(c.P.Key, c.P.Default) : c.Slider.Value;

    private bool HasAdjMask() => _pluginState.Masks.TryGetValue(AdjustMask.Key, out var m) && !string.IsNullOrEmpty(m.Kind);

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

    /// <summary>Values inside the Adjustments mask (whole photo + mask); null without a mask or when the mask has no values.</summary>
    private PhotoAdjustments? MaskExtras()
    {
        if (ExpSlider is null || AdjustMask.For(_pluginState) is null || AdjustMask.Values(_pluginState) is not { } local) return null;
        return BuildExtras(c => AdjustMask.Combine(c.P, GlobalCore(c), local), force: true);
    }

    /// <summary>Shows the whole-photo set or the mask's set on the panel sliders (the mask's set only while the panel has a mask).</summary>
    private void ShowAdjSet(bool mask)
    {
        if (mask && !HasAdjMask()) mask = false;
        if (mask == AdjEditMask) return;
        _adjSwitching = true;
        try
        {
            if (mask)
            {
                var local = _pluginState.Masks[AdjustMask.Key].Adjust;
                var g = new Dictionary<string, double>();
                foreach (var c in CoreUi().Where(IsMaskableCore)) g[c.P.Key] = c.Slider.Value;
                _adjGlobal = g;
                foreach (var c in CoreUi().Where(IsMaskableCore))
                    c.Slider.Value = Math.Clamp(local?.GetValueOrDefault(c.P.Key, c.P.Default) ?? c.P.Default, c.Slider.Minimum, c.Slider.Maximum);
            }
            else
            {
                var g = _adjGlobal!;
                _adjGlobal = null;
                foreach (var c in CoreUi().Where(IsMaskableCore))
                    c.Slider.Value = Math.Clamp(g.GetValueOrDefault(c.P.Key, c.P.Default), c.Slider.Minimum, c.Slider.Maximum);
            }
        }
        finally
        {
            _adjSwitching = false;
        }
        UpdatePanelLabels();
    }

    /// <summary>While the sliders show the mask's set: their values become the mask's own values.</summary>
    private void StoreMaskSliders()
    {
        if (!AdjEditMask || _adjSwitching || !_pluginState.Masks.TryGetValue(AdjustMask.Key, out var m)) return;
        var d = new Dictionary<string, double>();
        foreach (var c in CoreUi().Where(IsMaskableCore))
            if (Math.Abs(c.Slider.Value - c.P.Default) > 1e-9) d[c.P.Key] = c.Slider.Value;
        _pluginState.Masks[AdjustMask.Key] = m with { Adjust = d.Count > 0 ? d : null };
    }

    /// <summary>"Exposure +0.50, Temperature -20" for the mask's own values, or null when it has none.</summary>
    private string? MaskValuesSummary()
    {
        if (!_pluginState.Masks.TryGetValue(AdjustMask.Key, out var m) || m.Adjust is not { Count: > 0 } a) return null;
        var parts = CoreUi().Where(IsMaskableCore).Where(c => a.ContainsKey(c.P.Key)).Select(c => $"{c.P.Label} {c.P.Format(a[c.P.Key])}");
        return string.Join(", ", parts);
    }

    /// <summary>"Sliders change: Whole photo | Inside the mask" under the mask icons of the Adjustments panel.</summary>
    private FrameworkElement BuildAdjSetToggle(Brush muted)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 5, 0, 2) };
        var row = new WrapPanel();
        row.Children.Add(new TextBlock { Text = "Sliders change", Foreground = muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 3) });
        ToggleButton Seg(string text, string id, bool mask, string tip)
        {
            var b = new ToggleButton { Content = text, IsChecked = AdjEditMask == mask, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 4, 3), ToolTip = tip };
            b.SetResourceReference(StyleProperty, "ToolToggle");
            AutomationProperties.SetAutomationId(b, id);
            b.Click += (_, _) =>
            {
                b.IsChecked = true;
                if (AdjEditMask == mask) return;
                ShowAdjSet(mask);
                DeferRebuild();
                SetStatus(mask
                    ? "Sliders now set the mask's own values — inside the mask they add to the whole-photo values"
                    : "Sliders now set the whole-photo values", error: false);
            };
            return b;
        }
        row.Children.Add(Seg("Whole photo", "BtnAdjSetWhole", false, "The sliders change the whole photo (also inside the mask)"));
        row.Children.Add(Seg("Inside the mask", "BtnAdjSetMask", true,
            "The sliders set the mask's own values: inside the mask they add to the whole-photo values, outside it nothing changes"));
        sp.Children.Add(row);
        string? summary = MaskValuesSummary();
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) };
        AutomationProperties.SetAutomationId(hint, "AdjSetHint");
        if (AdjEditMask)
        {
            hint.Text = "Mask values — added to the whole-photo values inside the mask";
            hint.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        }
        else
        {
            hint.Text = summary is null ? "The mask has no values of its own yet — choose Inside the mask to set them" : "In the mask: " + summary;
            hint.Foreground = muted;
        }
        sp.Children.Add(hint);
        return sp;
    }
}
