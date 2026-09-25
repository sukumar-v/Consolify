"use strict";
/*
 * Loungepad bridge -- a Vortex extension.
 *
 * Vortex has no way in from outside: it is an Electron app whose state lives in a database only
 * it can open, and the one door is an extension running inside it. This is that extension. It
 * listens on the loopback interface and turns a handful of JSON requests into calls on Vortex's
 * own API -- list the mods of a game, enable or disable some, remove one, deploy, switch game --
 * so that Loungepad can put a mod list on the TV and drive it from a gamepad.
 *
 * Deliberately small. Downloads and installs are NOT here: `Vortex.exe --install <url>` does
 * that from the command line, and a second instance forwards it to the running one, so it works
 * whether or not this extension is loaded.
 *
 * Every request must carry `Authorization: Bearer <token>`, where the token is read from
 * bridge.json beside this file on EVERY request -- Loungepad writes a fresh one each time it
 * starts, and re-reading means a Loungepad restart never leaves Vortex holding a stale one. The
 * Host header must be loopback, which is what keeps a web page in a browser on the same PC from
 * reaching this through DNS rebinding.
 *
 * Copied into %APPDATA%\Vortex\plugins\loungepad-bridge by Loungepad itself (VortexBackend
 * .SyncPlugin), keyed on the version in info.json. Vortex only loads extensions at startup, so a
 * Vortex that was already running when the folder appeared has to be restarted once.
 */

const http = require("http");
const fs = require("fs");
const path = require("path");

const BRIDGE_VERSION = "1.0.6";
const DEFAULT_PORT = 47391;
const MAX_BODY = 64 * 1024;
const SWITCH_TIMEOUT_MS = 45000;   // a profile switch purges and redeploys, which can take a while
const DEPLOY_TIMEOUT_MS = 10 * 60 * 1000;

let vortex = null;
try { vortex = require("vortex-api"); } catch (e) { /* the harness stubs it in */ }

function log(level, message, meta) {
  try {
    // vortex.log already prefixes the line with the extension's name.
    if (vortex && typeof vortex.log === "function") vortex.log(level, message, meta);
    else console.log("[loungepad-bridge]", level, message, meta || "");
  } catch (e) { /* logging must never take the bridge down */ }
}

/* ---------------------------------- config ---------------------------------- */

function configPath() {
  return path.join(__dirname, "bridge.json");
}

/** { port, token } or null when Loungepad has not written one yet. Read on every request. */
function readConfig() {
  try {
    const raw = fs.readFileSync(configPath(), "utf8");
    const cfg = JSON.parse(raw);
    if (!cfg || typeof cfg.token !== "string" || cfg.token.length < 16) return null;
    const port = Number.isInteger(cfg.port) && cfg.port > 0 && cfg.port < 65536 ? cfg.port : DEFAULT_PORT;
    return { port, token: cfg.token };
  } catch (e) {
    return null;
  }
}

/* ---------------------------------- state readers ----------------------------------
 * Raw state paths rather than selectors wherever the path is plain: these are Vortex's persisted
 * shapes and have been stable for years, and reading them directly is what lets the harness
 * exercise this file with a fake store and no selector stubs.
 */

function get(obj, ...keys) {
  let cur = obj;
  for (const k of keys) {
    if (cur === null || cur === undefined) return undefined;
    cur = cur[k];
  }
  return cur;
}

// persistent.profiles, NOT persistent.profile.profiles: the first shape shipped here read the
// latter and saw every game as unmanaged, with the active profile id pointing at nothing.
function profilesOf(state) { return get(state, "persistent", "profiles") || {}; }
function activeProfileId(state) { return get(state, "settings", "profiles", "activeProfileId") || null; }
function activeGameId(state) {
  const p = profilesOf(state)[activeProfileId(state)];
  return p ? p.gameId : null;
}

/** The profile Vortex would switch to for this game: its last active one, else any it has. */
function profileForGame(state, gameId) {
  const profiles = profilesOf(state);
  const last = get(state, "settings", "profiles", "lastActiveProfile", gameId);
  if (last && profiles[last] && profiles[last].gameId === gameId) return profiles[last];
  for (const p of Object.values(profiles)) if (p && p.gameId === gameId) return p;
  return null;
}

function knownGames(state) { return get(state, "session", "gameMode", "known") || []; }
function discoveryOf(state, gameId) { return get(state, "settings", "gameMode", "discovered", gameId) || null; }

function gameEntry(state, g) {
  const disc = discoveryOf(state, g.id);
  return {
    id: g.id,
    name: g.name || g.id,
    path: disc && disc.path ? disc.path : null,
    managed: profileForGame(state, g.id) !== null,
    // Vortex's own rule (nexusGameId): the extension's nexusPageId, else the game id.
    nexusDomain: get(g, "details", "nexusPageId") || g.id,
    hidden: !!(disc && disc.hidden),
  };
}

function modsOf(state, gameId) {
  const profile = profileForGame(state, gameId);
  const table = get(state, "persistent", "mods", gameId) || {};
  const mods = Object.values(table).filter(Boolean).map(m => {
    const a = m.attributes || {};
    const ms = profile ? get(profile, "modState", m.id) : null;
    return {
      id: m.id,
      name: a.customFileName || a.logicalFileName || a.name || m.id,
      version: a.version || null,
      author: a.author || null,
      category: a.category !== undefined && a.category !== null ? String(a.category) : null,
      nexusModId: Number.isFinite(Number(a.modId)) && a.modId !== undefined && a.modId !== null ? Number(a.modId) : null,
      // The Nexus section it was downloaded from, which can differ from the game's own (a Skyrim
      // SE mod served from the original Skyrim's section). It is what the mod's page URL takes.
      nexusDomain: typeof a.downloadGame === "string" && a.downloadGame ? a.downloadGame : null,
      state: m.state || "installed",
      enabled: !!(ms && ms.enabled),
    };
  });
  mods.sort((x, y) => x.name.localeCompare(y.name, undefined, { sensitivity: "base" }));
  return { game: gameId, profileId: profile ? profile.id : null, mods };
}

/** A title folded for comparison: lower case, accents and punctuation gone, spaces squeezed. */
function normTitle(s) {
  return String(s || "").normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase()
    .replace(/&/g, " and ").replace(/[^a-z0-9]+/g, " ").trim();
}

function dialogsOf(state) { return (get(state, "session", "notifications", "dialogs") || []).filter(Boolean); }

function noticesOf(state) {
  return (get(state, "session", "notifications", "notifications") || [])
    .filter(n => n && (n.type === "warning" || n.type === "error"))
    .map(n => ({ id: n.id, type: n.type, title: n.title || null, message: n.message || null }));
}

/** Dialog text as plain words: bbcode and HTML tags dropped, whitespace folded, cut to a size a
 *  screen across the room can carry. */
function plainText(s) {
  if (typeof s !== "string") return "";
  const t = s.replace(/\[\/?[a-z*][^\]]*\]/gi, "").replace(/<[^>]+>/g, " ").replace(/\s+/g, " ").trim();
  return t.length > 600 ? t.slice(0, 597) + "…" : t;
}

/** A dialog as the launcher sees it. `actions` are the button labels, which is also what
 *  closeDialog takes; `answerable` is false for anything that wants more than a button press. */
function describeDialog(d) {
  const c = d.content || {};
  const actions = (Array.isArray(d.actions) ? d.actions : [])
    .map(a => typeof a === "string" ? a : a && typeof a.label === "string" ? a.label : null)
    .filter(Boolean);
  const wantsMore = !!(c.checkboxes && c.checkboxes.length) || !!(c.choices && c.choices.length) || !!c.input || !!(c.links && c.links.length && false);
  return {
    id: d.id,
    type: d.type || null,
    title: d.title || null,
    message: plainText(c.text || c.message || c.bbcode || c.htmlText || ""),
    actions,
    defaultAction: typeof d.defaultAction === "string" ? d.defaultAction : null,
    answerable: actions.length > 0 && !wantsMore,
  };
}

/* ---------------------------------- Vortex operations ---------------------------------- */

function wait(ms) { return new Promise(r => setTimeout(r, ms)); }

async function waitFor(pred, timeoutMs, everyMs = 250) {
  const until = Date.now() + timeoutMs;
  while (Date.now() < until) {
    if (pred()) return true;
    await wait(everyMs);
  }
  return pred();
}

class BridgeError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}

function requireKnownGame(api, gameId) {
  if (typeof gameId !== "string" || !gameId) throw new BridgeError(400, "game is required");
  const state = api.getState();
  if (!knownGames(state).some(g => g.id === gameId)) throw new BridgeError(404, `Vortex does not know a game called '${gameId}'`);
  return state;
}

/** Nine characters from the alphabet shortid uses, which is what Vortex names profiles with. */
function shortId() {
  const chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_-";
  let s = "";
  for (let i = 0; i < 9; i++) s += chars[Math.floor(Math.random() * chars.length)];
  return s;
}

/** A first profile for a located game, exactly as Vortex's Manage button makes one. */
function createDefaultProfile(api, gameId) {
  const profile = { id: shortId(), gameId, name: "Default", modState: {}, lastActivated: undefined };
  api.store.dispatch(vortex.actions.setProfile(profile));
  return profile;
}

/**
 * Ask Vortex to make a profile the active one and wait for it. A switch can stop on a
 * question -- which deployment method, where the staging folder goes -- so a dialog appearing
 * ends the wait too, and the caller reads the dialogs. True only when the switch completed.
 */
async function switchTo(api, profileId) {
  // Only a question raised BY the switch ends the wait: one that was already open -- a stale
  // notification dialog, an installer's -- is not this switch's business.
  const before = new Set(dialogsOf(api.getState()).map(d => d.id));
  const newDialogs = () => dialogsOf(api.getState()).filter(d => !before.has(d.id));
  api.store.dispatch(vortex.actions.setNextProfile(profileId));
  await waitFor(() => activeProfileId(api.getState()) === profileId || newDialogs().length > 0, SWITCH_TIMEOUT_MS);
  return { active: activeProfileId(api.getState()) === profileId, asking: newDialogs().length };
}

/**
 * Make the game's profile the active one. Enabling and deploying only ever act on the active
 * profile, so anything that changes a game has to come through here first. A game with no
 * profile has never been set up; that is /activate's job, on the user's say-so, not a side
 * effect of flipping a switch.
 */
async function ensureActive(api, gameId) {
  const state = requireKnownGame(api, gameId);
  const profile = profileForGame(state, gameId);
  if (!profile) throw new BridgeError(409, "This game has not been set up in Vortex yet");
  if (activeProfileId(state) === profile.id) return profile.id;

  const sw = await switchTo(api, profile.id);
  if (!sw.active) {
    if (sw.asking > 0) throw new BridgeError(409, "Vortex is asking something before it can switch to this game");
    throw new BridgeError(504, "Vortex did not finish switching to the game in time");
  }
  return profile.id;
}

function deploy(api) {
  return new Promise((resolve, reject) => {
    let done = false;
    const timer = setTimeout(() => {
      if (done) return;
      done = true;
      reject(new BridgeError(504, "Vortex did not finish deploying in time"));
    }, DEPLOY_TIMEOUT_MS);
    api.events.emit("deploy-mods", (err) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      if (err) reject(new BridgeError(500, "Deploy failed: " + (err.message || String(err))));
      else resolve();
    });
  });
}

function removeMod(api, gameId, modId) {
  return new Promise((resolve, reject) => {
    api.events.emit("remove-mod", gameId, modId, (err) => {
      if (err) reject(new BridgeError(500, "Remove failed: " + (err.message || String(err))));
      else resolve();
    });
  });
}

const routes = {
  "GET /status": async (api) => {
    const state = api.getState();
    return {
      ok: true,
      bridge: BRIDGE_VERSION,
      vortex: get(state, "app", "appVersion") || get(state, "app", "version") || null,
      activeGameId: activeGameId(state),
      activeProfileId: activeProfileId(state),
    };
  },

  "GET /games": async (api) => {
    const state = api.getState();
    return { games: knownGames(state).map(g => gameEntry(state, g)) };
  },

  "GET /mods": async (api, query) => {
    const state = requireKnownGame(api, query.game);
    return modsOf(state, query.game);
  },

  "POST /enable": async (api, query, body) => {
    const gameId = body.game;
    const modIds = Array.isArray(body.modIds) ? body.modIds.filter(x => typeof x === "string") : [];
    if (!modIds.length) throw new BridgeError(400, "modIds is required");
    const enabled = body.enabled !== false;
    const profileId = await ensureActive(api, gameId);
    const known = get(api.getState(), "persistent", "mods", gameId) || {};
    const missing = modIds.filter(id => !known[id]);
    if (missing.length) throw new BridgeError(404, `No such mod: ${missing.join(", ")}`);
    // Not an action creator, despite living under `actions`: setModsEnabled takes the api,
    // dispatches SET_MOD_ENABLED per mod itself, raises "mods-enabled" and returns a promise.
    // Called with (profileId, …) it fails with "api.getState is not a function".
    await vortex.actions.setModsEnabled(api, profileId, modIds, enabled);
    if (body.deploy !== false) await deploy(api);
    return modsOf(api.getState(), gameId);
  },

  "POST /remove": async (api, query, body) => {
    const gameId = body.game;
    if (typeof body.modId !== "string" || !body.modId) throw new BridgeError(400, "modId is required");
    await ensureActive(api, gameId);
    const known = get(api.getState(), "persistent", "mods", gameId) || {};
    if (!known[body.modId]) throw new BridgeError(404, `No such mod: ${body.modId}`);
    await removeMod(api, gameId, body.modId);
    return modsOf(api.getState(), gameId);
  },

  "POST /deploy": async (api, query, body) => {
    await ensureActive(api, body.game);
    await deploy(api);
    return { ok: true };
  },

  // Switch Vortex to the game, setting it up first if it never has been. Setting up is what
  // Vortex's own Manage button does (manageGameDiscovered): a "Default" profile, then the
  // switch to it, and the switch is what creates the staging folder and asks about the
  // deployment method. NOT the 'activate-game' event: on a game with no profile that opens a
  // "Choose profile" dialog with nothing in it, whose Activate button does nothing at all.
  // The reply says whether Vortex got there, and how many questions it stopped on if not.
  "POST /activate": async (api, query, body) => {
    const state = requireKnownGame(api, body.game);
    const disc = discoveryOf(state, body.game);
    if (!disc || !disc.path) throw new BridgeError(409, "Vortex has not located this game yet");
    let profile = profileForGame(state, body.game);
    let created = false;
    if (!profile) {
      profile = createDefaultProfile(api, body.game);
      created = true;
    }
    if (activeProfileId(api.getState()) === profile.id) return { ok: true, active: true, managed: true, created, asking: 0 };
    const sw = await switchTo(api, profile.id);
    return { ok: true, active: sw.active, managed: true, created, asking: sw.asking };
  },

  // Game extensions Vortex could install, from the catalogue it fetches itself, for a game it
  // has no extension for. Matched loosely on the game's name here; the launcher ranks exact
  // matches first and the user picks, so a near miss costs a glance, not a wrong install.
  "GET /extensions": async (api, query) => {
    const all = (get(api.getState(), "session", "extensions", "available") || []).filter(e => e && e.type === "game");
    const q = normTitle(query.query || "");
    const hits = q ? all.filter(e => {
      const g = normTitle(e.gameName || ""), n = normTitle(e.name || "");
      return (g && (g === q || g.includes(q) || q.includes(g))) || (n && n.includes(q));
    }) : [];
    return {
      total: all.length,
      extensions: hits.slice(0, 8).map(e => ({
        modId: e.modId, fileId: e.fileId, name: e.name, gameName: e.gameName || null,
        gameDomain: e.gameDomain || null, author: e.author || null, version: e.version || null,
        exact: normTitle(e.gameName || "") === q,
      })),
    };
  },

  // Tell Vortex where a game is, the way its own "manually set location" does but with the
  // folder the launcher already knows instead of a folder dialog. Vortex's own check first:
  // every file the extension requires has to be in that folder, or it is not the game.
  "POST /discover": async (api, query, body) => {
    const state = requireKnownGame(api, body.game);
    const game = knownGames(state).find(g => g.id === body.game);
    const dir = typeof body.path === "string" ? body.path.trim() : "";
    if (!dir) throw new BridgeError(400, "path is required");
    let stat = null;
    try { stat = fs.statSync(dir); } catch (e) { /* not there */ }
    if (!stat || !stat.isDirectory()) throw new BridgeError(400, "That folder does not exist");
    const required = Array.isArray(game.requiredFiles) ? game.requiredFiles : [];
    const missing = required.filter(f => !fs.existsSync(path.join(dir, f)));
    if (missing.length) throw new BridgeError(409, `That folder does not hold ${game.name || game.id}: ${missing[0]} is not in it`);
    const store = typeof body.store === "string" && body.store ? body.store : undefined;
    const disc = discoveryOf(state, game.id);
    // Same two branches as Vortex: a re-pointed game keeps its settings, a new one gets a record.
    if (disc && disc.path) api.store.dispatch(vortex.actions.setGamePath(game.id, dir, store, undefined));
    else api.store.dispatch(vortex.actions.addDiscoveredGame(game.id, {
      path: dir, tools: {}, hidden: false, environment: game.environment, executable: undefined, pathSetManually: true, store,
    }));
    await waitFor(() => { const d = discoveryOf(api.getState(), game.id); return !!(d && d.path); }, 5000);
    return { ok: true, game: gameEntry(api.getState(), game) };
  },

  // Vortex's own extension browser, opened on one extension. Installing is a click there: the
  // install itself is not on Vortex's API, only the page that does it.
  "POST /extension/show": async (api, query, body) => {
    const modId = Number(body.modId);
    if (!Number.isInteger(modId) || modId <= 0) throw new BridgeError(400, "modId is required");
    api.events.emit("show-extension-page", modId);
    return { ok: true };
  },

  // Diagnostics: the keys and value types at one point of Vortex's state tree, never the values.
  // This is how a state path that has moved between Vortex versions gets found in a minute
  // rather than by guessing (persistent.profiles was one).
  "GET /state-keys": async (api, query) => {
    const path = typeof query.path === "string" && query.path ? query.path.split(".") : [];
    const node = get(api.getState(), ...path);
    if (node === null || node === undefined) return { path: path.join("."), type: node === null ? "null" : "undefined" };
    if (typeof node !== "object") return { path: path.join("."), type: typeof node };
    const keys = {};
    for (const k of Object.keys(node).slice(0, 200)) {
      const v = node[k];
      keys[k] = v === null ? "null" : Array.isArray(v) ? `array(${v.length})` : typeof v;
    }
    return { path: path.join("."), type: Array.isArray(node) ? `array(${node.length})` : "object", keys };
  },

  // What Vortex is waiting on somebody to answer. A dialog blocks it outright -- the fallback
  // installer's "install this anyway?" is the one every unusual archive raises -- and its
  // buttons are reported so the launcher can press one. Warnings and errors are the
  // notifications worth relaying to a screen across the room.
  "GET /attention": async (api) => {
    const state = api.getState();
    return { dialogs: dialogsOf(state).map(describeDialog), notifications: noticesOf(state) };
  },

  // Press one of a dialog's buttons, by its label as /attention reported it. Only for a dialog
  // that is plain buttons: one with checkboxes, choices or a text field needs Vortex's window.
  "POST /answer": async (api, query, body) => {
    if (typeof body.id !== "string" || !body.id) throw new BridgeError(400, "id is required");
    const dialog = dialogsOf(api.getState()).find(d => d.id === body.id);
    if (!dialog) throw new BridgeError(404, "Vortex is no longer asking that");
    const described = describeDialog(dialog);
    if (!described.answerable) throw new BridgeError(409, "That dialog needs Vortex's own window");
    if (!described.actions.includes(body.action)) throw new BridgeError(400, `No such button: ${body.action}`);
    if (typeof api.closeDialog !== "function") throw new BridgeError(501, "This Vortex cannot answer dialogs from outside");
    api.closeDialog(body.id, body.action);
    const gone = await waitFor(() => !dialogsOf(api.getState()).some(d => d.id === body.id), 5000);
    return { ok: true, closed: gone };
  },
};

/* ---------------------------------- HTTP plumbing ---------------------------------- */

function send(res, status, payload) {
  const text = JSON.stringify(payload);
  res.writeHead(status, {
    "Content-Type": "application/json; charset=utf-8",
    "Content-Length": Buffer.byteLength(text),
    "Cache-Control": "no-store",
  });
  res.end(text);
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    req.on("data", c => {
      size += c.length;
      if (size > MAX_BODY) { reject(new BridgeError(413, "Body too large")); req.destroy(); return; }
      chunks.push(c);
    });
    req.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
    req.on("error", reject);
  });
}

function isLoopbackHost(host) {
  if (!host) return false;
  const h = host.replace(/:\d+$/, "").replace(/^\[|\]$/g, "").toLowerCase();
  return h === "127.0.0.1" || h === "localhost" || h === "::1";
}

function constantTimeEqual(a, b) {
  if (typeof a !== "string" || typeof b !== "string" || a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

async function handle(api, req, res) {
  if (!isLoopbackHost(req.headers.host)) return send(res, 403, { error: "Loopback only" });
  const cfg = readConfig();
  if (!cfg) return send(res, 503, { error: "The bridge has no configuration; start Loungepad once" });
  const auth = req.headers.authorization || "";
  const token = auth.startsWith("Bearer ") ? auth.slice(7).trim() : "";
  if (!constantTimeEqual(token, cfg.token)) return send(res, 401, { error: "Bad token" });

  const url = new URL(req.url, "http://127.0.0.1");
  const route = routes[`${req.method} ${url.pathname}`];
  if (!route) return send(res, 404, { error: "No such route" });

  let body = {};
  if (req.method === "POST") {
    const text = await readBody(req);
    if (text.trim()) {
      try { body = JSON.parse(text); }
      catch (e) { throw new BridgeError(400, "Body is not JSON"); }
      if (!body || typeof body !== "object" || Array.isArray(body)) throw new BridgeError(400, "Body must be an object");
    }
  }
  const query = Object.fromEntries(url.searchParams.entries());
  const result = await route(api, query, body);
  send(res, 200, result);
}

function startServer(api) {
  const cfg = readConfig();
  const port = cfg ? cfg.port : DEFAULT_PORT;
  const server = http.createServer((req, res) => {
    handle(api, req, res).catch(err => {
      const status = err instanceof BridgeError ? err.status : 500;
      if (status >= 500) log("warn", "request failed", { url: req.url, error: err.message || String(err) });
      try { send(res, status, { error: err.message || String(err) }); } catch (e) { /* socket gone */ }
    });
  });
  server.on("error", err => log("warn", "could not listen", { port, error: err.message }));
  server.listen(port, "127.0.0.1", () => log("info", "listening", { port }));
  return server;
}

/** Vortex's entry point. `once` runs after every extension has loaded, which is when the API is safe to use. */
function main(context) {
  context.once(() => {
    try { startServer(context.api); }
    catch (err) { log("error", "failed to start", { error: err.message || String(err) }); }
  });
  return true;
}

module.exports = main;
module.exports.default = main;
// For the harness only; Vortex never touches these.
module.exports._internal = { routes, startServer, BRIDGE_VERSION, DEFAULT_PORT };
