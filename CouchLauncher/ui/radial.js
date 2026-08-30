"use strict";

/*
 * Power Wheel (the radial) and the in-game menu.
 *
 * Loaded after app.js and shares its globals ($, send, esc, iconSvg, foot, renderMenu,
 * focusVisible, hoverEnabled, setOverlayMode, switchView, gameById, S).
 */

let radialOpen = false, radialIdx = 0;
let radialSub = null, radialSubIdx = 0;     // null | "windows" | "shortcuts"
let ingameOpen = false, ingameIdx = 0;
let overlayTargetTitle = "";
let hostWindows = [];

/* Spokes are laid out clockwise from the top, so with six of them the index IS the clock
   position: 0 top, 1 top-right, 2 lower-right, 3 bottom, 4 lower-left, 5 top-left. */
const RADIAL_ITEMS = [
  { id: "close",       label: "Close window",  icon: "x",        danger: true,   // top
    desc: "Ask the window behind this menu to quit" },
  { id: "windows",     label: "Switch window", icon: "viewBtn",                  // top-right
    desc: "Pick another open window and bring it to the TV" },
  { id: "shortcuts",   label: "Shortcuts",     icon: "apps",
    desc: "Task Manager, Explorer, Settings and friends" },
  { id: "keyboard",    label: "Keyboard",      icon: "keyboard",
    desc: "Show or hide the on-screen keyboard" },
  { id: "centerMouse", label: "Center mouse",  icon: "pointer",
    desc: "Park the pointer in the middle of the TV" },
  { id: "sleep",       label: "Sleep",         icon: "moon",                     // top-left
    desc: "Blank the screen — any button wakes it" },
];

/* Where the highlight sits before the stick has been pushed anywhere. Deliberately NOT spoke
   0: that one closes a window, and A on a menu you have only just opened should not be able
   to destroy something by default. */
const RADIAL_HOME = 1;

const SHORTCUTS = [
  { id: "taskManager",     label: "Task Manager",     icon: "bars" },
  { id: "explorer",        label: "File Explorer",    icon: "folder" },
  { id: "settings",        label: "Windows Settings", icon: "gear" },
  { id: "displaySettings", label: "Display Settings", icon: "monitor" },
  { id: "volume",          label: "Volume Mixer",     icon: "volume" },
  // Distinct from Sleep, which only blanks the screen: this hands the session to the Windows
  // lock screen, which the pad cannot drive at all.
  { id: "lock",            label: "Lock PC",          icon: "lock" },
];

/** Close every transient menu so an overlay never stacks on a stale one. */
function closeAllMenus() {
  filterOpen = false; gameMenu = null; collectOpen = false;
  manageOpen = false; confirmState = null; wolOpen = false;
  ["overlay-filter", "overlay-gamemenu", "overlay-collect",
   "overlay-manage", "overlay-confirm", "overlay-wol"]
    .forEach(id => $(id).classList.remove("active"));
}

/* ============================== radial ============================== */

function openRadial(targetTitle) {
  radialOpen = true; radialIdx = RADIAL_HOME; radialSub = null;
  overlayTargetTitle = targetTitle || "";
  setOverlayMode(true);
  closeAllMenus();
  renderRadial();
  $("overlay-radial").classList.add("active");
}

function closeRadial(refocus) {
  radialOpen = false; radialSub = null;
  $("overlay-radial").classList.remove("active");
  $("overlay-radialsub").classList.remove("active");
  setOverlayMode(false);
  send({ cmd: "closeOverlay", refocus: refocus !== false });
}

function renderRadial() {
  const ring = $("radialRing");
  ring.innerHTML = "";
  const R = 310, cx = 440, cy = 440;
  RADIAL_ITEMS.forEach((it, i) => {
    const ang = (-90 + i * (360 / RADIAL_ITEMS.length)) * Math.PI / 180;
    const el = document.createElement("div");
    el.className = "radial-item"
      + (i === radialIdx && focusVisible() ? " focused" : "")
      + (it.danger ? " danger" : "");
    el.style.left = (cx + R * Math.cos(ang)) + "px";
    el.style.top = (cy + R * Math.sin(ang)) + "px";
    el.innerHTML = iconSvg(it.icon) + "<span>" + esc(it.label) + "</span>";
    el.addEventListener("mouseenter", () => {
      if (hoverEnabled() && radialIdx !== i) { radialIdx = i; renderRadial(); }
    });
    el.addEventListener("click", () => { radialIdx = i; radialActivate(); });
    ring.appendChild(el);
  });
  const sel = RADIAL_ITEMS[radialIdx];
  $("radialSelName").textContent = sel.label;
  $("radialDesc").textContent = sel.desc || "";
  // "Close window" is the only spoke that acts on the window behind the menu, so that is the
  // only one that needs to name it.
  $("radialTarget").textContent =
    sel.id === "close" ? (overlayTargetTitle ? overlayTargetTitle.toUpperCase() : "NO WINDOW") : "";
  $("radialFoot").innerHTML = foot(["A", "Select"], ["B", "Close"]);
}

function radialActivate() {
  const it = RADIAL_ITEMS[radialIdx];
  switch (it.id) {
    case "close":     send({ cmd: "windowAction", action: "close" });    closeRadial(false); break;
    case "keyboard":  send({ cmd: "toggleKeyboard" });                   closeRadial(true);  break;
    // Blanks the TV and parks the pad; any button brings it back, so it needs no confirm step.
    case "sleep":     send({ cmd: "suspend" });                          closeRadial(false); break;
    case "centerMouse":
      // host re-centres the pointer and closes the overlay; do not refocus the old window
      send({ cmd: "centerMouse" });
      radialOpen = false; radialSub = null;
      $("overlay-radial").classList.remove("active");
      $("overlay-radialsub").classList.remove("active");
      setOverlayMode(false);
      setInputMode("pointer");    // tells the host too, so its copy stays in step
      break;
    case "windows":   openRadialSub("windows"); break;
    case "shortcuts": openRadialSub("shortcuts"); break;
  }
}

function radialInput(btn) {
  const n = RADIAL_ITEMS.length;
  switch (btn) {
    case "Right": case "Down": radialIdx = (radialIdx + 1) % n; renderRadial(); break;
    case "Left":  case "Up":   radialIdx = (radialIdx - 1 + n) % n; renderRadial(); break;
    case "A": if (focusVisible()) radialActivate(); break;
    case "B": closeRadial(true); break;
  }
}

/* ---- submenus ---- */

function radialSubItems() {
  if (radialSub === "windows") {
    if (!hostWindows.length) return [{ label: "No open windows", icon: "info", action: () => {} }];
    return hostWindows.map(w => ({
      label: w.title, icon: "folder", sub: w.processName,
      // The host restores the window, drags it onto the TV and focuses it -- switching to a
      // window you cannot see would be pointless from the couch.
      action: () => { send({ cmd: "windowAction", action: "focus", handle: w.handle }); closeRadial(false); },
    }));
  }
  if (radialSub === "shortcuts") {
    return SHORTCUTS.map(s => ({
      label: s.label, icon: s.icon,
      action: () => { send({ cmd: "shortcut", id: s.id }); closeRadial(false); },
    }));
  }
  return [];
}

function openRadialSub(kind) {
  radialSub = kind; radialSubIdx = 0;
  if (kind === "windows") send({ cmd: "listWindows" });
  renderRadialSub();
  $("overlay-radialsub").classList.add("active");
}

function renderRadialSub() {
  const items = radialSubItems();
  radialSubIdx = Math.max(0, Math.min(radialSubIdx, items.length - 1));
  $("radialSubTitle").textContent = radialSub === "windows" ? "SWITCH WINDOW" : "SHORTCUTS";
  renderMenu($("radialSubList"), $("radialSubFoot"), items, radialSubIdx,
    foot(["A", "Select"], ["B", "Back"]),
    (i) => { if (radialSubIdx !== i) { radialSubIdx = i; renderRadialSub(); } },
    (i) => items[i] && items[i].action());
}

function radialSubInput(btn) {
  const items = radialSubItems();
  switch (btn) {
    case "Up": radialSubIdx = Math.max(0, radialSubIdx - 1); renderRadialSub(); break;
    case "Down": radialSubIdx = Math.min(items.length - 1, radialSubIdx + 1); renderRadialSub(); break;
    case "A": if (focusVisible() && items[radialSubIdx]) items[radialSubIdx].action(); break;
    case "B": radialSub = null; $("overlay-radialsub").classList.remove("active"); renderRadial(); break;
  }
}

/* ============================== in-game menu ============================== */

function openIngame() {
  ingameOpen = true; ingameIdx = 0;
  setOverlayMode(true);
  closeAllMenus();
  renderIngame();
  $("overlay-ingame").classList.add("active");
}

function hideIngame() {
  ingameOpen = false;
  $("overlay-ingame").classList.remove("active");
  setOverlayMode(false);
}

function ingameItems() {
  const g = gameById(S.runningGameId);
  return [
    { label: "Resume", icon: "play", desc: "Back to what you were playing",
      action: () => { hideIngame(); send({ cmd: "resumeGame" }); } },
    { label: "Home", icon: "home", desc: "Leave the game running and open the library",
      action: () => { hideIngame(); setOverlayMode(false); switchView("library"); send({ cmd: "goHome" }); } },
    { label: "Power Wheel", icon: "apps", desc: "Switch windows, keyboard, sleep",
      action: () => { hideIngame(); send({ cmd: "setRadialActive", active: true }); openRadial(g ? g.title : ""); } },
    // No confirm step: drop the overlay and land back on the library. Leaving the overlay up
    // over a closing game looks like nothing happened at all.
    { label: "Close game", icon: "x", danger: true, desc: "Ask the game to quit and return here",
      action: () => { hideIngame(); setOverlayMode(false); switchView("library"); send({ cmd: "closeGame" }); } },
  ];
}

function renderIngame() {
  const g = gameById(S.runningGameId);
  $("ingameTitle").textContent = (g ? g.title : "PLAYING").toUpperCase();

  const items = ingameItems();
  ingameIdx = Math.max(0, Math.min(ingameIdx, items.length - 1));
  $("ingameDesc").textContent = (items[ingameIdx] && items[ingameIdx].desc) || "";

  const row = $("ingameList");
  row.innerHTML = "";
  items.forEach((it, i) => {
    const el = document.createElement("div");
    el.className = "ingame-tile"
      + (i === ingameIdx && focusVisible() ? " focused" : "")
      + (it.danger ? " danger" : "");
    el.innerHTML = iconSvg(it.icon) + "<span>" + esc(it.label) + "</span>";
    el.addEventListener("mouseenter", () => {
      if (hoverEnabled() && ingameIdx !== i) { ingameIdx = i; renderIngame(); }
    });
    el.addEventListener("click", () => { ingameIdx = i; it.action(); });
    row.appendChild(el);
  });

  $("ingameFoot").innerHTML = foot(["A", "Select"], ["B", "Resume"]);
}

function ingameInput(btn) {
  const items = ingameItems();
  switch (btn) {
    // A row, so it reads left and right. Up/Down are deliberately inert rather than wrapping
    // the row, which would feel like the highlight jumped for no reason.
    case "Left":  ingameIdx = Math.max(0, ingameIdx - 1); renderIngame(); break;
    case "Right": ingameIdx = Math.min(items.length - 1, ingameIdx + 1); renderIngame(); break;
    case "A": if (focusVisible() && items[ingameIdx]) items[ingameIdx].action(); break;
    case "B": hideIngame(); send({ cmd: "resumeGame" }); break;
  }
}
/** Host asked us to tear down any overlay menu (e.g. the combo was tapped while one was open). */
function dismissOverlays() {
  radialOpen = false; radialSub = null; ingameOpen = false;
  ["overlay-radial", "overlay-radialsub", "overlay-ingame"]
    .forEach(id => $(id).classList.remove("active"));
  closeAllMenus();
  setOverlayMode(false);
}
