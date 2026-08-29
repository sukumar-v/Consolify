"use strict";

/*
 * Radial power menu and in-game menu.
 *
 * Loaded after app.js and shares its globals ($, send, esc, iconSvg, foot, renderMenu,
 * focusVisible, hoverEnabled, confirmState, renderConfirm, switchView, gameById, S).
 */

let radialOpen = false, radialIdx = 0;
let radialSub = null, radialSubIdx = 0;     // null | "windows" | "shortcuts" | "power"
let ingameOpen = false, ingameIdx = 0;
let overlayTargetTitle = "";
let hostWindows = [];

const RADIAL_ITEMS = [
  { id: "close",     label: "Close window",  icon: "trash", danger: true },
  { id: "minimize",  label: "Minimize",      icon: "chevronsDown" },
  { id: "moveTv",    label: "Move to TV",    icon: "gamepad" },
  { id: "moveNext",  label: "Next display",  icon: "chevronsUp" },
  { id: "windows",   label: "Switch window", icon: "folder" },
  { id: "shortcuts", label: "Shortcuts",     icon: "terminal" },
  { id: "keyboard",  label: "Keyboard",      icon: "file" },
  { id: "power",     label: "Power",         icon: "store", danger: true },
];

const SHORTCUTS = [
  { id: "taskManager",     label: "Task Manager",     icon: "terminal" },
  { id: "explorer",        label: "File Explorer",    icon: "folder" },
  { id: "settings",        label: "Windows Settings", icon: "store" },
  { id: "displaySettings", label: "Display Settings", icon: "gamepad" },
  { id: "volume",          label: "Volume Mixer",     icon: "chevronsUp" },
  { id: "lock",            label: "Lock PC",          icon: "eyeOff" },
];

const POWER_ITEMS = [
  { id: "sleep",    label: "Sleep",     icon: "clock" },
  { id: "signout",  label: "Sign out",  icon: "eyeOff", danger: true },
  { id: "restart",  label: "Restart",   icon: "store",  danger: true },
  { id: "shutdown", label: "Shut down", icon: "trash",  danger: true },
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
    case "minimize":  send({ cmd: "windowAction", action: "minimize" }); closeRadial(false); break;
    case "moveTv":    send({ cmd: "windowAction", action: "moveToTv" }); closeRadial(true);  break;
    case "moveNext":  send({ cmd: "windowAction", action: "moveNext" }); closeRadial(true);  break;
    case "keyboard":  send({ cmd: "toggleKeyboard" });                   closeRadial(true);  break;
    case "windows":   openRadialSub("windows"); break;
    case "shortcuts": openRadialSub("shortcuts"); break;
    case "power":     openRadialSub("power"); break;
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
      action: () => { send({ cmd: "windowAction", action: "focus", handle: w.handle }); closeRadial(false); },
    }));
  }
  if (radialSub === "shortcuts") {
    return SHORTCUTS.map(s => ({
      label: s.label, icon: s.icon,
      action: () => { send({ cmd: "shortcut", id: s.id }); closeRadial(false); },
    }));
  }
  if (radialSub === "power") {
    return POWER_ITEMS.map(p => ({
      label: p.label, icon: p.icon, danger: p.danger,
      action: () => {
        // Sleep is recoverable; the rest end the session, so they get a confirm step.
        if (p.id === "sleep") { send({ cmd: "power", action: p.id }); closeRadial(false); return; }
        confirmState = {
          title: p.label.toUpperCase() + " THIS PC?",
          yesLabel: "Yes, " + p.label.toLowerCase(),
          onYes: () => { send({ cmd: "power", action: p.id }); closeRadial(false); },
        };
        renderConfirm();
        $("overlay-confirm").classList.add("active");
      },
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
  $("radialSubTitle").textContent =
    radialSub === "windows" ? "SWITCH WINDOW" : radialSub === "shortcuts" ? "SHORTCUTS" : "POWER";
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
    { label: "Power menu", icon: "store",
      action: () => { hideIngame(); send({ cmd: "setRadialActive", active: true }); openRadial(g ? g.title : ""); } },
    { label: "Close game", icon: "trash", danger: true,
      action: () => {
        confirmState = {
          title: "CLOSE THE GAME?",
          yesLabel: "Yes, close it",
          onYes: () => { hideIngame(); send({ cmd: "closeGame" }); },
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
