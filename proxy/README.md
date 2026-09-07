# Consolify metadata proxy

Holds the IGDB and SteamGridDB credentials so the launcher does not ship them, which means a user
installs Consolify and gets artwork with no signup, no API key and no settings to fill in.

This is the same arrangement Playnite uses. Its IGDB plugin ships no keys at all — its
`IgdbClient` takes a base URL rather than credentials, and `plugin.cfg` points it at
`https://api2.playnite.link/api/`. The reason is not preference: a desktop binary cannot keep a
secret, and Twitch's own guidance is that the client secret must never be exposed to users.

## What it costs

Cloudflare's free tier covers 100,000 requests a day and 1,000 KV writes a day, and this is
written to sit well inside that:

- **The launcher asks the proxy about very few games.** Steam titles are resolved by app id
  straight from Steam, with no key and no proxy involved. Non-Steam games are first looked up
  against Steam's own keyless search. Only what survives both — Epic and Game Pass exclusives,
  GOG-only classics, ROMs, itch.io games — reaches this service.
- **Answers are cached for 30 days, shared across every user.** Libraries overlap enormously, so
  the hundredth person to own a game costs one KV read and no upstream request.
- **Misses are cached too**, for 3 days, so a game nobody's database has does not re-ask forever.

The practical ceiling is IGDB's, not Cloudflare's: 4 requests/second across the whole credential,
shared by everyone. The cache is what keeps you under it.

## Getting the credentials

IGDB is not signed up for at igdb.com -- API access goes through Twitch, who own it. You need a
Twitch account with 2FA enabled, then an application registered at dev.twitch.tv. SteamGridDB is
its own account and takes about a minute.

Once you have them, check they work before deploying anything:

```powershell
$env:IGDB_CLIENT_ID = "..."
$env:IGDB_CLIENT_SECRET = "..."
$env:SGDB_KEY = "..."
.\verify-credentials.ps1
```

It reads from the environment so the values stay out of your shell history, and prints nothing but
pass/fail and the titles that came back.

## Deploying

You need a Cloudflare account (free), an IGDB client id/secret, and a SteamGridDB key.

Every command below passes `--config proxy/wrangler.toml` and so can be run **from the repository
root**, in any order, in a fresh terminal. Wrangler otherwise looks for its config in the current
directory only, and fails with `Required Worker name missing` when it does not find one.

Sign in to Cloudflare (opens a browser):

```bash
npx wrangler login
```

Create the cache. This prints an `id` — paste it into `proxy/wrangler.toml`, replacing
`PUT_YOUR_KV_NAMESPACE_ID_HERE`. Do this **before** deploying; the placeholder is not a real
namespace and a deploy carrying it will fail:

```bash
npx wrangler kv namespace create METADATA --config proxy/wrangler.toml
```

Set the three credentials. Each prompts for its value, which is encrypted at rest and never
written to the repo:

```bash
npx wrangler secret put IGDB_CLIENT_ID --config proxy/wrangler.toml
```

```bash
npx wrangler secret put IGDB_CLIENT_SECRET --config proxy/wrangler.toml
```

```bash
npx wrangler secret put SGDB_KEY --config proxy/wrangler.toml
```

Check it builds without uploading anything:

```bash
npx wrangler deploy --dry-run --config proxy/wrangler.toml
```

Then deploy, and confirm it is up:

```bash
npx wrangler deploy --config proxy/wrangler.toml
```

```bash
curl "https://consolify-metadata.<your-subdomain>.workers.dev/v1/health"
```

Finally, point the launcher at it by setting `DefaultEndpoint` in
`Consolify/Services/MetadataProxyClient.cs` to that URL and rebuilding. Users can override it in
Settings, but the shipped default is what makes it zero-setup.

## When something goes wrong

**`Required Worker name missing`** — wrangler is running somewhere without a config file. It reads
`wrangler.toml` from the current directory, not from the repository root, so this appears whenever
a command is run from anywhere but `proxy/`. Add `--config proxy/wrangler.toml`, as every command
above does.

**`KV namespace 'PUT_YOUR_KV_NAMESPACE_ID_HERE' is not valid`** — the `kv namespace create` step
has not been done, or its id was not pasted into `proxy/wrangler.toml`.

**The worker deploys but `/v1/facts` returns 502** — the credentials are wrong or missing. Check
them on their own first with `proxy/verify-credentials.ps1`, then confirm all three secrets are
set with `npx wrangler secret list --config proxy/wrangler.toml`.

## Endpoints

    GET /v1/facts?title=<title>   → { name, summary, developer, publisher, genres[],
                                      released, criticScore, cover, artwork }
    GET /v1/art?title=<title>     → { name, portrait, tile, hero, logo }
    GET /v1/health                → { ok: true }

`404` means "no confident answer", which is a normal outcome rather than a failure. The launcher
treats a 404, a 429 and a dead connection identically: it keeps whatever art it already had.

Every response carries the matched `name`, and **the launcher re-checks it against its own strict
title rule before accepting anything**. The proxy being loose, wrong or compromised cannot put the
wrong game's art on a tile.

## Things to know before you run this

- **You become the accountable party.** Under the Twitch Developer Service Agreement the traffic
  through your IGDB credential is yours, whoever generated it. Same for your SteamGridDB key.
- **IGDB's free tier is non-commercial.** That now applies to a service you operate, not just to
  your own copy of the app. If Consolify ever takes money, this needs revisiting first.
- **When this is down, artwork is down for everyone.** That is the trade you accept for zero
  setup — Playnite has the same failure mode, which is what their recurring "IGDB is broken"
  issues actually are. The launcher degrades quietly rather than erroring, and Steam games are
  unaffected because they never touch this.
- **Put a contact address in the worker's User-Agent** if you publish widely, so an upstream that
  is unhappy with your traffic can reach you before it revokes the key.
