# Scan & Metadata Remediation Plan — 2026-07-28

**Input:** the full-system review in
`docs/reports/scan-metadata-review-2026-07-27.md` (scanner findings S1–S14, pipeline
findings P1–P18, orchestration findings, cross-type serialization analysis). Every item
below cites its finding ID; read the report entry before working an item — it carries the
verified evidence and file:line anchors.

**Relationship to other plans:** independent of `background-ffmpeg-cpu-plan-2026-07-24.md`
(complete) and `system-review-remediation-plan-2026-07-24.md`. Touches scanners, metadata
providers, queues, the watcher, and the EF schema (4 migrations, batched per phase). No
client changes except where noted (SM-WI-044 possibly; SM-WI-070 none — channel split is
invisible to the UI).

**How to work this plan:** session protocol as before — read §9/§10 first, work one phase
per session (Phase 1 and 2 may share a session; Phase 5 is a full session), run each item's
verification, update §9/§10 in the same commit. Capture the server suite baseline at
session start (`dotnet test src/SoftMedia.Server.Tests`). Practical gotchas that bite this
plan specifically:

- Stop the dev server before building (bin lock).
- `dotnet ef` needs explicit `--project src/SoftMedia.Server --startup-project
  src/SoftMedia.Server`.
- EF InMemory evaluates LINQ client-side — every NEW query added by this plan (negative
  cache lookups, NOCASE path lookups, batched artwork loads) needs an integration test
  against real SQLite, not just a controller unit test.
- Scratch server instances share the repo content root (backups rotation, task status,
  wwwroot cache) — the test factory already strips `BackupRotationService`; keep it that
  way for any new integration fixtures.
- **Never point a scratch/test server at the production libraries: scans purge files
  missing from disk and the purge brake is not a guarantee.** Live verification uses
  copies (§3).

---

## 1. Conventions

- IDs: `SM-WI-###`. Phases ordered; later phases assume earlier ones merged.
- Every item: priority [H/M/L] → scope → acceptance → verification. "Live verify" =
  against a running server on `127.0.0.1:5011` (IPv4 — IPv6 localhost stalls ~210 ms/req).
- Regression rule: net server-suite count must not decrease. Behavior changes get tests in
  the same commit.
- **Real-name rule (maintainer requirement):** tests that exercise filename parsing,
  matching, disambiguation, or scan behavior must draw their file names from the
  fixture manifest (§3) harvested from the operator's actual Movies / TV / Music / Books
  libraries — not invented names. Synthetic names are allowed only as *additional*
  adversarial cases (quotes, non-ASCII, yearless), clearly labeled.
- **Provider etiquette rule:** no automated test may call a real third-party provider.
  Unit/integration tests use recorded/stubbed responses; the only real network traffic is
  operator-present live verification (§3), which is itself subject to the §2 limits.
- Each phase ends with an adversarial diff review of the phase's changes.

---

## 2. Rate-limit compliance table (authoritative targets)

The maintainer requirement: each official source's published limit respected
**individually** — no borrowed limiters, no shared budgets across hosts. This table is the
single source of truth; `RateLimiterFactory` gets one named limiter per row and a doc
comment citing the official source. Every HTTP request acquires exactly ONE lease from the
limiter for its host — the current TVMaze pattern (one lease, up to 3 requests, P9) and
the image-client pattern (TVMaze limiter for all hosts, P10/M6) are both non-compliant.

| Limiter name | Host(s) | Official policy (source) | Current config | Target config |
|---|---|---|---|---|
| `MusicBrainz` | musicbrainz.org | 1 req/s per IP, meaningful User-Agent mandatory (musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting) | 1/1s ✓ but Fix-Match search paths bypass it (P7) | Keep 1/1s; ALL call sites leased (SM-WI-022) |
| `CoverArtArchive` | coverartarchive.org | No published number (Internet Archive infra); honor 503 as throttle signal | **None** — HEAD checks unlimited (M4/P8) | New: 1/1s, queue 20; 503/error = "art unknown", never "art exists" (SM-WI-023) |
| `TVMaze` | api.tvmaze.com | 20 calls / 10 s per IP (tvmaze.com/api#rate-limiting) | 18/10s but one lease can cover up to ~3 requests → ~36/10s worst case (P9) | Keep 18/10s; one lease per HTTP request (SM-WI-021) |
| `OMDb` | omdbapi.com | Free key: 1,000 req/day (omdbapi.com) | 10/10s pacing ✓; daily counting ONLY in custom-key mode (P1) | Keep 10/10s; daily accounting for every key mode; limit-error recognition (SM-WI-011) |
| `Wikidata` | query.wikidata.org, www.wikidata.org/w/api.php | WDQS: max 5 parallel per IP, 60 s query deadline, ~30 errors/min ban threshold; Wikimedia UA policy (mediawiki.org WDQS User_Manual) | 5/10s ✓ (stricter than required); no HttpClient timeout (rides 100 s default, L1) | Keep 5/10s; 30 s client timeout (SM-WI-024); SPARQL escaping so malformed queries stop burning the error budget (SM-WI-012) |
| `OpenLibrary` | openlibrary.org | No hard published API number; identifying User-Agent with contact requested; be gentle | 3/1s fixed ✓ reasonable; search paths bypass (P7) | Keep 3/1s; all call sites leased (SM-WI-022) |
| `OpenLibraryCovers` | covers.openlibrary.org | **100 req/IP per 5 min** for non-CoverID/OLID lookups; exceeding = IP block (openlibrary.org/dev/docs/api/covers) | Shares the borrowed TVMaze image limiter ≈ 540/5min possible ✗ | New: 80/5min (20% margin) (SM-WI-020) |
| `WikimediaImages` | upload.wikimedia.org, commons.wikimedia.org | No hard number; UA policy mandatory; modest serial access expected | Borrowed TVMaze limiter ✗ | New: 5/1s (SM-WI-020) |
| `ImageDefault` | any other image host (TVMaze CDN, OMDb poster CDN, …) | — | Borrowed TVMaze limiter ✗ | New per-host default: 10/10s per distinct host (SM-WI-020) |

Notes: the existing `SoftMediaUserAgentHandler` already applies a compliant UA to every
provider and image client — keep it on all new clients. Limiter *pacing* (req/s) and
*budget* (OMDb daily quota) are different mechanisms; OMDb needs both.

**Shared-budget invariant (maintainer directive, 2026-07-28):** rate budgets are
per-PROVIDER and process-wide. Every library, every queue channel, every code path
(enrichment, Fix-Match search, image download, collection resolver) that talks to the
same provider/host acquires from that provider's ONE limiter instance. Splitting queue
channels or adding concurrency must never multiply a provider's request rate — channels
control fairness/ordering; limiters alone control rate. `RateLimiterFactory`'s
`GetOrAdd`-per-name design already guarantees a single instance per provider; the work
items above close the paths that bypass it.

---

## 3. Real-name fixtures and live-verify sandbox (Phase 0 output, used everywhere)

**Manifest.** `src/SoftMedia.Server.Tests/Fixtures/real-library-manifest.json` — harvested
from the operator's live libraries (Movies, TV, Music, Books) by SM-WI-001 and checked in
so tests are deterministic. Contents per library type: relative file paths (real release
naming), parsed-title expectations filled in on first review, plus flags for interesting
cases (yearless names, apostrophes/quotes, non-ASCII, multi-episode files, disc/track
layouts, comic issues). Names only — no media content, no absolute drive letters.

**Parser corpus.** SM-WI-002 turns the manifest into parameterized tests over the actual
scanner parsing paths. This locks CURRENT behavior before any scanner refactor (Phases 1
and 5 both touch scanners; the corpus is the tripwire).

**Live-verify sandbox.** A dedicated LiveVerify library layout (per-title folders — the
watcher/scanner expects them) built by COPYING a representative slice of the real
libraries: ~10 movies (must include ≥1 yearless filename and ≥1 remake-ambiguous title),
2 TV series (≥1 with specials), 2 music artists (≥2 albums), ~10 books (≥1 EPUB with
embedded ISBN, ≥1 without). Scans purge missing files and test servers share the repo
content root — the sandbox exists so no scan ever runs against the production library
folders during verification. Fixture ffmpeg lives at `src/SoftMedia.Server/ffmpeg-bin/`.

---

## 4. Phase 0 — Baseline and fixtures (part-session)

### SM-WI-001 [H] Real-library fixture manifest harvester
One-off dev utility (test-project tool or admin-only debug endpoint, maintainer's choice)
that reads the configured SQLite DB **read-only** (fallback: read-only enumeration of the
library roots) and emits the §3 manifest: per-type relative paths + item counts.
- Acceptance: manifest checked in covering all four library types; ≥50 movie names,
  ≥2 full series episode listings, ≥2 artist/album/track trees, ≥10 book files; interesting
  cases flagged. No absolute paths, no credentials.
- Verification: manifest loads in a test helper; spot-check 5 entries against the real
  library by eye.

### SM-WI-002 [H] Parser regression corpus
Parameterized tests driving `MovieScanner`/`TvScanner`/`MusicScanner`/`BookScanner`
filename→(title, year, SxxExx, track, issue) parsing from the manifest. Record current
outputs as the expected values (reviewed by the maintainer for obvious mistakes — a wrong
current parse becomes a documented known-issue row, not a silently locked bug).
- Acceptance: every manifest entry asserted; suite green before any other plan item lands.
- Verification: intentionally break one parser regex locally → corpus fails; revert.

### SM-WI-003 [M] Live-verify sandbox builder
Script (`tools/` PowerShell) that builds the §3 sandbox from configured source folders via
copy, never move; emits a README into the sandbox root stating it is disposable.
- Acceptance: re-runnable; produces the §3 slice; total size bounded (sample episodes, not
  whole seasons of large files, where needed).
- Verification: point a scratch server at the sandbox, scan completes, item counts match
  the slice.

---

## 5. Phase 1 — Stop active damage (data integrity)

### SM-WI-010 [H] Scanners must not wipe enriched/locked identity fields (S1)
In `MovieScanner` and `GameScanner` (audit Book/Music/Photo for the same shape):
Year becomes fill-only (`item.Year ??= parsedYear` semantics — a provider/admin value is
never overwritten by a filename parse; the parse only fills a hole). Title/SortTitle
re-stamped only when the file is new or changed AND `!item.MetadataLocked`. `MetadataLocked`
skips ALL identity stamping (Title, SortTitle, Year) while leaving technical analysis
(codec, duration, tracks) untouched — locks protect identity, not file facts.
- Acceptance: tests from the manifest — a yearless real movie filename with an enriched
  Year survives rescan; a Fix-Match-locked title with a "wrong" real filename survives
  rescan; unlocked new file still parses normally. Corpus (SM-WI-002) still green.
- Verification (live, sandbox): enrich a yearless movie, Fix-Match another, rescan twice,
  confirm both survive; check the log shows skipped stamping at Debug.

### SM-WI-011 [H] OMDb: quota accounting for every key mode + limit-error recognition (P1)
`OMDbProvider`: (a) count every request through `OmdbUsageTracker` regardless of key mode
(per-mode daily ceilings: bundled key gets a conservative shared-key ceiling, custom key
the user-configured one); (b) parse the `Error` field of `Response:"False"` bodies —
"Request limit reached", invalid-key, and HTTP 401 map to a new `ProviderUnavailable`
outcome: no `&s=` fallback search, no normal retry ladder; instead a single deferred retry
after the next UTC-midnight quota reset; (c) existing exhaustion notification fires for
bundled mode too.
- Acceptance: unit tests — limit-error response produces zero follow-up requests and a
  next-day-scheduled retry; not-found still runs the fallback search; both key modes
  decrement the tracker.
- Verification (live): with a deliberately exhausted/invalid key configured, enqueue 5
  movies → exactly 5 requests logged, all deferred to next day, one admin notification.

### SM-WI-012 [H] SPARQL escaping in the base client (P4)
Move `ComicWikidataProvider.EscapeForSparql` into `WikidataSparqlClient`; apply in
`BuildEntitySearchSelector` (and any other interpolation point found by audit).
- Acceptance: unit tests with titles containing `"`, `\`, and a real manifest title with
  an apostrophe; generated SPARQL parses (assert no naked quote in the mwapi:search term).
- Verification: enrich a sandbox item renamed to contain a quote — no HTTP 400 in logs.

### SM-WI-013 [H] Watcher writes take the SQLite write lock (S4)
Wrap `BaseMediaScanner.ProcessSingleFileAsync`'s final save and
`LibraryWatcher.MarkFileMissingAsync`'s save in the static `_dbWriteLock`. Additionally:
`ProcessStableFileAsync` defers (re-pends the file) while `IsLibraryInQueue(libraryId)` —
closes both the SQLITE_BUSY window and most of the duplicate-row race with a running scan.
- Acceptance: unit test that the deferral path re-pends rather than processes; existing
  watcher tests green.
- Verification (live, sandbox): drop a new file mid-scan → processed after the scan, no
  SQLITE_BUSY in logs, exactly one row.

### SM-WI-014 [L] Remove the dead `deferImageCaching` parameter (L3)
Its early-return skips MetadataHash stamping — a latent retry-loop trap. Sole caller
passes `false`; delete the parameter and the dead branch.

**Phase-1 exit:** adversarial diff review; suite green; corpus green.

---

## 6. Phase 2 — Rate-limit compliance (§2 is the spec)

### SM-WI-020 [H] Per-host image limiters (P10/M6)
Replace the borrowed-TVMaze image limiter with a host-keyed lookup in
`RateLimiterFactory` (`GetLimiterForHost(Uri)`): `OpenLibraryCovers` 80/5min,
`WikimediaImages` 5/1s, `CoverArtArchive` shared with SM-WI-023's limiter, per-host
`ImageDefault` 10/10s. `ImageCacheService` and `ImageDownloadQueueService` acquire by the
target URL's host. Image-queue concurrency stays 2 global for now (raising it is safe
after per-host limits exist; leave a comment).
- Acceptance: unit tests — covers.openlibrary.org URL acquires from the covers limiter;
  unknown host gets its own default limiter, not a shared one; table in §2 mirrored in
  doc comments with sources.
- Verification (live, sandbox): book-library scan; log/telemetry shows covers requests
  paced under 80/5min while TVMaze episode stills proceed unthrottled by it.

### SM-WI-021 [M] TVMaze: one lease per HTTP request (P9)
Restructure `TVMazeProvider` request helpers so each HTTP call acquires its own lease
(direct-ID, IMDb lookup, detail fetch, search — each leased individually).
- Acceptance: unit test counting lease acquisitions per code path (seam: injectable
  limiter) — equals HTTP request count in all three lookup shapes.

### SM-WI-022 [M] Lease the Fix-Match/search endpoints (P7/M3)
`SearchAsync`/`FetchByCandidateAsync` in MusicBrainz, TVMaze, Wikidata, OpenLibrary
providers acquire the same provider limiter as enrichment paths (OMDb already correct).
- Acceptance: unit tests per provider (same seam as SM-WI-021).
- Verification (live): hammer the Fix-Match search box for a music artist; MusicBrainz
  requests observed ≥1 s apart in the log.

### SM-WI-023 [M] Cover Art Archive: dedicated limiter, pessimistic failure (M4/P8)
CAA HEAD/GET calls acquire the `CoverArtArchive` limiter; 503/timeout/error = "art
unknown" (skip URL, let a later refresh retry) — never "art exists".
- Acceptance: unit test — 503 response does not store a PosterUrl.

### SM-WI-024 [L] Provider client timeouts (L1)
Wikidata/TVMaze/OMDb/MusicBrainz named clients: 30 s timeout (Wikidata aligned to the
60 s WDQS deadline at 30 s client-side). OpenLibrary keeps 15 s.
- Acceptance: config assertions in DI tests if present, else verified by code review in
  the phase diff.

### SM-WI-025 [L] OmdbUsageTracker: batch persistence (L2)
Persist the counter every N=10 requests and on day-rollover/shutdown instead of twice per
request; keep the in-memory count authoritative within a day. Accept ≤N undercount on
crash (safe direction: undercount → conservative next-day budget… note: undercount is
UNSAFE for quota — so persist on a short timer (10 s) instead, choose in-session and
document).
- Acceptance: unit test — 25 requests → ≤4 writes; restart resumes within tolerance.

### SM-WI-026 [M] Image downloads: one delayed retry (M6-part-2)
Transient download failure re-enqueues the request once with a 5-minute delay marker;
second failure logs at Warning with the item title and drops (enrichment refresh remains
the long-term repair).
- Acceptance: unit tests — retry happens once, not twice; gauge (`_pendingByLibrary`)
  stays balanced through the retry.

**Phase-2 exit:** adversarial diff review; suite green. Live spot-check: full sandbox
scan's provider request log audited against §2 — zero limiter violations.

---

## 7. Phase 3 — Match quality

### SM-WI-030 [H] Wikidata movies: year disambiguation + ID-first ordering (P3)
(a) Non-IMDb path fetches top-5 EntitySearch candidates WITH publication year; picks the
candidate matching `item.Year` ±1, else highest-ranked-with-any-year-match, else no-match
(prefer nothing over wrong — the OpenLibrary philosophy). (b) Chain order: the NFO chain
member's ImdbId extraction runs BEFORE the primary provider for movies, so an NFO-supplied
IMDb ID short-circuits title guessing entirely.
- Acceptance: stubbed-response tests using real ambiguous titles from the manifest (plus
  canonical synthetic cases "Dune 1984", "The Thing 2011"); yearless item + multiple
  candidates → no-match, not first-match. NFO-with-ImdbId test asserts zero EntitySearch
  calls.
- Verification (live, sandbox): the remake-ambiguous sandbox movie enriches to the correct
  film (year checked in the UI).

### SM-WI-031 [M] MusicBrainz: MBID-first + score threshold (M1/P5)
(a) `item.MusicBrainzId` set → direct `/artist/{mbid}` / `/release-group/{mbid}` fetch,
no search. (b) Search paths use MB's `score` with a threshold (start 85, constant with a
comment) plus artist-name agreement for release-groups; below threshold → no-match.
- Acceptance: stubbed tests — ID path does one request; low-score search result rejected;
  real artist/album names from the manifest as test data.
- Verification (live, sandbox): re-refresh an already-matched artist → single MB request
  in the log.

### SM-WI-032 [M] OpenLibrary: promoted-ISBN first + persist the OL work key (M2/P6)
(a) `TryIsbnLookupAsync` checks `item.Isbn` before any file parse; extraction only when
the column is empty. (b) **Migration:** `MediaItem.OpenLibraryKey` (string, nullable);
stored on first successful match; refreshes fetch by key.
- Acceptance: stubbed tests — second refresh does zero file I/O (seam on the extractor)
  and fetches by key; migration round-trips.
- Verification (live, sandbox): "All" refresh of the book slice; log shows no EPUB/PDF
  re-parsing for previously matched books.

**Phase-3 exit:** adversarial diff review; suite + corpus green. Migration batched with
Phase 4's (below) if sessions are combined; otherwise applied here.

---

## 8. Phase 4 — Wasted-work elimination

### SM-WI-040 [H] Negative-result cache (P2/H2)
**Migration:** `ProviderLookupCache` table — (Provider, NormalizedQuery) PK, Outcome
(Miss/Error), LastAttemptUtc, AttemptCount. Consulted at the top of each provider's search
path; a fresh miss (< TTL) short-circuits without HTTP. TTL: 30 days for Miss; Error rows
expire in 1 day (transient). ID-based lookups are never cached (IDs don't go stale the
same way). Cache rows are cleared for an item by Fix-Match (the admin is explicitly
overriding) and by the amnesty pass only when past TTL.
- Acceptance: unit tests — repeated enqueue of a no-match title does one provider call
  then zero until TTL; Fix-Match clears; integration test against real SQLite (EF InMemory
  caveat) for the upsert.
- Verification (live, sandbox): rename a file to gibberish, let the ladder exhaust, count
  provider requests across two forced amnesty cycles → second cycle does zero network.

### SM-WI-041 [M] Attempt-complete sentinel for poster-less successes (P12/M8)
Relaxed-mode `NeedsEnrichment` treats "MetadataHash stamped + provider match succeeded +
provider reported no image" as complete (mirror the comic/photo sentinel pattern —
report M8 anchors the policy lines).
- Acceptance: unit test — matched-but-imageless item does not re-enqueue on rescan.

### SM-WI-042 [M] Amnesty decay (P2-part-2)
`MetadataRetryAmnestyService` re-tries exhausted items on a decaying cadence (7 → 14 →
28 days, cap 28) tracked per item (reuse AttemptCount from SM-WI-040's table or a column).
- Acceptance: unit test on the cadence math; amnesty run respects the negative cache.

### SM-WI-043 [M] Probe-attempted sentinel + dead stat removal (S7)
`VideoAnalysisStrategy`: delete the dead mtime re-stat on the scan path; add a
"probe attempted" marker (nullable date column on MediaItem — batch into this phase's
migration) so the BitDepth/FrameRate/Width migration backfill runs once per file, not
forever.
- Acceptance: unit test — a file whose probe yields no BitDepth is not re-probed on the
  next scan; changed file (size/mtime) still re-probes.

### SM-WI-044 [M] Stop discarding computed provider data (P11/M7)
Decision item (§11 Q1): either persist OMDb runtime/awards, TVMaze status, MB artistType,
Game Platform/GameMode as typed nullable columns (this phase's migration) with detail-view
display, or strip them from queries/parsing. Default if no decision: PERSIST TVMaze
`status` (drives the existing "Running series" refresh mode properly — orchestration
finding) and Game Platform/GameMode (already paid for in SPARQL); DROP the OMDb extras
parsing (pure cost, no consumer).
- Acceptance: whichever branch — no computed-then-dropped fields remain (grep-provable);
  client build green if display added.

### SM-WI-045 [L] Small cleanups (L5, L6, orchestration)
RawPayload providerName from the actual provider; enrichment-mode setting read once per
queue batch (or rely on the settings cache measurably); `MetadataRefreshService`
candidates query projects `{Id, Type}` instead of full entities; "Running" refresh mode
filters by the now-persisted series status (if SM-WI-044 persisted it).

**Phase-4 exit:** adversarial diff review; ONE migration for the phase (OpenLibraryKey if
not yet applied, ProviderLookupCache, probe sentinel, SM-WI-044 columns); suite green.

---

## 9. Phase 5 — Scan performance at scale

### SM-WI-050 [H] Bounded batch as the parallel/save unit (S2)
`BaseMediaScanner`: flatten discovery output into batches (~100 files, same-directory
files kept together) as the `Parallel.ForEachAsync` element and the DbContext/save unit.
Striped parent locks and `_dbWriteLock` already make this safe (report S2).
- Acceptance: unit/integration tests — flat-folder fixture (manifest names) scans with >1
  concurrent probe (observable via a seam/counter); cancel mid-scan persists completed
  batches; per-batch save failure loses only that batch. Corpus green.
- Verification (live, sandbox + timing): scan wall-time on the sandbox recorded
  before/after in §10; a mid-scan cancel resumes without re-probing completed files.

### SM-WI-051 [H] Artwork sidecars captured during discovery (S3)
Discovery already enumerates each directory — collect image-extension siblings there and
pass them to `LocalArtworkService` (or memoize per-directory listings for the scan,
`_vaDirectoryCache` pattern). Unchanged files skip the artwork sweep entirely unless the
directory listing changed.
- Acceptance: tests — no `Directory.GetFiles` per media file (seam/counter); sidecar
  poster still found (manifest layouts); unchanged-file rescan does zero artwork listings.

### SM-WI-052 [M] Case-insensitive path lookups via NOCASE index (S6)
**Migration:** index on `Path` with `COLLATE NOCASE` (SQLite); rewrite the four
`ToLower()` comparisons (report S6 anchors) to collation-based equality. Non-ASCII casing
still differs under NOCASE (ASCII-only folding) — document that limitation in the query
comment; correctness is unchanged from today, the win is the index.
- Acceptance: integration test against real SQLite proving the query uses the index
  (EXPLAIN QUERY PLAN contains the index name) and matches case-insensitively.

### SM-WI-053 [M] Batch the post-scan TV artwork sweep (S8)
One query loading all series for the library, one save at the end (per-series try/catch
detaches only the failed entity).
- Acceptance: test — 3-series fixture does 1 query + 1 save (counter seam).

### SM-WI-054 [M] Slim known-files cache projection (S9)
`knownFilesCache` holds `{Id, Path, Size, DateModified, Type, IsMissing}`; full entity
fetched only when an update is actually needed.
- Acceptance: existing scanner tests green; memory spot-check noted in §10.

### SM-WI-055 [L] Small scanner fixes (S10, S11, S13, S14)
GameScanner change-detection + unchanged fast path; per-series (season,episode)→element
dictionary for TVMaze payload lookups; season cache key distinguishes null from 0;
attributes from `DirectoryInfo` enumeration.
- Acceptance: one test each for S10 and S13; S11/S14 covered by corpus + diff review.

### SM-WI-056 [L] Platform-conditional path comparer (S12)
`OrdinalIgnoreCase` only on Windows/macOS; `Ordinal` on Linux. One shared
`PathComparers.Platform` used by cache/seen-set.
- Acceptance: comparer unit test per platform branch (runtime-switchable seam).

**Phase-5 exit:** adversarial diff review; migration applied; suite + corpus green;
before/after sandbox scan timing recorded.

---

## 10. Phase 6 — Watcher gaps

### SM-WI-060 [H] Directory-Created handling (S5)
`LibraryWatcher.OnFileCreated`: if the path is an existing directory, enumerate its media
files into `_pendingFiles` (stability checks apply per file) AND set
`_librariesToScan[libraryId]` as a backstop for deep trees.
- Acceptance: unit test with a mocked event — directory create pends contained media
  files; non-media single file still ignored.
- Verification (live, sandbox): move a completed per-title folder into the watched movie
  sandbox from outside → item appears without a manual scan.

### SM-WI-061 [M] Watcher/scan duplicate-row hardening (S4 secondary)
With SM-WI-013's defer-during-scan in place, add the remaining guard: watcher existence
check re-runs inside the write lock immediately before insert (check-then-add made
atomic with the save).
- Acceptance: interleaving test (scan discovers + watcher imports same real filename) →
  exactly one row survives.

**Phase-6 exit:** adversarial diff review; suite green.

---

## 11. Phase 7 — Cross-type throughput (the original observation)

### SM-WI-070 [M] Split Book and Game out of the Shared metadata channel
`MetadataQueueService`: channels become Music(2), TV(4), Book(3), Game(3),
Shared→Movie/Photo(10). Concurrency numbers sized to each provider's §2 limits (Book:
OpenLibrary 3/s; Game: Wikidata 5/10s shared with movies — see §12 Q2).
- Acceptance: routing unit tests; a queued book item processes while movie backlog is
  deep (test with stub aggregator).
- Verification (live, sandbox): add movie+book libraries together; book metadata counts
  move while movie enrichment is still draining.

### SM-WI-071 [decision — §12 Q3] Concurrent scans for different library types
NOT implemented by default. The sequential scan queue is deliberate (disk contention,
provider pressure, UI model). Revisit only if the SM-WI-050/051 speedups plus SM-WI-070
leave multi-library initial setup unacceptably serial. If approved later: allow at most 2
concurrent scan jobs when library types (and thus provider sets + disk roots) differ;
requires queue, drain-monitor, and progress-UI rework — its own plan.

**Phase-7 exit:** adversarial diff review; suite green.

---

## 12. Phase 8 — Whole-scenario live QA and docs

### SM-WI-080 [H] Full live verification on the sandbox (operator-present)
Fresh DB (or fresh library entries), add Movies + TV + Music + Books sandbox libraries in
one sitting, watch the full convergence:
1. All four types' item counts and metadata progress under the new channel layout
   (SM-WI-070 visible progress on ≥2 types simultaneously).
2. Provider request log audited against §2 — zero violations across the whole run
   (grep per host; MusicBrainz inter-request gaps ≥1 s; covers.openlibrary.org total
   under budget).
3. SM-WI-010 spot-checks: yearless + Fix-Matched items survive a second full scan.
4. Negative-cache: the gibberish item from SM-WI-040 stays silent.
5. Timings vs. the Phase-5 baseline recorded in §10 session log.
- If any 3rd-party 4xx/429/ban response appears, capture it verbatim in §10 and STOP
  enrichment before continuing — evidence first, then fix.

### SM-WI-081 [L] Documentation
`docs/user-guide/`: metadata sources page — each provider, what it's used for, its
official limit, what the server does when quota is exhausted (OMDb next-day defer,
notifications), what Fix-Match + MetadataLocked guarantee (now actually true, SM-WI-010),
and the negative-cache/amnesty behavior ("why isn't this item retrying?").

---

## 13. Open questions (maintainer sign-off; defaults chosen so work can proceed)

- **Q1 (SM-WI-044):** persist vs drop the computed-but-discarded provider fields.
  Default: persist TVMaze status + Game Platform/GameMode; drop OMDb extras parsing.
- **Q2 (SM-WI-070): RESOLVED 2026-07-28 (maintainer)** — libraries sharing a provider
  share that provider's budget: split channels for fairness, but all types flow through
  the single per-provider limiter (§2 shared-budget invariant). Game+Movie both gate on
  the one Wikidata limiter regardless of channel layout.
- **Q3 (SM-WI-071):** concurrent scans across library types. Default: no (see item).
- **Q4 (SM-WI-001):** manifest contains real personal library titles in the repo.
  Default: acceptable (private repo); flag if the repo may go public — then the manifest
  moves to an untracked local file with a checked-in synthetic fallback.

## 14. Status

| Item | Phase | Pri | Finding(s) | Status |
|---|---|---|---|---|
| SM-WI-001 fixture manifest harvester | 0 | H | maintainer req | done (2026-07-28) |
| SM-WI-002 parser regression corpus | 0 | H | maintainer req | done (2026-07-28, 139 cases) |
| SM-WI-003 live-verify sandbox builder | 0 | M | maintainer req | done (2026-07-28) |
| SM-WI-010 scanner identity stamping | 1 | H | S1 | done (2026-07-28) |
| SM-WI-011 OMDb quota all modes + limit errors | 1 | H | P1 | done (2026-07-28) |
| SM-WI-012 SPARQL escaping | 1 | H | P4 | done (2026-07-28) |
| SM-WI-013 watcher write lock + defer | 1 | H | S4 | done (2026-07-28) |
| SM-WI-014 remove deferImageCaching | 1 | L | L3 | done (2026-07-28) |
| SM-WI-020 per-host image limiters | 2 | H | P10/M6 | done (2026-07-28) |
| SM-WI-021 TVMaze lease per request | 2 | M | P9/M5 | done (2026-07-28, + OpenLibrary) |
| SM-WI-022 lease search endpoints | 2 | M | P7/M3 | done (2026-07-28) |
| SM-WI-023 CAA limiter + pessimistic 503 | 2 | M | P8/M4 | done (2026-07-28) |
| SM-WI-024 provider timeouts | 2 | L | L1 | done (2026-07-28) |
| SM-WI-025 OMDb tracker batching | 2 | L | L2 | done (2026-07-28) |
| SM-WI-026 image download retry | 2 | M | P10 | done (2026-07-28) |
| SM-WI-030 Wikidata year disambiguation | 3 | H | P3/H3 | done (2026-07-28) |
| SM-WI-031 MusicBrainz MBID-first + score | 3 | M | P5/M1 | done (2026-07-28) |
| SM-WI-032 OpenLibrary ISBN/key reuse | 3 | M | P6/M2 | done (2026-07-28, migration) |
| SM-WI-040 negative-result cache | 4 | H | P2/H2 | done (2026-07-28) |
| SM-WI-041 poster-less attempt sentinel | 4 | M | P12/M8 | done (2026-07-28) |
| SM-WI-042 amnesty decay | 4 | M | P2 | done (2026-07-28) |
| SM-WI-043 probe sentinel + dead stat | 4 | M | S7 | done (2026-07-28) |
| SM-WI-044 persist-or-drop extras | 4 | M | P11/M7, Q1 | done (2026-07-28) |
| SM-WI-045 small cleanups | 4 | L | L5/L6/orch | done (2026-07-28) |
| SM-WI-050 bounded batch scan unit | 5 | H | S2 | done (2026-07-28) |
| SM-WI-051 artwork via discovery | 5 | H | S3 | done (2026-07-28, listing memo) |
| SM-WI-052 NOCASE path index | 5 | M | S6 | done (2026-07-28, lower() expr index) |
| SM-WI-053 batch TV artwork sweep | 5 | M | S8 | done (2026-07-28) |
| SM-WI-054 slim known-files cache | 5 | M | S9 | deferred (see §15 rationale) |
| SM-WI-055 small scanner fixes | 5 | L | S10/S11/S13/S14 | done (2026-07-28) |
| SM-WI-056 platform path comparer | 5 | L | S12 | done (2026-07-28) |
| SM-WI-060 directory-Created handling | 6 | H | S5 | done (2026-07-28) |
| SM-WI-061 duplicate-row hardening | 6 | M | S4 | done (2026-07-28) |
| SM-WI-070 split Book/Game channels | 7 | M | serialization | done (2026-07-28) |
| SM-WI-071 concurrent scans decision | 7 | — | Q3 | decision: NO (default stands) |
| SM-WI-080 whole-scenario live QA | 8 | H | all | done (2026-07-28, see §15) |
| SM-WI-081 docs | 8 | L | — | done (2026-07-28) |

## 15. Session log

- 2026-07-28 — plan created from `docs/reports/scan-metadata-review-2026-07-27.md`.
  No code changed yet. Rate-limit targets in §2 verified against current
  `RateLimiterFactory` values (TVMaze 18/10s, MusicBrainz 1/1s, Wikidata 5/10s,
  OpenLibrary 3/1s, OMDb 10/10s + custom-mode-only daily tracking, default 10/10s;
  image clients borrow TVMaze).
- 2026-07-28 — maintainer directive: per-provider budgets are shared process-wide across
  all libraries/channels (§2 shared-budget invariant added; Q2 resolved).
- 2026-07-28 — Phase 0 (minus SM-WI-003) + Phase 1 implemented. Suite after: **1918/0/0**.
  - SM-WI-001: harvester at `tools/FixtureHarvester` (console tool referencing the server
    project; NOT in the solution — run manually per its csproj comment). Manifest at
    `src/SoftMedia.Server.Tests/Fixtures/real-library-manifest.json`: 4 libraries, 7/7
    movies (library only has 7 — below the plan's aspirational ≥50, so movie edge cases
    lean on labeled synthetic additions), 3 series (Futurama 178 eps incl. 00x specials
    with Italian titles, Disenchantment, Doctor Who), 2 Anthrax artist trees (incl. 2-CD
    digipack), 30/154 Dune-series books. Fixtures copy wired into the test csproj.
  - SM-WI-002: `FileNameParserCorpusTests` — 139 manifest-driven cases snapshotting
    current ParseMovie/ParseTvEpisode behavior + a four-type coverage check.
  - SM-WI-010: **refinement vs. plan text** — identity (Title/SortTitle/Year) stamps only
    for NEW rows, not "new or changed": existing rows are matched BY PATH, so the parse
    cannot differ from creation time and re-stamping could only revert enrichment/admin
    data. Unlocked existing rows get fill-only Year (`??=`). Movie+Game scanners; TV/comic
    already guarded (report S1); Music/Photo identity comes from tags/EXIF, not filename
    re-stamps. Tests: 4 (Movie, real manifest names) + 2 (Game).
  - SM-WI-011: **refinement vs. plan text** — instead of a new "next-day retry" channel,
    quota/key errors are recognised inside the single request funnel and mark the usage
    tracker exhausted until UTC midnight: ladder retries may still fire but cost ZERO
    HTTP while suspended, and the null return means the `&s=` fallback never runs.
    Bundled-key mode now counts against the free-tier ceiling (1,000/day); 401 and
    "Invalid API key!" get a distinct `omdb_key_invalid` notification. Tests: 5 provider
    (stub HTTP handler, request-count assertions) + IsProviderUnavailableResponse theory
    + 1 tracker (midnight reset, persisted suspension).
  - SM-WI-012: `EscapeForSparql` moved to `WikidataSparqlClient` (public static), applied
    inside `BuildEntitySearchSelector` (callers pass raw titles); comic provider's private
    copy removed; `WikidataCollectionResolver`'s IMDb-ID literal also escaped (its
    prefix-check justification only validated 2 chars; NFO-sourced IDs are untrusted).
    Tests: 6 (incl. real Italian title with apostrophe).
  - SM-WI-013: **refinement vs. plan text** — the watcher defer rule is
    `ShouldDeferForActiveScan` (pure static): defer while a LibraryScan job for the
    library is Queued or Running BEFORE its Metadata stage; a scan draining enrichment
    (potentially hours) does NOT block imports — the walk is done, so neither the
    duplicate-row race nor write interleaving applies. Deferred files re-pend for the
    stability loop. Write lock exposed as `BaseMediaScanner.AcquireDbWriteLockAsync()`
    (public static disposable lease) and applied to the watcher single-file save and
    `MarkFileMissingAsync`. Tests: 3 (decision matrix + re-pend + Metadata-stage
    passthrough).
  - SM-WI-014: `deferImageCaching` removed from IMetadataAggregator/implementation/call
    site; `refreshImages:false` (Variable-mode refresh) remains the one image-suppression
    path. Test renamed/updated accordingly; queue-service mocks updated to 3-arg.
  - Phase-1 exit note: SM-WI-013's duplicate-row check-then-add hardening remains in
    SM-WI-061 (Phase 6) as planned; live sandbox verification of Phase 1 items waits on
    SM-WI-003 (next session's first task).
- 2026-07-28 — SM-WI-003 + Phase 2 implemented. Suite after: **1932/0/0**.
  - SM-WI-003: `tools/New-LiveVerifySandbox.ps1` built `C:\Users\Admin\SoftMedia-LiveVerify`
    (116 files, 19.0 GB): all movies ≤8 GB each (the three ~11 GB Austin Powers BDRips
    are size-capped out; small.soldiers 7.6 GB is the big-file fixture; the loose
    veto-smallsoldiers NFO and the Goldmember trailer ride along as sidecar fixtures;
    loose root videos wrapped into per-title folders), 2 series preferring
    specials-bearing + populated folders (2 eps/season, ≤4 specials), 2 artist trees,
    10 books. Re-runnable, copy-only. Harvester now prints library roots for the
    script's defaults.
  - SM-WI-020: `RateLimiterFactory.GetLimiterForHost(Uri)` + named `OpenLibraryCovers`
    (80/5min), `CoverArtArchive` (1/s), `WikimediaImages` (5/s) limiters; unknown hosts
    get per-host default limiters (the old code collapsed all unknown names into ONE
    shared "default" bucket — fixed as part of this). `RateLimitingDelegatingHandler`
    rewritten to resolve per host from the factory; image client no longer borrows the
    TVMaze limiter. 12 mapping tests incl. the shared-instance invariant.
  - SM-WI-021: TVMaze `GetStringLimitedAsync` — one lease per HTTP request (was one
    lease across up to 3). **Scope addition:** OpenLibrary had the same
    one-lease-per-lookup shape (unflagged by the review) — same fix applied.
  - SM-WI-022: leased funnels now cover MusicBrainz/TVMaze/Wikidata/OpenLibrary
    SearchAsync + FetchByCandidateAsync (Wikidata's via a protected helper on
    WikidataSparqlClient, shared by SPARQL and wbsearchentities — one budget).
  - SM-WI-023: CAA HEAD probes acquire the dedicated CoverArtArchive limiter; 503/queue
    -full/errors now report "art unknown" (no PosterUrl stored) instead of "art exists".
  - SM-WI-024: 30 s timeouts on Wikidata/TVMaze/OMDb/MusicBrainz/Game/Comic/collection-
    resolver clients (OpenLibrary keeps 15 s).
  - SM-WI-025: tracker persists every 10th increment + at limit boundaries +
    MarkExhausted (was 2 writes/request under a global gate). Crash loses ≤9 increments;
    overshoot absorbed by SM-WI-011's limit-error suspension. One pre-existing test
    updated to the new contract (date persisted at next boundary — safe across crashes
    since a stale persisted date rolls over on load).
  - SM-WI-026: transiently-failed image downloads retry ONCE after 5 min (settable for
    tests); second failure gives up with a Warning naming the item. Gauge stays
    balanced (retry re-increments on enqueue). 2 behavior tests.
  - Phase-2 exit note: the live §2-audit (full sandbox scan with per-host request-log
    review) is deliberately deferred to SM-WI-080 — the sandbox exists now, but running
    scans requires the operator-present server session; Phase 3 (match quality) should
    land first so the audit exercises the final call patterns.
- 2026-07-28 — Phase 3 implemented. Suite after: **1949/0/0**. Migration
  `20260728170735_AddOpenLibraryKey` created (applies at next server boot).
  - SM-WI-030(a): non-IMDb Wikidata movie lookups fetch 5 candidates rank-ordered via
    the EntitySearch ordinal (new `?ordinal wikibase:apiOrdinal true` binding in
    BuildEntitySearchSelector + ORDER BY; a new `SelectBinding` hook on
    WikidataSparqlClient defaults to bindings[0] for other subclasses).
    `SelectMovieBinding` rules: no file year OR single candidate → first; ±1 year match
    → highest-ranked such; yearless candidates beat contradicting ones; ALL candidates
    contradicting → no-match. **Refinement vs. plan text:** a SINGLE candidate is kept
    even with a contradicting year — rejecting the only hit over year drift
    (re-releases/director's cuts) would strand obscure titles; ambiguity is what the
    year guards. 6 binding-matrix tests.
  - SM-WI-030(b): MetadataRouter pre-reads the NFO fallback (movies AND TV — same code
    path, TVMaze also has an ImdbId lookup) when it's configured and the item lacks a
    promoted ImdbId; an NFO-supplied id is seeded into item.ImdbId so the primary's
    ID-direct path replaces title guessing. The pre-read is REUSED as the fallback data
    (NFO parsed at most once per pass). **Contract change:** two chain tests that
    asserted "sufficient primary → fallback never invoked" now assert AtMostOnce — the
    pre-read is free (local file) and the primary's result still wins. 3 new router
    tests.
  - SM-WI-031: MusicBrainz MBID-first direct fetches for artists
    (/ws/2/artist/{mbid}) and release-groups (/ws/2/release-group/{mbid}) — one leased
    request per refresh instead of a Lucene search under the 1/s budget; matched MBIDs
    now ride MetadataResult.MusicBrainzId (aggregator already promoted it) so the loop
    closes on first match. Search paths gate on MB's Lucene score
    (MinSearchScore = 85; sub-threshold → unmatched, "prefer nothing over wrong") and
    release-groups additionally require artist-credit agreement (relaxed
    equality/containment) when artist context exists — score-ordered candidates are
    walked, so a same-titled cover-band album is skipped at zero request cost. Both
    fetch paths also moved off the held-outer-lease shape onto GetStringLimitedAsync.
    5 stub-HTTP tests (real Anthrax names).
  - SM-WI-032: MediaItem.OpenLibraryKey + MetadataResult.OpenLibraryKey (promoted by
    the aggregator; stamped by search hits, ISBN hits, and Fix-Match fetches).
    Refreshes go key-first via search.json?q=key:"…" — same doc shape as every other
    path, so the one parser serves all (the raw /works endpoint is thinner and would
    downgrade data). TryIsbnLookupAsync consults the promoted Isbn column before ANY
    file parsing (strict-mock-verified). 3 tests (real Dune names). NOTE: MetadataHash
    input deliberately NOT extended with OpenLibraryKey this session — extending it
    would flip every item to "changed" on the next refresh; revisit with SM-WI-041's
    policy work where hash semantics are already in scope.
- 2026-07-28 — Phase 4 implemented. Suite after: **1963/0/0**. Migration
  `Phase4WastedWorkElimination` (ProviderLookupCache table + 6 MediaItem columns:
  AmnestyCount, NextAmnestyUtc, LastProbeAttemptUtc, SeriesStatus, GamePlatform,
  GameMode); applies at next boot together with AddOpenLibraryKey.
  - SM-WI-040: **deviation vs. plan text** — only DEFINITIVE misses are cached (30-day
    TTL, upsert bumps AttemptCount); the planned Error/1-day outcome was dropped
    because it would defeat the 1m/5m/30m/4h ladder that exists precisely for
    transients. Fix-Match clearing also dropped: rows are query-keyed and a Fix-Matched
    item is locked (no automatic lookups ever again) — an admin UNLOCK may wait out the
    TTL, documented trade-off. Integrated in 5 providers: WikidataSparqlClient base
    (opt-in via BuildLookupCacheKey — WikidataProvider + GameMetadataProvider opt in;
    comics keep their EMPTY-hash sentinel), TVMaze (search fallback; 404/empty/
    no-suitable-match record), OMDb (title flow; provider-unavailable nulls from the
    SM-WI-011 funnel are NOT recorded), OpenLibrary (separate ISBN-key and search-key
    entries; low-confidence rejects record — deterministic for the same query),
    MusicBrainz (artist + release-group; score-rejects and artist-mismatch exhaustion
    record). Singleton service, own DB scopes, cache failures never block enrichment.
    Tests: 6 service tests against REAL SQLite (:memory: — EF InMemory would hide the
    upsert translation) + 1 end-to-end TVMaze zero-network test.
  - SM-WI-041: relaxed-mode NeedsEnrichment is now `!hasPoster && hash empty` — an
    attempted-but-imageless item is complete; strict mode deliberately keeps retrying
    (explicit opt-in). One pre-existing policy test asserted the retry-forever contract
    and was inverted with rationale.
  - SM-WI-042: NextAmnestyDelay = min(28d, 7·2^(count+1)) → 14/28/28…; RunAmnestyAsync
    only grants due items (NextAmnestyUtc null/past), stamps the decay on grant
    (missing items exempt — they consume no quota), and a hash-changing enrichment in
    MetadataQueueService resets count+schedule. 2 new tests + decay theory.
  - SM-WI-043: dead per-file GetLastWriteTimeUtc stat deleted from Missing mode
    (scanners own change detection and stamp DateModified pre-analysis); Phase-2
    backfill gated on LastProbeAttemptUtc == null, stamped on every ATTEMPT; Full mode
    unaffected. 2 tests.
  - SM-WI-044 (Q1 default executed): TVMaze status → typed MetadataResult.SeriesStatus
    → MediaItem.SeriesStatus (series rows); Game Platform/GameMode → MediaItem columns;
    OMDb runtime/writer/awards/boxOffice parsing REMOVED; MB artistType Extra REMOVED.
    No client UI yet (columns are queryable; display is future scope).
  - SM-WI-045: RawPayload cache row labeled via MetadataResult.SourceProvider (TVMaze
    sets it; fallback keeps old rows readable); MetadataRefreshService projects
    {Id,Type,Title} instead of full entities, and "Running" mode now filters on the
    persisted SeriesStatus (null/Running/In Development pass — unknown status refreshes
    to learn it). L6 (per-item enrichment-mode read) intentionally SKIPPED: the
    settings service caches reads at 60 s TTL, so the per-item cost is a dictionary
    hit; noted as no-op.
- 2026-07-28 — Phase 5 implemented. Suite after: **1969/0/0** (SM-WI-002 corpus green
  through the whole scanner refactor — the tripwire held). Migration
  `Phase5PathLowerIndex` (raw SQL expression index; three migrations now pending at
  next boot).
  - SM-WI-050: `BuildScanBatches` (public static, pure) flattens discovery into
    ≤100-file batches — big directories split into consecutive chunks, small ones pack
    together, directory files never interleave. `Parallel.ForEachAsync` runs over
    batches; per-batch scope/save/deferred-enqueue unchanged. A flat 10k-file folder is
    now ~100 parallel work items with checkpoint saves instead of one. One pre-existing
    test asserting ≥12 scopes (per-directory unit) re-anchored to the batch model.
    3 invariant tests.
  - SM-WI-051: **refinement vs. plan text** — implemented as a per-scan directory-
    listing MEMO (`GetCachedDirectoryListing` on the base scanner, cleared each scan)
    passed into `ApplyLocalArtworkAsync` as an optional `listDirectory` seam, rather
    than capturing sidecars during discovery: discovery only enumerates media-extension
    files, and widening it would have plumbed sidecar lists through every scanner's
    ProcessFileAsync. Effect is the same — one listing per directory per scan (flat
    folders drop from O(N²) to O(N)); watcher/single-file paths keep live listings.
    Movie + TV-sweep call sites wired. 1 seam-proof test.
  - SM-WI-052: **refinement vs. plan text** — a `lower("Path")` EXPRESSION index
    (raw-SQL migration) instead of the planned COLLATE NOCASE rewrite: the existing
    `Path.ToLower()` queries already translate to `lower("Path") = @p` (index hit,
    verified by an EXPLAIN QUERY PLAN test against real SQLite), whereas
    EF.Functions.Collate cannot be evaluated by the InMemory provider the unit suite
    uses. ASCII-only folding limitation unchanged either way (documented at the sites).
    Query code untouched — zero regression surface.
  - SM-WI-053: TV artwork sweep batched — one query for all series, one save when
    anything changed; per-series failures detach the poisoned entity instead of
    clearing the whole tracker.
  - SM-WI-054: **DEFERRED, decision documented** — the slim-projection stub-attach
    approach risks silent wrong-reads for ANY current-or-future column omitted from
    the projection (exactly the data-loss bug class S1 was: this phase itself added 6
    columns), and the operator's library is 1,248 items — the memory win is
    hypothetical at this scale. Revisit only on real memory pressure, with a
    projection type the compiler forces to stay in sync.
  - SM-WI-055: GameScanner change detection + unchanged fast path (S10 — two
    pre-existing tests updated to seed Size/DateModified); per-series
    (season,episode)→element index kills the O(E²) TVMaze payload scans, with
    ValueKind guards the old code lacked (TVMaze specials carry "number": null — the
    linear scan would have thrown) (S11); null-SeasonNumber seasons key as -1 instead
    of colliding with Specials (S13, read path unaffected — episode lookups always
    carry concrete numbers); discovery reads directory attributes from the enumeration
    instead of an extra stat per directory (S14).
  - SM-WI-056: `PathComparers.Platform` (Ordinal on Linux, OrdinalIgnoreCase
    elsewhere) applied to the scan's path cache + seen-set. The striped parent-lock
    hash keeps OrdinalIgnoreCase (stripe collisions are benign).
- 2026-07-28 — Phases 6 + 7 implemented. Suite after: **1973/0/0**.
  - SM-WI-060: `OnFileCreated` (now protected for test access) handles a created
    DIRECTORY by pending every contained media file recursively (each gets the normal
    stability checks) AND scheduling a library scan as backstop; enumeration failures
    fall back to the scan alone. Tests: nested per-title folder (real Disenchantment
    release name) pends both episodes and ignores the txt; non-media single files
    still ignored.
  - SM-WI-061: `ProcessSingleFileAsync` re-checks for a same-path row INSIDE the write
    lock before saving a New result; a concurrent writer's committed row wins and the
    duplicate's Added entities are detached (previously the loser twin was purged by
    the next scan along with its user data). The narrow written-after-recheck window
    is backstopped by the partial unique Path index (loud failure, not silent dup).
    Test: two concurrent imports of the same new path → exactly one row, whatever the
    interleaving. TestMediaScanner gained an opt-in CreateRealItems mode for this.
  - SM-WI-070: metadata channels are now Music(2)/TV(4)/Book(3)/Game(3)/Shared(10 —
    Movie+Photo). Worker counts sized to each family's §2 budget; the §2 shared-budget
    invariant note lives at the channel construction site (Game+Movie still gate on
    the ONE Wikidata limiter — channels are fairness, limiters are rate). Test: 12
    blocked movies saturating Shared while a book (real Dune name) processes on its
    own channel within 5 s.
  - SM-WI-071: decision recorded — NO concurrent scans (plan default stands). With
    SM-WI-050's batch parallelism, SM-WI-070's channel fairness, and the sequential
    queue's data-safety properties, cross-library scan concurrency buys little and
    costs a queue/drain/UI redesign.
  - SM-WI-081 done: `docs/user-guide/metadata-sources.md` — provider/limit table, OMDb
    quota-exhaustion behavior, matching accuracy (ID-first/year/threshold), Fix-Match
    lock guarantee (now actually true post-SM-WI-010), negative-cache + amnesty
    explanation ("why isn't this item retrying?").
  - SM-WI-080 EXECUTED 2026-07-28 (operator running the server; admin credentials
    provided for the session; QA driven via the API against 127.0.0.1:5011).
    **PLAN COMPLETE** (SM-WI-054 deferred by documented decision). Results:
    1. All three migrations applied at boot (log 18:47:33–34).
    2. Four QA libraries (QA-Movies/TV/Music/Books → the LiveVerify sandbox) created
       and scanned back-to-back; all four types walked AND fully enriched within the
       first 20 s poll (111 files; embedded music tags absorb most MB work).
    3. §2 request-log audit over the QA window: ZERO local limiter rejections, zero
       429/"limit reached" lines. Per-host counts tiny vs. budgets (covers.openlibrary
       9/80·5min; MB 8 req ~1/s; CAA 9 probes; TVMaze 2 API calls — Futurama resolved
       search+detail; WDQS calls were the movie COLLECTION resolver, this install runs
       OMDb as movie primary). Two boundary-adjacency findings (60 ms MB pair, 682 ms
       CAA pair — SegmentsPerWindow=1 degenerates to fixed windows) → FIXED post-audit:
       MB + CAA limiters now slide with 4 segments. Takes effect at next server start.
    4. Enrichment quality on real files: all 3 yearless movies got correct Years +
       IMDb ids (A Boy and His Dog→1975, A Christmas Without Snow→1980, A Star is
       Born→2018 by notability, no file year to contradict); small.soldiers→1998 +
       tt0122718. All 10 books carry OpenLibraryKey + Isbn (one legitimately
       poster-less → complete under SM-WI-041, no loop). Both artists + all 6 albums
       carry MBIDs with album art. Futurama: SeriesStatus=Running, TvMazeId=538.
       Probe sentinel stamped on all 28 probed items.
    5. Negative cache LIVE in production already: ~60 definitive-miss rows from the
       REAL libraries' post-boot activity (OpenLibrary-unindexed comics, junk ISBNs,
       "unknown album|unknown artist"), every one AttemptCount=1.
    6. Gibberish test: "Zxqv Qplwm Nonexistent Film 1937" → rescan 1 cost exactly 2
       OMDb calls (t= + s= miss) and one cache row; rescan 2 cost ZERO provider
       requests across all hosts, AttemptCount stayed 1; the 4 real movies generated
       zero traffic on both rescans and their Years survived unchanged.
    7. Fix-Match lock survival live-check skipped (unit-covered by SM-WI-010 tests);
       QA-* libraries + the gibberish sandbox folder left in place for the operator to
       inspect/delete.
    8. Full-suite rerun after the limiter tweak blocked by the running server's bin
       lock (known gotcha) — run `dotnet test src/SoftMedia.Server.Tests` after the
       next server stop; the change reuses the exact option shape of the other
       sliding-window arms.
