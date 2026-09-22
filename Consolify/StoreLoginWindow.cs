using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Consolify.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Consolify;

/// <summary>
/// A store's own sign-in page, in a window of its own.
///
/// The page is the store's, untouched: the user types their password into Epic's, GOG's or
/// Microsoft's form, with whatever two-factor step that involves, and nothing here ever sees
/// it. What this window adds is a probe, run after every navigation, that asks "has the page
/// arrived at the thing we came for" -- an authorization code in a redirect, a token in a URL
/// fragment -- and closes the window with it the moment it has.
///
/// Each store gets its own WebView2 profile folder, so the session survives between runs. That
/// is what makes a later silent refresh possible for a store whose tokens cannot be refreshed
/// otherwise: the same window is run hidden, and the store's remembered session carries it
/// straight through to the redirect.
///
/// It is driven from the sofa like anything else on the desktop: the stick is the mouse the
/// moment the launcher is not in front, and the keyboard toggle raises the on-screen keyboard,
/// which types into whatever window is focused. Cancel is a real button, because the pad's B
/// does not reach a window that is not the launcher.
/// </summary>
public class StoreLoginWindow : Window
{
    private readonly WebView2 _web = new();
    private readonly Func<CoreWebView2, Task<bool>> _probe;
    private readonly TaskCompletionSource<bool> _done = new();
    private readonly string _profileDir;
    private readonly string _startUrl;
    private bool _probing;

    private StoreLoginWindow(string title, string profileDir, string startUrl, Func<CoreWebView2, Task<bool>> probe, bool visible)
    {
        _profileDir = profileDir;
        _startUrl = startUrl;
        _probe = probe;

        Title = title;
        Width = 1100;
        Height = 820;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x14));

        var bar = new DockPanel { Height = 44, Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x20)) };
        var cancel = new Button
        {
            Content = "Cancel",
            Width = 110,
            Margin = new Thickness(8),
            Padding = new Thickness(12, 4, 12, 4),
        };
        cancel.Click += (_, _) => Close();
        DockPanel.SetDock(cancel, Dock.Right);
        bar.Children.Add(cancel);
        bar.Children.Add(new TextBlock
        {
            Text = $"{title} — the store's own page; Consolify never sees your password. Close this window to cancel.",
            Foreground = new SolidColorBrush(Color.FromRgb(0xc8, 0xc8, 0xd0)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 14, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_web);
        Content = root;

        if (!visible)
        {
            // Off screen and never activated: a silent refresh must not steal the foreground
            // from a game or flash a browser at the television.
            ShowActivated = false;
            ShowInTaskbar = false;
            Opacity = 0;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -32000;
            Top = -32000;
            Width = 400;
            Height = 300;
        }

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) => _done.TrySetResult(false);
    }

    /// <summary>
    /// Runs the window until the probe says it has what it came for (true), or the user closes
    /// it or the timeout passes (false). Hidden runs get a short timeout by default, because
    /// a hidden window waiting on a login form that nobody can see would wait forever.
    /// </summary>
    public static async Task<bool> RunAsync(Window? owner, string title, string profileDir, string startUrl,
        Func<CoreWebView2, Task<bool>> probe, bool visible = true, TimeSpan? timeout = null)
    {
        var w = new StoreLoginWindow(title, profileDir, startUrl, probe, visible);
        if (visible && owner is { IsVisible: true }) w.Owner = owner;
        w.Show();

        var wait = timeout ?? (visible ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(25));
        var finished = await Task.WhenAny(w._done.Task, Task.Delay(wait));
        var ok = finished == w._done.Task && w._done.Task.Result;
        try { if (w.IsLoaded || w.IsVisible) w.Close(); } catch { /* already closed */ }
        return ok;
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: _profileDir);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // A fragment-only change (OAuth's #access_token=…) raises SourceChanged and not
            // NavigationCompleted, so both are watched.
            core.NavigationCompleted += async (_, _) => await ProbeAsync(core);
            core.SourceChanged += async (_, _) => await ProbeAsync(core);
            core.Navigate(_startUrl);
        }
        catch (Exception ex)
        {
            Log.Info($"Sign-in window: {ex.Message}");
            _done.TrySetResult(false);
            Close();
        }
    }

    private async Task ProbeAsync(CoreWebView2 core)
    {
        if (_probing || _done.Task.IsCompleted) return;
        _probing = true;
        try
        {
            if (await _probe(core))
            {
                _done.TrySetResult(true);
                Close();
            }
        }
        catch (Exception ex) { Log.Info($"Sign-in probe: {ex.Message}"); }
        finally { _probing = false; }
    }

    /// <summary>The page's current URL, fragment included.</summary>
    public static string SourceOf(CoreWebView2 core) => core.Source ?? "";

    /// <summary>
    /// Runs a script and returns its string result unquoted. ExecuteScriptAsync hands back the
    /// value as JSON, and it does not wait on a promise, so anything that has to make a request
    /// does it with a synchronous XMLHttpRequest -- deprecated, still supported, and the only
    /// way to get an answer out in one call.
    /// </summary>
    public static async Task<string> EvalStringAsync(CoreWebView2 core, string script)
    {
        var json = await core.ExecuteScriptAsync(script);
        if (string.IsNullOrEmpty(json) || json == "null") return "";
        try { return System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? ""; }
        catch { return ""; }
    }
}
