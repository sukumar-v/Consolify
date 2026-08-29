using System.Windows;
using CouchLauncher.Services;

namespace CouchLauncher;

public partial class App : Application
{
    private System.Threading.Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new System.Threading.Mutex(true, "CouchLauncher_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        Paths.EnsureCreated();
        Log.Info("---- Couch Launcher starting ----");

        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Log.Info($"Unhandled exception: {ex.ExceptionObject}");
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Info($"Dispatcher exception: {ex.Exception}");
            ex.Handled = true;
        };

        base.OnStartup(e);

        var window = new MainWindow(e.Args.Contains("--windowed"), e.Args.Contains("--opaque"));
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
