"use strict";

/*
 * Radial power menu and in-game menu.
 *
 * Loaded after app.js and shares its globals ($, send, esc, iconSvg, foot, renderMenu,
 * focusVisible, hoverEnabled, confirmState, renderConfirm, switchView, gameById, S).
 */

let radialOpen = false, radialIdx = 0;
let radialSub = null, radialSubIdx = 0;     // null | "windows" | "shortcuts"
let ingameOpen = false, ingameIdx = 0;
let overlayTargetTitle = "";
let hostWindows = [];

/* Switching to a window also drags it onto the TV, so there is no separate "move" spoke.
   The first spoke is what the stick points at when the menu opens, so it must be a safe
   one -- "Close window" sits last. */
const RADIAL_ITEMS = [
  { id: "windows",     label: "Switch window", icon: "folder" },
  { id: "shortcuts",   label: "Shortcuts",     icon: "terminal" },
  { id: "keyboard",    label: "Keyboard",      icon: "file" },
  { id: "centerMouse", label: "Center mouse",  icon: "info" },
  { id: "suspend",     label: "Suspend",       icon: "eyeOff" },
  { id: "close",       label: "Close window",  icon: "trash", danger: true },
];

const SHORTCUTS = [
  { id: "taskManager",     label: "Task Manager",     icon: "terminal" },
  { id: "explorer",        label: "File Explorer",    icon: "folder" },
  { id: "settings",        label: "Windows Settings", icon: "store" },
  { id: "displaySettings", label: "Display Settings", icon: "gamepad" },
  { id: "volume",          label: "Volume Mixer",     icon: "chevronsUp" },
  // Distinct from Suspend, which only blanks the screen: this hands the session to the Windows
  // lock screen, which the pad cannot drive at all.
  { id: "lock",            label: "Lock PC",          icon: "eyeOff" },
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
  radialOpen = true; radialIdx = 0; radialSub = null;
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
  $("radialSelName").textContent = RADIAL_ITEMS[radialIdx].label;
  $("radialTarget").textContent = overlayTargetTitle ? overlayTargetTitle.toUpperCase() : "NO WINDOW";
  $("radialFoot").innerHTML = foot(["A", "Select"], ["B", "Close"]);
}

function radialActivate() {
  const it = RADIAL_ITEMS[radialIdx];
  switch (it.id) {
    case "close":     send({ cmd: "windowAction", action: "close" });    closeRadial(false); break;
    case "keyboard":  send({ cmd: "toggleKeyboard" });                   closeRadial(true);  break;
    case "suspend":
      // Blanks the TV and parks the pad; any button brings it back. Confirmed anyway, because
      // a screen that goes black on a stray flick of the stick reads as a crash.
      confirmState = {
        title: "SUSPEND THIS PC?",
        yesLabel: "Yes, suspend",
        onYes: () => { send({ cmd: "suspend" }); closeRadial(false); },
      };
      renderConfirm();
      $("overlay-confirm").classList.add("active");
      break;
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
    { label: "Resume game", icon: "info", sub: "Back to what you were playing",
      action: () => { hideIngame(); send({ cmd: "resumeGame" }); } },
    { label: "Home", icon: "folder", sub: "Leave it running and open the library",
      action: () => { hideIngame(); setOverlayMode(false); switchView("library"); send({ cmd: "goHome" }); } },
    { label: "Windows menu", icon: "store", sub: "Switch windows, keyboard, suspend",
      action: () => { hideIngame(); send({ cmd: "setRadialActive", active: true }); openRadial(g ? g.title : ""); } },
    { label: "Close game", icon: "trash", danger: true,
      action: () => {
        confirmState = {
          title: "CLOSE THE GAME?",
          yesLabel: "Yes, close it",
          // Drop the overlay and land back on the library. Leaving the transparent overlay
          // window up over a closing game looks like nothing happened at all.
          onYes: () => { hideIngame(); setOverlayMode(false); switchView("library"); send({ cmd: "closeGame" }); },
        };
        renderConfirm();
        $("overlay-confirm").classList.add("active");
      } },
  ];
}

function renderIngame() {
  const g = gameById(S.runningGameId);
  $("ingameTitle").textContent = (g ? g.title : "PLAYING").toUpperCase();
  const items = ingameItems();
  ingameIdx = Math.max(0, Math.min(ingameIdx, items.length - 1));
  renderMenu($("ingameList"), $("ingameFoot"), items, ingameIdx,
    foot(["A", "Select"], ["B", "Resume"]),
    (i) => { if (ingameIdx !== i) { ingameIdx = i; renderIngame(); } },
    (i) => items[i].action());
}

function ingameInput(btn) {
  const items = ingameItems();
  switch (btn) {
    case "Up": ingameIdx = Math.max(0, ingameIdx - 1); renderIngame(); break;
    case "Down": ingameIdx = Math.min(items.length - 1, ingameIdx + 1); renderIngame(); break;
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
