<p align="center">
  <img src="assets/consolify-logo.jpg" alt="Consolify" width="160">
</p>

<h1 align="center">Consolify</h1>

<p align="center">
  <b>Turn a Windows 11 PC into a console.</b><br>
  Your games, your desktop, your whole machine — driven from the couch with nothing in your hands
  but a controller.
</p>

<p align="center">
  <a href="https://github.com/sukumar-v/Consolify/releases/latest">
    <img alt="Download Consolify for Windows"
         src="https://img.shields.io/badge/Download%20for%20Windows-F0A253?style=for-the-badge&logo=windows&logoColor=08080A">
  </a>
</p>

<p align="center">
  <a href="https://github.com/sukumar-v/Consolify/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/sukumar-v/Consolify?label=latest&color=F0A253"></a>
  <img alt="Total downloads" src="https://img.shields.io/github/downloads/sukumar-v/Consolify/total?color=1F1F24">
  <a href="LICENSE"><img alt="License: Apache 2.0" src="https://img.shields.io/badge/license-Apache--2.0-1F1F24"></a>
  <img alt="Platform: Windows 11" src="https://img.shields.io/badge/platform-Windows%2011-1F1F24">
</p>

![Consolify's library screen](assets/screenshot-library.jpg)

## Install

**[Download the latest release](https://github.com/sukumar-v/Consolify/releases/latest)**, unzip it
anywhere, and run `Consolify.exe`.

Nothing else to install: the .NET runtime is bundled. Windows 11 already has the one thing that is
not — the **Edge WebView2 Runtime** — and on Windows 10 the app will tell you where to get it.
Keep `Consolify.exe` and the `ui` folder together; the UI is loaded off disk at startup.

The build is not code-signed yet, so Windows SmartScreen will warn the first time you run it:
choose **More info → Run anyway**.

On first run Consolify scans Steam, Epic, GOG and the Xbox app for installed games, then puts
itself full-screen on your primary display. Point it at the TV in **Settings → Display**, and
turn on **Launch on startup** there if you want it to come up with Windows. Settings, library
and cover art live in `%APPDATA%\Consolify`; uninstalling is deleting the folder you unzipped.

`Consolify.exe --windowed` opens a 1280×720 window instead, which is easier to poke at from a desk.

## No keyboard. No mouse.

Most couch launchers get you as far as starting a game, then leave you stranded the moment you
need to do anything else — dismiss an update prompt, log into a store, close a window that opened
on the wrong screen. You end up walking to the desk for the mouse anyway.

Consolify is built so that never happens. **The controller is a complete input device**, not just
a menu remote:

- **The left stick is the mouse.** Deadzone, sensitivity and an acceleration curve — slow near
  the centre for precision, fast at full deflection to cross a 4K screen — are all sliders in
  Settings. The right stick scrolls. A and B are left and right click, live across the whole
  desktop the moment the launcher isn't in front.
- **The keyboard comes to you.** Hold Start to raise the Windows touch keyboard, which takes
  gamepad input directly, so you can type a search, a password or a message without getting up.
  Text fields inside the launcher raise it on their own.
- **The Power Wheel runs Windows.** One double-tap of View + Menu, from anywhere — including
  mid-game — and you can switch to any open window (it gets dragged onto the TV with you), fire
  a saved shortcut, summon the keyboard, re-centre a lost pointer, close the window in front of
  you, or blank the TV and park the pad until you press a button again.
- **The pointer knows when to disappear.** Touch the D-pad and the cursor hides and stops
  stealing focus; nudge the stick or a real mouse and it comes straight back. Optionally
  system-wide, so it stays hidden out on the desktop too.

The result is a machine you genuinely never have to walk over to. Games are the reason you sit
down; everything else stops being a reason to stand up.

![Driving the Windows touch keyboard with a gamepad](assets/screenshot-keyboard.png)

![The Power Wheel overlay](assets/screenshot-power-wheel.png)

## Your library, found automatically

Consolify scans **Steam**, **Epic**, **GOG** and the **Xbox app** from their local install data —
no accounts, no API keys, nothing phoned anywhere. It picks up real cover art, tracks playtime and
sessions locally, and rescans every time it starts, so a game installed yesterday is simply there.
Anything the scanners drag in that isn't a game (benchmarks, wallpaper tools, redistributables)
gets hidden with one button.

The TV is treated as a first-class display: the launcher places itself there pixel-exactly, makes
it the Windows primary before a game starts so the game opens on the right screen, and puts your
old primary back when you quit.

![Consolify's settings screen](assets/screenshot-settings.png)


## Build & run

Requirements: **.NET 8 SDK**, **WebView2 Runtime** (preinstalled on Windows 11).

```bash
dotnet build Consolify.sln
```

Run `Consolify\bin\Debug\net8.0-windows\Consolify.exe`, or open `Consolify.sln`
in Visual Studio 2022 and F5. Pass `--windowed` for a 1280×720 debug window (no always-on-top,
no focus guarding) instead of the full-screen TV mode.

The UI can also be previewed in a plain browser (it self-mocks sample data when not hosted in
WebView2): serve `Consolify/ui/` with any static server and open `index.html`.

To build the zip that goes on a release:

```powershell
.\tools\package.ps1            # or -Version 1.1.0 to stamp the tag you are about to push
```

It publishes self-contained and single-file into `artifacts\`, checks that `ui\` came along,
and writes `dist\Consolify-v<version>-win-x64.zip` with its SHA-256.

## Architecture

A native WPF shell (.NET 8) hosting the UI as an HTML/CSS/JS app in WebView2, bridged by JSON
messages, with every system-level feature behind Win32 P/Invoke.

```
Consolify/
  MainWindow.xaml(.cs)    Borderless, topmost, taskbar-less window; hosts WebView2 on the TV display
  UiBridge.cs             JSON message bridge: web UI <-> native services
  ui/                     The design-faithful UI (index.html, app.css, app.js), 1920x1080 stage
                          scaled to the display; served via WebView2 virtual host consolify.ui
  Services/
    DisplayService.cs     Monitor enumeration + primary-display switching (ChangeDisplaySettingsEx)
    GameLaunchService.cs  Launch orchestration, process tracking, window repositioning, playtime
    LibraryScanner.cs     Steam (ACF/VDF + librarycache art), Epic (.item manifests),
                          GOG (registry), Xbox (MicrosoftGame.config + AppModel repository)
    GamepadService.cs     XInput polling: UI navigation events + gamepad-mouse (SendInput/SetCursorPos)
    VirtualKeyboardService.cs  Touch keyboard (TabTip) via ITipInvocation COM
    CursorService.cs      Optional system-wide pointer hiding while the D-pad drives
    StartupService.cs     HKCU Run key registration
    Storage.cs            JSON persistence in %APPDATA%\Consolify (settings, library, log, covers)
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
- **Game options menu** — **Y** on any game opens a context menu: View game (the full detail
  page), favorite, add to collection, change cover art, and **Hide**. Hidden entries drop out of
  the library entirely and collect under the **Hidden** collection, which is how you get rid of
  non-games that the platform scanners pick up (benchmarks, wallpaper tools, redistributables).
- **Library organisation** — Favorites, automatic per-platform collections (Steam, Epic, GOG,
  Xbox, Manual), custom collections, and Hidden. The **X** overlay has two dropdowns: a
  categorised **multi-select Filter** (platform / status / favorites) and a single-select
  **Sort** (A–Z, Z–A, recently played, most played, largest, smallest); **Y** resets both.
  LB/RB switches between Library, Collections and Settings.
- **Adding games** — a **+ Add game** tile sits at the end of the library grid, and the same
  action lives under Settings → Library. File dialogs drop always-on-top while open, otherwise
  they open *behind* the full-screen launcher and appear to do nothing.
- **Pointer / D-pad input modes** — the mouse pointer hides and hover stops stealing focus as
  soon as the D-pad drives; moving the stick or a real mouse brings it straight back. Inside the
  launcher this is free; Settings → "Hide pointer system-wide" extends it to the rest of Windows
  by swapping the system cursors (restored on exit, on crash and on process exit).
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
- **Virtual keyboard** — the Windows *touch* keyboard (TabTip) via the ITipInvocation COM
  interface; osk.exe is only a last-resort fallback when TabTip doesn't exist. Hold Start
  (button + hold time configurable) to toggle; text inputs in the UI (collection names, launch
  arguments) raise it automatically. **The keyboard only accepts gamepad input on its "Gamepad"
  layout**, which Windows exposes solely through the keyboard's own settings flyout — there is
  no registry value or API to select it, so the app cannot switch it for you. It is a one-time
  choice that persists: step 01 of the in-app setup guide walks through it.
- **Lock screen, wake & startup** — Settings → "Launch Consolify at login" (HKCU Run key),
  plus a step-by-step in-app guide: the touch keyboard's Gamepad layout, a Windows Hello PIN for
  couch-friendly sign-in (the sign-in screen's touch keyboard supports gamepad input), letting the
  controller receiver wake the PC from sleep, and auto-starting the launcher.
- **Focus guarding** — no taskbar button; if the desktop steals focus while no game runs, the
  launcher re-activates itself (Settings → "Keep launcher focused"). Alt-Tab still works.

Data lives in `%APPDATA%\Consolify\` (`settings.json`, `library.json`, `covers\`,
`consolify.log`). Delete `library.json` to force a clean rescan.

The app was previously called **Couch Launcher**. On first run it copies `settings.json`,
`library.json` and any missing cover art out of `%APPDATA%\CouchLauncher\`, and moves an existing
HKCU Run entry across. It copies rather than moves, and leaves the old folder alone — delete that
yourself once you are satisfied the library came over.

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
   (Steam caches both portrait covers and landscape banners; Xbox supplies square store logos;
   manual entries use user-picked images) with a procedural gradient-and-initials placeholder
   when art is unavailable (Epic/GOG have no local art cache).
9. **Touch keyboard "Gamepad" layout** — the keyboard ignores controller input on its default
   layout, but Windows offers no registry value or API to select the Gamepad layout; it is
   only selectable from the keyboard's own settings flyout. Documented as a one-time manual
   step (guide step 01) rather than automated with an undocumented registry write.
8. **Fonts** — Manrope / IBM Plex Mono load from Google Fonts when online; otherwise the UI
   falls back to Segoe UI / Consolas.

## Explicitly out of scope

No input injection at the Windows lock screen / Secure Desktop (OS restriction; handled outside
this app). The in-app wake guide recommends automatic sign-in for a couch-only setup.

## Contributing

Bug reports and small fixes are welcome; open an issue first for anything larger. Build steps,
the conventions this repo follows, and what is deliberately out of scope are in
[CONTRIBUTING.md](CONTRIBUTING.md).

## License

Licensed under the [Apache License 2.0](LICENSE). See [NOTICE](NOTICE) for attribution and
third-party components.

"Consolify" and the Consolify logo are **not** covered by that grant — section 6 of the license
reserves trademarks. Fork it freely; give the fork its own name.
