
"use strict";

/* ============================== bridge ============================== */

const HOST = window.chrome && window.chrome.webview ? window.chrome.webview : null;

function send(msg) {
  if (HOST) HOST.postMessage(msg);
  else mockHandle(msg);
}

/* ============================== state ============================== */

let S = {
  games: [],
  collections: [],
  settings: null,
  displays: [],
  themes: [],
  startupRegistered: false,
  gameRunning: false,
  runningGameId: null,
  scanning: false,
  steamAccount: null,                    // { steamId, personaName, ownedCount, fetchedAt, error }
  stores: null,                          // { epic|gog|xbox: { signedIn, user, count, fetchedAt, error }, gamePass: { count, fetchedAt, error } }
  emulation: null,                       // { emulators: [...], romFolders: [...], platforms: [{ id, name, shortName, extensions, hasCores }] }
  padConnected: false,
};

let view = "library";                    // library | detail | settings
// Library focus lives on the scope element (see the spatial focus section), not in a zone+row+col
// triple -- that is what lets a theme lay the screen out any way it likes.

let detailGameId = null;
let detailReturn = "library";            // where B goes back to from detail

/* filter & sort (session state) — empty sets mean "no restriction" */
const F = { platforms: new Set(), status: new Set(), collections: new Set(), fav: false, hidden: false, sort: "az", search: "" };
/* The stores. An emulated game's platform is its SYSTEM -- "Super Nintendo", "PlayStation" -- so
   the filter lists those too, but only the ones the library actually has (see emulatedPlatforms):
   forty consoles with nothing under them would bury the five rows anyone uses. */
const PLATFORMS = ["Steam", "Epic", "GOG", "Xbox", "Manual"];

/** The systems the library holds ROMs for, in the catalogue's order, each with its count. */
function emulatedPlatforms() {
  const counts = new Map();
  for (const g of S.games) if (g.emulated) counts.set(g.platform, (counts.get(g.platform) || 0) + 1);
  const order = (S.emulation && S.emulation.platforms || []).map(p => p.name);
  return [...counts.keys()]
    .sort((a, b) => (order.indexOf(a) + 1 || 999) - (order.indexOf(b) + 1 || 999) || a.localeCompare(b))
    .map(name => ({ name, count: counts.get(name) }));
}

/* The catalogue entry behind an emulated game's platform id, and the emulator it runs with:
   its own, if it was given one under Manage, otherwise its folder's. */
function platformDef(id) { return (S.emulation && S.emulation.platforms || []).find(p => p.id === id) || null; }
function emulatorById(id) { return id && S.emulation ? (S.emulation.emulators || []).find(e => e.id === id) || null : null; }
function romFolderById(id) { return id && S.emulation ? (S.emulation.romFolders || []).find(f => f.id === id) || null : null; }
function emulatorFor(g) {
  if (!g || !g.emulated) return null;
  const folder = romFolderById(g.romFolderId);
  return emulatorById(g.emulatorId) || (folder ? emulatorById(folder.emulatorId) : null);
}
const STATUSES = ["Installed", "Not installed"];
const MINIMIZE_COMBOS = ["LS + RS", "LB + RB", "LT + RT + LB + RB", "Guide", "View + Menu", "LS + RB", "LB + RS", "Off"];
/* Deliberately combos rather than single buttons: inside a game every face and shoulder button
   belongs to the game, so a one-button binding would fire in the middle of play. */
const SCREENSHOT_COMBOS = ["Off", "View + Y", "View + X", "View + A", "View + B", "LB + RB", "LS + RS"];

const SORTS = [
  { id: "az", label: "Installed, then A – Z" },
  { id: "za", label: "Z – A" },
  { id: "recent", label: "Recently played" },
  { id: "played", label: "Most played" },
  { id: "sizeDesc", label: "Largest first" },
  { id: "sizeAsc", label: "Smallest first" },
  { id: "score", label: "Highest rated" },
];

function resetFilters() {
  F.platforms.clear();
  F.status.clear();
  F.collections.clear();
  F.fav = false;
  F.hidden = false;
  F.sort = "az";
  setSearch("");
}

function activeFilterCount() {
  return F.platforms.size + F.status.size + F.collections.size + (F.fav ? 1 : 0) + (F.hidden ? 1 : 0)
    + (F.search ? 1 : 0);
}

/* Collections a game belongs to are stored on the collection, not the game, so membership is a
   lookup rather than a property. Rebuilt per call: the sets are small and collections change
   from the same screen that reads them. */
/* Deleting one is worth a confirmation: it is the only destructive thing in Settings that cannot
   be undone by pressing the same button again. */
function askDeleteCollection(c) {
  const n = (c.gameIds || []).length;
  confirmState = {
    title: `Delete “${c.name}”?`,
    body: n ? `The collection goes; the ${n} game${n === 1 ? "" : "s"} in it stay in your library.`
            : "The collection is empty, so nothing else changes.",
    yesLabel: "Yes, delete",
    onYes: () => { send({ cmd: "deleteCollection", id: c.id }); toast(`${c.name} deleted`); },
  };
  confirmIdx = 0;
  $("overlay-confirm").classList.add("active");
  renderConfirm();
}

function gameInSelectedCollection(g) {
  return S.collections.some(c => F.collections.has(c.id) && (c.gameIds || []).includes(g.id));
}

/* input mode: "pad" hides the pointer and ignores hover; "pointer" is stick or real mouse.
   Starts on "pad" so the launcher boots couch-first with a visible highlight. */
let inputMode = "pad";

function setInputMode(mode) {
  if (inputMode === mode) return;
  inputMode = mode;
  document.body.classList.toggle("pad-mode", mode === "pad");
  // focusVisible() just changed, so whatever is on screen needs its highlight re-painted.
  // A host-driven switch (the stick moved, or the launcher came back to the foreground)
  // arrives with no mouse event behind it to trigger that on its own.
  repaintFocus();
  // Keep the host's copy in step. It only pushes "pointer" when its own idea of the mode
  // changes, so a switch we made locally (opening an overlay, centring the pointer) would
  // otherwise leave it believing we are already in pointer mode -- and the next stick move
  // would push nothing, stranding the UI in pad mode with the cursor hidden.
  send({ cmd: "inputMode", mode });
}

/* Set while a radial/in-game overlay is up. Those menus are pad-driven and hide the cursor,
   so hovering must not steer them and the highlight must always be painted — otherwise A can
   land on nothing and the menu looks frozen. */
let overlayMode = false;

/** Hover should only move focus when the pointer is actually the active input. */
function hoverEnabled() { return inputMode === "pointer" && !overlayMode; }

/* Whether the pointer currently rests on something selectable. In pointer mode with the
   cursor over empty space nothing is highlighted and A does nothing, so the pointer can
   never "arm" a stale item. The index is still remembered, so picking the D-pad back up
   resumes from the last selected item. */
let pointerOnItem = false;
/* Screens migrated to the spatial engine mark their items with [data-focusable]; the class
   list covers the ones still on index navigation. Both are here until the migration finishes. */
const FOCUSABLE_SEL = "[data-focusable], .cont-item, .grid-item, .tab, .set-row, .ov-row, .coll-card, .pill-btn";

/** Should a focus highlight be painted at all right now? */
function focusVisible() { return overlayMode || inputMode === "pad" || pointerOnItem; }

function setPointerOnItem(on) {
  if (pointerOnItem === on) return;
  pointerOnItem = on;
  repaintFocus();
}

/** Rebuild every screen. Used when the theme changes what the markup should be. */
function rerenderAll() {
  renderTabbars();
  renderLibrary();
  if (view === "settings") renderSettings();
  if (view === "detail") renderDetail();
}

/** Re-apply focus styling for whatever screen/overlay is currently up. */
function repaintFocus() {
  // Ordered like handleInput: whatever owns the input owns the highlight. The radial submenu
  // comes first for the same reason it does there -- it sits on top of everything else, and
  // repainting the library underneath it would leave the visible menu unhighlighted.
  if (radialSub) renderRadialSub();
  else if (filterOpen) renderFilter();
  else if (gameMenu) renderGameMenu();
  else if (collectOpen) renderCollect();
  else if (manageOpen) renderManage();
  else if (choiceState) renderChoice();
  else if (confirmState) renderConfirm();
  else if (view === "library") updateLibraryFocus(true);
  else if (view === "detail") updateDetailFocus();
  else if (view === "settings") renderSettings();
}

/* ============================== spatial focus ==============================
   The app-side half of nav.js. Focus is an element identified by a stable key
   rather than a row/column pair, so a re-render (or a theme that lays the same
   games out completely differently) lands the highlight back on the same thing. */

let navAnchor = null;      // sticky cross-axis coordinate, held along a straight run
let navAnchorAxis = null;  // "x" while moving vertically, "y" while moving horizontally

/* Each scope remembers its own highlight, parked on the scope element itself.
   One global key could not survive an overlay: opening the game menu over the library
   would overwrite the library's position, and closing it would drop you back on the
   wrong tile. Per-scope keys make open/close free, and nest correctly. */
function scopeKey(scope) { return scope ? scope.dataset.focusCurrent || null : null; }

/* Placing the highlight ends whatever run was in progress.

   Without this, walking down the settings categories and pressing A left the anchor
   sitting in the tab column; the first Down inside the options then scored the tabs as
   "straight below" and threw the highlight back out of the list. Anything that puts the
   highlight somewhere -- a render, an overlay opening, a click -- is a fresh start, so
   only navMove keeps the anchor, by restoring it after this. */
function setScopeKey(scope, key) {
  if (!scope) return;
  if (key) scope.dataset.focusCurrent = key;
  else delete scope.dataset.focusCurrent;
  navAnchor = null;
  navAnchorAxis = null;
}

function focusEl(scope) {
  scope = scope || Nav.activeScope();
  const key = scopeKey(scope);
  if (!scope || !key) return null;
  return Nav.focusables(scope).find(el => Nav.keyOf(el) === key) || null;
}

/** Focus an element outright: hover, a click, or landing on a screen. Ends any run. */
function setFocusEl(el) {
  if (!el) return;
  setScopeKey(el.closest("[data-focus-scope]"), Nav.keyOf(el));
  navAnchor = null;
  navAnchorAxis = null;
}

function clearFocus(scope) { setScopeKey(scope || Nav.activeScope(), null); }

/* Where the highlight should land when a screen has no remembered position.

   Never the tab bar: it is first in DOM order on every screen, so the naive "first
   focusable" dropped you on "Library" and made you press Down before you could do
   anything. Content first, then anything that is not a tab. */
function preferredFocus(list) {
  return list.find(el => el.dataset.gameId || el.dataset.collId)
      || list.find(el => !el.dataset.tab)
      || list[0]
      || null;
}

/** Put the highlight somewhere sensible in a scope that has lost it. */
function ensureFocus(scope) {
  if (focusEl(scope)) return;
  const el = preferredFocus(Nav.focusables(scope));
  if (el) setFocusEl(el); else clearFocus(scope);
}

/** Nearest ancestor that actually scrolls on the given axis. */
function scrollParentOf(el, axis) {
  for (let p = el.parentElement; p; p = p.parentElement) {
    const s = getComputedStyle(p);
    if (axis === "x") {
      if (/(auto|scroll)/.test(s.overflowX) && p.scrollWidth > p.clientWidth + 1) return p;
    } else if (/(auto|scroll)/.test(s.overflowY) && p.scrollHeight > p.clientHeight + 1) return p;
  }
  return null;
}

/** Offset along one axis, summed to the scroller. Layout pixels, to match scrollTop/Left. */
function offsetWithin(el, container, axis) {
  let n = 0;
  for (let e = el; e && e !== container; e = e.offsetParent) n += axis === "x" ? e.offsetLeft : e.offsetTop;
  return n;
}

/* How far a scroller has to move to put an element in view, or null if it already is.
   Snaps fully to either end so the first item keeps its focus-glow padding and the last
   is not left hanging a few pixels short. */
function revealOffset(sc, el, axis) {
  const near = offsetWithin(el, sc, axis);
  const far = near + (axis === "x" ? el.offsetWidth : el.offsetHeight);
  // Where the list is going, not where it is: mid-glide the two differ, and measuring against the
  // current position asked for the same scroll twice or turned round for a row already on its way in.
  const viewNear = scrollTarget(sc, axis);
  const size = axis === "x" ? sc.clientWidth : sc.clientHeight;
  const total = axis === "x" ? sc.scrollWidth : sc.scrollHeight;
  const viewFar = viewNear + size;

  /* Nothing focusable above the first row means the space above it is not slack -- it is that
     row's section heading. Clearing REVEAL_MARGIN for the focus glow scrolled that heading off
     the top the moment you walked back up the list, which is why the first category in every
     Settings tab kept vanishing. Snap to the end instead; the glow has its room there. */
  const ends = sc.querySelectorAll(FOCUSABLE_SEL);
  if (ends.length) {
    if (ends[0] === el) return 0;
    if (ends[ends.length - 1] === el) return total;
  }

  if (near - REVEAL_MARGIN <= 0) return 0;
  if (far + REVEAL_MARGIN >= total) return total;
  if (near - REVEAL_MARGIN < viewNear) return near - REVEAL_MARGIN;
  if (far + REVEAL_MARGIN > viewFar) return far + REVEAL_MARGIN - size;
  return null;
}

/**
 * Bring the focused element into view, on whichever axis its container scrolls.
 *
 * Both axes, because a theme is free to lay the grid out sideways -- flipping the scroller
 * to horizontal is one of the easiest things a theme can do, and without this the highlight
 * would walk straight off the edge of the screen.
 */
function revealFocus(el) {
  for (const axis of ["y", "x"]) {
    const sc = scrollParentOf(el, axis);
    if (!sc) continue;
    if (axis === "y") watchScrolled(sc);
    const next = revealOffset(sc, el, axis);
    if (next === null) continue;
    animateScroll(sc, axis, next);
  }
}

/*
 * Scrolling that follows the highlight, as a critically damped spring.
 *
 * Not scrollTo({behavior: "smooth"}). That starts a fresh eased animation from standstill on every
 * call, so a run of steps -- a held D-pad at 9 a second, a held arrow key at 30 -- kept throwing
 * away the motion in progress and accelerating from zero again: the list lurched forward, stalled,
 * lurched. With a held key it could not keep up at all and caught up in one jump when the key was
 * let go. A spring can be retargeted mid-flight without losing its velocity, so a run of steps is
 * one continuous glide, and a single step still eases in and out.
 *
 * A scroll the animator did not make -- the mouse wheel, the right stick, a drag -- hands control
 * back at once rather than being fought.
 */
const SCROLL_OMEGA = 22;          // spring stiffness, rad/s: settles in about 250 ms
const scrollAnims = new Map();    // "y"/"x" -> WeakMap(element -> state)
["x", "y"].forEach(a => scrollAnims.set(a, new WeakMap()));

function scrollProp(axis) { return axis === "x" ? "scrollLeft" : "scrollTop"; }

/** Where a scroller is heading: the animation's target, or where it is when nothing is running. */
function scrollTarget(sc, axis) {
  const a = scrollAnims.get(axis).get(sc);
  return a && a.running ? a.target : sc[scrollProp(axis)];
}

function stopScroll(sc, axis) {
  const a = scrollAnims.get(axis || "y").get(sc);
  if (a) a.running = false;
}

function animateScroll(sc, axis, to) {
  const prop = scrollProp(axis);
  const max = Math.max(0, axis === "x" ? sc.scrollWidth - sc.clientWidth : sc.scrollHeight - sc.clientHeight);
  to = Math.max(0, Math.min(to, max));
  const map = scrollAnims.get(axis);
  let a = map.get(sc);
  if (!a || !a.running) {
    a = { pos: sc[prop], vel: 0, target: to, written: sc[prop], running: true, t: null, lastFrameAt: performance.now() };
    map.set(sc, a);
  } else {
    a.target = to;
    return;                       // already gliding: the next frame heads for the new target
  }

  const step = (now) => {
    if (!a.running || map.get(sc) !== a) return;
    a.lastFrameAt = performance.now();
    // Somebody else moved it (wheel, stick, drag): let them have it.
    if (Math.abs(sc[prop] - a.written) > 2) { a.running = false; return; }
    // The first frame's timestamp is when that frame began, which can be BEFORE this animation
    // was asked for; measured from it, the first step moved nothing at all. Give it one frame.
    let dt = a.t === null ? 1 / 60 : Math.min(0.064, Math.max(0, (now - a.t) / 1000));
    a.t = now;
    while (dt > 0) {              // small fixed substeps keep the spring stable on a slow frame
      const h = Math.min(dt, 0.008);
      dt -= h;
      a.vel += (SCROLL_OMEGA * SCROLL_OMEGA * (a.target - a.pos) - 2 * SCROLL_OMEGA * a.vel) * h;
      a.pos += a.vel * h;
    }
    if (Math.abs(a.target - a.pos) < 0.5 && Math.abs(a.vel) < 20) { a.pos = a.target; a.running = false; }
    sc[prop] = a.pos;
    a.written = sc[prop];
    if (a.running) requestAnimationFrame(step);
  };
  requestAnimationFrame(step);

  // Frames that stop coming -- the window hidden mid-glide, or the preview, which runs
  // requestAnimationFrame once and then not again -- must still leave the list where it was going,
  // not stranded halfway. Checked for as long as the glide runs, not just at its start.
  const watch = () => {
    if (!a.running || map.get(sc) !== a) return;
    if (performance.now() - a.lastFrameAt > 150) {
      a.running = false;
      sc[prop] = a.target;
      return;
    }
    setTimeout(watch, 150);
  };
  setTimeout(watch, 150);
}

/** Move the highlight one step. Returns false when there is nowhere to go. */
function navMove(dir) {
  const scope = Nav.activeScope();
  const list = Nav.focusables(scope);
  if (!list.length) return false;

  const cur = focusEl();
  if (!cur) { setFocusEl(list[0]); afterFocusMove(); return true; }

  // Scrolled away with the right stick: the highlight is somewhere off screen, and stepping from
  // there would scroll the list straight back to it. Land on the first thing in view instead.
  const resumed = wheelScrolled && resumeInView(cur, list);
  wheelScrolled = false;
  if (resumed) {
    setScopeKey(scope, Nav.keyOf(resumed));
    afterFocusMove();
    return true;
  }

  const horizontal = dir === "Left" || dir === "Right";
  const axis = horizontal ? "y" : "x";
  const from = cur.getBoundingClientRect();
  // A change of axis starts a new run, and the anchor is re-taken from where we are.
  if (navAnchor === null || navAnchorAxis !== axis) {
    const c = Nav.centre(from);
    navAnchor = horizontal ? c.y : c.x;
    navAnchorAxis = axis;
  }
  const anchorNow = navAnchor;

  /* Left/Right stay in their row while the row has anywhere left to go.

     Without this, Right off the last tile of a grid row scored some item on a
     different line as "to the right and a bit up" and jumped there -- pressing
     Right on the last tile threw you into the carousel. Confining the pool to
     things that share the row keeps the common case sane; when the row really is
     exhausted the wrap below takes over, and only if there is nothing to wrap to
     does it fall back to the whole scope (which is what lets a theme put a
     sidebar to the left of a grid and have Right cross into it). */
  // data-nav-skip: can hold the highlight, but is never walked to. The search box is reached with
  // View or from the Filter menu only -- Up off the top row landing in it read as a mistake.
  const walkable = list.filter(el => !el.hasAttribute("data-nav-skip"));
  let pool = walkable.filter(el => el !== cur);
  if (horizontal) {
    const sameBand = pool.filter(el => {
      const r = el.getBoundingClientRect();
      return r.bottom > from.top && r.top < from.bottom;
    });
    if (sameBand.length) pool = sameBand;
  }

  const pick = (candidates) => {
    let best = null, bestScore = Infinity;
    for (const el of candidates) {
      const s = Nav.score(from, el.getBoundingClientRect(), dir, navAnchor);
      if (s < bestScore) { bestScore = s; best = el; }
    }
    return best;
  };

  let best = pick(pool);
  if (!best) best = Nav.wrapTarget(walkable, cur, dir);
  if (!best && pool.length !== walkable.length - 1) best = pick(walkable.filter(el => el !== cur));
  if (!best) return false;

  setScopeKey(scope, Nav.keyOf(best));
  // setScopeKey ends a run; this is a step within one, so put the anchor back.
  navAnchor = anchorNow;
  navAnchorAxis = axis;
  afterFocusMove();
  return true;
}

/*
 * Set by a vertical wheel -- which is what the right stick sends -- and consumed by the next
 * D-pad step. Only a WHEEL counts: revealFocus scrolls smoothly, so during a fast run of Down
 * presses the highlight is briefly half out of view on every step, and treating that as "the
 * user scrolled away" would throw the run back to the top of the screen.
 */
let wheelScrolled = false;
window.addEventListener("wheel", (e) => {
  if (Math.abs(e.deltaY) > Math.abs(e.deltaX)) wheelScrolled = true;
}, { passive: true, capture: true });

/*
 * The right stick sends real wheel events, and a wheel goes to whatever is under the cursor. In
 * pad mode the cursor is hidden and could be anywhere -- over the Continue row, the top bar, the
 * backdrop -- and in Classic the grid is only the bottom half of the screen, so most of the time
 * the stick scrolled nothing at all. Polish got away with it because its grid fills the screen.
 *
 * So a vertical wheel that lands on nothing that can scroll that way is handed to the list the
 * user is actually in: the open menu, else the scroller around the highlight, else the screen's
 * own list. A wheel that DOES land on a scroller is left alone, so a real mouse is unaffected.
 */
function wheelHome() {
  const scope = Nav.activeScope();
  if (!scope) return null;
  if (scope.classList.contains("overlay")) return scope.querySelector(".ov-scroll, .guide-body");
  const cur = focusEl(scope);
  const around = cur && scrollParentOf(cur, "y");
  if (around) return around;
  if (view === "library") return $("gridScroll");
  if (view === "settings") return $("settingsScroll");
  return null;
}

function canScrollY(el, dy) {
  if (!el || el.scrollHeight <= el.clientHeight + 1) return false;
  if (!/(auto|scroll)/.test(getComputedStyle(el).overflowY)) return false;
  return dy > 0 ? el.scrollTop + el.clientHeight < el.scrollHeight - 1 : el.scrollTop > 0;
}

window.addEventListener("wheel", (e) => {
  if (Math.abs(e.deltaY) <= Math.abs(e.deltaX) || e.ctrlKey) return;
  for (let p = e.target instanceof Element ? e.target : null; p; p = p.parentElement)
    if (canScrollY(p, e.deltaY)) return;          // already over something that will scroll
  const home = wheelHome();
  if (!home || !canScrollY(home, e.deltaY)) return;
  e.preventDefault();
  const step = e.deltaMode === 1 ? e.deltaY * 40 : e.deltaMode === 2 ? e.deltaY * home.clientHeight : e.deltaY;
  stopScroll(home, "y");
  home.scrollTop += step;
}, { passive: false });

/*
 * The right stick, scrolled by the frame.
 *
 * Over the launcher the host sends the stick's speed (wheel notches a second, up positive) rather
 * than wheel notches. A notch is a 100px jump, and they arrived up to 18 times a second on
 * whichever poll crossed the line, so however smoothly the stick was held the list moved in uneven
 * lurches. Here the list moves by speed x frame time on every frame, and the speed itself eases
 * towards what the stick says, so pushing and letting go ramp rather than snap.
 *
 * Which list: the one under the pointer when the pointer is in use, otherwise the one the user is
 * in (wheelHome). A speed older than 250 ms is treated as zero, so a lost "stop" cannot leave the
 * list running.
 */
const STICK_PX_PER_NOTCH = 100;     // what one wheel notch scrolls, so the speed matches the old feel
const STICK_EASE_SEC = 0.08;
let stickTarget = 0, stickVel = 0, stickAt = 0, stickRunning = false;
let stickEl = null, stickPos = 0;
let lastClientX = NaN, lastClientY = NaN;

/** The next frame, or 50 ms from now if frames have stopped (a hidden window, the preview). */
function nextFrame(cb) {
  let done = false;
  const run = () => { if (done) return; done = true; cb(performance.now()); };
  requestAnimationFrame(run);
  setTimeout(run, 50);
}

function stickScrollEl() {
  if (inputMode === "pointer" && !isNaN(lastClientX)) {
    for (let p = document.elementFromPoint(lastClientX, lastClientY); p; p = p.parentElement)
      if (p.scrollHeight > p.clientHeight + 1 && /(auto|scroll)/.test(getComputedStyle(p).overflowY)) return p;
  }
  return wheelHome();
}

function onStickScroll(notchesPerSec) {
  stickTarget = -notchesPerSec * STICK_PX_PER_NOTCH;   // up on the stick is up the list: scrollTop falls
  stickAt = performance.now();
  if (notchesPerSec !== 0) wheelScrolled = true;       // the next D-pad step resumes from what is in view
  if (stickRunning || notchesPerSec === 0) return;
  stickRunning = true;
  stickEl = null;
  let last = performance.now();
  const frame = (now) => {
    const dt = Math.min(0.1, Math.max(0, (now - last) / 1000));
    last = now;
    const target = now - stickAt > 250 ? 0 : stickTarget;
    stickVel += (target - stickVel) * (1 - Math.exp(-dt / STICK_EASE_SEC));
    if (target === 0 && Math.abs(stickVel) < 8) { stickVel = 0; stickRunning = false; return; }

    const el = stickScrollEl();
    if (el !== stickEl) { stickEl = el; stickPos = el ? el.scrollTop : 0; }
    if (el) {
      // Something else moved it -- the D-pad's glide, a real wheel -- so carry on from there.
      if (Math.abs(el.scrollTop - stickPos) > 2) stickPos = el.scrollTop;
      stopScroll(el, "y");
      const max = el.scrollHeight - el.clientHeight;
      stickPos = Math.max(0, Math.min(max, stickPos + stickVel * dt));
      el.scrollTop = stickPos;
    }
    nextFrame(frame);
  };
  nextFrame(frame);
}

/** The first focusable in view in the scroller the highlight has been scrolled out of, or null
    when the highlight is still on screen and an ordinary step should happen. */
function resumeInView(cur, list) {
  const sc = scrollParentOf(cur, "y");
  if (!sc) return null;
  const view = sc.getBoundingClientRect();
  const visible = (r) => {
    const h = Math.min(r.bottom, view.bottom) - Math.max(r.top, view.top);
    return h >= r.height * 0.6;
  };
  if (visible(cur.getBoundingClientRect())) return null;
  const inView = list.filter(el => sc.contains(el) && visible(el.getBoundingClientRect()));
  if (!inView.length) return null;
  // Top row first, then leftmost: the first item of the first row on screen.
  inView.sort((a, b) => {
    const ra = a.getBoundingClientRect(), rb = b.getBoundingClientRect();
    return (Math.round(ra.top) - Math.round(rb.top)) || (ra.left - rb.left);
  });
  return inView[0];
}

function afterFocusMove() {
  paintNav();
  const el = focusEl();
  if (el) revealFocus(el);
}

/** Apply the highlight, the section dimming and the backdrop from the DOM alone. */
function paintNav() {
  const scope = Nav.activeScope();
  if (!scope) return;
  const show = focusVisible();
  const cur = focusEl();

  Nav.focusables(scope).forEach(el => el.classList.toggle("focused", show && el === cur));
  // Anything marked as a dim group fades unless the highlight is inside it. Themes opt
  // in by adding data-dim-group; nothing here knows what a "Continue row" is.
  scope.querySelectorAll("[data-dim-group]").forEach(g =>
    g.classList.toggle("zone-dim", !!cur && !g.contains(cur)));

  /* Which region the highlight is in, published on the scope element so a theme can style a
     whole state off it -- collapsing a hero when focus reaches the grid, say. A theme cannot
     run script, so without this the only "where am I" signal available to CSS is the focus
     ring itself, which is far too local to drive a layout.

     Deliberately the remembered focus rather than the visible one: moving the mouse off an
     item clears the ring, and a layout that flipped back every time the pointer wandered
     would be unusable. */
  const region = cur ? cur.closest("[data-region]") : null;
  // The search box sits in the top bar, but what it is ABOUT is the grid: while a search is being
  // typed or is standing, say "grid" so a theme lays the results out in view. Polish would
  // otherwise slide back to its resting hero and leave the results as a peek under the dock.
  if (cur && cur.id === "libSearch" && (searchOpen || F.search)) scope.dataset.focusRegion = "grid";
  else if (region) scope.dataset.focusRegion = region.dataset.region;
  else delete scope.dataset.focusRegion;

  updateContinueScroll(true);
  const g = focusedGame();
  scheduleBackdrop(g);
  updateFocusDetail(g);
}

/* Keep the opt-in focus-detail region filled. Cheap enough to do on every move -- it is a
   handful of textContent writes -- and doing it unconditionally means a theme can slot the
   region in at any point and find it already correct. */
function updateFocusDetail(g) {
  const panel = $("fdTitle");
  if (!panel) return;
  panel.textContent = g ? g.title : "";
  $("fdMeta").textContent = g ? (g.installed ? shortMeta(g) : `${g.platform} · NOT INSTALLED`).toUpperCase() : "";
  $("fdDesc").textContent = g && g.installDir ? g.installDir : "";
  $("fdPlaytime").textContent = g ? fmtPlaytime(g.playtimeMinutes) : "";
  $("fdLastPlayed").textContent = g ? fmtLastPlayed(g.lastPlayed) : "";
  $("fdSize").textContent = g ? fmtSize(g.sizeBytes) : "";
}

/** The game the highlight is on, straight off the element. */
function focusedGame() {
  if (view === "detail") return gameById(detailGameId);
  const el = focusEl();
  return el && el.dataset.gameId ? gameById(el.dataset.gameId) : null;
}

/* ============================== theme ============================== */

/* The presets. Every one is a light, saturated tone: the accent is used as a fill behind dark
   text (the A badge, the Play button) as well as for rings and glows, so a dark accent would
   take the label down with it. A custom colour is allowed to be anything -- see accentRow. */
const ACCENTS = [
  { name: "Ember", hex: "#F0A253" },   // the default; the design's own colour
  { name: "Coral", hex: "#E97A6C" },
  { name: "Rose", hex: "#F07AA8" },
  { name: "Orchid", hex: "#C78BE8" },
  { name: "Indigo", hex: "#8098F0" },
  { name: "Aqua", hex: "#5FC9D6" },
  { name: "Mint", hex: "#6FCF97" },
  { name: "Lime", hex: "#B8D96B" },
];
const DEFAULT_ACCENT = ACCENTS[0].hex;

function accentName(hex) {
  const preset = ACCENTS.find(a => a.hex.toUpperCase() === String(hex).toUpperCase());
  return preset ? preset.name : "Custom";
}

/** Only "#RRGGBB" reaches the stylesheet. Mirrors SettingsStore.IsHexColor on the host. */
function isHexColor(v) { return typeof v === "string" && /^#[0-9A-Fa-f]{6}$/.test(v); }

/* Relative luminance, WCAG's formula. Used only to decide what colour sits legibly *on* the
   accent: the presets are all light enough for dark text, but a custom colour can be anything,
   and a navy accent with near-black text on it is an unreadable Play button. */
function luminance(hex) {
  const ch = i => {
    const v = parseInt(hex.substr(1 + i * 2, 2), 16) / 255;
    return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
  };
  return 0.2126 * ch(0) + 0.7152 * ch(1) + 0.0722 * ch(2);
}

/* Push the settings' colours onto the :root tokens. Everything in app.css resolves to those, so
   this one call recolours the whole UI -- no re-render, and nothing else has to know a theme
   exists. Setting a token to "" removes the override and falls back to the stylesheet's own
   value, which is what makes a bad or missing colour a no-op rather than a blank screen. */
/* The theme's markup is fetched, not linked, because it has to be parsed rather than
   rendered. Tracked by URL (which carries the file's mtime) so a save reloads it and an
   unchanged theme does not refetch on every state push. */
let themeHtmlUrl = null;

async function applyThemeMarkup() {
  const theme = currentTheme();
  const url = theme && theme.html ? theme.html : null;
  if (url === themeHtmlUrl) return false;
  themeHtmlUrl = url;

  if (!url) { Theme.clear(); applyThemeLayout(); return true; }
  try {
    const res = await fetch(url);
    if (!res.ok) throw new Error("HTTP " + res.status);
    Theme.load(await res.text(), url);
  } catch (e) {
    // A theme with broken markup keeps its styling and falls back to the built-in
    // layout, rather than taking the whole launcher down with it.
    Theme.clear();
    toast(`Theme markup failed to load: ${e.message}`);
  }
  applyThemeLayout();
  return true;
}

/** Hand each screen to the theme's layout, or put it back if the theme has none. */
function applyThemeLayout() {
  ["library", "detail", "settings"].forEach(id =>
    Theme.applyScreen(document.getElementById("screen-" + id), "screen-" + id));
}

function applyTheme() {
  applyThemeSheet();
  // A class rather than a per-screen render, because every screen has its own legend and a
  // theme may have moved it somewhere of its own.
  document.body.classList.toggle("no-legend", !!(S.settings && S.settings.hideLegend));
  // Fire and forget: the markup arrives a tick later and re-renders then, so the
  // colours are not held up waiting on a file read.
  applyThemeMarkup().then(changed => { if (changed) rerenderAll(); });

  const root = document.documentElement.style;
  // The theme's own tokens go on first so the accent setting still wins: a user who picks a
  // colour expects it to hold whatever theme is loaded, and a theme that wants to own the
  // accent simply ships a theme.css rule, which the stylesheet layer below cannot override.
  const theme = currentTheme();
  const tokens = (theme && theme.tokens) || {};
  for (const [name, value] of Object.entries(appliedTokens))
    if (!(name in tokens)) root.removeProperty(name);
  appliedTokens = {};
  for (const [name, value] of Object.entries(tokens)) {
    if (!/^--[A-Za-z0-9_-]+$/.test(name) || typeof value !== "string") continue;
    root.setProperty(name, value);
    appliedTokens[name] = value;
  }

  const hex = S.settings && S.settings.accentColor;
  const ok = isHexColor(hex);
  root.setProperty("--accent", ok ? hex : "");
  // 0.5 rather than WCAG's 0.179 contrast crossover: the ink is off-white and the deep is
  // near-black, so both are legible over a mid-tone and the eye prefers dark ink there.
  root.setProperty("--on-accent", ok && luminance(hex) < 0.5 ? "var(--ink)" : "");
}

/* Tokens this theme set, so switching themes can take them off again -- otherwise a token
   from the old theme survives into a new one that never mentions it. */
let appliedTokens = {};

function currentTheme() {
  const id = (S.settings && S.settings.theme) || "";
  return (S.themes || []).find(t => t.id === id) || null;
}

/* The theme's stylesheet is one <link> appended after app.css, so a theme overrides by
   ordinary cascade order and needs no !important anywhere.

   The href carries a cache-busting stamp from the host (the file's mtime). Re-setting the
   same href would not reload, which is exactly what made saving a theme edit look like it
   had done nothing. */
function applyThemeSheet() {
  const theme = currentTheme();
  const href = theme && theme.css ? theme.css : null;
  let link = document.getElementById("themeSheet");

  if (!href) { if (link) link.remove(); return; }
  if (link && link.getAttribute("href") === href) return;

  if (!link) {
    link = document.createElement("link");
    link.id = "themeSheet";
    link.rel = "stylesheet";
    document.head.appendChild(link);
  }
  link.setAttribute("href", href);
}

/* Only a pointer that actually moved counts. The browser also raises mousemove when the page
   scrolls or relayouts under a cursor that is sitting still -- which is exactly what happens
   while the arrow keys walk the grid past a parked pointer -- and treating that as the mouse
   being picked up threw the page into pointer mode and let hover steal the highlight mid-run. */
let lastMouseX = NaN, lastMouseY = NaN;
window.addEventListener("mousemove", (e) => {
  lastClientX = e.clientX; lastClientY = e.clientY;
  if (e.screenX === lastMouseX && e.screenY === lastMouseY) return;
  lastMouseX = e.screenX; lastMouseY = e.screenY;
  const wasPad = inputMode === "pad";
  setInputMode("pointer");
  setPointerOnItem(!!(e.target instanceof Element && e.target.closest(FOCUSABLE_SEL)));

  // Boundary events fire before the mousemove that caused them, so the mouseenter for the item
  // the pointer just arrived on ran while hover was still disabled and its handler ignored it.
  // Nothing would highlight until the pointer left the item and came back. Replay it against
  // whatever is under the cursor now — re-queried, because switching modes repaints and the
  // node from the event may already be detached.
  if (wasPad) {
    const under = document.elementFromPoint(e.clientX, e.clientY);
    const item = under && under.closest(FOCUSABLE_SEL);
    if (item) item.dispatchEvent(new MouseEvent("mouseenter"));
  }
});

// A click on an item always counts as being on it, even without a preceding move.
window.addEventListener("mousedown", (e) => {
  if (e.target instanceof Element && e.target.closest(FOCUSABLE_SEL)) setPointerOnItem(true);
}, true);

/*
 * A focused tile grows past its own box: scale(1.05) pushes it about 12px up (the transform
 * origin is its bottom edge) and the ring adds 3px all round. Anything less than that much
 * clearance and the highlight is shaved off against the scroller's edge.
 */
const REVEAL_MARGIN = 30;

/**
 * Mark a scroller while it is scrolled away from the top, which is what turns on the top fade.
 * Attaches once per element; the scroller nodes outlive the rows rendered into them.
 */
function watchScrolled(scroller) {
  const mark = () => scroller.classList.toggle("scrolled", scroller.scrollTop > 1);
  if (!scroller.dataset.scrollWatched) {
    scroller.dataset.scrollWatched = "1";
    scroller.addEventListener("scroll", mark, { passive: true });
  }
  mark();
}

/*
 * Menu icons — inline stroke SVG on a 24x24 grid. Drawn rather than pulled from a font or
 * emoji so they stay crisp at 10-foot distance, inherit currentColor (muted normally, accent
 * when focused) and add nothing to load.
 */
const ICONS = {
  search: '<circle cx="10.5" cy="10.5" r="6.5"/><path d="M15.5 15.5 20.5 20.5"/>',
  filter: '<path d="M3 4h18l-7 8.5V20l-4-2v-5.5L3 4z"/>',
  sort: '<path d="M4 6h10M4 12h7M4 18h4"/><path d="M17 5v14M20.5 15.5 17 19l-3.5-3.5"/>',
  sortAsc: '<path d="M4 6h4M4 12h8M4 18h12"/><path d="M18 5v14M21 16l-3 3-3-3"/>',
  sortDesc: '<path d="M4 6h12M4 12h8M4 18h4"/><path d="M18 5v14M21 16l-3 3-3-3"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3.5 2"/>',
  timer: '<path d="M10 2h4M12 8v6l4 2"/><circle cx="12" cy="14" r="8"/>',
  chevronsDown: '<path d="m7 6 5 5 5-5M7 13l5 5 5-5"/>',
  chevronsUp: '<path d="m7 18 5-5 5 5M7 11l5-5 5 5"/>',
  gamepad: '<path d="M7 11h4M9 9v4M15.5 12h.01M18 10h.01"/><rect x="2" y="6" width="20" height="12" rx="5"/>',
  checkCircle: '<circle cx="12" cy="12" r="9"/><path d="m8.5 12 2.5 2.5 4.5-5"/>',
  download: '<path d="M12 3v11M8 10.5l4 4 4-4M4 20h16"/>',
  star: '<path d="M12 3l2.7 5.5 6 .9-4.35 4.2 1.03 6L12 16.8 6.62 19.6l1.03-6L3.3 9.4l6-.9L12 3z"/>',
  eye: '<path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12z"/><circle cx="12" cy="12" r="3"/>',
  eyeOff: '<path d="M9.9 9.9a3 3 0 0 0 4.2 4.2"/><path d="M10.7 5.7A9.6 9.6 0 0 1 12 5.5c6 0 9.5 6.5 9.5 6.5a15 15 0 0 1-2 2.8M6.5 6.9A14.6 14.6 0 0 0 2.5 12S6 18.5 12 18.5a9 9 0 0 0 4.3-1.1"/><path d="m3 3 18 18"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 7.8h.01"/>',
  folder: '<path d="M4 19h16a1.5 1.5 0 0 0 1.5-1.5V9A1.5 1.5 0 0 0 20 7.5h-7.2L11 5H4a1.5 1.5 0 0 0-1.5 1.5v11A1.5 1.5 0 0 0 4 19z"/>',
  folderPlus: '<path d="M4 19h16a1.5 1.5 0 0 0 1.5-1.5V9A1.5 1.5 0 0 0 20 7.5h-7.2L11 5H4a1.5 1.5 0 0 0-1.5 1.5v11A1.5 1.5 0 0 0 4 19z"/><path d="M12 10.5v5M9.5 13h5"/>',
  image: '<rect x="3" y="5" width="18" height="14" rx="1.5"/><circle cx="8.5" cy="10" r="1.5"/><path d="m21 15.5-4.5-4.5L6.5 21"/>',
  refresh: '<path d="M21 12a9 9 0 1 1-2.64-6.36"/><path d="M21 3v6h-6"/>',
  trash: '<path d="M3.5 6.5h17M9 6.5V4h6v2.5M18.5 6.5 17.5 20h-11L5.5 6.5M10 11v5M14 11v5"/>',
  terminal: '<path d="m5 8 4 4-4 4M12 16h7"/><rect x="2" y="4" width="20" height="16" rx="1.5"/>',
  file: '<path d="M14 3H6.5A1.5 1.5 0 0 0 5 4.5v15A1.5 1.5 0 0 0 6.5 21h11a1.5 1.5 0 0 0 1.5-1.5V8l-5-5z"/><path d="M14 3v5h5"/>',
  store: '<path d="M21 12a9 9 0 1 1-2.6-6.35M21 3.5v5h-5"/>',
  /* A cartridge, for anything emulated: the one shape every system from the 2600 to the DS had
     in common, and the thing a ROM file stands for. */
  cartridge: '<path d="M6.5 3.5h11A1.5 1.5 0 0 1 19 5v11.5l-2 2.5H7l-2-2.5V5a1.5 1.5 0 0 1 1.5-1.5z"/>'
           + '<rect x="8" y="6.5" width="8" height="5.5" rx="0.8"/><path d="M9 15.5h6"/>',
  /* A pencil, for renaming. */
  edit: '<path d="M4 20h4.5L19 9.5a1.8 1.8 0 0 0 0-2.6l-1.9-1.9a1.8 1.8 0 0 0-2.6 0L4 15.5V20z"/><path d="m13 6.5 4.5 4.5"/>',
  /* A chip, for a core. */
  chip: '<rect x="6" y="6" width="12" height="12" rx="1.5"/><rect x="9.5" y="9.5" width="5" height="5" rx="0.8"/>'
      + '<path d="M9 2.5v3.5M15 2.5v3.5M9 18v3.5M15 18v3.5M2.5 9h3.5M2.5 15h3.5M18 9h3.5M18 15h3.5"/>',
  /* A folder with a cartridge in it, for a ROM folder. */
  romFolder: '<path d="M4 19h16a1.5 1.5 0 0 0 1.5-1.5V9A1.5 1.5 0 0 0 20 7.5h-7.2L11 5H4a1.5 1.5 0 0 0-1.5 1.5v11A1.5 1.5 0 0 0 4 19z"/>'
           + '<path d="M9.5 11h5v5h-5z"/>',

  /* ---- store marks, for the detail page. Each is the silhouette of the real thing reduced to
     this set's stroke weight: Steam's ringed valve, Epic's arched E, GOG's rounded wordmark
     frame, Xbox's sphere and cross. The store's name is printed beside them either way. ---- */
  steam: '<circle cx="12" cy="12" r="9"/><circle cx="15.2" cy="8.8" r="2.6"/>'
       + '<circle cx="8.2" cy="15.4" r="2.1"/><path d="M3.3 13.4 6.2 14.6M10.1 14.1l3.1-3"/>',
  epic: '<path d="M5 4.6h14v11.1l-7 3.7-7-3.7z"/><path d="M9.6 8.4h4.8M9.6 12h3.6M9.6 15.4h4.8M9.6 8.4v7"/>',
  gog: '<rect x="2.4" y="6" width="19.2" height="12" rx="3.2"/>'
     + '<text x="12" y="15.5" text-anchor="middle" font-size="7.4" font-weight="700"'
     + ' letter-spacing="0.4" fill="currentColor" stroke="none">GOG</text>',
  xbox: '<circle cx="12" cy="12" r="9"/><path d="M6.6 5.9C9 9 13.5 15.3 16.4 18.8M17.4 5.9C15 9 10.5 15.3 7.6 18.8"/>',

  /* ---- overlay-menu actions. Drawn to match the action rather than borrowed from a
     lookalike: an X closes, a moon sleeps, and "switch window" copies the two overlapping
     panes of the Xbox View button, which is the control that does this on a console. ---- */
  play: '<path d="M8 5.4v13.2L18.5 12 8 5.4z"/>',
  x: '<path d="M6.4 6.4l11.2 11.2M17.6 6.4L6.4 17.6"/>',
  viewBtn: '<rect x="2.5" y="8" width="11.5" height="9.5" rx="1.6"/>'
         + '<path d="M7.4 8V6.5A1.5 1.5 0 0 1 8.9 5h10.1a1.5 1.5 0 0 1 1.5 1.5v9a1.5 1.5 0 0 1-1.5 1.5H17"/>',
  moon: '<path d="M20.2 14.8A8.6 8.6 0 0 1 9.2 3.8a8.6 8.6 0 1 0 11 11z"/>',
  keyboard: '<rect x="2" y="5.5" width="20" height="13" rx="2"/>'
          + '<path d="M6 9.5h.01M10 9.5h.01M14 9.5h.01M18 9.5h.01M6 13h.01M10 13h.01M14 13h.01M18 13h.01M8.5 16.5h7"/>',
  pointer: '<path d="M12 2.5v3.2M12 18.3v3.2M2.5 12h3.2M18.3 12h3.2"/>'
         + '<path d="M9.4 9.4l7 2.9-3 1.1-1.1 3-2.9-7z"/>',
  home: '<path d="M3.5 10.4 12 3.8l8.5 6.6V19a1.5 1.5 0 0 1-1.5 1.5h-4v-6h-6v6H5A1.5 1.5 0 0 1 3.5 19v-8.6z"/>',
  apps: '<rect x="3.2" y="3.2" width="7.2" height="7.2" rx="1.6"/><rect x="13.6" y="3.2" width="7.2" height="7.2" rx="1.6"/>'
      + '<rect x="3.2" y="13.6" width="7.2" height="7.2" rx="1.6"/><rect x="13.6" y="13.6" width="7.2" height="7.2" rx="1.6"/>',
  power: '<path d="M12 3.2v8.4"/><path d="M7.3 6.4a7.6 7.6 0 1 0 9.4 0"/>',
  monitor: '<rect x="2.5" y="4" width="19" height="12.5" rx="1.6"/><path d="M8.5 20.5h7M12 16.5v4"/>',
  volume: '<path d="M4 9.4h3.6L12 5.4v13.2L7.6 14.6H4z"/><path d="M15.8 9.6a3.8 3.8 0 0 1 0 4.8M18.6 7.2a7.6 7.6 0 0 1 0 9.6"/>',
  lock: '<rect x="4.4" y="10.4" width="15.2" height="10.1" rx="1.8"/><path d="M8 10.4V7.6a4 4 0 0 1 8 0v2.8"/>',
  bars: '<path d="M4.5 20V9.5M9.5 20V4.5M14.5 20v-7M19.5 20v-4"/>',
  gear: '<circle cx="12" cy="12" r="3.1"/>'
      + '<path d="M12 2.6v2.8M12 18.6v2.8M2.6 12h2.8M18.6 12h2.8M5.3 5.3l2 2M16.7 16.7l2 2M18.7 5.3l-2 2M7.3 16.7l-2 2"/>',

  /* Controller status. A gamepad silhouette with grips reads as a controller at a glance far
     better than the rounded rectangle used elsewhere; the slashed one is the same shape, so
     "connected" and "not connected" are obviously two states of one thing. */
  controller: '<path d="M8.6 8h6.8a5.4 5.4 0 0 1 5.2 4l1.1 4.4a2.4 2.4 0 0 1-4.4 1.8L15.6 16H8.4l-1.7 2.2a2.4 2.4 0 0 1-4.4-1.8L3.4 12A5.4 5.4 0 0 1 8.6 8z"/>'
            + '<path d="M6.6 11.4v2.2M5.5 12.5h2.2M15.4 11.6h.01M17.6 13.4h.01"/>',
  controllerOff: '<path d="M8.6 8h6.8a5.4 5.4 0 0 1 5.2 4l1.1 4.4a2.4 2.4 0 0 1-4.4 1.8L15.6 16H8.4l-1.7 2.2a2.4 2.4 0 0 1-4.4-1.8L3.4 12A5.4 5.4 0 0 1 8.6 8z"/>'
               + '<path d="m2.6 2.6 18.8 18.8"/>',
};

function iconSvg(name) {
  const body = ICONS[name];
  if (!body) return "";
  return `<svg class="ov-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" ` +
         `stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${body}</svg>`;
}

/* ============================== buttons, by controller ==============================
 *
 * Every hint in the launcher is drawn as the button itself, never named in a badge: the A on an
 * Xbox pad is a green disc, on a DualSense it is a cross, on a Switch Pro controller it is the
 * B -- which sits where an Xbox A does, and the host maps by position -- on a pad we know nothing
 * about it is the bottom of a four-button diamond, and on a keyboard it is the Enter key.
 *
 * The launcher's own button names (A, B, X, Y, LB, RB, LT, RT, View, Menu, LS, RS, Guide) stay as
 * they are in the code and in settings.json. Only what is DRAWN changes, and it changes with the
 * last thing the user touched: a press on a pad carries that pad's family with it, a keypress or a
 * mouse click switches to the keyboard, and the whole page is repainted through its slots.
 *
 * Two families are tracked. `inputFamily` is what the legends draw. `padFamily` is the last GAMEPAD
 * seen, and the rows in Settings that name gamepad buttons always draw that one -- "Left click
 * button: Enter" would be nonsense.
 */
let inputFamily = "xbox";
let padFamily = "xbox";

/* What each button is called on each pad, for the places that have to say it in words: a
   confirm dialog's body, a settings hint. The Switch is by position, like the host's map. */
const BTN_NAMES = {
  xbox: { A: "A", B: "B", X: "X", Y: "Y", LB: "LB", RB: "RB", LT: "LT", RT: "RT", View: "View", Menu: "Menu", LS: "LS", RS: "RS", Guide: "the Xbox button" },
  playstation: { A: "Cross", B: "Circle", X: "Square", Y: "Triangle", LB: "L1", RB: "R1", LT: "L2", RT: "R2", View: "Create", Menu: "Options", LS: "L3", RS: "R3", Guide: "the PS button" },
  switch: { A: "B", B: "A", X: "Y", Y: "X", LB: "L", RB: "R", LT: "ZL", RT: "ZR", View: "−", Menu: "+", LS: "the left stick", RS: "the right stick", Guide: "Home" },
  generic: { A: "the bottom face button", B: "the right face button", X: "the left face button", Y: "the top face button", LB: "L1", RB: "R1", LT: "L2", RT: "R2", View: "Select", Menu: "Start", LS: "L3", RS: "R3", Guide: "Home" },
  keyboard: { A: "Enter", B: "Esc", X: "X", Y: "Y", LB: "[", RB: "]", LT: "LT", RT: "RT", View: "/", Menu: "M", LS: "LS", RS: "RS", Guide: "Guide" },
};

/* settings.json spells two of them the XInput way. */
function canonBtn(btn) {
  return btn === "Start" ? "Menu" : btn === "Back" ? "View" : btn === "Xbox" || btn === "PS" ? "Guide" : btn;
}

function btnName(btn, family) {
  const names = BTN_NAMES[family || inputFamily] || BTN_NAMES.xbox;
  return names[canonBtn(btn)] || btn;
}

/* "LS + RS" in words, for the pad in hand: "L3 + R3" on a DualSense. */
function comboName(combo, family) {
  if (!combo || combo === "Off") return "Off";
  return combo.split("+").map(p => btnName(p.trim(), family)).join(" + ");
}

/* ---- the drawings ----
   Each is an inline svg 40 units tall; the width varies with the shape and the page sizes them by
   height, so a pill and a disc sit on one baseline. Brand colours are literal here, like the
   accent swatches: the point is to look like the button. */
const SVG_FONT = "Manrope, Segoe UI, system-ui, sans-serif";
const SVG_MONO = "IBM Plex Mono, Consolas, monospace";
const DISC_DARK = "#26262C";
const DISC_RING = "rgba(255,255,255,0.28)";

function svgIcon(w, body) {
  return `<svg class="btn-icon" viewBox="0 0 ${w} 40" width="${w}" height="40" aria-hidden="true">${body}</svg>`;
}
function svgText(x, t, size, fill, opts = {}) {
  return `<text x="${x}" y="20.5" text-anchor="middle" dominant-baseline="central" ` +
    `font-family="${opts.mono ? SVG_MONO : SVG_FONT}" font-size="${size}" font-weight="${opts.weight || 700}" fill="${fill}">${esc(t)}</text>`;
}
/* A face button: a disc with a letter or a shape on it. */
function disc(fill, inner, ring) {
  return svgIcon(40, `<circle cx="20" cy="20" r="18" fill="${fill}"${ring ? ` stroke="${ring}" stroke-width="1.5"` : ""}/>${inner}`);
}
/* A shoulder, a trigger, Options, Start: a pill with its name on it. */
function pill(label) {
  const w = Math.max(44, 20 + label.length * 11);
  return svgIcon(w, `<rect x="1.5" y="6.5" width="${w - 3}" height="27" rx="13.5" fill="var(--ink)" fill-opacity="0.08" ` +
    `stroke="var(--ink)" stroke-opacity="0.45" stroke-width="1.5"/>` +
    svgText(w / 2, label, label.length > 3 ? 12 : 15, "var(--ink)", { weight: 600 }));
}
/* A key on the keyboard: a cap with the key's name and a shade along its bottom edge. */
function keycap(label) {
  const w = Math.max(40, 22 + label.length * 10.5);
  return svgIcon(w, `<rect x="1.5" y="3.5" width="${w - 3}" height="33" rx="7" fill="var(--ink)" fill-opacity="0.12" ` +
    `stroke="var(--ink)" stroke-opacity="0.5" stroke-width="1.5"/>` +
    `<rect x="6" y="30.5" width="${w - 12}" height="3" rx="1.5" fill="var(--bg-deep)" fill-opacity="0.55"/>` +
    svgText(w / 2, label, label.length > 3 ? 12.5 : 15, "var(--ink)", { weight: 600, mono: true }));
}
/* The D-pad, with the arms that matter lit: "v" for up and down, "h" for left and right. */
function dpad(arms) {
  const on = (a) => (arms === "all" || arms === a ? 0.95 : 0.26);
  return svgIcon(40,
    `<rect x="15" y="2" width="10" height="12" rx="2" fill="currentColor" fill-opacity="${on("v")}"/>` +
    `<rect x="15" y="26" width="10" height="12" rx="2" fill="currentColor" fill-opacity="${on("v")}"/>` +
    `<rect x="2" y="15" width="12" height="10" rx="2" fill="currentColor" fill-opacity="${on("h")}"/>` +
    `<rect x="26" y="15" width="12" height="10" rx="2" fill="currentColor" fill-opacity="${on("h")}"/>` +
    `<rect x="14" y="14" width="12" height="12" fill="currentColor" fill-opacity="0.26"/>`);
}
/* A pad we have no names for: the four face buttons as a diamond, the one meant filled in. */
function diamond(pos) {
  const dots = { top: [20, 7], right: [33, 20], bottom: [20, 33], left: [7, 20] };
  return svgIcon(40, Object.entries(dots).map(([k, [x, y]]) =>
    `<circle cx="${x}" cy="${y}" r="5.5" fill="currentColor" fill-opacity="${k === pos ? 1 : 0.2}" ` +
    `stroke="currentColor" stroke-opacity="0.55" stroke-width="1.2"/>`).join(""));
}

const GLYPH = {
  // Xbox's View: two overlapping panes. Menu: three bars. Both are what is printed on the pad.
  view: `<rect x="10" y="15.5" width="12" height="10" rx="1.6" fill="none" stroke="#fff" stroke-width="2"/>` +
        `<path d="M15.5 15.5V13a1.5 1.5 0 0 1 1.5-1.5h11.5a1.5 1.5 0 0 1 1.5 1.5v9.5a1.5 1.5 0 0 1-1.5 1.5H26" fill="none" stroke="#fff" stroke-width="2" stroke-linecap="round"/>`,
  lines: `<path d="M12.5 14h15M12.5 20h15M12.5 26h15" stroke="#fff" stroke-width="2.4" stroke-linecap="round"/>`,
  nexus: `<circle cx="20" cy="20" r="11" fill="none" stroke="#fff" stroke-width="2.2"/>` +
         `<path d="M13.5 13c4.2 2.6 9.2 8.8 13 14M26.5 13c-4.2 2.6-9.2 8.8-13 14" fill="none" stroke="#fff" stroke-width="2.2" stroke-linecap="round"/>`,
  cross: `<path d="M13.5 13.5l13 13M26.5 13.5l-13 13" stroke="#7C9BE6" stroke-width="3.2" stroke-linecap="round"/>`,
  circle: `<circle cx="20" cy="20" r="7.5" fill="none" stroke="#E0554F" stroke-width="3.2"/>`,
  square: `<rect x="12.5" y="12.5" width="15" height="15" rx="1.5" fill="none" stroke="#E68AC0" stroke-width="3.2"/>`,
  triangle: `<path d="M20 11.5 28.8 26.5H11.2z" fill="none" stroke="#63C58F" stroke-width="3.2" stroke-linejoin="round"/>`,
  // The two small buttons either side of the DualSense's touchpad, drawn the way the pad prints
  // them and the way the common icon packs do: the slanted pill of the button itself with its
  // mark above it -- three short rays for Create, three bars for Options. Each leans towards
  // the touchpad, so the two lean opposite ways.
  sonyCreate: `<rect x="15" y="17" width="10" height="21" rx="5" fill="var(--ink)" transform="rotate(14 20 27.5)"/>` +
              `<path d="M20 13V4.5M17.8 13.6 13.2 6.5M22.2 13.6l4.6-7.1" fill="none" stroke="var(--ink)" stroke-width="2.4" stroke-linecap="round"/>`,
  sonyOptions: `<rect x="15" y="17" width="10" height="21" rx="5" fill="var(--ink)" transform="rotate(-14 20 27.5)"/>` +
               `<path d="M15 5.5h10M15 9.5h10M15 13.5h10" fill="none" stroke="var(--ink)" stroke-width="2.2" stroke-linecap="round"/>`,
  minus: `<path d="M12 20h16" stroke="#fff" stroke-width="3" stroke-linecap="round"/>`,
  plus: `<path d="M12 20h16M20 12v16" stroke="#fff" stroke-width="3" stroke-linecap="round"/>`,
  home: `<path d="M11 19.5 20 11.5l9 8V28a1 1 0 0 1-1 1h-5.5v-6h-5v6H12a1 1 0 0 1-1-1z" fill="none" stroke="#fff" stroke-width="2" stroke-linejoin="round"/>`,
};

const BUTTON_ART = {
  xbox: {
    A: () => disc("#3AA03C", svgText(20, "A", 21, "#fff")),
    B: () => disc("#D3433C", svgText(20, "B", 21, "#fff")),
    X: () => disc("#3C7CD3", svgText(20, "X", 21, "#fff")),
    Y: () => disc("#E2B128", svgText(20, "Y", 21, "#101012")),
    LB: () => pill("LB"), RB: () => pill("RB"), LT: () => pill("LT"), RT: () => pill("RT"),
    LS: () => pill("LS"), RS: () => pill("RS"),
    View: () => disc(DISC_DARK, GLYPH.view, DISC_RING),
    Menu: () => disc(DISC_DARK, GLYPH.lines, DISC_RING),
    Guide: () => disc("#107C10", GLYPH.nexus),
  },
  playstation: {
    A: () => disc(DISC_DARK, GLYPH.cross, DISC_RING),
    B: () => disc(DISC_DARK, GLYPH.circle, DISC_RING),
    X: () => disc(DISC_DARK, GLYPH.square, DISC_RING),
    Y: () => disc(DISC_DARK, GLYPH.triangle, DISC_RING),
    LB: () => pill("L1"), RB: () => pill("R1"), LT: () => pill("L2"), RT: () => pill("R2"),
    LS: () => pill("L3"), RS: () => pill("R3"),
    View: () => svgIcon(40, GLYPH.sonyCreate),
    Menu: () => svgIcon(40, GLYPH.sonyOptions),
    Guide: () => disc(DISC_DARK, svgText(20, "PS", 13, "#fff"), DISC_RING),
  },
  // By position: the launcher's "A" is the bottom button, which Nintendo prints a B on.
  switch: {
    A: () => disc("#1B1B1F", svgText(20, "B", 20, "#fff"), DISC_RING),
    B: () => disc("#1B1B1F", svgText(20, "A", 20, "#fff"), DISC_RING),
    X: () => disc("#1B1B1F", svgText(20, "Y", 20, "#fff"), DISC_RING),
    Y: () => disc("#1B1B1F", svgText(20, "X", 20, "#fff"), DISC_RING),
    LB: () => pill("L"), RB: () => pill("R"), LT: () => pill("ZL"), RT: () => pill("ZR"),
    LS: () => pill("LS"), RS: () => pill("RS"),
    View: () => disc("#1B1B1F", GLYPH.minus, DISC_RING),
    Menu: () => disc("#1B1B1F", GLYPH.plus, DISC_RING),
    Guide: () => disc("#1B1B1F", GLYPH.home, DISC_RING),
  },
  generic: {
    A: () => diamond("bottom"), B: () => diamond("right"), X: () => diamond("left"), Y: () => diamond("top"),
    LB: () => pill("L1"), RB: () => pill("R1"), LT: () => pill("L2"), RT: () => pill("R2"),
    LS: () => pill("L3"), RS: () => pill("R3"),
    View: () => pill("SELECT"), Menu: () => pill("START"), Guide: () => pill("HOME"),
  },
  keyboard: {
    A: () => keycap("Enter"), B: () => keycap("Esc"), X: () => keycap("X"), Y: () => keycap("Y"),
    LB: () => keycap("["), RB: () => keycap("]"), LT: () => keycap("LT"), RT: () => keycap("RT"),
    LS: () => keycap("LS"), RS: () => keycap("RS"),
    View: () => keycap("/"), Menu: () => keycap("M"), Guide: () => keycap("Guide"),
  },
};

/** The picture of a button, for a family (the current one by default). */
function btnIcon(btn, family) {
  const fam = family || inputFamily;
  btn = canonBtn(btn);
  if (btn === "DpadV" || btn === "DpadH" || btn === "Dpad") {
    if (fam === "keyboard")
      return btn === "DpadV" ? keycap("↑") + keycap("↓") : btn === "DpadH" ? keycap("←") + keycap("→") : keycap("↑↓←→");
    return dpad(btn === "DpadV" ? "v" : btn === "DpadH" ? "h" : "all");
  }
  const art = BUTTON_ART[fam] || BUTTON_ART.xbox;
  const draw = art[btn] || BUTTON_ART.xbox[btn];
  return draw ? draw() : pill(btn);
}

/*
 * A slot is where a button is drawn. It carries the button's name, so paintButtons can redraw
 * every one on the page when the pad in hand changes without anything being re-rendered.
 * `padOnly` marks a slot that is about a gamepad whatever is in use: the button rows in Settings.
 */
function slot(btn, padOnly) {
  return `<span class="btn-slot" data-btn="${esc(canonBtn(btn))}"${padOnly ? ' data-pad=""' : ""}>` +
    btnIcon(btn, padOnly ? padFamily : inputFamily) + `</span>`;
}

/** "LS + RS" as pictures, for a settings value. */
function comboHtml(combo) {
  if (!combo || combo === "Off") return "Off";
  return `<span class="combo">` + combo.split("+").map(p => slot(p.trim(), true)).join(`<span class="combo-plus">+</span>`) + `</span>`;
}

/** Text with [[A]]-style references drawn as buttons. Escapes the text first, so a title cannot smuggle markup in. */
function hintHtml(text) {
  return esc(text).replace(/\[\[(\w+)\]\]/g, (_, b) => slot(b, true));
}

/** Redraw every slot for the families now in use. */
function paintButtons(root) {
  (root || document).querySelectorAll("[data-btn]").forEach(el => {
    el.innerHTML = btnIcon(el.dataset.btn, "pad" in el.dataset ? padFamily : inputFamily);
  });
}

/*
 * The last thing the user touched. A pad announces its family with every press and whenever the
 * host sees it picked up; a keypress or a mouse click says "keyboard". Mouse MOVEMENT does not
 * count: the left stick moves the real Windows pointer, so a mousemove can be the pad.
 */
function setInputFamily(family) {
  if (!family || !BTN_NAMES[family]) return;
  if (family !== "keyboard") padFamily = family;
  if (inputFamily === family) return;
  inputFamily = family;
  document.body.dataset.input = family;
  paintButtons(document);
  // Settings names buttons in its hints and warnings, and those are words, not slots.
  if (view === "settings") renderSettings();
}

/** One entry of a legend: the button, then what it does. Clickable, so a mouse can press it. */
function legendItem(btn, label) {
  const press = /^Dpad/.test(btn) ? "" : ` data-press="${esc(canonBtn(btn))}"`;
  return `<div class="legend-item"${press}>${slot(btn)}<span>${esc(label)}</span></div>`;
}

const LIBRARY_LEGEND = [["A", "Launch"], ["X", "Filter"], ["Y", "Options"], ["View", "Search"], ["Menu", "Settings"]];

function renderLibraryLegend() {
  const el = $("libraryFoot");
  if (el) el.innerHTML = foot(...LIBRARY_LEGEND);
}

function renderDetailLegend(g) {
  const el = $("detailFoot");
  if (el) el.innerHTML = foot(["A", "Select"], ["B", "Back"], ["X", g && g.favorite ? "Unfavorite" : "Favorite"]);
}

// A click on a legend entry is that button. Delegated, because footers are rebuilt constantly.
document.addEventListener("click", (e) => {
  const item = e.target instanceof Element ? e.target.closest(".legend-item[data-press]") : null;
  if (!item || inputOpen) return;
  handleInput(item.dataset.press, "mouse");
});

/**
 * Shared renderer for every overlay menu, so the game options, manage, collection and
 * confirm menus all read like the filter menu. Items are
 * { cat } headers or { label, icon, sub, checked, radio, danger, summary, action }.
 *
 * All five render through here, so marking rows up once puts every one of them on the
 * spatial engine. `idx` stays the caller's own selection -- these lists have real index
 * semantics -- but the highlight and the movement come from the DOM, so a theme can lay a
 * menu out as a row or a grid without any of the callers knowing.
 */
function renderMenu(listEl, footEl, items, idx, footHtml, onHover, onClick) {
  // Every step rebuilds the list, and emptying it snapped the scroll back to the top -- so on a
  // menu longer than its box the smooth reveal restarted from 0 on every press and the last rows
  // could go unseen. Hold the position across the rebuild; revealFocus moves it from there.
  const keepTop = listEl.scrollTop;
  listEl.innerHTML = "";
  items.forEach((r, i) => {
    if (r.cat) {
      const c = document.createElement("div");
      c.className = "ov-cat";
      c.textContent = r.cat;
      listEl.appendChild(c);
      return;
    }
    const el = document.createElement("div");
    el.className = "ov-row" + (r.danger ? " danger" : "") + (r.thumb ? " has-thumb" : "");
    el.dataset.focusable = "";
    el.dataset.focusKey = "row:" + i;
    el.dataset.rowIndex = i;
    let right = "";
    if (r.summary !== undefined) right = `<div class="ov-value"><span class="ov-summary">${esc(r.summary)}</span><span class="arrow">▸</span></div>`;
    else if (r.checked !== undefined) right = `<span class="ov-check${r.checked ? "" : " off"}">${r.checked ? (r.star ? "★" : r.radio ? "●" : "✓") : "○"}</span>`;
    // A picture of the window replaces the icon rather than joining it: the icon was standing in
    // for exactly this, and showing both says the same thing twice.
    const lead = r.thumb
      ? `<div class="ov-thumb${r.thumbIsIcon ? " is-icon" : ""}" style="background-image:url('${r.thumb}')"></div>`
      : iconSvg(r.icon);
    // The subtitle goes UNDER the label, never beside it. Side by side, a long subtitle took the
    // row's width and squeezed the label down to its first two letters -- "Install" read "In…".
    const text = r.sub
      ? `<span class="ov-text"><span class="ov-name">${esc(r.label ?? r.name)}</span><span class="ov-sub">${esc(r.sub)}</span></span>`
      : `<span>${esc(r.label ?? r.name)}</span>`;
    if (r.sub) el.classList.add("two-line");
    el.innerHTML = `<div class="ov-label">${lead}${text}</div>${right}`;
    el.addEventListener("mouseenter", () => { if (hoverEnabled()) onHover(i); });
    el.addEventListener("click", () => onClick(i));
    listEl.appendChild(el);
  });
  if (footEl) footEl.innerHTML = footHtml;
  listEl.scrollTop = keepTop;
  watchOverflow(listEl);

  // The caller owns the index, so point the scope's highlight at whatever it chose.
  const scope = listEl.closest("[data-focus-scope]");
  setScopeKey(scope, "row:" + idx);
  const cur = focusEl(scope);
  const show = focusVisible();
  Nav.focusables(scope).forEach(el => el.classList.toggle("focused", show && el === cur));
  if (cur && show) revealFocus(cur);
}

/* A menu taller than its box fades at whichever edge has more beyond it. The scrollbar is hidden
   on purpose, and without this a list that ran past its box simply looked complete. */
function markOverflow(el) {
  el.classList.toggle("more-above", el.scrollTop > 2);
  el.classList.toggle("more-below", el.scrollTop + el.clientHeight < el.scrollHeight - 2);
}
function watchOverflow(el) {
  if (!el.dataset.overflowWatched) {
    el.dataset.overflowWatched = "1";
    el.addEventListener("scroll", () => markOverflow(el), { passive: true });
  }
  markOverflow(el);
}

/** Step an overlay's index by moving through the DOM, so the list need not be a column. */
function menuStep(dir, idx, count) {
  if (!navMove(dir)) return idx;
  const el = focusEl();
  const next = el && el.dataset.rowIndex !== undefined ? parseInt(el.dataset.rowIndex, 10) : idx;
  return Math.max(0, Math.min(count - 1, next));
}

/** Standard footer hints: [button, label] pairs, drawn for the pad in hand. */
function foot(...pairs) {
  return pairs.map(([btn, label]) => legendItem(btn, label)).join("");
}

/* overlays */
let filterOpen = false, filterIdx = 0;
let manageOpen = false, manageIdx = 0;
let collectOpen = false, collectIdx = 0;
let confirmState = null, confirmIdx = 0; // { title, onYes }
let inputOpen = false, inputConfirm = null;
let guideOpen = false;

let settingsIdx = 0;
let saveTimer = null;

/* Kept only for the carousel geometry (how many tiles fit, how far it can slide).
   Focus no longer indexes into either of these. */
let contItems = [];
let gridRows = [];

/* ============================== helpers ============================== */

const $ = (id) => document.getElementById(id);
const esc = (s) => String(s ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));

function artUrl(name) {
  if (!name) return null;
  return HOST ? `https://consolify.data/covers/${encodeURIComponent(name)}` : name;
}

function coverUrl(g) { return artUrl(g.coverFile); }

/* The ~16:9 tile art. Falls back to the portrait cover so a landscape tile is never empty, but
   never to the hero: a 3:1 backdrop centre-cropped into a tile shows background, not the game. */
function bannerUrl(g) { return artUrl(g.bannerFile) || coverUrl(g); }

/* The wide backdrop. Falls back to the tile, which is at least landscape, and then stops.
   NOT to the portrait cover: 2:3 art hung across a screen at its own aspect is a tall narrow
   column of box art, and because the backdrop follows the highlight, walking along a row of
   tiles made the picture behind them change shape every time it landed on a game with no hero.
   A flat colour is a better answer than a cover in a slot that is not for covers. */
function heroUrl(g) { return artUrl(g.heroFile) || artUrl(g.bannerFile); }

/*
 * Everything that could go behind a whole screen, best first.
 *
 * The hero leads. It used to be second, on the theory that IGDB's 1920x1080 artwork is the shape
 * of the screen and therefore fills one with nothing cropped -- which is true about its SHAPE and
 * says nothing about the picture. IGDB's artworks are user uploads in no particular order, and
 * the first one is as likely to be a flat background plate as the game: Persona 3's was a blue
 * diagonal, two floating leaves and 31 KB of JPEG, against 873 KB of key art in Steam's hero.
 * Steam's library_hero is curated, it is the same picture the store shows, and the band-over-a-
 * blurred-bed treatment handles its 3.1:1 perfectly well. So the 16:9 slot is now the fallback,
 * which is what it is actually good for: games with no hero at all.
 *
 * A LIST rather than one URL because a name in the library can outlive the file it points at, and
 * the right answer to a picture that will not load is the next one down, not a flat colour.
 */
function backdropUrls(g) {
  return [artUrl(g.heroFile), artUrl(g.backdropFile), artUrl(g.bannerFile)].filter(Boolean);
}

function backdropUrl(g) { return backdropUrls(g)[0] || null; }

function logoUrl(g) { return artUrl(g.logoFile); }

/* Art the user picked by hand. The host writes it under a "custom_" name precisely so nothing
   else can ever write that name, which makes the prefix a reliable answer to "did somebody
   choose this?" -- and therefore to "is there anything to undo?". */
function isCustomArt(file) { return typeof file === "string" && file.startsWith("custom_"); }

function hashHue(str) {
  let h = 0;
  for (let i = 0; i < str.length; i++) h = (h * 31 + str.charCodeAt(i)) >>> 0;
  return h % 360;
}

function initials(title) {
  const words = title.split(/\s+/).filter(Boolean);
  return (words.length >= 2 ? words[0][0] + words[1][0] : title.slice(0, 2)).toUpperCase();
}

/*
 * Cover art loads through a small queue.
 *
 * WebView2's virtual-host mapping drops requests when a whole screen's worth of tiles
 * fire at once: with ~22 images requested simultaneously from https://consolify.data only
 * the first handful resolved and the rest failed outright, leaving most tiles blank
 * even though every file was present and valid. Capping concurrency and retrying with
 * a cache-busting suffix makes the load reliable; anything still failing after its
 * retries falls back to the initials placeholder instead of an empty tile.
 */
const IMG_MAX_CONCURRENT = 6;
const IMG_MAX_ATTEMPTS = 4;
const imgQueue = [];
let imgActive = 0;

function queueArt(url, done) {
  imgQueue.push({ url, done, attempt: 0 });
  pumpImgQueue();
}

function pumpImgQueue() {
  while (imgActive < IMG_MAX_CONCURRENT && imgQueue.length) {
    const job = imgQueue.shift();
    imgActive++;
    const probe = new Image();
    probe.onload = () => {
      imgActive--;
      // The natural size goes back with the URL so the caller can decide whether this picture
      // survives a cover-crop into its box.
      job.done(probe.src, probe.naturalWidth, probe.naturalHeight);
      pumpImgQueue();
    };
    probe.onerror = () => {
      imgActive--;
      job.attempt++;
      if (job.attempt < IMG_MAX_ATTEMPTS) {
        setTimeout(() => { imgQueue.push(job); pumpImgQueue(); }, 80 * job.attempt);
      } else {
        job.done(null);
      }
      pumpImgQueue();
    };
    // A fresh query string on retry also sidesteps any negatively-cached response.
    probe.src = job.attempt ? `${job.url}?r=${job.attempt}` : job.url;
  }
}

function paintPlaceholder(g, el) {
  unbedArt(el);
  const h = hashHue(g.title);
  el.style.background = `linear-gradient(150deg, hsl(${h},16%,15%) 0%, hsl(${(h + 40) % 360},22%,9%) 100%)`;
  el.classList.add("ph");
  el.innerHTML = `<span>${esc(initials(g.title))}</span>`;
}

/*
 * How far a picture may be off its box before cover-cropping does visible damage.
 *
 * Landscape tile art has the game name burnt into it, running close to the edges. Steam capsules
 * are 1.75:1 and land inside this; header.jpg is 2.14:1 and does not, and cover was shaving 18%
 * off its width -- REANIMAL lost the end of its own name.
 *
 * Only landscape boxes get the treatment. Portrait box art is drawn to be cropped and always has
 * been cropped here, so a portrait tile keeps cover and looks exactly as it did.
 */
const ART_FIT_TOLERANCE = 1.02;

/*
 * The aspect of the box a background is actually painted into.
 *
 * Not clientWidth/clientHeight: `background-origin: content-box` -- which is what holds tile art
 * clear of the rounded corners -- makes `cover` size against the CONTENT box, while clientWidth
 * includes the padding. Measuring the padding box made the continue row look 1.79:1 when it was
 * really 1.86:1, so a 1.75:1 capsule read as a near-perfect fit and was cropped 6% narrower --
 * a strip off each side, taking the edge of the logo with it.
 */
function artBoxAspect(el) {
  const cs = getComputedStyle(el);
  const px = (v) => parseFloat(v) || 0;
  const inset = cs.backgroundOrigin === "content-box";
  const w = el.clientWidth - (inset ? px(cs.paddingLeft) + px(cs.paddingRight) : 0);
  const h = el.clientHeight - (inset ? px(cs.paddingTop) + px(cs.paddingBottom) : 0);
  return w > 0 && h > 0 ? w / h : NaN;
}

function artFit(el, w, h) {
  if (!w || !h) return "cover";                     // never measured; keep the old behaviour
  const box = artBoxAspect(el);
  if (!isFinite(box) || box <= 1.2) return "cover"; // portrait or square: crop as before
  const off = (w / h) / box;
  return (off > 1 ? off : 1 / off) > ART_FIT_TOLERANCE ? "contain" : "cover";
}

/**
 * `fit` forces the sizing for art that is scenery rather than a tile. A full-bleed backdrop must
 * always cover: it has no edges of its own to protect, and letterboxing one leaves bars down the
 * screen -- which is what artFit did to the detail page, whose 3:1 hero is nowhere near its 16:9
 * box. Only tiles, whose art carries the game's name near the edges, are worth fitting.
 */
function applyArt(g, el, url, fit) {
  if (!url) { paintPlaceholder(g, el); setArtAspect(el, 0, 0); return; }
  queueArt(url, (src, w, h) => {
    if (!el.isConnected) return;          // tile was re-rendered while loading
    if (!src) { paintPlaceholder(g, el); setArtAspect(el, 0, 0); return; }
    setArtAspect(el, w, h);
    const size = fit || artFit(el, w, h);

    if (size === "contain") { bedArt(el, src); return; }

    unbedArt(el);
    el.style.backgroundImage = `url('${src}')`;
    el.style.backgroundSize = size;
    el.style.backgroundRepeat = "no-repeat";
  });
}

/*
 * Fitted art does not reach every edge of its box, and what it leaves behind should not be two
 * black bars. The box is filled with a blurred, dimmed copy of the same picture and the sharp one
 * laid over it -- the same thing the library backdrop does with a hero that does not fit the
 * screen, and the only answer that neither crops the art nor leaves a hole.
 *
 * Both layers are children rather than one being the element's own background, because a child
 * always paints ABOVE its parent's background and never below it -- and because a filter on the
 * element would blur the sharp layer along with the bed.
 *
 * Only built when it is actually needed, so the great majority of tiles, whose art fills the box,
 * carry no extra layer and no blur.
 */
function bedArt(el, src) {
  el.style.backgroundImage = "none";
  layer(el, "art-bed").style.backgroundImage = `url('${src}')`;
  layer(el, "art-top").style.backgroundImage = `url('${src}')`;
}

function unbedArt(el) {
  el.querySelectorAll(":scope > .art-bed, :scope > .art-top").forEach(n => n.remove());
}

/** One of the two layers, made on demand. Appended in order, which is paint order. */
function layer(el, cls) {
  let node = el.querySelector(":scope > ." + cls);
  if (!node) {
    node = document.createElement("div");
    node.className = cls;
    el.appendChild(node);
  }
  return node;
}

/*
 * Publish the shape of the picture that landed, so CSS can cut the box to it.
 *
 * The alternative is to guess, and both places that show a full-width picture were guessing:
 * the detail page and the library backdrop were sized for Steam's 3.1:1 hero, so anything else
 * in that slot -- IGDB hands back 16:9 artwork -- was blown up and cropped to fit a box chosen
 * for a different picture. A box cut to the art's own aspect has nothing to crop.
 */
function setArtAspect(el, w, h) {
  if (w && h) el.style.setProperty("--art-aspect", (w / h).toFixed(4));
  else el.style.removeProperty("--art-aspect");
}

function fmtPlaytime(min) {
  if (!min || min < 1) return "—";
  const h = Math.floor(min / 60), m = Math.round(min % 60);
  return h > 0 ? `${h}h ${String(m).padStart(2, "0")}m` : `${m}m`;
}

function fmtSize(bytes) {
  if (!bytes) return "—";
  const gb = bytes / (1024 ** 3);
  return gb >= 1 ? `${gb.toFixed(1)} GB` : `${(bytes / 1024 ** 2).toFixed(0)} MB`;
}

function fmtLastPlayed(iso) {
  if (!iso) return "—";
  const d = new Date(iso);
  if (isNaN(d)) return "—";
  const now = new Date();
  const time = `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`;
  const day0 = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const dayD = new Date(d.getFullYear(), d.getMonth(), d.getDate());
  const diff = Math.round((day0 - dayD) / 86400000);
  if (diff === 0) return `Today, ${time}`;
  if (diff === 1) return `Yesterday, ${time}`;
  if (diff < 365) return `${d.getDate()} ${d.toLocaleString("en", { month: "short" })}, ${time}`;
  return d.toLocaleDateString();
}

function shortMeta(g) {
  const played = g.playtimeMinutes >= 60 ? ` · ${Math.round(g.playtimeMinutes / 60)}h` : "";
  return g.platform + played;
}

/* A game that was never installed has no size to report: the Steam account lists what you own,
   not how big it is. Printing the dash fmtSize gives for zero read as a broken field. */
function uninstalledMeta(g) {
  return g.sizeBytes ? `${g.platform} · ${fmtSize(g.sizeBytes)} · not installed`
                     : `${g.platform} · not installed`;
}

function gameById(id) { return S.games.find(g => g.id === id) || null; }

function toast(msg, ms = 2600) {
  const t = $("toast");
  t.textContent = msg;
  t.classList.add("show");
  clearTimeout(t._timer);
  t._timer = setTimeout(() => t.classList.remove("show"), ms);
}

/* ============================== stage / clock / battery ============================== */

function fitStage() {
  const sc = Math.min(window.innerWidth / 1920, window.innerHeight / 1080);
  $("stage").style.transform = `translate(-50%, -50%) scale(${sc})`;
}
window.addEventListener("resize", fitStage);

function tickClock() {
  const now = new Date();
  const days = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];
  const months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
  let h = now.getHours();
  const suffix = h >= 12 ? "PM" : "AM";
  h = h % 12 || 12;                       // 0 and 12 both display as 12
  const time = `${h}:${String(now.getMinutes()).padStart(2, "0")} ${suffix}`;
  const date = `${days[now.getDay()]} ${now.getDate()} ${months[now.getMonth()]}`;
  document.querySelectorAll(".clock").forEach(el => el.textContent = `${date} · ${time}`);
}
setInterval(tickClock, 10000);

/* Battery indicator: only shown for controllers that actually run on a battery
   (wireless pads report ALKALINE/NIMH); wired pads and no-pad show nothing. */
/**
 * Controller status: a pad icon, a battery bar and the percentage.
 *
 * `percent` is null when nothing can tell us the real charge -- XInput only reports four coarse
 * levels, and over Bluetooth often reports no battery at all. In that case the bar is drawn at
 * the coarse level and no number is shown, rather than inventing a precise-looking figure.
 */
function updateBattery(m) {
  const el = $("padBattery");
  el.classList.remove("low", "charging", "off");

  // The message's own `present` decides, not S.padConnected. The pad is usually detected while
  // the WebView is still starting, so that first "connected" push has no bridge to travel over
  // and is lost -- gating on it left a connected controller showing as missing.
  if (!m || m.present === false) {
    el.innerHTML = iconSvg("controllerOff");
    el.classList.add("off");
    el.title = "No controller connected";
    return;
  }
  S.padConnected = true;

  const pct = typeof m.percent === "number" ? Math.max(0, Math.min(100, m.percent)) : null;
  // Coarse levels are 0..3; -1 means even that is unknown, so show an empty-looking bar.
  const fill = pct !== null ? pct
             : m.level >= 0 ? [12, 38, 68, 100][Math.min(m.level, 3)]
             : 0;

  if (m.charging) el.classList.add("charging");
  else if (pct !== null && pct <= 15) el.classList.add("low");
  else if (pct === null && m.level === 0) el.classList.add("low");

  el.innerHTML =
    iconSvg("controller") +
    `<span class="batt"><span class="batt-fill" style="width:${fill}%"></span></span>` +
    (m.charging ? `<span class="batt-pct">${pct !== null ? pct + "%" : ""}⚡</span>`
                : pct !== null ? `<span class="batt-pct">${pct}%</span>` : "");
  el.title = m.charging ? "Controller charging"
           : pct !== null ? `Controller battery ${pct}%`
           : "Controller connected; battery level unavailable";
}

/* ============================== backdrop ============================== */

let bdFront = "bdA";
let bdCurrentKey = null;

/*
 * The backdrop follows the highlight, but not tile by tile during a fast run.
 *
 * Every change decodes a full-size hero, crossfades two screen-sized layers and re-blurs a third
 * (#backdrop::before, blur(64px) across the whole screen). Done on each step of a held key that
 * was the single most expensive thing in the frame, and it is what made the grid stutter under
 * the scroll. A run of steps now changes the text straight away and the picture once the
 * highlight settles -- which is also what a console dashboard does.
 */
const BACKDROP_SETTLE_MS = 170;
let bdDeferTimer = null, lastPaintAt = -Infinity;

function scheduleBackdrop(g) {
  const now = performance.now();
  const fast = now - lastPaintAt < BACKDROP_SETTLE_MS;
  lastPaintAt = now;
  if (!fast) { setBackdrop(g); return; }
  clearTimeout(bdDeferTimer);
  bdDeferTimer = setTimeout(() => { bdDeferTimer = null; setBackdrop(focusedGame()); }, BACKDROP_SETTLE_MS);
}

function setBackdrop(game) {
  // A direct call -- the detail page and Settings clear it -- wins over one still waiting.
  clearTimeout(bdDeferTimer);
  bdDeferTimer = null;
  // Keyed on the picture, not the game. On the game id alone this skipped every repaint while
  // the highlight stayed put -- including the one after a metadata pass swapped the art out from
  // under it, which left the element pointing at a file that no longer existed and the screen
  // black until you moved. The id is still in the key so two games that share a fallback picture
  // do not confuse it.
  const url = game && backdropUrl(game);
  const key = game ? game.id + "|" + (url || "") : "none";
  if (key === bdCurrentKey) return;
  bdCurrentKey = key;

  const front = $(bdFront);
  const backId = bdFront === "bdA" ? "bdB" : "bdA";
  const back = $(backId);

  const flat = () => {
    setArtAspect(back, 0, 0);
    if (!game) { back.style.backgroundImage = "none"; return; }
    const h = hashHue(game.title);
    back.style.backgroundImage = `linear-gradient(150deg, hsl(${h},18%,16%), hsl(${(h + 40) % 360},22%,7%))`;
  };
  const wrap = document.getElementById("backdrop");
  const show = (image) => {
    // The bed is one shared layer rather than one per crossfade slot: it is blurred past
    // recognition, so swapping it outright is invisible where crossfading two of them is just
    // two more full-screen filters for the compositor to run.
    if (image) wrap.style.setProperty("--bd-image", `url('${image}')`);
    else wrap.style.removeProperty("--bd-image");
    wrap.classList.toggle("has-art", !!image);
    back.classList.add("visible");
    front.classList.remove("visible");
    bdFront = backId;
  };

  if (!url) { flat(); show(null); return; }

  // Loaded rather than assigned, because the shape of the picture decides the height of the
  // element a theme hangs it in -- see setArtAspect. Waiting for the image also means the old
  // backdrop holds until the new one can be drawn at the right size, instead of appearing at the
  // wrong one and resizing. The key guard drops art the highlight has already moved past.
  //
  // Down the list on a failure rather than straight to a flat colour. A library entry can name a
  // file that is no longer on disk -- a download rejected for being the wrong shape is deleted,
  // and the field that pointed at it is not always cleared in the same pass -- and one stale name
  // should cost that picture, not the whole backdrop.
  const candidates = backdropUrls(game);
  const tryFrom = (i) => {
    if (i >= candidates.length) { flat(); show(null); return; }
    queueArt(candidates[i], (src, w, h) => {
      if (bdCurrentKey !== key) return;
      if (!src) { tryFrom(i + 1); return; }
      back.style.backgroundImage = `url('${src}')`;
      setArtAspect(back, w, h);
      show(src);
    });
  };
  tryFrom(0);
}

/* ============================== tab bars ============================== */

/* Empty on purpose. With one screen left there is nothing to switch between, and a lone "Library"
   tab was only a label taking up the top of the screen. The bars still render -- they carry the
   clock and the title count -- they simply have no tabs in them now. */
const TAB_DEFS = [];

function renderTabbars() {
  document.querySelectorAll("[data-tabbar]").forEach(bar => {
    const active = bar.dataset.tabbar;
    bar.innerHTML = "";
    TAB_DEFS.forEach((t, i) => {
      const el = document.createElement("div");
      el.className = "tab" + (t.id === active ? " tab-active" : "");
      el.textContent = t.label;
      el.dataset.tab = t.id;
      // Tabs are ordinary focusables now, so Up from the top row reaches them by geometry
      // rather than by a hardcoded zone list.
      el.dataset.focusable = "";
      el.dataset.action = "tab:" + t.id;
      el.addEventListener("mouseenter", () => {
        if (!hoverEnabled()) return;
        if (view === "library") { setFocusEl(el); updateLibraryFocus(true); }
      });
      el.addEventListener("click", () => { if (t.id !== view) switchView(t.id); });
      bar.appendChild(el);
    });
  });
}

/* ============================== tiles (shared) ============================== */

function makeAddTile(onHover, onClick) {
  const item = document.createElement("div");
  item.className = "grid-item add-tile";
  item.dataset.focusable = "";
  item.dataset.action = "addGame";
  item.innerHTML = `<div class="add-plus">+</div><div class="add-label">Add game</div>`;
  if (onHover) item.addEventListener("mouseenter", () => { if (hoverEnabled()) onHover(); });
  if (onClick) item.addEventListener("click", onClick);
  return item;
}

/**
 * The binding data a theme's templates see for one game.
 *
 * Formatted values sit alongside the raw ones on purpose: a template should be able to write
 * {{playtime}} without knowing that the app stores minutes, but {{playtimeMinutes}} is there
 * for a theme that wants to do its own thing with it.
 */
function gameView(g) {
  return {
    id: g.id, title: g.title, platform: g.platform, emulated: !!g.emulated,
    installed: g.installed, favorite: g.favorite, hidden: g.hidden,
    cover: coverUrl(g) || "", banner: bannerUrl(g) || "",
    hero: heroUrl(g) || "", logo: logoUrl(g) || "",
    backdrop: backdropUrl(g) || "",
    playtimeMinutes: g.playtimeMinutes || 0, sizeBytes: g.sizeBytes || 0,
    playtime: fmtPlaytime(g.playtimeMinutes),
    lastPlayed: fmtLastPlayed(g.lastPlayed),
    size: fmtSize(g.sizeBytes),
    sessions: g.sessions || 0,
    meta: g.installed ? shortMeta(g) : uninstalledMeta(g),
    initials: initials(g.title),
    // Fetched metadata. Empty string rather than undefined, so a template that prints one of
    // these for a game we know nothing about leaves a gap instead of the word "undefined".
    description: g.description || "",
    developer: g.developer || "",
    publisher: g.publisher || "",
    genres: (g.genres || []).join(", "),
    releaseDate: g.releaseDate || "",
    year: releaseYear(g.releaseDate) || "",
    score: typeof g.criticScore === "number" ? String(g.criticScore) : "",
    pegi: typeof g.pegiRating === "number" ? String(g.pegiRating) : "",
  };
}

/* A themed tile still gets the focus and identity attributes from here rather than trusting
   the template to carry them: a theme that forgets data-focusable would produce a grid you
   cannot navigate, which is a miserable thing to debug from a sofa. */
function themedTile(name, g, cls) {
  const el = Theme.render(name, gameView(g));
  if (!el) return null;
  el.classList.add(cls);
  if (!g.installed) el.classList.add("uninstalled");
  el.dataset.focusable = "";
  el.dataset.gameId = g.id;
  return el;
}

function makeGridTile(g, onHover, onClick, onDetails) {
  if (g.__add) return makeAddTile(onHover, onClick);

  let item = themedTile("game-tile", g, "grid-item");
  if (item) {
    if (onHover) item.addEventListener("mouseenter", () => { if (hoverEnabled()) onHover(); });
    if (onClick) item.addEventListener("click", onClick);
    if (onDetails) item.addEventListener("contextmenu", (e) => { e.preventDefault(); onDetails(); });
    return item;
  }

  item = document.createElement("div");
  item.className = "grid-item" + (g.installed ? "" : " uninstalled");
  // What makes the tile navigable and activatable; see the contract in nav.js.
  item.dataset.focusable = "";
  item.dataset.gameId = g.id;

  const art = document.createElement("div");
  art.className = "grid-art";
  applyArt(g, art, coverUrl(g));
  item.appendChild(art);

  const label = document.createElement("div");
  label.className = "grid-label";
  const meta = g.installed ? shortMeta(g) : uninstalledMeta(g);
  label.innerHTML = `<div class="grid-title">${esc(g.title)}</div><div class="grid-meta">${esc(meta)}</div>`;
  item.appendChild(label);

  if (g.favorite) {
    const star = document.createElement("div");
    star.className = "tile-fav";
    star.textContent = "★";
    item.appendChild(star);
  }

  if (onHover) item.addEventListener("mouseenter", () => { if (hoverEnabled()) onHover(); });
  if (onClick) item.addEventListener("click", onClick);
  if (onDetails) item.addEventListener("contextmenu", (e) => { e.preventDefault(); onDetails(); });
  return item;
}

/* ============================== library data ============================== */

function sortGames(list) {
  const score = (g) => (typeof g.criticScore === "number" ? g.criticScore : -1);
  const by = {
    // The default puts what you can play right now first. With a whole store account imported,
    // A to Z alone buried the dozen installed games among hundreds you would have to download.
    az: (a, b) => (b.installed ? 1 : 0) - (a.installed ? 1 : 0) || a.title.localeCompare(b.title),
    za: (a, b) => b.title.localeCompare(a.title),
    recent: (a, b) => new Date(b.lastPlayed || 0) - new Date(a.lastPlayed || 0),
    played: (a, b) => (b.playtimeMinutes || 0) - (a.playtimeMinutes || 0),
    sizeDesc: (a, b) => (b.sizeBytes || 0) - (a.sizeBytes || 0),
    sizeAsc: (a, b) => (a.sizeBytes || 0) - (b.sizeBytes || 0),
    // An unrated game sorts below a badly rated one rather than above it: a missing score is
    // not a low score, but a wall of blanks at the top is not what "highest rated" is for.
    // Ties fall back to the title so the order is stable between renders.
    score: (a, b) => score(b) - score(a) || a.title.localeCompare(b.title),
  }[F.sort] || ((a, b) => a.title.localeCompare(b.title));
  return [...list].sort(by);
}

/** Tiles across one row of the all-games grid. Mirrors the width in .grid-item. */
const GRID_COLS = 9;

/** Games eligible for the library: everything the user hasn't hidden. */
function visibleGames() { return S.games.filter(g => !g.hidden); }

/* ============================== editions ==============================
 *
 * One game, several stores. 1000xRESIST owned on Steam and on Xbox is one tile, not two: the
 * library entries stay separate (each has its own install state, launch route and playtime, and
 * the host keys everything by id), and the page groups them by title for display.
 *
 * Which edition a tile stands for is decided in this order:
 *   1. the one the user picked under Manage → "Launch with", which the host remembers
 *   2. an installed one over one that is not -- the tile should launch, not offer a download
 *   3. the stores in EDITION_ORDER, Steam first, when more than one is installed
 *   4. the one with more playtime
 * The others are listed in the game menu ("Play on Xbox", "Install on Xbox") and under Manage.
 *
 * Two entries from the SAME store never group, even with identical titles: Steam sells two games
 * called exactly "DOOM", and merging them would hide one the account paid for.
 */
const EDITION_ORDER = ["Steam", "Epic", "GOG", "Xbox", "Manual"];
const EDITION_SUFFIXES = [
  "game of the year edition", "anniversary edition", "definitive edition", "enhanced edition",
  "complete edition", "ultimate edition", "standard edition", "special edition", "deluxe edition",
  "goty edition", "gold edition", "remastered",
];
let EDITIONS = new Map();   // game id -> { members: Game[] }

/* The same folding TitleMatch does on the host -- accents, apostrophes, "&", punctuation, one
   trailing edition -- plus the Microsoft Store's habit of labelling the PC build. */
function titleKey(title) {
  let s = String(title || "").toLowerCase()
    .replace(/&/g, " and ").replace(/['’‘´`]/g, "")
    .normalize("NFD").replace(/[̀-ͯ]/g, "").replace(/[™®©]/g, "");
  s = s.replace(/\s*\((?:game preview|early access|pc|windows(?: 1[01])?)\)\s*$/, "")
       .replace(/\s*[-–:]\s*(?:windows(?: 1[01])?|pc)(?: edition)?\s*$/, "")
       .replace(/[^a-z0-9]+/g, " ").trim();
  for (const suffix of EDITION_SUFFIXES)
    if (s.endsWith(" " + suffix)) { s = s.slice(0, -suffix.length - 1).trim(); break; }
  return s;
}

function rankEditions(members) {
  const order = (g) => { const i = EDITION_ORDER.indexOf(g.platform); return i < 0 ? 99 : i; };
  return [...members].sort((a, b) =>
    (b.preferredEdition ? 1 : 0) - (a.preferredEdition ? 1 : 0)
    || (b.installed ? 1 : 0) - (a.installed ? 1 : 0)
    || order(a) - order(b)
    || (b.playtimeMinutes || 0) - (a.playtimeMinutes || 0));
}

/* Rebuilt on every state push. Hidden games are left out of the grouping: hiding is done to a
   whole game (see the game menu), and a hidden one is its own entry in the hidden view. */
function buildEditions() {
  EDITIONS = new Map();
  const byKey = new Map();
  for (const g of S.games) {
    // A ROM never groups with a store copy, or with a ROM for another system: "Doom" on the SNES
    // and DOOM on Steam are different games that happen to share a name, and "Sonic the
    // Hedgehog" on the Genesis and on the Master System are two different games too.
    const key = g.hidden || g.emulated ? "" : titleKey(g.title);
    let group = null;
    if (key) {
      const groups = byKey.get(key) || [];
      byKey.set(key, groups);
      group = groups.find(x => !x.members.some(m => m.platform === g.platform)) || null;
      if (!group) { group = { members: [] }; groups.push(group); }
    } else {
      group = { members: [] };
    }
    group.members.push(g);
    EDITIONS.set(g.id, group);
  }
}

/** Every store this game is in, the one its tile launches first. */
function editionsOf(g) {
  const group = g && EDITIONS.get(g.id);
  return group ? rankEditions(group.members) : (g ? [g] : []);
}

/** The edition a tile for this game stands for. */
function primaryEdition(g) { return editionsOf(g)[0] || g; }

/*
 * One entry per game out of a list of library entries. When a filter has already narrowed the
 * list -- a platform filter of "Xbox", say -- the tile stands for the best edition that is still
 * IN the list, so filtering to Xbox shows the Xbox copy rather than hiding the game or showing
 * the Steam one.
 */
function collapseEditions(list) {
  const inList = new Set(list.map(g => g.id));
  const seen = new Set();
  const out = [];
  for (const g of list) {
    const group = EDITIONS.get(g.id);
    if (group) { if (seen.has(group)) continue; seen.add(group); }
    const members = group ? group.members : [g];
    const rep = rankEditions(members).find(m => inList.has(m.id)) || g;
    out.push({ rep, members });
  }
  return out;
}

/** Tell the host which edition launches, and apply it here straight away so the tile follows. */
function preferEdition(g) {
  const members = editionsOf(g);
  members.forEach(m => { m.preferredEdition = m.id === g.id; });
  send({ cmd: "preferEdition", id: g.id, siblings: members.filter(m => m.id !== g.id).map(m => m.id) });
}

/* Continue carousel geometry.
   Measured off the rendered tiles rather than assumed: the built-in layout is a 300px tile on a
   328px pitch, but a theme is free to change both, and a hardcoded pitch slides the track by the
   wrong amount the moment it does. The constants below are only the fallback for the first paint,
   before there are two tiles to measure. */
const CONTINUE_MAX = 12;
const CONT_STEP = 328;
const CONT_VIEWPORT = 1760;
let contScroll = 0;   // index of the leftmost visible tile

/** Distance between two adjacent tiles, gap included. */
function contStep() {
  const track = $("continueTrack");
  const a = track && track.children[0], b = track && track.children[1];
  if (a && b) {
    const d = b.offsetLeft - a.offsetLeft;
    if (d > 0) return d;
  }
  return CONT_STEP;
}

/* How many tiles fit, by walking them rather than dividing: the row's padding is cancelled by a
   negative margin so its width is not the usable width, and tiles need not all be one size. */
function contPerView() {
  const track = $("continueTrack");
  const first = track && track.children[0];
  if (!first) return 1;
  const row = $("continueRow");
  const width = row && row.clientWidth ? row.clientWidth : CONT_VIEWPORT;
  let n = 0;
  for (const el of track.children) {
    if (el.offsetLeft - first.offsetLeft + el.offsetWidth > width + 1) break;
    n++;
  }
  return Math.max(1, n);
}

function contMaxScroll() { return Math.max(0, contItems.length - contPerView()); }

/**
 * Slide the carousel the minimum distance needed to keep the focused tile on screen.
 * `follow` is false for hover: letting the mouse drag the carousel makes tiles slide out
 * from under the cursor, which fires another hover and runs away.
 */
/** The carousel index the highlight is on, or null when it is elsewhere. */
function focusedContIndex() {
  const el = focusEl();
  if (!el || el.dataset.contIndex === undefined) return null;
  return parseInt(el.dataset.contIndex, 10);
}

function updateContinueScroll(follow) {
  const track = $("continueTrack");
  if (!track) return;
  const perView = contPerView();

  const i = focusedContIndex();
  if (follow && i !== null) {
    if (i < contScroll) contScroll = i;
    else if (i > contScroll + perView - 1) contScroll = i - perView + 1;
  }
  contScroll = Math.max(0, Math.min(contScroll, contMaxScroll()));
  track.style.transform = `translateX(${-contScroll * contStep()}px)`;
}

/* Right stick horizontal -> carousel. The host turns stick X into real HWHEEL events, so this
   also means a horizontal-scrolling mouse or trackpad drives the carousel. */
let hWheelAccum = 0;

function overlayOpen() {
  return inputOpen || filterOpen || !!gameMenu || collectOpen || manageOpen || !!choiceState || !!confirmState || guideOpen;
}

window.addEventListener("wheel", (e) => {
  if (Math.abs(e.deltaX) <= Math.abs(e.deltaY)) return;   // vertical intent: let it scroll normally
  if (overlayOpen() || view !== "library") return;
  if (focusedContIndex() === null) return;

  hWheelAccum += e.deltaX;
  let moved = false;
  while (Math.abs(hWheelAccum) >= 120) {
    const dir = Math.sign(hWheelAccum);
    hWheelAccum -= dir * 120;
    // Step along the carousel by moving focus, so the wheel and the D-pad end up in
    // exactly the same place rather than keeping two ideas of where the highlight is.
    if (!navMove(dir > 0 ? "Right" : "Left")) { hWheelAccum = 0; break; }
    const next = focusedContIndex();
    if (next === null) break;   // walked out of the carousel; stop rather than drift
    moved = true;
  }
  if (moved) {
    setInputMode("pad");   // the stick is navigating, so show the highlight it is moving
    updateLibraryFocus();
  }
}, { passive: true });

function libraryData() {
  // Hidden games are the whole library when you ask for them, and none of it otherwise. A
  // separate view rather than "show hidden as well": the point of asking is to look over what you
  // put away and take something back out, and mixing them back into 200 tiles is not that.
  const base = F.hidden ? S.games.filter(g => g.hidden) : visibleGames();
  // The platform filter narrows the entries BEFORE they are grouped into games, so it picks which
  // edition shows; everything else is a question about the game as a whole.
  let groups = collapseEditions(F.platforms.size ? base.filter(g => F.platforms.has(g.platform)) : base);
  if (F.fav) groups = groups.filter(x => x.members.some(m => m.favorite));
  if (F.collections.size) groups = groups.filter(x => x.members.some(gameInSelectedCollection));
  if (F.status.size === 1) {
    const wantInstalled = F.status.has("Installed");
    groups = groups.filter(x => x.rep.installed === wantInstalled);
  }
  const needle = searchKey(F.search);
  if (needle) groups = groups.filter(x => x.members.some(m => searchKey(m.title).includes(needle)));
  const filtered = groups.map(x => x.rep);

  const cont = collapseEditions(base.filter(g => g.lastPlayed && g.installed))
    .map(x => x.rep)
    .sort((a, b) => new Date(b.lastPlayed) - new Date(a.lastPlayed))
    .slice(0, CONTINUE_MAX);

  // The "add a game" tile always trails the grid so it's reachable without a menu.
  const items = [...sortGames(filtered), { __add: true }];
  const rows = [];
  // Nine, not eight. The tile is cut to 2:3 so box art is never cropped (see .grid-item), and at
  // eight across that shape would have been 199x298 -- tall enough to leave barely one row on
  // screen. Nine narrower columns keep the same 1760 run and the same row height.
  for (let i = 0; i < items.length; i += GRID_COLS) rows.push(items.slice(i, i + GRID_COLS));
  return { cont, rows, total: filtered.length };
}

function filterSummary(total) {
  const bits = [`${total} TITLE${total === 1 ? "" : "S"}`];
  if (F.platforms.size) bits.push([...F.platforms].join(" + ").toUpperCase());
  if (F.fav) bits.push("FAVORITES");
  if (F.collections.size) bits.push(S.collections.filter(c => F.collections.has(c.id)).map(c => c.name.toUpperCase()).join(" + "));
  if (F.status.size === 1) bits.push([...F.status][0].toUpperCase());
  if (F.hidden) bits.push("HIDDEN");
  if (F.search) bits.push(`MATCHING “${F.search.toUpperCase()}”`);
  const sort = SORTS.find(s => s.id === F.sort);
  if (F.sort !== "az" && sort) bits.push(sort.label.toUpperCase());
  // Only as a note on the library you ARE looking at. While the hidden view is on, these games
  // are the list, so counting them off to one side says the opposite of what it means.
  const hidden = S.games.length - visibleGames().length;
  if (hidden > 0 && !F.hidden) bits.push(`${hidden} HIDDEN`);
  return bits.join(" · ");
}

/* ============================== library render ============================== */

/** The running game shown as a banner above Continue, so it is the first thing focus lands on. */
function renderPlaying() {
  const sec = $("playingSection");
  const g = S.gameRunning ? gameById(S.runningGameId) : null;
  if (!g) { sec.style.display = "none"; return; }

  sec.style.display = "";
  const art = $("playingArt");
  art.className = "playing-art";
  art.innerHTML = "";
  art.style.background = "";
  applyArt(g, art, bannerUrl(g));
  $("playingName").textContent = g.title;
  $("playingMeta").textContent = (g.platform + " · RUNNING").toUpperCase();

  const card = $("playingCard");
  // data-role tells libraryAccept this tile resumes rather than relaunches.
  card.dataset.gameId = g.id;
  card.dataset.role = "playing";
  card.onmouseenter = () => { if (hoverEnabled()) { setFocusEl(card); updateLibraryFocus(true); } };
  card.onclick = () => { setFocusEl(card); updateLibraryFocus(true); send({ cmd: "resumeGame" }); };
}

function renderLibrary() {
  const { cont, rows, total } = libraryData();
  renderPlaying();
  contItems = cont;
  gridRows = rows;
  // Not only from revealFocus: scrolling with the wheel never moves the pad focus, and the top
  // fade still has to come on.
  watchScrolled($("gridScroll"));

  // Games, not entries: a game owned on two stores is one title.
  const titles = collapseEditions(visibleGames()).length;
  $("titleCount").textContent = `${titles} TITLE${titles === 1 ? "" : "S"}`;
  $("gridLabel").textContent = filterSummary(total);

  // Continue carousel (landscape banner art)
  const rowEl = $("continueRow");
  rowEl.innerHTML = "";
  const track = document.createElement("div");
  track.className = "continue-track";
  track.id = "continueTrack";
  rowEl.appendChild(track);
  $("continueSection").style.display = cont.length ? "" : "none";
  cont.forEach((g, i) => {
    let item = themedTile("continue-tile", g, "cont-item");
    if (!item) {
      item = document.createElement("div");
      item.className = "cont-item";

      const art = document.createElement("div");
      art.className = "cont-art";
      applyArt(g, art, bannerUrl(g));

      const meta = document.createElement("div");
      meta.className = "cont-meta";
      meta.innerHTML = `<div class="cont-title">${esc(g.title)}</div><div class="cont-sub">${esc(shortMeta(g))}</div>`;

      item.appendChild(art); item.appendChild(meta);
    }
    // A game can sit in both the carousel and the grid, so the key has to say which one
    // this is -- on the bare game id the highlight would jump between them.
    item.dataset.focusable = "";
    item.dataset.gameId = g.id;
    item.dataset.focusKey = "cont:" + g.id;
    item.dataset.contIndex = i;
    item.addEventListener("mouseenter", () => {
      if (!hoverEnabled()) return;
      setFocusEl(item); updateLibraryFocus(true);
    });
    item.addEventListener("click", () => { setFocusEl(item); updateLibraryFocus(true); libraryAccept("A"); });
    track.appendChild(item);
  });

  // Grid
  const scroll = $("gridScroll");
  scroll.innerHTML = "";
  if (total === 0) {
    const note = document.createElement("div");
    note.className = "empty-note";
    note.innerHTML = S.scanning
      ? "Scanning your Steam, Epic, GOG and Xbox libraries and your ROM folders…"
      : F.search
        ? `No games match “${esc(F.search)}”. Press ${slot("View")} to change the search, or ${slot("B")} while typing to clear it.`
      : (visibleGames().length
        ? `Nothing matches the current filter. Press ${slot("X")} to change it, or ${slot("Y")} to reset.`
        : "No games found yet. Use the <b>+ Add game</b> tile below, or rescan from <b>Settings → Library</b>.");
    scroll.appendChild(note);
  }
  rows.forEach((row, r) => {
    const rowDiv = document.createElement("div");
    rowDiv.className = "grid-row";
    rowDiv.dataset.dimGroup = "";
    row.forEach((g, c) => {
      const item = makeGridTile(g, null, null);
      item.dataset.focusKey = g.__add ? "action:addGame" : "tile:" + g.id;
      item.addEventListener("mouseenter", () => { if (hoverEnabled()) { setFocusEl(item); updateLibraryFocus(true); } });
      item.addEventListener("click", () => { setFocusEl(item); updateLibraryFocus(true); libraryAccept("A"); });
      rowDiv.appendChild(item);
    });
    scroll.appendChild(rowDiv);
  });

  clampFocus();
  updateLibraryFocus();
}

/** The focused element's game, including the trailing "add game" tile as null. */
function focusedCell() {
  const el = focusEl();
  if (!el) return null;
  if (el.dataset.action === "addGame") return { __add: true };
  return el.dataset.gameId ? gameById(el.dataset.gameId) : null;
}

/* Focus survives a re-render by key, but the thing it was on can disappear -- a filter
   change, a game uninstalled, the running game exiting. Fall back to the first focusable
   rather than leaving the highlight nowhere. */
function clampFocus() { ensureFocus(Nav.activeScope()); }

function updateLibraryFocus(noScroll) {
  paintNav();
  if (noScroll) return;
  const el = focusEl();
  if (el) revealFocus(el);
}

/* ============================== library nav ============================== */

function libraryNav(btn) { navMove(btn); }

/* What A / Y do is read off the focused element, not inferred from which zone the
   highlight is in. That is the whole point: a theme can put a launchable tile
   anywhere, or invent a row of its own, and activation still works. */
const ACTIONS = {
  addGame: () => send({ cmd: "addManual" }),
  search: () => openSearch(),
  resume: () => send({ cmd: "resumeGame" }),
  "tab:library": () => switchView("library"),
};

function libraryAccept(btn) {
  if (!focusVisible()) return;   // pointer is over empty space: nothing is armed
  const el = focusEl();
  if (!el) return;

  const g = el.dataset.gameId ? gameById(el.dataset.gameId) : null;
  if (g) {
    // The running game resumes rather than relaunching; the element says which it is.
    const running = el.dataset.role === "playing";
    if (btn === "A") {
      if (running) send({ cmd: "resumeGame" });
      else if (!g.installed) offerInstall(g);
      else launchGame(g);
    } else if (btn === "Y") {
      openGameMenu(g.id, "library");
    }
    return;
  }

  if (btn !== "A") return;
  const act = ACTIONS[el.dataset.action];
  if (act) act();
}

function libraryInput(btn) {
  switch (btn) {
    case "Up": case "Down": case "Left": case "Right": libraryNav(btn); break;
    case "A": case "Y": libraryAccept(btn); break;
    case "X": openFilter(); break;
    // View is the button with the two squares, left of the guide button. LB and RB were free too,
    // but RB is the keyboard toggle by default and the pair is a minimize combo option.
    case "View": openSearch(); break;
    // Settings lost its tab, so this is the way in. Menu is the pad's ☰ button; holding it is
    // still the keyboard toggle, and only a tap gets here.
    case "Menu": switchView("settings"); break;
    // B walks back out of the grid: to its top, then up to the carousel.
    case "B": {
      const list = Nav.focusables(Nav.activeScope());
      const cur = focusEl();
      if (!cur || !list.length) break;
      const inGrid = !!cur.closest("#gridScroll");
      if (inGrid) {
        const first = list.find(el => el.closest("#gridScroll"));
        if (first && first !== cur) { setFocusEl(first); afterFocusMove(); break; }
      }
      const above = list.find(el => el.closest("#continueRow"));
      if (above) { setFocusEl(above); afterFocusMove(); }
      break;
    }
  }
}

function launchGame(g) {
  if (S.gameRunning) {
    // Already playing something else. Offer the swap rather than just refusing -- from the
    // couch, "a game is already running" left you with nothing to do about it.
    if (S.runningGameId === g.id) { send({ cmd: "resumeGame" }); return; }
    const running = gameById(S.runningGameId);
    confirmState = {
      title: running ? `Close ${running.title}?` : "Close the running game?",
      body: `${g.title} will start once it has closed.`,
      yesLabel: `Close and play ${g.title}`,
      icon: "play", danger: false,
      onYes: () => { toast(`Closing ${running ? running.title : "the game"}…`); send({ cmd: "launch", id: g.id, replace: true }); },
    };
    confirmIdx = 0;
    $("overlay-confirm").classList.add("active");
    renderConfirm();
    return;
  }
  toast(`Launching ${g.title}…`);
  send({ cmd: "launch", id: g.id });
}

/* The host decides what can be installed and writes the store's own URI onto the entry --
   steam://install, Epic's launcher, goggalaxy://, the Microsoft Store -- so the page only has to
   ask whether there is one. An installed game never carries it. */
function canInstall(g) { return !!g.installUri; }

const STORE_NAMES = { Steam: "Steam", Epic: "the Epic Games Launcher", GOG: "GOG Galaxy", Xbox: "the Microsoft Store" };
function storeName(g) {
  // A GOG game on a PC without Galaxy opens its own page on gog.com, where the installer is.
  if (g.platform === "GOG" && /^https:\/\/www\.gog\.com/.test(g.installUri || "")) return "gog.com";
  return STORE_NAMES[g.platform] || g.platform;
}

/* A confirm rather than a straight send, for two reasons. The launcher steps aside for the
   store's window -- it is topmost on the TV and the window would open behind it -- and vanishing
   on one press of A is a surprise; and the body is the only place to say how to come back. */
function offerInstall(g) {
  if (!canInstall(g)) { toast(`${g.title} is not installed`); return; }
  const combo = S.settings && S.settings.minimizeCombo && S.settings.minimizeCombo !== "Off"
    ? S.settings.minimizeCombo : null;
  const store = storeName(g);
  const Store = store.charAt(0).toUpperCase() + store.slice(1);
  confirmState = {
    title: `Install ${g.title}?`,
    body: `${Store} opens with ${g.title} ready to install. Consolify steps aside while it does` +
      (combo ? `; press ${comboName(combo, padFamily)} to come back.` : "; start Consolify again to come back.") +
      " The tile turns playable once the download has finished.",
    yesLabel: `Install with ${store.replace(/^the /, "")}`,
    icon: "download", danger: false,
    onYes: () => send({ cmd: "install", id: g.id }),
  };
  confirmIdx = 0;
  $("overlay-confirm").classList.add("active");
  renderConfirm();
}

/* ============================== detail ============================== */

function openDetail(id, from) {
  detailGameId = id;
  detailReturn = from || "library";
  clearFocus(document.getElementById("screen-detail"));
  switchView("detail");
}

/* Metacritic's own bands, because the colour is only a shorthand if it matches the one people
   already know from the site: green 75+, yellow 50-74, red below. */
function scoreBand(n) { return n >= 75 ? "good" : n >= 50 ? "mixed" : "poor"; }

/*
 * Release dates arrive in whatever shape the source kept them in. Ours is an ISO timestamp, which
 * is the one worth rewriting: "2024-02-02T00:00:00Z" is not something to put on a page.
 *
 * Its three numbers are read straight out of the string rather than through Date's parser, which
 * would take the Z at its word, convert to local time on the way back out, and print every release
 * a day early anywhere west of Greenwich. A release date carries no time of day to convert.
 *
 * Everything else is passed through untouched -- Steam writes "2 Feb, 2024", which already reads
 * fine, and it also writes "Q1 2024" and bare years, which any reformatting would have to guess a
 * day for and would get wrong.
 */
function fmtReleased(date) {
  if (!date) return null;
  const iso = /^(\d{4})-(\d{2})-(\d{2})/.exec(String(date));
  if (!iso) return String(date);
  const d = new Date(+iso[1], +iso[2] - 1, +iso[3]);
  if (isNaN(d)) return String(date);
  return `${d.getDate()} ${d.toLocaleString("en", { month: "short" })} ${d.getFullYear()}`;
}

function releaseYear(date) {
  const m = /\b(\d{4})\b/.exec(date || "");
  return m ? m[1] : null;
}

/*
 * The numbers along the bottom. Built rather than hard-coded so a stat that has nothing to say
 * can be left out entirely: "SESSIONS —" next to "AVG SESSION —" on a game you have never opened
 * is four words saying nothing, and a row of dashes is what made this page feel like a form.
 *
 * Playtime and size are always shown, even at zero, because their absence would read as missing
 * data rather than as a game you have not played.
 */
function renderDetailStats(g) {
  const el = $("detailStats");
  const stats = [];
  const add = (label, value) => { if (value) stats.push({ label, value }); };

  stats.push({ label: "PLAYTIME", value: fmtPlaytime(g.playtimeMinutes) });
  add("SESSIONS", g.sessions > 0 ? String(g.sessions) : null);
  // Worth more than the raw total: it says whether this is a game you dip into or disappear into.
  add("AVG SESSION", g.sessions > 0 && g.playtimeMinutes > 0
    ? fmtPlaytime(g.playtimeMinutes / g.sessions) : null);
  add("LAST PLAYED", g.lastPlayed ? fmtLastPlayed(g.lastPlayed) : null);
  add("RELEASED", fmtReleased(g.releaseDate));
  // A game that was never installed has no size on record, and a dash in a stats row reads as
  // something missing rather than something unknowable.
  if (g.installed || g.sizeBytes) stats.push({ label: g.installed ? "ON DISK" : "DOWNLOAD", value: fmtSize(g.sizeBytes) });

  el.innerHTML = stats.map(s =>
    `<div class="stat"><div class="stat-label mono">${esc(s.label)}</div>` +
    `<div class="stat-value">${esc(s.value)}</div></div>`).join("");
}

/* PEGI's own bands: 3 and 7 green, 12 and 16 amber, 18 red. */
function pegiBand(age) { return age >= 18 ? "red" : age >= 12 ? "amber" : "green"; }

/* ESRB's, in the same three steps: E and E10+ green, T amber, M and AO red. RP is "not rated
   yet", which is not a severity at all, so it gets the neutral one. */
function esrbBand(r) {
  if (r === "M" || r === "AO") return "red";
  if (r === "T") return "amber";
  if (r === "RP") return "grey";
  return "green";
}

/*
 * What other people made of the game: the critic score, and the age it is rated for.
 *
 * Both are given a panel rather than a number in the middle of the facts line. A bare "82" next
 * to the developer's name is a number with no unit -- it could be a rank, a count or a percentage
 * -- and a bare "16" is worse, because it looks like one of those too. With a scale and the name
 * of whoever said it, each reads at a glance from across a room, which is the whole job.
 *
 * The score's source is named, never assumed. Steam's appdetails carries a genuine Metacritic
 * score and says so; IGDB's aggregated_rating is its own average of critics and is NOT
 * Metacritic, so printing Metacritic's name on every score would be wrong about half the time.
 */
/*
 * One badge for both: a value over the name of whoever gave it.
 *
 * The captions beside them are gone. "AGE RATING / 16 AND OVER" beside a mark that already says
 * PEGI 16 is the same fact three times, and "OUT OF 100" beside a score is a footnote nobody
 * needs twice. What the score was actually missing is the thing the age mark had all along --
 * the name of the body that issued it, sitting under the number where it cannot be read as part
 * of it. So the score gets the same two-part construction: 89 over METACRITIC.
 */
function ratingBadge(value, word, band, label) {
  return `<div class="badge" data-band="${band}" role="img" aria-label="${esc(label)}">` +
    `<div class="badge-value">${esc(value)}</div>` +
    `<div class="badge-word mono">${esc(word)}</div></div>`;
}

function renderDetailRatings(g) {
  const el = $("detailRatings");
  const parts = [];

  if (typeof g.criticScore === "number") {
    // Named for whoever actually scored it. IGDB aggregates critics itself and is not Metacritic,
    // so a fixed label here would put one publication's name on another's number.
    // "IGDB critics" -> "IGDB". The space is required: without it this also ate the "critic"
    // inside "Metacritic" and every Metacritic score was labelled META.
    const source = (g.criticSource || "Critics").replace(/\s+critics?$/i, "");
    parts.push(ratingBadge(String(g.criticScore), source.toUpperCase(),
      scoreBand(g.criticScore), `${source} ${g.criticScore} out of 100`));
  }

  // ESRB first: Steam lists it for more games than PEGI, and every game in a 16-game sample that
  // had a PEGI rating had an ESRB one too. The wordmark says which board it is, so falling back
  // to PEGI cannot be mistaken for the other.
  if (g.esrbRating) {
    parts.push(ratingBadge(g.esrbRating, "ESRB", esrbBand(g.esrbRating),
      `ESRB rating ${g.esrbRating}`));
  } else if (typeof g.pegiRating === "number") {
    parts.push(ratingBadge(String(g.pegiRating), "PEGI", pegiBand(g.pegiRating),
      `PEGI ${g.pegiRating}`));
  }

  // Empty rather than a row of "unrated" placeholders: most indies carry neither, and saying so
  // twice on every one of them is what made this page read like a form.
  el.innerHTML = parts.join("");
}

/*
 * Why the board rated it what it did, in the board's own words -- "Blood and Gore", "Mild
 * Lyrics". It belongs under the description because that is where the rest of the pitch is, and
 * it is the one line on this page that says something about the CONTENT rather than about the
 * file: everything else below is playtime, size and dates.
 */
function renderDetailDescriptors(g) {
  const el = $("detailDescriptors");
  const list = g.contentDescriptors || [];
  el.innerHTML = list.map(d => `<span class="descriptor">${esc(d)}</span>`).join("");
}

/*
 * Where the game came from, drawn in the same stroked 24x24 style as every other icon here
 * rather than pasted in as four brand logos: the set stays one family, it inherits currentColor,
 * and it costs nothing to load. The name sits beside it, because a silhouette alone is a
 * guessing game for anyone who does not already know the mark.
 */
const PLATFORM_ICONS = {
  Steam: "steam", Epic: "epic", GOG: "gog", Xbox: "xbox", Manual: "file",
};

/** The mark for where a game came from: its store's, or the cartridge for anything emulated. */
function platformIcon(g) { return g.emulated ? "cartridge" : PLATFORM_ICONS[g.platform] || "store"; }

function renderDetailPlatform(g) {
  const el = $("detailPlatform");
  const icon = platformIcon(g);
  // For a ROM the useful second fact is which program runs it, not which other stores have it.
  const emu = emulatorFor(g);
  const others = g.emulated ? [] : editionsOf(g).filter(m => m.id !== g.id).map(m => m.platform);
  el.innerHTML = (icon ? iconSvg(icon) : "") + `<span>${esc(g.platform)}</span>` +
    (others.length ? `<span class="also-on">also on ${esc(others.join(", "))}</span>` : "") +
    (g.emulated ? `<span class="also-on">${emu ? "via " + esc(emu.name) : "no emulator set"}</span>` : "");
}

/*
 * The line under the title. It used to say how the game launches and where its files are, which
 * is troubleshooting detail on the one screen meant to sell you on playing something -- that has
 * moved to Manage, where you go when you actually want to change it.
 *
 * Everything here is optional. A game with no fetched metadata falls back to its platform, so the
 * row is never empty and never a row of placeholder dashes.
 */
function renderDetailFacts(g) {
  const row = $("detailFacts");
  row.innerHTML = "";

  const add = (cls, text) => {
    const el = document.createElement("span");
    el.className = cls;
    el.textContent = text;
    row.appendChild(el);
  };

  const bits = [];
  const year = releaseYear(g.releaseDate);
  if (year) bits.push(year);
  if (g.developer) bits.push(g.developer);
  // Only when it is somebody else. On the great majority of games the publisher is the
  // developer, and printing the same name twice reads as a mistake.
  if (g.publisher && g.publisher !== g.developer) bits.push(g.publisher);
  // Three is what fits before the row starts wrapping, and the first three are the useful ones --
  // Steam lists "Indie" and "Casual" after whatever the game actually is.
  if (g.genres && g.genres.length) bits.push(g.genres.slice(0, 3).join(", "));
  if (!bits.length) bits.push(g.platform);

  bits.forEach((b, i) => {
    if (i) add("fact-dot", "·");
    add("fact", b);
  });

  // Worth its own chip rather than a word in the list: on a couch it is the difference between
  // starting the game and going to find a keyboard.
  if (g.controllerSupport === "full") add("fact-chip", "Full controller support");
  else if (g.controllerSupport === "partial") add("fact-chip", "Partial controller support");

  if (!g.installed) add("fact-chip fact-warn", "Not installed");
}

function renderDetail() {
  const g = gameById(detailGameId);
  if (!g) { switchView(detailReturn); return; }

  $("detailCrumb").textContent = g.platform.toUpperCase();
  $("detailTitle").textContent = g.title;
  $("detailFav").style.display = g.favorite ? "" : "none";
  renderDetailLegend(g);

  // The wordmark, where the art we already fetch has one. It is the game's own lettering rather
  // than ours, which is most of what makes this page look like a storefront instead of a form.
  // The text title stays in the DOM as the fallback and for anything reading the page.
  const logo = $("detailLogo"), titleRow = $("detailTitleRow");
  const logoSrc = logoUrl(g);
  logo.hidden = !logoSrc;
  titleRow.hidden = !!logoSrc;
  if (logoSrc) {
    logo.style.backgroundImage = `url('${logoSrc}')`;
    logo.setAttribute("aria-label", g.title);
  }

  renderDetailFacts(g);
  renderDetailRatings(g);
  renderDetailPlatform(g);
  $("detailDesc").textContent = g.description || "";
  renderDetailDescriptors(g);
  renderDetailStats(g);

  $("playLabel").textContent = !g.installed
    ? (canInstall(g) ? "Install" : "Not installed")
    : (g.playtimeMinutes > 0 ? "Continue" : "Play");

  const art = $("detailArt");
  art.className = "detail-art";
  art.innerHTML = "";
  art.style.background = "";
  // "cover" on purpose: the box is cut to this picture's own aspect (see --art-aspect and
  // .detail-art), so there is nothing left for cover to crop, and scenery must never letterbox.
  applyArt(g, art, backdropUrl(g), "cover");

  document.querySelectorAll("#detailActions .pill-btn").forEach(el => {
    el.onmouseenter = () => { if (hoverEnabled()) { setFocusEl(el); paintNav(); } };
    el.onclick = () => { setFocusEl(el); paintNav(); detailActivate(); };
  });

  updateDetailFocus();
  setBackdrop(null);
}

const DETAIL_BTNS = ["play", "collect", "manage"];

function updateDetailFocus() {
  const scope = document.getElementById("screen-detail");
  // Default to Play the first time in, then let the engine hold the position.
  if (!focusEl(scope)) setScopeKey(scope, "detail:play");
  paintNav();
}

function detailActivate() {
  const g = gameById(detailGameId);
  const el = focusEl();
  const act = el ? el.dataset.act : null;
  if (act === "play" && g) { if (!g.installed) offerInstall(g); else launchGame(g); }
  else if (act === "collect") openCollect();
  else if (act === "manage") openManage();
}

function detailInput(btn) {
  const g = gameById(detailGameId);
  switch (btn) {
    case "Left": case "Right": case "Up": case "Down": navMove(btn); break;
    case "A": if (focusVisible()) detailActivate(); break;
    case "X": if (g) send({ cmd: "toggleFavorite", id: g.id }); break;
    case "B": switchView(detailReturn); break;
  }
}

/* ============================== settings ============================== */

function allSettingsRows() {
  const s = S.settings;
  if (!s) return [];
  // applyTheme here as well as on the host's state push: the push is 350ms of debounce away, and
  // the accent has to move with the ◂ ▸ that changed it or the picker looks broken.
  const set = (fn) => { fn(); applyTheme(); scheduleSave(); renderSettings(); };
  const displayLabel = (d) => {
    if (!d) return "None";
    const num = d.deviceName.replace(/\D/g, "");
    return `${d.friendlyName} · ${d.width}×${d.height}${d.isPrimary ? " · PRIMARY" : ""} (Display ${num})`;
  };

  const rows = [];
  rows.push({ section: "APPEARANCE", cat: "general" });
  const themes = S.themes && S.themes.length ? S.themes : [{ id: "", name: "Classic" }];
  const theme = themes.find(t => t.id === (s.theme || "")) || themes[0];
  rows.push(cycleRow("Theme", themes.map(t => t.id), () => (s.theme || ""), v => set(() => s.theme = v),
    theme && theme.error ? null
      : [theme && theme.author ? "by " + theme.author : null,
         theme && theme.version ? "v" + theme.version : null,
         theme && theme.description ? theme.description : null]
        .filter(Boolean).join(" · ") || "Drop a theme folder into the themes directory to add one",
    theme && theme.error ? theme.error : null,
    Object.fromEntries(themes.map(t => [t.id, t.name]))));
  rows.push({
    name: "Themes folder", hint: "A theme is a folder with a theme.css and an optional theme.json. Edits apply as you save",
    type: "action", label: "Open",
    action: () => send({ cmd: "openThemesFolder" }),
  });
  rows.push(accentRow(s, set));
  rows.push(toggleRow("Hide the button hints", "Drops the bar along the bottom of every screen. The buttons still do the same things",
    () => !!s.hideLegend, v => set(() => s.hideLegend = v)));

  rows.push({ section: "DISPLAY", cat: "general" });
  rows.push({
    name: "TV display", hint: "Consolify opens here, and games are steered onto it",
    type: "select",
    value: displayLabel(S.displays.find(d => d.deviceName === s.tvDeviceName) || null),
    adjust: (dir) => set(() => {
      if (!S.displays.length) return;
      let i = S.displays.findIndex(d => d.deviceName === s.tvDeviceName);
      i = (i + dir + S.displays.length) % S.displays.length;
      s.tvDeviceName = S.displays[i].deviceName;
    }),
  });
  rows.push(toggleRow("Switch primary display on launch", "Games default to the primary display, so the TV becomes primary while a game runs",
    () => s.switchPrimaryOnLaunch, v => set(() => s.switchPrimaryOnLaunch = v)));
  rows.push(toggleRow("Reposition game windows", "If a game still opens on another monitor, nudge its window onto the TV",
    () => s.repositionGameWindow, v => set(() => s.repositionGameWindow = v)));
  rows.push(toggleRow("Keep launcher focused", "Pull focus back if the desktop steals it while no game is running",
    () => s.keepFocus, v => set(() => s.keepFocus = v)));

  rows.push({ section: "GAMEPAD", cat: "input" });
  rows.push(toggleRow("Gamepad mouse", "Left stick moves the cursor; right stick scrolls",
    () => s.gamepadMouseEnabled, v => set(() => s.gamepadMouseEnabled = v)));
  rows.push(toggleRow("Stay active while a game is focused", "The gamepad-mouse always works when a game is running but not focused; this keeps it alive inside the game too (off avoids fighting native controller support)",
    () => s.gamepadMouseDuringGame, v => set(() => s.gamepadMouseDuringGame = v)));
  rows.push(sliderRow("Stick deadzone", () => s.deadzone, 0.05, 0.40, 0.01, v => set(() => s.deadzone = v), v => v.toFixed(2)));
  rows.push(sliderRow("Cursor sensitivity", () => s.sensitivity, 0.2, 3.0, 0.1, v => set(() => s.sensitivity = v), v => v.toFixed(1) + "×"));
  rows.push(sliderRow("Acceleration curve", () => s.accelExponent, 1.0, 3.0, 0.1, v => set(() => s.accelExponent = v), v => v.toFixed(1)));
  rows.push(buttonRow("Speed boost button", ["RT", "LT", "LB", "RB", "LS", "RS", "Off"], () => s.boostButton, v => set(() => s.boostButton = v),
    "Hold to move the cursor and scroll faster — crossing a 4K screen a nudge at a time gets old"));
  if (s.boostButton !== "Off")
    rows.push(sliderRow("Boost multiplier", () => s.boostMultiplier, 1.5, 5.0, 0.5, v => set(() => s.boostMultiplier = v), v => v.toFixed(1) + "×"));
  rows.push(buttonRow("Left click button", ["A", "B", "X", "Y", "LB", "RB", "LS", "RS"], () => s.leftClickButton, v => set(() => s.leftClickButton = v),
    "Sends a real mouse click when the launcher is not focused"));
  rows.push(buttonRow("Right click button", ["A", "B", "X", "Y", "LB", "RB", "LS", "RS"], () => s.rightClickButton, v => set(() => s.rightClickButton = v)));
  rows.push(toggleRow("Hide pointer system-wide", "The pointer always hides inside the launcher on D-pad input; this extends it to the rest of Windows. Replaces the system cursors, so it is restored when Consolify exits",
    () => s.hideCursorSystemWide, v => set(() => s.hideCursorSystemWide = v)));
  // A setting the host defaults to on: a settings file from before it has no key, and undefined
  // must read as on rather than off.
  rows.push(toggleRow("Touchpad mouse", "DualSense and DualShock 4 as a laptop touchpad: one finger moves the pointer, two fingers scroll, pinch to zoom, press the pad to click (with two fingers down for a right click)",
    () => s.touchpadMouse !== false, v => set(() => s.touchpadMouse = v)));
  // Every touchpad setting defaults to on or to 1.0 on the host; a settings file from before one
  // existed has no key, and undefined must read as that default.
  if (s.touchpadMouse !== false) {
    rows.push(sliderRow("Touchpad sensitivity", () => s.touchpadSensitivity ?? 1, 0.25, 4.0, 0.25,
      v => set(() => s.touchpadSensitivity = v), v => v.toFixed(2) + "×",
      "Slow strokes move the pointer a little for precision, quick ones a lot; this scales both"));
    rows.push(toggleRow("Tap to click", "A light tap is a click, a two-finger tap a right click, two taps a double click. Pressing the pad down always clicks",
      () => s.touchpadTapToClick !== false, v => set(() => s.touchpadTapToClick = v)));
    if (s.touchpadTapToClick !== false)
      rows.push(toggleRow("Tap and drag", "Tap, then touch and hold: the button stays down while the finger moves, to drag a window or select text. Lift and touch again quickly to carry on; tap to let go. Makes a single tap wait a moment before it clicks",
        () => s.touchpadTapDrag !== false, v => set(() => s.touchpadTapDrag = v)));
    rows.push(cycleRow("Two-finger scroll direction", ["Natural", "Traditional"],
      () => (s.touchpadNaturalScroll === false ? "Traditional" : "Natural"),
      v => set(() => s.touchpadNaturalScroll = v === "Natural"),
      s.touchpadNaturalScroll === false
        ? "Fingers down scrolls down, like a mouse wheel"
        : "The page follows the fingers, like a phone. The Windows touchpad default"));
    rows.push(sliderRow("Scroll speed", () => s.touchpadScrollSpeed ?? 1, 0.25, 4.0, 0.25,
      v => set(() => s.touchpadScrollSpeed = v), v => v.toFixed(2) + "×",
      "How far two fingers scroll. A quick flick keeps the page coasting after they lift"));
  }
  const comboWarn =
    s.minimizeCombo === "Guide" ?
      "Windows and Steam both grab this button. Disable BOTH: (1) Windows — Settings > Gaming > " +
      "Xbox Game Bar, turn off \u201COpen Xbox Game Bar using this button on a controller\u201D. " +
      "(2) Steam — Settings > Controller > uncheck \u201CEnable Steam Input\u201D for the pad, or in " +
      "Big Picture go to Settings > Controller > Guide Button Chord Layout and clear it. Steam must " +
      "be fully restarted afterwards."
    : s.minimizeCombo === "View + Menu" ?
      "Steam binds View + Menu (Back + Start) to open Big Picture. Disable it in Steam: Settings > " +
      "Controller > Guide Button Chord Layout, or turn off Steam Input for this controller. Restart " +
      "Steam afterwards."
    : null;

  rows.push(buttonRow("Menu combo", MINIMIZE_COMBOS, () => s.minimizeCombo, v => set(() => s.minimizeCombo = v),
    "Tap to hide or bring back the launcher (in-game menu while a game runs); double tap to open the Power Wheel",
    comboWarn));

  rows.push(buttonRow("Screenshot button", SCREENSHOT_COMBOS, () => s.screenshotCombo, v => set(() => s.screenshotCombo = v),
    "Taps F12, Steam's screenshot key. Works while a game is focused, which the Xbox Share button cannot manage, " +
    "because Windows keeps that button to itself and never passes it to applications",
    s.screenshotCombo !== "Off" && s.screenshotCombo === s.minimizeCombo
      ? "Same as the menu combo above, so one press does both. Pick a different one."
      : null));

  rows.push({ section: "KEYBOARD", cat: "keyboard" });
  rows.push(cycleRow("Keyboard app", ["Builtin", "TabTip", "Osk"], () => s.keyboardApp, v => set(() => s.keyboardApp = v),
    "The Consolify Keyboard never takes focus, so it keeps working over a game and leaves the caret " +
    "where it was. TabTip is the Windows touch keyboard; Osk is the classic on-screen keyboard",
    s.keyboardApp === "Builtin" ? null
      : "Windows' own keyboards are not built for a gamepad: TabTip only accepts one on its Gamepad " +
        "layout, which has to be picked by hand in its settings, and Osk accepts none at all — it " +
        "needs a real mouse. Both also take the foreground, so the field you were typing into can " +
        "lose its caret.",
    KEYBOARD_APP_LABELS));
  rows.push(buttonRow("Keyboard button", ["Start", "Back", "LS", "RS", "LB", "RB"], () => s.keyboardToggleButton, v => set(() => s.keyboardToggleButton = v),
    "Shows and hides the keyboard from anywhere in the launcher"));
  // Press is quicker and is the default. Hold exists because it leaves the tap free, which is the
  // only way to keep a button that already does something inside the launcher.
  rows.push(cycleRow("Opens on", ["Press", "Hold"], () => s.keyboardToggleMode, v => set(() => s.keyboardToggleMode = v),
    (s.keyboardToggleMode || "Press") === "Hold"
      ? "Hold the button down. A tap still does whatever that button normally does"
      : "One tap. The button does nothing else while this is set",
    (s.keyboardToggleMode || "Press") !== "Hold" && (s.keyboardToggleButton === "Start" || s.keyboardToggleButton === "Back")
      ? `${s.keyboardToggleButton === "Start" ? "[[Menu]] opens Settings from the library" : "[[View]] opens search on the library"}, and on Press the keyboard takes it outright — pick another button, or switch to Hold.`
      : null,
    { Press: "Press", Hold: "Hold" }));
  if ((s.keyboardToggleMode || "Press") === "Hold")
    rows.push(sliderRow("Hold time", () => s.keyboardToggleHoldMs, 200, 2000, 100, v => set(() => s.keyboardToggleHoldMs = v), v => Math.round(v) + " ms"));
  rows.push(toggleRow("Show while a game is running",
    "Off by default: inside a game every button belongs to the game, and a keyboard arriving over one is a surprise. A keyboard already on screen can always be closed either way",
    () => !!s.keyboardInGame, v => set(() => s.keyboardInGame = v)));

  rows.push({ section: "CONSOLIFY KEYBOARD", cat: "keyboard" });
  rows.push(sliderRow("Keyboard size", () => s.keyboardScale, 0.6, 1.6, 0.05,
    v => set(() => s.keyboardScale = v), v => Math.round(v * 100) + "%",
    "Scales the keys up or down from the size Consolify picks for the TV"));
  rows.push(sliderRow("D-pad repeat delay", () => s.keyRepeatDelayMs, 120, 900, 10,
    v => set(() => s.keyRepeatDelayMs = v), v => Math.round(v) + " ms",
    "How long a direction is held before the highlight starts moving on its own"));
  rows.push(sliderRow("D-pad repeat speed", () => s.keyRepeatIntervalMs, 20, 300, 5,
    v => set(() => s.keyRepeatIntervalMs = v), v => Math.round(v) + " ms",
    "Gap between steps once it is moving — lower is faster"));
  rows.push({
    name: "Show keyboard now", type: "action", label: "Toggle",
    action: () => send({ cmd: "toggleKeyboard" }),
  });

  rows.push({ section: "LIBRARY", cat: "library" });
  const romCount = S.games.filter(g => g.emulated).length;
  const counts = PLATFORMS.map(p => `${p} ${S.games.filter(g => g.platform === p).length}`)
    .concat(romCount ? [`Emulated ${romCount}`] : []).join(" · ");
  rows.push({
    name: "Rescan platforms",
    hint: (counts ? counts + " · " : "") + "Steam, Epic, GOG and the Xbox app from their install data, plus every ROM folder and playlist",
    type: "action", label: S.scanning ? "Scanning…" : "Rescan",
    action: () => { if (!S.scanning) send({ cmd: "rescan" }); },
  });
  rows.push({
    name: "Add a game manually", hint: "Point to an .exe and optional cover art",
    type: "action", label: "Add",
    action: () => send({ cmd: "addManual" }),
  });

  // One row per ROM folder and one per emulator, each opening its own options list; the two
  // "Add" rows are the way in. A folder with no emulator, or a RetroArch folder with no core,
  // says so on its row: its games are in the library and cannot start, and that is the one
  // thing worth a warning here.
  rows.push({ section: "EMULATORS & ROM FOLDERS", cat: "library" });
  rows.push(toggleRow("Find emulators and ROMs automatically",
    "Every scan looks for installed emulators in the usual places, then for games: RetroArch's playlists, an Emulation\\roms layout, and folders named after a system. Anything you remove stays removed",
    () => s.detectEmulators !== false, v => set(() => s.detectEmulators = v)));
  const em = S.emulation || { emulators: [], romFolders: [], platforms: [] };
  (em.romFolders || []).forEach(f => {
    const p = platformDef(f.platformId);
    const emu = emulatorById(f.emulatorId);
    const n = S.games.filter(g => g.romFolderId === f.id).length;
    const bits = [`${n} game${n === 1 ? "" : "s"}`, emu ? emu.name : "no emulator"];
    if (usesCore(emu)) bits.push(f.core ? coreName(f.core) : "no core");
    if (f.detected) bits.push("found automatically");
    // A playlist is named for its file; the folder its games are in is the games' business.
    const where = f.playlist ? `RetroArch playlist · ${f.path.split(/[\\/]/).pop()}` : f.path;
    rows.push({
      name: p ? p.name : f.platformId, hint: `${where} · ${bits.join(" · ")}`,
      warn: !emu ? "No emulator is set for this folder, so its games cannot start yet"
          : usesCore(emu) && !f.core ? `${emu.name} needs a core for this system before its games can start` : undefined,
      type: "action", label: "Options",
      action: () => openRomFolderOptions(f),
    });
  });
  rows.push({
    name: "Add a ROM folder", hint: "One folder per system. You will be asked which system it is and which emulator runs it; the folder's name is a first guess",
    type: "action", label: "Add",
    action: () => send({ cmd: "romFolderPick" }),
  });
  (em.emulators || []).forEach(e => rows.push({
    name: e.name, hint: `${e.exePath} · ${e.args || '"{rom}"'}${e.detected ? " · found automatically" : ""}`,
    type: "action", label: "Options",
    action: () => openEmulatorOptions(e),
  }));
  rows.push({
    name: "Add an emulator", hint: "For one the scan did not find: point to its .exe. RetroArch, Dolphin, PCSX2, DuckStation, PPSSPP, mGBA, MAME and the other common ones are recognised and set up on their own",
    type: "action", label: "Add",
    action: () => send({ cmd: "emuAdd" }),
  });

  rows.push({ section: "STEAM ACCOUNT", cat: "library" });
  rows.push(toggleRow("Show games you own but haven't installed", steamAccountHint(),
    () => !!s.steamShowOwned, v => set(() => s.steamShowOwned = v)));
  rows.push(secretRow("Steam Web API key", s,
    "Optional. Only needed if your Steam profile keeps its game details private. Free from steamcommunity.com/dev/apikey — any domain name will do",
    () => s.steamApiKey, v => set(() => s.steamApiKey = v)));

  rows.push({ section: "EPIC, GOG & XBOX", cat: "library" });
  rows.push(storeRow("epic", "Epic Games account",
    "Sign in to list every game you own on the Epic Games Store. Anything not installed shows greyed out and installs from here"));
  rows.push(storeRow("gog", "GOG account",
    "Sign in to list every game you own on GOG. Installs go through GOG Galaxy when it is here, and through gog.com when it is not"));
  rows.push(storeRow("xbox", "Xbox account",
    "Sign in with your Microsoft account to list the PC games on your Xbox profile — the ones it has seen you play"));
  rows.push(toggleRow("Show the PC Game Pass catalogue", gamePassHint(),
    () => !!s.gamePassCatalog, v => set(() => s.gamePassCatalog = v)));
  rows.push(secretRow("Xbox sign-in app id", s,
    "Optional. Only if Microsoft stops accepting the Xbox app's own sign-in: the client id of an app registration of your own. See the README",
    () => s.xboxClientId, v => set(() => s.xboxClientId = v)));

  // Collections are made from a game's own menu, so this is only the other half of that: the
  // place to get rid of one. Nothing lists them otherwise now that the tab is gone.
  if (S.collections.length) {
    rows.push({ section: "COLLECTIONS", cat: "library" });
    S.collections.forEach(c => {
      const n = (c.gameIds || []).length;
      rows.push({
        name: c.name,
        hint: n === 1 ? "1 game · filter by it with [[X]] on the library" : `${n} games · filter by it with [[X]] on the library`,
        type: "action", label: "Delete", danger: true,
        action: () => askDeleteCollection(c),
      });
    });
  }

  rows.push({ section: "ARTWORK & METADATA", cat: "library" });
  rows.push({
    name: "Refresh artwork & metadata",
    hint: "Covers, descriptions and scores are fetched automatically in the background. Nothing below needs setting up",
    type: "action", label: "Refresh",
    action: () => { send({ cmd: "refreshMetadata" }); toast("Fetching in the background"); },
  });
  // Everything from here down is an escape hatch, not a setup step. Worth keeping visible -- some
  // people would rather not route anything through a shared service -- but the hints have to say
  // plainly that leaving them alone is the normal thing to do.
  rows.push(secretRow("SteamGridDB key", s,
    "Optional. Use your own key instead of the shared service. Free from steamgriddb.com",
    () => s.steamGridDbKey, v => set(() => s.steamGridDbKey = v)));
  rows.push(secretRow("IGDB client ID", s,
    "Optional. Register an application at dev.twitch.tv to use your own instead of the shared service",
    () => s.igdbClientId, v => set(() => s.igdbClientId = v)));
  rows.push(secretRow("IGDB client secret", s,
    "The secret from the same Twitch application. Stored in plain text in settings.json",
    () => s.igdbClientSecret, v => set(() => s.igdbClientSecret = v)));
  rows.push({
    name: "Metadata service", hint: "Where the shared lookups go. Leave blank for the built-in one",
    type: "action", label: s.metadataEndpoint ? "Custom" : "Default",
    action: () => openInput("METADATA SERVICE URL", s.metadataEndpoint || "",
      v => set(() => s.metadataEndpoint = v.trim())),
  });

  rows.push({ section: "STARTUP, WAKE & LOCK SCREEN", cat: "general" });
  rows.push(toggleRow("Launch Consolify at login", "Registers a startup entry so the launcher is ready after wake or reboot",
    () => s.launchOnStartup, v => set(() => s.launchOnStartup = v)));
  rows.push({
    name: "Couch setup guide", hint: "Gamepad keyboard layout, PIN sign-in, controller wake, auto-start",
    type: "action", label: "Open guide",
    action: () => { guideOpen = true; $("overlay-guide").classList.add("active"); },
  });
  rows.push({
    name: "Restore default settings",
    hint: "Puts every setting back the way a fresh install has it. Your games and collections are untouched",
    type: "action", label: "Restore", danger: true,
    action: () => {
      confirmState = {
        title: "Restore default settings?",
        body: "Theme, accent, display, gamepad and keyboard settings all go back to their defaults. Your library, collections and playtime are not affected.",
        yesLabel: "Restore defaults",
        icon: "refresh", danger: true,
        onYes: () => send({ cmd: "resetSettings" }),
      };
      confirmIdx = 0;
      $("overlay-confirm").classList.add("active");
      renderConfirm();
    },
  });
  rows.push({
    name: "Exit Consolify", type: "action", label: "Exit", danger: true,
    action: () => send({ cmd: "exitApp" }),
  });
  return rows;
}


/*
 * A credential. Shown masked because these rows sit on a TV, which is the one screen in the house
 * most likely to have someone else looking at it -- but the last four characters stay visible so
 * you can tell a key that is set from a key that is set *wrong* without clearing it to find out.
 *
 * A is the only way in, and it opens the usual text prompt with the real value to edit.
 */
function secretRow(name, s, hint, get, setV) {
  const cur = () => get() || "";
  return {
    name, hint, type: "action",
    label: cur() ? (cur().length <= 4 ? "••••" : "••••" + cur().slice(-4)) : "Not set",
    action: () => openInput(name.toUpperCase(), cur(), v => setV(v.trim())),
  };
}
/* The state of the Steam link, in one line under its toggle. It names the account because the
   account is read off the Steam client rather than typed in, and the wrong one would otherwise be
   invisible; it carries the count because an empty answer and a broken fetch look identical. */
function steamAccountHint() {
  const a = S.steamAccount;
  if (!a || !a.steamId) return "No Steam login was found on this PC. Sign in to Steam once, then rescan";
  const who = `Signed in to Steam as ${a.personaName || a.steamId}`;
  if (!S.settings || !S.settings.steamShowOwned)
    return `${who}. Lists your whole Steam library, with anything not on disk greyed out and installable with [[A]]`;
  if (a.error) return `${who} · ${a.error}`;
  if (a.fetchedAt) return `${who} · ${a.ownedCount} game${a.ownedCount === 1 ? "" : "s"} in your library`;
  return `${who} · fetching your library…`;
}

/* One row per store you sign in to, the way Playnite's library plugins work. Being signed in IS the
   opt-in: there is no separate toggle, because a store you have signed into and then hidden the
   games of is two settings saying opposite things. The hint carries the account name, because a
   wrong account would otherwise be invisible, and the count, because an empty answer and a broken
   fetch look identical. */
function storeRow(store, name, offHint) {
  const st = S.stores && S.stores[store];
  const signedIn = !!(st && st.signedIn);
  let hint = offHint;
  if (signedIn) {
    const who = st.user ? `Signed in as ${st.user}` : "Signed in";
    hint = st.error ? `${who} · ${st.error}`
      : st.fetchedAt ? `${who} · ${st.count} game${st.count === 1 ? "" : "s"}`
      : `${who} · fetching your library…`;
  }
  return {
    name, hint, type: "action", label: signedIn ? "Sign out" : "Sign in",
    action: () => {
      if (signedIn) askSignOut(store, name);
      else { toast("Opening the sign-in window…"); send({ cmd: "storeSignIn", store }); }
    },
  };
}

function askSignOut(store, name) {
  confirmState = {
    title: `Sign out of ${name.replace(/ account$/i, "")}?`,
    body: "The games you own there leave the library. Anything installed stays, and signing in again brings the rest back.",
    yesLabel: "Sign out",
    icon: "x", danger: true,
    onYes: () => send({ cmd: "storeSignOut", store }),
  };
  confirmIdx = 0;
  $("overlay-confirm").classList.add("active");
  renderConfirm();
}

function gamePassHint() {
  const g = S.stores && S.stores.gamePass;
  if (!S.settings || !S.settings.gamePassCatalog)
    return "Every game included with PC Game Pass, installable from here. Playing one needs the Xbox app and a subscription";
  if (g && g.error) return g.error;
  return g && g.count ? `${g.count} games in the catalogue` : "Fetching the catalogue…";
}

function toggleRow(name, hint, get, setV) {
  return {
    name, hint, type: "toggle", value: get(),
    adjust: () => setV(!get()),
    action: () => setV(!get()),
  };
}

/* Both row builders fall back when a setting is missing. A settings file written by an older
   build has no key for an option added since, and one undefined value used to throw inside the
   formatter and take the whole settings screen down with it. */
function sliderRow(name, get, min, max, step, setV, fmt, hint) {
  const cur = () => { const v = get(); return typeof v === "number" && isFinite(v) ? v : min; };
  return {
    name, hint, type: "slider", value: cur(), min, max, fmt,
    adjust: (dir) => {
      let v = Math.round((cur() + dir * step) / step) * step;
      v = Math.max(min, Math.min(max, v));
      setV(v);
    },
  };
}

/* `labels` renames an option on screen without changing what is stored: "Builtin" is the value
   the host has always written to settings.json, but "Consolify Keyboard" is what it is called. */
function cycleRow(name, options, get, setV, hint, warn, labels) {
  const cur = () => (options.includes(get()) ? get() : options[0]);
  return {
    name, hint, warn, type: "select", value: (labels && labels[cur()]) || cur(),
    adjust: (dir) => {
      const i = (options.indexOf(cur()) + dir + options.length) % options.length;
      setV(options[i]);
    },
  };
}

/* A row whose options are gamepad buttons or combos. The stored value is the XInput name, as it
   always was; what is shown is the button drawn for the pad last used. */
function buttonRow(name, options, get, setV, hint, warn) {
  const row = cycleRow(name, options, get, setV, hint, warn);
  row.valueHtml = comboHtml(options.includes(get()) ? get() : options[0]);
  return row;
}

/* The accent picker. ◂ ▸ walk the presets so the common case never needs a keyboard, and A opens
   a text prompt for a hex code from anywhere on the row.

   A hand-typed colour joins the strip as an extra stop on the end rather than snapping to the
   nearest preset, and cycling off it drops it again -- so the strip only ever shows colours you
   can actually land on, and there is no dead stop to press through. */
function accentRow(s, set) {
  const cur = () => (isHexColor(s.accentColor) ? s.accentColor.toUpperCase() : DEFAULT_ACCENT);
  const custom = () => !ACCENTS.some(a => a.hex === cur());
  const stops = () => (custom() ? [...ACCENTS.map(a => a.hex), cur()] : ACCENTS.map(a => a.hex));

  return {
    name: "Accent colour",
    hint: "Focus rings, active tabs and sliders. Every shade of it is derived from this one value",
    type: "swatch",
    swatches: ACCENTS.map(a => a.hex),
    value: cur(),
    label: accentName(cur()),
    custom: custom(),
    adjust: (dir) => set(() => {
      const list = stops();
      s.accentColor = list[(list.indexOf(cur()) + dir + list.length) % list.length];
    }),
    action: () => openInput("Accent colour (hex, e.g. #F0A253)", cur(), (v) => {
      const hex = v.startsWith("#") ? v : "#" + v;
      if (isHexColor(hex)) set(() => s.accentColor = hex.toUpperCase());
      else toast("Enter a colour as #RRGGBB");
    }),
  };
}

const KEYBOARD_APP_LABELS = {
  Builtin: "Consolify Keyboard",
  TabTip: "Windows touch keyboard",
  Osk: "Windows on-screen keyboard",
};

/* ---- settings categories ----
   One screen of every setting had become unreadable. The rows are unchanged; they are just
   filtered to the active category, reached with the sidebar. The shoulders are deliberately not
   bound: they used to move between Library, Collections and Settings, and with one screen left
   there is nothing for them to do -- so the legend does not offer them either. */
const SETTINGS_TABS = [
  { id: "general",  label: "General" },
  { id: "input",    label: "Controller" },
  { id: "keyboard", label: "Keyboard" },
  { id: "library",  label: "Library" },
];
let settingsTab = "general";
/* Which half of the screen has the highlight. Settings opens on the sidebar, so the first thing
   you choose is what you are configuring rather than being dropped into a list of rows; A (or
   Right) steps into the options and B steps back out to the categories. */
let settingsPane = "nav";

function settingsRows() {
  let cat = null;
  return allSettingsRows().filter(r => {
    if (r.section) cat = r.cat;
    return (r.section ? r.cat : cat) === settingsTab;
  });
}

function setSettingsTab(id) {
  if (settingsTab !== id) { settingsTab = id; settingsIdx = 0; }
  renderSettings();
}

function settingsTabIdx() {
  const i = SETTINGS_TABS.findIndex(t => t.id === settingsTab);
  return i < 0 ? 0 : i;
}

function enterSettingsPane(pane) {
  settingsPane = pane;
  if (pane === "rows") settingsIdx = Math.min(settingsIdx, Math.max(0, settingsRows().filter(r => !r.section).length - 1));
  renderSettings();
}

function cycleSettingsTab(dir) {
  const i = settingsTabIdx();
  setSettingsTab(SETTINGS_TABS[(i + dir + SETTINGS_TABS.length) % SETTINGS_TABS.length].id);
}

function renderSettingsNav() {
  const nav = $("settingsNav");
  if (!nav) return;
  let cat = null;
  const counts = {};
  allSettingsRows().forEach(r => {
    if (r.section) { cat = r.cat; return; }
    counts[cat] = (counts[cat] || 0) + 1;
  });

  nav.innerHTML = "";
  SETTINGS_TABS.forEach(t => {
    const el = document.createElement("div");
    el.className = "set-tab" + (t.id === settingsTab ? " active" : "");
    el.dataset.focusable = "";
    el.dataset.focusKey = "settab:" + t.id;
    el.dataset.settingsTab = t.id;
    el.innerHTML = `<span>${esc(t.label)}</span><span class="set-tab-count">${counts[t.id] || 0}</span>`;
    el.addEventListener("click", () => { setFocusEl(el); setSettingsTab(t.id); });
    el.addEventListener("mouseenter", () => {
      if (!hoverEnabled()) return;
      // Repaint the highlight; do NOT rebuild the list. renderSettings() replaces every tab node,
      // and a node destroyed between mousedown and mouseup never raises a click -- which is why
      // the categories could not be clicked at all. The option rows already guard against this.
      setFocusEl(el);
      paintNav();
    });
    nav.appendChild(el);
  });
}

function renderSettings() {
  renderSettingsNav();
  // The legend changes with the pane: on the categories B leaves Settings, inside the options it
  // only steps back to the categories, and saying so is cheaper than letting people find out.
  const footEl = $("settingsFoot");
  // No LB/RB entry: there is one screen left, so the shoulders switch between nothing. A legend
  // that names a button which does not respond is worse than a shorter legend.
  if (footEl) footEl.innerHTML = settingsPane === "nav"
    ? foot(["A", "Open"], ["B", "Back"], ["DpadV", "Category"])
    : foot(["A", "Select"], ["B", "Categories"], ["DpadH", "Adjust"]);
  const rows = settingsRows();
  const scroll = $("settingsScroll");
  scroll.innerHTML = "";

  const focusables = rows.filter(r => !r.section);
  settingsIdx = Math.max(0, Math.min(settingsIdx, focusables.length - 1));
  let fi = -1;

  rows.forEach(r => {
    if (r.section) {
      const el = document.createElement("div");
      el.className = "set-section";
      el.textContent = r.section;
      scroll.appendChild(el);
      return;
    }
    fi++;
    const idx = fi;
    const el = document.createElement("div");
    el.className = "set-row";
    el.dataset.focusable = "";
    el.dataset.focusKey = "setrow:" + settingsTab + ":" + idx;
    el.dataset.rowIndex = idx;
    // Left/Right adjust the value here instead of moving; settingsInput reads this.
    if (r.adjust) el.dataset.navLock = "horizontal";

    // The arrows carry a direction, so a mouse can step a value either way (see the click below).
    const left = `<span class="arrow" data-dir="-1">◂</span>`, rightArrow = `<span class="arrow" data-dir="1">▸</span>`;
    let right = "";
    if (r.type === "toggle") {
      right = r.value
        ? `${left}<span class="set-toggle-on">ON</span>${rightArrow}`
        : `${left}<span class="set-toggle-off">OFF</span>${rightArrow}`;
    } else if (r.type === "select") {
      right = `${left}<span>${r.valueHtml || esc(r.value)}</span>${rightArrow}`;
    } else if (r.type === "swatch") {
      // The presets are shown as dots, the selected one ringed, with a trailing dot for a custom
      // colour so the strip reads as the row's full range rather than a value plus a mystery.
      const dot = (hex, on) =>
        `<span class="swatch${on ? " on" : ""}" style="background:${esc(hex)}"></span>`;
      const dots = r.swatches.map(hex => dot(hex, hex === r.value)).join("")
        + (r.custom ? dot(r.value, true) : "");
      right = `${left}<span class="swatch-strip">${dots}</span>`
        + `<span class="swatch-name">${esc(r.label)}</span>${rightArrow}`;
    } else if (r.type === "slider") {
      const pct = ((r.value - r.min) / (r.max - r.min)) * 100;
      right = `<div class="slider">${left}<div class="slider-track"><div class="slider-fill" style="width:${pct}%"></div></div>${rightArrow}<span class="slider-val">${esc(r.fmt(r.value))}</span></div>`;
    } else if (r.type === "action") {
      right = `<span class="set-action-label${r.danger ? " danger" : ""}">${esc(r.label)}</span>`;
    }

    el.innerHTML = `<div class="set-left"><div class="set-name">${esc(r.name)}</div>${r.hint ? `<div class="set-hint">${hintHtml(r.hint)}</div>` : ""}${r.warn ? `<div class="set-warn">${hintHtml(r.warn)}</div>` : ""}</div><div class="set-value">${right}</div>`;

    el.addEventListener("mouseenter", () => {
      if (!hoverEnabled()) return;
      if (settingsIdx === idx && settingsPane === "rows") return;
      settingsIdx = idx; settingsPane = "rows"; renderSettings();
    });
    el.addEventListener("click", (e) => {
      settingsIdx = idx; settingsPane = "rows";
      const row = settingsRows().filter(x => !x.section)[idx];
      // On an arrow, step that way; anywhere else on the row is the same as pressing A.
      const arrow = e.target instanceof Element ? e.target.closest(".arrow") : null;
      if (arrow && row.adjust) { row.adjust(parseInt(arrow.dataset.dir, 10) || 1); return; }
      if (row.action) row.action(); else if (row.adjust) row.adjust(1);
    });
    scroll.appendChild(el);
  });

  // Keep the highlight on the row the caller has selected, then let the engine paint.
  const scope = document.getElementById("screen-settings");
  if (settingsPane === "rows") setScopeKey(scope, "setrow:" + settingsTab + ":" + settingsIdx);
  else setScopeKey(scope, "settab:" + settingsTab);
  paintNav();
  const cur = focusEl(scope);
  if (cur && focusVisible()) revealFocus(cur);
}

/* Settings keeps its two panes, but they are now just two groups of focusables in one
   scope: the categories on the left, the rows on the right. Movement is geometric, and
   the pane is derived from where the highlight actually landed rather than tracked by
   hand -- which is what lets a theme stack them, or drop the sidebar entirely.

   Left/Right are the exception to pure geometry: on a row they adjust the value, because
   that is what ◂ ▸ mean everywhere else in this UI. A row marks itself data-nav-lock. */
function settingsInput(btn) {
  const scope = document.getElementById("screen-settings");
  const el = focusEl(scope);
  const rows = settingsRows().filter(r => !r.section);
  const row = el && el.dataset.rowIndex !== undefined ? rows[parseInt(el.dataset.rowIndex, 10)] : null;
  const onTab = !!(el && el.dataset.settingsTab);

  switch (btn) {
    case "Up": case "Down":
      if (navMove(btn)) syncSettingsPane();
      break;

    case "Left": case "Right":
      if (row && el.dataset.navLock === "horizontal") { row.adjust(btn === "Right" ? 1 : -1); break; }
      if (navMove(btn)) syncSettingsPane();
      break;

    case "A":
      if (!focusVisible()) break;
      // The category comes off the element, not off settingsTab. Hovering a category highlights
      // it without selecting it, so A was opening whichever one had last been activated -- the
      // highlight said Keyboard and the rows that appeared were Controller's, which reads as A
      // not working at all.
      if (onTab) { setSettingsTab(el.dataset.settingsTab); enterSettingsPane("rows"); break; }
      if (row) { if (row.action) row.action(); else if (row.adjust) row.adjust(1); }
      break;

    // Back steps out to the categories first, and only leaves Settings from there.
    case "B":
      if (onTab) switchView("library");
      else enterSettingsPane("nav");
      break;
  }
}

/** Pane and category follow the highlight, so nothing has to be kept in step by hand. */
function syncSettingsPane() {
  const el = focusEl(document.getElementById("screen-settings"));
  if (!el) return;
  if (el.dataset.settingsTab) {
    settingsPane = "nav";
    if (el.dataset.settingsTab !== settingsTab) setSettingsTab(el.dataset.settingsTab);
    else renderSettings();
  } else if (el.dataset.rowIndex !== undefined) {
    settingsPane = "rows";
    settingsIdx = parseInt(el.dataset.rowIndex, 10);
    renderSettings();
  }
}

function scheduleSave() {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => send({ cmd: "saveSettings", settings: S.settings }), 350);
}

/* ============================== filter overlay ============================== */

/* filterLevel: null = the Filter/Sort menu, "filter"/"sort" = an open dropdown */
let filterLevel = null;

function applyFilter() {
  focus = { zone: "grid", row: 0, col: 0 };
  renderLibrary();
  renderFilter();
}

function openFilter() {
  filterOpen = true; filterIdx = 0; filterLevel = null;
  $("overlay-filter").classList.add("active");
  renderFilter();
}

function closeFilter() {
  filterOpen = false; filterLevel = null;
  $("overlay-filter").classList.remove("active");
}

/** Rows of the top-level Filter / Sort menu. */
function filterMenuRows() {
  const n = activeFilterCount();
  return [
    { name: "Search", icon: "search", summary: F.search ? `“${F.search}”` : "By title", run: () => { closeFilter(); openSearch(); } },
    { name: "Filter", icon: "filter", summary: n ? `${n} active` : "All games", open: "filter" },
    { name: "Sort", icon: "sort", summary: SORTS.find(s => s.id === F.sort).label, open: "sort" },
  ];
}

/** Flat list of the multi-select dropdown, with category headers interleaved. */
function filterDropdownRows() {
  const rows = [{ cat: "PLATFORM" }];
  const platformRow = (p, icon, sub) => ({
    label: p, icon, sub, checked: F.platforms.has(p),
    toggle: () => { F.platforms.has(p) ? F.platforms.delete(p) : F.platforms.add(p); },
  });
  PLATFORMS.forEach(p => rows.push(platformRow(p, "gamepad")));
  // One row per system there are ROMs for, under their own heading. Same set as the stores --
  // ticking SNES and Steam shows both -- so a system is a platform in every sense the filter has.
  const systems = emulatedPlatforms();
  if (systems.length) {
    rows.push({ cat: "EMULATED" });
    systems.forEach(s => rows.push(platformRow(s.name, "cartridge", `${s.count}`)));
  }
  rows.push({ cat: "STATUS" });
  STATUSES.forEach(s => rows.push({
    label: s, icon: s === "Installed" ? "checkCircle" : "download", checked: F.status.has(s),
    toggle: () => { F.status.has(s) ? F.status.delete(s) : F.status.add(s); },
  }));
  // Only when there are some. An empty category reads as a broken feature, and collections are
  // made from the game menu rather than here, so there is nothing to offer until one exists.
  if (S.collections.length) {
    rows.push({ cat: "COLLECTIONS" });
    S.collections.forEach(c => rows.push({
      label: c.name, icon: "folder", sub: `${(c.gameIds || []).length}`,
      checked: F.collections.has(c.id),
      toggle: () => { F.collections.has(c.id) ? F.collections.delete(c.id) : F.collections.add(c.id); },
    }));
  }
  rows.push({ cat: "OTHER" });
  rows.push({
    label: "Favorites only", icon: "star", checked: F.fav,
    toggle: () => { F.fav = !F.fav; },
  });
  // The only way back to something you hid, short of turning the filter off again. Counted so the
  // row says how many there are: an empty hidden view and a broken filter look the same.
  const hiddenCount = S.games.filter(g => g.hidden).length;
  rows.push({
    label: "Hidden games", icon: "eyeOff", sub: `${hiddenCount}`,
    checked: F.hidden,
    toggle: () => { F.hidden = !F.hidden; },
  });
  return rows;
}

const SORT_ICONS = {
  az: "sortAsc", za: "sortDesc", recent: "clock",
  played: "timer", sizeDesc: "chevronsDown", sizeAsc: "chevronsUp", score: "star",
};

function sortDropdownRows() {
  return SORTS.map(s => ({
    label: s.label, icon: SORT_ICONS[s.id], checked: F.sort === s.id, radio: true,
    toggle: () => { F.sort = s.id; },
  }));
}

function currentFilterRows() {
  if (filterLevel === "filter") return filterDropdownRows();
  if (filterLevel === "sort") return sortDropdownRows();
  return filterMenuRows();
}

/** Indices of rows that can take focus (category headers can't). */
function filterFocusable(rows) {
  return rows.map((r, i) => r.cat ? -1 : i).filter(i => i >= 0);
}

function renderFilter() {
  const rows = currentFilterRows();
  const focusable = filterFocusable(rows);
  if (!focusable.includes(filterIdx)) filterIdx = focusable[0] ?? 0;

  $("filterTitle").textContent =
    filterLevel === "filter" ? "FILTER" : filterLevel === "sort" ? "SORT" : "FILTER & SORT";

  const footHtml = filterLevel === null
    ? foot(["A", "Open"], ["B", "Close"], ["Y", "Reset all"])
    : foot(["A", filterLevel === "sort" ? "Choose" : "Toggle"], ["B", "Back"], ["Y", "Reset all"]);

  renderMenu($("filterList"), $("filterFoot"), rows, filterIdx, footHtml,
    (i) => { if (filterIdx !== i) { filterIdx = i; renderFilter(); } },
    (i) => { filterIdx = i; filterActivate(); });
}

function filterActivate() {
  const row = currentFilterRows()[filterIdx];
  if (!row) return;
  if (row.run) { row.run(); return; }
  if (row.open) { filterLevel = row.open; filterIdx = 0; renderFilter(); return; }
  row.toggle();
  if (row.radio) { filterLevel = null; filterIdx = 2; }  // sort is single-select: pick and close
  applyFilter();
}

function filterInput(btn) {
  const rows = currentFilterRows();
  switch (btn) {
    // Category headers are not focusable, so the engine steps over them without the
    // caller having to keep its own list of which rows can be landed on.
    case "Up": case "Down": filterIdx = menuStep(btn, filterIdx, rows.length); renderFilter(); break;
    case "A": case "Right": if (focusVisible()) filterActivate(); break;
    case "Y": resetFilters(); applyFilter(); toast("Filters and sort reset"); break;
    case "Left": case "B":
      if (filterLevel) { filterLevel = null; filterIdx = 0; renderFilter(); }
      else if (btn === "B") closeFilter();
      break;
    case "X": closeFilter(); break;
  }
}

/* ============================== game context menu (Y) ============================== */

let gameMenu = null;   // { gameId, from, idx }

function openGameMenu(gameId, from) {
  gameMenu = { gameId, from, idx: 0 };
  $("overlay-gamemenu").classList.add("active");
  renderGameMenu();
}

function closeGameMenu() {
  gameMenu = null;
  $("overlay-gamemenu").classList.remove("active");
}

function gameMenuItems() {
  if (!gameMenu) return [];
  const g = gameById(gameMenu.gameId);
  if (!g) return [];
  const running = S.gameRunning && S.runningGameId === g.id;
  // A fixed order, whatever the game: View game first, then what gets you playing (Resume,
  // Install, the other stores), then the rest. Art is changed from the detail page's Manage.
  const top = [
    { label: "View game", icon: "info", sub: "Full details page", action: () => { const f = gameMenu.from; closeGameMenu(); openDetail(g.id, f); } },
  ];
  if (running) top.push({ label: "Resume game", icon: "play", sub: "Back to the running game",
    action: () => { closeGameMenu(); send({ cmd: "resumeGame" }); } });
  if (!g.installed && canInstall(g)) top.push({ label: "Install", icon: "download",
    sub: `Through ${storeName(g)}; the tile turns playable when it is done`,
    action: () => { closeGameMenu(); offerInstall(g); } });
  // The same game in the other stores it is in. Played from here once, not made the default --
  // that is what Manage > Launch with is for.
  editionsOf(g).filter(m => m.id !== g.id).forEach(m => {
    if (m.installed) top.push({ label: `Play on ${m.platform}`, icon: "play",
      sub: "Just this once. Manage → Launch with changes the default",
      action: () => { closeGameMenu(); launchGame(m); } });
    else if (canInstall(m)) top.push({ label: `Install on ${m.platform}`, icon: "download",
      sub: `Through ${storeName(m)}`,
      action: () => { closeGameMenu(); offerInstall(m); } });
  });
  const items = [
    ...top,
    { label: g.favorite ? "Remove from favorites" : "Add to favorites", icon: "star", action: () => send({ cmd: "toggleFavorite", id: g.id }) },
    { label: "Add to collection", icon: "folderPlus", action: () => { closeGameMenu(); openCollect(g.id); } },
    // Hiding is done to the whole game: hiding only the Steam copy would just bring the Xbox one
    // out from behind it as a tile of its own.
    { label: g.hidden ? "Unhide" : "Hide", icon: g.hidden ? "eye" : "eyeOff",
      sub: g.hidden ? "Show in the library again" : "Not a game? Keep it out of the library",
      action: () => { send({ cmd: "setHidden", ids: editionsOf(g).map(m => m.id), hidden: !g.hidden }); closeGameMenu(); } },
  ];
  if (running) items.push({ label: "Close game", icon: "x", danger: true,
    action: () => { closeGameMenu(); send({ cmd: "closeGame" }); } });
  // Deleting the entry for the game you are in the middle of playing is never what you meant,
  // so this one only shows while it is not running.
  if (g.manual && !running) items.push({
    label: "Remove from library", icon: "trash", danger: true,
    action: () => { send({ cmd: "removeGame", id: g.id }); closeGameMenu(); },
  });
  return items;
}

function renderGameMenu() {
  if (!gameMenu) return;
  const g = gameById(gameMenu.gameId);
  $("gameMenuTitle").textContent = (g ? g.title : "GAME").toUpperCase();
  const items = gameMenuItems();
  gameMenu.idx = Math.max(0, Math.min(gameMenu.idx, items.length - 1));
  renderMenu($("gameMenuList"), $("gameMenuFoot"), items, gameMenu.idx,
    foot(["A", "Select"], ["B", "Back"]),
    (i) => { if (gameMenu.idx !== i) { gameMenu.idx = i; renderGameMenu(); } },
    (i) => items[i].action());
}

function gameMenuInput(btn) {
  const items = gameMenuItems();
  switch (btn) {
    case "Up": case "Down": gameMenu.idx = menuStep(btn, gameMenu.idx, items.length); renderGameMenu(); break;
    case "A": if (focusVisible() && items[gameMenu.idx]) items[gameMenu.idx].action(); break;
    case "B": case "Y": closeGameMenu(); break;
  }
}

/* ============================== add-to-collection overlay ============================== */

let collectTarget = null;

function collectItems() {
  const g = gameById(collectTarget);
  if (!g) return [];
  const items = [{
    label: "Favorites", icon: "star", star: true, checked: g.favorite,
    action: () => { send({ cmd: "toggleFavorite", id: g.id }); },
  }, {
    label: "Hidden", icon: "eyeOff", checked: g.hidden,
    action: () => { send({ cmd: "toggleHidden", id: g.id }); },
  }];
  S.collections.forEach(c => items.push({
    label: c.name, icon: "folder", checked: c.gameIds.includes(g.id),
    action: () => send({ cmd: "toggleInCollection", collectionId: c.id, id: g.id }),
  }));
  items.push({
    label: "New collection…", icon: "folderPlus",
    action: () => {
      closeCollect();
      openInput("NEW COLLECTION NAME", "", name => send({ cmd: "createCollection", name, gameId: g.id }));
    },
  });
  return items;
}

function openCollect(gameId) {
  collectTarget = gameId || detailGameId;
  collectOpen = true; collectIdx = 0;
  $("overlay-collect").classList.add("active");
  renderCollect();
}
function closeCollect() { collectOpen = false; $("overlay-collect").classList.remove("active"); }

function renderCollect() {
  const items = collectItems();
  collectIdx = Math.max(0, Math.min(collectIdx, items.length - 1));
  renderMenu($("collectList"), $("collectFoot"), items, collectIdx,
    foot(["A", "Toggle"], ["B", "Back"]),
    (i) => { if (collectIdx !== i) { collectIdx = i; renderCollect(); } },
    (i) => items[i].action());
}

function collectInput(btn) {
  const items = collectItems();
  switch (btn) {
    case "Up": case "Down": collectIdx = menuStep(btn, collectIdx, items.length); renderCollect(); break;
    case "A": if (focusVisible() && items[collectIdx]) items[collectIdx].action(); break;
    case "B": closeCollect(); break;
  }
}

/* ============================== manage overlay ============================== */

/* How this game actually starts, in one line. */
function launchRoute(g) {
  if (g.emulated) { const e = emulatorFor(g); return e ? `Through ${e.name}` : "No emulator is set for it"; }
  if (g.preferDirectLaunch && g.exePath) return g.exePath;
  if (g.platform === "Steam") return "Through the Steam client";
  if (g.platform === "Epic") return "Through the Epic Games launcher";
  if (g.platform === "Xbox") return "Through the Xbox app";
  return g.exePath || "Directly from its executable";
}

function manageItems() {
  const g = gameById(detailGameId);
  if (!g) return [];
  const items = [];
  // Only for a game in more than one store. Picking one makes it what the tile launches, over
  // the default of "whichever is installed, Steam first".
  const editions = editionsOf(g);
  if (editions.length > 1) {
    items.push({ cat: "LAUNCH WITH" });
    // Listed in the fixed store order, not in rank order. Ranked, the chosen store jumped to the
    // top the moment it was chosen, and the row under the highlight changed out from beneath it.
    const byStore = (a, b) => EDITION_ORDER.indexOf(a.platform) - EDITION_ORDER.indexOf(b.platform);
    [...editions].sort(byStore).forEach(m => items.push({
      label: m.platform, icon: platformIcon(m), radio: true,
      checked: m.id === g.id,
      action: () => {
        if (m.id === g.id) return;
        preferEdition(m);
        detailGameId = m.id;
        renderDetail();
        renderManage();
        renderLibrary();
        toast(`${g.title} now launches with ${m.platform}${m.installed ? "" : " (not installed yet)"}`);
      },
    }));
    items.push({ cat: "THIS COPY" });
  }
  if (g.emulated) {
    // A ROM has no executable of its own to change; what it has is an emulator, a title that
    // was guessed from its file name, and a command line that is the emulator's template
    // unless overridden here. "Remove" is absent on purpose: the file would be found again on
    // the next scan. Hide is the way to keep one out.
    const emu = emulatorFor(g);
    const folder = romFolderById(g.romFolderId);
    const template = g.args || (folder && folder.args) || (emu && emu.args) || '"{rom}"';
    items.push({
      label: "Rename", icon: "edit",
      sub: "The name is read off the file. Fixing it is how a wrongly matched game fetches the right details",
      action: () => {
        closeManage();
        openInput("GAME TITLE", g.title, v => { if (v && v !== g.title) send({ cmd: "setTitle", id: g.id, title: v }); });
      },
    });
    items.push({
      label: "Run with", icon: "cartridge", sub: launchRoute(g),
      action: () => { closeManage(); openEmulatorChoice(g); },
    });
    items.push({
      label: "Set launch arguments", icon: "terminal",
      sub: g.args ? g.args : `Uses ${emu ? emu.name + "'s" : "the emulator's"} own: ${template}`,
      action: () => {
        closeManage();
        openInput("LAUNCH ARGUMENTS ({rom} is the file)", g.args || template, v => send({ cmd: "setArgs", id: g.id, args: v === template ? "" : v }));
      },
    });
  } else {
    items.push({
      label: "Set launch arguments", icon: "terminal", sub: g.args || "e.g. --launcher-skip",
      action: () => {
        closeManage();
        openInput("LAUNCH ARGUMENTS", g.args || "", v => send({ cmd: "setArgs", id: g.id, args: v }));
      },
    });
    items.push({
      label: "Change executable", icon: "file",
      // The subtitle carries the launch route, which used to sit under the title on the detail
      // page. It is troubleshooting detail: it belongs on the screen you open to change it.
      sub: launchRoute(g),
      action: () => { send({ cmd: "pickExe", id: g.id }); closeManage(); },
    });
    if (g.preferDirectLaunch && (g.platform === "Steam" || g.platform === "Epic"))
      items.push({ label: `Launch through ${g.platform} again`, icon: "store", action: () => { send({ cmd: "launchViaStore", id: g.id }); closeManage(); } });
  }
  // Two pictures, two entries. They are different shapes and they appear in different places, so
  // one "change artwork" that set both would put whichever file was chosen into a slot it is the
  // wrong shape for. The subtitles say where each one shows up, because "cover" and "tile" are
  // our words for them and nobody else's.
  items.push({
    label: "Change tile art", icon: "image", sub: "Library tiles · 1.75:1",
    action: () => { send({ cmd: "pickCover", id: g.id, slot: "tile" }); closeManage(); },
  });
  items.push({
    label: "Change cover art", icon: "image", sub: "Portrait box art · 2:3",
    action: () => { send({ cmd: "pickCover", id: g.id, slot: "cover" }); closeManage(); },
  });
  // Only once there is something to undo. A picked file outranks every source for good -- see
  // Assign -- so without this there is no way back to the fetched art short of editing the JSON.
  if (isCustomArt(g.bannerFile)) items.push({
    label: "Use the fetched tile art again", icon: "refresh",
    action: () => { send({ cmd: "resetArt", id: g.id, slot: "tile" }); closeManage(); },
  });
  if (isCustomArt(g.coverFile)) items.push({
    label: "Use the fetched cover again", icon: "refresh",
    action: () => { send({ cmd: "resetArt", id: g.id, slot: "cover" }); closeManage(); },
  });
  if (g.manual) items.push({
    label: "Remove from library", icon: "trash", danger: true,
    action: () => { send({ cmd: "removeGame", id: g.id }); closeManage(); switchView(detailReturn); },
  });
  return items;
}

function openManage() {
  manageOpen = true; manageIdx = 0;
  $("overlay-manage").classList.add("active");
  renderManage();
}
function closeManage() { manageOpen = false; $("overlay-manage").classList.remove("active"); }

function renderManage() {
  const items = manageItems();
  manageIdx = Math.max(0, Math.min(manageIdx, items.length - 1));
  // A section heading cannot take the highlight; start on the row under it.
  while (items[manageIdx] && items[manageIdx].cat && manageIdx < items.length - 1) manageIdx++;
  renderMenu($("manageList"), $("manageFoot"), items, manageIdx,
    foot(["A", "Select"], ["B", "Back"]),
    (i) => { if (manageIdx !== i) { manageIdx = i; renderManage(); } },
    (i) => items[i].action());
}

function manageInput(btn) {
  const items = manageItems();
  switch (btn) {
    case "Up": case "Down": manageIdx = menuStep(btn, manageIdx, items.length); renderManage(); break;
    case "A": if (focusVisible() && items[manageIdx]) items[manageIdx].action(); break;
    case "B": closeManage(); break;
  }
}

/* ============================== choice overlay ==============================
 *
 * A pick-one list for whatever needs one and has no menu of its own: which system a ROM folder
 * is for, which emulator runs a game, what to do with a folder. The same card and rows as every
 * other menu. Choosing an item closes the list and then runs the item, so an item that opens
 * another list simply does -- which is how the two-step folder wizard is built out of it.
 */
let choiceState = null;   // { title, items, idx, onBack }

function openChoice(title, items, opts = {}) {
  const idx = Math.max(0, items.findIndex(i => !i.cat && i.checked));
  choiceState = { title, items, idx, onBack: opts.onBack || null };
  $("overlay-choice").classList.add("active");
  renderChoice();
}

function closeChoice() { choiceState = null; $("overlay-choice").classList.remove("active"); }

function renderChoice() {
  if (!choiceState) return;
  const { title, items } = choiceState;
  const focusable = items.map((r, i) => r.cat ? -1 : i).filter(i => i >= 0);
  if (!focusable.includes(choiceState.idx)) choiceState.idx = focusable[0] ?? 0;
  $("choiceTitle").textContent = title.toUpperCase();
  renderMenu($("choiceList"), $("choiceFoot"), items, choiceState.idx,
    foot(["A", "Choose"], ["B", choiceState.onBack ? "Back" : "Cancel"]),
    (i) => { if (choiceState && choiceState.idx !== i) { choiceState.idx = i; renderChoice(); } },
    (i) => { if (choiceState) { choiceState.idx = i; choiceActivate(); } });
}

function choiceActivate() {
  const item = choiceState && choiceState.items[choiceState.idx];
  if (!item || item.cat || !item.action) return;
  closeChoice();
  item.action();
}

function choiceInput(btn) {
  switch (btn) {
    case "Up": case "Down":
      choiceState.idx = menuStep(btn, choiceState.idx, choiceState.items.length);
      renderChoice();
      break;
    case "A": if (focusVisible()) choiceActivate(); break;
    case "B": { const back = choiceState.onBack; closeChoice(); if (back) back(); break; }
  }
}

/* ============================== emulators and ROM folders ==============================
 *
 * The host owns the lists (see the emu* commands in UiBridge); the page only asks. Adding a
 * folder is a three-step thing -- the host's folder dialog, then "which system", then "which
 * emulator" -- and the last two are lists here, where a gamepad can answer them. The folder's
 * name seeds the system, so the usual answer is A, A.
 */
let romWizard = null;        // { path, platformId } while a folder is being added
let pendingEmuPick = null;   // called with the new emulator's id after "Add an emulator…"

function coreName(path) { return String(path || "").split(/[\\/]/).pop().replace(/_libretro\.dll$/i, ""); }
function usesCore(emu) { return !!emu && /\{core\}/i.test(emu.args || ""); }

function startRomFolderWizard(path, guess) {
  romWizard = { path, platformId: guess || null };
  const name = path.split(/[\\/]/).filter(Boolean).pop() || path;
  openPlatformChoice(guess, id => { romWizard.platformId = id; wizardPickEmulator(); }, null,
    `Which system is ${name}?`);
}

function wizardPickEmulator() {
  const w = romWizard;
  openEmulatorPick(w.platformId, null, id => {
    send({ cmd: "romFolderAdd", path: w.path, platformId: w.platformId, emulatorId: id || "" });
    romWizard = null;
  }, () => startRomFolderWizard(w.path, w.platformId), { allowNone: true });
}

/* The catalogue, with the current or guessed system first so the likely answer is under the
   highlight. The extensions ride along as the subtitle: they are what "which system" means to
   the scan, and the quickest way to tell Sega CD (.cue, .chd) from Genesis (.md, .bin). */
function openPlatformChoice(currentId, onPick, onBack, title) {
  const list = S.emulation && S.emulation.platforms || [];
  const items = list.map(p => ({
    label: p.name, icon: "cartridge", radio: true, checked: p.id === currentId,
    sub: (p.extensions || []).slice(0, 6).map(e => "." + e).join("  "),
    action: () => onPick(p.id),
  }));
  const i = items.findIndex(x => x.checked);
  if (i > 0) items.unshift(items.splice(i, 1)[0]);
  openChoice(title || "Which system?", items, { onBack });
}

/* The emulators set up, the ones known to run this system first. "Add an emulator…" opens the
   host's file dialog and comes back through the emuAdded message with the new id. */
function openEmulatorPick(platformId, currentId, onPick, onBack, opts = {}) {
  const emus = [...(S.emulation && S.emulation.emulators || [])];
  const fits = e => (e.platforms || []).includes(platformId);
  emus.sort((a, b) => (fits(b) ? 1 : 0) - (fits(a) ? 1 : 0) || a.name.localeCompare(b.name));
  const items = emus.map(e => ({
    label: e.name, icon: "gamepad", radio: true, checked: e.id === currentId,
    sub: fits(e) ? "Runs this system" : e.exePath.split(/[\\/]/).pop(),
    action: () => onPick(e.id),
  }));
  items.push({
    label: "Add an emulator…", icon: "folderPlus", sub: "Point to its .exe. The common ones set themselves up",
    action: () => { pendingEmuPick = onPick; send({ cmd: "emuAdd" }); },
  });
  if (opts.allowNone) items.push({
    label: "Decide later", icon: "clock", sub: "The games are listed now and get an emulator when the folder does",
    action: () => onPick(""),
  });
  openChoice(opts.title || "Which emulator runs it?", items, { onBack });
}

/* Per game, from Manage: its own emulator, or back to its folder's. */
function openEmulatorChoice(g) {
  const folder = romFolderById(g.romFolderId);
  const folderEmu = folder ? emulatorById(folder.emulatorId) : null;
  const emus = S.emulation && S.emulation.emulators || [];
  const items = [{
    label: folderEmu ? `${folderEmu.name} — the folder's choice` : "The folder's emulator (none set yet)",
    icon: "romFolder", radio: true, checked: !g.emulatorId,
    action: () => send({ cmd: "setEmulator", id: g.id, emulatorId: "" }),
  }];
  emus.forEach(e => items.push({
    label: e.name, icon: "gamepad", radio: true, checked: g.emulatorId === e.id,
    sub: (e.platforms || []).includes(g.platformId) ? "Runs this system" : undefined,
    action: () => send({ cmd: "setEmulator", id: g.id, emulatorId: e.id }),
  }));
  openChoice(`Run ${g.title} with`, items);
}

function openRomFolderOptions(f) {
  const p = platformDef(f.platformId);
  const emu = emulatorById(f.emulatorId);
  const exts = f.extensions && f.extensions.length ? f.extensions : (p ? p.extensions : []);
  const again = () => openRomFolderOptions(romFolderById(f.id) || f);
  const items = [
    { label: "System", icon: "cartridge", sub: p ? p.name : f.platformId,
      action: () => openPlatformChoice(f.platformId, id => send({ cmd: "romFolderUpdate", id: f.id, platformId: id }), again) },
    { label: "Emulator", icon: "gamepad", sub: emu ? emu.name : "None set — the games cannot start until one is",
      action: () => openEmulatorPick(f.platformId, f.emulatorId, id => send({ cmd: "romFolderUpdate", id: f.id, emulatorId: id }), again) },
  ];
  if (usesCore(emu)) items.push({
    label: "Core", icon: "chip", sub: f.core ? coreName(f.core) : "Not chosen — pick one before playing",
    action: () => send({ cmd: "romFolderPickCore", id: f.id }),
  });
  items.push({
    label: "Launch arguments for this folder", icon: "terminal",
    sub: f.args || `Uses ${emu ? emu.name + "'s own" : "the emulator's own"}`,
    action: () => openInput("FOLDER LAUNCH ARGUMENTS", f.args || (emu ? emu.args : ""),
      v => send({ cmd: "romFolderUpdate", id: f.id, args: emu && v === emu.args ? "" : v })),
  });
  // A playlist lists its files by name; there are no extensions to choose.
  if (!f.playlist) items.push({
    label: "File types", icon: "file", sub: exts.map(e => "." + e).join("  "),
    action: () => openInput("FILE TYPES, COMMA SEPARATED", exts.join(", "),
      v => send({ cmd: "romFolderUpdate", id: f.id, extensions: v })),
  });
  items.push({
    label: f.playlist ? "Remove this playlist" : "Remove this folder", icon: "trash", danger: true,
    action: () => {
      const n = S.games.filter(g => g.romFolderId === f.id).length;
      confirmState = {
        title: `Remove ${p ? p.name : "this folder"}?`,
        body: `${f.path} leaves the library${n ? ` with its ${n} game${n === 1 ? "" : "s"}` : ""}. Nothing on disk is touched, and it will not be found again by itself.`,
        yesLabel: "Remove", icon: "trash", danger: true,
        onYes: () => send({ cmd: "romFolderRemove", id: f.id }),
      };
      confirmIdx = 0;
      $("overlay-confirm").classList.add("active");
      renderConfirm();
    },
  });
  openChoice(p ? p.name : "ROM folder", items);
}

function openEmulatorOptions(e) {
  const users = (S.emulation && S.emulation.romFolders || []).filter(f => f.emulatorId === e.id);
  const items = [
    { label: "Rename", icon: "edit", sub: e.name,
      action: () => openInput("EMULATOR NAME", e.name, v => { if (v) send({ cmd: "emuUpdate", id: e.id, name: v }); }) },
    { label: "Change program", icon: "file", sub: e.exePath, action: () => send({ cmd: "emuPickExe", id: e.id }) },
    { label: "Launch arguments", icon: "terminal", sub: e.args || '"{rom}"',
      action: () => openInput("LAUNCH ARGUMENTS ({rom} is the file)", e.args || "", v => send({ cmd: "emuUpdate", id: e.id, args: v })) },
    { label: "Remove", icon: "trash", danger: true,
      action: () => {
        confirmState = {
          title: `Remove ${e.name}?`,
          body: users.length
            ? `${users.length} ROM folder${users.length === 1 ? "" : "s"} use${users.length === 1 ? "s" : ""} it and will need another emulator. The program itself is not touched.`
            : "The program itself is not touched.",
          yesLabel: "Remove", icon: "trash", danger: true,
          onYes: () => send({ cmd: "emuRemove", id: e.id }),
        };
        confirmIdx = 0;
        $("overlay-confirm").classList.add("active");
        renderConfirm();
      } },
  ];
  openChoice(e.name, items);
}

/* ============================== confirm overlay ============================== */

/*
 * A dialog, not a menu. It used to be a one-row list -- a single highlighted option that could
 * not be moved off, with the explanation squeezed into that row's subtitle -- which read as a
 * menu with something missing. Now it is what a console asks you with: a heading, the details,
 * and the two buttons that answer it, A to go ahead and B to back out. There is nothing to
 * navigate, so nothing is highlighted.
 *
 * Defaults suit the destructive cases, which is most of them; an install or a game swap passes
 * its own icon and clears `danger`, since starting something is not a red action.
 */
function renderConfirm() {
  if (!confirmState) return;
  const danger = confirmState.danger !== false;
  $("confirmDialog").classList.toggle("danger", danger);
  $("confirmIcon").innerHTML = iconSvg(confirmState.icon || "trash");
  $("confirmTitle").textContent = confirmState.title;
  $("confirmBody").textContent = confirmState.body || "";
  $("confirmBody").hidden = !confirmState.body;
  $("confirmYesLabel").textContent = confirmState.yesLabel || "Delete";
}

function confirmChoose(yes) {
  const st = confirmState;
  confirmState = null;
  $("overlay-confirm").classList.remove("active");
  if (yes && st) st.onYes();
}

/* A answers regardless of where the pointer is: the dialog is the only thing on screen that can
   take an answer, so there is no "A over empty space" to guard against. */
function confirmInput(btn) {
  switch (btn) {
    case "A": confirmChoose(true); break;
    case "B": confirmChoose(false); break;
  }
}

$("confirmYes").addEventListener("click", () => confirmChoose(true));
$("confirmNo").addEventListener("click", () => confirmChoose(false));
// A click on the dimmed screen around the dialog is a no, the way it is everywhere else.
$("overlay-confirm").addEventListener("click", (e) => { if (e.target.id === "overlay-confirm") confirmChoose(false); });

/* ============================== text input overlay ============================== */

function openInput(title, value, onConfirm) {
  inputOpen = true;
  inputConfirm = onConfirm;
  $("inputTitle").textContent = title;
  const field = $("inputField");
  field.value = value;
  $("overlay-input").classList.add("active");
  send({ cmd: "focusPage" });   // see openSearch: no keystrokes reach the page without it
  setTimeout(() => { field.focus(); field.select(); }, 50);
  setTimeout(() => { if (document.activeElement !== field) { field.focus(); field.select(); } }, 250);
  send({ cmd: "showKeyboard" });
}

function closeInput(confirmed) {
  const cb = inputConfirm;
  const value = $("inputField").value.trim();
  inputOpen = false;
  inputConfirm = null;
  $("overlay-input").classList.remove("active");
  $("inputField").blur();
  send({ cmd: "hideKeyboard" });
  if (confirmed && cb) cb(value);
}

$("inputField").addEventListener("keydown", (e) => {
  e.stopPropagation();
  if (e.key === "Enter") closeInput(true);
  if (e.key === "Escape") closeInput(false);
});

/*
 * The keyboard toggle can be bound to View, and in Press mode the host takes the toggle button
 * outright -- the page never saw the press, so on the library View raised the keyboard and search
 * never opened. The page now says when it wants View itself: on the library, with nothing on top
 * of it and no search already open. Everywhere else the toggle keeps the button. Search raises the
 * keyboard anyway, so nothing is lost.
 *
 * Published on change, from the input path and on a short timer as well, because overlays open
 * from the mouse and the host too, not only from the pad.
 */
let claimedButtons = "";
function publishClaims() {
  const overlayUp = overlayOpen() || radialOpen || !!radialSub || ingameOpen
    || document.body.classList.contains("overlay-mode");
  const want = view === "library" && !overlayUp && !searchOpen ? "View" : "";
  if (want === claimedButtons) return;
  claimedButtons = want;
  send({ cmd: "claimButtons", buttons: want ? [want] : [] });
}
setInterval(publishClaims, 400);

/* ============================== library search ==============================
 *
 * A field in the library top bar, so it is there in every theme -- both slot the top bar, and
 * Polish hides the section headings. View opens it and raises the on-screen keyboard; every
 * keystroke refilters the grid live. Enter, A or Down keeps the search and drops the highlight on
 * the first result; Escape or B while typing clears it. With a search standing, the field shows
 * it and the grid heading says so, and View opens it again to change it.
 *
 * Matching is by folded title (see titleKey), across every store a game is in, and it is a
 * substring match on words -- "hollow" finds Hollow Knight and Hollow Knight: Silksong.
 */
let searchOpen = false;
let searchReturnKey = null;   // where the highlight was before View, for B to put it back

function searchKey(text) { return titleKey(text); }

function setSearch(value) {
  F.search = String(value || "").trim();
  const field = document.getElementById("searchField");
  if (field && field.value !== value) field.value = value || "";
  const box = document.getElementById("libSearch");
  if (box) box.classList.toggle("has-query", !!F.search);
}

function openSearch() {
  if (view !== "library") switchView("library");
  const scope = document.getElementById("screen-library");
  const box = $("libSearch");
  if (!searchOpen) {
    const was = scope.dataset.focusCurrent || null;
    searchReturnKey = was === "search" ? null : was;
  }
  searchOpen = true;
  box.classList.add("active");
  // The highlight moves up to the field, so what is being driven is what is lit.
  setInputMode("pad");
  setFocusEl(box);
  paintNav();
  const field = $("searchField");
  field.value = F.search;
  // The page only receives keystrokes once the HOST has put keyboard focus into the WebView --
  // the launcher is driven by the pad and nothing in the page is normally focused, so a DOM
  // focus() alone gives a field with no caret that the on-screen keyboard types into nothing.
  send({ cmd: "focusPage" });
  const place = () => { field.focus(); field.setSelectionRange(field.value.length, field.value.length); };
  place();
  setTimeout(place, 80);
  setTimeout(place, 250);
  send({ cmd: "showKeyboard" });
}

function closeSearch(keep) {
  if (!searchOpen) return;
  searchOpen = false;
  $("libSearch").classList.remove("active");
  $("searchField").blur();
  send({ cmd: "hideKeyboard" });
  if (!keep) setSearch("");
  renderLibrary();
  const scope = document.getElementById("screen-library");
  // Kept: straight onto the first result, which is the point of having searched. Cleared: back
  // to wherever the highlight was before View, so a cancelled search changes nothing.
  const first = keep && F.search && document.querySelector("#gridScroll [data-focusable]");
  if (first) setFocusEl(first);
  else if (!keep && searchReturnKey) setScopeKey(scope, searchReturnKey);
  if (!focusEl(scope)) ensureFocus(scope);
  searchReturnKey = null;
  setInputMode("pad");
  afterFocusMove();
}

function searchInput(btn) {
  switch (btn) {
    case "A": case "Down": closeSearch(true); break;
    case "B": closeSearch(false); break;
  }
}

$("searchField").addEventListener("input", (e) => { setSearch(e.target.value); renderLibrary(); });
$("searchField").addEventListener("keydown", (e) => {
  e.stopPropagation();
  if (e.key === "Enter" || e.key === "ArrowDown") { e.preventDefault(); closeSearch(true); }
  if (e.key === "Escape") { e.preventDefault(); closeSearch(false); }
});
$("libSearch").addEventListener("mousedown", (e) => { if (!searchOpen) { e.preventDefault(); openSearch(); } });
$("libSearch").addEventListener("mouseenter", () => { if (hoverEnabled() && !searchOpen) { setFocusEl($("libSearch")); paintNav(); } });

/* ============================== couch setup guide ============================== */

const GUIDE_STEPS = [
  ["Switch the touch keyboard to the Gamepad layout (one time)", `Windows does not expose this as a setting an app can flip, so do it once by hand and it sticks. Open the touch keyboard (the keyboard button, ${slot("RB", true)} unless you changed it), tap the <b>cog icon</b> in its top-left, open <b>Keyboard layout</b> and choose <b>Gamepad</b>. You then get controller navigation with button accelerators — <b>X</b> backspace, <b>Y</b> space. On the default layout the keyboard ignores the pad entirely. Requires Windows 11 build 26100.3624 or newer.`],
  ["Sign in from the couch: set up a Windows Hello PIN", "Apps cannot type into the secure lock screen, but you don't need one: in <b>Settings → Accounts → Sign-in options</b>, add a <b>PIN (Windows Hello)</b>. The sign-in screen's PIN pad works with the touch keyboard, which supports gamepad input — so after a wake you can sign in without leaving the sofa. For a fully hands-off couch PC, enable automatic sign-in instead (<b>netplwiz</b>, untick \"Users must enter a user name and password\")."],
  ["Let your controller's receiver wake the PC", "Open <b>Device Manager</b> and find your gamepad's USB receiver (under <b>Human Interface Devices</b> or <b>Xbox Peripherals</b>). Open its <b>Power Management</b> tab and tick <b>Allow this device to wake the computer</b>. Pressing the controller button will then wake the PC from sleep."],
  ["Auto-start Consolify", "Turn on <b>Launch Consolify at login</b> in Settings → Startup so the PC lands straight back on the TV with gamepad-mouse active after waking."],
];

function renderGuide() {
  $("guideBody").innerHTML = GUIDE_STEPS.map(([t, txt], i) =>
    `<div class="guide-step"><div class="guide-num">${String(i + 1).padStart(2, "0")}</div><div class="guide-step-body"><div class="guide-step-title">${t}</div><div class="guide-step-text">${txt}</div></div></div>`
  ).join("");
}

function guideInput(btn) {
  const body = $("guideBody");
  switch (btn) {
    case "Up": animateScroll(body, "y", scrollTarget(body, "y") - 160); break;
    case "Down": animateScroll(body, "y", scrollTarget(body, "y") + 160); break;
    case "B": case "A": guideOpen = false; $("overlay-guide").classList.remove("active"); break;
  }
}

/**
 * The still the host grabbed of whatever was on screen before the menu opened. Deliberately
 * NOT cleared when overlay mode ends: the in-game menu drops overlay mode on its way into the
 * radial, and clearing here would blank the background halfway through that hop. It is simply
 * replaced by the next capture, and null (a capture the host could not make, e.g. a game in
 * exclusive fullscreen) falls back to the menu's own solid ground.
 */
function setOverlayShot(dataUri) {
  const el = $("overlayShot");
  el.style.backgroundImage = dataUri ? `url("${dataUri}")` : "none";
  el.classList.toggle("has-shot", !!dataUri);
}

/* ---- overlay mode: a menu over a still of the desktop or the game behind it ---- */
function setOverlayMode(on) {
  overlayMode = on;
  document.documentElement.classList.toggle("overlay-mode", on);
  document.body.classList.toggle("overlay-mode", on);
  if (on) {
    setInputMode("pad");   // the pointer has no business here
    // Not redundant with the send inside setInputMode: if we were already in pad mode that
    // call returns early, and the host could still be sitting on a stale "pointer".
    send({ cmd: "inputMode", mode: "pad" });
  }
}

/** Nearest radial spoke for a stick vector (y is up from the pad, down in screen space). */
function stickToSpoke(x, y, count) {
  const ang = Math.atan2(-y, x) * 180 / Math.PI;      // screen-space degrees
  const step = 360 / count;
  return ((Math.round((ang + 90) / step) % count) + count) % count;
}

/* ============================== view switching ============================== */

function switchView(v) {
  view = v;
  document.querySelectorAll(".screen").forEach(s => s.classList.remove("active"));
  $("screen-" + v).classList.add("active");
  if (v === "library") { clampFocus(); updateLibraryFocus(); }
  if (v === "detail") renderDetail();
  // Always land on the categories, never mid-list in whatever was open last time.
  if (v === "settings") { settingsPane = "nav"; settingsIdx = 0; renderSettings(); setBackdrop(null); }
}


/* ============================== input routing ============================== */

const DIRECTIONS = new Set(["Up", "Down", "Left", "Right"]);

function handleInput(btn, src) {
  if (inputOpen) return; // the text field (and the touch keyboard's own pad support) owns input
  // Only a direction hands control back to the pad. A face button must never re-arm a
  // highlight the pointer has cleared, so A over empty space does nothing.
  if (DIRECTIONS.has(btn)) setInputMode("pad");
  if (confirmState) { confirmInput(btn); return; }
  if (searchOpen) { searchInput(btn); return; }
  if (radialSub) { radialSubInput(btn); return; }
  if (radialOpen) { radialInput(btn); return; }
  if (ingameOpen) { ingameInput(btn); return; }
  if (guideOpen) { guideInput(btn); return; }
  if (filterOpen) { filterInput(btn); return; }
  if (gameMenu) { gameMenuInput(btn); return; }
  if (collectOpen) { collectInput(btn); return; }
  if (manageOpen) { manageInput(btn); return; }
  if (choiceState) { choiceInput(btn); return; }

  if (view === "library") libraryInput(btn);
  else if (view === "detail") detailInput(btn);
  else if (view === "settings") settingsInput(btn);
}

// After every press, so the claim follows the screen the press just led to.
const routeInput = handleInput;
handleInput = function (btn, src) { routeInput(btn, src); publishClaims(); };

/*
 * The keyboard, as a pad. What each key stands for is what the legend draws for it (see
 * BUTTON_ART.keyboard), so the two have to move together: Enter is A, Esc is B, the letters are
 * the letters, the brackets are the shoulders, "/" opens search and M opens Settings.
 */
const KEYMAP = {
  ArrowUp: "Up", ArrowDown: "Down", ArrowLeft: "Left", ArrowRight: "Right",
  Enter: "A", Space: "A", Escape: "B", Backspace: "B",
  KeyY: "Y", KeyX: "X", KeyM: "Menu",
  BracketLeft: "LB", BracketRight: "RB",
  Slash: "View",
};
// By key as well as by physical code: some keyboards and injected input carry only the one.
const KEYMAP_BY_KEY = {
  ArrowUp: "Up", ArrowDown: "Down", ArrowLeft: "Left", ArrowRight: "Right",
  Enter: "A", " ": "A", Escape: "B", Backspace: "B",
  y: "Y", Y: "Y", x: "X", X: "X", m: "Menu", M: "Menu",
  "[": "LB", "]": "RB", "/": "View",
};

const KEY_REPEAT_MS = 85;
let lastKeyStepAt = -Infinity;

window.addEventListener("keydown", (e) => {
  if (inputOpen) return;
  const btn = KEYMAP[e.code] || KEYMAP_BY_KEY[e.key];
  if (!btn || e.ctrlKey || e.altKey || e.metaKey) return;
  e.preventDefault();
  setInputFamily("keyboard");
  // A held direction walks on; a held Enter must not launch the game twice.
  if (e.repeat && !DIRECTIONS.has(btn)) return;
  // Windows repeats a held key about 30 times a second. Every one of those used to be handled,
  // each with a layout pass, faster than the page could paint -- so the screen froze while the
  // key was held and jumped to the end when it was let go. Repeats are paced to a rate the eye
  // can follow, and the ones in between are dropped rather than queued.
  const now = performance.now();
  if (e.repeat && now - lastKeyStepAt < KEY_REPEAT_MS) return;
  lastKeyStepAt = now;
  handleInput(btn, "kb");   // handleInput switches to pad mode on directions only
});

/*
 * The mouse, as a pad. Buttons only: a real click means a hand is on the mouse, where a mousemove
 * can be the left stick driving the pointer. The right button is "back", and on a game it is that
 * game's menu -- the two things a mouse otherwise cannot do. A click on the dimmed screen around
 * any menu is "back" as well, as it already was on the confirm dialog.
 */
/* A click the host sent from a pad's touchpad is not the mouse. The host says so just before
   sending it, and the two can arrive in either order, so the note is kept for a moment AND puts
   the pad family back in case the click got here first. */
let padClickAt = -Infinity;
window.addEventListener("mousedown", () => {
  if (performance.now() - padClickAt > 400) setInputFamily("keyboard");
}, true);

window.addEventListener("contextmenu", (e) => {
  e.preventDefault();
  if (inputOpen) return;
  const tile = e.target instanceof Element ? e.target.closest("[data-game-id]") : null;
  if (tile && view === "library" && !overlayOpen() && !overlayMode) {
    setFocusEl(tile);
    setPointerOnItem(true);
    updateLibraryFocus(true);
    libraryAccept("Y");
    return;
  }
  handleInput("B", "mouse");
});

document.querySelectorAll(".overlay").forEach(ov => {
  if (ov.id === "overlay-confirm") return;   // has its own, and answers "no"
  ov.addEventListener("click", (e) => {
    if (e.target !== ov) return;
    if (ov.id === "overlay-input") closeInput(false);
    else handleInput("B", "mouse");
  });
});

/* The text prompt's own hints. Always keys, whatever is in hand: it is a text field. */
function renderInputHint() {
  const el = $("inputHint");
  if (!el) return;
  el.innerHTML = `<div class="legend-item" id="inputOk">${keycap("Enter")}<span>Confirm</span></div>` +
    `<div class="legend-item" id="inputCancel">${keycap("Esc")}<span>Cancel</span></div>`;
  $("inputOk").addEventListener("click", () => closeInput(true));
  $("inputCancel").addEventListener("click", () => closeInput(false));
}

/* ============================== host messages ============================== */

let lastBatteryMsg = null;

function handleHostMessage(m) {
  switch (m.type) {
    case "state": {
      const firstState = S.settings === null;
      const wasEmpty = S.games.length === 0;
      S.games = m.games || [];
      buildEditions();
      S.collections = m.collections || [];
      // A collection can be deleted while it is still being filtered on. Left alone, the stale id
      // matches nothing and the library goes empty with no visible reason why.
      for (const id of [...F.collections])
        if (!S.collections.some(c => c.id === id)) F.collections.delete(id);

      S.settings = m.settings;
      S.displays = m.displays || [];
      // Keep whatever the last themes push carried if this state has none, so a state
      // refresh cannot blank the list between watcher events.
      S.themes = m.themes || S.themes;
      S.startupRegistered = m.startupRegistered;
      S.gameRunning = m.gameRunning;
      S.runningGameId = m.runningGameId;
      S.scanning = m.scanning;
      S.steamAccount = m.steamAccount || null;
      S.stores = m.stores || null;
      S.emulation = m.emulation || null;
      if (S.settings) S.settings.launchOnStartup = m.startupRegistered;
      applyTheme();
      // First real library: let clampFocus drop the highlight onto the first game rather
      // than leaving it wherever the empty screen had put it.
      if ((firstState || wasEmpty) && S.games.length) clearFocus(document.getElementById("screen-library"));
      renderLibrary();
      if (view === "settings") renderSettings();
      if (view === "detail") renderDetail();
      if (collectOpen) renderCollect();
      if (manageOpen) renderManage();
      if (gameMenu) renderGameMenu();
      if (filterOpen) renderFilter();
      if (choiceState) renderChoice();
      if (firstState && S.settings && !S.settings.tvDeviceName && S.displays.length > 1) {
        switchView("settings");
        toast("Welcome — pick which display is your TV");
      }
      break;
    }
    case "scanning":
      S.scanning = m.busy;
      $("scanStatus").textContent = m.busy ? "SCANNING…" : "";
      if (!m.busy && view === "settings") renderSettings();
      break;
    case "pad":
      // The family rides with the press, so the legend is right for the pad that was just used.
      setInputFamily(m.layout);
      handleInput(m.button, "pad");
      break;
    // The pad in hand changed, or one was picked up again after the keyboard had the legend.
    case "padLayout":
      setInputFamily(m.layout);
      break;
    case "padClick":
      padClickAt = performance.now();
      setInputFamily(padFamily);
      break;
    case "stickScroll":
      onStickScroll(m.v || 0);
      break;
    case "overlay":
      overlayTargetTitle = m.targetTitle || "";
      setOverlayShot(m.shot);
      setOverlayMode(true);
      hostWindows = m.windows || [];
      if (m.runningGameId !== undefined) S.runningGameId = m.runningGameId;
      if (m.mode === "radial") openRadial(m.targetTitle);
      else if (m.mode === "ingame") openIngame();
      break;
    case "windows":
      hostWindows = m.windows || [];
      if (radialSub === "windows") renderRadialSub();
      break;
    // Thumbnails arrive one at a time, after the list is already on screen -- see
    // StartThumbnails on the host for why they are not part of it.
    case "windowThumb": {
      const w = hostWindows.find(x => x.handle === m.handle);
      if (w && m.image) {
        w.thumb = m.image;
        w.thumbIsIcon = !!m.icon;
        if (radialSub === "windows") renderRadialSub();
      }
      break;
    }
    case "dismiss":
      dismissOverlays();
      break;
    case "stick":
      if (radialOpen && !radialSub) {
        const i = stickToSpoke(m.x, m.y, RADIAL_ITEMS.length);
        if (i !== radialIdx) { radialIdx = i; renderRadial(); }
      }
      break;
    // A theme file changed on disk. The list carries a fresh cache-busting stamp, so
    // re-applying reloads the stylesheet without a restart.
    case "themes":
      S.themes = m.themes || [];
      applyTheme();
      if (view === "settings") renderSettings();
      break;

    case "inputMode":
      // Only the host can tell us the stick moved the cursor, or that the launcher just came
      // back to the foreground and the pad should be driving again. It never pushes "pad" off
      // a face button, so this can't re-arm a highlight the pointer has cleared.
      // "pointer" from the host is the stick and nothing else, so it is the pad being used.
      if (m.mode === "pointer") setInputFamily(padFamily);
      setInputMode(m.mode);
      break;
    case "padConnected":
      S.padConnected = m.connected;
      if (m.connected) toast("Controller connected");
      else updateBattery(null);
      if (lastBatteryMsg && m.connected) updateBattery(lastBatteryMsg);
      break;
    case "battery":
      lastBatteryMsg = m;
      updateBattery(m);
      break;
    case "game":
      S.gameRunning = m.running;
      S.runningGameId = m.id;
      renderLibrary();
      break;
    // The host's folder dialog closed on a folder; the rest of adding it is asked here.
    case "romFolderPicked":
      startRomFolderWizard(m.path, m.platformId);
      break;
    // "Add an emulator…" from a pick list came back -- with an id, or with null when the file
    // dialog was cancelled. Either way the pick list that asked for it is put back up, so a
    // cancelled dialog cannot leave the folder wizard hanging with nothing on screen.
    case "emuAdded": {
      const cb = pendingEmuPick;
      pendingEmuPick = null;
      if (cb && m.id) cb(m.id);
      else if (cb && romWizard) wizardPickEmulator();
      break;
    }
    case "toast":
      toast(m.message);
      break;
  }
}

if (HOST) HOST.addEventListener("message", (e) => handleHostMessage(e.data));

/* ============================== mock (browser preview only) ============================== */

const mockWindows = [
  { handle: 1, title: "Cyberpunk 2077", processName: "Cyberpunk2077", minimized: false, display: "" },
  { handle: 2, title: "Steam", processName: "steam", minimized: false, display: "" },
  { handle: 3, title: "Downloads - File Explorer", processName: "explorer", minimized: true, display: "" },
];

const mockCollections = [
  { id: "c1", name: "Cozy evenings", gameIds: ["gog:salttide", "manual:foundrynine"] },
];

/* A slice of the host's catalogue, enough to walk the folder wizard and the options lists. */
const mockEmulation = {
  emulators: [
    { id: "e1", name: "RetroArch", exePath: "C:\\RetroArch\\retroarch.exe", args: '-L "{core}" "{rom}" -f', preset: "retroarch", platforms: ["nes", "snes", "n64", "gb", "gba", "ps1", "genesis", "arcade"], detected: true },
    { id: "e2", name: "DuckStation", exePath: "C:\\Emulators\\DuckStation\\duckstation-qt-x64-ReleaseLTCG.exe", args: '-batch -fullscreen -- "{rom}"', preset: "duckstation", platforms: ["ps1"] },
  ],
  romFolders: [
    { id: "f1", path: "D:\\ROMs\\SNES", platformId: "snes", emulatorId: "e1", core: "C:\\RetroArch\\cores\\snes9x_libretro.dll", args: null, extensions: null, recurse: true },
    { id: "f2", path: "D:\\ROMs\\PS1", platformId: "ps1", emulatorId: "e1", core: null, args: null, extensions: null, recurse: true },
    { id: "f3", path: "D:\\ROMs\\Arcade", platformId: "arcade", emulatorId: null, core: null, args: null, extensions: ["zip"], recurse: false },
    { id: "f4", path: "C:\\RetroArch\\playlists\\Nintendo - Game Boy Advance.lpl", platformId: "gba", emulatorId: "e1", core: "C:\\RetroArch\\cores\\mgba_libretro.dll", args: null, extensions: null, recurse: true, playlist: true, detected: true },
  ],
  platforms: [
    { id: "nes", name: "Nintendo Entertainment System", shortName: "NES", extensions: ["nes", "fds", "zip", "7z"], hasCores: true },
    { id: "snes", name: "Super Nintendo", shortName: "SNES", extensions: ["sfc", "smc", "zip", "7z"], hasCores: true },
    { id: "n64", name: "Nintendo 64", shortName: "N64", extensions: ["n64", "z64", "v64", "zip"], hasCores: true },
    { id: "gba", name: "Game Boy Advance", shortName: "GBA", extensions: ["gba", "zip", "7z"], hasCores: true },
    { id: "ps1", name: "PlayStation", shortName: "PS1", extensions: ["cue", "chd", "pbp", "m3u"], hasCores: true },
    { id: "ps2", name: "PlayStation 2", shortName: "PS2", extensions: ["iso", "chd", "cso"], hasCores: true },
    { id: "genesis", name: "Sega Genesis / Mega Drive", shortName: "Genesis", extensions: ["md", "gen", "bin", "zip"], hasCores: true },
    { id: "arcade", name: "Arcade", shortName: "Arcade", extensions: ["zip", "7z", "chd"], hasCores: true },
  ],
};

function mockHandle(msg) {
  const pushState = () => {
    const seedW = (t) => `https://picsum.photos/seed/${t.toLowerCase().replace(/[^a-z]/g, "")}w/600/340`;
    const seedP = (t) => `https://picsum.photos/seed/${t.toLowerCase().replace(/[^a-z]/g, "")}/400/480`;
    const seedH = (t) => `https://picsum.photos/seed/${t.toLowerCase().replace(/[^a-z]/g, "")}h/1200/390`;
    const g = (title, platform, opts = {}) => ({
      id: platform.toLowerCase() + ":" + title.toLowerCase().replace(/[^a-z]/g, ""),
      title, platform, installed: true, manual: platform === "Manual",
      playtimeMinutes: 0, sessions: 0, lastPlayed: null, sizeBytes: 0,
      favorite: false, hidden: false, preferDirectLaunch: false, args: null, installUri: null,
      coverFile: seedP(title), bannerFile: seedW(title), heroFile: seedH(title),
      // Stand-in metadata, so the preview exercises the detail page's facts row. Individual
      // entries below override it -- including back to nothing, which is what a game we could
      // not fetch looks like and the case most likely to be got wrong.
      description: "A placeholder blurb standing in for the store's own two-sentence pitch, long "
        + "enough to show where a real one wraps and where the clamp takes over.",
      developer: "Northmoor Studio", publisher: "Northmoor",
      genres: ["Action", "Adventure", "Indie"], releaseDate: "Mar 12, 2021",
      criticScore: 82, criticSource: "Metacritic", controllerSupport: "full", ...opts,
    });
    const now = Date.now();
    const games = [
      g("Hollowmark: Second Ascent", "Steam", { playtimeMinutes: 4934, sessions: 41, favorite: true, lastPlayed: new Date(now - 86400000).toISOString(), sizeBytes: 64.2 * 1024 ** 3, installDir: "C:\\Games\\Steam\\steamapps\\common\\Hollowmark" }),
      // No fetched metadata at all -- the facts row has to fall back to the platform and the
      // description has to collapse rather than leave a gap under the title.
      g("Ridgeline 84", "Epic", { playtimeMinutes: 660, sessions: 9, lastPlayed: new Date(now - 2 * 86400000).toISOString(), sizeBytes: 31 * 1024 ** 3, description: null, developer: null, publisher: null, genres: [], releaseDate: null, criticScore: null, criticSource: null, controllerSupport: null }),
      g("Salt & Tide", "GOG", { playtimeMinutes: 2820, sessions: 30, favorite: true, lastPlayed: new Date(now - 3 * 86400000).toISOString(), sizeBytes: 12 * 1024 ** 3, criticScore: 61, controllerSupport: "partial" }),
      g("Foundry Nine", "Manual", { playtimeMinutes: 360, sessions: 5, lastPlayed: new Date(now - 4 * 86400000).toISOString(), sizeBytes: 8 * 1024 ** 3, criticScore: 38, controllerSupport: null }),
      g("Cassette Run", "Steam", { playtimeMinutes: 180, sessions: 3, lastPlayed: new Date(now - 5 * 86400000).toISOString(), sizeBytes: 4 * 1024 ** 3 }),
      // enough recently-played entries to exercise the Continue carousel
      g("Nightpost", "Steam", { playtimeMinutes: 95, sessions: 2, lastPlayed: new Date(now - 6 * 86400000).toISOString() }),
      g("Umber Fields", "Steam", { playtimeMinutes: 210, sessions: 4, lastPlayed: new Date(now - 7 * 86400000).toISOString() }),
      g("The Quiet Shore", "Epic", { playtimeMinutes: 140, sessions: 3, lastPlayed: new Date(now - 8 * 86400000).toISOString() }),
      g("Vector Bloom", "Steam", { playtimeMinutes: 60, sessions: 1, lastPlayed: new Date(now - 9 * 86400000).toISOString() }),
      g("Marrow", "GOG", { playtimeMinutes: 480, sessions: 7, lastPlayed: new Date(now - 10 * 86400000).toISOString() }),
      g("Halden Court", "Manual", { playtimeMinutes: 30, sessions: 1, lastPlayed: new Date(now - 11 * 86400000).toISOString() }),
      g("Tin Orchard", "Epic", { playtimeMinutes: 75, sessions: 2, lastPlayed: new Date(now - 12 * 86400000).toISOString() }),
      g("Paper Lanterns", "Epic"), g("Ninefold", "Manual"),
      g("Bellwether", "GOG"),
      g("Iron Compass", "Steam", { installed: false, sizeBytes: 42 * 1024 ** 3, installUri: "steam://install/1" }),
      g("Low Country", "GOG", { installed: false, installUri: "goggalaxy://openGameView/1" }),
      g("Solaria Drift", "Epic", { installed: false, installUri: "com.epicgames.launcher://apps/Solaria?action=install" }),
      g("Hollow Reef", "Steam", { installed: false, sizeBytes: 27 * 1024 ** 3, installUri: "steam://install/2" }),
      g("Glassmoor", "Manual", { installed: false, sizeBytes: 55 * 1024 ** 3 }),
      g("Tidewrack", "GOG", { installed: false, sizeBytes: 12 * 1024 ** 3 }),
      g("Forza Horizon 5", "Xbox", { playtimeMinutes: 1240, sessions: 18, sizeBytes: 110 * 1024 ** 3 }),
      g("Sea of Thieves", "Xbox", { sizeBytes: 78 * 1024 ** 3 }),
      // The same game in two stores, to exercise the grouping: one tile, launches the installed Xbox copy.
      g("Forza Horizon 5", "Steam", { installed: false, installUri: "steam://install/1551360" }),
      g("Hollow Reef", "Xbox", { installed: false, installUri: "ms-windows-store://pdp/?productid=9XXXXXXXXXXX" }),
      g("Starfield", "Xbox", { installed: false, installUri: "ms-windows-store://pdp/?productid=9NCJSXWZRJPS" }),
      g("Wallpaper Engine", "Steam", { hidden: true, sizeBytes: 2 * 1024 ** 3 }),
      // ROMs: the platform is the system, and they group with nothing.
      g("Super Mario World", "Super Nintendo", { emulated: true, platformId: "snes", romFolderId: "f1", romPath: "D:\\ROMs\\SNES\\Super Mario World (USA).sfc", playtimeMinutes: 420, sessions: 6, lastPlayed: new Date(now - 86400000 * 1.5).toISOString(), sizeBytes: 512 * 1024, releaseDate: "Nov 21, 1990", developer: "Nintendo EAD", publisher: "Nintendo", genres: ["Platform"], criticScore: 94, criticSource: "IGDB critics" }),
      g("Chrono Trigger", "Super Nintendo", { emulated: true, platformId: "snes", romFolderId: "f1", romPath: "D:\\ROMs\\SNES\\Chrono Trigger (USA).sfc", sizeBytes: 4 * 1024 ** 2, releaseDate: "Mar 11, 1995", genres: ["RPG"], criticScore: 92, criticSource: "IGDB critics" }),
      g("Doom", "Super Nintendo", { emulated: true, platformId: "snes", romFolderId: "f1", romPath: "D:\\ROMs\\SNES\\Doom (USA).sfc", sizeBytes: 2 * 1024 ** 2, description: null, developer: null, publisher: null, genres: [], releaseDate: null, criticScore: null, criticSource: null, controllerSupport: null }),
      g("Final Fantasy VII", "PlayStation", { emulated: true, platformId: "ps1", romFolderId: "f2", romPath: "D:\\ROMs\\PS1\\Final Fantasy VII (USA).m3u", sizeBytes: 1.3 * 1024 ** 3, releaseDate: "Jan 31, 1997", genres: ["RPG"] }),
      g("Crash Bandicoot", "PlayStation", { emulated: true, platformId: "ps1", romFolderId: "f2", emulatorId: "e2", romPath: "D:\\ROMs\\PS1\\Crash Bandicoot (USA).chd", sizeBytes: 320 * 1024 ** 2 }),
      g("sf2", "Arcade", { emulated: true, platformId: "arcade", romFolderId: "f3", romPath: "D:\\ROMs\\Arcade\\sf2.zip", sizeBytes: 3 * 1024 ** 2, description: null, developer: null, publisher: null, genres: [], releaseDate: null, criticScore: null, criticSource: null, controllerSupport: null, coverFile: null, bannerFile: null, heroFile: null }),
      g("Pokemon: Emerald Version", "Game Boy Advance", { emulated: true, platformId: "gba", romFolderId: "f4", romPath: "C:\\RetroArch\\downloads\\GBA\\Pokemon - Emerald Version (USA, Europe).gba", sizeBytes: 16 * 1024 ** 2, releaseDate: "Sep 16, 2004", genres: ["RPG"] }),
    ];
    if (mockHandle._titles) games.forEach(x => { if (mockHandle._titles[x.id]) x.title = mockHandle._titles[x.id]; });
    if (mockHandle._emus) games.forEach(x => { if (mockHandle._emus[x.id] !== undefined) x.emulatorId = mockHandle._emus[x.id] || null; });
    if (mockHandle._fav) games.forEach(x => { if (mockHandle._fav[x.id] !== undefined) x.favorite = mockHandle._fav[x.id]; });
    if (mockHandle._running) { S.gameRunning = true; S.runningGameId = mockHandle._running; }
  if (mockHandle._hidden) games.forEach(x => { if (mockHandle._hidden[x.id] !== undefined) x.hidden = mockHandle._hidden[x.id]; });
    handleHostMessage({
      type: "state",
      games,
      collections: mockCollections,
      steamAccount: { steamId: "76561198000000000", personaName: "couchplayer", ownedCount: 212,
        fetchedAt: new Date().toISOString(), error: null },
      stores: {
        epic: { signedIn: true, user: "couchplayer", count: 35, fetchedAt: new Date().toISOString(), error: null },
        gog: { signedIn: false, user: null, count: 0, fetchedAt: null, error: null },
        xbox: { signedIn: true, user: "CouchGamer", count: 12, fetchedAt: null, error: "The sign-in has expired. Sign in again" },
        gamePass: { count: 0, fetchedAt: null, error: null },
      },
      emulation: mockEmulation,
      settings: S.settings || {
        tvDeviceName: "\\\\.\\DISPLAY2", switchPrimaryOnLaunch: true, repositionGameWindow: true,
        keepFocus: true, launchOnStartup: false, gamepadMouseEnabled: true, gamepadMouseDuringGame: false,
        deadzone: 0.18, sensitivity: 1.0, accelExponent: 1.8, hideCursorSystemWide: false,
        touchpadMouse: true, touchpadSensitivity: 1.0, touchpadTapToClick: true,
        touchpadTapDrag: true, touchpadNaturalScroll: true, touchpadScrollSpeed: 1.0,
        boostButton: "RT", boostMultiplier: 2.5, hideLegend: false, igdbClientId: "", igdbClientSecret: "", steamGridDbKey: "", metadataEndpoint: "",
        steamShowOwned: true, steamApiKey: "", gamePassCatalog: false, xboxClientId: "", detectEmulators: true,
        leftClickButton: "A", rightClickButton: "B",
        minimizeCombo: "LS + RS",
        keyboardToggleButton: "Start", keyboardToggleHoldMs: 600,
        keyboardApp: "Builtin", keyboardScale: 1.0, keyRepeatDelayMs: 350, keyRepeatIntervalMs: 90,
        accentColor: "#F0A253", theme: "",
      },
      displays: [
        { deviceName: "\\\\.\\DISPLAY1", friendlyName: "Dell U2723QE", x: 0, y: 0, width: 3840, height: 2160, isPrimary: true },
        { deviceName: "\\\\.\\DISPLAY2", friendlyName: "LG C3 OLED", x: 3840, y: 0, width: 3840, height: 2160, isPrimary: false },
      ],
      startupRegistered: false, gameRunning: false, runningGameId: null, scanning: false,
    });
  };

  if (msg.cmd === "ready") {
    setTimeout(pushState, 60);
    setTimeout(() => { handleHostMessage({ type: "padConnected", connected: true }); handleHostMessage({ type: "battery", present: true, percent: 62, charging: false, level: 2 }); }, 700);
  } else if (msg.cmd === "launch") {
    toast("(preview) would launch " + msg.id);
  } else if (msg.cmd === "toggleFavorite") {
    mockHandle._fav = mockHandle._fav || {};
    const cur = (S.games.find(g => g.id === msg.id) || {}).favorite;
    mockHandle._fav[msg.id] = !cur;
    pushState();
  } else if (msg.cmd === "toggleHidden") {
    mockHandle._hidden = mockHandle._hidden || {};
    const cur = (S.games.find(g => g.id === msg.id) || {}).hidden;
    mockHandle._hidden[msg.id] = !cur;
    pushState();
  } else if (msg.cmd === "addManual") {
    toast("(preview) would open the file picker");
  } else if (msg.cmd === "createCollection") {
    mockCollections.push({ id: "c" + (mockCollections.length + 1), name: msg.name, gameIds: msg.gameId ? [msg.gameId] : [] });
    pushState();
  } else if (msg.cmd === "toggleInCollection") {
    const c = mockCollections.find(x => x.id === msg.collectionId);
    if (c) { const i = c.gameIds.indexOf(msg.id); if (i >= 0) c.gameIds.splice(i, 1); else c.gameIds.push(msg.id); }
    pushState();
  } else if (msg.cmd === "deleteCollection") {
    const i = mockCollections.findIndex(x => x.id === msg.id);
    if (i >= 0) mockCollections.splice(i, 1);
    pushState();
  } else if (msg.cmd === "listWindows") {
    handleHostMessage({ type: "windows", windows: mockWindows });
  } else if (msg.cmd === "windowAction") {
    toast("(preview) window " + msg.action);
  } else if (msg.cmd === "shortcut") {
    toast("(preview) shortcut " + msg.id);
  } else if (msg.cmd === "power") {
    toast("(preview) power " + msg.action);
  } else if (msg.cmd === "closeOverlay" || msg.cmd === "resumeGame" || msg.cmd === "goHome") {
    /* host-side window juggling; nothing to do in the browser preview */
  } else if (msg.cmd === "closeGame") {
    mockHandle._running = null;
    handleHostMessage({ type: "game", running: false, id: null });
    toast("(preview) game closed");
  } else if (msg.cmd === "rescan") {
    handleHostMessage({ type: "scanning", busy: true });
    setTimeout(() => handleHostMessage({ type: "scanning", busy: false }), 1500);
  } else if (msg.cmd === "setArgs") {
    toast("(preview) args = " + msg.args);
  } else if (msg.cmd === "romFolderPick") {
    setTimeout(() => handleHostMessage({ type: "romFolderPicked", path: "D:\\ROMs\\N64", platformId: "n64" }), 300);
  } else if (msg.cmd === "romFolderAdd") {
    mockEmulation.romFolders.push({ id: "f" + (mockEmulation.romFolders.length + 1), path: msg.path, platformId: msg.platformId, emulatorId: msg.emulatorId || null, core: null, args: null, extensions: null, recurse: true });
    toast(`(preview) added ${msg.platformId} folder ${msg.path}`);
    pushState();
  } else if (msg.cmd === "romFolderUpdate") {
    const f = mockEmulation.romFolders.find(x => x.id === msg.id);
    if (f) {
      if (msg.platformId) f.platformId = msg.platformId;
      if (msg.emulatorId !== undefined) { f.emulatorId = msg.emulatorId || null; f.core = null; }
      if (msg.args !== undefined) f.args = msg.args || null;
      if (msg.extensions !== undefined) f.extensions = msg.extensions ? msg.extensions.split(/[,\s;]+/).filter(Boolean) : null;
    }
    pushState();
  } else if (msg.cmd === "romFolderPickCore") {
    const f = mockEmulation.romFolders.find(x => x.id === msg.id);
    if (f) f.core = "C:\\RetroArch\\cores\\mednafen_psx_hw_libretro.dll";
    toast("(preview) core chosen");
    pushState();
  } else if (msg.cmd === "romFolderRemove") {
    const i = mockEmulation.romFolders.findIndex(x => x.id === msg.id);
    if (i >= 0) mockEmulation.romFolders.splice(i, 1);
    pushState();
  } else if (msg.cmd === "emuAdd") {
    const e = { id: "e" + (mockEmulation.emulators.length + 1), name: "Dolphin", exePath: "C:\\Emulators\\Dolphin\\Dolphin.exe", args: '-b -e "{rom}"', preset: "dolphin", platforms: ["gc", "wii"] };
    mockEmulation.emulators.push(e);
    pushState();
    setTimeout(() => handleHostMessage({ type: "emuAdded", id: e.id }), 200);
  } else if (msg.cmd === "emuUpdate") {
    const e = mockEmulation.emulators.find(x => x.id === msg.id);
    if (e) { if (msg.name) e.name = msg.name; if (msg.args !== undefined) e.args = msg.args; }
    pushState();
  } else if (msg.cmd === "emuPickExe") {
    toast("(preview) would open the file picker");
  } else if (msg.cmd === "emuRemove") {
    const i = mockEmulation.emulators.findIndex(x => x.id === msg.id);
    if (i >= 0) mockEmulation.emulators.splice(i, 1);
    mockEmulation.romFolders.forEach(f => { if (f.emulatorId === msg.id) f.emulatorId = null; });
    pushState();
  } else if (msg.cmd === "setEmulator") {
    mockHandle._emus = mockHandle._emus || {};
    mockHandle._emus[msg.id] = msg.emulatorId || null;
    pushState();
  } else if (msg.cmd === "setTitle") {
    mockHandle._titles = mockHandle._titles || {};
    mockHandle._titles[msg.id] = msg.title;
    pushState();
  }
}

/* ============================== boot ============================== */

fitStage();
tickClock();
renderTabbars();
renderGuide();
renderLibraryLegend();
renderInputHint();
paintButtons(document);
// Show the no-controller state straight away. The host only pushes when something changes, so
// waiting for a message left the corner blank until a pad was plugged in.
updateBattery(null);
switchView("library");
send({ cmd: "ready" });
