# Scan & Metadata Systems Review — 2026-07-27

Scope: library scanning (`Services/Scanning/*`), metadata enrichment (`Services/Metadata/*`),
image pipeline (`Services/Background/ImageDownloadQueueService.cs`), queue orchestration
(`LibraryScanQueueService`, `MetadataRefreshService`, `ScheduledScanService`,
`MetadataRetryAmnestyService`), storage model (`Models/MediaItem.cs` + migrations).
Method: two parallel deep-read review agents (scanners; providers/pipeline) plus direct
review of orchestration and storage. Every finding was verified against surrounding code
before inclusion.

## Verdict

The architecture is fundamentally sound and in several places better than the
Jellyfin/Plex baseline (purge brake, soft-delete + heal, move/rename reconciliation,
ID-based re-lookup, DB-backed retry ladder, normalized storage). The problems are
implementation gaps, not design mistakes. Two findings are active data-loss/correctness
bugs (S1, P1); the rest are efficiency and match-quality improvements.

## Cross-type serialization (from the 2026-07-27 investigation)

- Library scans are strictly sequential (`LibraryScanQueueService` single worker) — by
  design, but it means multi-library initial setup progresses one type at a time.
- Movie/Book/Game/Photo enrichment shares ONE FIFO channel
  (`MetadataQueueService.GetChannelForType`) — books wait behind the entire movie backlog.
- Artwork is a single global channel, 2 concurrent, FIFO across all types
  (`ImageDownloadQueueService`).
- Levers: split Book/Game channels; per-host image limiters + per-type queues; optionally
  concurrent scans for different library types (bigger change).

## Scanner findings (ranked)

### S1 (HIGH — data loss): rescans wipe enriched Year and revert admin-locked titles
`MovieScanner.cs:68-74`, `GameScanner.cs:60-66`. Scanners unconditionally re-stamp
`Title`/`Year` from the filename parse on every scan and never check `MetadataLocked`.
- Yearless filename ("Inception.mkv") → provider-enriched `Year` set back to null on next
  scan; item still "looks complete" so enrichment never repairs it.
- Fix Match (`AdminController.cs:416-435`) sets `MetadataLocked = true`, honored by the
  metadata queue — but the next library scan reverts Title/SortTitle/Year anyway (the
  per-directory `SaveChangesAsync` persists all tracked changes even for Skipped results).
- TvScanner (`TvScanner.cs:357-361`) and the comic path (`BookScanner.cs:298-302`) already
  guard this; movies/games just miss it.
- Fix: fill-only Year (`movie.Year = year ?? movie.Year`); skip identity stamping when
  `MetadataLocked` or unchanged.

### S2 (HIGH — perf): parallelism/transaction unit is "one directory"
`BaseMediaScanner.cs:220-305`. A flat 10k-file folder = ONE work item: zero parallel
ffprobe, one DbContext tracking 10k entities, one giant save at the end (failure/cancel
discards everything, next scan re-probes all). Fix: flatten discovery into bounded batches
(~100 files) as the parallel/save unit; existing striped locks + `_dbWriteLock` make it safe.

### S3 (HIGH — perf): local-artwork sweep is O(N²) in flat folders and runs for unchanged files
`MovieScanner.cs:87-88` → `LocalArtworkService.cs:76` re-lists the whole directory per
file, every scan. Flat 10k-movie folder ≈ 100M filename materializations per rescan;
brutal on SMB. Fix: capture image sidecars during discovery enumeration or memoize the
per-directory listing (pattern already exists: `_vaDirectoryCache`).

### S4 (HIGH — correctness): watcher writes bypass the SQLite write lock
`BaseMediaScanner.cs:484` (`ProcessSingleFileAsync` final save) and
`LibraryWatcher.cs:466` (`MarkFileMissingAsync`) save without `_dbWriteLock`, while
`_stableFileSemaphore(3,3)` allows 3 concurrent writers — potentially during a running
scan (SQLITE_BUSY risk; the codebase's own SR-WI-035 discipline). Secondary race: watcher
import + concurrent scan can mint a duplicate row; next scan purges one twin with its
history. Fix: take the lock in both saves; optionally defer watcher processing while
`IsLibraryInQueue(libraryId)`.

### S5 (HIGH — gap): watcher misses directories moved into the library
`LibraryWatcher.cs:380-383`. A completed folder moved in (the standard *arr/torrent
pattern) raises ONE Created event for the directory; `OnFileCreated` rejects non-media
paths and does nothing. Content invisible until the scheduled sweep. Fix: if
`Directory.Exists(fullPath)`, enumerate media files into `_pendingFiles` or flag
`_librariesToScan[libraryId]` (the rename path already does this).

### S6 (MEDIUM): `Path.ToLower()` lookups = unindexed full-table scans, ASCII-only folding
`BaseMediaScanner.cs:469-471`, `LibraryWatcher.cs:460-461`, `TvScanner.cs:444-449`,
`MusicScanner.cs:259-265`. `LOWER(Path)` can't use the Path index (50-file import against
100k rows = 50 table scans) and SQLite LOWER() folds ASCII only (non-ASCII casing
differences re-mint the duplicate this query was added to prevent). Fix: `COLLATE NOCASE`
index on Path or a stored normalized-path column.

### S7 (MEDIUM): dead per-file stat + files that re-ffprobe forever
`Services/Media/Strategies/VideoAnalysisStrategy.cs:87,99`. The mtime check in Missing
mode is dead on the scan path (scanner stamps `DateModified` before `AnalyzeAsync`) — one
wasted filesystem stat per unchanged file. The `BitDepth == null || FrameRate == null ||
Width == null` migration guard re-probes files ffprobe can't fill, every scan, forever.
Fix: pass discovery mtime down; add a "probe attempted" sentinel.

### S8 (MEDIUM): post-scan TV artwork sweep is N+1
`TvScanner.cs:246-281`: per-series query + FS probes + save, sequential; 500 series = 500
queries + up to 500 commits per scan even when stable. Batch-load, save once.

### S9 (MEDIUM): scan-long full-entity cache is unbounded memory
`BaseMediaScanner.cs:165-178`: `knownFilesCache` holds complete entities (Overview,
ExifJson, …) for the whole library. Slim projection (Id/Path/Size/DateModified/Type/
IsMissing) for the skip-check would cut ~10×.

### S10-S14 (LOW)
- GameScanner never re-analyzes changed files and has no unchanged-file fast path
  (`GameScanner.cs:81-96`).
- O(E²) episode-metadata lookup per series (`TvScanner.cs:612-686`) — build a
  (season,episode)→element dictionary once per series.
- `OrdinalIgnoreCase` path keys wrong on case-sensitive filesystems
  (`BaseMediaScanner.cs:165,184`) — platform-conditional comparer.
- Season-cache key collision: `s.SeasonNumber ?? 0` collides null with Specials
  (`TvScanner.cs:106`).
- Per-subdirectory `File.GetAttributes` extra stat in discovery (`BaseMediaScanner.cs:385`).

## Metadata pipeline findings (ranked)

### P1 (HIGH): OMDb bundled-key mode bypasses quota accounting; limit errors read as "not found"
`OMDbProvider.cs:112-121` counts usage only for `mode == "custom"`; the default shared
"softmedia" key is never counted and never stops. `OMDbProvider.cs:232-234` ignores the
`Error` field, so "Request limit reached!" triggers the `&s=` search fallback (second
wasted request) then the full retry ladder (4 more chain runs), then `IsRetryExhausted` —
which the weekly amnesty resets, repeating the burn weekly. Fix: count every request
regardless of mode; parse `Error` and treat limit/401 as provider-unavailable (skip
fallback, next-day retry).

### P2 (HIGH): no negative-result caching — never-matching items re-query forever
Only TVMaze RawPayload and the comic "EMPTY" sentinel cache anything. A no-match
movie/book/game stamps nothing → every ladder tier re-runs the full provider chain with
the identical query; `MetadataRetryAmnestyService.cs:140-177` resets exhaustion every 7
days. 300 unmatched items ≈ 1,500 wasted requests/week, indefinitely. Fix: (provider,
normalized query)→miss cache with TTL, and/or decaying amnesty cadence.

### P3 (HIGH): Wikidata movie matching ignores Year, takes LIMIT 1 by notability
`WikidataProvider.cs:33-34,61-62`. "Dune (1984)" resolves to the 2021 film; the wrong
match stamps a poster so relaxed-mode `NeedsEnrichment` seals it as complete. TVMaze
(±1-year scoring) and OMDb (`&y=`) already do this right. Fix: fetch top-N candidates
with year, pick `item.Year` ±1; run the NFO chain's ImdbId extraction BEFORE the primary
provider so ID lookup wins.

### P4 (HIGH): SPARQL title interpolation unescaped
`WikidataSparqlClient.cs:104-116` embeds the title raw; quotes/backslashes → HTTP 400 →
full retry ladder + weekly amnesty forever. `ComicWikidataProvider.cs:129-130` already has
`EscapeForSparql` — move it into the base client.

### P5 (MEDIUM): MusicBrainz never re-uses its promoted MBID; takes result[0] blindly
`MusicBrainzProvider.cs:36-47` (no `item.MusicBrainzId` short-circuit — every refresh
repeats the Lucene search at 1 req/s) and `:309-311`/`:398-400` (no score threshold, no
year/artist cross-check). Fix: ID-first fetch; use MB's `score` + agreement checks.

### P6 (MEDIUM): OpenLibrary re-parses the book file for ISBN on every fetch
`OpenLibraryProvider.cs:182-197` always runs `ExtractAsync(item.Path)` (full EPUB/PDF
parse) despite `item.Isbn` being promoted on first success (`MetadataAggregator.cs:135-139`).
No OL work key persisted either. Fix: promoted-column first; persist the matched OL key.

### P7 (MEDIUM): Fix-Match/search endpoints bypass rate limiters
No lease in MusicBrainz/TVMaze/Wikidata/OpenLibrary `SearchAsync`/`FetchByCandidateAsync`
paths (OMDb's is funneled correctly). MusicBrainz bans by UA for policy violations. Fix:
acquire the same limiter leases.

### P8 (MEDIUM): CoverArt Archive HEAD checks unlimited + failure-optimistic
`MusicBrainzProvider.cs:228-250,468-499`: up to 2 unthrottled HEADs per album/track; 503
treated as "art exists", leaving items hotlink-proxying remote URLs.

### P9 (MEDIUM): TVMaze lease under-counts (up to ~2 real requests per permit)
`TVMazeProvider.cs:39-104` — worst case ≈36 req/10s vs the published 20/10s. One lease
per HTTP request.

### P10 (MEDIUM): image pipeline — one borrowed limiter for all hosts; no download retry
`ServiceCollectionExtensions.cs:469-489` reuses the TVMaze limiter for every image host
(can exceed covers.openlibrary.org's 100 req/5min while throttling fast hosts);
`ImageDownloadQueueService.cs:147-151` drops failed downloads permanently (item keeps
proxying the remote URL). Fix: per-host limiters keyed on `Uri.Host`; one bounded delayed
retry.

### P11 (MEDIUM): provider data computed then discarded
`MetadataAggregator.cs:147-155` persists `Extra` only for photos — OMDb
runtime/writer/awards/boxOffice, TVMaze status, MB artistType are built and dropped.
`GameMetadataProvider.cs:77-84` Platform/GameMode have no consumer at all (SPARQL pays
for P400/P404 for nothing). Persist or stop fetching.

### P12 (MEDIUM): poster-less successful matches loop the ladder + amnesty forever
`MetadataQueueService.cs:243-262` + relaxed-mode `NeedsEnrichment` (`!hasPoster`,
`MetadataEnrichmentPolicy.cs:83-87`): a matched movie with no provider image retries the
identical query weekly, forever. Fix: "attempt complete" sentinel when hash stamped +
provider succeeded (comics/photos already have the pattern).

### P13-P18 (LOW)
- No timeouts on Wikidata/TVMaze/OMDb/MusicBrainz clients (100s default) — a hung WDQS
  query pins a Shared-channel slot; set ~20-30s.
- `OmdbUsageTracker` writes settings twice per request under a global semaphore — batch.
- `deferImageCaching` dead param whose early-return skips MetadataHash stamping (latent
  retry-loop trap) — `MetadataAggregator.cs:52,172-175`.
- Retry enqueue-then-delete crash window (benign, deduped).
- RawPayload cache hardcodes providerName "TVMaze" (`MetadataAggregator.cs:257`).
- Enrichment-mode setting re-read per item in the hot loop (`MetadataQueueService.cs:248-250`).

### Orchestration (direct review)
- `MetadataRefreshService.RunRefreshJobAsync` (`MetadataRefreshService.cs:80-103`)
  materializes full entities when it needs Id/Type — project.
- "Running" refresh mode refreshes ALL series regardless of status (comment acknowledges).

## What NOT to change (strengths)

- Purge brake + admin notification, unreachable-root abort, soft-delete/heal
  (`IsMissing`), retention-window hard delete — better than typical media servers.
- Move/rename reconciliation preserving watch history (`BaseMediaScanner.cs:579-688`).
- Incremental scanning keyed on size+mtime in every scanner, with correct
  capture-before-stamp ordering.
- Bulk pre-load caches killing N+1s; static `_dbWriteLock`; chunked ExecuteUpdate/Delete;
  two-tier scan queue with detection preemption.
- Normalized storage: MetadataJson dropped (2026-04), promoted indexed columns,
  relational genres/cast with diff-based updates, partial unique Path index.
- ID-based re-lookup (ImdbId/TvMazeId), TVMaze single-call embedding, Kodi-style NFO chain
  with correct precedence (sidecar > NFO > provider; local art wins).
- MusicBrainz exactly at 1 req/s; mandatory User-Agents on every client (Wikimedia-policy
  compliant); DB-backed retry ladder honoring MetadataLocked at one chokepoint.
- OpenLibrary matching quality (ISBN-first, token-coverage scoring, confidence threshold
  preferring no metadata over wrong metadata).

## Suggested fix order

1. S1 (scanner wipes Year / bypasses MetadataLocked) — active data loss.
2. P1 (OMDb quota accounting + limit-error recognition) — cheap, stops the worst storms.
3. P4 (SPARQL escaping) — tiny, kills a permanent-failure class.
4. S4 + S5 (watcher write lock; directory-move detection) — correctness + biggest UX gap.
5. P3 (Wikidata year disambiguation) — biggest wrong-match payoff.
6. P2 + P12 (negative-result cache + attempt-complete sentinel) — largest sustained
   network savings.
7. S2 + S3 (batch parallel unit; artwork sweep memoization) — flat-library scan speed.
8. P5/P6 (MBID/ISBN reuse), S6 (NOCASE path index), P10 (per-host image limiters).
9. Cross-type parallelism (channel splits) if the "one type at a time" setup experience
   matters — see serialization section.
