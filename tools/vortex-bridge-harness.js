"use strict";
/*
 * Exercises Consolify/vortex-bridge/index.js without Vortex.
 *
 * Stubs the `vortex-api` module and hands the extension a fake api -- a state tree in the shape
 * Vortex persists, a store whose dispatch applies the one action the bridge uses, and an event
 * bus whose handlers do what Vortex's do (switch profile, deploy, remove) -- then drives the HTTP
 * surface and checks the answers. The extension is copied to a scratch folder first so that the
 * bridge.json it reads on every request can be written and rewritten beside it.
 *
 *   node tools\vortex-bridge-harness.js
 *
 * Exit code is the number of failures.
 */

const fs = require("fs");
const os = require("os");
const path = require("path");
const http = require("http");
const Module = require("module");
const { EventEmitter } = require("events");

const PORT = 47399;   // not the real one, so a running Vortex with the bridge is never hit
const TOKEN_A = "a".repeat(32);
const TOKEN_B = "b".repeat(32);

/* ---------------------------------- fake Vortex ---------------------------------- */

function makeState() {
  return {
    app: { appVersion: "1.13.7" },
    session: {
      extensions: {
        available: [
          { name: "Hollow Knight Support", modId: 501, fileId: 9001, author: "someone", version: "1.2.0", type: "game", gameId: 2000, gameDomain: "hollowknight", gameName: "Hollow Knight" },
          { name: "Hollow Knight: Silksong Support", modId: 502, fileId: 9002, author: "someone", version: "0.1.0", type: "game", gameId: 2001, gameDomain: "hollowknightsilksong", gameName: "Hollow Knight: Silksong" },
          { name: "Dark Theme", modId: 77, fileId: 78, author: "x", version: "1.0", type: "theme" },
        ],
      },
      gameMode: {
        known: [
          { id: "skyrimse", name: "Skyrim Special Edition", details: { nexusPageId: "skyrimspecialedition" } },
          { id: "fallout4", name: "Fallout 4", details: { nexusPageId: "fallout4" } },
          { id: "cyberpunk2077", name: "Cyberpunk 2077", details: { nexusPageId: "cyberpunk2077" } },
          { id: "undiscovered", name: "Nowhere To Be Found" },
          { id: "hollowknight", name: "Hollow Knight", requiredFiles: ["hollow_knight.exe"], environment: { SteamAPPId: "367520" } },
        ],
      },
      notifications: {
        notifications: [
          { id: "n1", type: "info", message: "Deployment complete" },
          { id: "n2", type: "warning", title: "Unsolved conflicts", message: "2 mods conflict" },
        ],
        dialogs: [
          { id: "d1", type: "question", title: "You Have Reached The Fallback Installer!",
            content: { bbcode: "The archive [b]Consolify Test Mod[/b] does not match a known layout.<br/>Install it anyway?" },
            actions: ["Cancel", "Continue"], defaultAction: "Continue" },
          { id: "d2", type: "question", title: "Pick a folder", content: { input: [{ id: "path", label: "Folder" }] }, actions: ["Cancel", "OK"] },
        ],
      },
    },
    settings: {
      gameMode: {
        discovered: {
          skyrimse: { path: "D:\\Steam\\steamapps\\common\\Skyrim Special Edition" },
          fallout4: { path: "D:\\Steam\\steamapps\\common\\Fallout 4" },
          cyberpunk2077: { path: "D:\\GOG Games\\Cyberpunk 2077", hidden: true },
        },
      },
      profiles: { activeProfileId: "p-sk", lastActiveProfile: { skyrimse: "p-sk", fallout4: "p-fo" } },
    },
    persistent: {
      // persistent.profiles: the shape a real Vortex 1.15 answered with (see the bridge's comment).
      profiles: {
        "p-sk": { id: "p-sk", gameId: "skyrimse", name: "Default", modState: { modA: { enabled: true }, modB: { enabled: true }, modC: { enabled: false } } },
        "p-fo": { id: "p-fo", gameId: "fallout4", name: "Default", modState: { fo1: { enabled: false } } },
      },
      mods: {
        skyrimse: {
          modA: { id: "modA", state: "installed", attributes: { name: "SkyUI", version: "5.2SE", author: "SkyUI Team", modId: 12604, category: 42 } },
          modB: { id: "modB", state: "installed", attributes: { logicalFileName: "Unofficial Patch", version: "4.3.3", author: "Arthmoor", modId: "266" } },
          modC: { id: "modC", state: "downloaded", attributes: { customFileName: "A Quality World Map" } },
        },
        fallout4: {
          fo1: { id: "fo1", state: "installed", attributes: { name: "Full Dialogue Interface", version: "1.2", modId: 1235 } },
        },
      },
    },
  };
}

function makeApi(state, calls) {
  const events = new EventEmitter();
  const api = {
    getState: () => state,
    store: {
      dispatch: (action) => {
        calls.push(["dispatch", action]);
        if (action.type === "SET_MODS_ENABLED") {
          const profile = state.persistent.profiles[action.profileId];
          for (const id of action.modIds) profile.modState[id] = { enabled: action.enabled, enabledTime: Date.now() };
        }
        if (action.type === "SET_PROFILE") state.persistent.profiles[action.profile.id] = { ...action.profile };
        // Vortex switches on the next tick, the way the real profile manager reacts to nextProfileId.
        if (action.type === "SET_NEXT_PROFILE") { state.settings.profiles.nextProfileId = action.profileId; setTimeout(() => { state.settings.profiles.activeProfileId = action.profileId; }, 30); }
        if (action.type === "ADD_DISCOVERED_GAME") state.settings.gameMode.discovered[action.id] = { ...action.result };
        if (action.type === "SET_GAME_PATH") Object.assign(state.settings.gameMode.discovered[action.gameId], { path: action.gamePath, store: action.store });
      },
    },
    events,
    closeDialog: (id, action) => {
      calls.push(["closeDialog", id, action]);
      const list = state.session.notifications.dialogs;
      const i = list.findIndex(d => d.id === id);
      if (i >= 0) list.splice(i, 1);
    },
  };
  events.on("show-extension-page", (modId) => calls.push(["show-extension-page", modId]));
  // The bridge no longer raises activate-game (its "Choose profile" dialog is empty for a game
  // with no profile); a call here is a regression.
  events.on("activate-game", (gameId) => calls.push(["activate-game", gameId]));
  events.on("deploy-mods", (cb) => {
    calls.push(["deploy-mods"]);
    setTimeout(() => cb(null), 10);
  });
  events.on("remove-mod", (gameId, modId, cb) => {
    calls.push(["remove-mod", gameId, modId]);
    delete state.persistent.mods[gameId][modId];
    setTimeout(() => cb(null), 10);
  });
  return api;
}

/* ---------------------------------- loading the extension ---------------------------------- */

const fakeVortexApi = {
  log: () => {},
  actions: {
    // The real one is a helper, not an action creator: (api, profileId, modIds, enabled) that
    // dispatches per mod and returns a promise. Modelled the same way so a call with the old
    // argument order fails here the way it failed against Vortex 2.7.
    setModsEnabled: (api, profileId, modIds, enabled) => {
      if (!api || typeof api.getState !== "function") throw new TypeError("api.getState is not a function");
      api.store.dispatch({ type: "SET_MODS_ENABLED", profileId, modIds, enabled });
      return Promise.resolve();
    },
    addDiscoveredGame: (id, result) => ({ type: "ADD_DISCOVERED_GAME", id, result }),
    setGamePath: (gameId, gamePath, store, exePath) => ({ type: "SET_GAME_PATH", gameId, gamePath, store, exePath }),
    setProfile: (profile) => ({ type: "SET_PROFILE", profile }),
    setNextProfile: (profileId) => ({ type: "SET_NEXT_PROFILE", profileId }),
  },
  selectors: {},
  util: {},
};
const realLoad = Module._load;
Module._load = function (request, parent, isMain) {
  if (request === "vortex-api") return fakeVortexApi;
  return realLoad.apply(this, arguments);
};

const srcDir = path.join(__dirname, "..", "Consolify", "vortex-bridge");
const scratch = fs.mkdtempSync(path.join(os.tmpdir(), "consolify-bridge-"));
for (const f of fs.readdirSync(srcDir)) fs.copyFileSync(path.join(srcDir, f), path.join(scratch, f));
const writeConfig = (token, port = PORT) => fs.writeFileSync(path.join(scratch, "bridge.json"), JSON.stringify({ port, token }));
writeConfig(TOKEN_A);

const extension = require(path.join(scratch, "index.js"));

/* ---------------------------------- requests ---------------------------------- */

function request(method, urlPath, { token = TOKEN_A, body, host } = {}) {
  return new Promise((resolve, reject) => {
    const data = body === undefined ? null : (typeof body === "string" ? body : JSON.stringify(body));
    const headers = { Accept: "application/json" };
    if (token) headers.Authorization = "Bearer " + token;
    if (data !== null) { headers["Content-Type"] = "application/json"; headers["Content-Length"] = Buffer.byteLength(data); }
    if (host) headers.Host = host;
    const req = http.request({ host: "127.0.0.1", port: PORT, method, path: urlPath, headers }, res => {
      const chunks = [];
      res.on("data", c => chunks.push(c));
      res.on("end", () => {
        const text = Buffer.concat(chunks).toString("utf8");
        let json = null;
        try { json = JSON.parse(text); } catch (e) { /* not JSON */ }
        resolve({ status: res.statusCode, json, text });
      });
    });
    req.on("error", reject);
    if (data !== null) req.write(data);
    req.end();
  });
}

/* ---------------------------------- the checks ---------------------------------- */

let failures = 0;
function check(name, cond, detail) {
  if (cond) console.log("  ok   " + name);
  else { failures++; console.log("  FAIL " + name + (detail !== undefined ? "  -> " + JSON.stringify(detail) : "")); }
}

async function run() {
  const state = makeState();
  const calls = [];
  const api = makeApi(state, calls);
  let started = false;
  extension({ api, once: (fn) => { fn(); started = true; } });
  check("extension starts through context.once", started);
  await new Promise(r => setTimeout(r, 150));

  console.log("auth");
  let r = await request("GET", "/status", { token: null });
  check("no token is 401", r.status === 401, r);
  r = await request("GET", "/status", { token: "x".repeat(32) });
  check("wrong token is 401", r.status === 401, r);
  r = await request("GET", "/status", { host: "evil.example:47399" });
  check("non-loopback Host is 403", r.status === 403, r);
  r = await request("GET", "/nothing");
  check("unknown route is 404", r.status === 404, r);

  console.log("status and games");
  r = await request("GET", "/status");
  check("status ok", r.status === 200 && r.json.ok === true && r.json.vortex === "1.13.7", r);
  check("status names the active game", r.json.activeGameId === "skyrimse" && r.json.activeProfileId === "p-sk", r.json);
  r = await request("GET", "/games");
  check("games: every known game, located or not", r.status === 200 && r.json.games.length === 5, r.json);
  const sk = r.json.games.find(g => g.id === "skyrimse");
  const cp = r.json.games.find(g => g.id === "cyberpunk2077");
  const und = r.json.games.find(g => g.id === "undiscovered");
  check("skyrim is managed with its path and Nexus section", sk && sk.managed && sk.path.endsWith("Skyrim Special Edition") && sk.nexusDomain === "skyrimspecialedition", sk);
  check("cyberpunk is discovered, hidden and unmanaged", cp && !cp.managed && cp.hidden && cp.path.startsWith("D:\\GOG"), cp);
  check("an undiscovered game has no path", und && und.path === null && !und.managed, und);

  console.log("mods");
  r = await request("GET", "/mods?game=skyrimse");
  check("three mods, sorted by name", r.status === 200 && r.json.mods.map(m => m.name).join("|") === "A Quality World Map|SkyUI|Unofficial Patch", r.json);
  const byId = Object.fromEntries(r.json.mods.map(m => [m.id, m]));
  check("enabled flags come from the profile", byId.modA.enabled && byId.modB.enabled && !byId.modC.enabled, byId);
  check("nexus id is a number from a number or a string", byId.modA.nexusModId === 12604 && byId.modB.nexusModId === 266 && byId.modC.nexusModId === null, byId);
  check("state and version carried", byId.modC.state === "downloaded" && byId.modA.version === "5.2SE" && byId.modA.category === "42", byId);
  r = await request("GET", "/mods?game=nope");
  check("unknown game is 404", r.status === 404, r);
  r = await request("GET", "/mods");
  check("missing game is 400", r.status === 400, r);

  console.log("enable and disable");
  calls.length = 0;
  r = await request("POST", "/enable", { body: { game: "skyrimse", modIds: ["modB"], enabled: false } });
  check("disable answers with the list", r.status === 200 && r.json.mods.find(m => m.id === "modB").enabled === false, r.json);
  check("disable dispatched once and deployed once, without switching", calls.map(c => c[0]).join(",") === "dispatch,deploy-mods", calls);
  check("the store saw the right action", calls[0][1].type === "SET_MODS_ENABLED" && calls[0][1].profileId === "p-sk" && calls[0][1].enabled === false, calls[0]);
  r = await request("POST", "/enable", { body: { game: "skyrimse", modIds: ["ghost"], enabled: true } });
  check("enabling an unknown mod is 404", r.status === 404, r);
  r = await request("POST", "/enable", { body: { game: "skyrimse", modIds: [], enabled: true } });
  check("no modIds is 400", r.status === 400, r);
  r = await request("POST", "/enable", { body: "{not json" });
  check("bad JSON is 400", r.status === 400, r);
  r = await request("POST", "/enable", { body: { game: "cyberpunk2077", modIds: ["x"], enabled: true } });
  check("an unmanaged game is 409", r.status === 409, r);

  console.log("switching game");
  calls.length = 0;
  r = await request("POST", "/enable", { body: { game: "fallout4", modIds: ["fo1"], enabled: true } });
  check("enable on another game switches first, through setNextProfile", r.status === 200 && calls[0][1].type === "SET_NEXT_PROFILE" && calls[0][1].profileId === "p-fo", calls);
  check("and Vortex is now on that game", state.settings.profiles.activeProfileId === "p-fo" && r.json.mods[0].enabled === true, r.json);
  r = await request("POST", "/activate", { body: { game: "skyrimse" } });
  check("activate switches back", r.status === 200 && r.json.active === true && r.json.managed === true && r.json.created === false && state.settings.profiles.activeProfileId === "p-sk", r.json);
  calls.length = 0;
  r = await request("POST", "/activate", { body: { game: "cyberpunk2077" } });
  const madeProfile = calls.find(c => c[0] === "dispatch" && c[1].type === "SET_PROFILE");
  check("activate on a located game with no profile makes a Default one and switches to it", r.status === 200 && r.json.managed === true && r.json.created === true && r.json.active === true && madeProfile && madeProfile[1].profile.gameId === "cyberpunk2077" && madeProfile[1].profile.name === "Default", r.json);
  check("the profile is Vortex-shaped, nine characters, and now the active one", /^[0-9A-Za-z_-]{9}$/.test(madeProfile[1].profile.id) && state.settings.profiles.activeProfileId === madeProfile[1].profile.id, madeProfile[1].profile);
  check("activate-game was never raised", !calls.some(c => c[0] === "activate-game"), calls);
  r = await request("GET", "/games");
  check("cyberpunk now lists as managed", r.json.games.find(g => g.id === "cyberpunk2077").managed === true, r.json);
  r = await request("POST", "/activate", { body: { game: "undiscovered" } });
  check("activate on a game Vortex has not located is 409", r.status === 409, r);
  r = await request("POST", "/activate", { body: { game: "skyrimse" } });
  check("back to skyrim for the rest", r.json.active === true, r.json);

  console.log("remove and deploy");
  calls.length = 0;
  r = await request("POST", "/remove", { body: { game: "skyrimse", modId: "modC" } });
  check("remove answers with two mods left", r.status === 200 && r.json.mods.length === 2 && !r.json.mods.some(m => m.id === "modC"), r.json);
  check("remove went through the event", calls.some(c => c[0] === "remove-mod" && c[2] === "modC"), calls);
  r = await request("POST", "/remove", { body: { game: "skyrimse", modId: "modC" } });
  check("removing it again is 404", r.status === 404, r);
  calls.length = 0;
  r = await request("POST", "/deploy", { body: { game: "skyrimse" } });
  check("deploy ok", r.status === 200 && r.json.ok === true && calls.some(c => c[0] === "deploy-mods"), r);

  console.log("attention and answering");
  r = await request("GET", "/attention");
  check("two dialogs and one warning; the info notification is left out", r.status === 200 && r.json.dialogs.length === 2 && r.json.notifications.length === 1 && r.json.notifications[0].title === "Unsolved conflicts", r.json);
  const d1 = r.json.dialogs[0], d2 = r.json.dialogs[1];
  check("a button dialog carries its buttons and plain text", d1.answerable && d1.actions.join("|") === "Cancel|Continue" && d1.defaultAction === "Continue" && d1.message === "The archive Consolify Test Mod does not match a known layout. Install it anyway?", d1);
  check("a dialog with a text field is not answerable from here", d2.answerable === false && d2.actions.length === 2, d2);
  calls.length = 0;
  r = await request("POST", "/answer", { body: { id: "d1", action: "Maybe" } });
  check("a button the dialog does not have is 400", r.status === 400, r);
  r = await request("POST", "/answer", { body: { id: "d2", action: "OK" } });
  check("answering the unanswerable is 409", r.status === 409, r);
  r = await request("POST", "/answer", { body: { id: "d1", action: "Continue" } });
  check("answering presses the button and the dialog goes", r.status === 200 && r.json.closed === true && calls.some(c => c[0] === "closeDialog" && c[2] === "Continue"), r.json);
  r = await request("POST", "/answer", { body: { id: "d1", action: "Continue" } });
  check("answering it again is 404", r.status === 404, r);
  r = await request("GET", "/attention");
  check("one dialog left", r.json.dialogs.length === 1 && r.json.dialogs[0].id === "d2", r.json);

  console.log("game extensions");
  r = await request("GET", "/extensions?query=Hollow%20Knight");
  check("both Hollow Knight extensions match, the exact one flagged, the theme left out", r.status === 200 && r.json.total === 2 && r.json.extensions.length === 2 && r.json.extensions.find(e => e.modId === 501).exact === true && r.json.extensions.find(e => e.modId === 502).exact === false, r.json);
  r = await request("GET", "/extensions?query=Hollow%20Knight%3A%20Silksong");
  check("the longer title matches its own extension exactly", r.json.extensions.some(e => e.modId === 502 && e.exact), r.json);
  r = await request("GET", "/extensions?query=Celeste");
  check("no extension for Celeste", r.status === 200 && r.json.extensions.length === 0, r.json);
  calls.length = 0;
  r = await request("POST", "/extension/show", { body: { modId: 501 } });
  check("show-extension-page is raised with the mod id", r.status === 200 && calls.some(c => c[0] === "show-extension-page" && c[1] === 501), calls);
  r = await request("POST", "/extension/show", { body: { modId: "abc" } });
  check("a bad mod id is 400", r.status === 400, r);

  console.log("locating a game");
  const gameDir = fs.mkdtempSync(path.join(os.tmpdir(), "hk-"));
  r = await request("POST", "/discover", { body: { game: "hollowknight", path: path.join(gameDir, "nope") } });
  check("a folder that does not exist is 400", r.status === 400, r);
  r = await request("POST", "/discover", { body: { game: "hollowknight", path: gameDir } });
  check("a folder without the game's files is 409 and names the file", r.status === 409 && /hollow_knight\.exe/.test(r.json.error), r);
  fs.writeFileSync(path.join(gameDir, "hollow_knight.exe"), "");
  calls.length = 0;
  r = await request("POST", "/discover", { body: { game: "hollowknight", path: gameDir, store: "steam" } });
  check("a folder with them is recorded as a manual discovery", r.status === 200 && r.json.game.path === gameDir && calls[0][1].type === "ADD_DISCOVERED_GAME" && calls[0][1].result.pathSetManually === true && calls[0][1].result.store === "steam", r.json);
  r = await request("GET", "/games");
  check("and the game now lists with its path, still unmanaged", r.json.games.some(g => g.id === "hollowknight" && g.path === gameDir && !g.managed), r.json);
  calls.length = 0;
  r = await request("POST", "/discover", { body: { game: "hollowknight", path: gameDir } });
  check("pointing an already located game goes through setGamePath", r.status === 200 && calls[0][1].type === "SET_GAME_PATH", calls);

  console.log("token rotation");
  writeConfig(TOKEN_B);
  r = await request("GET", "/status", { token: TOKEN_A });
  check("the old token stops working at once", r.status === 401, r);
  r = await request("GET", "/status", { token: TOKEN_B });
  check("the new one works without a restart", r.status === 200, r);
  fs.unlinkSync(path.join(scratch, "bridge.json"));
  r = await request("GET", "/status", { token: TOKEN_B });
  check("no config at all refuses everything", r.status === 503, r);
  writeConfig(TOKEN_B);

  console.log(failures === 0 ? "\nall checks passed" : `\n${failures} check(s) failed`);
  process.exit(failures);
}

run().catch(err => { console.error(err); process.exit(99); });
