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

- Five shapes, and they are not interchangeable. Putting the wrong one in a slot is what
  made tiles look like they had the wrong game's art:
  - `CoverFile` portrait 2:3 (600x900) — portrait grid tiles
  - `BannerFile` 1.75:1 (616x353, Steam's capsule) — landscape tiles, the continue row,
    the now-playing card
  - `HeroFile` ~3.1:1 (1920x620, 3840x1240 at 2x) — full-screen backdrops and the detail page
  - `BackdropFile` 16:9 key art — only for a game with no hero at all; see the note below
    about why it is no longer preferred
  - `LogoFile` transparent wordmark, 1.2:1 or wider — the title as art on the detail page
- `bannerUrl` falls back to the cover, never to the hero. `backdropUrls` is a list —
  hero, then backdrop, then the tile — and never reaches the portrait cover. A 3:1 hero
  centre-cropped into a 16:9 tile throws away 43% of the width and what is left is
  background; a 2:3 cover hung across a screen is a column of box art.
- `MetadataService` fills the rest in after the scan, from Steam only: the CDN
  (`cdn.cloudflare.steamstatic.com/steam/apps/<appid>/…`) for art and the undocumented
  `store.steampowered.com/api/appdetails` for the description, developer, genres, release
  date, controller support and the Metacritic score. No key, no account, and keyed by app
  id so there is no title matching and so no chance of attaching the wrong game's art.
  Roughly 200 requests per 5 minutes per IP, hence the 1.5s gap between store calls.
- Every metadata field is cosmetic and every failure is swallowed. Offline, the launcher
  keeps whatever the scanner copied out of Steam's local cache (which is half-size: the
  cached "library_600x900" is really 300x450).
- Art the user picks by hand is written as `custom_<id>[_<slot>].<ext>`. That prefix is the only
  thing keeping it — nothing else ever writes that name, so neither a rescan nor an
  enrich can overwrite the file or point the game away from it. The cover has no suffix (it was
  the only pickable slot once and the name is on disk in everyone's install); the tile is
  `_tile`. The guard in `Assign` covers every slot, and `CustomSlots` seeds `filled` so a picked
  slot is not even downloaded for. `HasFetchedArt` has to accept a custom name too, or a game
  with hand-picked tile art reads as "no art" and rejoins the queue on every single start.
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
- **Bumping `SCHEMA` does nothing on its own — the worker has to be deployed.** PEGI was added
  to `proxy/src/worker.js` and never shipped, so the live service kept answering without the
  field at all and no amount of launcher-side work could show a rating. `curl
  <endpoint>/v1/facts?title=<something-nobody-has-asked-for>` and look for `X-Cache: MISS`:
  a fresh answer that is still missing a field means the deploy, not the cache.
- `MetadataService.FetchVersion` is the other half of that. A build that learns a new field
  has to be able to go back and ask for it, and the 14-day freshness window would otherwise
  hold a library on the old answers. Bump it whenever a field is added or a picture starts
  being picked differently; everything stamped older is fetched again on the next pass.
- SteamGridDB's landscape grids are **920x430 and 460x215, and both are 2.14:1** — it has no
  1.75:1 shape at all. Steam's `capsule_616x353` is the only source of one, so the capsule is
  asked for *before* the service (`PreferSteamCapsuleAsync`) and only that one picture is. Ask
  the service first for the tile and every game in the library grows a blurred bed, because
  every tile is suddenly the one shape the box is not cut to. A game with no capsule --
  REANIMAL, Forza Horizon 6, Shotgun Cop Man all 404 for it -- still falls through to the
  service, whose 920x430 is twice the header's resolution in the same shape.
- Both keyed clients now ask by Steam app id when there is one, as the proxy always has: IGDB
  via `external_games.category = 1`, SteamGridDB via `/games/steam/<appid>`. They used to
  search by title regardless, so a user who supplied their own credentials was getting the
  weaker path — the opposite of what supplying them is for.
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
- **An art file's name has to carry the SOURCE as well as the slot.** It used to be slot plus
  extension — `_hdtile` + `.jpg` — so Steam's 616x353 capsule and SteamGridDB's 920x430 grid,
  which is also served as .jpg, resolved to the same path. The service overwrote the capsule in
  place; every later pass found a file *named* like a capsule, skipped the download because it
  already existed, and pointed the tile at 2.14:1 art. Twenty tiles with a blurred mat under
  them and not one line of code that said why. Names are now `<id>_st_<slot>.<ext>` and
  `<id>_sv_<slot>.<ext>`, so two sources can hold the same slot on disk and which one a game
  uses is decided by the code that runs, not by whoever wrote last. Renaming a suffix orphans
  the old files, which is a cache and harmless — but bump `FetchVersion` with it.
- Every download is shape-checked against the slot it is going into (`Fits` / `Bounds`), and a
  picture of the wrong shape is deleted rather than assigned. IGDB's `artworks` are whatever
  somebody uploaded: taking the first one for the backdrop put a 1080x1080 square behind
  DREDGE's screen and an 810x1080 portrait behind Hollow Knight's, and because the backdrop
  follows the highlight, walking along a row changed the shape of the picture every few tiles.
  The bounds are wide on purpose — they reject the wrong KIND of picture, not one a few percent
  off. Art we cannot decode (SteamGridDB serves .webp) is accepted rather than discarded.
- **`Fits` can tell that an artwork is 16:9 and cannot tell that it is any good.** Persona 3's
  first IGDB artwork passed the gate at 1920x1080 and is a blue diagonal, two floating leaves and
  31 KB of JPEG — against 873 KB of key art in Steam's hero. So `backdropUrls` puts the **hero
  first** and the 16:9 backdrop second, and the enrich only fills the Backdrop slot when nothing
  filled Hero. `library_hero` is curated and is the picture the store itself shows; the
  band-over-a-blurred-bed treatment handles its 3.1:1 perfectly well. A side effect worth having:
  every backdrop in the library is now the same shape, so the picture no longer changes proportion
  as the highlight moves.
- `setBackdrop` walks a LIST of candidates and drops to the next on a load failure. A name in the
  library can outlive the file it points at — a download rejected for being the wrong shape is
  deleted, and only a *successful* download ever replaces a name — so one stale entry used to
  cost the whole backdrop, permanently, through any number of refreshes. `Unassign` now clears the
  field when the file it names is the one being rejected, and the UI falls through regardless.
- `library_hero_2x.jpg` 404s for a lot of older apps (Celeste, Hollow Knight, TUNIC, Aseprite,
  Henry Stickmin all only have the 1x). That is what SteamGridDB's hero is for, and it is why the
  order is Steam 2x → service → Steam 1x rather than just "Steam".
- `heroUrl` stops at the tile. It must never fall back to the portrait cover: 2:3 art hung
  across a screen at its own aspect is a tall column of box art, and a flat colour is the
  better answer for a game with no wide art at all.
- **One priority order for art, best source first, and the first to fill a slot keeps it.** Steam's
  fixed-size library assets lead (`SteamPreferred`: capsule 616x353, library_600x900_2x,
  library_hero_2x), then the service for what Steam has nothing for and for every non-Steam game,
  then Steam's leftovers (`SteamFallback`: header.jpg, the 1x hero, logo.png). Facts still come
  from the service first — this is only about pictures.
- **`StoreRemoteAsync` has to honour `filled`.** It did not, while the Steam CDN loop did, so
  "the capsule gets first refusal" was true for exactly the two lines until the service ran and
  overwrote it. A rule that only some callers obey is not a rule; put the gate in the one place
  every caller goes through.
- **Three separate places key off the art naming scheme, and a rename has to move all of them:**
  `MetadataService` (writes the names), `MetadataService.HasFetchedArt`, and
  `LibraryStore.KeepBest`. KeepBest went on testing for the retired `_hd` after the rename, so it
  stopped recognising fetched art and every scan reverted the whole library to Steam's local
  half-size cache — which is what "the artwork keeps changing" was on restart, as opposed to the
  overwrite bug that caused it within a pass.
- `MergeScanned` has to list every fetched field, exactly like `CopySettings`, and it fails the
  same silent way. `BackdropFile`, `PegiRating` and `MetadataVersion` were all missing: the
  backdrop was dropped and refetched on every scan, a PEGI rating would have been wiped the
  moment the proxy started sending one, and the version stamp resetting to 0 made every game look
  stale so the whole library refetched on every single start.
- `setBackdrop` keys on the game id **and the resolved URL**. On the id alone it skipped the
  repaint whenever the highlight had not moved — including the push right after a metadata pass
  swapped the art out from under it, which left the element pointing at a file that no longer
  existed and the screen black until you moved.
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
- **A height floor on the backdrop cannot buy height without buying width.** `.bd` has `left` and
  `right` pinned and an `aspect-ratio`, so `min-height: 72%` grew the *element* to 1605px inside a
  1280px screen and `#backdrop`'s `overflow: hidden` threw away everything past the right edge — a
  fifth of the picture, off one side only, and on a 4K panel the fifth that survived was being
  upscaled 1.25x to get there. Both halves of "cropped and blurry", from one number. `--bd-fill`
  now defaults to 0 and `max-width: 100%` means raising it costs the bottom of the picture rather
  than the sides, which is the cheaper edge: key art puts its logo across the middle and the
  element is anchored `top`. The sharp layer hangs at its own height and the blurred bed carries
  the rest of the screen, which is what the bed is for.
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
- The score and the PEGI age sit together on the right, level with the stats hairline: the corner
  of a game's box, which is where an age mark has been printed for thirty years. Each is a panel
  with a caption -- a bare "82" is a number with no unit and a bare "16" reads like one too -- and
  the PEGI group is `row-reverse` so the tablet, not a line of small type, is what sits in the
  corner. Positioned absolutely out of the column: the stats are anchored to the bottom of the
  page and the ratings are not always there, so in flow a game with a score would put its Play
  button somewhere different from a game without one.
- The wordmark gets a pool of shade of its own (`.detail-titleblock::before`, only when the logo
  is showing). A logo is whatever colour its designer chose and the key art behind it is the same
  palette -- Cyberpunk's yellow on yellow, and the reason a screen-wide scrim is not the answer:
  anything heavy enough to separate them flattens the picture everywhere else.
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

## Running two copies

- `App.OnStartup` takes a `Consolify_SingleInstance` mutex, and a second copy used to just
  `Shutdown()`. That happens **before** `Log.Info("---- Consolify starting ----")`, so launching
  the exe while a copy was already running did nothing at all: no window, no error, and not one
  line in the log to say a start had been attempted. Whatever the running copy happened to be
  showing then got blamed on the build. A second copy now signals `Consolify_ShowExisting` and the
  running one calls `Unpark()` and logs that it did.
- So: **never leave a test instance running.** The user launches
  `publish\Consolify.exe` by hand and from the HKCU Run key, and a copy left behind after a test
  silently swallows every launch of theirs. Kill it in the same turn it is finished with.
- A gap in the log where a start should be is the signature of this. If the user reports something
  and the log has no `---- Consolify starting ----` for it, they were looking at an instance
  somebody else started.

## PEGI

- **Steam carries the age boards itself** — `appdetails` returns a `ratings` object with one entry
  per board (`pegi`, `esrb`, `usk`, `cero`, …), keyed by app id, keyless, no title matching. That
  makes it a strictly better source than IGDB for anything on Steam, and it is one word in the
  filter list: `ratings`. The filter list is exhaustive, so leaving the word out drops the whole
  block silently, which is what "PEGI never shows up" was for a long time.
- The rating arrives as a **string**, and some boards put letters in it ("m", "r18", "z"), so only
  PEGI's own five numbers are accepted. A game with no `pegi` entry has no European rating —
  most indies do not. 4 of 20 in the test library have one, and that is correct, not a failure.
- The proxy's IGDB age extraction is **separately still broken**: `/v1/facts` returns `pegi: null`
  for everything, including Cyberpunk 2077 and The Witcher 3, on a verified fresh `X-Cache: MISS`.
  The likely cause is `AGE_SHAPES` stepping down to the last entry (which asks for no age fields
  at all) and `ageShape` then sticking for the life of the isolate — and a 400 from an unrelated
  field, such as `external_games.category`, would be misattributed to the age fields and trigger
  exactly that. It only matters now for games that are not on Steam at all.

## Fitted art and the bed

- **A bed must be stretched to the box, not centre-cropped.** `background-size: cover` showed a
  zoomed MIDDLE of the picture behind a strip that sits directly above and below that same
  picture's own top and bottom edges: two pieces of one image that do not line up, dimmed to 55%.
  The eye reads that as what it looks like — a bar. `background-size: 100% 100%` matches the sharp
  layer's horizontal scale, so the strip is the art's own colours carrying on past its edge and
  stops registering as a border at all. Both `.art-bed` and Polish's `.tv-bed`.
- The small `transform: scale(1.08)` is only there to keep the blur's feathered edge outside the
  rounded box. It used to be 1.14, which was fighting the misalignment rather than the feather.
- **Do not solve leftover strips by cropping.** A 2.14:1 tile cropped to 1.75:1 loses 18% of its
  width: REANIMAL and Forza Horizon 6 survive that (centred logos) and Shotgun Cop Man does not —
  its title runs vertically down the left edge and is gone. Edge-detail heuristics do not separate
  the two cases reliably (measured: 0.79 vs 0.48 and 0.60 of centre std-dev, n=3). The bed is the
  answer; "Change tile art" in Manage is the escape hatch for a tile somebody dislikes.

## The detail page's rating marks

- **Nothing is ever cropped off a tile.** `contain` shows 100% of the picture; the leftover strip
  is the bed. A crop was measured and rejected — see the note under "Fitted art" — so a title
  running down an edge, like Shotgun Cop Man's, is always whole.
- One badge construction for both marks: a value over the name of whoever issued it. The captions
  beside them are gone — "AGE RATING / 16 AND OVER" next to a mark already reading PEGI 16 is the
  same fact three times, and "OUT OF 100" is a footnote. What the score was missing is what the
  age mark always had: the issuing body's name under the number, where it cannot be read as part
  of it.
- **ESRB is preferred over PEGI** because Steam lists it for more games: in this library six
  carry an ESRB rating and four a PEGI one, and every PEGI game also had ESRB. The wordmark in the
  badge says which board it is, so falling back cannot be mistaken for the other. Drawn in the
  page's own materials, never the boards' actual artwork.
- Stripping "critics" off a source name needs `\s+critics?$`, not `\s*critics?$` — without the
  required space it also eats the "critic" inside "Metacritic", and every Metacritic score came
  out labelled META.
- Content descriptors ("Blood and Gore", "Mild Lyrics") come from the same board as the rating and
  sit under the description. ESRB writes them as a sentence, so the last one arrives as "and
  Strong Language" and the leading "and " has to come off.
- **Steam's `logo.png` is a wordmark, 1.78:1 or wider, every time. SteamGridDB's logos are
  whatever somebody drew** — 0.92:1 for DREDGE, 1.11:1 for Henry Stickmin, 7.34:1 for ULTRAKILL.
  The detail page hangs this where the title goes, so a square one lands as a small blob in the
  corner of a box cut for a wordmark. Steam's now gets first refusal, and the Logo slot's `Bounds`
  reject anything squarer than 1.2:1 — falling back to the text title, which is better than a blob.

## Filters

- `F.hidden` is a separate VIEW, not "show hidden as well": the reason to ask is to look over what
  you put away and take something back out, and mixing them into 200 tiles is not that. The row
  carries the count, because an empty hidden view and a broken filter look identical.

## The Steam account

- Uninstalled Steam games come from the Web API's `GetOwnedGames`, which needs a key: the shared
  proxy's `STEAM_API_KEY` (`/v1/owned`) for profiles whose game details are public, the user's own
  key (`SteamApiKey`) for private ones. **There is no keyless route any more.** The community
  `games?tab=all` page and the older `?xml=1` feed both redirect to a login for every profile,
  public or not (checked Sept 2026), and nothing local lists the library with names: the
  per-account `userdata/<id>/config/librarycache/*.json` are achievement pointers, `licensecache`
  is encrypted, and `appcache/librarycache` is shared between every account that has logged in on
  the PC, so it mixes libraries.
- The account is read from `config/loginusers.vdf`. `MostRecent` is missing on some installs, so
  the newest `Timestamp` is the fallback.
- The answer is cached in `steam-owned.json`, keyed by SteamID, and held for six hours; a manual
  Rescan forces it. A failed fetch serves the cache, so an offline start keeps the library. With the
  toggle off the next scan drops the uninstalled entries, because `MergeScanned` only keeps what was
  scanned plus manual entries -- that is the intended way to remove them.
- `StateFlags` bit 4 is "fully installed". A download that has just begun already has a manifest
  and a folder, so `Directory.Exists` alone showed it as installed for the whole download.
- A `FileSystemWatcher` on every steamapps folder triggers a *quiet* scan 5s after the last
  manifest write: no spinner, and `PushState` only when the (id, installed) signature changed. A
  download rewrites its manifest every few seconds, and a full repaint each time throws the
  highlight around under somebody browsing.
- `install` parks the launcher (`Park()`) before opening `steam://install/<appid>`: the launcher is
  topmost on the TV and Steam's dialog would open behind it. The UI's confirm says how to come back
  (the minimize combo, or starting the exe again when the combo is Off).
- The proxy route has to be **deployed with the secret set**, or every fetch answers 501 and the
  Settings row says to add a key. Same trap as PEGI: the code being in `worker.js` proves nothing.

## Store accounts: Epic, GOG, Xbox and Game Pass

- Epic, GOG and Xbox libraries come from signing in to each store, the way Playnite does it: a
  `StoreLoginWindow` (a WebView2 with a profile folder per store under
  `%LOCALAPPDATA%\Consolify\webview2-accounts`) shows the store's own page, a probe runs after every
  navigation and closes the window the moment it has what it came for. Tokens are DPAPI-encrypted
  in `%APPDATA%\Consolify\accounts\<store>.bin`; the last library answer is plain JSON beside it,
  held six hours and served on any failure. Sign-out deletes all three.
- **GOG Galaxy's database was tried first and rejected**: it lists every connected store's library
  locally with no login, but only for people who have Galaxy, and a store disconnected in Galaxy
  keeps a stale list forever. The user asked for the Playnite route instead.
- **Epic** signs in as the Epic Games Launcher's own OAuth client (`launcherAppClient2`, the one
  Legendary, Heroic and Playnite all use). The library host is
  `library-service.live.use1a.on.epicgames.com`; the `library-service.prod.epicgames.com` that
  Playnite names no longer resolves. The catalogue is one request per item, so its answers are kept
  in the cache file by id and only new items are looked up.
- **GOG** is a cookie session. The Galaxy client credentials every open-source client carries
  (`46899977096215655` and its secret) are answered `invalid_client` now, for a bad code and a bad
  refresh token alike, so there is no OAuth route left. The sign-in window's `gog-al` cookie and
  friends are copied out of WebView2's CookieManager and replayed by HttpClient with
  `AllowAutoRedirect = false`: a 302 from the account page means the session is gone. Box art is
  `api.gog.com/v2/games/<id>` (`_links.boxArtImage`), public and per game.
- **Xbox** has no owned-games list; it has the title history (played games, any device), which is
  what Playnite imports. Four tokens in a chain: Microsoft OAuth → user.auth.xboxlive.com →
  xsts.auth.xboxlive.com (which carries the xuid and gamertag) → titlehub. The default sign-in
  client is the Xbox app's own (`0000000048093EE3`, implicit flow, `MBI_SSL` scope), whose RPS
  ticket prefix is tried as `t=`, bare, then `d=` on a 400. `XboxClientId` in Settings switches to
  the code flow of an Azure registration of one's own (`d=` only), which is what Playnite ships
  with. Neither could be verified end to end here: there was no account to sign in with.
- **The PC Game Pass catalogue is keyless**: `catalog.gamepass.com/sigls/v2?id=<sigl>` (the "All PC
  Games" list) answers with product ids, `displaycatalog.mp.microsoft.com/v7.0/products?bigIds=`
  turns twenty at a time into titles, package family names and art (`Poster` is 2:3,
  `SuperHeroArt` 16:9). Each answer carries every screenshot, so the whole list is ~30 MB; it is
  held for a week in `gamepass.json`. The Store suffixes PC builds with " - Windows" or "(PC)",
  which `CleanTitle` strips or the strict Steam title match never succeeds.
- Ids: Xbox title history is `xbox:pfn:<pfn>`, the catalogue `xbox:store:<productId>`, an installed
  Xbox game `xbox:<identity>`. All three carry `PackageFamilyName`, and `NotAlreadyFound` dedupes
  on it -- owned against installed, and owned against owned, since a PFN is unique where a title
  is not (Steam sells two games named exactly "DOOM").
- **Every uninstalled game gets the lite metadata pass**, not just Steam's: cover and tile only,
  facts from Steam by title, never the shared service. Five hundred catalogue games through the
  proxy would be a thousand KV writes on a free tier that allows a thousand a day. The store's own
  art (`RemoteCoverUrl`/`RemoteBackdropUrl`, written as `_pf_`) is the last fallback, and it is the
  third name that `KeepBest`, `HasFetchedArt` and `MetadataService` all have to know.
- `HasFetchedArt` accepts the cover alone for an uninstalled game. Judged on the tile, a Game Pass
  game that is not on Steam has "no art", rejoins the queue on every start, and costs a paced Steam
  search each time for the same nothing.
- Only Steam has a manifest to watch. After any Install the library is re-read once a minute for up
  to three hours (`BeginInstallPolling`), quietly, until the game is on disk.
- `Game.InstallUri` is written by the service that listed the game and is the only thing the
  Install button keys off; the host checks it against `InstallSchemes` before starting anything, so
  a hand-edited library.json cannot turn Install into "run this". GOG's is `goggalaxy://` when
  Galaxy is installed and the game's gog.com page otherwise.
- The sign-in window is driven like the rest of the desktop: stick as mouse, keyboard toggle for the
  on-screen keyboard (it types into the focused window), a Cancel button because B does not reach a
  window that is not the launcher. `BeginModalDialog` around it, or it opens behind the launcher.
- **The Xbox app's own client id (0000000048093EE3) is dead for user tokens**: sign-in succeeds,
  then user.auth.xboxlive.com answers 403 to its ticket with every prefix. Verified on a real
  account, Sept 2026. The way Playnite works is an Azure app registration of its own (client id in
  its source, scopes `Xboxlive.signin Xboxlive.offline_access`, `d=` ticket) -- the "Let this app
  access your info?" prompt is that registration's consent screen. `DefaultClientId` in
  `XboxAccountClient` is the slot for a Consolify registration; until one exists the fallback is
  `000000004C12AE6F` with `t=`, which @xboxreplay/xboxlive-auth reports working and which can go
  the same way. The ticket loop tries every prefix on 400/401/403 and logs each answer.

## Editions, the confirm dialog and search

- **One game in several stores is one tile.** Library entries stay separate on the host (install
  state, launch route, playtime and art are per copy, and everything is keyed by id); the page
  groups them by `titleKey` in `buildEditions`, rebuilt on every state push. Never two copies from
  the same store in one group: Steam sells two games named exactly "DOOM".
- The tile stands for `rankEditions(...)[0]`: the copy picked under Manage → Launch with
  (`Game.PreferredEdition`, carried through `MergeScanned`), then an installed copy, then Steam,
  Epic, GOG, Xbox, then playtime. The game menu offers "Play on X" / "Install on X" for the others.
- The platform filter runs BEFORE grouping, so filtering to Xbox shows the Xbox copy; every other
  filter (favourite, collection, installed, search) is asked of the game as a whole.
- Hide is sent as `setHidden` for every copy. Hiding one copy only brought the other out from behind
  it as a tile of its own.
- `titleKey` must stay in step with `TitleMatch` on the host, plus the Microsoft Store's " - Windows"
  and "(Game Preview)" labels.
- The confirm overlay is a dialog, not a menu: heading, body, and A / B chips that are also what a
  mouse clicks. Nothing in it is focusable, so `confirmInput` takes A without `focusVisible()`.
  Titles are sentence case now; the old all-caps title belonged to the menu-card style.
- A menu row's subtitle sits under its label (`.two-line`). Side by side, a long subtitle ellipsised
  the label down to "In…".
- Search lives in the library top bar because both themes slot the top bar and Polish hides every
  section heading. View opens it (LB/RB are minimize-combo options and RB is the keyboard toggle).
  Typing refilters live; Enter/A/Down keeps it and lands on the first result; Esc/B clears it.
- The default sort puts installed games first, then A to Z. The label says so.
- A text field needs the HOST to hand keyboard focus to the WebView (`focusPage` →
  `MainWindow.FocusPage` → `WebView.Focus()`, which is what calls the controller's MoveFocus).
  DOM `focus()` alone gives a field with no caret that the on-screen keyboard types into nothing.
  `openSearch` and `openInput` both send it.
- The search box is a focusable in the library scope (`data-focus-key="search"`,
  `data-action="search"`, `data-nav-skip`), so View lights it -- but the D-pad never walks to it:
  `navMove` leaves `data-nav-skip` elements out of its candidates. View or the Filter menu only. While a search is
  open or standing, `paintNav` publishes the focus region as "grid" so Polish shows the results.
- `renderMenu` rebuilds its list on every step; it must restore `scrollTop` after emptying it, or a
  menu longer than its box restarts its smooth reveal from 0 on each press and the last rows go
  unseen (Persona 3 Reload's Manage). Overflowing menus fade the edge with more beyond it; the fade
  is 28px, inside REVEAL_MARGIN, so it never dims the highlighted row.
- Manage → Launch with lists stores in `EDITION_ORDER`, never rank order: ranked, the chosen store
  jumped to the top and the row under the highlight changed.
- After a vertical wheel (the right stick), the next D-pad step lands on the first item in view
  when the highlight has been scrolled out of sight (`resumeInView`). Keyed on the wheel event,
  not on visibility alone: during a fast run of presses the smooth reveal leaves the highlight half
  out of view on every step.
- **The page claims View on the library** (`publishClaims` → `claimButtons` →
  `GamepadService.UiClaimedButtons`). When the keyboard toggle is bound to View, Press mode used to
  spend the press before the UI saw it, so View raised the keyboard and search never opened. A
  claimed press is delivered to the UI and marked spent for the toggle; the claim is dropped under
  any overlay, while searching and on other screens, so the toggle keeps View everywhere else.
- **Wheels are routed** to the list the user is in when they land on nothing scrollable
  (`wheelHome`). The right stick sends real wheel events, which go to whatever is under the hidden
  cursor; Classic's grid is only the bottom half of the screen, so the stick usually scrolled
  nothing there. A wheel over a real scroller is left to the browser.

## Over a game: the foreground and the exit

- **Windows refuses SetForegroundWindow from a program that did not receive the last input, and
  XInput is not input.** With a game in front, the Guide menu and the Power Wheel were shown
  (topmost) but never became foreground, so `launcherFg` was false and the pad went nowhere until a
  mouse click. `TakeForeground` joins the foreground window's input queue for the call
  (`AttachThreadInput`), and while `_overlayActive` the pad service treats the launcher as in front
  and the game as not focused regardless, so a refused foreground can no longer freeze a menu.
- **A session used to wait a flat 15 s after every exit** for a successor process from the install
  dir. Now: anything of the game still running at that instant carries the session on (launchers
  hand over before they exit); a close Consolify asked for waits for nothing (`_closeRequested`);
  a process that lived under 90 s (a pre-launcher) keeps the 15 s window; anything longer gets 1 s,
  for a game that restarts itself. The poll is every 250 ms, not 1.5 s.
