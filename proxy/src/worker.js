/*
 * Consolify metadata proxy.
 *
 * Holds the IGDB and SteamGridDB credentials so the launcher does not have to. This is the same
 * shape Playnite uses -- its IGDB plugin ships no keys and talks to api2.playnite.link -- and it
 * exists for the same reason: a desktop binary cannot keep a secret, and Twitch's terms say the
 * client secret must never be exposed to users.
 *
 * Two endpoints, both GET, both cached:
 *
 *   /v1/facts?title=<title>   description, developer, genres, release date, critic score
 *   /v1/art?title=<title>     portrait / tile / hero / logo image URLs
 *
 * Either may answer 404, which means "no confident answer", not "something broke". The launcher
 * treats a 404 and a network failure identically: it keeps whatever art it already had.
 *
 * The cache is the whole economy of this service. IGDB allows 4 requests a second across the
 * entire credential -- not per user -- so an uncached proxy would fall over the moment more than
 * a handful of people scanned at once. Game metadata is effectively static and libraries overlap
 * enormously (everyone owns Hollow Knight), so a shared cache turns thousands of users into a few
 * thousand upstream requests, once, ever.
 */

const CACHE_TTL = 60 * 60 * 24 * 30;   // 30 days. Game facts do not change; art rarely does.
const MISS_TTL = 60 * 60 * 24 * 3;     // Remember "no match" too, but re-check sooner: a game may
                                       // be added to a database after we first ask for it.
const SCHEMA = "v2";                   // bump when a fetcher changes shape or its picking
                                       // rules; it is part of every cache key, so stale answers retire
const RATE_LIMIT = 240;                // requests per IP per window
const RATE_WINDOW = 60;                // seconds

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);

    if (request.method !== "GET") return json({ error: "method not allowed" }, 405);
    if (url.pathname === "/v1/health") return json({ ok: true });

    const title = (url.searchParams.get("title") || "").trim();
    if (!title) return json({ error: "title is required" }, 400);
    if (title.length > 200) return json({ error: "title too long" }, 400);

    const limited = await rateLimited(request, env);
    if (limited) return json({ error: "slow down" }, 429, { "Retry-After": String(RATE_WINDOW) });

    try {
      if (url.pathname === "/v1/facts") return await serve(env, ctx, "facts", title, igdbFacts);
      if (url.pathname === "/v1/art") return await serve(env, ctx, "art", title, gridArt);
      return json({ error: "not found" }, 404);
    } catch (err) {
      // Never leak an upstream error body: it can carry our own credentials back to the caller.
      console.error(`${url.pathname} "${title}": ${err && err.message}`);
      return json({ error: "upstream failed" }, 502);
    }
  },
};

/* ---------------------------------------------------------------- serving */

/**
 * Cache-first. A hit costs one KV read and no upstream call at all, which is what keeps this
 * inside both IGDB's rate limit and a free hosting tier.
 */
async function serve(env, ctx, kind, title, fetcher) {
  const key = `${kind}:${SCHEMA}:${normalise(title)}`;

  const cached = await env.METADATA.get(key, { type: "json" });
  if (cached) {
    return cached.miss
      ? json({ error: "no match" }, 404, { "X-Cache": "HIT" })
      : json(cached.data, 200, { "X-Cache": "HIT" });
  }

  const data = await fetcher(env, title);

  // Written after the response is on its way, so a cache write never delays the caller.
  ctx.waitUntil(env.METADATA.put(
    key,
    JSON.stringify(data ? { data } : { miss: true }),
    { expirationTtl: data ? CACHE_TTL : MISS_TTL },
  ));

  return data
    ? json(data, 200, { "X-Cache": "MISS" })
    : json({ error: "no match" }, 404, { "X-Cache": "MISS" });
}

/*
 * Only for the cache key, and deliberately looser than the launcher's own matching: it just has to
 * make "Hollow Knight" and "hollow  knight" share a cache entry. The launcher re-checks the name
 * that comes back against its own strict rule before it accepts anything, so a sloppy key here
 * cannot put the wrong game's art on a tile.
 */
function normalise(title) {
  return title.toLowerCase().normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .replace(/['\u2018\u2019`]/g, "")
    .replace(/[^a-z0-9]+/g, " ")
    .trim();
}

/* ------------------------------------------------------------------- IGDB */

async function igdbFacts(env, title) {
  const token = await igdbToken(env);
  if (!token) return null;

  const body =
    `search "${title.replace(/"/g, " ")}"; ` +
    "fields name, summary, first_release_date, aggregated_rating, category, " +
    "follows, total_rating_count, version_parent, " +
    "genres.name, cover.image_id, artworks.image_id, " +
    "involved_companies.developer, involved_companies.publisher, involved_companies.company.name; " +
    "limit 20;";

  const res = await fetch("https://api.igdb.com/v4/games", {
    method: "POST",
    headers: {
      "Client-ID": env.IGDB_CLIENT_ID,
      "Authorization": `Bearer ${token}`,
      "Content-Type": "text/plain",
    },
    body,
  });
  if (!res.ok) throw new Error(`igdb ${res.status}`);

  const all = await res.json();
  // category 0 is a main game. The rest are DLC, bundles, ports and episodes, which share their
  // parent's title and would otherwise win the match on a coin toss.
  // version_parent marks an edition or regional variant of another entry; those inherit their
  // parent's title and are never the one wanted.
  const games = all.filter(g =>
    (g.category === undefined || g.category === 0) && g.version_parent === undefined);
  const hit = pick(title, games, g => g.name);
  if (!hit) return null;

  const companies = hit.involved_companies || [];
  const named = (flag) => {
    const c = companies.find(x => x[flag] && x.company && x.company.name);
    return c ? c.company.name : null;
  };

  return {
    name: hit.name,
    summary: hit.summary || null,
    developer: named("developer"),
    publisher: named("publisher"),
    genres: (hit.genres || []).map(g => g.name).filter(Boolean),
    released: hit.first_release_date
      ? new Date(hit.first_release_date * 1000).toISOString().slice(0, 10)
      : null,
    criticScore: typeof hit.aggregated_rating === "number"
      ? Math.round(hit.aggregated_rating) : null,
    cover: hit.cover && hit.cover.image_id ? igdbImage(hit.cover.image_id, "cover_big_2x") : null,
    artwork: hit.artworks && hit.artworks.length && hit.artworks[0].image_id
      ? igdbImage(hit.artworks[0].image_id, "1080p") : null,
  };
}

const igdbImage = (id, size) => `https://images.igdb.com/igdb/image/upload/t_${size}/${id}.jpg`;

/**
 * Twitch client-credentials token, cached in KV against its own stated lifetime. Without the
 * cache every cold worker would mint a fresh token, and Twitch rate limits that too.
 */
async function igdbToken(env) {
  const cached = await env.METADATA.get("igdb:token");
  if (cached) return cached;

  const res = await fetch("https://id.twitch.tv/oauth2/token", {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      client_id: env.IGDB_CLIENT_ID,
      client_secret: env.IGDB_CLIENT_SECRET,
      grant_type: "client_credentials",
    }),
  });
  if (!res.ok) throw new Error(`twitch token ${res.status}`);

  const data = await res.json();
  if (!data.access_token) throw new Error("twitch token missing");

  // A minute early, so a token cannot expire between our check and IGDB's.
  const ttl = Math.max(60, (data.expires_in || 3600) - 60);
  await env.METADATA.put("igdb:token", data.access_token, { expirationTtl: ttl });
  return data.access_token;
}

/* ------------------------------------------------------------ SteamGridDB */

async function gridArt(env, title) {
  const search = await sgdb(env, `/search/autocomplete/${encodeURIComponent(title)}`);
  if (!search || !Array.isArray(search.data)) return null;

  const hit = pick(title, search.data, g => g.name);
  if (!hit || !hit.id) return null;

  // Each shape is independent: a game with no art at a given size answers empty, which is normal.
  const [portrait, tile, hero, logo] = await Promise.all([
    firstUrl(env, `/grids/game/${hit.id}?dimensions=600x900`),
    firstUrl(env, `/grids/game/${hit.id}?dimensions=920x430,460x215`),
    firstUrl(env, `/heroes/game/${hit.id}`),
    firstUrl(env, `/logos/game/${hit.id}`),
  ]);

  if (!portrait && !tile && !hero && !logo) return null;
  return { name: hit.name, portrait, tile, hero, logo };
}

async function firstUrl(env, path) {
  const body = await sgdb(env, path);
  if (!body || !Array.isArray(body.data)) return null;
  const found = body.data.find(x => typeof x.url === "string");
  return found ? found.url : null;
}

async function sgdb(env, path) {
  const res = await fetch(`https://www.steamgriddb.com/api/v2${path}`, {
    headers: { Authorization: `Bearer ${env.SGDB_KEY}` },
  });
  if (res.status === 401) throw new Error("sgdb key rejected");
  if (!res.ok) return null;   // 404 means "nothing of that shape", which is not an error
  return res.json();
}

/* ------------------------------------------------------------- shared bits */

/**
 * Exact-after-normalising, preferring an exact hit. This mirrors the launcher's rule so the proxy
 * does not waste a cache slot on something the client will reject anyway -- but the client checks
 * again regardless, and its check is the one that counts.
 *
 * Ties are the interesting case and are NOT harmless. IGDB carries several entries named exactly
 * "Fortnite" (the game, and the delisted Chinese version published by Tencent) and two named
 * exactly "DOOM" (1993 and 2016). Taking whichever came back first gave Fortnite the wrong
 * developer and a summary about a regional variant. Neither the proxy's title check nor the
 * launcher's can see this -- the names really are identical -- so the tie has to be broken on
 * something else, and popularity picks the canonical entry every time.
 */
function pick(wanted, candidates, nameOf) {
  const want = normalise(wanted);
  if (!want) return null;
  const exact = candidates.filter(c => normalise(nameOf(c) || "") === want);
  if (exact.length === 0) return null;
  return exact.sort(popularityFirst)[0];
}

const weight = (g) => (g.follows || 0) * 10 + (g.total_rating_count || 0);
const popularityFirst = (a, b) => weight(b) - weight(a);

/**
 * A crude per-IP cap. Not a security boundary -- an IP is cheap to change -- just enough that one
 * broken client cannot burn the whole IGDB budget for everyone else.
 */
async function rateLimited(request, env) {
  const ip = request.headers.get("CF-Connecting-IP") || "unknown";
  const bucket = Math.floor(Date.now() / 1000 / RATE_WINDOW);
  const key = `rl:${ip}:${bucket}`;
  const count = parseInt(await env.METADATA.get(key) || "0", 10);
  if (count >= RATE_LIMIT) return true;
  await env.METADATA.put(key, String(count + 1), { expirationTtl: RATE_WINDOW * 2 });
  return false;
}

function json(body, status = 200, headers = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      // Let Cloudflare's own edge cache absorb repeats before they even reach the worker.
      "Cache-Control": status === 200 ? `public, max-age=${CACHE_TTL}` : "public, max-age=3600",
      ...headers,
    },
  });
}
