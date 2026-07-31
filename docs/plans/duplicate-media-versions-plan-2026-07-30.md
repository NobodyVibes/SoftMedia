# Duplicate Media & Version Groups Plan — 2026-07-30

> **STATUS: ALL SESSIONS COMPLETE (2026-07-31). DV-WI-001..024 shipped and
> LIVE-VERIFIED.** Session 6 ran the §7 checklist against the real dev server with
> synthetic ffmpeg fixtures (movie in 1080p+4K, 4-episode series with duplicated E03)
> in scratch libraries: scan grouped both pairs (movie via post-scan pass, episode via
> deterministic id); grid showed ONE card (vc=2, "4K", fronted by 2160); episode list 4
> rows with E3 collapsed; season count 4; detail Versions=2 primary 4K; marking E3
> watched via the 720p copy watched both rows; the series completed and left Continue
> Watching; both half-watched movie copies held ONE CW slot; simulated version-switch
> beats re-keyed the single play row to the 4K file; finishing retired the movie;
> duplicates report listed both QA groups; split → 5 episode rows, merge → 4. Boot
> backfill also grouped 26 REAL duplicate groups in the operator's library. Chromecast
> interactive check skipped (no device; switcher-hidden-while-casting is code-pinned).
> Scratch libraries/user/fixtures fully cleaned up; AllowUserSignup left Disabled.
>
> **POST-SHIP FIX (2026-07-31):** VersionLabelHelper.ResolutionLabel is now
> WIDTH-AWARE (thresholds mirror the client's MediaQualityInfo panel). Height-only
> mapping under-labeled every cinemascope encode by a tier (1920×816 scope 1080p read
> "720p" — operator-reported on a real 2.35:1 title). Known remaining nicety, accepted:
> VersionPrimaryRule still ranks by raw Height; for copies of ONE title aspect ratios
> match so ordering is unaffected — only a pathological cross-aspect duplicate pair
> (≥2.7:1 scope 1080p vs 16:9 720p) could front the wrong copy.
>
> **UX REVISION ×2 (2026-07-31, owner request):** versions UI settled on a SPLIT PLAY
> BUTTON (`PlayVersionMenu`) after two iterations — (1) the standalone VersionsSection
> list, then (2) a dropdown on the header quality badge, which the owner found too easy
> to miss. Final form: with multiple copies a chevron segment joins the primary Play
> button and opens the "play this version" menu (label chip, Default marker, size,
> watched tick, admin prefer-star); single-file titles keep the plain full-width Play,
> and the header badge is a pure indicator again. Survey rationale recorded: Jellyfin
> exposes a labeled Version select (high discoverability), Plex hides "Play Version…"
> behind an overflow menu because it auto-picks (SoftMedia deliberately doesn't, so the
> choice must be visible at the play decision); Netflix/Amazon expose no versions at
> all (ABR). Also fixed en route: the header badge was gated on the retired `quality`
> DTO field and had silently stopped rendering. Menu rows are real button[role=menuitem]
> elements with the admin star as a SIBLING (the repo's a11y guard rejects div-onClick,
> and nesting buttons is invalid HTML).
>
> **UX ADDITION (2026-07-31, owner request, revised same day):** pre-play version
> INSPECTION drives PLAY — in MediaQualityInfo (the specs strip under the genres) the
> "Video:" VALUE is the version dropdown; picking a copy fetches that sibling item
> (`GET /media/{id}`, cached 60s) and the whole panel (codec, bit depth, fps, audio
> incl. Atmos, bitrate, track dropdowns) shows that file's probed metadata. The pick
> is CONTROLLED state owned by MediaDetailPage: the main Play (and Play-from-beginning)
> then plays the inspected version ("play what you're looking at", movies only — TV
> plays via the next-episode resolver); the split-Play chevron remains the explicit
> per-press override. EPISODES (owner follow-up, same day): selecting an episode now
> fetches its FULL detail — collapsed list rows carry versionCount but NOT versions[],
> only GET /media/{id} hydrates them — so the dropdown appears for episodes too; the
> pick is page-owned and, while set, the series Play button plays that exact file
> instead of the next-episode resolver (cleared on episode switch). No server changes:
> sibling items already carry full metadata.
> This file is retained as design history.
>
> **SESSION 1 COMPLETE (2026-07-30) — DV-WI-001..006 shipped.** Layer 1
> hardening landed: next/prev navigation skips same-(S,E) siblings with a deterministic
> `.ThenBy(Id)` order; completion/first-unwatched are group-keyed; season counts are
> distinct (null/0 episode numbers still count per-file); Continue Watching collapses
> duplicate movies on an interim (library, normalized title, year) key with
> newest-copy-decides semantics; MarkWatched fans IsWatched out to sibling episode rows
> (positions/history stay per-file; movies deferred to DV-WI-014); detection fingerprints
> one row per episode with same-duration siblings inheriting through the chapter-source
> guards; DLNA suffixes duplicate episode titles. Tests: 5 resolver, 4 fan-out,
> 3 continue-watching, 3 detection, 1 DLNA (SQLite), 3 count.
>
> **SESSION 2 COMPLETE (2026-07-30) — DV-WI-010..013 shipped.** Migration
> `AddVersionGroups` (VersionGroupId Guid? indexed + PreferredVersion bool);
> `VersionGroupHelper` (deterministic MD5 episode id, NormalizeTitleKey — punctuation/case
> only, extra WORDS distinguish — and AreSameMovie with provider veto); static
> `VersionGroupAssigner` (fill-only: per-file movie join, library-wide GroupMoviesAsync
> with provider-conflict split, episode backfill); TvScanner stamps deterministic ids
> (recomputes only when the parsed S/E identity moves), MovieScanner assigns per-file +
> post-scan convergence pass; `VersionGroupBackfillService` idempotent boot sweep. Admin:
> POST /admin/versions/merge + /split, GET /admin/versions/duplicates; client
> DuplicateVersionsCard (own query key; split-only UI — a merge affordance needs item
> pickers and lands with the Session 4 detail page). DTO: dead `Quality` removed;
> VersionGroupId/VersionLabel added to every mapping (`VersionLabelHelper` = the single
> label authority, DLNA delegates to it); GET /media/{id} hydrates VersionCount +
> Versions[] with the computed primary.
>
> **SESSION 3 COMPLETE (2026-07-30) — DV-WI-014..016 shipped.** `VersionPrimaryRule` =
> the one primary-version rule in BOTH forms (in-memory OrderPrimaryFirst + translatable
> OnePerVersionGroup correlated-subquery filter; final tiebreaker is PATH, not Id — Guid
> comparison doesn't translate; keep the two forms aligned). Collapsed to one-per-group:
> library grid (LibraryRepository, before count/sort/page so pagination is exact, with
> batched VersionCount stamping via IMediaRepository.GetVersionCountsAsync), episode list
> (LibraryService.GetSeriesEpisodesAsync — group watched=any, resume=latest-played copy,
> VersionCount), global search (BuildSearchMatchQuery), post-play genre suggestions.
> Interactions: fan-out generalized to VersionGroupId (movies included; falls back to the
> episode natural key ONLY for EpisodeNumber>0) and extended to rating (+ per-sibling
> InternalRating recompute), favorite, watchlist (shared WatchlistedAt); empty sibling
> rows are GC'd. ReconcileGroupWatchedAsync (any-watched wins, existing rows only,
> chunked ≤999 params) runs in the boot backfill and after admin merge. CW keys movies by
> VersionGroupId (title+year fallback); most-watched merges group plays in memory after
> the SQL aggregate; post-play marathon list dedupes order-preserving. NOT collapsed
> (deliberate): hero/recently-added rows (a new copy of an old title is genuinely the
> recent thing), DLNA (labeled duplicates by design), smart playlists (Session 4 call).
>
> **SESSION 4 COMPLETE (2026-07-31) — DV-WI-020..022 shipped.** `VersionsSection` mounts
> unconditionally in MediaDetailLayout (self-hides <2 versions): per-copy label chip,
> Default marker (computed primary), container/size, watched tick, per-version Play
> (`/play/{versionId}`), and an admin star pinning PreferredVersion via new
> POST /admin/versions/prefer (clears the sibling's claim — at most one per group).
> Labels unified (DV-WI-021): QualityBadge is now label-driven (renders server
> versionLabel verbatim), MediaCard's badge stays VISIBLE when versionCount>1
> (hover-only otherwise), episode-row badges prefer versionLabel with the old
> height heuristic as never-probed fallback. Series header honest (DV-WI-022):
> GET /media/{id} hydrates a series-level aggregate (best height/width, HDR-any,
> matching label) and the client's representative-episode sampling is deleted
> (clicking an episode still overrides quality info). Search-result version suffix
> (B12) became moot — search collapses to one result per group since Session 3.
> Smart playlists left uncollapsed (they enumerate files by design; revisit on
> feedback).
>
> **SESSION 5 COMPLETE (2026-07-31) — DV-WI-023/024 shipped.** Player: "Version"
> submenu in the More-options menu (beside Quality, which remains the transcode
> ladder), listing the item's copies by label with the current one highlighted.
> Switching saves the live position against the TARGET id (server resume state — the
> fresh mount resumes through the normal progress flow), navigates
> `/play/{id}` with replace:true (back-nav rule), and starts editions with a >5%
> runtime delta from 0. Hidden while casting (cast token is item-locked).
> VersionDto gained DurationSeconds for the comparability check. History
> correctness: RecordPlaybackHistoryAsync re-keys a sibling copy's open play to the
> new file when the beat continues it (same RewatchRestartFraction rule as
> same-item beats), moving PlayCount with the row — one sitting stays ONE play and
> the "PlayCount == history rows" invariant holds on both items; a from-the-top
> edition restart correctly opens its own play. Session 6 (full-suite + LiveVerify
> end-to-end QA per §7) pending.
>
> Source: two-agent audit of 2026-07-30 (server + client sweeps) confirming SoftMedia has
> NO version/variant concept anywhere — every media FILE is an independent `MediaItem`
> row, and duplicate copies of the same logical title (1080p + 4K, different language,
> accidental re-download) break autoplay, series completion, watched state, counts, and
> Continue Watching, while the player's "quality" menu cannot reach the other copy at all.

## §1 Problem statement

Two files of the same TV episode produce two Episode rows sharing
(SeriesId, SeasonNumber, EpisodeNumber); two files of the same movie produce two fully
independent Movie items (scanner identity is file **path** only — the partial unique
index `IX_MediaItems_Path_UniqueFileBacked` enforces one row per path and nothing else).
Duplicates are a legitimate state (quality variants, language variants, accidental
copies) and must be first-class, not an error.

Confirmed breakage today (file refs verified 2026-07-30):

| # | Symptom | Where |
|---|---------|-------|
| B1 | Autoplay/next serves the SAME episode again (the duplicate row), non-deterministically | `RecommendationService.cs:179-191` (`episodes[currentIndex + 1]`), ordering from `MediaRepository.cs:271-287` (no tiebreaker) |
| B2 | `IsSeriesComplete` can never become true; show stuck in Continue Watching forever | `RecommendationService.cs:100-143`, consumed `ContinueWatchingService.cs:136-137` |
| B3 | Watched/progress/rating/favorite are per-file; the other copy always reads unwatched | `AppDbContext.cs:155-156` (PK = UserId+MediaItemId), `InteractionController.cs:114-118` (no fan-out) |
| B4 | Two half-watched copies of one movie occupy two Continue Watching slots | `ContinueWatchingService.cs:147-161` (movie branch dedupes nothing) |
| B5 | Season/series episode counts inflate (11/10) | `MediaRepository.cs:82-90`, `LibraryService.cs:388,419`; client `TVDetailView.tsx:718,768` |
| B6 | Episode list renders identical "E3" twice (same title/still/aria-label); jump-to-episode reaches only the first | `TVDetailView.tsx:377-408` (append, never merge), `:678` (findIndex) |
| B7 | Movie grid: two indistinguishable cards; quality badge is hover-only; no cross-link between the two detail pages | `VirtualMediaGrid.tsx:194-198`, `MediaCard.tsx:177-178`, `MovieDetailView.tsx` (no versions section) |
| B8 | Player quality menu is a hardcoded downscale ladder for the already-open file; picking "4K" on the 1080p copy silently no-ops | `VideoPlayer.tsx:2571-2584`, `TranscodeProfileBuilder.cs:709-715` (`min(...,iw)` never upscales) |
| B9 | Series header quality info sampled from an arbitrary duplicate of S01E01 | `TVDetailView.tsx:536-548` |
| B10 | No duplicate detection anywhere: no admin report, no scan warning | `AdminController.cs` (audited: none) |
| B11 | DLNA lists the episode twice; intro/credits detection fingerprints it twice (wasted CPU) | `DlnaContentDirectory.cs:156`, `IntroCreditsDetectionService.cs:83,152` |
| B12 | Search results for duplicate episodes/movies are byte-identical | `GlobalSearchResults.tsx:15-31` |
| B13 | Badge/label inconsistency: `FHD` vs `1080p` vs `HD`; `MediaItemDto.Quality` is dead (never assigned server-side) | `TVDetailView.tsx:71-96`, `QualityBadge.tsx`, `MediaQualityInfo.tsx:61-137`, `MediaItemDto.cs:65` |

Already fixed (2026-07-30, do not redo): duplicate episode rows share the cached still
(`ImageDownloadQueueService` updates ALL matching rows; `TvMetadataEnricher` no longer
clobbers `/cache/` stills). Regression tests exist.

## §2 Design: two layers, not one

**Layer 1 — logical-identity hardening (Sessions 1):** fix the outright bugs (B1–B5)
using natural keys only, no schema change. Ships value immediately and stays correct
even if Layer 2 slips. Episodes have a natural key (SeriesId, SeasonNumber,
EpisodeNumber); movies get interim watched-state parity only where safely inferable.

**Layer 2 — first-class Version Groups (Sessions 2–5):** an explicit
`VersionGroupId` on `MediaItem` groups all copies of one logical title. One card/row
per group everywhere; a Versions UI on detail pages; a real version switcher in the
player. This is the Plex/Jellyfin-style feature the 1080p/4K use case actually needs.

### §2.1 Identity rules (what makes two files "the same title")

- **Episode:** same (SeriesId, SeasonNumber, EpisodeNumber). Automatic, no heuristics.
- **Movie:** same library + same normalized title (casefold, strip punctuation/articles
  via existing `MediaStringHelpers.GetSortTitle`) + same year (±0; null year matches
  concrete year only if no better candidate — mirror `TvScanner.TryGetCachedSeries`
  wildcard semantics). Heuristic, therefore **admin-overridable** (merge/split,
  DV-WI-011). Provider IDs (`TvMazeId`/`OmdbId` etc.), when both rows have one, are a
  stronger signal and win over the title heuristic (equal → group; different → never).
- **Editions are versions too** ("Director's Cut", "Extended"): they group with the
  theatrical copy but carry the edition token in their version label. Rationale:
  watched-state should NOT be shared per-edition is arguable, but splitting them
  creates worse UX (two cards again); the label keeps them distinguishable. Revisit
  only if users complain.
- **Language variants** group; label carries the primary audio language.

### §2.2 Version label & primary-version rule

- **Label** derived server-side once per item (not stored; computed in DTO mapping):
  `{2160p|1080p|…}` from `Height` (single authority, DV-WI-021) + ` {HDR-format}` +
  ` {edition token}` (parsed from filename: `directors|extended|theatrical|unrated|remastered|imax`)
  + ` {audio lang}` when it differs across the group. Fallback when unprobed: container.
- **Primary version** (the row a group is represented by): deterministic computed rule —
  max `Height`, then `HdrFormat != null`, then max `Bitrate`, then newest `DateAdded`,
  then smallest `Id` (total order ⇒ stable). Optional explicit override column
  `PreferredVersion` (bool, admin/user-set via detail page) beats the rule. NEVER store
  the computed choice — computing at query time cannot drift when files come and go.
- **Watched state at group level:** group-watched = ANY version watched; group progress =
  the interaction row with the latest `LastPlayed`. Writes FAN OUT: marking watched (or
  the 95%/credits auto-complete) stamps all sibling rows (§DV-WI-005) so every existing
  per-row consumer (`RecommendationService`, DLNA, sort-by-played) stays correct without
  rewriting their queries.

### §2.3 Explicitly out of scope

- Cross-library grouping (same movie in two libraries stays two groups — ACLs differ).
- Merging PLAY HISTORY rows across versions (history is per-file; only state fans out).
- Automatic deletion/"keep best" of duplicates (admin report only, DV-WI-012; deleting
  user files is a different risk class).
- ABR/quality-ladder changes — the transcode ladder is correct for its job; version
  switching is a separate control (DV-WI-019).
- **Plex-style automatic capability-aware version pick — REJECTED by owner 2026-07-30.**
  Playback always uses the primary version (or the user's explicit pick); delivery cost
  is governed by the EXISTING resolution/bitrate limits (server `MaxTranscodeResolution`,
  per-user `MaxStreamBitrateKbps`, client default quality / Data Saver). Do not add
  sibling-substitution logic to `StreamPlanService`.

## §3 Schema & migration

One migration (`AddVersionGroups`):
- `MediaItems.VersionGroupId` — `Guid?`, indexed (non-unique). Null = ungrouped
  (containers, photos, tracks — grouping applies to Movie/Episode only for now).
- `MediaItems.PreferredVersion` — `bool`, default false (explicit primary override).
- Backfill runs as a startup/one-shot maintenance pass (DV-WI-010), NOT inside the
  migration (needs normalization + provider-ID logic; keep SQL migrations dumb).

## §4 Work items

### Session 1 — Layer 1 hardening (no schema)

**DV-WI-001 — Next/previous episode must skip same-(S,E) siblings.**
- Fix `RecommendationService.GetNextEpisodeFromCurrentAsync` / previous / forward-scan:
  advance past rows whose (SeasonNumber, EpisodeNumber) equal the current row's; add a
  deterministic tiebreaker to `MediaRepository.GetEpisodesAsync` ordering
  (`.ThenBy(m => m.Id)`) so duplicate traversal is stable.
- Verify: unit tests — dup S01E03 (watch 1080p → next is S01E04, prev from S01E04 lands
  on ONE S01E03 deterministically); wrap-scan with dup at season end.

**DV-WI-002 — Series completion & next-episode selection are distinct-based.**
- `IsSeriesComplete` and the "first unwatched" forward scan treat an episode complete if
  ANY row with that (S,E) is complete.
- Verify: dup episode, one copy watched → series completes; Continue Watching drops it.

**DV-WI-003 — Distinct episode counts.**
- `MediaRepository.GetEpisodeCountAsync` (+ the series-level count in `LibraryService`)
  count distinct (SeasonNumber, EpisodeNumber). EF/SQLite: `GroupBy(...).Count()` or
  `Select(new{S,E}).Distinct().Count()` — verify translation on SQLite, NOT InMemory
  (EF-InMemory memory note: controller unit tests evaluate client-side and hide
  untranslatable queries; use the SQLite in-memory harness).
- Verify: integration test 10 episodes + 1 dup → 10.

**DV-WI-004 — Continue Watching: collapse duplicate movies.**
- Movie branch of `ContinueWatchingService`: group candidates by the movie identity rule
  (§2.1 — interim: normalized title + year within library) and keep the row with the
  latest `LastPlayed`. (TV already collapses per-series; its resume target is fixed by
  DV-WI-001/002.)
- Verify: two half-watched copies → one slot, resuming the most recent.

**DV-WI-005 — Watched/progress fan-out for episodes.**
- `InteractionService.MarkWatchedAsync` and the auto-complete path (the ~10s tail-beat
  absorption — see play-history completion cascade memory: test the ABOVE-threshold tail
  path) also stamp `IsWatched` on sibling rows sharing (SeriesId, S, E). Progress
  position does NOT fan out in Layer 1 (files differ in duration only trivially, but
  transcode/HLS offsets make blind copying risky; group-level resume arrives with Layer 2
  read-side aggregation, DV-WI-014).
- Movies: skipped in Layer 1 (no trustworthy key until VersionGroupId exists).
- Verify: mark 1080p watched → 4K row watched; unwatch fans out too; rating/favorite do
  NOT fan out yet (deliberate — they follow in DV-WI-014 with groups).

**DV-WI-006 — Intro/credits detection + DLNA dedupe (cheap wins).**
- `IntroCreditsDetectionService`: skip rows whose (SeriesId,S,E) already has markers this
  sweep (fingerprint once, copy markers to siblings — timings are per-file, so only copy
  when Duration within ~2s; else fingerprint both). `DlnaContentDirectory`: keep both
  entries but suffix the version label (DLNA renderers can't picker; two labeled entries
  is correct there).
- Verify: unit test — dup episode triggers one fingerprint when durations match.

### Session 2 — Version Groups: schema, scanner, backfill, admin

**DV-WI-010 — `VersionGroupId` migration + assignment service + backfill.**
- Migration per §3. New `VersionGroupService`: `AssignGroupAsync(MediaItem)` implementing
  §2.1 (episodes: derived from (SeriesId,S,E) — deterministic GUID v5-style hash of the
  triple so parallel scanner workers converge without coordination; movies: lookup by
  provider ID → title+year heuristic, minting a new GUID when alone). Called from
  `MovieScanner`/`TvScanner.ProcessFileAsync` for new items AND when S/E numbers change;
  one-shot backfill pass over existing rows at startup (idempotent, logged, guarded by a
  `SystemState` flag like other one-shots). `dotnet ef` gotcha: explicit
  `--project src/SoftMedia.Server --startup-project src/SoftMedia.Server`.
- Verify: scanner integration tests (dup episode two workers → same group; two movies
  same title+year → same group; same title different year → different groups; provider-ID
  conflict → different groups); backfill test over seeded legacy rows.

**DV-WI-011 — Admin merge/split endpoints.**
- `POST /api/admin/versions/merge {itemIds[]}` (assign one group) and
  `POST /api/admin/versions/split {itemId}` (mint fresh group). Guard: same library, same
  Type. Audit-logged like other admin actions.
- Verify: controller tests incl. cross-library rejection.

**DV-WI-012 — Admin duplicates report.**
- `GET /api/admin/versions/duplicates`: groups with >1 member, with per-member path,
  resolution label, size, watched-by counts. Admin UI card (Settings → Libraries area)
  listing them with merge/split buttons — heads-up: an admin card invalidating
  `['settings']` clobbers unsaved SettingsPage edits (settings-draft memory note); this
  card must use its own query key.
- Verify: endpoint test; client card renders groups (RTL test).

**DV-WI-013 — Retire `MediaItemDto.Quality`; add `VersionInfo`.**
- Delete the dead `Quality` field. Add to `MediaItemDto`: `VersionGroupId`,
  `VersionLabel` (§2.2), `VersionCount`, and (detail-DTO only) `Versions[]` =
  {id, label, width, height, hdrFormat, bitrate, container, sizeBytes, isPrimary,
  watched, progress}. Label computation is ONE server-side helper (also exported to the
  client via DTO — client stops deriving labels three different ways, DV-WI-021).
- Verify: DTO mapping tests; client compiles (`npm run build` — the ONLY client type
  gate, root tsc checks nothing).

### Session 3 — Server read-paths become group-aware

**DV-WI-014 — Group-level interactions.**
- Read-side: episode/movie DTO `watched`/`progress` resolve at group level (any-watched;
  latest-LastPlayed progress row). Write-side: extend DV-WI-005 fan-out to movies via
  VersionGroupId, and include rating/favorite/watchlist (write to all siblings — keeps
  every existing per-row query correct).
- Verify: interaction integration tests across a movie group; sort-by-most-played
  aggregates group plays once (TV grids already roll up to series — extend the movie
  ranking to group).

**DV-WI-015 — Browse/episode listings return one row per group.**
- `GetEpisodesAsync` consumers that feed UI lists (`/libraries/series/{id}/episodes`,
  browse/grid queries in `BrowseService`, search) collapse to the primary version
  (§2.2 rule + `PreferredVersion` override), carrying `VersionCount`/`Versions[]`.
  Order-on-entity-BEFORE-projecting (EF translation memory note). Non-UI consumers
  (scan, detection, DLNA) keep seeing all rows.
- Verify: SQLite integration tests — grid shows one card per movie group; episode list
  one row per (S,E); `GetNextEpisodeAsync` still consistent with the collapsed list.

**DV-WI-016 — Continue Watching + post-play + recommendations on groups.**
- Replace DV-WI-004's interim title heuristic with VersionGroupId; post-play
  "unfinished from collection" and "most watched" dedupe by group.
- Verify: extend Session 1 tests to group-keyed paths.

### Session 4 — Client UX

**DV-WI-020 — Detail pages: Versions section + version-aware play.**
- `MovieDetailView`/episode rows in `TVDetailView`: when `versionCount > 1`, show a
  Versions block (label, size, codec/HDR chips, per-version watched tick) with
  play-per-version and a "prefer this version" toggle (writes `PreferredVersion` via
  DV-WI-011-adjacent endpoint). Primary Play button plays the primary version at the
  group resume position. Episode list is single-row per (S,E) (server does it —
  DV-WI-015); the row's badge shows the BEST label of the group.
- Verify: RTL tests for the versions block, `npm run build` green.

**DV-WI-021 — One quality-label system.**
- Single shared label util fed by DTO `VersionLabel` (kill the `FHD`/`1080p`/`HD`
  three-way drift: `TVDetailView.getResolutionBadge`, `QualityBadge`, parts of
  `MediaQualityInfo`). Card badge becomes always-visible when `versionCount > 1`
  (it is the differentiator; hover-only defeats it).
- Verify: RTL snapshot of the three surfaces using one label source.

**DV-WI-022 — Series header quality + counts from the group/series aggregate.**
- Replace the arbitrary representative-episode sample (`TVDetailView.tsx:536-548`) with
  a server-provided series-level aggregate (max resolution across episodes, HDR-any);
  season "N episodes" uses the distinct count (already fixed server-side, DV-WI-003 —
  client just consumes it). Search result subtitles append the version label when a
  result is a non-primary duplicate (B12).
- Verify: RTL — mixed 1080p/4K series header reads 4K deterministically.

### Session 5 — Player version switching

**DV-WI-023 — Version switcher in the player.**
- New "Version" submenu next to Quality in `VideoPlayer` (options = `Versions[]` from the
  detail DTO / a `GET /api/v1/media/{id}/versions` endpoint), shown only when
  `versionCount > 1`. Switching: capture current position → stop stream → start playback
  of the sibling item id → seek to position (positions are wall-clock comparable across
  versions of the same cut; editions with materially different durations (>5% delta)
  switch from 0 with a toast). Quality menu is unchanged (still the transcode ladder for
  the CURRENT file). Back-navigation must still resolve via `backNavigation.ts`
  (hierarchical back memory note) — the swap must not push history entries.
- Verify: player integration tests (mock plan endpoint); manual live QA with a real
  1080p+4K pair (see §7).

**DV-WI-024 — Session/interaction correctness on switch.**
- Progress beats during/after a switch attribute to the NEW item id; the completion
  cascade must not double-count the same sitting as two plays (tail beats on the old id
  absorb into its completed row — align with play-history completion cascade memory).
  Chromecast/DLNA: switcher hidden (cast token is locked to a single media item's stream
  routes — artwork-auth memory note); the cast sender keeps playing the version it
  started.
- Verify: unit tests on the beat path across an id swap; cast session smoke.

### Session 6 — Verification & docs

- Full suites: server green, client green, `npm run build`.
- LiveVerify sandbox (fixtures memory note: per-title folders; ffmpeg at
  `src/SoftMedia.Server/ffmpeg-bin/`): add a duplicate-pair fixture (same episode twice
  at different resolutions via ffmpeg scale, same movie twice) and script the checks:
  scan → one episode row shown with versionCount 2 → autoplay skips → watch one → both
  watched → series completes → duplicates report lists the pair → player switch works.
- CHANGELOG entries (user-facing: "Multiple copies of the same movie/episode are now
  grouped as versions…"); update this plan's STATUS; memory note update.

## §5 Risks & open questions

- **Movie heuristic false-positives** (two genuinely different films, same title+year,
  no provider IDs): mitigated by provider-ID veto + admin split + the duplicates report
  making groupings visible. Accept residual risk; split is one click.
- **Fan-out writes vs. read-aggregation:** fan-out chosen to keep dozens of existing
  per-row queries correct. Cost: sibling rows' interactions are denormalized. The
  invariant "all siblings agree on IsWatched" must hold after merge/split too — merge
  triggers a reconciliation pass (any-watched wins).
- **Backfill on large libraries:** one-shot pass is O(items); title normalization in
  memory, no per-row queries (bulk load like `TvScanner.ScanLibraryAsync` does).
- **Watcher single-file path** (Sonarr-style imports) must assign groups too — it
  bypasses the full-scan preload (SR-WI-030 precedent); DV-WI-010's service must not
  assume warm caches.
- Open: should marking an EDITION watched fan out to the theatrical cut? Current answer
  yes (§2.1); flag for user feedback.

## §6 Standing constraints (apply to every session)

- EF InMemory hides translation bugs — integration-test all new LINQ on SQLite; order on
  the entity before projecting.
- Client type gate is `npm run build` only.
- Backend running locks `bin/` — stop it before `dotnet build`/`ef`.
- Vite/curl must target `127.0.0.1:5011` (IPv6 proxy stall).
- 404-over-403 anti-probe rule for any new endpoint.
- New admin cards: own query keys; never invalidate `['settings']` blind.

## §7 Live QA checklist (final gate, real server + real files)

1. Library with movie in 1080p+4K and series with one duplicated episode; scan.
2. Grid: one movie card, badge visible; TV: one E3 row, "10 episodes".
3. Play 1080p movie halfway → one Continue Watching slot; resume opens primary.
4. Player: Version menu lists both; switch mid-play resumes position in 4K.
5. Finish episode via 1080p copy → next episode is E4; both E3 rows watched; finish
   series → drops from Continue Watching.
6. Admin duplicates report shows both groups; split → two cards; merge → one again.
7. Chromecast: version menu hidden, playback unaffected.

## §8 Suggested sequencing

| Session | Items | Theme |
|---|---|---|
| 1 | DV-WI-001..006 | Layer 1 hardening — fixes B1–B5, B11 with no schema risk |
| 2 | DV-WI-010..013 | Groups exist: schema, scanner, backfill, admin merge/split/report |
| 3 | DV-WI-014..016 | Server read/write paths group-aware |
| 4 | DV-WI-020..022 | Client UX: versions UI, one badge system, honest series header |
| 5 | DV-WI-023..024 | Player version switching + interaction correctness |
| 6 | — | Full-suite + LiveVerify + docs/changelog |

Session 1 is independently shippable and should land even if Layer 2 is deferred.
