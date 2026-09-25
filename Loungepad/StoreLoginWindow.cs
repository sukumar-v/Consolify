using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Loungepad.Interop;
using Loungepad.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Loungepad;

/// <summary>
/// What a browsing window has that a sign-in does not: Back and Forward, the on-screen keyboard
/// a press away, a pad that drives all three, and a hook for links the page cannot show.
/// </summary>
public sealed class BrowseOptions
{
    public string? Hint { get; init; }
    public string CloseLabel { get; init; } = "Done";
    /// <summary>Given every navigation to a scheme the page cannot show (nxm://, steam://…)
    /// instead of letting Windows open whatever handles it.</summary>
    public Action<string>? OnExternalUri { get; init; }
    /// <summary>Raises (or lowers) the on-screen keyboard; the bar gets a button for it.</summary>
    public Action? ShowKeyboard { get; init; }
    /// <summary>The pad button that already toggles the keyboard, drawn on that button as a hint.</summary>
    public string? KeyboardButton { get; init; }
    /// <summary>The toggle is a hold rather than a tap, which the hint says.</summary>
    public bool KeyboardHold { get; init; }
    /// <summary>Called with the window's pad handler while it is open and with null once it has
    /// closed. The handler is given the name of a face or shoulder button pressed while the
    /// launcher is not in front and says whether it took it.</summary>
    public Action<Func<string, bool>?>? RegisterPadHandler { get; init; }
    /// <summary>The family of the pad in hand when the window opens: xbox, playstation, switch,
    /// generic. The bar draws its button hints for it.</summary>
    public string PadFamily { get; init; } = "xbox";
    /// <summary>Called with a watcher while the window is open and with null once it has closed;
    /// the watcher is told the family whenever the pad in hand changes.</summary>
    public Action<Action<string>?>? WatchPadFamily { get; init; }
}

/// <summary>
/// A store's own sign-in page, or nexusmods.com, in a window of its own.
///
/// The page is the site's, untouched: the user types their password into Epic's, GOG's or
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
/// which types into whatever window is focused. There is no title bar. Its close button was the
/// one control in the window that Windows draws rather than we do, and a stick-click on it
/// behaved differently from a click on Done, in a way that left the pad doing nothing until a
/// real mouse closed the window. Everything the window offers is a button in its own bar.
///
/// The bar is itself a WebView: a page built here with the launcher's glyphs.js inlined into it,
/// so the pad buttons on Back, Forward and Keyboard are the very same drawings as every legend in
/// the launcher -- a green A, a red B, a Cross and a Circle on a DualSense -- and follow the pad
/// in hand the same way. Drawing them again in WPF would have been a second set to keep in step.
/// </summary>
public class StoreLoginWindow : Window
{
    private readonly WebView2 _web = new();
    private readonly WebView2? _bar;
    private readonly Func<CoreWebView2, Task<bool>> _probe;
    private readonly TaskCompletionSource<bool> _done = new();
    private readonly string _profileDir;
    private readonly string _startUrl;
    private readonly BrowseOptions? _options;
    private IntPtr _hwnd;
    private bool _probing;

    private const int BarHeight = 60;

    private StoreLoginWindow(string title, string profileDir, string startUrl, Func<CoreWebView2, Task<bool>> probe, bool visible,
        BrowseOptions? options)
    {
        _profileDir = profileDir;
        _startUrl = startUrl;
        _probe = probe;
        _options = options;

        Title = title;
        Width = 1100;
        Height = 820;
        MinWidth = 640;
        MinHeight = 480;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x14));

        var root = new DockPanel();
        if (options is not null)
        {
            _bar = new WebView2 { Height = BarHeight, DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x16, 0x16, 0x1b) };
            DockPanel.SetDock(_bar, Dock.Top);
            root.Children.Add(_bar);
        }
        else
        {
            // A sign-in keeps the plain bar: one sentence and Cancel.
            var bar = new DockPanel { Height = 48, Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x20)) };
            var cancel = new Button
            {
                Content = "Cancel", Width = 110, Margin = new Thickness(8), Padding = new Thickness(12, 4, 12, 4), Focusable = false,
            };
            cancel.Click += (_, _) => Close();
            DockPanel.SetDock(cancel, Dock.Right);
            bar.Children.Add(cancel);
            bar.Children.Add(new TextBlock
            {
                Text = $"{title} — the store's own page; Loungepad never sees your password. Cancel closes this window.",
                Foreground = new SolidColorBrush(Color.FromRgb(0xc8, 0xc8, 0xd0)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 14, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
        }
        root.Children.Add(_web);
        // A hairline round the whole thing: with no title bar the window has no edge of its own
        // against the launcher's dark backdrop.
        Content = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0x3c, 0x3c, 0x48)), BorderThickness = new Thickness(1), Child = root };

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
        SourceInitialized += (_, _) => _hwnd = new WindowInteropHelper(this).Handle;
        Loaded += async (_, _) =>
        {
            _options?.RegisterPadHandler?.Invoke(HandlePadButton);
            _options?.WatchPadFamily?.Invoke(family => PostToBar(new { family }));
            await InitAsync();
        };
        Closed += (_, _) =>
        {
            _options?.RegisterPadHandler?.Invoke(null);
            _options?.WatchPadFamily?.Invoke(null);
            _done.TrySetResult(false);
        };
    }

    /// <summary>
    /// Runs the window until the probe says it has what it came for (true), or the user closes
    /// it or the timeout passes (false). Hidden runs get a short timeout by default, because
    /// a hidden window waiting on a login form that nobody can see would wait forever.
    /// </summary>
    /// <param name="browse">Back, Forward, the keyboard and the pad, for a window that is a
    /// browser rather than a sign-in. Null keeps the sign-in shape: one Cancel button.</param>
    public static async Task<bool> RunAsync(Window? owner, string title, string profileDir, string startUrl,
        Func<CoreWebView2, Task<bool>> probe, bool visible = true, TimeSpan? timeout = null, BrowseOptions? browse = null)
    {
        var w = new StoreLoginWindow(title, profileDir, startUrl, probe, visible, browse);
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
            if (_bar is not null) await InitBarAsync(env);

            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // A fragment-only change (OAuth's #access_token=…) raises SourceChanged and not
            // NavigationCompleted, so both are watched.
            core.NavigationCompleted += async (_, _) => await ProbeAsync(core);
            core.SourceChanged += async (_, _) => await ProbeAsync(core);
            core.HistoryChanged += (_, _) => PostToBar(new { canBack = core.CanGoBack, canForward = core.CanGoForward });
            if (_options is { } options)
            {
                if (options.OnExternalUri is { } external)
                    core.LaunchingExternalUriScheme += (_, e) =>
                    {
                        e.Cancel = true;
                        try { external(e.Uri); }
                        catch (Exception ex) { Log.Info($"External link handler: {ex.Message}"); }
                    };
                // A link the site would open in a new tab stays in this window: there is no tab
                // bar here, and a second window is not something a gamepad can get back from.
                core.NewWindowRequested += (_, e) =>
                {
                    e.Handled = true;
                    if (!string.IsNullOrEmpty(e.Uri)) core.Navigate(e.Uri);
                };
            }
            core.Navigate(_startUrl);
        }
        catch (Exception ex)
        {
            Log.Info($"Sign-in window: {ex.Message}");
            _done.TrySetResult(false);
            Close();
        }
    }

    // ---- the bar ----

    private async Task InitBarAsync(CoreWebView2Environment env)
    {
        await _bar!.EnsureCoreWebView2Async(env);
        var core = _bar.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.WebMessageReceived += (_, e) =>
        {
            string? cmd = null;
            try { cmd = JsonDocument.Parse(e.WebMessageAsJson).RootElement.GetProperty("cmd").GetString(); } catch { }
            switch (cmd)
            {
                case "back": GoBack(); break;
                case "forward": GoForward(); break;
                case "keyboard": _options?.ShowKeyboard?.Invoke(); break;
                case "close": Close(); break;
            }
        };
        core.NavigateToString(BarHtml(_options!));
    }

    /// <summary>The bar's page: the launcher's own drawings, inlined, over a few buttons.</summary>
    private static string BarHtml(BrowseOptions o)
    {
        string glyphs;
        try { glyphs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ui", "glyphs.js")); }
        catch (Exception ex)
        {
            Log.Info($"Browse bar: glyphs.js could not be read ({ex.Message}); drawing plain chips");
            glyphs = "function btnIcon(b){return '<span class=\"chip\">'+b+'</span>';} function iconSvg(){return '';}";
        }
        // The drawings colour themselves with the launcher's tokens (a shoulder pill is filled
        // with var(--ink)); without the tokens a pill is black on black. The :root block of
        // app.css is the one source of them, so it is copied in rather than restated.
        var tokens = ":root { --ink: #F6F5F3; --accent: #F0A253; --bg: #08080A; --card: #101012; --edge: #FFFFFF; --danger: #E97A6C; }";
        try
        {
            var css = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ui", "app.css"));
            var m = System.Text.RegularExpressions.Regex.Match(css, @":root\s*\{[^}]*\}");
            if (m.Success) tokens = m.Value;
        }
        catch (Exception ex) { Log.Info($"Browse bar: app.css could not be read ({ex.Message}); using built-in tokens"); }
        var kbButton = string.IsNullOrEmpty(o.KeyboardButton) || o.KeyboardButton == "Off" ? "" : o.KeyboardButton;
        var setup = JsonSerializer.Serialize(new
        {
            family = o.PadFamily,
            hint = o.Hint ?? "",
            close = o.CloseLabel,
            keyboard = o.ShowKeyboard is not null,
            kbButton,
            kbHold = o.KeyboardHold,
        });
        return $$"""
            <!doctype html><html><head><meta charset="utf-8">
            <link rel="preconnect" href="https://fonts.googleapis.com"><link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
            <link href="https://fonts.googleapis.com/css2?family=Manrope:wght@400;500;600;700&display=swap" rel="stylesheet">
            <style>
              {{tokens}}
              html, body { margin: 0; height: 100%; background: #16161b; color: #ecebf1; font-family: Manrope, "Segoe UI", system-ui, sans-serif; overflow: hidden; user-select: none; }
              .bar { display: flex; align-items: center; gap: 10px; height: 100%; padding: 0 12px; box-sizing: border-box; }
              /* Darker than the bar, like the launcher's own page under its legends: the glyphs
                 are drawn with dark discs and pills (#26262C) that vanish on a mid-grey button. */
              .btn { display: inline-flex; align-items: center; gap: 10px; height: 42px; padding: 0 14px; border-radius: 12px;
                     border: 1px solid rgba(255,255,255,0.09); background: #0d0d10; color: #ecebf1;
                     font: 600 15px Manrope, "Segoe UI", sans-serif; cursor: pointer; white-space: nowrap; }
              .btn:hover { background: #1c1c22; }
              .btn.off { opacity: 0.4; cursor: default; }
              .btn.off:hover { background: #0d0d10; }
              .btn-slot { display: inline-flex; align-items: center; gap: 4px; }
              .btn-icon { height: 28px; width: auto; display: block; }
              .chip { display: inline-block; padding: 2px 7px; border-radius: 6px; background: #3c3c48; font-size: 12px; font-weight: 700; }
              .arrow, .ov-icon { width: 22px; height: 22px; stroke: currentColor; fill: none; stroke-width: 2.2; stroke-linecap: round; stroke-linejoin: round; }
              .hold { font-size: 12px; font-weight: 500; color: #a9a8b3; margin-left: -4px; }
              .hint { flex: 1; min-width: 0; font-size: 14px; color: #a9a8b3; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
              .done { background: #f0a253; border-color: transparent; color: #1a1208; }
              .done:hover { background: #f6b46f; }
            </style></head><body>
            <div class="bar">
              <button class="btn" id="back" data-cmd="back" title="Back"><span class="btn-slot" data-btn="B"></span><svg class="arrow" viewBox="0 0 24 24"><path d="M15 5 8 12l7 7"/></svg></button>
              <button class="btn" id="forward" data-cmd="forward" title="Forward"><svg class="arrow" viewBox="0 0 24 24"><path d="m9 5 7 7-7 7"/></svg><span class="btn-slot" data-btn="X"></span></button>
              <button class="btn" id="keyboard" data-cmd="keyboard" hidden><span class="btn-slot" id="kbSlot"></span><span class="hold" id="kbHold" hidden>hold</span><span id="kbIcon"></span><span>Keyboard</span></button>
              <div class="hint" id="hint"></div>
              <button class="btn done" id="close" data-cmd="close"><span class="btn-slot" data-btn="Y"></span><span id="closeLabel">Done</span></button>
            </div>
            <script>{{glyphs}}</script>
            <script>
              const setup = {{setup}};
              let family = setup.family || "xbox";
              const $ = id => document.getElementById(id);
              $("hint").textContent = setup.hint;
              $("closeLabel").textContent = setup.close;
              if (setup.keyboard) {
                $("keyboard").hidden = false;
                if (setup.kbButton) { $("kbSlot").dataset.btn = setup.kbButton; $("kbHold").hidden = !setup.kbHold; }
                else $("kbSlot").remove();
                $("kbIcon").innerHTML = iconSvg("keyboard");
              }
              function paint() { document.querySelectorAll("[data-btn]").forEach(el => { el.innerHTML = btnIcon(el.dataset.btn, family); }); }
              paint();
              document.querySelectorAll("[data-cmd]").forEach(el => el.addEventListener("click", () => {
                if (el.classList.contains("off")) return;
                window.chrome.webview.postMessage({ cmd: el.dataset.cmd });
              }));
              window.chrome.webview.addEventListener("message", e => {
                const m = e.data || {};
                if (m.family) { family = m.family; paint(); }
                if ("canBack" in m) $("back").classList.toggle("off", !m.canBack);
                if ("canForward" in m) $("forward").classList.toggle("off", !m.canForward);
              });
            </script>
            </body></html>
            """;
    }

    private void PostToBar(object message)
    {
        var core = _bar?.CoreWebView2;
        if (core is null) return;
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }
        catch (Exception ex) { Log.Info($"Browse bar: {ex.Message}"); }
    }

    private void GoBack() { var c = _web.CoreWebView2; if (c is { CanGoBack: true }) c.GoBack(); }
    private void GoForward() { var c = _web.CoreWebView2; if (c is { CanGoForward: true }) c.GoForward(); }

    /// <summary>
    /// A pad button pressed while the launcher is not in front, on the pad's own thread. Only
    /// while this window is the one in front: the same pad drives whatever else is on the
    /// desktop. B is Back, X is Forward and Y is Done, as the bar says. Not LB and RB: RB is
    /// the keyboard toggle by default, and the keyboard is how anything gets typed into the
    /// site.
    /// </summary>
    private bool HandlePadButton(string name)
    {
        if (_hwnd == IntPtr.Zero || NativeMethods.GetForegroundWindow() != _hwnd) return false;
        switch (name)
        {
            case "B": Dispatcher.BeginInvoke(GoBack); return true;
            case "X": Dispatcher.BeginInvoke(GoForward); return true;
            case "Y": Dispatcher.BeginInvoke(Close); return true;
            default: return false;
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
