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

/* input mode: "pad" hides the pointer and ignores hover; "pointer" is stick or real mouse */
let inputMode = "pointer";

function setInputMode(mode) {
  if (inputMode === mode) return;
  inputMode = mode;
  document.body.classList.toggle("pad-mode", mode === "pad");
}

/** Hover should only move focus when the pointer is actually the active input. */
function hoverEnabled() { return inputMode === "pointer"; }

window.addEventListener("mousemove", () => setInputMode("pointer"));

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
  const t = `${String(now.getHours()).padStart(2, "0")}:${String(now.getMinutes()).padStart(2, "0")}`;
  document.querySelectorAll(".clock").forEach(el => el.textContent = t);
}
setInterval(tickClock, 15000);

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
    .slice(0, 5);

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

function renderLibrary() {
  const { cont, rows, total } = libraryData();
  contItems = cont;
  gridRows = rows;

  $("titleCount").textContent = `${S.games.length} TITLE${S.games.length === 1 ? "" : "S"}`;
  $("gridLabel").textContent = filterSummary(total);

  // Continue row (landscape banner art)
  const rowEl = $("continueRow");
  rowEl.innerHTML = "";
  $("continueSection").style.display = cont.length ? "" : "none";
  cont.forEach((g, i) => {
    const item = document.createElement("div");
    item.className = "cont-item";

    const art = document.createElement("div");
    art.className = "cont-art";
    applyArt(g, art, bannerUrl(g));

    const maxPt = Math.max(...cont.map(x => x.playtimeMinutes || 0), 1);
    const pct = Math.max(4, Math.round((g.playtimeMinutes || 0) / maxPt * 100));
    const track = document.createElement("div");
    track.className = "cont-progress-track";
    track.innerHTML = `<div class="cont-progress" style="width:${pct}%"></div>`;
    art.appendChild(track);

    const meta = document.createElement("div");
    meta.className = "cont-meta";
    meta.innerHTML = `<div class="cont-title">${esc(g.title)}</div><div class="cont-sub">${esc(shortMeta(g))}</div>`;

    item.appendChild(art); item.appendChild(meta);
    item.addEventListener("mouseenter", () => {
      if (!hoverEnabled()) return;
      focus = { zone: "cont", row: 0, col: i }; updateLibraryFocus(true);
    });
    item.addEventListener("click", () => { focus = { zone: "cont", row: 0, col: i }; updateLibraryFocus(true); libraryAccept("A"); });
    rowEl.appendChild(item);
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
  const tabs = document.querySelectorAll("#screen-library [data-tabbar] .tab");
  tabs.forEach((t, i) => t.classList.toggle("focused", focus.zone === "tabs" && i === tabIdx));

  const contFocused = focus.zone === "cont";
  $("continueSection").classList.toggle("zone-dim", !contFocused && contItems.length > 0);
  document.querySelectorAll("#continueRow .cont-item").forEach((el, i) => {
    el.classList.toggle("focused", contFocused && i === focus.col);
  });

  document.querySelectorAll("#gridScroll .grid-row").forEach((rowEl, r) => {
    const rowFocused = focus.zone === "grid" && r === focus.row;
    rowEl.classList.toggle("zone-dim", !rowFocused);
    rowEl.querySelectorAll(".grid-item").forEach((el, c) => {
      const f = rowFocused && c === focus.col;
      el.classList.toggle("focused", f);
      if (f && !noScroll) el.scrollIntoView({ block: "nearest", behavior: "smooth" });
    });
  });

  setBackdrop(focusedGame());
}

/* ============================== library nav ============================== */

function libraryNav(btn) {
  const zones = ["tabs"];
  if (contItems.length) zones.push("cont");
  for (let i = 0; i < gridRows.length; i++) zones.push("grid" + i);

  const zoneIndex = () => {
    if (focus.zone === "tabs") return 0;
    if (focus.zone === "cont") return 1;
    return zones.indexOf("grid" + focus.row);
  };

  if (btn === "Left" || btn === "Right") {
    const dir = btn === "Right" ? 1 : -1;
    if (focus.zone === "tabs") tabIdx = Math.max(0, Math.min(TAB_DEFS.length - 1, tabIdx + dir));
    else if (focus.zone === "cont") focus.col = Math.max(0, Math.min(contItems.length - 1, focus.col + dir));
    else focus.col = Math.max(0, Math.min(gridRows[focus.row].length - 1, focus.col + dir));
    updateLibraryFocus();
    return;
  }

  if (btn === "Up" || btn === "Down") {
    const dir = btn === "Down" ? 1 : -1;
    const zi = Math.max(0, Math.min(zones.length - 1, zoneIndex() + dir));
    const z = zones[zi];
    const xCenter = focus.zone === "cont" ? focus.col * 328 + 150
                  : focus.zone === "grid" ? focus.col * 223 + 100 : 0;
    if (z === "tabs") { focus = { zone: "tabs", row: 0, col: 0 }; }
    else if (z === "cont") {
      const col = focus.zone === "tabs" ? 0 : Math.round((xCenter - 150) / 328);
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
  if (S.gameRunning) { toast("A game is already running"); return; }
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
      card.className = "coll-card" + (i === collListIdx ? " focused" : "");

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

      card.addEventListener("mouseenter", () => { if (hoverEnabled() && collListIdx !== i) { collListIdx = i; renderCollections(); } });
      card.addEventListener("click", () => { collListIdx = i; openCollectionGrid(c.id); });
      listEl.appendChild(card);
    });

    const focused = listEl.querySelector(".coll-card.focused");
    if (focused) focused.scrollIntoView({ block: "nearest", behavior: "smooth" });

    const delHint = cols[collListIdx] && cols[collListIdx].custom
      ? `<div class="legend-item"><div class="btn-badge">X</div><span>Delete collection</span></div>` : "";
    legend.innerHTML = `
      <div class="legend-item"><div class="btn-badge btn-a">A</div><span>Open</span></div>
      <div class="legend-item"><div class="btn-badge">B</div><span>Back</span></div>
      ${delHint}
      <div class="legend-item"><div class="btn-pill mono">LB · RB</div><span>Section</span></div>`;
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
      <div class="legend-item"><div class="btn-badge">Y</div><span>Details</span></div>
      <div class="legend-item"><div class="btn-badge">B</div><span>Collections</span></div>
      <div class="legend-item"><div class="btn-pill mono">LB · RB</div><span>Section</span></div>`;
  }
}

function updateCollFocus(noScroll) {
  document.querySelectorAll("#collGridScroll .grid-row").forEach((rowEl, r) => {
    const rowFocused = r === collFocus.row;
    rowEl.classList.toggle("zone-dim", !rowFocused);
    rowEl.querySelectorAll(".grid-item").forEach((el, c) => {
      const f = rowFocused && c === collFocus.col;
      el.classList.toggle("focused", f);
      if (f && !noScroll) el.scrollIntoView({ block: "nearest", behavior: "smooth" });
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
      case "Up": collListIdx = Math.max(0, collListIdx - 1); renderCollections(); break;
      case "Down": collListIdx = Math.min(cols.length - 1, collListIdx + 1); renderCollections(); break;
      case "A": if (cols[collListIdx]) openCollectionGrid(cols[collListIdx].id); break;
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
        if (collFocus.row === 0) { collMode = "list"; renderCollections(); }
        else { collFocus.row--; collFocus.col = Math.min(collFocus.col, collGridRows[collFocus.row].length - 1); updateCollFocus(); }
        break;
      case "Down":
        if (collFocus.row < collGridRows.length - 1) { collFocus.row++; collFocus.col = Math.min(collFocus.col, collGridRows[collFocus.row].length - 1); updateCollFocus(); }
        break;
      case "A": if (g) { if (g.installed) launchGame(g); else toast(`${g.title} is not installed`); } break;
      case "Y": if (g) openGameMenu(g.id, "collections"); break;
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
  document.querySelectorAll("#detailActions .pill-btn").forEach(el => {
    el.classList.toggle("focused", el.dataset.act === DETAIL_BTNS[detailBtn]);
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
    case "A": detailActivate(); break;
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
  rows.push(toggleRow("Stay active while a game runs", "Keep the gamepad-mouse alive in games (off avoids fighting native controller support)",
    () => s.gamepadMouseDuringGame, v => set(() => s.gamepadMouseDuringGame = v)));
  rows.push(sliderRow("Stick deadzone", () => s.deadzone, 0.05, 0.40, 0.01, v => set(() => s.deadzone = v), v => v.toFixed(2)));
  rows.push(sliderRow("Cursor sensitivity", () => s.sensitivity, 0.2, 3.0, 0.1, v => set(() => s.sensitivity = v), v => v.toFixed(1) + "×"));
  rows.push(sliderRow("Acceleration curve", () => s.accelExponent, 1.0, 3.0, 0.1, v => set(() => s.accelExponent = v), v => v.toFixed(1)));
  rows.push(cycleRow("Left click button", ["A", "B", "X", "Y", "LB", "RB", "LS", "RS"], () => s.leftClickButton, v => set(() => s.leftClickButton = v),
    "Sends a real mouse click when the launcher is not focused"));
  rows.push(cycleRow("Right click button", ["A", "B", "X", "Y", "LB", "RB", "LS", "RS"], () => s.rightClickButton, v => set(() => s.rightClickButton = v)));
  rows.push(toggleRow("Hide pointer system-wide", "The pointer always hides inside the launcher on D-pad input; this extends it to the rest of Windows. Replaces the system cursors, so it is restored when Couch Launcher exits",
    () => s.hideCursorSystemWide, v => set(() => s.hideCursorSystemWide = v)));
  rows.push({
    name: "Minimize / restore launcher", hint: "Press View + Menu (Back + Start) together at any time",
    type: "static", value: "VIEW + MENU",
  });

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

function sliderRow(name, get, min, max, step, setV, fmt) {
  return {
    name, type: "slider", value: get(), min, max, fmt,
    adjust: (dir) => {
      let v = Math.round((get() + dir * step) / step) * step;
      v = Math.max(min, Math.min(max, v));
      setV(v);
    },
  };
}

function cycleRow(name, options, get, setV, hint) {
  return {
    name, hint, type: "select", value: get(),
    adjust: (dir) => {
      const i = (options.indexOf(get()) + dir + options.length) % options.length;
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
    el.className = "set-row" + (idx === settingsIdx ? " focused" : "");

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

    el.innerHTML = `<div class="set-left"><div class="set-name">${esc(r.name)}</div>${r.hint ? `<div class="set-hint">${esc(r.hint)}</div>` : ""}</div><div class="set-value">${right}</div>`;

    el.addEventListener("mouseenter", () => { if (hoverEnabled() && settingsIdx !== idx) { settingsIdx = idx; renderSettings(); } });
    el.addEventListener("click", () => { settingsIdx = idx; const row = settingsRows().filter(x => !x.section)[idx]; if (row.action) row.action(); else if (row.adjust) row.adjust(1); });
    scroll.appendChild(el);
  });

  const focused = scroll.querySelector(".set-row.focused");
  if (focused) focused.scrollIntoView({ block: "nearest", behavior: "smooth" });
}

function settingsInput(btn) {
  const rows = settingsRows().filter(r => !r.section);
  const row = rows[settingsIdx];
  switch (btn) {
    case "Up": settingsIdx = Math.max(0, settingsIdx - 1); renderSettings(); break;
    case "Down": settingsIdx = Math.min(rows.length - 1, settingsIdx + 1); renderSettings(); break;
    case "Left": if (row && row.adjust) row.adjust(-1); break;
    case "Right": if (row && row.adjust) row.adjust(1); break;
    case "A": if (row) { if (row.action) row.action(); else if (row.adjust) row.adjust(1); } break;
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
    { name: "Filter", summary: n ? `${n} active` : "All games", open: "filter" },
    { name: "Sort", summary: SORTS.find(s => s.id === F.sort).label, open: "sort" },
  ];
}

/** Flat list of the multi-select dropdown, with category headers interleaved. */
function filterDropdownRows() {
  const rows = [{ cat: "PLATFORM" }];
  PLATFORMS.forEach(p => rows.push({
    label: p, checked: F.platforms.has(p),
    toggle: () => { F.platforms.has(p) ? F.platforms.delete(p) : F.platforms.add(p); },
  }));
  rows.push({ cat: "STATUS" });
  STATUSES.forEach(s => rows.push({
    label: s, checked: F.status.has(s),
    toggle: () => { F.status.has(s) ? F.status.delete(s) : F.status.add(s); },
  }));
  rows.push({ cat: "OTHER" });
  rows.push({
    label: "Favorites only", checked: F.fav,
    toggle: () => { F.fav = !F.fav; },
  });
  return rows;
}

function sortDropdownRows() {
  return SORTS.map(s => ({
    label: s.label, checked: F.sort === s.id, radio: true,
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

  const list = $("filterList");
  list.innerHTML = "";
  rows.forEach((r, i) => {
    if (r.cat) {
      const c = document.createElement("div");
      c.className = "ov-cat";
      c.textContent = r.cat;
      list.appendChild(c);
      return;
    }
    const el = document.createElement("div");
    el.className = "ov-row" + (i === filterIdx ? " focused" : "");
    if (r.open) {
      el.innerHTML = `<span>${esc(r.name)}</span><div class="ov-value"><span class="ov-summary">${esc(r.summary)}</span><span class="arrow">▸</span></div>`;
    } else {
      const mark = r.radio ? "●" : "✓";
      el.innerHTML = `<span>${esc(r.label)}</span><span class="ov-check${r.checked ? "" : " off"}">${r.checked ? mark : "○"}</span>`;
    }
    el.addEventListener("mouseenter", () => { if (hoverEnabled() && filterIdx !== i) { filterIdx = i; renderFilter(); } });
    el.addEventListener("click", () => { filterIdx = i; filterActivate(); });
    list.appendChild(el);
  });

  const foot = $("filterFoot");
  foot.innerHTML = filterLevel === null
    ? `<div class="legend-item"><div class="btn-badge btn-a">A</div><span>Open</span></div>
       <div class="legend-item"><div class="btn-badge">Y</div><span>Reset all</span></div>
       <div class="legend-item"><div class="btn-badge">B</div><span>Close</span></div>`
    : `<div class="legend-item"><div class="btn-badge btn-a">A</div><span>${filterLevel === "sort" ? "Choose" : "Toggle"}</span></div>
       <div class="legend-item"><div class="btn-badge">Y</div><span>Reset all</span></div>
       <div class="legend-item"><div class="btn-badge">B</div><span>Back</span></div>`;

  const focusedEl = list.querySelector(".ov-row.focused");
  if (focusedEl) focusedEl.scrollIntoView({ block: "nearest" });
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
    case "A": case "Right": filterActivate(); break;
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
  if (!g) return [{ label: "Close", action: closeGameMenu }];
  const items = [
    { label: "View game", sub: "Full details page", action: () => { const f = gameMenu.from; closeGameMenu(); openDetail(g.id, f); } },
    { label: g.favorite ? "Remove from favorites" : "Add to favorites", action: () => send({ cmd: "toggleFavorite", id: g.id }) },
    { label: "Add to collection", action: () => { closeGameMenu(); openCollect(g.id); } },
    { label: "Change cover art", action: () => { closeGameMenu(); send({ cmd: "pickCover", id: g.id }); } },
    { label: g.hidden ? "Unhide" : "Hide", sub: g.hidden ? "Show in the library again" : "Not a game? Keep it out of the library",
      action: () => { send({ cmd: "toggleHidden", id: g.id }); closeGameMenu(); } },
  ];
  if (g.manual) items.push({
    label: "Remove from library", danger: true,
    action: () => { send({ cmd: "removeGame", id: g.id }); closeGameMenu(); },
  });
  items.push({ label: "Close", action: closeGameMenu });
  return items;
}

function renderGameMenu() {
  if (!gameMenu) return;
  const g = gameById(gameMenu.gameId);
  $("gameMenuTitle").textContent = (g ? g.title : "GAME").toUpperCase();
  const list = $("gameMenuList");
  list.innerHTML = "";
  gameMenuItems().forEach((it, i) => {
    const el = document.createElement("div");
    el.className = "quick-item" + (i === gameMenu.idx ? " focused" : "") + (it.danger ? " danger" : "");
    el.innerHTML = `<span>${esc(it.label)}</span>${it.sub ? `<span class="quick-sub">${esc(it.sub)}</span>` : ""}`;
    el.addEventListener("mouseenter", () => { if (hoverEnabled() && gameMenu.idx !== i) { gameMenu.idx = i; renderGameMenu(); } });
    el.addEventListener("click", () => it.action());
    list.appendChild(el);
  });
}

function gameMenuInput(btn) {
  const items = gameMenuItems();
  switch (btn) {
    case "Up": gameMenu.idx = Math.max(0, gameMenu.idx - 1); renderGameMenu(); break;
    case "Down": gameMenu.idx = Math.min(items.length - 1, gameMenu.idx + 1); renderGameMenu(); break;
    case "A": items[gameMenu.idx].action(); break;
    case "B": case "Y": closeGameMenu(); break;
  }
}

/* ============================== add-to-collection overlay ============================== */

let collectTarget = null;

function collectItems() {
  const g = gameById(collectTarget);
  if (!g) return [];
  const items = [{
    label: "Favorites", star: true, checked: g.favorite,
    action: () => { send({ cmd: "toggleFavorite", id: g.id }); },
  }, {
    label: "Hidden", checked: g.hidden,
    action: () => { send({ cmd: "toggleHidden", id: g.id }); },
  }];
  S.collections.forEach(c => items.push({
    label: c.name, checked: c.gameIds.includes(g.id),
    action: () => send({ cmd: "toggleInCollection", collectionId: c.id, id: g.id }),
  }));
  items.push({
    label: "New collection…",
    action: () => {
      closeCollect();
      openInput("NEW COLLECTION NAME", "", name => send({ cmd: "createCollection", name, gameId: g.id }));
    },
  });
  items.push({ label: "Done", action: closeCollect });
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
  const list = $("collectList");
  list.innerHTML = "";
  collectItems().forEach((it, i) => {
    const el = document.createElement("div");
    el.className = "quick-item" + (i === collectIdx ? " focused" : "");
    const check = it.checked === undefined ? "" :
      `<span class="ov-check${it.checked ? "" : " off"}">${it.star ? "★" : "✓"}</span>`;
    el.innerHTML = `<span>${esc(it.label)}</span>${check}`;
    el.addEventListener("mouseenter", () => { if (hoverEnabled() && collectIdx !== i) { collectIdx = i; renderCollect(); } });
    el.addEventListener("click", () => it.action());
    list.appendChild(el);
  });
}

function collectInput(btn) {
  const items = collectItems();
  switch (btn) {
    case "Up": collectIdx = Math.max(0, collectIdx - 1); renderCollect(); break;
    case "Down": collectIdx = Math.min(items.length - 1, collectIdx + 1); renderCollect(); break;
    case "A": items[collectIdx].action(); break;
    case "B": closeCollect(); break;
  }
}

/* ============================== manage overlay ============================== */

function manageItems() {
  const g = gameById(detailGameId);
  if (!g) return [{ label: "Back", action: closeManage }];
  const items = [];
  items.push({
    label: "Set launch arguments", sub: g.args || "e.g. --launcher-skip",
    action: () => {
      closeManage();
      openInput("LAUNCH ARGUMENTS", g.args || "", v => send({ cmd: "setArgs", id: g.id, args: v }));
    },
  });
  items.push({
    label: "Choose executable (launch directly)…",
    sub: g.preferDirectLaunch && g.exePath ? g.exePath.split("\\").pop() : "Bypass the store launcher",
    action: () => { send({ cmd: "pickExe", id: g.id }); closeManage(); },
  });
  if (g.preferDirectLaunch && (g.platform === "Steam" || g.platform === "Epic"))
    items.push({ label: `Launch through ${g.platform} again`, action: () => { send({ cmd: "launchViaStore", id: g.id }); closeManage(); } });
  items.push({ label: "Change cover art", action: () => { send({ cmd: "pickCover", id: g.id }); closeManage(); } });
  if (g.manual) items.push({
    label: "Remove from library", danger: true,
    action: () => { send({ cmd: "removeGame", id: g.id }); closeManage(); switchView(detailReturn); },
  });
  items.push({ label: "Back", action: closeManage });
  return items;
}

function openManage() { manageOpen = true; manageIdx = 0; renderManage(); $("overlay-manage").classList.add("active"); }
function closeManage() { manageOpen = false; $("overlay-manage").classList.remove("active"); }

function renderManage() {
  const list = $("manageList");
  list.innerHTML = "";
  manageItems().forEach((it, i) => {
    const el = document.createElement("div");
    el.className = "quick-item" + (i === manageIdx ? " focused" : "") + (it.danger ? " danger" : "");
    el.innerHTML = `<span>${esc(it.label)}</span>${it.sub ? `<span class="quick-sub">${esc(it.sub)}</span>` : ""}`;
    el.addEventListener("mouseenter", () => { if (hoverEnabled() && manageIdx !== i) { manageIdx = i; renderManage(); } });
    el.addEventListener("click", () => it.action());
    list.appendChild(el);
  });
}

function manageInput(btn) {
  const items = manageItems();
  switch (btn) {
    case "Up": manageIdx = Math.max(0, manageIdx - 1); renderManage(); break;
    case "Down": manageIdx = Math.min(items.length - 1, manageIdx + 1); renderManage(); break;
    case "A": items[manageIdx].action(); break;
    case "B": closeManage(); break;
  }
}

/* ============================== confirm overlay ============================== */

function renderConfirm() {
  $("confirmTitle").textContent = confirmState.title;
  const list = $("confirmList");
  list.innerHTML = "";
  ["Yes, delete", "Cancel"].forEach((label, i) => {
    const el = document.createElement("div");
    el.className = "quick-item" + (i === confirmIdx ? " focused" : "") + (i === 0 ? " danger" : "");
    el.innerHTML = `<span>${label}</span>`;
    el.addEventListener("mouseenter", () => { if (hoverEnabled() && confirmIdx !== i) { confirmIdx = i; renderConfirm(); } });
    el.addEventListener("click", () => confirmChoose(i));
    list.appendChild(el);
  });
}

function confirmChoose(i) {
  const st = confirmState;
  confirmState = null;
  $("overlay-confirm").classList.remove("active");
  if (i === 0 && st) st.onYes();
}

function confirmInput(btn) {
  switch (btn) {
    case "Up": confirmIdx = Math.max(0, confirmIdx - 1); renderConfirm(); break;
    case "Down": confirmIdx = Math.min(1, confirmIdx + 1); renderConfirm(); break;
    case "A": confirmChoose(confirmIdx); break;
    case "B": confirmChoose(1); break;
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

function handleInput(btn, src) {
  if (inputOpen) return; // the text field (and the touch keyboard's own pad support) owns input
  if (confirmState) { confirmInput(btn); return; }
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
  setInputMode("pad");   // keyboard drives like a D-pad: get the pointer out of the way
  handleInput(btn, "kb");
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
    case "inputMode":
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
      break;
    case "toast":
      toast(m.message);
      break;
  }
}

if (HOST) HOST.addEventListener("message", (e) => handleHostMessage(e.data));

/* ============================== mock (browser preview only) ============================== */

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
      g("Nightpost", "Steam"), g("Umber Fields", "Steam"), g("The Quiet Shore", "Epic"),
      g("Vector Bloom", "Steam"), g("Marrow", "GOG"), g("Halden Court", "Manual"),
      g("Tin Orchard", "Epic"), g("Paper Lanterns", "Epic"), g("Ninefold", "Manual"),
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
    if (mockHandle._hidden) games.forEach(x => { if (mockHandle._hidden[x.id] !== undefined) x.hidden = mockHandle._hidden[x.id]; });
    handleHostMessage({
      type: "state",
      games,
      collections: mockCollections,
      settings: S.settings || {
        tvDeviceName: "\\\\.\\DISPLAY2", switchPrimaryOnLaunch: true, repositionGameWindow: true,
        keepFocus: true, launchOnStartup: false, gamepadMouseEnabled: true, gamepadMouseDuringGame: false,
        deadzone: 0.18, sensitivity: 1.0, accelExponent: 1.8, hideCursorSystemWide: false,
        leftClickButton: "A", rightClickButton: "B",
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
