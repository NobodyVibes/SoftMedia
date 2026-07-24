# Pagination Conventions (SR-WI-065 / API-M6)

Two list styles coexist in the v1 API. Native clients must handle both; neither is
scheduled to change before 1.0. All JSON is camelCase.

## Style 1 — Offset paging with the `PagedResult<T>` envelope

```json
{ "items": [ ... ], "totalCount": 1234, "page": 1, "pageSize": 50 }
```

Query parameters: `?page=` (1-based, floored to 1) and `?pageSize=` (default 50,
clamped server-side to 1–100). `totalCount` is the full filtered count, so
`ceil(totalCount / pageSize)` gives the page count.

| Endpoint | Notes |
|---|---|
| `GET /api/v1/browse` | Full filter/sort surface; grid browse |
| `GET /api/v1/libraries/{id}/items` | Same filter surface, single library |

### Envelope-less offset paging (same mechanics, bare array)

| Endpoint | Notes |
|---|---|
| `GET /api/v1/interaction/history` | `page`/`pageSize` (clamp 1–100) but returns a bare `PlaybackHistoryEntryDto[]` — no `totalCount`. Clients page until a short/empty page comes back. |

## Style 2 — Bare `?limit=` lists (no envelope, no offset)

Return a plain JSON array of at most `limit` items. There is no way to fetch
"the next page"; these are top-N feed endpoints, not browsable collections.

| Endpoint | Default `limit` | Notes |
|---|---|---|
| `GET /api/v1/continue-watching` | 20 | |
| `GET /api/v1/media/recent` | 20 | Optional `?type=` library-type filter; episodes/tracks are rolled up to their series/album |
| `GET /api/v1/media/search` | 5 | Global search, per invocation |
| `GET /api/v1/watchlist` | 50 | |

## Unpaginated lists

Some list endpoints take no paging input at all and return a bounded, server-shaped
list — e.g. `GET /api/v1/libraries/{id}/recent` (served from the recently-added
cache) and the parent/child listings (`series/{id}/episodes`, `albums/{id}/tracks`,
etc., which return the complete natural set).

## Caveat — offset paging under a shifting sort

Style-1 endpoints sorted by `dateadded` (and the history feed) page by
`OFFSET/LIMIT` over a live collection. If items are inserted (a scan imports new
media) or removed between page fetches, rows shift under the client: an item can
appear on two consecutive pages (duplicate) or slide across the boundary and never
be seen (skip). Clients should dedup by `id` when appending pages and treat the
page sequence as a snapshot hint, not a stable cursor.

## Recommendation for future feed endpoints

New chronological feeds should use **keyset (cursor) pagination** instead of
offset: order by `(dateAdded, id)` and accept `?after=<dateAdded>,<id>`, returning
the next `limit` rows past that key. Keyset is immune to the skip/duplicate
problem above and stays index-friendly at any depth. Existing endpoints keep their
current contracts; this applies to net-new surfaces.
