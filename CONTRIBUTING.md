# Contributing to Consolify

Thanks for taking an interest.

## Before starting something big

Consolify has a deliberately narrow shape: a controller-first launcher for a TV that also lets the
pad drive Windows itself. Bug fixes and small improvements are welcome as a straight pull request.
For anything larger — a new platform scanner, a new overlay, a whole settings section — please open
an issue first so the shape can be agreed before you spend a weekend on it.

One thing is permanently out of scope: **input injection at the Windows lock screen / Secure
Desktop**. That is an OS restriction rather than a bug, it is handled outside this app, and patches
adding `SendInput` or virtual-HID paths aimed at the lock screen will be declined.

## Building

Requirements: **.NET 8 SDK** and the **WebView2 Runtime** (preinstalled on Windows 11).

```bash
dotnet build Consolify.sln
```

```bash
dotnet run --project Consolify/Consolify.csproj -- --windowed
```

`--windowed` gives a 1280x720 non-topmost window instead of full-screen TV mode, which is much
easier to work against from a desk.

## Verifying a change

The UI is a plain web page and mocks up sample data when it is not hosted in WebView2, so serving
`Consolify/ui/` with any static server is the fastest loop for layout and UI logic.

It cannot see anything host-level. **If your change touches input, focus, the cursor or window
behaviour, run the real app** — the browser preview will happily show you a working page while the
WPF/WebView2 host misbehaves. Two things that catch people out there: the window sets
`ShowInTaskbar=false`, so `Process.MainWindowHandle` is 0 and you have to find it by enumerating
top-level windows for the pid; and the window must stay opaque, because `AllowsTransparency` makes
WPF host it as a layered window and the WebView2 then receives no mouse or wheel messages at all.

## Style

Match the surrounding code — there is no formatter to run.

Comments here explain *why*, not *what*, and they earn their place: most of the odd-looking code in
`Consolify/Interop/` and `Consolify/Services/` is odd for a documented reason, and that reason is
written next to it. Please keep that up, especially for Win32 behaviour that would otherwise look
like a mistake to the next reader.

## Commit messages

- Subject: one line, imperative or descriptive, no trailing period.
- Body: short bullet points, one idea each, wrapped at ~80 columns, continuation lines indented two
  spaces. Not paragraphs of prose.
- Say what changed and, where it is not obvious, why — a bullet can carry a root cause or a
  measurement.
- Skip the body entirely when the subject already says everything.
- No `Co-Authored-By:` trailers and no "Generated with" footers, for any tool or model.

```
Stop the scrolled grid from clipping through the All games header

- The scroller's -18px top margin more than ate the 16px section gap: its top
  edge, where overflow gets clipped, sat 3px above the header's label text
- Margin is now -8px, and the top edge fades over 26px when content is above
- The fade is off at scrollTop 0, so the first row keeps its focus-glow padding
```

## Licensing

Consolify is licensed under the [Apache License 2.0](LICENSE). By opening a pull request you agree
your contribution is licensed under those terms, per section 5 of the License — which includes it
being distributed in binary form as part of released builds.

The name "Consolify" and the Consolify logo are **not** covered by that grant; section 6 reserves
trademarks, and [NOTICE](NOTICE) spells it out. Forks are welcome, under their own name.
