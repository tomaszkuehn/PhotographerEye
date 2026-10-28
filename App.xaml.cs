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
    }
}