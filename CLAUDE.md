# Consolify — working notes

## Commit messages

- **Never add a `Co-Authored-By:` trailer.** Not for Claude, not for any model. No
  "Generated with" footers either.
- Subject line: one line, imperative or descriptive, no trailing period.
- Body: **short bullet points only.** Never paragraphs of prose.
- One idea per bullet. Wrap at ~80 columns; indent continuation lines two spaces.
- Say what changed and, where it is not obvious, why — a bullet may carry a root
  cause or a measurement, but keep it to a sentence or two.
- Skip the body entirely when the subject already says everything.

```
Stop the scrolled grid from clipping through the All games header

- The scroller's -18px top margin more than ate the 16px section gap: its top
  edge, where overflow gets clipped, sat 3px above the header's label text
- Margin is now -8px, and the top edge fades over 26px when content is above
- The fade is off at scrollTop 0, so the first row keeps its focus-glow padding
```

## Verifying changes

- The `ui-preview` server renders the same HTML but **not** the WPF/WebView2
  hosting, so it cannot see host-level input, focus, cursor or window bugs. It is
  fine for layout and UI logic only.
- It serves `Consolify/`, so the app is at `/ui/index.html` and a theme's files are
  reachable at `/themes/<id>/…` — which is what makes a theme previewable at all.
  Push one in by hand: a `{type:"themes", themes:[…]}` message with those URLs,
  then `S.settings.theme = "<id>"` and `applyTheme()`.
- The preview never fires `requestAnimationFrame`, so **CSS transitions freeze at
  their start value** and `scrollTo({behavior:"smooth"})` does nothing. Neither is a
  bug in the page. To check an animated end state, disable transitions
  (`* { transition: none !important }`) and measure; to check scroll-follow, record
  the `scrollTo` calls.
- The published app takes no keyboard input: it is gamepad-driven, nothing in the
  page is focused, and `keydown` never fires. Win32 `SetFocus` on the WebView2
  render window is not enough — WebView2 needs the host's `MoveFocus`. Drive
  navigation in the preview instead (same `handleInput` path the pad uses) and use
  the published app for how it renders.
- For anything touching input, focus, the cursor or window behaviour, run the
  published app: `publish\Consolify.exe --windowed` gives a 1280x720
  non-topmost window. Drive it with real `SendInput` and screen captures.
- The WPF window sets `ShowInTaskbar=false`, so `Process.MainWindowHandle` is 0 —
  find the window by enumerating top-level windows for the pid.
- **Never send clicks** while testing: a stray click once launched a real game.
  Only kill launcher instances started within the session.

## Architecture

- WPF (.NET 8) shell hosting the design's HTML/CSS/JS in WebView2, bridged by
  JSON messages (`UiBridge`): UI → host `{cmd}`, host → UI `{type}`.
- The launcher window must stay **opaque**. `AllowsTransparency` makes WPF host it
  as a layered window and the WebView2 then receives no mouse or wheel messages at
  all. Overlay menus paint their own dim over a screen capture instead.

## Art and metadata

- Four shapes, and they are not interchangeable. Putting the wrong one in a slot is what
  made tiles look like they had the wrong game's art:
  - `CoverFile` portrait (600x900) — portrait grid tiles
  - `BannerFile` ~16:9 (616x353) — landscape tiles, the continue row, the now-playing card
  - `HeroFile` ~3:1 (1920x620) — full-screen backdrops and the detail page
  - `LogoFile` transparent wordmark — unused so far; there for a theme that wants the
    title as art
- `bannerUrl` falls back to the cover, never to the hero. `heroUrl` falls back to
  anything. A 3:1 hero centre-cropped into a 16:9 tile throws away 43% of the width and
  what is left is background.
- `MetadataService` fills the rest in after the scan, from Steam only: the CDN
  (`cdn.cloudflare.steamstatic.com/steam/apps/<appid>/…`) for art and the undocumented
  `store.steampowered.com/api/appdetails` for the description, developer, genres, release
  date, controller support and the Metacritic score. No key, no account, and keyed by app
  id so there is no title matching and so no chance of attaching the wrong game's art.
  Roughly 200 requests per 5 minutes per IP, hence the 1.5s gap between store calls.
- Every metadata field is cosmetic and every failure is swallowed. Offline, the launcher
  keeps whatever the scanner copied out of Steam's local cache (which is half-size: the
  cached "library_600x900" is really 300x450).
- Art the user picks by hand is written as `custom_<id>.<ext>`. That prefix is the only
  thing keeping it — nothing else ever writes that name, so neither a rescan nor an
  enrich can overwrite the file or point the game away from it.
- Non-Steam games have only a title to match on, so both keyed providers go through
  `TitleMatch`, which accepts nothing short of exact-after-normalising (accents, `&`,
  apostrophes and punctuation folded; one trailing edition suffix discounted). "Portal"
  does not match "Portal 2" and never should — a wrong cover is worse than a missing
  one, because nothing about it looks wrong.
- Epic's manifest folder is not a games list: it also holds Unreal Engine, Quixel Bridge
  and Fab plugins, and the engine entries have a launch executable, so they pass the
  "has an exe" test. `IsEpicGame` reads Epic's own `AppCategories` instead — a game
  carries "games", an engine carries "engines".
- Credentials for IGDB and SteamGridDB live in settings.json in plain text. A rejection
  is latched per pass, so bad keys produce one log line rather than one per game, and
  games skipped because of it are left unstamped so corrected keys retry at once.
- Metadata is tiered so that almost nothing reaches a keyed source. Steam entries go by
  app id; non-Steam entries are first resolved against Steam's keyless
  `storesearch` endpoint (most games sold on Epic/GOG/Xbox are also on Steam); only
  what is genuinely not on Steam reaches the proxy in `proxy/`, which holds the IGDB
  and SteamGridDB credentials. A user's own keys, if set, take priority over the proxy.
- Newer Steam apps have stopped publishing to `cdn/steam/apps/<id>/<name>`. Forza
  Horizon 6 serves only `library_hero.jpg` there and 404s for the capsule and header.
  `appdetails` always names a working `header_image` under `store_item_assets`, so
  facts are fetched *before* art and that URL is the tile's last resort.
- The proxy's title matching is a courtesy; the launcher re-checks every response
  against `TitleMatch` itself. A proxy that is wrong, stale or replaced still cannot put
  another game's art on a tile.
- `JsonElement.TryGetInt32` and friends **throw** on a JSON `null` rather than returning
  false — they return false only for a number that will not fit. A `"criticScore": null`,
  which is most games, took out that game's whole enrichment silently. Read numbers
  through `JsonNum.Int/Long/Double`, which check `ValueKind` first.
- IGDB carries several entries with byte-identical titles: two named exactly "DOOM"
  (1993 and 2016), more than one named "Fortnite". `TitleMatch` cannot separate those, so
  the tie is broken on popularity (`follows`, then `total_rating_count`) and entries with
  a `version_parent` are dropped. Without it, Fortnite came back as the delisted Chinese
  version, developer "Tencent Games".
- The worker's cache key carries a `SCHEMA` constant. Bump it whenever a fetcher's shape
  or picking rules change, or the old answers are served for another 30 days.
- Tile art is never cover-cropped. It has the game's name burnt into it, close to the
  edges: a 2.14:1 header.jpg in a 16:9 box lost 18% of its width and REANIMAL lost the
  end of its own name. Polish's tile is cut to 1.75:1 (Steam's capsule exactly) and uses
  `contain`; the default theme decides per image in `artFit`, and only for landscape
  boxes — portrait box art is drawn to be cropped and still is.
- The hero is fetched at 2x (`library_hero_2x.jpg`, 3840x1240) where it exists. `.bd` is
  inset -80px, so on a 1920x1080 stage it covers 2080x1240 — from the 1x hero that was a
  2x upscale showing 54% of the width, which is what "zoomed in and blurry" was.
- Each Steam art entry carries its own filename suffix, so a name on disk says which
  source it came from. That is what lets an install pick up a better source later while
  still skipping downloads for art it already has; one shared name per slot forced a
  choice between the two. Renaming a suffix orphans the old files in the covers dir,
  which is a cache and harmless.
- The shared service is asked **first**, Steam second as the fallback. What makes that safe
  is that both are asked by Steam app id when there is one: IGDB via
  `external_games.category = 1`, SteamGridDB via `/games/steam/<appid>`. An id lookup
  cannot answer with a different game, so proxy-first does not reintroduce the title
  matching that produced the wrong Fortnite. The title is still sent as the fallback for
  games the upstream does not index under that id.
- "Fallback" means fills gaps, not overwrites. Steam skips any art slot the service
  filled, and skips the text fields entirely when the service answered — keyed on whether
  the service answered, not on whether a field is empty, because a value left over from a
  previous run is also non-empty and testing emptiness would make stale data
  uncorrectable. Controller support is always Steam's: IGDB has no equivalent.

## Settings, themes and the keyboard

- A bundled theme is installed once and then kept up to date by the `version` in its theme.json
  (`ThemeService.SyncBuiltIn`). It used to skip any folder that already existed, which froze a
  bundled theme at whatever shipped the day it was first installed -- every later fix to Polish
  landed in the app and was never seen, because the copy being loaded was the old one in
  %APPDATA%. Bump the version whenever a bundled theme changes or nobody gets the change. The
  folder is copied to `theme-backups` first, so an edited theme is recoverable rather than gone.
- `CopySettings` has to list every setting. `HideCursorSystemWide` was wired through the UI, the
  host and the CSS but never copied, so the toggle moved on screen and was gone again on the next
  state push -- a whole feature that silently did nothing.
- Restore-defaults keeps `TvDeviceName`. Which screen is the television is a fact about the room,
  not a preference, and clearing it moves the launcher off the screen the user is looking at.
- The keyboard toggle is evaluated BEFORE the `serviceActive` gate, like the menu combo and the
  screenshot key: inside a focused game the rest of the pad is silent, and the keyboard is most
  useful exactly there. `KeyboardInGame` decides whether it may fire; closing a keyboard that is
  already up is always allowed, or an opted-out player could strand one on screen.
- In Press mode the toggle button is consumed on the press (`toggleFired` is set there), so it
  never also reaches the UI. That is why Start is a bad choice for it -- Start is the Menu button
  -- and why the Settings row warns about exactly that. Hold leaves the tap free, which is the
  point of having both.

## Screens and the switcher

- Library is the only top-level screen. Collections became a filter category (`F.collections`,
  a set of collection ids) and Settings is reached with the Menu button from the library —
  `TAB_DEFS` holds one entry and there is no section cycling, so LB/RB are free.
  Collections themselves still exist and are still made from the game menu.
- A collection can be deleted while it is still being filtered on, so stale ids are pruned
  when state arrives. Left alone the filter matches nothing and the library looks empty for
  no visible reason.
- `renderMenu` paints its highlight onto `listEl.closest("[data-focus-scope]")`. An overlay
  without that attribute gets no highlight at all — which is what was wrong with the power
  wheel's submenus. It also only paints rows that are focusable, and nothing inside a hidden
  overlay is, so an overlay must be made `.active` *before* it is rendered.
- `repaintFocus` must have a branch for every overlay that can own input, ordered as in
  `handleInput`. A missing branch repaints the screen underneath and leaves the visible menu
  unhighlighted.
- The window switcher follows the Alt+Tab rules, and the one that matters is DWM's cloak flag:
  a suspended Store app stays "visible" in the old sense, which is why ApplicationFrameHost and
  TextInputHost appeared as if they were programs. Do **not** blocklist ApplicationFrameHost —
  it owns the frame window of every Store app, so blocking it hides Settings and Windows
  Security too. Cloaking already separates the ghosts from the real ones.
- A minimized window reports a 160x28 rect wherever Windows parks it. Any "too small to be
  real" test has to ask `IsIconic` first, or it eats exactly the windows the switcher is for.
- Thumbnails come from `PrintWindow` with `PW_RENDERFULLCONTENT`, which is the flag that makes
  it work for DirectComposition and UWP surfaces; without it browsers and Store apps come back
  blank. They are captured off the UI thread and pushed one at a time *after* the list, because
  PrintWindow waits on the target's message loop and the switcher is often opened precisely
  because something is stuck.
- An overlay must be made `.active` **before** it is rendered, everywhere — not just the power
  wheel. `openFilter`, `openGameMenu`, `openCollect`, `openManage` and the confirm all rendered
  first, so the first row was never highlighted until something moved.
- Hover handlers must not rebuild the list they are on. `renderSettingsNav` replaces every tab
  node, and a node destroyed between mousedown and mouseup never raises a click — which is why
  the settings categories could not be clicked at all while the option rows could. Set focus and
  `paintNav()`; the rows already did exactly that via a guard.
- `TAB_DEFS` is empty. The top bars still render for the clock and title count, they just have
  no tabs in them.
- A minimized window cannot be photographed: PrintWindow answers true and hands back an empty
  bitmap. `WindowService` keeps the last picture of each window (`_thumbs`, pruned of dead
  handles on every list) and falls back to the window's icon, flagged as `IsIcon` so the UI
  draws it inside the box rather than cover-cropping a 32px square into a smear. Alt+Tab shows
  a real picture because DWM keeps the last composed frame; there is no public way to read that.
- Tile art is flush to the tile; only the corners are rounded. Insetting it to keep its corners
  clear of the radius did stop the clipping and left a surface-coloured mat round every tile, so
  each read as a picture pasted onto a rounded card. A radius only ever takes the four corners --
  the sides were being lost to a crop, and the fix for that is the box being the shape of the
  picture, not a border around it.
- Art that still does not fill its box gets a bed, not black bars: a blurred, dimmed copy of the
  same picture behind the fitted one. A uniform grid cannot give each tile the shape of its own
  art, and the two shapes in play are 1.75:1 and 2.14:1, so a few tiles will always have a strip
  left over. Polish builds the bed in its template (`.tv-bed`); everything going through
  `applyArt` gets `.art-bed` + `.art-top` built on demand, and ONLY when the fit is `contain` --
  so the great majority of tiles carry no extra layer and no blur.
- Both layers are children. A child always paints above its parent's background and never below
  it, so the bed cannot be the element's own background; and a `filter` on the element would blur
  the sharp layer along with the bed. The bed is also scaled ~1.15, because a blur feathers its
  own edges and a feathered edge inside a rounded box reads as a halo.
- `applyArt` takes an optional `fit`. Scenery must always `cover` — a full-bleed backdrop has no
  edges of its own to protect, and `artFit` letterboxed the detail page's 3:1 hero inside its
  16:9 box, which is where the black bars came from. Only tiles are worth fitting.
- Full-width art is cut to the art, not to a number. `applyArt` measures what loaded and writes
  `--art-aspect` on the element; the detail hero and Polish's backdrop are `aspect-ratio:
  var(--art-aspect, 3.1)` with `max-height: 100%`. A fixed height only ever suited one source:
  62% of a 16:9 stage is 2.87:1, so Steam's 3.1:1 hero lost the sides and IGDB's 16:9 artwork lost
  42% of its height.
- Polish floors the library backdrop at `--bd-fill` (72%) on top of that aspect. At its own shape
  a 3.1:1 hero is 57% of a 16:9 screen and stopped dead there -- a hard horizontal edge across the
  middle with the bed below it. The floor carries it past the title and into the dock, where the
  fade has room to happen; the price is about 20% of the width. It is one number: down towards 58%
  keeps every pixel and brings the band back, up towards 90% takes more screen and less picture.
  Art that is already tall enough is untouched either way.
- A mask has to fade to nothing at the element's own edge. Polish's stopped at 99% of a band that
  was only 57% tall, so the art was still clearly visible where it ended -- which is what read as
  cut rather than dissolved.
- The rest of the screen is the same picture, blurred and scaled past the edges --
  `#backdrop::before`, fed by `--bd-image`, which `setBackdrop` writes alongside the art. It is
  the trick tvOS and Plex both use, and it is the only way to have the art fill a screen and stay
  sharp: a 3.1:1 hero stretched over 16:9 shows the middle 57% of itself and nothing else. The
  bed is blurred, so its resolution never matters. Polish's bottom scrim had to come off full
  opacity to let it through -- it was written when there was nothing behind it to show.
- `BackdropFile` is 16:9 key art and is what `backdropUrl` prefers for anything filling a whole
  screen, falling back to the 3.1:1 hero. Only IGDB publishes art of that shape; Steam's
  `page_bg_generated_v6b` is 16:9 but 28-63 KB of auto-generated blur. IGDB's artwork used to be
  written into the Hero slot, which is what made a backdrop's shape unpredictable -- the same slot
  held 3.1:1 for Steam games and 16:9 for everything else.
- `artFit` must measure the CONTENT box, not `clientWidth/clientHeight`. `background-origin:
  content-box` -- what holds tile art clear of the rounded corners -- makes `cover` size against
  the content box, so measuring the padding box read the continue row as 1.79:1 when it was 1.86:1;
  a 1.75:1 capsule then looked like a perfect fit and lost a strip off each side. That was the
  "the sides are cut off, not just the corners" report, and rounding was not the cause of it.
- Every landscape box is 1.75:1, which is Steam's capsule exactly: `.cont-art` 300x172,
  `.playing-art` 366 wide against the card's 210, Polish's tile `--tile-w / 1.745`. The grid tile
  is 174x261, which is 2:3 -- box art's own shape -- and nine of them plus eight 24px gaps is the
  same 1760 run eight 199px ones made. `GRID_COLS` and `.grid-item` have to move together.
- The critic score is labelled with `criticSource`, never with "Metacritic" by default. Steam's
  appdetails carries a real Metacritic score and says so; IGDB's `aggregated_rating` is its own
  average and is not Metacritic, so a fixed label would be wrong about half the time.
- The score and the PEGI age sit together above the stats hairline, each a panel with a caption.
  A bare "82" is a number with no unit -- it could be a rank or a percentage -- and a bare "16"
  reads like one too.
- IGDB moved age ratings from numeric enums (`category`/`rating`) to references
  (`organization`/`rating_category`), and APIcalypse fails the WHOLE query with a 400 for one
  unknown field -- so guessing wrong costs the description and the score as well as the rating.
  Both the worker and `IgdbClient` try the shapes in order, step down only on a 400, and remember
  what worked. The last shape asks for no age fields at all, so there is always a query that runs.
- A game carries ratings from several boards at once and their enums overlap: ESRB's rating 4 and
  PEGI's rating 4 are different things. Identify the board before reading the number.
- Only an ISO release date is reformatted, and its three numbers are read out of the string rather
  than through `Date`. Parsing "2024-02-02T00:00:00Z" reads UTC and printing reads back local, so
  west of Greenwich every release came out a day early. Steam's own "2 Feb, 2024", "Q1 2024" and
  bare years are passed through: reformatting them means guessing a day.
- `.detail-main` is anchored with `margin-top: auto`. A game with no metadata has a much shorter
  column than one with everything, and centring put the Play button in a different place on each.
- The detail page uses `LogoFile` when there is one, falling back to the text title. Both stay in
  the DOM; `renderDetail` toggles `hidden`.
- `revealOffset` snaps to the ends for the scroller's first and last focusable. Nothing focusable
  above the first row means the space above it is not slack, it is that row's section heading --
  clearing REVEAL_MARGIN for the focus glow scrolled the heading off the moment you walked back up,
  which is why the first category in every Settings tab kept vanishing.
- A on a settings category reads the category off the element, not off `settingsTab`. Hovering a
  category highlights it without selecting it, so A opened whichever one had last been activated:
  the highlight said Keyboard and Controller's rows appeared, which reads as A doing nothing.
