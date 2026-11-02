using System;
using System.Windows;

namespace ImgViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            Debug.Log("UIEX: " + ex.Exception);
            ex.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Debug.Log("DOMEX: " + ex.ExceptionObject);
        base.OnStartup(e);
        if (Environment.GetEnvironmentVariable("PE_OPEN_PLUGINS") == "1")
        {
            void OpenPluginsDialog()
            {
                if (MainWindow is MainWindow mw)
                {
                    Debug.Log("PLUGINS-WINDOW: opening (test hook)");
                    new PluginsWindow { Owner = mw, Topmost = true }.Show();
                }
            }
            if (MainWindow is MainWindow mw) OpenPluginsDialog();
            else Dispatcher.BeginInvoke(new Action(OpenPluginsDialog), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }
}