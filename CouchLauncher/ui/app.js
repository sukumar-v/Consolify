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
  startupRegistered: false,
  gameRunning: false,
  runningGameId: null,
  scanning: false,
  padConnected: false,
};

const SECTIONS = ["library", "collections", "settings"];
let view = "library";                    // library | collections | detail | settings
let focus = { zone: "cont", row: 0, col: 0 };  // library zones: tabs | cont | grid
let tabIdx = 0;

let detailGameId = null;
let detailReturn = "library";            // where B goes back to from detail
let detailBtn = 0;

/* filter & sort (session state) — empty sets mean "no restriction" */
const F = { platforms: new Set(), status: new Set(), fav: false, sort: "az" };
const PLATFORMS = ["Steam", "Epic", "GOG", "Xbox", "Manual"];
const STATUSES = ["Installed", "Not installed"];
const MINIMIZE_COMBOS = ["LS + RS", "LB + RB", "LT + RT + LB + RB", "Guide", "View + Menu", "LS + RB", "LB + RS", "Off"];

const SORTS = [
  { id: "az", label: "A – Z" },
  { id: "za", label: "Z – A" },
  { id: "recent", label: "Recently played" },
  { id: "played", label: "Most played" },
  { id: "sizeDesc", label: "Largest first" },
  { id: "sizeAsc", label: "Smallest first" },
];

function resetFilters() {
  F.platforms.clear();
  F.status.clear();
  F.fav = false;
  F.sort = "az";
}

function activeFilterCount() {
  return F.platforms.size + F.status.size + (F.fav ? 1 : 0);
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
const FOCUSABLE_SEL = ".cont-item, .grid-item, .tab, .set-row, .ov-row, .coll-card, .pill-btn";

/** Should a focus highlight be painted at all right now? */
function focusVisible() { return overlayMode || inputMode === "pad" || pointerOnItem; }

function setPointerOnItem(on) {
  if (pointerOnItem === on) return;
  pointerOnItem = on;
  repaintFocus();
}

/** Re-apply focus styling for whatever screen/overlay is currently up. */
function repaintFocus() {
  if (filterOpen) renderFilter();
  else if (gameMenu) renderGameMenu();
  else if (collectOpen) renderCollect();
  else if (manageOpen) renderManage();
  else if (confirmState) renderConfirm();
  else if (view === "library") updateLibraryFocus(true);
  else if (view === "collections") {
    if (collMode === "grid") updateCollFocus(true);
    else updateCollListFocus(true);
  }
  else if (view === "detail") updateDetailFocus();
  else if (view === "settings") renderSettings();
}

window.addEventListener("mousemove", (e) => {
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
 * Scroll a focused element into view, keeping REVEAL_MARGIN of clearance on the side it is
 * approaching from. `scrollIntoView({block:"nearest"})` stops the moment the element's *unscaled*
 * box is visible, which parks it flush against the edge and clips the growth — coming down the
 * grid it shaved the bottom of the new row, coming up it shaved the top.
 *
 * The first and last rows snap fully to the ends instead, so their outer edge is never cropped.
 */
function revealIn(scroller, el, isFirst, isLast) {
  if (!scroller || !el) return;
  if (isFirst) { scroller.scrollTo({ top: 0, behavior: "smooth" }); return; }
  if (isLast) { scroller.scrollTo({ top: scroller.scrollHeight, behavior: "smooth" }); return; }

  // offsetTop is relative to the scroller because it is `position: relative` -- deliberately, so
  // this arithmetic stays in layout pixels. getBoundingClientRect would report post-transform
  // pixels (the whole stage is scaled to the window) and would not match scrollTop.
  const top = el.offsetTop;
  const bottom = top + el.offsetHeight;
  const viewTop = scroller.scrollTop;
  const viewBottom = viewTop + scroller.clientHeight;

  let next = null;
  if (top - REVEAL_MARGIN < viewTop) next = top - REVEAL_MARGIN;
  else if (bottom + REVEAL_MARGIN > viewBottom) next = bottom + REVEAL_MARGIN - scroller.clientHeight;
  if (next === null) return;

  const max = Math.max(0, scroller.scrollHeight - scroller.clientHeight);
  scroller.scrollTo({ top: Math.max(0, Math.min(next, max)), behavior: "smooth" });
}

/*
 * Menu icons — inline stroke SVG on a 24x24 grid. Drawn rather than pulled from a font or
 * emoji so they stay crisp at 10-foot distance, inherit currentColor (muted normally, accent
 * when focused) and add nothing to load.
 */
const ICONS = {
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
  trash: '<path d="M3.5 6.5h17M9 6.5V4h6v2.5M18.5 6.5 17.5 20h-11L5.5 6.5M10 11v5M14 11v5"/>',
  terminal: '<path d="m5 8 4 4-4 4M12 16h7"/><rect x="2" y="4" width="20" height="16" rx="1.5"/>',
  file: '<path d="M14 3H6.5A1.5 1.5 0 0 0 5 4.5v15A1.5 1.5 0 0 0 6.5 21h11a1.5 1.5 0 0 0 1.5-1.5V8l-5-5z"/><path d="M14 3v5h5"/>',
  store: '<path d="M21 12a9 9 0 1 1-2.6-6.35M21 3.5v5h-5"/>',

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
};

function iconSvg(name) {
  const body = ICONS[name];
  if (!body) return "";
  return `<svg class="ov-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" ` +
         `stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${body}</svg>`;
}

/**
 * Shared renderer for every overlay menu, so the game options, manage, collection and
 * confirm menus all read like the filter menu. Items are
 * { cat } headers or { label, icon, sub, checked, radio, danger, summary, action }.
 */
function renderMenu(listEl, footEl, items, idx, footHtml, onHover, onClick) {
  listEl.innerHTML = "";
  const focusable = items.map((r, i) => r.cat ? -1 : i).filter(i => i >= 0);
  items.forEach((r, i) => {
    if (r.cat) {
      const c = document.createElement("div");
      c.className = "ov-cat";
      c.textContent = r.cat;
      listEl.appendChild(c);
      return;
    }
    const el = document.createElement("div");
    el.className = "ov-row" + (i === idx && focusVisible() ? " focused" : "") + (r.danger ? " danger" : "");
    let right = "";
    if (r.summary !== undefined) right = `<div class="ov-value"><span class="ov-summary">${esc(r.summary)}</span><span class="arrow">▸</span></div>`;
    else if (r.checked !== undefined) right = `<span class="ov-check${r.checked ? "" : " off"}">${r.checked ? (r.star ? "★" : r.radio ? "●" : "✓") : "○"}</span>`;
    else if (r.sub) right = `<span class="ov-sub">${esc(r.sub)}</span>`;
    el.innerHTML = `<div class="ov-label">${iconSvg(r.icon)}<span>${esc(r.label ?? r.name)}</span></div>${right}`;
    el.addEventListener("mouseenter", () => { if (hoverEnabled()) onHover(i); });
    el.addEventListener("click", () => onClick(i));
    listEl.appendChild(el);
  });
  if (footEl) footEl.innerHTML = footHtml;

  const focusedEl = listEl.querySelector(".ov-row.focused");
  if (focusedEl) {
    revealIn(listEl, focusedEl, idx === focusable[0], idx === focusable[focusable.length - 1]);
  }
}

/** Standard footer hints. */
function foot(...pairs) {
  return pairs.map(([btn, label]) =>
    `<div class="legend-item"><div class="btn-badge${btn === "A" ? " btn-a" : ""}">${btn}</div><span>${esc(label)}</span></div>`
  ).join("");
}

/* collections screen */
let collMode = "list";                   // list | grid
let collListIdx = 0;
let collSel = null;                      // selected collection id
let collFocus = { row: 0, col: 0 };
let collGridRows = [];

/* overlays */
let filterOpen = false, filterIdx = 0;
let manageOpen = false, manageIdx = 0;
let collectOpen = false, collectIdx = 0;
let confirmState = null, confirmIdx = 0; // { title, onYes }
let inputOpen = false, inputConfirm = null;
let wolOpen = false;

let settingsIdx = 0;
let saveTimer = null;

/* layout caches */
let contItems = [];
let gridRows = [];

/* ============================== helpers ============================== */

const $ = (id) => document.getElementById(id);
const esc = (s) => String(s ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));

function coverUrl(g) {
  if (g.coverFile) return HOST ? `https://couch.data/covers/${encodeURIComponent(g.coverFile)}` : g.coverFile;
  return null;
}
function bannerUrl(g) {
  if (g.bannerFile) return HOST ? `https://couch.data/covers/${encodeURIComponent(g.bannerFile)}` : g.bannerFile;
  return coverUrl(g);
}

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
 * fire at once: with ~22 images requested simultaneously from https://couch.data only
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
    probe.onload = () => { imgActive--; job.done(probe.src); pumpImgQueue(); };
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
  const h = hashHue(g.title);
  el.style.background = `linear-gradient(150deg, hsl(${h},16%,15%) 0%, hsl(${(h + 40) % 360},22%,9%) 100%)`;
  el.classList.add("ph");
  el.innerHTML = `<span>${esc(initials(g.title))}</span>`;
}

function applyArt(g, el, url) {
  if (!url) { paintPlaceholder(g, el); return; }
  queueArt(url, (src) => {
    if (!el.isConnected) return;          // tile was re-rendered while loading
    if (src) el.style.backgroundImage = `url('${src}')`;
    else paintPlaceholder(g, el);
  });
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
function updateBattery(batteryType, level) {
  const el = $("padBattery");
  if (!S.padConnected || batteryType === undefined || batteryType < 2) {
    el.textContent = "";
    return;
  }
  const bars = "▮".repeat(Math.max(level, 0) + 1) + "▯".repeat(3 - Math.min(level, 3));
  el.textContent = `PAD ${bars}`;
  el.style.color = level <= 0 ? "#E97A6C" : "";
}

/* ============================== backdrop ============================== */

let bdFront = "bdA";
let bdCurrentKey = null;

function setBackdrop(game) {
  const key = game ? game.id : "none";
  if (key === bdCurrentKey) return;
  bdCurrentKey = key;

  const front = $(bdFront);
  const backId = bdFront === "bdA" ? "bdB" : "bdA";
  const back = $(backId);

  const url = game && (bannerUrl(game) || coverUrl(game));
  if (url) back.style.backgroundImage = `url('${url}')`;
  else if (game) {
    const h = hashHue(game.title);
    back.style.backgroundImage = `linear-gradient(150deg, hsl(${h},18%,16%), hsl(${(h + 40) % 360},22%,7%))`;
  } else back.style.backgroundImage = "none";

  back.classList.add("visible");
  front.classList.remove("visible");
  bdFront = backId;
}

/* ============================== tab bars ============================== */

const TAB_DEFS = [
  { id: "library", label: "Library" },
  { id: "collections", label: "Collections" },
  { id: "settings", label: "Settings" },
];

function renderTabbars() {
  document.querySelectorAll("[data-tabbar]").forEach(bar => {
    const active = bar.dataset.tabbar;
    bar.innerHTML = "";
    TAB_DEFS.forEach((t, i) => {
      const el = document.createElement("div");
      el.className = "tab" + (t.id === active ? " tab-active" : "");
      el.textContent = t.label;
      el.dataset.tab = t.id;
      el.addEventListener("mouseenter", () => {
        if (!hoverEnabled()) return;
        if (view === "library") { focus = { zone: "tabs", row: 0, col: 0 }; tabIdx = i; updateLibraryFocus(true); }
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
  item.innerHTML = `<div class="add-plus">+</div><div class="add-label">Add game</div>`;
  item.addEventListener("mouseenter", () => { if (hoverEnabled()) onHover(); });
  item.addEventListener("click", onClick);
  return item;
}

function makeGridTile(g, onHover, onClick, onDetails) {
  if (g.__add) return makeAddTile(onHover, onClick);

  const item = document.createElement("div");
  item.className = "grid-item" + (g.installed ? "" : " uninstalled");

  const art = document.createElement("div");
  art.className = "grid-art";
  applyArt(g, art, coverUrl(g));
  item.appendChild(art);

  const label = document.createElement("div");
  label.className = "grid-label";
  const meta = g.installed ? shortMeta(g) : `${g.platform} · ${fmtSize(g.sizeBytes)} · not installed`;
  label.innerHTML = `<div class="grid-title">${esc(g.title)}</div><div class="grid-meta">${esc(meta)}</div>`;
  item.appendChild(label);

  if (g.favorite) {
    const star = document.createElement("div");
    star.className = "tile-fav";
    star.textContent = "★";
    item.appendChild(star);
  }

  item.addEventListener("mouseenter", () => { if (hoverEnabled()) onHover(); });
  item.addEventListener("click", onClick);
  if (onDetails) item.addEventListener("contextmenu", (e) => { e.preventDefault(); onDetails(); });
  return item;
}

/* ============================== library data ============================== */

function sortGames(list) {
  const by = {
    az: (a, b) => a.title.localeCompare(b.title),
    za: (a, b) => b.title.localeCompare(a.title),
    recent: (a, b) => new Date(b.lastPlayed || 0) - new Date(a.lastPlayed || 0),
    played: (a, b) => (b.playtimeMinutes || 0) - (a.playtimeMinutes || 0),
    sizeDesc: (a, b) => (b.sizeBytes || 0) - (a.sizeBytes || 0),
    sizeAsc: (a, b) => (a.sizeBytes || 0) - (b.sizeBytes || 0),
  }[F.sort] || ((a, b) => a.title.localeCompare(b.title));
  return [...list].sort(by);
}

/** Games eligible for the library: everything the user hasn't hidden. */
function visibleGames() { return S.games.filter(g => !g.hidden); }

/* Continue carousel geometry: 300px tile + 28px gap, inside the 1920 stage less 80px gutters. */
const CONTINUE_MAX = 12;
const CONT_STEP = 328;
const CONT_VIEWPORT = 1760;
let contScroll = 0;   // index of the leftmost visible tile

/** Slide the carousel the minimum distance needed to keep the focused tile on screen. */
function contPerView() { return Math.max(1, Math.floor((CONT_VIEWPORT + 28) / CONT_STEP)); }
function contMaxScroll() { return Math.max(0, contItems.length - contPerView()); }

/**
 * Slide the carousel the minimum distance needed to keep the focused tile on screen.
 * `follow` is false for hover: letting the mouse drag the carousel makes tiles slide out
 * from under the cursor, which fires another hover and runs away.
 */
function updateContinueScroll(follow) {
  const track = $("continueTrack");
  if (!track) return;
  const perView = contPerView();

  if (follow && focus.zone === "cont") {
    if (focus.col < contScroll) contScroll = focus.col;
    else if (focus.col > contScroll + perView - 1) contScroll = focus.col - perView + 1;
  }
  contScroll = Math.max(0, Math.min(contScroll, contMaxScroll()));
  track.style.transform = `translateX(${-contScroll * CONT_STEP}px)`;
}

/* Right stick horizontal -> carousel. The host turns stick X into real HWHEEL events, so this
   also means a horizontal-scrolling mouse or trackpad drives the carousel. */
let hWheelAccum = 0;

function overlayOpen() {
  return inputOpen || filterOpen || !!gameMenu || collectOpen || manageOpen || !!confirmState || wolOpen;
}

window.addEventListener("wheel", (e) => {
  if (Math.abs(e.deltaX) <= Math.abs(e.deltaY)) return;   // vertical intent: let it scroll normally
  if (overlayOpen() || view !== "library") return;
  if (focus.zone !== "cont" || !contItems.length) return;

  hWheelAccum += e.deltaX;
  let moved = false;
  while (Math.abs(hWheelAccum) >= 120) {
    const dir = Math.sign(hWheelAccum);
    hWheelAccum -= dir * 120;
    const next = Math.max(0, Math.min(contItems.length - 1, focus.col + dir));
    if (next === focus.col) { hWheelAccum = 0; break; }   // already at an end
    focus.col = next;
    moved = true;
  }
  if (moved) {
    setInputMode("pad");   // the stick is navigating, so show the highlight it is moving
    updateLibraryFocus();
  }
}, { passive: true });

function libraryData() {
  let filtered = visibleGames();
  if (F.platforms.size) filtered = filtered.filter(g => F.platforms.has(g.platform));
  if (F.fav) filtered = filtered.filter(g => g.favorite);
  if (F.status.size === 1) {
    const wantInstalled = F.status.has("Installed");
    filtered = filtered.filter(g => g.installed === wantInstalled);
  }

  const cont = visibleGames()
    .filter(g => g.lastPlayed && g.installed)
    .sort((a, b) => new Date(b.lastPlayed) - new Date(a.lastPlayed))
    .slice(0, CONTINUE_MAX);

  // The "add a game" tile always trails the grid so it's reachable without a menu.
  const items = [...sortGames(filtered), { __add: true }];
  const rows = [];
  for (let i = 0; i < items.length; i += 8) rows.push(items.slice(i, i + 8));
  return { cont, rows, total: filtered.length };
}

function filterSummary(total) {
  const bits = [`${total} TITLE${total === 1 ? "" : "S"}`];
  if (F.platforms.size) bits.push([...F.platforms].join(" + ").toUpperCase());
  if (F.fav) bits.push("FAVORITES");
  if (F.status.size === 1) bits.push([...F.status][0].toUpperCase());
  const sort = SORTS.find(s => s.id === F.sort);
  if (F.sort !== "az" && sort) bits.push(sort.label.toUpperCase());
  const hidden = S.games.length - visibleGames().length;
  if (hidden > 0) bits.push(`${hidden} HIDDEN`);
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
  card.onmouseenter = () => { if (hoverEnabled()) { focus = { zone: "playing", row: 0, col: 0 }; updateLibraryFocus(true); } };
  card.onclick = () => { focus = { zone: "playing", row: 0, col: 0 }; updateLibraryFocus(true); send({ cmd: "resumeGame" }); };
}

function renderLibrary() {
  const { cont, rows, total } = libraryData();
  renderPlaying();
  contItems = cont;
  gridRows = rows;

  $("titleCount").textContent = `${S.games.length} TITLE${S.games.length === 1 ? "" : "S"}`;
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
    const item = document.createElement("div");
    item.className = "cont-item";

    const art = document.createElement("div");
    art.className = "cont-art";
    applyArt(g, art, bannerUrl(g));

    const meta = document.createElement("div");
    meta.className = "cont-meta";
    meta.innerHTML = `<div class="cont-title">${esc(g.title)}</div><div class="cont-sub">${esc(shortMeta(g))}</div>`;

    item.appendChild(art); item.appendChild(meta);
    item.addEventListener("mouseenter", () => {
      if (!hoverEnabled()) return;
      focus = { zone: "cont", row: 0, col: i }; updateLibraryFocus(true);
    });
    item.addEventListener("click", () => { focus = { zone: "cont", row: 0, col: i }; updateLibraryFocus(true); libraryAccept("A"); });
    track.appendChild(item);
  });

  // Grid
  const scroll = $("gridScroll");
  scroll.innerHTML = "";
  if (total === 0) {
    const note = document.createElement("div");
    note.className = "empty-note";
    note.innerHTML = S.scanning
      ? "Scanning your Steam, Epic, GOG and Xbox libraries…"
      : (visibleGames().length
        ? "Nothing matches the current filter. Press <b>X</b> to change it, or <b>Y</b> to reset."
        : "No games found yet. Use the <b>+ Add game</b> tile below, or rescan from <b>Settings → Library</b>.");
    scroll.appendChild(note);
  }
  rows.forEach((row, r) => {
    const rowDiv = document.createElement("div");
    rowDiv.className = "grid-row";
    row.forEach((g, c) => {
      const item = makeGridTile(g,
        () => { focus = { zone: "grid", row: r, col: c }; updateLibraryFocus(true); },
        () => { focus = { zone: "grid", row: r, col: c }; updateLibraryFocus(true); libraryAccept("A"); });
      rowDiv.appendChild(item);
    });
    scroll.appendChild(rowDiv);
  });

  clampFocus();
  updateLibraryFocus();
}

function focusedGame() {
  if (view === "detail") return gameById(detailGameId);
  if (view === "library" && focus.zone === "playing") return gameById(S.runningGameId);
  if (view === "collections") {
    if (collMode === "grid") return (collGridRows[collFocus.row] || [])[collFocus.col] || null;
    return null;
  }
  if (focus.zone === "cont") return contItems[focus.col] || null;
  if (focus.zone === "grid") {
    const it = (gridRows[focus.row] || [])[focus.col];
    return it && !it.__add ? it : null;
  }
  return null;
}

/** The focused grid cell, including the trailing "add game" tile. */
function focusedCell() {
  if (focus.zone === "grid") return (gridRows[focus.row] || [])[focus.col] || null;
  if (focus.zone === "cont") return contItems[focus.col] || null;
  return null;
}

function clampFocus() {
  if (focus.zone === "cont") {
    if (!contItems.length) focus = { zone: gridRows.length ? "grid" : "tabs", row: 0, col: 0 };
    else focus.col = Math.min(focus.col, contItems.length - 1);
  }
  if (focus.zone === "grid") {
    if (!gridRows.length) focus = { zone: contItems.length ? "cont" : "tabs", row: 0, col: 0 };
    else {
      focus.row = Math.min(focus.row, gridRows.length - 1);
      focus.col = Math.min(focus.col, gridRows[focus.row].length - 1);
    }
  }
}

function updateLibraryFocus(noScroll) {
  const show = focusVisible();
  const playingCard = $("playingCard");
  if (playingCard) playingCard.classList.toggle("focused", show && focus.zone === "playing");
  const tabs = document.querySelectorAll("#screen-library [data-tabbar] .tab");
  tabs.forEach((t, i) => t.classList.toggle("focused", show && focus.zone === "tabs" && i === tabIdx));

  const contFocused = focus.zone === "cont";
  $("continueSection").classList.toggle("zone-dim", !contFocused && contItems.length > 0);
  document.querySelectorAll("#continueRow .cont-item").forEach((el, i) => {
    el.classList.toggle("focused", show && contFocused && i === focus.col);
  });
  updateContinueScroll(!noScroll);

  const scroller = $("gridScroll");
  document.querySelectorAll("#gridScroll .grid-row").forEach((rowEl, r) => {
    const rowFocused = focus.zone === "grid" && r === focus.row;
    rowEl.classList.toggle("zone-dim", !rowFocused);
    rowEl.querySelectorAll(".grid-item").forEach((el, c) => {
      const f = rowFocused && c === focus.col;
      el.classList.toggle("focused", show && f);
      if (show && f && !noScroll) revealIn(scroller, el, r === 0, r === gridRows.length - 1);
    });
  });

  setBackdrop(focusedGame());
}

/* ============================== library nav ============================== */

function libraryNav(btn) {
  const zones = ["tabs"];
  if (S.gameRunning && gameById(S.runningGameId)) zones.push("playing");
  if (contItems.length) zones.push("cont");
  for (let i = 0; i < gridRows.length; i++) zones.push("grid" + i);

  const zoneIndex = () => {
    if (focus.zone === "tabs" || focus.zone === "playing" || focus.zone === "cont")
      return zones.indexOf(focus.zone);
    return zones.indexOf("grid" + focus.row);
  };

  if (btn === "Left" || btn === "Right") {
    const dir = btn === "Right" ? 1 : -1;
    if (focus.zone === "tabs") tabIdx = Math.max(0, Math.min(TAB_DEFS.length - 1, tabIdx + dir));
    else if (focus.zone === "playing") { /* single card: nothing to move to */ }
    else if (focus.zone === "cont") focus.col = Math.max(0, Math.min(contItems.length - 1, focus.col + dir));
    else focus.col = Math.max(0, Math.min(gridRows[focus.row].length - 1, focus.col + dir));
    updateLibraryFocus();
    return;
  }

  if (btn === "Up" || btn === "Down") {
    const dir = btn === "Down" ? 1 : -1;
    const zi = Math.max(0, Math.min(zones.length - 1, zoneIndex() + dir));
    const z = zones[zi];
    // Positions are on-screen, so the carousel offset has to be folded in both ways.
    const xCenter = focus.zone === "cont" ? (focus.col - contScroll) * CONT_STEP + 150
                  : focus.zone === "grid" ? focus.col * 223 + 100 : 0;
    if (z === "tabs") { focus = { zone: "tabs", row: 0, col: 0 }; }
    else if (z === "playing") { focus = { zone: "playing", row: 0, col: 0 }; }
    else if (z === "cont") {
      const col = focus.zone === "tabs" ? contScroll : contScroll + Math.round((xCenter - 150) / CONT_STEP);
      focus = { zone: "cont", row: 0, col: Math.max(0, Math.min(contItems.length - 1, col)) };
    } else {
      const r = parseInt(z.slice(4), 10);
      const col = focus.zone === "tabs" ? 0
                : focus.zone === "cont" ? Math.round((xCenter - 100) / 223) : focus.col;
      focus = { zone: "grid", row: r, col: Math.max(0, Math.min(gridRows[r].length - 1, col)) };
    }
    updateLibraryFocus();
  }
}

function libraryAccept(btn) {
  if (!focusVisible()) return;   // pointer is over empty space: nothing is armed
  if (focus.zone === "playing") {
    const g = gameById(S.runningGameId);
    if (!g) return;
    if (btn === "A") send({ cmd: "resumeGame" });
    else if (btn === "Y") openGameMenu(g.id, "library");
    return;
  }
  if (focus.zone === "tabs") {
    if (btn !== "A") return;
    const target = TAB_DEFS[tabIdx].id;
    if (target !== "library") switchView(target);
    return;
  }
  const cell = focusedCell();
  if (cell && cell.__add) {
    if (btn === "A") send({ cmd: "addManual" });
    return;
  }
  const g = focusedGame();
  if (!g) return;
  if (btn === "A") {
    if (!g.installed) { toast(`${g.title} is not installed`); return; }
    launchGame(g);
  } else if (btn === "Y") {
    openGameMenu(g.id, "library");
  }
}

function libraryInput(btn) {
  switch (btn) {
    case "Up": case "Down": case "Left": case "Right": libraryNav(btn); break;
    case "A": case "Y": libraryAccept(btn); break;
    case "X": openFilter(); break;
    case "B":
      if (focus.zone === "grid" && focus.row > 0) { focus.row = 0; focus.col = 0; updateLibraryFocus(); }
      else if (focus.zone === "grid" && contItems.length) { focus = { zone: "cont", row: 0, col: 0 }; updateLibraryFocus(); }
      break;
  }
}

function launchGame(g) {
  if (S.gameRunning) {
    // Already playing something else. Offer the swap rather than just refusing -- from the
    // couch, "a game is already running" left you with nothing to do about it.
    if (S.runningGameId === g.id) { send({ cmd: "resumeGame" }); return; }
    const running = gameById(S.runningGameId);
    confirmState = {
      title: running ? `CLOSE ${running.title.toUpperCase()}?` : "CLOSE THE RUNNING GAME?",
      body: `${g.title} will start once it has closed.`,
      yesLabel: `Close and play ${g.title}`,
      icon: "play", danger: false,
      onYes: () => { toast(`Closing ${running ? running.title : "the game"}…`); send({ cmd: "launch", id: g.id, replace: true }); },
    };
    confirmIdx = 0;
    renderConfirm();
    $("overlay-confirm").classList.add("active");
    return;
  }
  toast(`Launching ${g.title}…`);
  send({ cmd: "launch", id: g.id });
}

/* ============================== collections ============================== */

function collectionsData() {
  const list = [];
  const vis = visibleGames();
  list.push({ id: "fav", name: "Favorites", star: true, games: sortGames(vis.filter(g => g.favorite)) });
  PLATFORMS.forEach(p => {
    const games = vis.filter(g => g.platform === p);
    if (games.length) list.push({ id: "plat:" + p, name: p, games: sortGames(games) });
  });
  S.collections.forEach(c => {
    const games = sortGames(c.gameIds.map(gameById).filter(g => g && !g.hidden));
    list.push({ id: c.id, name: c.name, custom: true, games });
  });
  // Hidden lives at the end — it's a holding pen for things that aren't really games.
  const hidden = S.games.filter(g => g.hidden);
  if (hidden.length) list.push({ id: "hidden", name: "Hidden", games: sortGames(hidden) });
  return list;
}

function renderCollections() {
  const cols = collectionsData();
  collListIdx = Math.max(0, Math.min(collListIdx, cols.length - 1));

  const listEl = $("collList");
  const gridWrap = $("collGridWrap");
  const legend = $("legend-collections");

  if (collMode === "list") {
    listEl.classList.remove("hidden");
    gridWrap.classList.remove("active");
    $("collCrumb").textContent = `${cols.length} COLLECTIONS`;

    listEl.innerHTML = "";
    cols.forEach((c, i) => {
      const card = document.createElement("div");
      card.className = "coll-card" + (i === collListIdx && focusVisible() ? " focused" : "");

      const thumbs = c.games.slice(0, 6).map(g => {
        const url = coverUrl(g);
        return url
          ? `<div class="coll-thumb" style="background-image:url('${url}')"></div>`
          : `<div class="coll-thumb"><span>${esc(initials(g.title))}</span></div>`;
      }).join("");

      card.innerHTML = `
        <div class="coll-info">
          <div class="coll-name">${c.star ? '<span class="fav-star">★</span>' : ""}${esc(c.name)}</div>
          <div class="coll-meta">${c.games.length} GAME${c.games.length === 1 ? "" : "S"}${c.custom ? " · CUSTOM" : ""}</div>
        </div>
        <div class="coll-thumbs">${thumbs}</div>`;

      card.addEventListener("mouseenter", () => { if (hoverEnabled() && collListIdx !== i) { collListIdx = i; updateCollListFocus(true); } });
      card.addEventListener("click", () => { collListIdx = i; openCollectionGrid(c.id); });
      listEl.appendChild(card);
    });

    updateCollListFocus();
    setBackdrop(null);
  } else {
    const col = cols.find(c => c.id === collSel);
    if (!col) { collMode = "list"; renderCollections(); return; }

    listEl.classList.add("hidden");
    gridWrap.classList.add("active");
    $("collCrumb").textContent = "COLLECTIONS / " + col.name.toUpperCase();
    $("collGridTitle").textContent = col.name;
    $("collGridLabel").textContent = `${col.games.length} GAME${col.games.length === 1 ? "" : "S"}`;

    collGridRows = [];
    for (let i = 0; i < col.games.length; i += 8) collGridRows.push(col.games.slice(i, i + 8));
    collFocus.row = Math.max(0, Math.min(collFocus.row, collGridRows.length - 1));
    collFocus.col = Math.max(0, Math.min(collFocus.col, (collGridRows[collFocus.row] || []).length - 1));

    const scroll = $("collGridScroll");
    scroll.innerHTML = "";
    if (!collGridRows.length) {
      const note = document.createElement("div");
      note.className = "empty-note";
      note.innerHTML = col.id === "fav"
        ? "No favorites yet — press <b>Y</b> on a game and choose <b>Add to favorites</b>."
        : col.id === "hidden"
          ? "Nothing hidden. Press <b>Y</b> on anything that isn't really a game and choose <b>Hide</b>."
          : "This collection is empty — press <b>Y</b> on a game and choose <b>Add to collection</b>.";
      scroll.appendChild(note);
    }
    collGridRows.forEach((row, r) => {
      const rowDiv = document.createElement("div");
      rowDiv.className = "grid-row";
      row.forEach((g, c) => {
        rowDiv.appendChild(makeGridTile(g,
          () => { collFocus = { row: r, col: c }; updateCollFocus(true); },
          () => { collFocus = { row: r, col: c }; updateCollFocus(true); if (g.installed) launchGame(g); else toast(`${g.title} is not installed`); }));
      });
      scroll.appendChild(rowDiv);
    });
    updateCollFocus();

    legend.innerHTML = `
      <div class="legend-item"><div class="btn-badge btn-a">A</div><span>Launch</span></div>
      <div class="legend-item"><div class="btn-badge">B</div><span>Back</span></div>
      <div class="legend-item"><div class="btn-badge">Y</div><span>Options</span></div>
      <div class="legend-item"><div class="btn-pill mono">LB · RB</div><span>Section</span></div>`;
  }
}

/**
 * Focus-only update for the collections list. Re-running renderCollections() on every hover
 * rebuilt every card and grid tile, which restarted the async cover loads and made the whole
 * screen flicker as the pointer crossed the gaps between items.
 */
function updateCollListFocus(noScroll) {
  const show = focusVisible();
  const listEl = $("collList");
  const cards = listEl.querySelectorAll(".coll-card");
  cards.forEach((el, i) => {
    const f = i === collListIdx;
    el.classList.toggle("focused", show && f);
    if (show && f && !noScroll) revealIn(listEl, el, i === 0, i === cards.length - 1);
  });

  const cols = collectionsData();
  const delHint = cols[collListIdx] && cols[collListIdx].custom
    ? `<div class="legend-item"><div class="btn-badge">X</div><span>Delete collection</span></div>` : "";
  $("legend-collections").innerHTML = `
    <div class="legend-item"><div class="btn-badge btn-a">A</div><span>Open</span></div>
    <div class="legend-item"><div class="btn-badge">B</div><span>Back</span></div>
    ${delHint}
    <div class="legend-item"><div class="btn-pill mono">LB · RB</div><span>Section</span></div>`;
}

function updateCollFocus(noScroll) {
  const show = focusVisible();
  const scroller = $("collGridScroll");
  document.querySelectorAll("#collGridScroll .grid-row").forEach((rowEl, r) => {
    const rowFocused = r === collFocus.row;
    rowEl.classList.toggle("zone-dim", !rowFocused);
    rowEl.querySelectorAll(".grid-item").forEach((el, c) => {
      const f = rowFocused && c === collFocus.col;
      el.classList.toggle("focused", show && f);
      if (show && f && !noScroll) revealIn(scroller, el, r === 0, r === collGridRows.length - 1);
    });
  });
  setBackdrop(focusedGame());
}

function openCollectionGrid(id) {
  collSel = id;
  collMode = "grid";
  collFocus = { row: 0, col: 0 };
  renderCollections();
}

function collectionsInput(btn) {
  const cols = collectionsData();
  if (collMode === "list") {
    switch (btn) {
      case "Up": collListIdx = Math.max(0, collListIdx - 1); updateCollListFocus(); break;
      case "Down": collListIdx = Math.min(cols.length - 1, collListIdx + 1); updateCollListFocus(); break;
      case "A": if (focusVisible() && cols[collListIdx]) openCollectionGrid(cols[collListIdx].id); break;
      case "X": {
        const c = cols[collListIdx];
        if (c && c.custom) {
          confirmState = {
            title: `DELETE “${c.name.toUpperCase()}”?`,
            onYes: () => send({ cmd: "deleteCollection", id: c.id }),
          };
          confirmIdx = 1;
          renderConfirm();
          $("overlay-confirm").classList.add("active");
        }
        break;
      }
      case "B": switchView("library"); break;
    }
  } else {
    const g = focusedGame();
    switch (btn) {
      case "Left": collFocus.col = Math.max(0, collFocus.col - 1); updateCollFocus(); break;
      case "Right": collFocus.col = Math.min((collGridRows[collFocus.row] || []).length - 1, collFocus.col + 1); updateCollFocus(); break;
      case "Up":
        // Stays inside the collection: only B returns to the list.
        if (collFocus.row > 0) {
          collFocus.row--;
          collFocus.col = Math.min(collFocus.col, collGridRows[collFocus.row].length - 1);
          updateCollFocus();
        }
        break;
      case "Down":
        if (collFocus.row < collGridRows.length - 1) { collFocus.row++; collFocus.col = Math.min(collFocus.col, collGridRows[collFocus.row].length - 1); updateCollFocus(); }
        break;
      case "A": if (focusVisible() && g) { if (g.installed) launchGame(g); else toast(`${g.title} is not installed`); } break;
      case "Y": if (focusVisible() && g) openGameMenu(g.id, "collections"); break;
      case "B": collMode = "list"; renderCollections(); break;
    }
  }
}

/* ============================== detail ============================== */

function openDetail(id, from) {
  detailGameId = id;
  detailReturn = from || "library";
  detailBtn = 0;
  switchView("detail");
}

function renderDetail() {
  const g = gameById(detailGameId);
  if (!g) { switchView(detailReturn); return; }

  $("detailCrumb").textContent = g.platform.toUpperCase();
  $("detailTitle").textContent = g.title;
  $("detailFav").style.display = g.favorite ? "" : "none";
  $("favLegend").textContent = g.favorite ? "Unfavorite" : "Favorite";

  const bits = [];
  if (g.preferDirectLaunch && g.exePath) bits.push("Launches its executable directly (store launcher bypassed).");
  else if (g.platform === "Steam") bits.push("Launches through the Steam client.");
  else if (g.platform === "Epic") bits.push("Launches through the Epic Games launcher.");
  else bits.push("Launches directly from its executable.");
  if (g.args) bits.push(`Arguments: ${g.args}`);
  if (g.installDir) bits.push(g.installDir);
  if (!g.installed) bits.push("Currently not installed on this PC.");
  $("detailDesc").textContent = bits.join("  ·  ");

  $("statPlaytime").textContent = fmtPlaytime(g.playtimeMinutes);
  $("statLastPlayed").textContent = fmtLastPlayed(g.lastPlayed);
  $("statSessions").textContent = g.sessions > 0 ? String(g.sessions) : "—";
  $("statSize").textContent = fmtSize(g.sizeBytes);

  $("playLabel").textContent = g.playtimeMinutes > 0 ? "Continue" : "Play";

  const art = $("detailArt");
  art.className = "detail-art";
  art.innerHTML = "";
  art.style.background = "";
  applyArt(g, art, bannerUrl(g));

  document.querySelectorAll("#detailActions .pill-btn").forEach((el, i) => {
    el.onmouseenter = () => { if (hoverEnabled()) { detailBtn = i; updateDetailFocus(); } };
    el.onclick = () => { detailBtn = i; updateDetailFocus(); detailActivate(); };
  });

  updateDetailFocus();
  setBackdrop(null);
}

const DETAIL_BTNS = ["play", "collect", "manage"];

function updateDetailFocus() {
  detailBtn = Math.max(0, Math.min(detailBtn, DETAIL_BTNS.length - 1));
  const show = focusVisible();
  document.querySelectorAll("#detailActions .pill-btn").forEach(el => {
    el.classList.toggle("focused", show && el.dataset.act === DETAIL_BTNS[detailBtn]);
  });
}

function detailActivate() {
  const g = gameById(detailGameId);
  const act = DETAIL_BTNS[detailBtn];
  if (act === "play" && g) { if (!g.installed) toast(`${g.title} is not installed`); else launchGame(g); }
  else if (act === "collect") openCollect();
  else if (act === "manage") openManage();
}

function detailInput(btn) {
  const g = gameById(detailGameId);
  switch (btn) {
    case "Left": detailBtn--; updateDetailFocus(); break;
    case "Right": detailBtn++; updateDetailFocus(); break;
    case "A": if (focusVisible()) detailActivate(); break;
    case "X": if (g) send({ cmd: "toggleFavorite", id: g.id }); break;
    case "B": switchView(detailReturn); break;
  }
}

/* ============================== settings ============================== */

function settingsRows() {
  const s = S.settings;
  if (!s) return [];
  const set = (fn) => { fn(); scheduleSave(); renderSettings(); };
  const displayLabel = (d) => {
    if (!d) return "None";
    const num = d.deviceName.replace(/\D/g, "");
    return `${d.friendlyName} · ${d.width}×${d.height}${d.isPrimary ? " · PRIMARY" : ""} (Display ${num})`;
  };

  const rows = [];
  rows.push({ section: "DISPLAY" });
  rows.push({
    name: "TV display", hint: "Couch Launcher opens here, and games are steered onto it",
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

  rows.push({ section: "GAMEPAD" });
  rows.push(toggleRow("Gamepad mouse", "Left stick moves the cursor; right stick scrolls",
    () => s.gamepadMouseEnabled, v => set(() => s.gamepadMouseEnabled = v)));
  rows.push(toggleRow("Stay active while a game is focused", "The gamepad-mouse always works when a game is running but not focused; this keeps it alive inside the game too (off avoids fighting native controller support)",
    () => s.gamepadMouseDuringGame, v => set(() => s.gamepadMouseDuringGame = v)));
  rows.push(sliderRow("Stick deadzone", () => s.deadzone, 0.05, 0.40, 0.01, v => set(() => s.deadzone = v), v => v.toFixed(2)));
  rows.push(sliderRow("Cursor sensitivity", () => s.sensitivity, 0.2, 3.0, 0.1, v => set(() => s.sensitivity = v), v => v.toFixed(1) + "×"));
  rows.push(sliderRow("Acceleration curve", () => s.accelExponent, 1.0, 3.0, 0.1, v => set(() => s.accelExponent = v), v => v.toFixed(1)));
  rows.push(cycleRow("Speed boost button", ["RT", "LT", "LB", "RB", "LS", "RS", "Off"], () => s.boostButton, v => set(() => s.boostButton = v),
    "Hold to move the cursor and scroll faster — crossing a 4K screen a nudge at a time gets old"));
  if (s.boostButton !== "Off")
    rows.push(sliderRow("Boost multiplier", () => s.boostMultiplier, 1.5, 5.0, 0.5, v => set(() => s.boostMultiplier = v), v => v.toFixed(1) + "×"));
  rows.push(cycleRow("Left click button", ["A", "B", "X", "Y", "LB", "RB", "LS", "RS"], () => s.leftClickButton, v => set(() => s.leftClickButton = v),
    "Sends a real mouse click when the launcher is not focused"));
  rows.push(cycleRow("Right click button", ["A", "B", "X", "Y", "LB", "RB", "LS", "RS"], () => s.rightClickButton, v => set(() => s.rightClickButton = v)));
  rows.push(toggleRow("Hide pointer system-wide", "The pointer always hides inside the launcher on D-pad input; this extends it to the rest of Windows. Replaces the system cursors, so it is restored when Couch Launcher exits",
    () => s.hideCursorSystemWide, v => set(() => s.hideCursorSystemWide = v)));
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

  rows.push(cycleRow("Menu combo", MINIMIZE_COMBOS, () => s.minimizeCombo, v => set(() => s.minimizeCombo = v),
    "Tap to minimize or restore the launcher (in-game menu while a game runs); double tap to open the Power Wheel",
    comboWarn));

  rows.push({ section: "VIRTUAL KEYBOARD" });
  rows.push(cycleRow("Toggle button (hold)", ["Start", "Back", "LS", "RS", "LB", "RB"], () => s.keyboardToggleButton, v => set(() => s.keyboardToggleButton = v),
    "Hold this button to show or hide the Windows touch keyboard (it supports gamepad input)"));
  rows.push(sliderRow("Hold time", () => s.keyboardToggleHoldMs, 200, 2000, 100, v => set(() => s.keyboardToggleHoldMs = v), v => Math.round(v) + " ms"));
  rows.push({
    name: "Show keyboard now", type: "action", label: "Toggle",
    action: () => send({ cmd: "toggleKeyboard" }),
  });

  rows.push({ section: "LIBRARY" });
  const counts = PLATFORMS.slice(1).map(p => `${p} ${S.games.filter(g => g.platform === p).length}`).join(" · ");
  rows.push({
    name: "Rescan platforms", hint: counts || "Steam, Epic and GOG are scanned from their local install data",
    type: "action", label: S.scanning ? "Scanning…" : "Rescan",
    action: () => { if (!S.scanning) send({ cmd: "rescan" }); },
  });
  rows.push({
    name: "Add a game manually", hint: "Point to an .exe and optional cover art",
    type: "action", label: "Add",
    action: () => send({ cmd: "addManual" }),
  });

  rows.push({ section: "STARTUP, WAKE & LOCK SCREEN" });
  rows.push(toggleRow("Launch Couch Launcher at login", "Registers a startup entry so the launcher is ready after wake or reboot",
    () => s.launchOnStartup, v => set(() => s.launchOnStartup = v)));
  rows.push({
    name: "Couch setup guide", hint: "Gamepad keyboard layout, PIN sign-in, Wake-on-LAN, controller wake",
    type: "action", label: "Open guide",
    action: () => { wolOpen = true; $("overlay-wol").classList.add("active"); },
  });
  rows.push({
    name: "Exit Couch Launcher", type: "action", label: "Exit", danger: true,
    action: () => send({ cmd: "exitApp" }),
  });
  return rows;
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
function sliderRow(name, get, min, max, step, setV, fmt) {
  const cur = () => { const v = get(); return typeof v === "number" && isFinite(v) ? v : min; };
  return {
    name, type: "slider", value: cur(), min, max, fmt,
    adjust: (dir) => {
      let v = Math.round((cur() + dir * step) / step) * step;
      v = Math.max(min, Math.min(max, v));
      setV(v);
    },
  };
}

function cycleRow(name, options, get, setV, hint, warn) {
  const cur = () => (options.includes(get()) ? get() : options[0]);
  return {
    name, hint, warn, type: "select", value: cur(),
    adjust: (dir) => {
      const i = (options.indexOf(cur()) + dir + options.length) % options.length;
      setV(options[i]);
    },
  };
}

function renderSettings() {
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
    el.className = "set-row" + (idx === settingsIdx && focusVisible() ? " focused" : "");

    let right = "";
    if (r.type === "toggle") {
      right = r.value
        ? `<span class="arrow">◂</span><span class="set-toggle-on">ON</span><span class="arrow">▸</span>`
        : `<span class="arrow">◂</span><span class="set-toggle-off">OFF</span><span class="arrow">▸</span>`;
    } else if (r.type === "select") {
      right = `<span class="arrow">◂</span><span>${esc(r.value)}</span><span class="arrow">▸</span>`;
    } else if (r.type === "slider") {
      const pct = ((r.value - r.min) / (r.max - r.min)) * 100;
      right = `<div class="slider"><span class="arrow">◂</span><div class="slider-track"><div class="slider-fill" style="width:${pct}%"></div></div><span class="arrow">▸</span><span class="slider-val">${esc(r.fmt(r.value))}</span></div>`;
    } else if (r.type === "action") {
      right = `<span class="set-action-label"${r.danger ? ' style="color:#E97A6C"' : ""}>${esc(r.label)}</span>`;
    } else if (r.type === "static") {
      right = `<span class="mono" style="font-size:18px;letter-spacing:0.1em;color:rgba(246,245,243,0.6)">${esc(r.value)}</span>`;
    }

    el.innerHTML = `<div class="set-left"><div class="set-name">${esc(r.name)}</div>${r.hint ? `<div class="set-hint">${esc(r.hint)}</div>` : ""}${r.warn ? `<div class="set-warn">${esc(r.warn)}</div>` : ""}</div><div class="set-value">${right}</div>`;

    el.addEventListener("mouseenter", () => { if (hoverEnabled() && settingsIdx !== idx) { settingsIdx = idx; renderSettings(); } });
    el.addEventListener("click", () => { settingsIdx = idx; const row = settingsRows().filter(x => !x.section)[idx]; if (row.action) row.action(); else if (row.adjust) row.adjust(1); });
    scroll.appendChild(el);
  });

  const focused = scroll.querySelector(".set-row.focused");
  if (focused) revealIn(scroll, focused, settingsIdx === 0, settingsIdx === focusables.length - 1);
}

function settingsInput(btn) {
  const rows = settingsRows().filter(r => !r.section);
  const row = rows[settingsIdx];
  switch (btn) {
    case "Up": settingsIdx = Math.max(0, settingsIdx - 1); renderSettings(); break;
    case "Down": settingsIdx = Math.min(rows.length - 1, settingsIdx + 1); renderSettings(); break;
    case "Left": if (row && row.adjust) row.adjust(-1); break;
    case "Right": if (row && row.adjust) row.adjust(1); break;
    case "A": if (focusVisible() && row) { if (row.action) row.action(); else if (row.adjust) row.adjust(1); } break;
    case "B": switchView("library"); break;
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
  renderFilter();
  $("overlay-filter").classList.add("active");
}

function closeFilter() {
  filterOpen = false; filterLevel = null;
  $("overlay-filter").classList.remove("active");
}

/** Rows of the top-level Filter / Sort menu. */
function filterMenuRows() {
  const n = activeFilterCount();
  return [
    { name: "Filter", icon: "filter", summary: n ? `${n} active` : "All games", open: "filter" },
    { name: "Sort", icon: "sort", summary: SORTS.find(s => s.id === F.sort).label, open: "sort" },
  ];
}

/** Flat list of the multi-select dropdown, with category headers interleaved. */
function filterDropdownRows() {
  const rows = [{ cat: "PLATFORM" }];
  PLATFORMS.forEach(p => rows.push({
    label: p, icon: "gamepad", checked: F.platforms.has(p),
    toggle: () => { F.platforms.has(p) ? F.platforms.delete(p) : F.platforms.add(p); },
  }));
  rows.push({ cat: "STATUS" });
  STATUSES.forEach(s => rows.push({
    label: s, icon: s === "Installed" ? "checkCircle" : "download", checked: F.status.has(s),
    toggle: () => { F.status.has(s) ? F.status.delete(s) : F.status.add(s); },
  }));
  rows.push({ cat: "OTHER" });
  rows.push({
    label: "Favorites only", icon: "star", checked: F.fav,
    toggle: () => { F.fav = !F.fav; },
  });
  return rows;
}

const SORT_ICONS = {
  az: "sortAsc", za: "sortDesc", recent: "clock",
  played: "timer", sizeDesc: "chevronsDown", sizeAsc: "chevronsUp",
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
  if (row.open) { filterLevel = row.open; filterIdx = 0; renderFilter(); return; }
  row.toggle();
  if (row.radio) { filterLevel = null; filterIdx = 1; }  // sort is single-select: pick and close
  applyFilter();
}

function filterInput(btn) {
  const rows = currentFilterRows();
  const focusable = filterFocusable(rows);
  const pos = focusable.indexOf(filterIdx);
  switch (btn) {
    case "Up": filterIdx = focusable[Math.max(0, pos - 1)]; renderFilter(); break;
    case "Down": filterIdx = focusable[Math.min(focusable.length - 1, pos + 1)]; renderFilter(); break;
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
  renderGameMenu();
  $("overlay-gamemenu").classList.add("active");
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
  const items = [
    { label: "View game", icon: "info", sub: "Full details page", action: () => { const f = gameMenu.from; closeGameMenu(); openDetail(g.id, f); } },
    { label: g.favorite ? "Remove from favorites" : "Add to favorites", icon: "star", action: () => send({ cmd: "toggleFavorite", id: g.id }) },
    { label: "Add to collection", icon: "folderPlus", action: () => { closeGameMenu(); openCollect(g.id); } },
    { label: "Change cover art", icon: "image", action: () => { closeGameMenu(); send({ cmd: "pickCover", id: g.id }); } },
    { label: g.hidden ? "Unhide" : "Hide", icon: g.hidden ? "eye" : "eyeOff",
      sub: g.hidden ? "Show in the library again" : "Not a game? Keep it out of the library",
      action: () => { send({ cmd: "toggleHidden", id: g.id }); closeGameMenu(); } },
  ];
  if (running) {
    items.unshift({ label: "Resume game", icon: "play", sub: "Back to the running game",
      action: () => { closeGameMenu(); send({ cmd: "resumeGame" }); } });
    items.push({ label: "Close game", icon: "x", danger: true,
      action: () => { closeGameMenu(); send({ cmd: "closeGame" }); } });
  }
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
    case "Up": gameMenu.idx = Math.max(0, gameMenu.idx - 1); renderGameMenu(); break;
    case "Down": gameMenu.idx = Math.min(items.length - 1, gameMenu.idx + 1); renderGameMenu(); break;
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
  renderCollect();
  $("overlay-collect").classList.add("active");
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
    case "Up": collectIdx = Math.max(0, collectIdx - 1); renderCollect(); break;
    case "Down": collectIdx = Math.min(items.length - 1, collectIdx + 1); renderCollect(); break;
    case "A": if (focusVisible() && items[collectIdx]) items[collectIdx].action(); break;
    case "B": closeCollect(); break;
  }
}

/* ============================== manage overlay ============================== */

function manageItems() {
  const g = gameById(detailGameId);
  if (!g) return [];
  const items = [];
  items.push({
    label: "Set launch arguments", icon: "terminal", sub: g.args || "e.g. --launcher-skip",
    action: () => {
      closeManage();
      openInput("LAUNCH ARGUMENTS", g.args || "", v => send({ cmd: "setArgs", id: g.id, args: v }));
    },
  });
  items.push({
    label: "Change executable", icon: "file",
    sub: g.preferDirectLaunch && g.exePath ? g.exePath.split("\\").pop() : "Bypass the store launcher",
    action: () => { send({ cmd: "pickExe", id: g.id }); closeManage(); },
  });
  if (g.preferDirectLaunch && (g.platform === "Steam" || g.platform === "Epic"))
    items.push({ label: `Launch through ${g.platform} again`, icon: "store", action: () => { send({ cmd: "launchViaStore", id: g.id }); closeManage(); } });
  items.push({ label: "Change cover art", icon: "image", action: () => { send({ cmd: "pickCover", id: g.id }); closeManage(); } });
  if (g.manual) items.push({
    label: "Remove from library", icon: "trash", danger: true,
    action: () => { send({ cmd: "removeGame", id: g.id }); closeManage(); switchView(detailReturn); },
  });
  return items;
}

function openManage() { manageOpen = true; manageIdx = 0; renderManage(); $("overlay-manage").classList.add("active"); }
function closeManage() { manageOpen = false; $("overlay-manage").classList.remove("active"); }

function renderManage() {
  const items = manageItems();
  manageIdx = Math.max(0, Math.min(manageIdx, items.length - 1));
  renderMenu($("manageList"), $("manageFoot"), items, manageIdx,
    foot(["A", "Select"], ["B", "Back"]),
    (i) => { if (manageIdx !== i) { manageIdx = i; renderManage(); } },
    (i) => items[i].action());
}

function manageInput(btn) {
  const items = manageItems();
  switch (btn) {
    case "Up": manageIdx = Math.max(0, manageIdx - 1); renderManage(); break;
    case "Down": manageIdx = Math.min(items.length - 1, manageIdx + 1); renderManage(); break;
    case "A": if (focusVisible() && items[manageIdx]) items[manageIdx].action(); break;
    case "B": closeManage(); break;
  }
}

/* ============================== confirm overlay ============================== */

function renderConfirm() {
  if (!confirmState) return;
  $("confirmTitle").textContent = confirmState.title;
  // Defaults suit the destructive cases, which is most of them; swapping games passes its own
  // icon and clears `danger`, since starting a game is not a red action.
  const items = [{
    label: confirmState.yesLabel || "Yes, delete",
    sub: confirmState.body,
    icon: confirmState.icon || "trash",
    danger: confirmState.danger !== false,
  }];
  confirmIdx = 0;
  renderMenu($("confirmList"), $("confirmFoot"), items, confirmIdx,
    foot(["A", "Confirm"], ["B", "Cancel"]),
    () => {}, () => confirmChoose(true));
}

function confirmChoose(yes) {
  const st = confirmState;
  confirmState = null;
  $("overlay-confirm").classList.remove("active");
  if (yes && st) st.onYes();
}

function confirmInput(btn) {
  switch (btn) {
    case "A": if (focusVisible()) confirmChoose(true); break;
    case "B": confirmChoose(false); break;
  }
}

/* ============================== text input overlay ============================== */

function openInput(title, value, onConfirm) {
  inputOpen = true;
  inputConfirm = onConfirm;
  $("inputTitle").textContent = title;
  const field = $("inputField");
  field.value = value;
  $("overlay-input").classList.add("active");
  setTimeout(() => { field.focus(); field.select(); }, 50);
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

/* ============================== lock screen / wake guide ============================== */

const WOL_STEPS = [
  ["Switch the touch keyboard to the Gamepad layout (one time)", "Windows does not expose this as a setting an app can flip, so do it once by hand and it sticks. Open the touch keyboard (hold <b>Start</b>), tap the <b>cog icon</b> in its top-left, open <b>Keyboard layout</b> and choose <b>Gamepad</b>. You then get controller navigation with button accelerators — <b>X</b> backspace, <b>Y</b> space. On the default layout the keyboard ignores the pad entirely. Requires Windows 11 build 26100.3624 or newer."],
  ["Sign in from the couch: set up a Windows Hello PIN", "Apps cannot type into the secure lock screen, but you don't need one: in <b>Settings → Accounts → Sign-in options</b>, add a <b>PIN (Windows Hello)</b>. The sign-in screen's PIN pad works with the touch keyboard, which supports gamepad input — so after a wake you can sign in without leaving the sofa. For a fully hands-off couch PC, enable automatic sign-in instead (<b>netplwiz</b>, untick \"Users must enter a user name and password\")."],
  ["Enable Wake-on-LAN in BIOS/UEFI", "Reboot and enter BIOS setup (usually <b>Del</b> or <b>F2</b> during boot). Find <b>Wake-on-LAN</b>, <b>Power On by PCI-E</b> or <b>Resume by LAN</b> — often under Power Management or Advanced — and enable it. Save and exit."],
  ["Allow the network adapter to wake the PC", "In Windows, open <b>Device Manager → Network adapters</b>, double-click your Ethernet adapter, and on the <b>Power Management</b> tab tick <b>Allow this device to wake the computer</b> and <b>Only allow a magic packet to wake the computer</b>. On the <b>Advanced</b> tab enable <b>Wake on Magic Packet</b>."],
  ["Let your controller's receiver wake the PC", "Still in Device Manager, find your gamepad's USB receiver (under <b>Human Interface Devices</b> or <b>Xbox Peripherals</b>). Open its <b>Power Management</b> tab and tick <b>Allow this device to wake the computer</b>. Pressing the controller button will then wake the PC from sleep."],
  ["Disable Fast Startup for reliable WOL from shutdown", "Wake-on-LAN from full shutdown often fails with Fast Startup. In <b>Control Panel → Power Options → Choose what the power buttons do</b>, click <b>Change settings that are currently unavailable</b> and untick <b>Turn on fast startup</b>."],
  ["Send the magic packet", "Use any Wake-on-LAN app on your phone (or another PC) with this machine's MAC address and your LAN's broadcast address, port 9. Find the MAC with <b>ipconfig /all</b> — the Ethernet adapter's Physical Address."],
  ["Auto-start Couch Launcher", "Turn on <b>Launch Couch Launcher at login</b> in Settings → Startup so the PC lands straight back on the TV with gamepad-mouse active after waking."],
];

function renderWol() {
  $("wolBody").innerHTML = WOL_STEPS.map(([t, txt], i) =>
    `<div class="wol-step"><div class="wol-num">${String(i + 1).padStart(2, "0")}</div><div class="wol-step-body"><div class="wol-step-title">${t}</div><div class="wol-step-text">${txt}</div></div></div>`
  ).join("");
}

function wolInput(btn) {
  const body = $("wolBody");
  switch (btn) {
    case "Up": body.scrollBy({ top: -160, behavior: "smooth" }); break;
    case "Down": body.scrollBy({ top: 160, behavior: "smooth" }); break;
    case "B": case "A": wolOpen = false; $("overlay-wol").classList.remove("active"); break;
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
  if (v === "collections") renderCollections();
  if (v === "detail") renderDetail();
  if (v === "settings") { renderSettings(); setBackdrop(null); }
}

function cycleSection(dir) {
  const cur = SECTIONS.indexOf(view);
  if (cur === -1) return;
  const next = SECTIONS[(cur + dir + SECTIONS.length) % SECTIONS.length];
  switchView(next);
}

/* ============================== input routing ============================== */

const DIRECTIONS = new Set(["Up", "Down", "Left", "Right"]);

function handleInput(btn, src) {
  if (inputOpen) return; // the text field (and the touch keyboard's own pad support) owns input
  // Only a direction hands control back to the pad. A face button must never re-arm a
  // highlight the pointer has cleared, so A over empty space does nothing.
  if (DIRECTIONS.has(btn)) setInputMode("pad");
  if (confirmState) { confirmInput(btn); return; }
  if (radialSub) { radialSubInput(btn); return; }
  if (radialOpen) { radialInput(btn); return; }
  if (ingameOpen) { ingameInput(btn); return; }
  if (wolOpen) { wolInput(btn); return; }
  if (filterOpen) { filterInput(btn); return; }
  if (gameMenu) { gameMenuInput(btn); return; }
  if (collectOpen) { collectInput(btn); return; }
  if (manageOpen) { manageInput(btn); return; }

  if ((btn === "LB" || btn === "RB") && SECTIONS.includes(view)) {
    cycleSection(btn === "RB" ? 1 : -1);
    return;
  }

  if (view === "library") libraryInput(btn);
  else if (view === "collections") collectionsInput(btn);
  else if (view === "detail") detailInput(btn);
  else if (view === "settings") settingsInput(btn);
}

const KEYMAP = {
  ArrowUp: "Up", ArrowDown: "Down", ArrowLeft: "Left", ArrowRight: "Right",
  Enter: "A", Escape: "B", Backspace: "B",
  KeyY: "Y", KeyX: "X",
  BracketLeft: "LB", BracketRight: "RB",
};

window.addEventListener("keydown", (e) => {
  if (inputOpen) return;
  const btn = KEYMAP[e.code];
  if (!btn) return;
  e.preventDefault();
  handleInput(btn, "kb");   // handleInput switches to pad mode on directions only
});

/* ============================== host messages ============================== */

let lastBatteryMsg = null;

function handleHostMessage(m) {
  switch (m.type) {
    case "state": {
      const firstState = S.settings === null;
      const wasEmpty = S.games.length === 0;
      S.games = m.games || [];
      S.collections = m.collections || [];
      S.settings = m.settings;
      S.displays = m.displays || [];
      S.startupRegistered = m.startupRegistered;
      S.gameRunning = m.gameRunning;
      S.runningGameId = m.runningGameId;
      S.scanning = m.scanning;
      if (S.settings) S.settings.launchOnStartup = m.startupRegistered;
      if ((firstState || wasEmpty) && focus.zone === "tabs" && S.games.length)
        focus = { zone: "cont", row: 0, col: 0 };
      renderLibrary();
      if (view === "collections") renderCollections();
      if (view === "settings") renderSettings();
      if (view === "detail") renderDetail();
      if (collectOpen) renderCollect();
      if (manageOpen) renderManage();
      if (gameMenu) renderGameMenu();
      if (filterOpen) renderFilter();
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
      handleInput(m.button, "pad");
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
    case "dismiss":
      dismissOverlays();
      break;
    case "stick":
      if (radialOpen && !radialSub) {
        const i = stickToSpoke(m.x, m.y, RADIAL_ITEMS.length);
        if (i !== radialIdx) { radialIdx = i; renderRadial(); }
      }
      break;
    case "inputMode":
      // Only the host can tell us the stick moved the cursor, or that the launcher just came
      // back to the foreground and the pad should be driving again. It never pushes "pad" off
      // a face button, so this can't re-arm a highlight the pointer has cleared.
      setInputMode(m.mode);
      break;
    case "padConnected":
      S.padConnected = m.connected;
      if (m.connected) toast("Controller connected");
      else updateBattery(undefined, 0);
      if (lastBatteryMsg && m.connected) updateBattery(lastBatteryMsg.batteryType, lastBatteryMsg.level);
      break;
    case "battery":
      lastBatteryMsg = m;
      updateBattery(m.batteryType, m.level);
      break;
    case "game":
      S.gameRunning = m.running;
      S.runningGameId = m.id;
      renderLibrary();
      break;
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

function mockHandle(msg) {
  const pushState = () => {
    const seedW = (t) => `https://picsum.photos/seed/${t.toLowerCase().replace(/[^a-z]/g, "")}w/600/340`;
    const seedP = (t) => `https://picsum.photos/seed/${t.toLowerCase().replace(/[^a-z]/g, "")}/400/480`;
    const g = (title, platform, opts = {}) => ({
      id: platform.toLowerCase() + ":" + title.toLowerCase().replace(/[^a-z]/g, ""),
      title, platform, installed: true, manual: platform === "Manual",
      playtimeMinutes: 0, sessions: 0, lastPlayed: null, sizeBytes: 0,
      favorite: false, hidden: false, preferDirectLaunch: false, args: null,
      coverFile: seedP(title), bannerFile: seedW(title), ...opts,
    });
    const now = Date.now();
    const games = [
      g("Hollowmark: Second Ascent", "Steam", { playtimeMinutes: 4934, sessions: 41, favorite: true, lastPlayed: new Date(now - 86400000).toISOString(), sizeBytes: 64.2 * 1024 ** 3, installDir: "C:\\Games\\Steam\\steamapps\\common\\Hollowmark" }),
      g("Ridgeline 84", "Epic", { playtimeMinutes: 660, sessions: 9, lastPlayed: new Date(now - 2 * 86400000).toISOString(), sizeBytes: 31 * 1024 ** 3 }),
      g("Salt & Tide", "GOG", { playtimeMinutes: 2820, sessions: 30, favorite: true, lastPlayed: new Date(now - 3 * 86400000).toISOString(), sizeBytes: 12 * 1024 ** 3 }),
      g("Foundry Nine", "Manual", { playtimeMinutes: 360, sessions: 5, lastPlayed: new Date(now - 4 * 86400000).toISOString(), sizeBytes: 8 * 1024 ** 3 }),
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
      g("Iron Compass", "Steam", { installed: false, sizeBytes: 42 * 1024 ** 3 }),
      g("Low Country", "GOG", { installed: false, sizeBytes: 18 * 1024 ** 3 }),
      g("Solaria Drift", "Epic", { installed: false, sizeBytes: 61 * 1024 ** 3 }),
      g("Hollow Reef", "Steam", { installed: false, sizeBytes: 27 * 1024 ** 3 }),
      g("Glassmoor", "Manual", { installed: false, sizeBytes: 55 * 1024 ** 3 }),
      g("Tidewrack", "GOG", { installed: false, sizeBytes: 12 * 1024 ** 3 }),
      g("Forza Horizon 5", "Xbox", { playtimeMinutes: 1240, sessions: 18, sizeBytes: 110 * 1024 ** 3 }),
      g("Sea of Thieves", "Xbox", { sizeBytes: 78 * 1024 ** 3 }),
      g("Starfield", "Xbox", { installed: false, sizeBytes: 125 * 1024 ** 3 }),
      g("Wallpaper Engine", "Steam", { hidden: true, sizeBytes: 2 * 1024 ** 3 }),
    ];
    if (mockHandle._fav) games.forEach(x => { if (mockHandle._fav[x.id] !== undefined) x.favorite = mockHandle._fav[x.id]; });
    if (mockHandle._running) { S.gameRunning = true; S.runningGameId = mockHandle._running; }
  if (mockHandle._hidden) games.forEach(x => { if (mockHandle._hidden[x.id] !== undefined) x.hidden = mockHandle._hidden[x.id]; });
    handleHostMessage({
      type: "state",
      games,
      collections: mockCollections,
      settings: S.settings || {
        tvDeviceName: "\\\\.\\DISPLAY2", switchPrimaryOnLaunch: true, repositionGameWindow: true,
        keepFocus: true, launchOnStartup: false, gamepadMouseEnabled: true, gamepadMouseDuringGame: false,
        deadzone: 0.18, sensitivity: 1.0, accelExponent: 1.8, hideCursorSystemWide: false,
        boostButton: "RT", boostMultiplier: 2.5,
        leftClickButton: "A", rightClickButton: "B",
        minimizeCombo: "LS + RS",
        keyboardToggleButton: "Start", keyboardToggleHoldMs: 600,
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
    setTimeout(() => { handleHostMessage({ type: "padConnected", connected: true }); handleHostMessage({ type: "battery", batteryType: 3, level: 2 }); }, 700);
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
  }
}

/* ============================== boot ============================== */

fitStage();
tickClock();
renderTabbars();
renderWol();
switchView("library");
send({ cmd: "ready" });
