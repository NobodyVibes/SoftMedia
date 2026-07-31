# Metadata & Cache Follow-ups Plan — 2026-07-29

> **STATUS: ALL ITEMS COMPLETE (2026-07-29, same day).** MC-WI-001..010 implemented and
> verified: server suite 1990/0, client suite 575/0, `npm run build` green, and live QA
> against the running dev server confirmed MC-WI-001 (static `/cache/trickplay/...`
> 404s even for a file that exists on disk; `/cache/images/...` still 200; API 401s
> unauthenticated). Notes per item below record what shipped where it differs from the
> original sketch: MC-WI-006 landed as orphan-genre reaping in the daily sweep (not a
> DeleteLibraryAsync hook — the admin normalisation pass remains a deliberate separate
> tool); MC-WI-003 has no dedicated test (private timer-driven sweep; one-line query
> filter). This file is retained as design history.

Follow-ups discovered while fixing the three cache-lifecycle reports (library deletion
leaking cast/trickplay artifacts; permanent `cache/images/proxy` duplicates; unexplained
`cache/subtitles` files). Work items are MC-WI-001..010, ordered by priority.

## Baseline — already fixed on 2026-07-29 (context, do not redo)

- `LibraryCleanupService` (new): manual library delete now removes artwork, trickplay,
  thumbnails, cached subtitle VTTs, and cast headshots. Cast deletion is keyed by
  `Person.ExternalId` (the on-disk key; the old code passed PKs and deleted nothing) with
  a shared-person guard (Persons are global across libraries).
- `ProxyImageStore` (new): owns `cache/images/proxy`. The image download queue and the
  nightly adoption pass delete a URL's proxy copy (+ derived thumbnails + `.404`
  sentinel) the moment the permanent item-keyed copy takes over; leftovers expire after
  30 days (cache hits touch mtime).
- `ImageCacheCleanupService` daily sweep extended: trickplay dirs, thumbnails (7-day
  min-age guard for proxy-derived keys), cast headshots (valid = referenced by any
  `MediaItemCast` row), subtitle VTTs (valid = hash of any row's `Path`), proxy TTL.
  Row-existence contract everywhere: `IsMissing` rows keep artifacts (SR-WI-011/037).
- `DeleteImageForMediaItem` covers ComicSeries/ComicIssue (books/ keys); the download
  queue now processes ComicSeries posters (previously enqueued-then-dropped, leaving the
  row proxying forever); library delete also purges the library's `LibraryRecentCaches` row.
- Tests: `LibraryCleanupServiceTests` (incl. ExternalId-vs-PK regression),
  6 new sweep tests. Suite 1982/0 green.

Subtitle files answer (for reference): `cache/subtitles/*.vtt` are ffmpeg extractions of
EMBEDDED subtitle tracks, created on playback start by `SubtitleService`, keyed
`{hash16(source path)}_s{track}_{mtimeTicks}.vtt`. No provider downloads exist.

---

## P1 — security / correctness

### MC-WI-001 — Stop unauthenticated static serving of `cache/subtitles` and `cache/trickplay`
- **Problem:** `Program.cs` (~line 362) uses a plain `app.UseStaticFiles()` over wwwroot, so
  extracted subtitle text and trickplay frames — actual media content — are reachable with
  NO auth at `/cache/subtitles/<name>.vtt` and `/cache/trickplay/<guid>/sheet-N.jpg`.
  Names are hash/guid-based (obscure, not enumerable) but a leaked URL needs no credentials.
- **Verified 2026-07-29:** both areas are consumed ONLY via authorized endpoints —
  `TranscodeController` `/subtitles.vtt` (token-checked) and `TrickplayController`
  (`[Authorize(ScopePolicies.ReadLibrary)]`, `?token=` lifted by JwtBearerEvents). The
  client never requests the static paths (grep of `SoftMedia.Client/src` = 0 hits).
- **Fix (low-risk option):** a small middleware before `UseStaticFiles()` that returns 404
  for any request path starting `/cache/subtitles` or `/cache/trickplay`. (404, not 401 —
  consistent with the anti-probe rule.) `/cache/images/**` must STAY public: the client
  loads poster paths from DB columns in plain `<img>` tags.
- **Alternative (bigger):** relocate both roots outside wwwroot (they never needed to be
  web-served). Requires a boot-time directory move (same-volume rename, cheap even at 4 GB)
  and touching `TrickplayService`/`SubtitleService` root construction + tests. Prefer the
  middleware unless the "caches live under wwwroot/cache" convention is being revisited anyway.
- **Verify:** integration test — unauthenticated GET of a seeded
  `/cache/subtitles/x.vtt` → 404; `/cache/images/movies/x.jpg` still 200; trickplay via
  API with token still 200.
- **Effort:** small.

### MC-WI-002 — `ImageController` archive.org allowlist drifted from the audited one
- **Problem:** audit wave-2 L-26 tightened `ImageCacheService.IsHostAllowed` to anchor on
  `.us.archive.org` / `.ca.archive.org` because the broad `.archive.org` suffix admits
  `web.archive.org` — a content-rewriting fetch proxy that can launder an arbitrary
  upstream fetch through an allowlisted host. `ImageController.IsHostAllowed`
  (ImageController.cs ~line 57) still uses the broad `.archive.org` suffix while its
  doc comment claims "Kept in sync with ImageCacheService". It is not.
- **Fix:** extract ONE shared policy helper (e.g. `Services/Media/ImageFetchPolicy.cs`:
  the allowlist set, `IsHostAllowed`, and the manual redirect-follower — both classes
  currently duplicate ~80 lines of it) and use it from both `ImageController` and
  `ImageCacheService`, with the anchored L-26 suffixes.
- **Verify:** extend `ImageControllerSsrfTests` with a `web.archive.org` redirect case
  (must be blocked) and an `iaNNN.us.archive.org` case (must pass); existing
  ImageCacheService SSRF tests keep passing against the shared helper.
- **Effort:** small–medium.

## P2 — remaining leaks / consistency

### MC-WI-003 — `TrickplayWorker` retries offline (`IsMissing`) items every sweep
- **Problem:** the candidate query (TrickplayWorker.cs ~lines 90–96) filters only on type
  and non-empty `Path`. An `IsMissing` item fails `File.Exists` inside `GenerateAsync`,
  logs a warning, stays `!HasTrickplay`, and is re-selected every sweep — warning spam
  and wasted polling for as long as a drive is offline.
- **Fix:** add `&& !m.IsMissing` to the query.
- **Verify:** unit test seeding an IsMissing row → worker sweep selects nothing for it.
- **Effort:** trivial.

### MC-WI-004 — Immediate artifact cleanup on scan-driven hard deletes
- **Problem:** `BaseMediaScanner.HardDeleteItemsAsync` (~lines 964–987; retention expiry
  after `MissingItemRetentionDays`, or immediate when retention = 0) is DB-only. All
  derived artifacts now DO get reclaimed, but only by the daily sweep — up to ~24 h later,
  and only if the server stays up. The manual-delete path cleans immediately; this path
  should match.
- **Fix:** before the `ExecuteDeleteAsync`/`RemoveRange`, capture `(Id, Type, Path)` of
  the doomed rows and invoke the same cleanup surface `LibraryCleanupService` uses
  (consider extracting a `DeleteArtifactsForItemsAsync(items, paths)` shared method so
  scanner and library delete cannot drift). Cast headshots can stay sweep-only here —
  per-scan exclusivity queries are not worth it.
- **Verify:** scanner test — retention-expired item's poster/trickplay/VTT gone right
  after the scan, without running the sweep; purge-brake and shielded-path tests unaffected.
- **Effort:** medium (BaseMediaScanner DI touch — it currently has no artifact services).

### MC-WI-005 — Orphaned `Person` rows are never deleted
- **Problem:** nothing in the codebase ever removes `Persons` rows. After a library
  delete, persons referenced only by that library keep their rows forever (headshot
  files are now cleaned, DB rows are not). Harmless per-row, unbounded over time.
- **Fix:** in the daily sweep, set-based delete of persons with no referencing
  `MediaItemCast` row: `db.Persons.Where(p => !db.MediaItemCasts.Any(c => c.PersonId == p.Id)).ExecuteDeleteAsync()`.
  SQLite serializes writers, so the NOT-EXISTS evaluation is atomic; a concurrent scan
  that later re-credits the person simply re-creates the row via the aggregator's
  ExternalId dedup. NOTE: `ExecuteDelete` does not run on the EF InMemory provider —
  test with the SQLite in-memory harness (see EF-InMemory memory note).
- **Verify:** sweep test — person with cast row kept, person without one removed.
- **Effort:** small.

### MC-WI-006 — Genre retirement not wired into library deletion
- **Problem:** deleting a library cascades `MediaItemGenres` but orphaned `Genres` rows
  remain; `GenreMaintenanceService` can retire them but is reachable only via the manual
  admin endpoint (AdminController ~line 287).
- **Fix:** call the retirement pass at the end of `DeleteLibraryAsync` (best-effort,
  logged, never failing the delete), or fold it into the daily sweep alongside MC-WI-005.
- **Verify:** delete-library test asserting orphan genre rows are gone.
- **Effort:** small.

## P3 — polish / hygiene

### MC-WI-007 — Admin visibility: cache size stats
- **Idea:** the Background Tasks card can already trigger "Image Cache Cleanup" manually,
  but nothing shows cache footprint. Add a small system endpoint + admin card reporting
  per-area file count/bytes (`images/{sub}`, `proxy`, `thumbnails`, `trickplay`,
  `subtitles`) so regressions like the 4.1 GB trickplay pile are visible at a glance.
- **Effort:** small–medium (server endpoint + one admin card).

### MC-WI-008 — `MediaTracksController` subtitle endpoint bypasses the VTT cache
- **Problem:** `GET /api/media/{id}/subtitles/{trackIndex}` (MediaTracksController
  ~lines 120–141) pipes ffmpeg stdout per request (`-c:s webvtt -f webvtt pipe:1`) —
  every call re-demuxes the container even though `SubtitleService` has a persistent
  cache for exactly this.
- **Fix:** route it through `SubtitleService.ExtractSubtitleToVttAsync` (serve the
  caller-copy), keeping the streaming endpoint shape.
- **Effort:** small–medium (mind the absolute-vs-subtitle-relative stream index
  conversion — `GetSubtitleStreamIndexAsync` exists for this).

### MC-WI-009 — Thumbnail touch-on-hit
- **Problem:** proxy-derived thumbnails are unknown keys to the sweep and get reaped once
  older than 7 days even when served daily, then regenerated on next view (minor churn).
- **Fix:** best-effort `File.SetLastWriteTimeUtc(now)` on the fast path of
  `GetOrCreateThumbnailAsync` (mirrors `ProxyImageStore.TouchOnHit`), making the min-age
  guard true LRU.
- **Effort:** trivial. Do together with MC-WI-002/whenever ThumbnailService is next open.

### MC-WI-010 — Repo hygiene: dead test project and TempDebug
- **Problem:** `src/SoftMedia.Tests` is NOT in `SoftMedia.sln` and its
  `LibraryIntegrationTests` constructs `LibraryService` with a long-stale signature (it
  no longer compiles); it silently rots and misleads greps. `src/TempDebug` also looks
  like leftovers.
- **Fix:** delete both (or, if the integration tests are wanted, move the useful cases
  into `SoftMedia.Server.Tests` and fix signatures). Decide, don't keep the zombie.
- **Effort:** small.

## Suggested sequencing

1. MC-WI-001 + MC-WI-002 together (one security-focused session; both touch the
   image/static pipeline and have crisp tests).
2. MC-WI-003..006 as one "lifecycle consistency" session (scanner + sweep + delete path).
3. MC-WI-007..010 opportunistically.

Standing constraints for all items: preserve the row-existence contract (`IsMissing`
artifacts heal on drive return), 404-over-403 anti-probe rule, and the client type gate
is `npm run build` (root tsc checks nothing) — though only MC-WI-007 touches the client.
