/*
 * The drawings: the menu icons and the picture of every pad button, for every pad family and the
 * keyboard. In a file of its own because they are drawn in two places -- the launcher page, and
 * the bar of the Nexus browse window, which is a second WebView with this file inlined into it --
 * and one set of drawings is the only way the two stay the same. Everything here is plain
 * functions over constants: nothing reads the page's state except the two family variables at the
 * bottom, which the page assigns (setInputFamily in app.js). Loaded before app.js.
 */

/** Text made safe for markup. Here rather than in app.js because a pill's label goes through it,
 *  and the bar page has no app.js: without it the keyboard glyph was the one that never drew. */
const esc = (s) => String(s ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
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
  /* A tornado, for Vortex: bands narrowing downward, each one nudged sideways so the funnel twists. */
  tornado: '<path d="M3 4.5h18M5 8.5h14.5M7.5 12.5h10M9 16.5h6.5M10.5 20.5h3"/>',
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

