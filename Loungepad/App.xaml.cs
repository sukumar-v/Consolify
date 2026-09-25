using System.Windows;
using Loungepad.Services;

namespace Loungepad;

public partial class App : Application
{
    private System.Threading.Mutex? _instanceMutex;
    /// <summary>Held, never owned: while this handle is open an old Consolify.exe finds its mutex
    /// taken and hands over to this copy instead of starting beside it.</summary>
    private System.Threading.Mutex? _oldNameMutex;
    private readonly List<(System.Threading.EventWaitHandle Signal, System.Threading.RegisteredWaitHandle Registration)> _wake = new();

    /// <summary>
    /// Set by a second copy to say "somebody tried to start me again". The launcher hides itself
    /// rather than closing when a game starts, so the second copy is nearly always a person
    /// double-clicking the exe because they cannot see the window that is already running.
    /// </summary>
    private const string WakeEventName = "Loungepad_ShowExisting";

    /// <summary>What a restart after an update passes on: how this copy was started.</summary>
    public static string[] RestartArgs { get; private set; } = Array.Empty<string>();
    /// <summary>The version this copy was updated from, for one "Updated to" toast.</summary>
    public static string? UpdatedFrom { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        RestartArgs = e.Args.Where(a => a == "--windowed").ToArray();
        UpdatedFrom = ArgValue(e.Args, "--updated-from");
        // Started by an update: the copy that installed it is on its way out and still holds the
        // mutex, so wait for it rather than handing over to it.
        var waitFor = int.TryParse(ArgValue(e.Args, "--wait-for"), out var oldPid) ? oldPid : (int?)null;
        if (waitFor is { } pid) UpdateService.WaitForPrevious(pid);

        _instanceMutex = new System.Threading.Mutex(true, "Loungepad_SingleInstance", out bool createdNew);
        if (!createdNew && waitFor is not null)
        {
            try { createdNew = _instanceMutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (System.Threading.AbandonedMutexException) { createdNew = true; }
        }
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

        // Before the data folder moves: the old copy's last save has to land in it first.
        var closedOld = ConsolifyMigration.CloseRunningCopy();
        Paths.EnsureCreated();
        Log.Info($"---- Loungepad {UpdateService.Format(UpdateService.Current)} starting ----");
        if (closedOld is not null) Log.Info(closedOld);
        if (UpdatedFrom is not null) Log.Info($"Updated from {UpdatedFrom}");

        UpdateService.FinishPreviousUpdate();
        // An update downloaded in the background goes in now, before there is a window to close.
        var settings = new SettingsStore();
        settings.Load();
        if (settings.Settings.AutoUpdate && UpdateService.InstallStagedAtStartup(RestartArgs))
        {
            Shutdown();
            return;
        }

        ThemeService.SyncBuiltIn();
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

        _oldNameMutex = new System.Threading.Mutex(false, ConsolifyMigration.OldMutexName);
        ListenForSecondLaunch(window, WakeEventName);
        // A desktop shortcut or pinned Start entry that still points at Consolify.exe starts the
        // old copy, which finds its mutex held and signals this -- so it shows Loungepad.
        ListenForSecondLaunch(window, ConsolifyMigration.OldWakeEventName);
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>
    /// Answer a second copy by showing this one. Unpark is what the launcher already uses to come
    /// back from a game, so a double-click behaves the same way whether the window was hidden
    /// behind a game or just behind something else.
    /// </summary>
    private void ListenForSecondLaunch(MainWindow window, string eventName)
    {
        try
        {
            var signal = new System.Threading.EventWaitHandle(
                false, System.Threading.EventResetMode.AutoReset, eventName);
            var registration = System.Threading.ThreadPool.RegisterWaitForSingleObject(
                signal,
                (_, _) => window.Dispatcher.BeginInvoke(() =>
                {
                    Log.Info("Another copy was started; showing this one");
                    window.Unpark();
                }),
                null, System.Threading.Timeout.Infinite, false);
            _wake.Add((signal, registration));
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
        foreach (var (signal, registration) in _wake)
        {
            registration.Unregister(null);
            signal.Dispose();
        }
        _oldNameMutex?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
