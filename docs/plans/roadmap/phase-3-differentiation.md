# Phase 3 — Differentiation

**Roadmap Phase:** 3 of 4
**Status:** In Progress *(scope revised by maintainer 2026-05-30)*
**Estimated Duration:** 2-3 weeks (revised — two items only)
**Date:** 2026-05-11
**Parent Document:** [00-roadmap-overview.md](./00-roadmap-overview.md)

## 0. Maintainer scope revision (2026-05-30)

The original five-item phase was reviewed and **cut to two** before implementation. The full work-item bodies below are the *original* spec; where they conflict, **this section wins**:

| Item | Original | Final decision |
|------|----------|----------------|
| **P3-WI-001** Chromecast sender | as written | **Keep.** Implement. |
| **P3-WI-002** OpenSubtitles auto-download | external provider | **Dropped.** Embedded-track extraction + sidecar `.srt`/`.vtt` discovery already cover the common case; hash-matching a third party conflicts with the privacy charter. (Not replaced — no manual-upload feature in this phase either.) |
| **P3-WI-003** Bulk edit + manual match | as written | **Keep — full Option B (provider re-search).** Includes (a) `MediaItem.MetadataLocked` flag + auto-refresh skip, (b) admin manual field/poster editing, AND (c) the Plex-style provider re-search: typed query → ranked candidates with posters/years → click to apply. Requires adding a `SearchAsync(query, year?)` method to every metadata provider that doesn't have one. |
| **P3-WI-004** Smart playlists + tags | both | **Dropped entirely.** No tags, no smart playlists. |
| **P3-WI-005** Reserved community slot | placeholder | **Dropped.** The "community slot" framing doesn't fit a solo OSS project. |

So Phase 3 ships **two items**: Chromecast (SPA-only) and the Full manual match. The 6-10 week estimate is replaced with 2-3 weeks.

A pre-implementation verification workflow (5 agents) ran against the *original* items; its findings on P3-WI-001 (cast.framework loads via Google-hosted script, not npm) and P3-WI-003 (provider `SearchAsync` is the biggest net-new gap) remain authoritative. P3-WI-002/004/005 findings are now moot.

## 1. Phase Summary

Phase 3 work items are individually small-to-medium but collectively differentiate SoftMedia from Plex and Jellyfin. Unlike Phases 0-2, items here have no strict internal sequencing — contributors can pick whichever they prefer. One slot is held open for a community-proposed contribution.

Item-level specifications in this phase are deliberately less prescriptive than Phases 0-2 because their detailed design will only be finalised at implementation time. Each item carries enough information to begin a design spike and produce a more detailed sub-spec.

## 2. Objectives

- Users can cast a stream to a Chromecast-compatible device from the web client.
- Videos missing subtitles in the user's language can have them downloaded automatically.
- Administrators can manually correct wrong metadata matches without deleting and re-scanning files.
- Users can define rule-based playlists that update automatically as the library changes.
- Users can attach freeform tags to media items.

## 3. Prerequisites

- Phases 0-2 complete. Notable per-item dependencies:
  - P3-WI-001 (Chromecast) depends on P1-WI-003 (bandwidth caps applied to Cast streams).
  - P3-WI-003 (manual match) depends on the metadata-refresh respect for `MetadataLocked` (added by this work item).
  - P3-WI-004 (smart playlists) depends on tags from the same work item.

## 4. Work-Item Summary

| ID | Title | Status | Effort |
|----|-------|--------|--------|
| P3-WI-001 | Chromecast Sender | Not Started | 3-5 d |
| P3-WI-002 | OpenSubtitles Auto-Download | Not Started | 3-4 d |
| P3-WI-003 | Bulk Metadata Edit and Manual Match Override | Not Started | 4-5 d |
| P3-WI-004 | Smart Playlists and Tags | Not Started | 5-7 d |
| P3-WI-005 | Reserved for Community Contribution | Open | — |

## 5. Work Items

### P3-WI-001 — Chromecast Sender

#### Motivation

The single largest perceived gap vs Plex. Chromecast support is implemented browser-side via Google's `cast.framework` JavaScript library — no server-side dependency, no per-server quota, no SDK fees.

#### Specification

- Integrate `cast.framework` into `src/SoftMedia.Client/src/components/player/VideoPlayer.tsx`.
- Detect cast availability via the `cast.framework.CastContext` API; render a Cast button when ≥ 1 receiver is reachable.
- On cast start, hand the current `StreamPlan.url` (carrying `?access_token=...` per SDD §4.5) to the Cast SDK. The Cast device fetches the HLS playlist and segments directly from SoftMedia.
- Support pause/play/seek through the Cast Media Control namespace.
- End cast session cleanly on tab close or explicit user stop.
- The P1-WI-003 bandwidth cap applies to Cast streams identically to browser streams (no special-casing).

#### Acceptance

- Chrome browser on the same LAN as a Chromecast device shows the Cast button.
- Casting a Direct-Play H.264 stream plays on the receiver.
- Casting a Transcode (HLS) stream plays on the receiver.
- Seek operations on the receiver are reflected in the sender UI.
- WAN-classified clients respect the WAN bandwidth cap from P1-WI-003 when casting.

#### Estimated Effort

3-5 days.

#### Dependencies

- P1-WI-003 (bandwidth cap honoured on Cast streams).

#### Risks

- **Chromecast receivers reject some HLS variants.** Mitigation: test against Chromecast Ultra and Google TV; document supported formats in `docs/user-docs/features/casting.md`.
- **`cast.framework` requires HTTPS in production.** Documented in the reverse-proxy guide (P0-WI-001 deliverable).

---

### P3-WI-002 — OpenSubtitles Auto-Download

#### Motivation

Sidecar subtitle ingestion exists (`Services/Media/SubtitleService.cs`); embedded extraction exists; the missing piece is auto-fetching when the user's preferred language is not present. OpenSubtitles has a free tier with an established REST API and is the de-facto open-subtitle source.

#### Specification

- Introduce an `ISubtitleProvider` abstraction. `EmbeddedSubtitleProvider` and `OpenSubtitlesProvider` implement it.
- Trigger: on first play of a video item where neither embedded nor sidecar subtitles exist in the user's preferred language (`UserPreference.PreferredSubtitleLanguage`). **Not** triggered on scan, to respect OpenSubtitles' rate limits.
- Per-user opt-in: `AutoDownloadSubtitles` user preference, default `false`. Auto-download requires explicit opt-in *and* configured credentials.
- Admin settings: `OpenSubtitlesApiKey`, `OpenSubtitlesUsername`, `OpenSubtitlesPassword`. **No first-party shared key** — OpenSubtitles' terms of service make sharing risky.
- Match preference order: by movie hash → by IMDb ID → by title + year.
- Downloaded subtitle is saved as a sidecar `.{lang}.vtt` adjacent to the source file. If the source directory is read-only, fall back to a configured central path (`SubtitleCachePath`, default `data/subtitle-cache/`).

#### Acceptance

- Playing a video with no preferred-language subtitles and `AutoDownloadSubtitles=true` results in a subtitle appearing within ~10 seconds and persisting for subsequent plays.
- OpenSubtitles rate limit respected: the existing `RateLimitingDelegatingHandler` is reused; daily-allowance exhaustion produces a non-dismissable banner.
- A user without configured OpenSubtitles credentials sees a friendly "credentials required" hint, not a silent failure.

#### Estimated Effort

3-4 days.

#### Dependencies

- None within Phase 3.

#### Risks

- **Wrong-cut subtitle match** (subtitle is for a different edition). Mitigation: prefer matches verified by movie hash; expose a "pick another subtitle" UI affordance after first selection.

---

### P3-WI-003 — Bulk Metadata Edit and Manual Match Override

#### Motivation

Every long-lived library accumulates wrong matches. Today, the only fix is to delete the row and re-scan with a renamed file. "Fix Match" is a power-user staple in both Plex and Jellyfin.

#### Specification

##### Data Model

Add to `MediaItem`:

- `MetadataLocked` (bool, default `false`)
- `MetadataLockedAt` (DateTime? UTC)

##### Endpoints (admin-only)

- `POST /api/v1/admin/match/{itemId}/search` body `{ query, year? }` — re-runs the type-locked provider with an explicit query; returns ranked candidates including provider-specific confidence signals (Wikidata: `P31=film`; TVMaze: `status="Running"`; etc.).
- `POST /api/v1/admin/match/{itemId}/apply` body `{ providerId, providerKind }` — fetches and writes the chosen candidate's metadata, sets `MetadataLocked=true`, `MetadataLockedAt=now`.
- `POST /api/v1/admin/match/{itemId}/unlock` — clears the lock and re-queues the item for normal metadata refresh.

##### Refresh Behaviour

`MetadataRefreshService` and the scan-time enrichment path skip items where `MetadataLocked=true`. Item-detail responses include `metadataLocked` and `metadataLockedAt` so the UI can show a lock indicator.

##### Frontend

- "Fix Match…" affordance on detail pages, admin-only.
- Search dialog: query input → ranked candidates → confirm → success toast.
- Lock indicator on detail pages with an "Unlock" admin action.

#### Acceptance

- A mis-matched movie can be corrected to a chosen candidate within three clicks (open dialog, type query, click candidate).
- Subsequent metadata refresh leaves the locked item unchanged.
- Unlock restores the item to the normal refresh path; next refresh updates it.

#### Estimated Effort

4-5 days.

#### Dependencies

- None within Phase 3.

#### Risks

- **Provider candidate ranking is heuristic.** Bad suggestions waste admin time. Mitigation: surface provider-specific confidence signals; allow the admin to refine the query.

---

### P3-WI-004 — Smart Playlists and Tags

#### Motivation

A user-tag system unblocks user-curated collections that don't fit the existing franchise-`CollectionId` model. A rule-based playlist engine is a music-server staple ("everything 4K + HDR + unwatched", "all 80s synthwave + favourites"). Together they round out the library-curation story.

#### Specification

##### Tags

| Table | Columns |
|-------|---------|
| `Tags` | `Id` (Guid PK), `UserId` (FK), `Name` (string, unique-per-user), `Color` (hex string) |
| `MediaItemTags` | `MediaItemId` (FK), `TagId` (FK) — composite PK |

Tags are **user-scoped**: User A's "favourites" tag is distinct from User B's, and a tag created by one user is invisible to others.

CRUD endpoints under `/api/v1/tags`.

##### Smart Playlists

| Table | Columns |
|-------|---------|
| `SmartPlaylists` | `Id`, `UserId`, `Name`, `RulesJson` (encoded predicate tree), `CreatedAt`, `UpdatedAt` |

Rule grammar v1 — predicate tree with AND / OR groups and leaf predicates on:

- `genre`, `tag`, `library`
- `year-range`, `dateAdded-range`
- `internalRating-range`
- `watched` (bool)
- `videoCodec`, `audioCodec`
- `resolution`, `hdrFormat`

Materialisation: smart playlists are **evaluated on read** against the current library state. No stored membership table — avoids cache-invalidation complexity.

The existing `Playlist` table (hand-curated, ordered) remains separate.

#### Acceptance

- User creates a `rewatch` tag and applies it to 5 items.
- Smart playlist "rewatch + 4K" returns exactly those items satisfying both conditions.
- Editing a rule live-updates the playlist contents without explicit refresh.
- Smart-playlist read performance: P95 ≤ 200 ms for a library of 10 000 items with a 5-level predicate tree.

#### Estimated Effort

5-7 days.

#### Dependencies

- None within Phase 3.

#### Risks

- **Pathological predicate trees cause slow queries.** Mitigation: bound tree depth to 5 levels; cap leaves to 20 per tree; reject overly-complex rules with `400 Bad Request`.

---

### P3-WI-005 — Reserved for Community Contribution

Phase 3 deliberately leaves one slot open for a community-proposed contribution. Candidates from the gap analysis that would fit:

- **TV calendar / "Coming Soon" view.** Cheap UI on top of existing TVMaze airdate data.
- **Music lyrics.** LRC parser plus sidecar serving.
- **ReplayGain.** Per-track analysis + playback gain application.
- **Photos library completion.** `PhotosController` plus a photo scanner and timeline UI on top of the existing `MediaType.Photo` and `ExifMetadataProvider`.
- **Series follow / per-series notification setting.** Pairs with the P2-WI-004 webhook taxonomy.

The selected item is recorded here once committed, with a sub-document under `phase-3/community-{slug}.md`.

## 6. Phase Exit Criteria

Phase 3 is complete when **either**:

- All four explicit work items (P3-WI-001 through P3-WI-004) report acceptance criteria passing in CI, **or**
- Three of the four explicit items plus the community-contributed item are complete.

## 7. Out of Scope

- AirPlay sender (Phase 4).
- DLNA renderer (Phase 4).
- First-party Trakt / Last.fm / AniList SDKs — webhook recipes are the preferred path (Phase 4).
- LDAP authentication (Phase 4).
- Federated multi-server deployments (Phase 4).
- Native mobile clients (Phase 4).

## 8. Verification Log *(added 2026-05-30)*

Phase 3 shipped only **P3-WI-001** + **P3-WI-003** per the §0 scope revision. The other three items were dropped before any code was written:
- **P3-WI-002 OpenSubtitles** — dropped (embedded + sidecar already cover the common case; hash-matching a third party conflicts with the privacy charter).
- **P3-WI-004 Smart playlists + tags** — dropped (maintainer decision).
- **P3-WI-005 Reserved community slot** — dropped (framing doesn't fit a solo OSS project).

Server build clean. Client `tsc -b` clean; `vitest` 152/152. Server tests: **635 passed / 1 skipped / 0 failed** (up from 624 pre-Phase-3; +11 tests: 4 TVMaze search + 5 admin match + 1 anonymous-scope-bypass regression + 1 from earlier security fix).

### Pre-implementation verification (2-agent workflow)
- **WI-001:** `proceed-as-written`. One cosmetic correction — `StreamPlan.url` actually carries `?token=` not `?access_token=`; the JWT handler accepts both.
- **WI-003:** the most useful finding was that **every metadata provider already calls its search endpoint internally as a private fallback path** — so `SearchAsync` was mostly extracting that into a public method that returns multiple ranked candidates, not building search from scratch. Estimate dropped from ~7 days to ~4–5 days. Also identified `MetadataQueueService.ProcessItemAsync` as the **single chokepoint** every refresh/enrichment funnels through, so one guard there covers `MetadataRefreshService`, `MetadataRetryService`, all scanners, and (via the aggregator's downstream image-extract) the image-download queue.

### P3-WI-001 — Chromecast sender (frontend-only)
- `index.html` loads Google's `cast_sender.js?loadCastFramework=1`. **SRI intentionally not applied** — Google publishes a mutable, latest-tracking URL; pinning would break Cast on every SDK roll. Documented inline with a CSP-layer mitigation note.
- New `useCast()` hook initialises `cast.framework` against the default media receiver, tracks `isCastAvailable` / `isCasting` / `receiverName`, exposes `castNow(info)` / `stopCasting()`. Cast SDK globals typed as `any` (no @types package).
- Cast button in `VideoPlayer.tsx` between the explainer and quality buttons. Bandwidth cap (P1-WI-003) applies server-side without extra wiring since Cast hits the same `/api/transcode/{id}/master.m3u8` URL.
- No backend changes.

### P3-WI-003 — Full manual match (Option B)
- **Model + migration:** `MediaItem.MetadataLocked` (bool) + `MetadataLockedAt`; migration `20260530072824_AddMediaItemMetadataLock`. Surfaced in `MediaItemDto`.
- **Single-chokepoint lock guard:** `MetadataQueueService.ProcessItemAsync` skips locked items. Covers every refresh path with one line.
- **`ISearchableMetadataProvider`** capability interface (`SearchAsync` + `FetchByCandidateAsync`). Implemented in 5 providers, all reusing existing private search endpoints:
  - **TVMaze** — `/search/shows`, reuses `TvMazeId` short-circuit.
  - **OMDb** — `&s=` search respecting API-key mode, reuses `ImdbId` short-circuit.
  - **Open Library** — `/search.json?q=&sort=editions` with cover URLs.
  - **MusicBrainz** — release-group Lucene search with optional firstreleasedate filter.
  - **Wikidata** — uses `wbsearchentities` REST API for search (skipping SPARQL); Q-id-direct SPARQL for fetch.
- **Admin endpoints** (admin-only, on `AdminController`): `POST /admin/match/{id}/search`, `POST /admin/match/{id}/apply`, `PATCH /admin/match/{id}` (manual edit, auto-locks), `POST /admin/match/{id}/unlock`.
- **Frontend:** `FixMatchCard` rendered above the detail view for admins. Re-search modal with poster grid → apply; manual edit form; unlock action.
- **Tests (9):** `TVMazeProviderSearchTests` (4) + `AdminMatchIntegrationTests` (5).

### Maintainer follow-up notes
- Wikidata's `SearchAsync` returns all entity types from `wbsearchentities`, not just films — the description column lets the user disambiguate by eye (same UX as Plex's "Fix Match"). A future tightening could filter by `instance of (P31)` with a per-result claims call.
- The single-chokepoint lock guard means the lock is honoured everywhere even when new metadata services are added — **provided they go through `MetadataQueueService.EnqueueMetadataRefreshAsync`**. New direct-mutation paths must add their own guard.
