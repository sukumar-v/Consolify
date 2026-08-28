# Couch Launcher

A full-screen, controller-first game launcher for the living-room TV, built for Windows 11.
The UI is the imported Claude Design project (`Couch Launcher.dc.html`) implemented as a real
HTML/CSS/JS app, hosted in **WebView2** inside a native **WPF** shell (.NET 8) that provides all
system-level features through Win32 P/Invoke.

## Build & run

Requirements: **.NET 8 SDK**, **WebView2 Runtime** (preinstalled on Windows 11).

```bash
dotnet build CouchLauncher.sln
```

Run `CouchLauncher\bin\Debug\net8.0-windows\CouchLauncher.exe`, or open `CouchLauncher.sln`
in Visual Studio 2022 and F5. Pass `--windowed` for a 1280×720 debug window (no always-on-top,
no focus guarding) instead of the full-screen TV mode.

The UI can also be previewed in a plain browser (it self-mocks sample data when not hosted in
WebView2): serve `CouchLauncher/ui/` with any static server and open `index.html`.

## Architecture

```
CouchLauncher/
  MainWindow.xaml(.cs)    Borderless, topmost, taskbar-less window; hosts WebView2 on the TV display
  UiBridge.cs             JSON message bridge: web UI <-> native services
  ui/                     The design-faithful UI (index.html, app.css, app.js), 1920x1080 stage
                          scaled to the display; served via WebView2 virtual host couch.ui
  Services/
    DisplayService.cs     Monitor enumeration + primary-display switching (ChangeDisplaySettingsEx)
    GameLaunchService.cs  Launch orchestration, process tracking, window repositioning, playtime
    LibraryScanner.cs     Steam (ACF/VDF + librarycache art), Epic (.item manifests), GOG (registry)
    GamepadService.cs     XInput polling: UI navigation events + gamepad-mouse (SendInput/SetCursorPos)
    VirtualKeyboardService.cs  TabTip.exe / osk.exe toggle
    StartupService.cs     HKCU Run key registration
    Storage.cs            JSON persistence in %APPDATA%\CouchLauncher (settings, library, log, covers)
  Interop/NativeMethods.cs   All P/Invoke declarations
```

## Feature notes

- **TV display targeting** — pick the TV in Settings → Display. The launcher window is placed
  there with `SetWindowPos` (pixel-exact, per-monitor DPI aware). Before a game launches the TV
  is made the Windows *primary* display (most games open on the primary), and the previous
  primary is restored when the game exits. As a fallback, for ~30 s after launch any visible
  game window that opened on another monitor is moved onto the TV.
- **Process tracking** — Steam/Epic launches go through their store URI, so the real game
  process is found by matching process image paths against the game's install directory
  (`QueryFullProcessImageName`); direct exe launches are tracked directly. Playtime, session
  count and last-played are recorded locally on exit.
- **Process tracking follows launcher chains** — sessions stay alive as long as *any* process
  from the game's install directory runs, so pre-launchers (REDprelauncher → REDlauncher →
  Cyberpunk2077.exe) don't end the session early. Per-game **launch arguments**
  (e.g. `--launcher-skip` for Cyberpunk) and a **direct-exe override** that bypasses the store
  launcher are available under Detail → Manage.
- **Library organisation** — Favorites (X on a game's detail page), automatic per-platform
  collections, and custom collections (Detail → Add to Collection → New collection, named via
  the touch keyboard). The Filter overlay (X in the library) filters by platform / favorites /
  installed state and sorts A–Z, Z–A, recently played, most played, or by size.
  LB/RB switches between Library, Collections and Settings.
- **Gamepad** (XInput, pad 0):
  - Launcher focused → D-pad/A/B/X/Y drive the UI exactly as the on-screen legend shows;
    the left stick moves the mouse cursor (hover focuses, so stick and D-pad stay in sync),
    right stick scrolls.
  - Launcher not focused, no game running (e.g. you tabbed to the desktop) → the configured
    buttons (defaults: A = left click, B = right click) send real mouse clicks.
  - Game running → the whole service idles so games with native controller support never see
    phantom input (opt back in with Settings → "Stay active while a game runs").
  - **View + Menu (Back + Start) together** minimizes the launcher to use the desktop, and
    restores it when pressed again.
  - Deadzone, sensitivity and the acceleration exponent (slow near center, fast at full
    deflection) are sliders in Settings.
  - The header shows a controller **battery gauge** — only for wireless pads that actually
    report a battery; wired controllers show nothing.
- **Virtual keyboard** — the Windows *touch* keyboard (TabTip, which has native gamepad
  support) via the ITipInvocation COM interface; osk.exe is only a last-resort fallback when
  TabTip doesn't exist. Hold Start (button + hold time configurable) to toggle; text inputs in
  the UI (collection names, launch arguments) raise it automatically.
- **Lock screen, wake & startup** — Settings → "Launch Couch Launcher at login" (HKCU Run key),
  plus a step-by-step in-app guide: Windows Hello PIN for couch-friendly sign-in (the sign-in
  screen's touch keyboard supports gamepad input), BIOS Wake-on-LAN, adapter magic-packet
  settings, letting the controller receiver wake the PC, Fast Startup, and automatic sign-in.
- **Focus guarding** — no taskbar button; if the desktop steals focus while no game runs, the
  launcher re-activates itself (Settings → "Keep launcher focused"). Alt-Tab still works.

Data lives in `%APPDATA%\CouchLauncher\` (`settings.json`, `library.json`, `covers\`,
`couchlauncher.log`). Delete `library.json` to force a clean rescan.

## Design → native mapping (flagged deviations)

The imported design is the source of truth for palette, type, spacing, focus motion, backdrop
behaviour and screen structure. Things that could not map 1:1 to local desktop reality:

1. **ACHIEVEMENTS stat** (detail screen) — achievements aren't available offline for
   Steam/Epic/GOG without authenticated web APIs. Replaced with **SESSIONS** (locally tracked).
2. **"Y Screenshots & saves"** legend on the detail screen — no portable local source for
   screenshots/cloud-save state. Replaced by the Manage menu (cover art, launch arguments,
   direct-exe override).
3. **Continue-row progress bars** — the design implies completion progress, which no platform
   exposes locally. The bar shows playtime relative to your most-played recent title.
4. **Game descriptions** ("A hand-drawn descent through…") — not available locally; the detail
   screen shows platform / launch route / install path instead.
5. **Genre filter and Metacritic sort** — no offline data source (store metadata needs
   authenticated web APIs), so the Filter overlay offers platform/favorites/installed filters
   and A–Z / Z–A / recency / playtime / size sorts instead.
6. **Not-installed titles** — the design dims games that are owned but not installed. Locally
   only installed games are discoverable (store catalogs need authenticated APIs), so
   "not installed" shows for entries whose files have been removed since scanning.
7. **Sample imagery** — the design's placeholder photos are replaced by real cover art
   (Steam caches both portrait covers and landscape banners; manual entries use user-picked
   images) with a procedural gradient-and-initials placeholder when art is unavailable
   (Epic/GOG have no local art cache).
8. **Fonts** — Manrope / IBM Plex Mono load from Google Fonts when online; otherwise the UI
   falls back to Segoe UI / Consolas.

## Explicitly out of scope

No input injection at the Windows lock screen / Secure Desktop (OS restriction; handled outside
this app). The in-app wake guide recommends automatic sign-in for a couch-only setup.
