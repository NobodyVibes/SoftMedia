# Metadata Sources

SoftMedia enriches your library from free, public metadata providers. This page explains
which provider serves which library type, the official usage limits SoftMedia respects,
and what the server does when a provider is unavailable or an item can't be matched.

## Providers and their limits

| Provider | Used for | Official limit SoftMedia enforces |
|---|---|---|
| Wikidata (query.wikidata.org) | Movies, Games, Comics | ≤5 in-flight per 10 s (WDQS allows 5 parallel); 30 s request timeout |
| OMDb (omdbapi.com) | Movies (primary or fallback) | 1,000 requests/day on the free tier — counted for BOTH the bundled shared key and custom keys |
| TVMaze (api.tvmaze.com) | TV series, seasons, episodes, cast | 18 calls per 10 s (official cap 20; margin kept) — one budget slot per HTTP request |
| MusicBrainz (musicbrainz.org) | Artists, albums | Strict 1 request/second (MusicBrainz bans violators by User-Agent) |
| Cover Art Archive (coverartarchive.org) | Album covers | 1 probe/second; a 503 is treated as "unknown", never "art exists" |
| Open Library (openlibrary.org) | Books | 3 requests/second |
| Open Library Covers (covers.openlibrary.org) | Book covers | 80 downloads per 5 minutes (official cap: 100/5 min, exceeding risks an IP block) |
| Wikimedia (upload.wikimedia.org) | Movie/game posters from Wikidata | 5 downloads/second |
| Local NFO sidecars | Movies, TV (Kodi convention) | No network — read locally, at most once per lookup |

Every rate budget is **shared across the whole server**: scans, refreshes, Fix-Match
searches, and image downloads for the same provider all draw from one budget, so no
combination of activity can exceed a provider's published policy.

Each library type also has its own metadata **queue channel** (Movies, TV, Music, Books,
Games), so adding several libraries at once enriches all types in parallel — books no
longer wait for the entire movie backlog to finish.

## When OMDb runs out of quota

When OMDb reports "Request limit reached" (or rejects the API key), SoftMedia suspends
ALL OMDb calls until midnight UTC and posts a notification. No fallback searches are
burned against an exhausted key. Retries resume automatically the next day.

## How matching stays accurate

- **IMDb-id first**: an NFO sidecar carrying an IMDb id is read before any provider, so
  the lookup is exact instead of a title guess. Matched ids (IMDb, TVMaze, MusicBrainz,
  Open Library) are stored, making every later refresh a single direct request.
- **Year disambiguation**: movie title searches fetch several candidates and pick the one
  matching your file's year (±1). If every candidate contradicts the file's year, the
  item is left unenriched — wrong metadata is worse than none. Use **Fix Match** on the
  item's admin menu to resolve these by hand.
- **Confidence thresholds**: MusicBrainz and Open Library matches below a confidence
  score are rejected rather than guessed.

## Fix Match and locking

Fixing a match (or editing metadata) sets a **lock** on the item. Locked items are never
touched by scans, refreshes, or artwork sweeps — including the filename re-parse on
rescans, so a corrected title/year survives every future scan. Unlock from the same menu
to let automatic enrichment resume.

## "Why isn't this item retrying?"

- A search that definitively found **no match** is remembered for 30 days — the same
  query is not re-sent on every scan. Transient errors (timeouts, provider hiccups) are
  NOT remembered; those retry on a short ladder (1 min → 5 min → 30 min → 4 h).
- After the ladder is exhausted, the weekly **retry amnesty** re-grants attempts on a
  decaying schedule (about 1, 2, then 4 weeks apart). Any successful enrichment resets
  the schedule.
- An item that matched but whose provider simply has **no poster** is considered
  complete in Relaxed mode (it will not retry forever). Strict mode keeps retrying until
  its type-specific fields are filled — that is what Strict opts into.
