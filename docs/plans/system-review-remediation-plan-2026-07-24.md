# System-Review Remediation Plan — 2026-07-24

**Input:** `docs/reports/system-review-2026-07-23.md` (five-agent code-verified whole-system
review). This plan converts its findings into ordered, verifiable work items.

**Relationship to other plans:** `docs/plans/native-app-readiness-plan-2026-07-21.md` has only
Session 5 (operator-present live QA + v1.0.0 tag) and deferred Phase C (Docker) remaining. This
plan slots **before** that Session 5: the v1.0.0 tag should not ship with known data-loss and
playback-stall defects. See §6 Q2 for the exact gate.

**How to work this plan (session protocol):** at the start of any session, read §7 (status
table) and §8 (session log), work the assigned phase only, run the verification steps listed on
each item, and update §7/§8 in the same commit as the work. Suite baselines at plan creation:
server `dotnet test src/SoftMedia.Server.Tests` 1303/0/0 (~60s), client `npm run test` 262/262,
client types via `npm run build` ONLY (root tsc checks nothing — solution-style tsconfig). Stop
the dev server before building (bin lock). Live verification against `127.0.0.1:5011` (IPv4 —
IPv6 localhost stalls ~210ms/request).

---

## 1. Conventions

- IDs: `SR-WI-###`. Severity from the source report is noted as [H]/[M]/[L-batch].
- Every item lists: scope → acceptance criteria → verification. "Live verify" means against a
  running server with synthetic fixtures (ffmpeg at `src/SoftMedia.Server/ffmpeg-bin/`; recipes
  in the review report and prior plans).
- Regression rule: any behavior change to scanners or transcoding must add/adjust tests in the
  same commit; net suite count must not decrease.
- Review rule: each phase ends with an adversarial diff review (the July waves repeatedly proved
  happy-path-only verification insufficient — see verified-gaps memory lessons).

---

## 2. Phase 0 — Preconditions & hygiene (start of Session A)

### SR-WI-001 [H] Resolve the ExtrasService working-tree regression — **gated on §6 Q1**
The uncommitted `ExtrasService.cs`/`ExtrasServiceTests.cs` edit removes boundary-prefix
matching, breaking `Film-trailer.mkv` ↔ `Film.1080p.BDRip.mkv` pairing and re-enabling
`Aliens-trailer` → `Alien` misattachment.
- Default (if maintainer doesn't object): `git checkout --` both files, restoring the committed
  behavior and its 32 test lines.
- If the edit was deliberate: state the intended matching rule, re-implement the two lost
  guarantees under it, and re-cover with tests.
- Acceptance: both scenarios above covered by passing tests. Verification: extras tests green;
  manual fixture folder with `Film.1080p.BDRip.mkv` + `Film-trailer.mkv` + `Aliens-trailer.mkv`
  shows exactly one, correctly-attached trailer.

### SR-WI-002 [L] Git debris purge
`git rm --cached` + delete: `DumpDune.cs`, `dune_search.json`, `debug_args.txt`, `test.jpg`,
`test.txt`, `grep.exe.stackdump`, `TempVerify/`, `src/TempDebug/`, `temp-net-test/`,
`TempITunes/`, `src/SoftMedia.Server/probe_output.json`; extend `.gitignore` accordingly.
Also remove dead client files: `src/components/ui/ProgressBar.tsx`, `src/lib/sampleData.ts`,
`src/App.css`, `src/assets/react.svg`; replace the `vite.svg` favicon.
- Acceptance: `git ls-files` shows none of the above; client build green with no unused-import
  fallout. (No history purge in this item — that remains the maintainer-gated R-WI-001 remainder.)

---

## 3. Phase 1 — Data safety (Session A) — **the "never lose user data" phase**

### SR-WI-010 [H] Mass-purge circuit breaker (LIB-H1, fast guard)
In `CleanupOrphansAsync`: before deleting, compute the orphan fraction against the library's
known item count. If it exceeds a threshold (default 25%, admin-configurable setting
`Scanning:MaxPurgeFraction`) **and** the absolute count exceeds a floor (default 20 items),
abort the cleanup, mark the scan Completed-with-warning, raise an admin notification (existing
notification bell + log Error) explaining the likely cause (unavailable mount) and how to force
(a per-library "force cleanup" action requiring explicit admin confirmation).
- Also treat an empty enumeration of a previously non-empty root as automatic abort regardless
  of threshold.
- Acceptance: simulated empty-root scan on a seeded library deletes nothing, warns, and a forced
  re-run deletes. Unit tests for threshold/floor/force paths. Verification: live — point a
  library at an empty dir after seeding, scan, confirm zero deletions + warning surfaced.

### SR-WI-011 [H] Soft-delete lifecycle (`IsMissing`) with heal-on-reappear (LIB-H1 root fix)
Add `MediaItem.IsMissing` (+ migration + index). Orphan cleanup marks items missing instead of
deleting; missing items are excluded from browse/search/home/DLNA/continue-watching surfaces but
retain all child rows (history, interactions, bookmarks, playlists). A scan that re-finds the
path clears the flag. Hard-delete only after a retention window (default 30 days, setting) or
via explicit admin action; hard-delete still cascades as today.
- Sweep required: every catalog query surface must filter `!IsMissing` (browse, search, hero,
  recommendations, DLNA, playlists render as "unavailable" rather than vanishing).
- Acceptance: unmount→scan→remount→scan round-trip preserves watch history and playlist
  membership end-to-end (integration test). Verification: live round-trip on a seeded library.
- Note: SR-WI-010 stays even after this lands (defense in depth; the breaker becomes the guard
  against mass *missing*-marking noise).

### SR-WI-012 [H] Rename/move reconciliation (LIB-H4)
Before marking items missing (post-011), attempt to re-bind: match orphaned rows to newly
discovered files by (size, mtime) — unique match required; fall back to same-filename match
within the library. On match, update `Path` in place, preserving identity and children.
- Acceptance: file rename, folder rename, and cross-folder move within a library all preserve
  item GUID + watch state (tests). Casing-only rename on Windows handled (fix the
  BINARY-collated lookup in `ProcessSingleFileAsync` to match the scan's OrdinalIgnoreCase in
  the same commit). Verification: live rename of a show folder mid-session.

### SR-WI-013 [M] Watcher robustness + scheduled-scan default (LIB-M3, watcher part of LIB-H4) — **§6 Q4**
- FileSystemWatcher `Error` → enqueue a scan of the affected library (not just a log line).
- Raise `InternalBufferSize` to 64 KB.
- Handle directory rename/delete events: enqueue a library scan (reconciliation from SR-WI-012
  makes this safe) instead of ignoring them.
- Default `LibraryScanIntervalHours` to 12 on fresh installs (existing installs keep their
  stored value); settings description updated. Also fix the `EnableFileWatcher` description to
  state "takes effect after restart" (boot-only behavior, matching `EnableDlna` honesty).
- Acceptance: unit tests for error→scan and dir-rename→scan; fresh-install seed shows 12.

---

## 4. Phase 2 — Playback resilience (Session B)

### SR-WI-020 [H] Session revival + real crash retry (STR-H1)
- In `StartTranscodeAsync`'s existing-session branch: if `Process` is null/exited and the
  playlist lacks `#EXT-X-ENDLIST` coverage of the requested position, restart ffmpeg from the
  last produced segment boundary (reuse the far-seek restart machinery), preserving the session
  key and negotiated plan.
- `/resume` and segment/playlist requests on a Dormant session trigger the same revival.
- Wire `CrashRetryCount`/`MaxCrashRetries` for real: increment on abnormal exit
  (non-zero exit code before ENDLIST), stop retrying past the cap, and mark the session Failed
  (consumed by SR-WI-026's client signal). Stop labeling crashes "completed".
- Acceptance: unit tests for revive-on-request, revive-on-resume, retry cap. Live verify:
  pause a transcoded synthetic clip >3 min (or lower the dormancy threshold for the test),
  resume, confirm playback continues past the old buffer; `taskkill` the ffmpeg mid-stream and
  confirm automatic restart. **Adversarial case to cover: revival must take the per-key session
  lock (interacts with SR-WI-025) and must not fight the far-seek restart path.**

### SR-WI-021 [H] ffmpeg lifecycle on host shutdown (STR-H4)
Hosted service whose `StopAsync` iterates `GetAllSessions()` and kills+disposes every live
process (and trickplay/subtitle extraction children via a registered-process registry). On
Windows, put transcode children in a Job Object with kill-on-close so a hard host crash still
reaps them; guard with OS checks (no Linux behavior change; PDEATHSIG noted for the Docker
phase).
- Acceptance: integration test that a started (fake/ffmpeg -f lavfi) session's process is dead
  after host `StopAsync`. Live verify: start transcode, stop server normally, `tasklist` shows
  no ffmpeg; startup temp purge now succeeds.

### SR-WI-022 [H] Subtitle extraction parity + cache (STR-H3, STR-M6-subs)
- Route sidecar VTT extraction through `RunProcessForExitCodeAsync` with the burn-in path's
  10-minute timeout; require exit 0; delete partial output on failure (mirror R-WI-012's fix).
- Cache extracted subtitles per (mediaId, trackIndex) outside session dirs (invalidate on file
  mtime change); far-seek restarts and new sessions reuse instead of re-demuxing the container.
- Acceptance: unit tests (failure deletes partial; cache hit on second session). Live verify:
  large synthetic MKV (padded), extraction completes with correct full-length VTT; second seek
  serves cached file (log evidence).

### SR-WI-023 [H] Software tone mapping + color metadata (STR-H2) — **§6 Q3**
- Non-NVIDIA HDR→SDR: insert `zscale=t=linear:npl=100,tonemap=hable,zscale=p=bt709:t=bt709:m=bt709,format=yuv420p`
  (parameterized by the existing tone-map-operator setting) before encode.
- Emit `-color_primaries/-color_trc/-colorspace` on ALL outputs (SDR bt709; passthrough carries
  source values).
- PreserveHDR + h264 combination corrected (no 8-bit squash of PQ content: force tone-map or
  hevc/10-bit as negotiated).
- Debug `toneMapped` computed from the actual pipeline (fixes the mislabel; pairs with SR-WI-024).
- Performance note: software tone mapping is CPU-heavy; log a one-time warning with a pointer
  to the hardware-accel setting when it engages.
- Acceptance: profile-builder unit tests for nvidia/none/intel/amd × HDR/SDR matrix. Live
  verify: synthetic HDR (PQ) clip transcoded with `HardwareAcceleration=none` shows tonemap
  filter in args and visually normal output; ffprobe of output shows bt709 tags.

### SR-WI-024 [M] Debug endpoint sid threading (STR-M3)
Pass `sid` through `TranscodeController` → `TranscodeDebugService`; build the session key with
StreamId. Acceptance: debug panel reports the real session for a sid-keyed transcode (live
check + unit test).

### SR-WI-025 [M] Stop-path locking (STR-M1)
`StopTranscode`/`EnterDormantState` acquire the per-key session lock; alternatively (client
side) far-seek awaits the DELETE before swapping src (navigateEpisode already does). Do both —
server lock is the correctness fix, client ordering removes the race window.
- Acceptance: concurrency test — DELETE racing a restart never kills the successor session.

### SR-WI-026 [M] Player error surfacing (STR-M4, part of CLI errors)
- Plan POST failure: 429 → "Server is busy (N streams active) — try again shortly" honoring
  Retry-After; 5xx → visible error with retry button (no more eternal "Loading player...").
- Cap fatal-network `startLoad()` retries (e.g. 6 attempts/30 s) then show "Connection to server
  lost" with a retry action; show a transient "reconnecting…" indicator while retrying.
- Server: a session marked Failed (SR-WI-020) returns a distinct status (e.g. 409 + code) on
  playlist/segment fetch; client maps it to "Transcoding failed on the server" + diagnostics
  pointer.
- Acceptance: client tests for each branch; live verify by killing ffmpeg with retries exhausted.

### SR-WI-027 [M] HDR capability detection fix (STR-M5)
`displaySupportsHdr` requires `(video-dynamic-range: high)`; drop the `(color-gamut: p3)`
fallback. Acceptance: unit test of the hook logic; manual check on an SDR laptop reports
supportsHdr=false → HDR sources now tone-mapped (works because of SR-WI-023).

### SR-WI-028 [L-batch] Streaming low batch
Numeric trickplay sheet sort (or `%03d` naming + migration of existing sheets); trickplay
cancellation kill + timeout; HLS manifest line-based URI rewrite (replace the blind
`.Replace(".ts",…)`); capacity-check TOCTOU under a lock; null-plan path applies the ≥1000
bitrate floor and codec validation; direct-play cap gate covers unknown-bitrate/non-movie video;
disk-pressure check pauses live ffmpeg below the threshold (reuse throttle suspend), not just
dormant eviction; replace the fixed 3 s startup delay with manifest-existence polling (200 ms
interval, 15 s cap).
- Acceptance: each has a unit test; suite green.

---

## 5. Phase 3 — Scan & metadata correctness (Session C)

### SR-WI-030 [H] Parent dedup on single-file imports (LIB-H2)
`EnsureSeriesAsync`/`EnsureSeasonAsync`/`EnsureArtistAsync`/`EnsureAlbumAsync`: on cache miss,
query the DB before creating (pattern already proven in `BookScanner.EnsureComicSeriesAsync`).
Drain `_seriesNeedingEnrichment` at the end of `ProcessSingleFileAsync` so watcher-created
parents get metadata immediately.
- Acceptance: watcher-import integration test — second episode of an existing series via
  `ProcessSingleFileAsync` reuses the series row and enqueues enrichment. Live verify with a
  copy-into-watched-folder.

### SR-WI-031 [H] Stop clobbering cached episode stills (LIB-H3)
`PopulateEpisodeMetadata` sets `BackdropUrl` only when empty or not already a `/cache/` path.
- Acceptance: rescan test — enriched episode keeps its local still. Sweep for the same pattern
  on any other provider-populated image field.

### SR-WI-032 [H] Year-parser pattern order (LIB-H5)
Parenthesized/bracketed year wins over bare trailing year; when both exist, the parenthesized
one is the year and the other stays in the title.
- Acceptance: table-driven tests: `Blade Runner 2049 (2017)`, `Wonder Woman 1984 (2020)`,
  `Death Race 2000 (1975)`, `2001 A Space Odyssey (1968)`, plus existing cases unchanged.

### SR-WI-033 [M] TV parsing gaps (LIB-M1)
Episode digits `\d{1,3}`; multi-episode patterns (`S01E01E02`, `S01E01-E02` → primary episode +
span recorded in title suffix for now); `Specials`/`Season 00` folders → season 0; bracket-prefix
(anime) release-group stripping before matching; directory parser walks one extra level for the
show name when the immediate parent is a season-like folder.
- Acceptance: table-driven parser tests for each family; no "Episode 0" from the fixture set.

### SR-WI-034 [M] Series identity includes year (LIB-M2)
Key series cache and DB lookup by (CleanTitle, Year-when-known); `Doctor Who (1963)` vs `(2005)`
stay separate; migration-safe (existing single-title rows keep working; no automatic split).

### SR-WI-035 [M] Scanner write-lock coverage (LIB-M4 / API-L)
Route parent-creation `SaveChangesAsync` calls through `_dbWriteLock`. Additionally assert
SQLite pragmas at connection open (`journal_mode=WAL`, `busy_timeout=30000`,
`synchronous=NORMAL`) via a connection interceptor (covers the API-review pragma gap).

### SR-WI-036 [M] Metadata retry + refresh coverage (LIB-M5, LIB-M7)
Raise `MaxRetries` so the 4 h backoff tier is reachable; clear `IsRetryExhausted` on manual
fix-match/refresh and via a weekly amnesty task; extend the global Metadata Refresh task to
music/books/comics/games (respecting `MetadataLocked` and provider rate limits); add a per-item
refresh endpoint (admin) — the Fix Match UI gains a "Refresh metadata" action.

### SR-WI-037 [M] Image cache lifecycle (LIB-M6)
Wire `CleanupOrphanedImages` into the scan Finishing stage (post-011 it must treat *missing*
items' art as retained until hard-delete); add the `books` directory; add cache-refresh
invalidation used by the per-item refresh (SR-WI-036).

### SR-WI-038 [M/L-batch] Library batch
"Various Artists" convention for VA compilations lacking AlbumArtist (LIB-M9); junk-word list
extension (common release tags) + bracket-group stripping (LIB-L); skipped-file (quote/control
chars) admin surface — reuse the watcher-issues dashboard list (LIB-L); single-file delete →
targeted item removal incl. cached-art delete instead of full library scan (LIB-L).

---

## 6. Phase 4 — Client shell & performance (Session D, first half)

### SR-WI-040 [H] Responsive mobile shell (CLI-H1)
Breakpoint-driven layout: below `md`, sidebar becomes an overlay drawer (hamburger already
exists in TopBar), `<main>` margin drops, TopBar logo block collapses; library grid switches to
`repeat(auto-fill, minmax(110px, 1fr))`-style adaptive sizing with reduced page padding; player
control bar wraps/priorities at narrow widths (secondary controls into an overflow menu).
- Acceptance: Playwright/manual pass at 375×812 and 768×1024 — nav usable, grid ≥3 columns on
  phones, no horizontal scroll; desktop unchanged pixel-wise at ≥1024.

### SR-WI-041 [H] Route-level code splitting (CLI-H2)
`React.lazy` + `Suspense` for ReaderPage (epubjs/react-pdf), PlayerPage (hls.js), SettingsPage,
and admin-only pages; Cast SDK script becomes lazy-injected on first cast-capable playback
(CLI-L). Keep the PWA precache list coherent (drop the 5 MB `maximumFileSizeToCacheInBytes`
override if no chunk needs it).
- Acceptance: `npm run build` shows initial chunk ≤ ~1 MB raw (goal: ~350 KB gzip); login page
  network trace loads neither epubjs nor hls.js; all routes still render (client tests green).

### SR-WI-042 [M] Library grid virtualization (CLI-M10)
Virtualize the LibraryPage grid with the already-present `@tanstack/react-virtual` (row-based
virtualization over the CSS grid). Acceptance: 10k-item seeded library scrolls at 60fps-ish with
bounded DOM node count (manual perf check + node-count assertion in a test).

---

## 7. Phase 5 — Client bug & UX batch (Session D, second half)

### SR-WI-050 [M] Bug batch
WatchlistButton invalidates `['media', mediaId]` (kills the revert-after-save bug);
TopBar admin link → `/settings/admin`; remove-or-wire the four dead user-menu items (default:
remove "Report Issues"/"Help"/"Switch User", wire "View Profile" to the settings account page);
port the photo Share copy-link to the general detail page (or hide the button);
`ConfirmationModal` backdrop `bg-black/50`.
- Acceptance: client tests for the watchlist invalidation and admin route; visual check of the
  backdrop.

### SR-WI-051 [M] Shared Modal primitive + a11y batch (CLI-M5 + lows)
One `Modal` component (role=dialog, aria-modal, focus trap, Escape, focus-return — pattern from
the reader drawers) adopted by the six offending modals; Combobox gets combobox/listbox ARIA +
arrow-key selection; TopBar icon buttons get aria-labels; MediaDetailLayout sidebar buttons get
aria-labels (copy the photo variant); micro-text contrast pass (`text-gray-500`/9-10px →
`text-gray-400`/11px minimum on dark surfaces).
- Acceptance: a11yGuards test extended to assert dialog semantics on all modals.

### SR-WI-052 [M] Error-state batch (CLI-M6 + lows)
Home rows render an inline page-level error + retry when any row query errors (distinguish
`isError` from loading); PersistentPlayer `<audio>` gets `onError` → toast + auto-advance;
detail/library/player error strings get retry actions and 401/404/offline differentiation;
ProtectedRoute forced-logout carries `state.from` + shows "session expired" on the login page;
LibraryPage `handleRescan` failure surfaces a toast.

### SR-WI-053 [M] Resume / start-over choice (CLI-M7)
Detail-page Play button becomes split/menu when a resume position exists: "Resume from H:MM" /
"Play from beginning" (`?start=0` plumbing already exists). Album Play disabled-with-spinner
until tracks resolve (CLI-L).

### SR-WI-054 [decision] i18n gate — **§6 Q5**
Default: de-scope l10n for 1.0; record it in this plan + the readiness plan. If GO instead:
string extraction becomes its own pre-native session (not part of this plan's estimates).

*(Design-system drift (CLI-M8) is deliberately NOT a 1.0 item: 451 call sites is a mechanical
but wide sweep — schedule as a standalone post-1.0 chore with visual-regression screenshots.)*

---

## 8. Phase 6 — API contract & ops prep (Session E)

### SR-WI-060 [M] UTC DateTime JSON converter (API-M1)
System.Text.Json converter stamping `DateTimeKind.Utc` on unspecified kinds at serialization
(and parsing tolerant on input). Acceptance: integration test asserting every DateTime in
representative DTO responses ends with `Z`/offset; client displays unchanged (it already
assumed UTC where it mattered — verify the few `new Date()` call sites).

### SR-WI-061 [M] ProblemDetails everywhere (API-M2)
`AddProblemDetails` + global exception handler (no empty-body 500s); migrate manual `{error}` /
`{message}` returns to ProblemDetails; the must-change-password middleware emits the same shape
(keep its distinguishing `code`). Document the envelope in `docs/api/native-client-onboarding.md`.
- Compatibility sweep: client `catch` paths that read `error`/`message` fields updated in the
  same commit. Acceptance: integration tests for an unhandled-exception route + a validation
  failure + a manual 4xx, all one shape.

### SR-WI-062 [M] Index migration (API-M3)
`MediaItems`: `(LibraryId, Type)`, `(Type, DateAdded)`, `Title`, `Year`, unique `Path`
(pre-migration dedup guard: verify no duplicate Paths exist; the scan lock work makes new dupes
impossible). `UserMediaInteractions`: `(UserId, LastPlayed)`. EF migration via
`dotnet ef` with explicit `--project/--startup-project` (memory gotcha).
- Acceptance: migration applies on a copy of the live DB; browse/continue-watching query plans
  use the indexes (EXPLAIN QUERY PLAN spot-check).

### SR-WI-063 [M] DTO hygiene (API-M4)
Remove `Path` from `MediaItemDto` for non-admin callers (admin surfaces that need it get an
admin DTO or conditional field); keep `Metadata` but document its keys and freeze additions
(full typing deferred — breaking); drop the string `Duration` in favor of `DurationSeconds`
(client sweep for usages in the same commit).
- Acceptance: client builds/tests green; API docs regenerated.

### SR-WI-064 [M] Persistent log sink (API-M5)
Rolling file sink (simplest adequate: a custom `ILoggerProvider` writing daily files, 7-day
retention, Warning+ by default, level independent of the ring buffer; respects the T6.6
Hosting.Diagnostics pin — NEVER remove that pin). Log directory under `data/logs`, surfaced on
the Server & Network page next to the log viewer.
- Acceptance: unit test of rotation/retention; live verify a Warning appears in today's file.

### SR-WI-065 [L-batch] API/ops batch
`GetRecentMediaAsync` rewritten as SQL-side rollup or slim projection (API-M7); backup directory
resolved against ContentRoot not CWD; pagination conventions documented for native clients
(API-M6); stray local DBs/artifacts in the server project root deleted (local hygiene, they're
already gitignored); non-Production `C:\TestMedia` seed gated behind Development only.

---

## 9. Phase 7 — Post-1.0 competitive roadmap (outline only — not scheduled by this plan)

Ordered by leverage per the review's §6 comparison. Each needs its own mini-plan when picked up.
1. **External subtitle sidecars** (`.srt`/`.ass` next to media) + OpenSubtitles provider
   (readiness-plan Q4 said post-1.0) — the single most-cited adoption blocker.
2. **VAAPI** hardware transcode (prerequisite for credible Linux/Docker story) → then
   **Docker/Linux packaging** (reactivate native-app plan Phase C as written).
3. **TMDB/TVDB optional providers** + metadata language setting + NFO write-back.
4. **Multi-part stacking & version grouping** (LIB-M8).
5. **ABR ladder** (multi-rendition HLS) — large; design doc first.
6. **OPDS** (cheap win given the book stack), **audiobooks** (m4b, chapterized), **SyncPlay**,
   **smart playlists**, **lyrics**, server analytics dashboard, PIN/kid profile switcher,
   design-system sweep (CLI-M8), AirPlay.

---

## 10. §6 Maintainer decisions — **ALL RESOLVED 2026-07-24** (Q1/Q2/Q3/Q5 answered by maintainer; Q4/Q6 defaults accepted unopposed)

| # | Question | Decision | Rationale |
|---|---|---|---|
| Q1 | ExtrasService working-tree edit | **REVERT — DONE 2026-07-24** | Maintainer confirmed revert; committed boundary-matching behavior restored, 8/8 extras tests green. |
| Q2 | What gates the v1.0.0 tag? | **Everything — Phases 0–6 (Sessions A–E) ALL gate 1.0** | Maintainer chose the full scope over the recommended A–C+bugs minimum. Native-app plan Session 5 (operator QA + tag) runs only after Session E. |
| Q3 | Software tone mapping default for non-NVIDIA | **ON by default** | Maintainer delegated to "most common / fewest complaints": Plex tone-maps by default without complaint; Jellyfin's opt-in checkbox is a chronic washed-out-HDR complaint source. One-time log hint suggests GPU accel on weak CPUs. |
| Q4 | Scheduled scans default 12h on fresh installs | **ON (12h)** — default accepted | Watcher can miss changes on NAS shares; twice-daily sweep is the standard safety net. Existing installs keep their stored value. |
| Q5 | i18n for 1.0 | **De-scoped** — maintainer confirmed | English-only for 1.0; re-decide at native-app kickoff when the string inventory is organized anyway. |
| Q6 | Soft-delete retention window | **30 days** — default accepted | Missing items hide but keep history for 30 days (drive may come back), then hard-delete. |

---

## 11. §7 Status

| Item | Status | Notes |
|---|---|---|
| SR-WI-001 | **Complete** (2026-07-24) | Reverted per Q1; 8/8 extras tests green |
| SR-WI-002 | **Complete** (2026-07-24) | Debris + dead client files purged, ignore rules added, favicon fixed |
| SR-WI-010 | **Complete** (2026-07-24) | Purge brake + `MaxScanPurgePercent` setting + admin bell alert; 20-item floor; 100% = override |
| SR-WI-011 | **Complete** (2026-07-24) | `IsMissing`/`MissingSinceUtc` + migration `AddMediaItemMissingFlags`; ~30-site catalog sweep (`ExcludeMissing()`); heal-on-reappear (scan + watcher paths); retention hard-delete (`MissingItemRetentionDays`, 0 = legacy); DTO surfaces `IsMissing` |
| SR-WI-012 | **Complete** (2026-07-24) | `ReconcileMovedFilesAsync` (unique size+mtime, then unique filename; ambiguous binds nothing) runs pre-processing; watcher single-file lookup now case-insensitive |
| SR-WI-013 | **Complete** (2026-07-24) | Watcher error→recovery scan; file/dir renames schedule scans; 64 KB buffer; fresh-install scan interval 12h; `EnableFileWatcher` restart note |
| SR-WI-020 | **Complete** (2026-07-24) | Append-revival (`ApplyResumeArgs` + `TryReviveSessionAsync`); dormant wake on segment/unpause; exit-code crash detection; progress-gated retry budget; `Failed` state |
| SR-WI-021 | **Complete** (2026-07-24) | `TranscodeShutdownService` kill sweep (`Kill(entireProcessTree)`); Job Objects deferred (see §12) |
| SR-WI-022 | **Complete** (2026-07-24) | Exit-code-strict 10-min VTT extraction, partials deleted; persistent unshifted cache under wwwroot/cache/subtitles keyed (path-hash, track, mtime) |
| SR-WI-023 | **Complete** (2026-07-24) | Software zscale/tonemap chain for non-NVIDIA (Q3: default ON); color metadata on all encodes; PreserveHDR+h264 override; live-verified vs washed-out baseline |
| SR-WI-024 | **Complete** (2026-07-24) | sid threaded through debug endpoint; `toneMapped` reflects the engaged pipeline |
| SR-WI-025 | **Complete** (2026-07-24) | Server: stop/dormant paths take the per-key session lock; client: far-seek awaits DELETE (2s cap, generation-guarded) |
| SR-WI-026 | **Complete** (2026-07-24) | Failed→409 `{"error":"transcode_failed"}` (StreamResultService); client: plan 429/5xx surfaced, capped HLS reconnect w/ indicator, 409 terminal state |
| SR-WI-027 | **Complete** (2026-07-24) | `displaySupportsHdr` requires `(video-dynamic-range: high)`; P3 fallback removed |
| SR-WI-028 | **Complete** (2026-07-24) | Trickplay numeric sort + cancel/timeout kill; manifest line-based rewrite (.ts-in-token regression pinned); direct-play cap covers all video + size/duration estimate; capacity reservation gate; playlist poll replaces 3s sleep; disk-pressure suspends live encoders |
| SR-WI-030 | **Complete** (2026-07-24) | DB double-check in Ensure(Series/Season/Artist/Album); watcher-created series enqueue metadata immediately (`_fullScanActive` flag) |
| SR-WI-031 | **Complete** (2026-07-24) | BackdropUrl `/cache/` guard; sweep also made Overview + ReleaseDate fill-only (were re-clobbered every scan) |
| SR-WI-032 | **Complete** (2026-07-24) | Parenthesized/bracketed year wins; bare-name behavior pinned unchanged |
| SR-WI-033 | **Complete** (2026-07-24) | E100+/multi-episode/anime-bracket patterns; Specials→S0 + parent-folder show name; multi-episode SPAN not persisted (result-type constraint, noted) |
| SR-WI-034 | **Complete** (2026-07-24) | (Title, Year) series identity; also fixed ExtractYear-after-CleanShowName ordering bug (year was always null); null year = wildcard |
| SR-WI-035 | **Complete** (2026-07-24) | Parent saves under `_dbWriteLock` (Tv/Music/Book); `SqlitePragmaInterceptor` asserts WAL/busy_timeout/synchronous on open |
| SR-WI-036 | **Complete** (2026-07-24) | 4h retry tier reachable (MaxRetries 4); exhaustion cleared on apply/refresh; weekly `MetadataRetryAmnesty` task; All-mode refresh covers 8 types; `POST match/{id}/refresh` + Fix Match UI button |
| SR-WI-037 | **Complete** (2026-07-24) | Daily `ImageCacheCleanupService` (row-existence orphan criterion — IsMissing art retained); `books` covered; `InvalidateCachedImagesAsync` wired into the refresh endpoint |
| SR-WI-038 | **Complete** (2026-07-24) | VA compilations (tag + directory heuristic); junk-word list +22; unsafe-name skips on the file-issues dashboard; targeted watcher delete (soft-mark, no full scan) |
| SR-WI-040 | **Complete** (2026-07-24) | Off-canvas drawer <md w/ focus mgmt + backdrop + Escape; desktop pixel-identical; mobile search overlay; TopBar bug batch folded in (admin link, dead menu items, ARIA) |
| SR-WI-041 | **Complete** (2026-07-24) | All routes lazy; initial chunk 2.65 MB→450 kB (145 kB gzip); Cast SDK lazy-injected; precache override dropped |
| SR-WI-042 | **Complete** (2026-07-24) | `VirtualMediaGrid` (row-virtualized, bounded DOM); adaptive columns <md; desktop pixel-identical |
| SR-WI-050 | **Complete** (2026-07-24) | Watchlist key fix (test-pinned); Share copy-link ported; TopBar items in 040 |
| SR-WI-051 | **Complete** (2026-07-24) | Shared `ui/Modal` (dialog semantics, trap, Escape, focus return) adopted by all six modals; `bg-opacity` backdrop bug fixed; Combobox ARIA + keyboard; a11y ratchet guards |
| SR-WI-052 | **Complete** (2026-07-24) | Home error banner + retry; PersistentPlayer onError toast + auto-advance w/ full-pass stop; session-expired notice + return-path; PlayerPage/MediaDetailPage 404-vs-retry |
| SR-WI-053 | **Complete** (2026-07-24) | Resume/Play-from-beginning split (progress + next-episode endpoints; ≥95% = no-resume); Album Play disabled-until-loaded |
| SR-WI-054 | **Complete** (2026-07-24) | Decision only: i18n de-scoped from 1.0 per Q5 (recorded §10) |
| SR-WI-060..065 | Not started | Session E |
| Phase 7 | Unscheduled | Post-1.0 |

Session order: A → B → C → D → E → native-app plan Session 5 (operator QA + v1.0.0).
Sessions B and C are independent of each other and could swap or parallelize if needed;
D depends on nothing server-side; E's SR-WI-061/063 should land before native-client work
consumes the API.

## 12. §8 Session log

- 2026-07-24 — Plan created from `docs/reports/system-review-2026-07-23.md`. All §10 decisions
  resolved same day (Q1 revert, Q2 full A–E gate, Q3 tonemap ON, Q4 12h default, Q5 i18n
  de-scoped, Q6 30d retention). SR-WI-001 executed: ExtrasService + tests reverted via
  `git checkout --`, extras suite 8/8. Working tree now clean except this plan + the review
  report (both untracked, ready to commit). Next: Session A (SR-WI-002, 010–013).
- 2026-07-24 — **SESSION A COMPLETE** (SR-WI-002, 010, 011, 012, 013). Server suite 1335/0/0
  (baseline 1303 → +32 net; 2 pre-existing tests updated to the new 12h-default contract),
  client build green. Implementation notes for future sessions: (1) orphan handling now runs
  reconcile → brake → soft-delete-mark → retention-delete, all inside
  `BaseMediaScanner.CleanupOrphansAsync` + `ReconcileMovedFilesAsync`; reconciliation MUST stay
  before the processing walk (afterwards the moved file already has a fresh row). (2) The brake
  never trips under 20 newly-missing items and is wholly disabled at `MaxScanPurgePercent=100`
  (including the empty-discovery trip — deliberate, so an intentionally emptied library can be
  cleaned). (3) Containers (Series/Season/Artist/Album/ComicSeries) are never marked missing —
  visibility filters on Series-only queries are pointless; the sweep skipped them on purpose,
  as well as the BrowseService "unplayed" PlaybackHistory subquery (filtering it would resurrect
  watched series into Never Played). (4) Missing items still resolve by id
  (detail/playlists/streams); only catalog listings filter — the client can use the new
  `MediaItemDto.IsMissing` for "unavailable" badges (Session D candidate, not yet rendered).
  (5) `SettingsService.InitializeDefaultsAsync` is add-if-missing: existing installs keep their
  stored scan interval; only fresh DBs get 12. Deviation from item text: SR-WI-010's
  "per-library force cleanup action" is realized as the `MaxScanPurgePercent=100` override
  rather than a new endpoint/UI (simpler, uses the existing settings surface; revisit only if
  operators ask). Live verify of the NAS-unmount round-trip deferred to Session 5 as planned.
- 2026-07-24 — **SESSION B COMPLETE** (SR-WI-020..028). Server suite 1394/0/0 (+59), client
  282/282 + build green. Implementation notes: (1) Revival APPENDS to the existing playlist —
  `TranscodeService.ApplyResumeArgs` rebases the builder's literal `-start_number 0` token and
  adds `append_list`; if the builder ever changes that token, ApplyResumeArgs returns null and
  revival degrades to a full restart (test-pinned). Revival entry points: master.m3u8
  existing-session branch (resets an exhausted crash budget — explicit client intent), segment
  requests + unpause on Dormant (background Task, idempotent under the per-key lock). (2) The
  crash-retry budget resets ONLY on progress past `LastCrashSegmentIndex + 2` — never on
  client activity. (3) `TranscodeState.Failed` → `TranscodeFailedException` →
  StreamResultService maps to 409 `{"error":"transcode_failed"}`; the client's terminal-state
  handler keys on response code 409 (checked before `fatal`, like the 410 admin-stop). (4)
  Non-NVIDIA HDR tone mapping forces SOFTWARE decode but keeps qsv/amf ENCODERS (they accept
  system-memory frames); chain order deinterlace→scale→tonemap mirrors CUDA; a "no path
  between colorspaces" zscale error means a linear intermediate is missing. (5) Stop-path
  locking is sync-over-async on the per-key SemaphoreSlim (no thread affinity → no deadlock);
  `StopSessionInternalAsync` intentionally takes NO lock (always called with it held). (6)
  Subtitle VTT cache stores UNSHIFTED extractions; each session gets a private copy that
  `OffsetWebVttTimestamps` mutates in place — never point sessions at the cache file directly.
  Deviations: Windows Job Objects (hard-crash reaping) deferred — graceful-shutdown sweep
  covers the common case; existing trickplay manifests keep old ordinal order until
  regenerated. Session 5 live-verify additions: pause>3min→resume, mid-stream ffmpeg kill →
  auto-revival, HDR transcode on `HardwareAcceleration=none`, server shutdown leaves no
  ffmpeg (`tasklist`).
- 2026-07-24 — **SESSION C COMPLETE** (SR-WI-030..038). Server suite 1482/0/0 (+88), client
  285/285 + build green. Notes for future sessions: (1) Watcher-path metadata enqueue uses a
  `_fullScanActive` flag on TvScanner because `BaseMediaScanner.ProcessSingleFileAsync` is not
  virtual — if the base ever gains an override seam, fold the flag away. (2) VA compilations:
  per-track performer display needs a schema column (`TrackArtist` string) the model lacks —
  tracks attach to the "Various Artists" artist for now; known follow-up, do NOT repurpose
  Director/Studio, and remember empty-container cleanup would purge a VA artist left with no
  track FKs. (3) Multi-episode files parse to their PRIMARY episode only — the parser result
  tuple has no span field; a deliberate result-type change is needed to carry it. (4) The
  image-cache cleanup's orphan criterion is ROW-EXISTENCE (raw DbSet), never a filtered
  surface — IsMissing items keep their art; `tv/cast` is excluded by design (int person-id
  keys). `InvalidateCachedImagesAsync` retains `*_local` sidecar copies; series-level art is
  keyed by series id. (5) Anime absolute numbering = season 1 by convention (comment in
  parser). (6) `SqlitePragmaInterceptor` guards on connection type name so InMemory tests
  pass through. Live-verify additions for Session 5: Sonarr-style watcher import creates no
  duplicate series and gets artwork without a manual scan; deleting a file leaves history
  and shows the item back after restore.
- 2026-07-24 — **SESSION D COMPLETE** (SR-WI-040..042, 050..054). Client-only session:
  build green, suite 373/373 (+88 over the 285 baseline); server untouched. Notes:
  (1) Mobile drawer state is transient component state in MainLayout, NOT the persisted
  uiStore (deliberate); desktop collapse persists as before, and the drawer always renders
  full-width even when desktop collapse is persisted (`useIsMdUp` gate). (2) The six modals
  are ratcheted onto `ui/Modal` by a11yGuards — new modals must use it or the guard fails;
  `bg-opacity-*` is banned repo-wide by the same guard. (3) Route chunks are all precached
  (93 entries / 2.85 MiB) so offline navigation still works; the 5 MB precache override is
  gone — a future chunk >2 MiB will FAIL the PWA build (that's the signal to re-split, not
  to re-raise the cap). (4) Cast SDK loads via `castSdkLoader.ts` on first cast-capable
  mount; `__onGCastApiAvailable` must be registered BEFORE script injection. (5) Resume
  position comes from `GET /interaction/{id}/progress` (movies/episodes) and
  `/series/{id}/next-episode` (series) — the detail DTO deliberately does not carry it
  (server change would belong to Session E's DTO work if ever wanted). (6) Sequential-reveal
  cascade applies to the first 30 grid items only (virtualized rows render immediately).
  Known small follow-ups recorded: AlbumDetailView's own "Play All" wasn't guarded (the
  MediaDetailPage handler is); `api.ts` gained one line (`logout('expired')`) outside the
  session's ownership lists — reviewed and kept. Live-verify additions for Session 5:
  phone-width (375px) nav/search/browse pass; cast still works end-to-end (SDK lazy path);
  offline PWA route navigation after first load.
