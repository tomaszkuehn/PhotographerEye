using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ImgViewer;

public sealed class AboutWindow : Window
{
    public AboutWindow()
    {
        Title = "ImgViewer — O aplikacji";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)Application.Current.Resources["WindowBrush"];
        Foreground = (Brush)Application.Current.Resources["ForegroundBrush"];
        FontSize = 12;
        ResizeMode = ResizeMode.NoResize;
        Icon = Application.Current.MainWindow?.Icon;

        var muted = (Brush)Application.Current.Resources["MutedBrush"];
        var version = Assembly.GetEntryAssembly()?.GetName().Version;

        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 12) };

        panel.Children.Add(new TextBlock
        {
            Text = "ImgViewer",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Przeglądarka obrazów JPG/PNG  •  wersja {version?.Major ?? 1}.{version?.Minor ?? 0}",
            Foreground = muted,
            Margin = new Thickness(0, 4, 0, 14),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        panel.Children.Add(MakeLabel("Autor:"));
        panel.Children.Add(new TextBlock { Text = "Tomasz Kuehn", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 1, 0, 0) });

        panel.Children.Add(MakeLabel("E-mail:"));
        panel.Children.Add(MakeLink("tomasz.kuehn@gmail.com", "mailto:tomasz.kuehn@gmail.com", muted));

        panel.Children.Add(MakeLabel("GitHub:"));
        panel.Children.Add(MakeLink("https://github.com/tomaszkuehn", "https://github.com/tomaszkuehn", muted));

        panel.Children.Add(new TextBlock
        {
            Text = "Licencja: użytek prywatny, niekomercyjny.\nZakaz modyfikacji i redystrybucji (LICENSE).",
            Foreground = muted,
            Margin = new Thickness(0, 14, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        var ok = new Button
        {
            Content = "OK",
            Width = 88,
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(ok, "BtnAboutOk");
        ok.Click += (_, _) => Close();
        panel.Children.Add(ok);

        Content = panel;
    }

    private static TextBlock MakeLabel(string text) =>
        new()
        {
            Text = text,
            Foreground = (Brush)Application.Current.Resources["MutedBrush"],
            Margin = new Thickness(0, 7, 0, 0),
            FontSize = 11
        };

    private static TextBlock MakeLink(string text, string url, Brush foreground)
    {
        var link = new Hyperlink(new Run(text))
        {
            Foreground = foreground,
            TextDecorations = System.Windows.TextDecorations.Underline
        };
        link.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception)
            {
            }
        };
        return new TextBlock(link) { Margin = new Thickness(0, 1, 0, 0) };
    }
}