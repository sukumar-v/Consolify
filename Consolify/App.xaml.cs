using System.Windows;
using Consolify.Services;

namespace Consolify;

public partial class App : Application
{
    private System.Threading.Mutex? _instanceMutex;
    private System.Threading.EventWaitHandle? _wakeSignal;
    private System.Threading.RegisteredWaitHandle? _wakeRegistration;

    /// <summary>
    /// Set by a second copy to say "somebody tried to start me again". The launcher hides itself
    /// rather than closing when a game starts, so the second copy is nearly always a person
    /// double-clicking the exe because they cannot see the window that is already running.
    /// </summary>
    private const string WakeEventName = "Consolify_ShowExisting";

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new System.Threading.Mutex(true, "Consolify_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // Hand the running copy the foreground and go quietly. It used to just Shutdown(),
            // which meant launching the exe a second time did NOTHING AT ALL: no window, no
            // error, and -- because this runs before the log is even opened -- not so much as a
            // line to say a start had been attempted. Every symptom then got blamed on whatever
            // the already-running copy happened to be showing.
            try
            {
                if (System.Threading.EventWaitHandle.TryOpenExisting(WakeEventName, out var running))
                    using (running) running.Set();
            }
            catch { /* the other copy is on its way out; nothing to wake */ }

            Shutdown();
            return;
        }

        Paths.EnsureCreated();
        ThemeService.SyncBuiltIn();
        Log.Info("---- Consolify starting ----");
        StartupService.MigrateOldEntry();

        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Log.Info($"Unhandled exception: {ex.ExceptionObject}");
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Info($"Dispatcher exception: {ex.Exception}");
            ex.Handled = true;
        };

        base.OnStartup(e);

        var window = new MainWindow(e.Args.Contains("--windowed"));
        MainWindow = window;
        window.Show();

        ListenForSecondLaunch(window);
    }

    /// <summary>
    /// Answer a second copy by showing this one. Unpark is what the launcher already uses to come
    /// back from a game, so a double-click behaves the same way whether the window was hidden
    /// behind a game or just behind something else.
    /// </summary>
    private void ListenForSecondLaunch(MainWindow window)
    {
        try
        {
            _wakeSignal = new System.Threading.EventWaitHandle(
                false, System.Threading.EventResetMode.AutoReset, WakeEventName);
            _wakeRegistration = System.Threading.ThreadPool.RegisterWaitForSingleObject(
                _wakeSignal,
                (_, _) => window.Dispatcher.BeginInvoke(() =>
                {
                    Log.Info("Another copy was started; showing this one");
                    window.Unpark();
                }),
                null, System.Threading.Timeout.Infinite, false);
        }
        catch (Exception ex)
        {
            // Worth nothing more than a line: the launcher works, it just will not answer a
            // second double-click.
            Log.Info($"Could not listen for a second launch: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _wakeRegistration?.Unregister(null);
        _wakeSignal?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
