using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace ImgViewer;

/// <summary>Shown before a plugin is installed. Nothing from the plugin has been executed at this point.</summary>
public sealed class TrustDialog : Window
{
    public TrustDialog(PluginInspection insp)
    {
        Title = "Install plugin?";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (Brush)Application.Current.Resources["WindowBrush"];
        Foreground = (Brush)Application.Current.Resources["ForegroundBrush"];
        FontSize = 12;
        var muted = (Brush)Application.Current.Resources["MutedBrush"];
        var warn = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D));

        var root = new StackPanel { Margin = new Thickness(18, 14, 18, 12) };
        var m = insp.Manifest;
        root.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        var sub = string.Join("  ·  ", new[] { m.Version, m.Author, insp.IsPackage ? "id " + m.Id : "loose assembly" }.Where(s => !string.IsNullOrWhiteSpace(s)));
        root.Children.Add(new TextBlock { Text = sub, Foreground = muted, Margin = new Thickness(0, 2, 0, 0) });
        if (!string.IsNullOrWhiteSpace(m.Description))
            root.Children.Add(new TextBlock { Text = m.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });

        void Row(string label, string value, Brush? brush = null, string? autoId = null)
        {
            var g = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var l = new TextBlock { Text = label, Foreground = muted };
            var v = new TextBox
            {
                Text = value,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = brush ?? Foreground,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = label.StartsWith("SHA") ? new FontFamily("Consolas") : FontFamily
            };
            if (autoId is not null) AutomationProperties.SetAutomationId(v, autoId);
            Grid.SetColumn(v, 1);
            g.Children.Add(l);
            g.Children.Add(v);
            root.Children.Add(g);
        }

        var sig = insp.Signature;
        Row("Publisher", sig.Display, sig.Signed && sig.Valid ? null : warn, "TrustSignature");
        if (insp.IsPackage) Row("SHA-256 package", insp.PackageSha256, null, "TrustPackageSha");
        Row("SHA-256 assembly", insp.EntrySha256, null, "TrustEntrySha");
        Row("Plugin classes", insp.PluginTypes.Count > 0 ? string.Join("\n", insp.PluginTypes) : "(none found)");
        Row("Files", string.Join(", ", insp.Files));

        root.Children.Add(new TextBlock { Text = "Requested permissions", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 2) });
        if (insp.IsPackage && m.Permissions.Length == 0)
            root.Children.Add(new TextBlock { Text = "None declared (image processing only).", Foreground = muted });
        foreach (var p in m.Permissions)
        {
            var desc = PluginTrust.KnownPermissions.TryGetValue(p, out var d) ? d : "Unknown permission";
            root.Children.Add(new TextBlock { Text = $"•  {p} — {desc}", TextWrapping = TextWrapping.Wrap });
        }
        foreach (var w in insp.Warnings)
            root.Children.Add(new TextBlock { Text = "⚠  " + w, Foreground = warn, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        root.Children.Add(new TextBlock
        {
            Text = "Plugins run inside PhotographerEye with the same rights as the app. Permissions are informational, not enforced. Install only plugins from publishers you trust."
                   + (insp.AlreadyInstalled ? "\nAn installed copy will be replaced." : ""),
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0)
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = insp.AlreadyInstalled ? "Replace" : "Install", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsDefault = false };
        AutomationProperties.SetAutomationId(ok, "TrustInstall");
        ok.Click += (_, _) => { DialogResult = true; };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true, IsDefault = true };
        AutomationProperties.SetAutomationId(cancel, "TrustCancel");
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
    }
}
