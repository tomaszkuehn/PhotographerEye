using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ImgViewer;

public sealed class PluginsWindow : Window
{
    private readonly StackPanel _list;
    private readonly TextBlock _status;

    public PluginsWindow()
    {
        Debug.Log("PLUGINS-WINDOW: ctor start");
        Title = "PhotographerEye — Plugins";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.Resources["WindowBrush"];
        Foreground = (Brush)Application.Current.Resources["ForegroundBrush"];
        FontSize = 12;
        ResizeMode = ResizeMode.NoResize;
        Icon = Application.Current.MainWindow?.Icon;

        var muted = (Brush)Application.Current.Resources["MutedBrush"];
        var panel = new DockPanel { Margin = new Thickness(18, 14, 18, 12) };

        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        top.Children.Add(new TextBlock
        {
            Text = "Plugins",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        top.Children.Add(new TextBlock
        {
            Text = "Import a .pepkg package (or a plugin .dll). Filters appear in the Adjustments panel or on the toolbar, exporters and commands in the menu. Installed to %APPDATA%\\PhotographerEye\\Plugins. A plugin that fails 3 times in a row is switched off automatically.",
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        panel.Children.Add(top);
        DockPanel.SetDock(top, Dock.Top);

        _list = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = _list,
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var listBorder = new Border
        {
            Child = scroll,
            BorderBrush = (Brush)Application.Current.Resources["BorderBrushCol"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 5, 6, 5)
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(listBorder, "PluginList");
        panel.Children.Add(listBorder);

        _status = new TextBlock
        {
            Foreground = muted,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(_status);
        DockPanel.SetDock(_status, Dock.Bottom);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var import = new Button { Content = "Import plugin…", MinWidth = 120, Margin = new Thickness(0, 0, 8, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(import, "PluginImport");
        import.Click += OnImportClick;
        var reload = new Button { Content = "Reload", MinWidth = 70, Margin = new Thickness(0, 0, 8, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(reload, "PluginReload");
        reload.Click += (_, _) => Run(() => PluginManager.Instance.Reload());
        var close = new Button { Content = "Close", MinWidth = 70, IsCancel = true };
        System.Windows.Automation.AutomationProperties.SetAutomationId(close, "BtnPluginsClose");
        close.Click += (_, _) => Close();
        buttons.Children.Add(import);
        buttons.Children.Add(reload);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        DockPanel.SetDock(buttons, Dock.Bottom);

        Content = panel;
        Debug.Log("PLUGINS-WINDOW: ctor end");
        PluginManager.Instance.Changed += RebuildList;
        Closed += (_, _) => PluginManager.Instance.Changed -= RebuildList;
        RebuildList();
    }

    private void RebuildList()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RebuildList));
            return;
        }
        _list.Children.Clear();
        var muted = (Brush)Application.Current.Resources["MutedBrush"];
        var warn = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D));
        var files = PluginManager.Instance.Files;
        if (files.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "No plugins installed. Import a .pepkg package or a plugin .dll.",
                Foreground = muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2)
            });
        }
        foreach (var f in files)
        {
            if (f.Error is not null || f.Plugins.Count == 0)
            {
                var errRow = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
                errRow.Children.Add(new TextBlock
                {
                    Text = $"{f.FileName}  - load error ({f.Source})",
                    FontWeight = FontWeights.SemiBold
                });
                errRow.Children.Add(new TextBlock { Text = f.Error ?? "no plugins in this file", Foreground = warn, TextWrapping = TextWrapping.Wrap });
                var delBtn = CreateDeleteButton(f);
                delBtn.HorizontalAlignment = HorizontalAlignment.Right;
                delBtn.Margin = new Thickness(0, 4, 0, 0);
                errRow.Children.Add(delBtn);
                _list.Children.Add(errRow);
                continue;
            }
            for (int i = 0; i < f.Plugins.Count; i++)
            {
                var plug = f.Plugins[i];
                var row = new Grid { Margin = new Thickness(2, 3, 2, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var chk = new CheckBox
                {
                    VerticalAlignment = VerticalAlignment.Top,
                    IsChecked = plug.Enabled,
                    IsEnabled = plug.Error is null,
                    Margin = new Thickness(0, 2, 8, 0)
                };
                System.Windows.Automation.AutomationProperties.SetAutomationId(chk, "PluginEnabled_" + plug.Id);
                var id = plug.Id;
                chk.Checked += (_, _) => Run(() => PluginManager.Instance.SetPluginEnabled(id, true));
                chk.Unchecked += (_, _) => Run(() => PluginManager.Instance.SetPluginEnabled(id, false));
                Grid.SetColumn(chk, 0);

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock
                {
                    Text = plug.Name + (string.IsNullOrWhiteSpace(plug.Version) ? "" : "  " + plug.Version),
                    FontWeight = FontWeights.SemiBold
                });
                if (!string.IsNullOrWhiteSpace(plug.Description))
                    info.Children.Add(new TextBlock { Text = plug.Description, Foreground = muted, TextWrapping = TextWrapping.Wrap });
                var parts = new System.Collections.Generic.List<string>();
                if (plug.Filters.Count > 0) parts.Add($"{plug.Filters.Count} filter(s)");
                if (plug.Masks.Count > 0) parts.Add($"{plug.Masks.Count} mask(s)");
                if (plug.Importers.Count > 0) parts.Add("import " + string.Join(" ", plug.Importers.SelectMany(x => x.D.Extensions)));
                if (plug.Exporters.Count > 0) parts.Add($"{plug.Exporters.Count} exporter(s)");
                if (plug.Commands.Count > 0) parts.Add($"{plug.Commands.Count} command(s)");
                if (plug.MetaProviders.Count > 0) parts.Add("metadata");
                if (plug.Presets.Count > 0) parts.Add($"{plug.Presets.Count} preset(s)");
                if (parts.Count > 0)
                    info.Children.Add(new TextBlock { Text = string.Join("  \u00B7  ", parts), Foreground = muted, FontSize = 11, TextWrapping = TextWrapping.Wrap });
                var api = plug.IsV1 ? "API v1 (legacy adapter)" : $"API v{plug.Api}";
                info.Children.Add(new TextBlock
                {
                    Text = $"{plug.Id}  \u00B7  {api}  \u00B7  {f.FileName}  \u00B7  {f.Source}  \u00B7  {f.Signature.Display}",
                    Foreground = muted,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 1, 0, 0),
                    ToolTip = "SHA-256: " + f.Sha256
                });
                if (plug.Permissions.Length > 0)
                    info.Children.Add(new TextBlock { Text = "Permissions: " + string.Join(", ", plug.Permissions), Foreground = muted, FontSize = 11 });
                if (plug.Error is not null)
                    info.Children.Add(new TextBlock { Text = plug.Error, Foreground = warn, TextWrapping = TextWrapping.Wrap });
                else if (!plug.Enabled && plug.DisabledReason is not null)
                    info.Children.Add(new TextBlock { Text = "Disabled automatically: " + plug.DisabledReason, Foreground = warn, TextWrapping = TextWrapping.Wrap });
                Grid.SetColumn(info, 1);

                row.Children.Add(chk);
                row.Children.Add(info);
                if (i == 0)
                {
                    var del = CreateDeleteButton(f);
                    del.VerticalAlignment = VerticalAlignment.Top;
                    Grid.SetColumn(del, 2);
                    row.Children.Add(del);
                }
                else
                {
                    row.Children.Add(new FrameworkElement { Width = 26 });
                }
                _list.Children.Add(row);
            }
        }
        var reg = PluginManager.Instance.Registry;
        var errs = files.Count(f => f.Error is not null) + files.Sum(f => f.Plugins.Count(p => p.Error is not null));
        int active = reg.Plugins.Count;
        _status.Text = (active > 0
                ? $"{active} plugin(s) active, {reg.Filters.Count(x => !x.IsCore)} filter(s)."
                : "No plugins active.")
            + (errs > 0 ? $" {errs} failed to load." : "");
    }

    private void Run(Action a)
    {
        try
        {
            a();
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private Button CreateDeleteButton(PluginFileEntry f)
    {
        var del = new Button
        {
            Content = "\uE74D",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Width = 26,
            Height = 22,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = f.Source == "imported" ? "Uninstall (delete the plugin files)" : "Disable (bundled plugins stay in the app folder)"
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(del, "PluginRemove_" + f.FileName);
        del.Click += (_, _) => Run(() => PluginManager.Instance.Remove(f.FileName));
        return del;
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Plugin",
            Filter = PluginManager.ImportDialogFilter,
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var insp = PluginPackage.Inspect(dlg.FileName);
            var trust = new TrustDialog(insp) { Owner = this };
            if (trust.ShowDialog() != true)
            {
                _status.Text = "Import cancelled.";
                return;
            }
            PluginManager.Instance.Install(insp);
            _status.Text = $"Installed {insp.Manifest.Name}.";
        }
        catch (Exception ex)
        {
            Debug.Log($"PLUGINS: import failed [{ex.GetType().Name}] {ex.Message}");
            _status.Text = $"Import failed: {ex.Message}";
        }
    }
}
