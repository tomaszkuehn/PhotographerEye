using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ImgViewer.Services;

namespace ImgViewer;

/// <summary>
/// The privacy policy (<see cref="PrivacyPolicy"/>) in Polish or English. On the first start it must be accepted
/// (DialogResult true = accepted, false = declined or closed); from the settings it is only shown.
/// </summary>
public sealed class PrivacyWindow : Window
{
    private readonly bool _mustAccept;
    private readonly DateTime? _acceptedAt;
    private string _lang = PrivacyPolicy.DefaultLanguage;
    private readonly StackPanel _doc = new() { Margin = new Thickness(22, 14, 22, 18) };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly Button _accept = new() { MinWidth = 96, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0), IsDefault = true };
    private readonly Button _decline = new() { MinWidth = 96, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
    private readonly Button _close = new() { MinWidth = 96, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
    private readonly Button _pl = LangButton("Polski"), _en = LangButton("English");
    private readonly ScrollViewer _scroll;

    public PrivacyWindow(bool mustAccept, DateTime? acceptedAt = null)
    {
        _mustAccept = mustAccept;
        _acceptedAt = acceptedAt;
        Width = 680;
        Height = 700;
        MinWidth = 420;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)Application.Current.Resources["WindowBrush"];
        Foreground = (Brush)Application.Current.Resources["ForegroundBrush"];
        FontSize = 13;
        try { Icon = Application.Current.MainWindow?.Icon ?? System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/icon.ico")); }
        catch (Exception) { }
        if (mustAccept) ShowInTaskbar = true;

        AutomationProperties.SetAutomationId(_accept, "PrivacyAccept");
        AutomationProperties.SetAutomationId(_decline, "PrivacyDecline");
        AutomationProperties.SetAutomationId(_close, "PrivacyClose");
        AutomationProperties.SetAutomationId(_pl, "PrivacyLangPl");
        AutomationProperties.SetAutomationId(_en, "PrivacyLangEn");
        AutomationProperties.SetAutomationId(_note, "PrivacyNote");
        AutomationProperties.SetAutomationId(_doc, "PrivacyText");

        _pl.Click += (_, _) => SetLanguage("pl");
        _en.Click += (_, _) => SetLanguage("en");
        // closing the first-start window without a choice declines too (ShowDialog returns false)
        _accept.Click += (_, _) => DialogResult = true;
        _decline.Click += (_, _) => DialogResult = false;
        _close.Click += (_, _) => Close();

        var langs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 14, 0) };
        langs.Children.Add(_pl);
        langs.Children.Add(_en);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (mustAccept)
        {
            buttons.Children.Add(_decline);
            buttons.Children.Add(_accept);
        }
        else buttons.Children.Add(_close);
        var bottom = new DockPanel { Margin = new Thickness(18, 10, 18, 12) };
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_note);
        var bottomBorder = new Border
        {
            Child = bottom,
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = (Brush)Application.Current.Resources["MutedBrush"]
        };

        _scroll = new ScrollViewer { Content = _doc, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        var root = new DockPanel();
        DockPanel.SetDock(langs, Dock.Top);
        DockPanel.SetDock(bottomBorder, Dock.Bottom);
        root.Children.Add(langs);
        root.Children.Add(bottomBorder);
        root.Children.Add(_scroll);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            // the document scrolls with the keyboard although the buttons hold the focus
            if (e.Key == Key.PageDown) { _scroll.PageDown(); e.Handled = true; }
            else if (e.Key == Key.PageUp) { _scroll.PageUp(); e.Handled = true; }
        };
        SetLanguage(_lang);
    }

    private void SetLanguage(string lang)
    {
        _lang = lang;
        _pl.FontWeight = lang == "pl" ? FontWeights.Bold : FontWeights.Normal;
        _en.FontWeight = lang == "en" ? FontWeights.Bold : FontWeights.Normal;
        bool pl = lang == "pl";
        Title = pl ? "PhotographerEye — Polityka prywatności" : "PhotographerEye — Privacy policy";
        _accept.Content = pl ? "Akceptuję" : "Accept";
        _decline.Content = pl ? "Nie akceptuję — zamknij" : "Decline and exit";
        _close.Content = pl ? "Zamknij" : "Close";
        _note.Text = _mustAccept
            ? pl ? "Aby korzystać z PhotographerEye, zaakceptuj politykę prywatności." : "To use PhotographerEye, please accept the privacy policy."
            : _acceptedAt is DateTime at
                ? (pl ? $"Zaakceptowano {at:d MMMM yyyy, HH:mm}" : $"Accepted on {at:d MMMM yyyy, HH:mm}") + $" (v{PrivacyPolicy.Version})"
                : $"v{PrivacyPolicy.Version}";
        _note.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
        Render(PrivacyPolicy.Text(lang));
        _scroll.ScrollToTop();
    }

    // ---------------- a small Markdown subset: # / ## headings, "- " bullets, paragraphs, **bold**, *italic*, `code`

    private void Render(string md)
    {
        _doc.Children.Clear();
        foreach (var raw in md.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith("# "))
                _doc.Children.Add(Para(line[2..], 20, FontWeights.SemiBold, new Thickness(0, 0, 0, 2)));
            else if (line.StartsWith("## "))
                _doc.Children.Add(Para(line[3..], 15, FontWeights.SemiBold, new Thickness(0, 14, 0, 4)));
            else if (line.StartsWith("- "))
            {
                var row = new DockPanel { Margin = new Thickness(6, 2, 0, 2) };
                var dot = new TextBlock { Text = "•", Margin = new Thickness(0, 0, 8, 0) };
                DockPanel.SetDock(dot, Dock.Left);
                row.Children.Add(dot);
                row.Children.Add(Para(line[2..], 13, FontWeights.Normal, new Thickness(0)));
                _doc.Children.Add(row);
            }
            else _doc.Children.Add(Para(line, 13, FontWeights.Normal, new Thickness(0, 3, 0, 3)));
        }
    }

    private static readonly Regex InlineRx = new(@"\*\*(.+?)\*\*|`(.+?)`|\*(.+?)\*", RegexOptions.Compiled);

    private static TextBlock Para(string text, double size, FontWeight weight, Thickness margin)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = weight, Margin = margin };
        int at = 0;
        foreach (Match m in InlineRx.Matches(text))
        {
            if (m.Index > at) tb.Inlines.Add(new Run(text[at..m.Index]));
            if (m.Groups[1].Success) tb.Inlines.Add(new Bold(new Run(m.Groups[1].Value)));
            else if (m.Groups[2].Success) tb.Inlines.Add(new Run(m.Groups[2].Value) { FontFamily = new FontFamily("Consolas") });
            else tb.Inlines.Add(new Italic(new Run(m.Groups[3].Value)));
            at = m.Index + m.Length;
        }
        if (at < text.Length) tb.Inlines.Add(new Run(text[at..]));
        return tb;
    }

    private static Button LangButton(string text) =>
        new() { Content = text, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(4, 0, 0, 0), Focusable = false };
}
