using System;
using System.Windows;
using ImgViewer.Services;

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

        // The privacy policy must be accepted before the app opens (first start, or a new policy version).
        if (!PrivacyPolicy.IsAccepted(SettingsStore.Load()))
        {
            var mode = ShutdownMode;
            ShutdownMode = ShutdownMode.OnExplicitShutdown; // closing the policy window must not end the app by itself
            bool accepted = new PrivacyWindow(mustAccept: true).ShowDialog() == true;
            Debug.Log("PRIVACY: " + (accepted ? "accepted " : "declined ") + PrivacyPolicy.Version);
            if (!accepted)
            {
                Shutdown();
                return;
            }
            SettingsStore.Save(PrivacyPolicy.Accept(SettingsStore.Load(), DateTime.Now));
            ShutdownMode = mode;
        }

        var main = new MainWindow();
        MainWindow = main;
        main.Show();

        if (Environment.GetEnvironmentVariable("PE_OPEN_PLUGINS") == "1")
        {
            Debug.Log("PLUGINS-WINDOW: opening (test hook)");
            new PluginsWindow { Owner = main, Topmost = true }.Show();
        }
    }
}
