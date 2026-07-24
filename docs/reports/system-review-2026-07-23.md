# SoftMedia Whole-System Review — 2026-07-23

Five parallel code-verified subsystem reviews (streaming/transcode, security, library/metadata,
client app, API/data layer) plus a product feature inventory and a competitive comparison against
Plex and Jellyfin. All findings below were verified against source by the reviewing agent; items
marked with file:line were read directly. Previously-remediated items (R-WI-001..020, B-01..B-21,
NR-WI-001..014 — see `docs/reports/feature-gap-analysis-2026-07-15.md` and
`docs/plans/native-app-readiness-plan-2026-07-21.md`) were checked for regressions and are NOT
re-reported unless regressed.

**Headline:** security posture is excellent (zero exploitable findings at any privilege level).
The serious problems cluster in two places: **data safety during library scans** and
**playback-session resilience**. Both matter disproportionately for a "puts the users first"
self-hosted product — losing a user's watch history or stalling their movie after a pause are
exactly the failures that break trust.

---

## 0. Working-tree regression (flag before anything else)

The uncommitted `ExtrasService.cs` + `ExtrasServiceTests.cs` change **deletes** the
`StemMatchesTitle`/`CompanionBase` boundary-prefix matching (and its 32 test lines) and replaces
it with a plain `StartsWith(movieStem)`. Effects:

1. `Film-trailer.mkv` beside `Film.1080p.BDRip.mkv` no longer pairs (trailer stem doesn't start
   with the release-tagged stem) — the dominant real-world trailer naming stops working.
2. `Aliens-trailer.mkv` now attaches to `Alien.mkv` — the exact false-positive the boundary check
   existed to prevent.

If not deliberate, revert both files. If deliberate, the removed cases need replacement coverage.

---

## 1. Security (clean)

Full adversarial pass over all 34 controllers, path jails, SSRF guards, JWT/refresh/media/cast
tokens, rate limits, LIKE injection, XSS, CSRF, DLNA, uploads, headers. **No exploitable
vulnerability found** for unauthenticated, authenticated non-admin, or admin attackers. All July
2026 hardening intact. Notes:

- **SEC-1 (LOW, hygiene):** debug/throwaway files tracked in git: `DumpDune.cs` (hardcodes a
  local DB path), `dune_search.json` (484 KB), `src/SoftMedia.Server/probe_output.json` (107 KB),
  `debug_args.txt`, `test.jpg`, `TempVerify/`, `src/TempDebug/`, `temp-net-test/`. No secrets
  inside (grepped). `git rm` + ignore rules before the 1.0 tag.
- CSP still Report-Only by default (`Security:EnforceCsp`) — deliberate; flip during Session 5.
- QuickConnect `pending/{code}`/`authorize` lack a dedicated rate limit, but require full session
  and the ~10⁹ keyspace vs ≤100 live codes makes enumeration impractical. Mention-only.

---

## 2. Library management & metadata

### HIGH

- **LIB-H1 — Empty-but-reachable library root purges the library and cascades away user data.**
  `BaseMediaScanner.cs:109-124` guards only `!Directory.Exists`. An unmounted mount point or an
  SMB share that reconnects empty passes the guard; `CleanupOrphansAsync` (`:497-561`)
  `ExecuteDeleteAsync`es every item, and `PlaybackHistory`, `UserMediaInteraction`, `Bookmark`,
  `ReadingSession`, `PlaylistItem` all cascade-delete (`AppDbContext.cs:300-310`, `:147-159`).
  Re-scan recreates items under new GUIDs — history unrecoverable. **The classic self-hoster
  disaster.** Fix: orphan-fraction circuit breaker (abort + notify if >N% would purge) and/or
  soft-delete (`IsMissing`) with heal-on-reappear (also fixes LIB-H4).
- **LIB-H2 — Watcher single-file imports duplicate Series/Artist/Album, and watcher-created
  series get no metadata.** Scanners are Scoped; the watcher path builds a fresh scanner with
  empty session caches, and `TvScanner.EnsureSeriesAsync`/`EnsureSeasonAsync`
  (`TvScanner.cs:360-460`) and `MusicScanner.EnsureArtistAsync`/`EnsureAlbumAsync`
  (`MusicScanner.cs:202-321`) check only the cache before creating — no DB double-check
  (`BookScanner.EnsureComicSeriesAsync` does it right). Every Sonarr/Lidarr import mints a bare
  duplicate parent until the next full scan self-heals. Fix: DB lookup on cache miss; drain
  `_seriesNeedingEnrichment` in `ProcessSingleFileAsync` too.
- **LIB-H3 — Every rescan reverts cached episode stills to hotlinked TVMaze URLs.**
  `TvScanner.PopulateEpisodeMetadata` (`TvScanner.cs:536-541`) overwrites `episode.BackdropUrl`
  (set to `/cache/images/...` by `ImageDownloadQueueService.cs:202-214`) with the raw remote URL
  on every scan; nothing re-caches. Breaks offline installs and violates the codebase's own
  no-hotlinking rule. Fix: only set when empty / not already a `/cache/` path.
- **LIB-H4 — Renames/moves destroy watch state.** Identity is `Path` only
  (`BaseMediaScanner.cs:154-233`); rename = purge (cascading history per LIB-H1) + new item. A
  **directory** rename (renaming a show folder) enqueues nothing at all in the watcher
  (`LibraryWatcher.cs:412-430` handles files only) — subtree stale until a scan purges it.
  Fix: reconcile orphans by (size, mtime)/filename before deletion; handle directory renames.
- **LIB-H5 — Year-parsing pattern-order bug.** `FileNameParser.cs:7-13` tries "Title Year" before
  "Title (Year)": `Blade Runner 2049 (2017).mkv` → Title "Blade Runner", Year 2049 (in range,
  accepted). Same for `Wonder Woman 1984 (2020)`, `Death Race 2000 (1975)`. Wrong provider
  lookups follow. Fix: parenthesized-year pattern first.

### MEDIUM

- **LIB-M1** — TV parsing gaps: `S(\d{1,2})E(\d{1,2})` fails on E100+/multi-episode
  (`S01E01E02`)/anime bracket naming → indistinguishable "Episode 0" rows; `Specials/` folder can
  create a series named "Specials"; no absolute numbering (`FileNameParser.cs:15-34`,
  `TvScanner.cs:554-576`).
- **LIB-M2** — Series cached by `Title` alone (`TvScanner.cs:78`); `Doctor Who (1963)` and
  `Doctor Who (2005)` merge.
- **LIB-M3** — Scheduled scans default OFF (interval 0, `ScheduledScanService.cs:204-210`);
  FileSystemWatcher `Error` (buffer overflow during big copies) only logs — no rescan scheduled;
  8 KB default buffer; no polling fallback for SMB/NFS. Default NAS install can silently never
  pick up new files.
- **LIB-M4** — Parent-creation `SaveChangesAsync` inside the parallel walk bypasses the scanner
  `_dbWriteLock` (`TvScanner.cs:392,448`, `MusicScanner.cs:249,309`, `BookScanner.cs:338`) —
  SQLITE_BUSY risk on first large scans.
- **LIB-M5** — Metadata retry exhaustion is permanent after ~36 min of backoff (the 4 h backoff
  entry is unreachable dead code, `MetadataRetryService.cs:22-49`); nothing ever resets
  `IsRetryExhausted`; music/books/games have no bulk-refresh rescue (see LIB-M7).
- **LIB-M6** — Image cache: cached images never refreshed; `CleanupOrphanedImages` has **zero
  callers** (leaks forever, compounded by LIB-H4 GUID churn); its dir list omits `books`.
- **LIB-M7** — Global metadata refresh covers Movies + Series only
  (`MetadataRefreshService.cs:69-86`).
- **LIB-M8** — No multi-part stacking (`CD1/CD2` = two items) and no version grouping
  (1080p + 4K = two cards).
- **LIB-M9** — VA compilations without AlbumArtist tags explode into per-performer single-track
  albums (`MusicScanner.cs:130-146`); no "Various Artists" convention.

### LOW

OMDb auto-match takes first result blindly; no metadata language setting (English-only plots for
non-English users); quote/control-char filenames silently skipped with no admin surface;
`ProcessSingleFileAsync` path lookup is BINARY-collated vs the scan's OrdinalIgnoreCase (casing
change on Windows duplicates then purges w/ history); single-file delete triggers a full library
scan; junk-word list is release-group-specific.

---

## 3. Streaming & playback

### HIGH

- **STR-H1 — Pause → permanent stall; crash recovery is dead code.** `EnterDormantState` kills
  ffmpeg at the 120 s buffer mark (`TranscodeService.cs:756-779`); nothing ever revives a Dormant
  session (`ThrottleMonitorService.cs:86` skips it; `/resume` only clears `IsPaused`,
  `TranscodeService.cs:156-167`). When the client drains its buffer, segments 404, hls.js
  re-fetches the identical master URL, and `StartTranscodeAsync` (`:264-389`) returns the dead
  session as "already active" — infinite spinner. A mid-stream ffmpeg crash is labeled
  "completed" (`ThrottleMonitorService.cs:96-101`) with the same dead-end.
  `MaxCrashRetries`/`CrashRetryCount` are reset but never incremented or consulted. Fix: liveness
  check in the existing-session branch; restart ffmpeg at the last segment boundary; wire crash
  retries for real.
- **STR-H2 — No software tone mapping: HDR→SDR correct on NVIDIA only.** Tone mapping is gated on
  `HardwareAcceleration == "nvidia"` (`TranscodeProfileBuilder.cs:199`). CPU/Intel/AMD encodes of
  PQ/HLG sources go straight to `libx264 -pix_fmt yuv420p` — washed-out gray output — and no
  color metadata flags are ever emitted. The debug panel claims `toneMapped: true`
  (`TranscodeDebugService.cs:170`), making it undiagnosable. Fix: software
  `zscale/tonemap=hable` fallback chain; stamp color metadata; report reality.
- **STR-H3 — Sidecar VTT extraction hard-killed at 30 s; partial file served.**
  `ExtractSubtitleToVttAsync` (`SubtitleService.cs:52-111`) rides `ProcessRunner.RunProcessAsync`'s
  30 s kill and accepts any non-empty file — large remuxes lose subtitles partway through. The
  burn-in `.ass` twin was already fixed properly (10-min, exit-code-strict, deletes partials);
  apply the same to the default text-sub path.
- **STR-H4 — ffmpeg orphaned on server shutdown/restart.** No `ApplicationStopping` hook or
  `StopAsync` kills session processes; on restart the temp-dir purge fails against the orphan's
  open handles (swallowed) so it burns CPU/disk indefinitely. Fix: hosted-service `StopAsync`
  kill sweep; consider Job Objects / PDEATHSIG.

### MEDIUM

- **STR-M1** — Far-seek fire-and-forget DELETE races the new session (stop paths don't take the
  session lock) and can kill the fresh ffmpeg → stall via STR-H1 (`VideoPlayer.tsx:1601-1604`,
  `TranscodeService.cs:723-731`).
- **STR-M2** — `AudioStreamController.GetTranscodedStream` (`:101-185`): per-request ffmpeg with
  no concurrency cap, no session registration (invisible to admin dashboard/stop), leaked
  `Process` handles. Unused by the SPA — cap it or remove it.
- **STR-M3** — Debug endpoint drops `sid` (`TranscodeController.cs:442-448`,
  `TranscodeDebugService.cs:46`): every real transcode reports "likely direct play"; the
  remux-HDR `toneMapped` mislabel persists underneath.
- **STR-M4** — Error propagation: plan-POST failure (incl. 429) silently leaves "Loading
  player..."; fatal HLS network errors retry `startLoad()` unbounded with no UI; abnormal ffmpeg
  exit is never signaled to the client (`VideoPlayer.tsx:657, 949-961`).
- **STR-M5** — `useMediaCapabilities.ts:143-165` treats `(color-gamut: p3)` as HDR-capable —
  nearly all modern SDR laptops/phones — so HDR is direct-played/remuxed untouched to SDR
  screens. Require `(video-dynamic-range: high)` only.
- **STR-M6** — Fixed `Task.Delay(3000)` inside the session lock on every start/far-seek
  (`TranscodeService.cs:520`); subtitled far-seeks re-demux the entire container every seek
  (per-session dirs defeat the reuse check). Poll for the manifest; cache extracted subs per
  (mediaId, track).
- **STR-M7** — Trickplay generation: cancellation orphans ffmpeg, no timeout
  (`TrickplayService.cs:118-127`).

### LOW

Buffer math assumes 6 s segments (drifts for remux copy — confirmed still open); trickplay sheet
ordinal sort breaks scrubbing past ~2 h 47 m (`sheet-10` < `sheet-2`); HLS manifest rewrite via
blind `.Replace(".ts", …)` can corrupt token-bearing URLs (~1/2500 tokens); capacity-check TOCTOU
can exceed session caps; null-plan path misses the ≥1000 bitrate clamp and codec validation;
direct-play cap gate skips unknown-bitrate/non-movie rows (`StreamController.cs:116-118`); live
ffmpeg never throttled on low disk (only dormant eviction at 500 MB); ASS styling stripped on the
default sidecar path (burn-in preserves it but is a user pref, not per-format).

Residual-status check: per-user bitrate cap on direct play **FIXED**; fabricated-sid
resolution/codec bypass **FIXED**; 6 s buffer drift **open** (low); debug toneMapped mislabel
**open** (now masked by STR-M3).

---

## 4. Client app

### HIGH

- **CLI-H1 — No mobile shell.** Sidebar is `fixed` 256 px with unconditional `ml-64` on `<main>`
  (`MainLayout.tsx:25-28`, `Sidebar.tsx:109-112`, `TopBar.tsx:119`); on a 375 px phone the nav
  eats ~68% of the viewport. No drawer, no breakpoint, no bottom nav. The web client is the
  interim mobile client until native ships — this blocks that.
- **CLI-H2 — Single 2.65 MB JS chunk (780 KB gzip), zero code splitting.** `App.tsx` statically
  imports every page; epubjs, react-pdf, hls.js, SignalR, framer-motion load before the login
  page renders. Lazy-loading Reader/Player/Settings alone likely halves the initial chunk.

### MEDIUM (bugs)

- **CLI-M1** — `WatchlistButton.tsx:50` invalidates `['media-detail', mediaId]` — a key no query
  uses (detail page uses `['media', id]`) — and the sync-back at `:58-60` then **reverts the
  button to its pre-toggle state after a successful save**.
- **CLI-M2** — TopBar "View in Admin Dashboard" navigates to pre-refactor `/settings?tab=admin`,
  which redirects to the Transcoding page (`TopBar.tsx:260`, `App.tsx:126-130`). Should be
  `/settings/admin`.
- **CLI-M3** — Four dead user-menu items with no onClick ("View Profile", "Report Issues",
  "Help", "Switch User", `TopBar.tsx:325-352`); the detail-page Share button for non-photos is
  also a dead control (`MediaDetailLayout.tsx:226-231`; the photo variant implements copy-link).
- **CLI-M4** — `ConfirmationModal.tsx:27` uses Tailwind-v3 `bg-opacity-50` (removed in v4) — the
  backdrop renders fully opaque black. Use `bg-black/50`.
- **CLI-M5** — Six modals (Confirmation, Streaming, LibraryAccess, Ratings, CreateUser,
  ResetPassword) have zero dialog semantics: no `role="dialog"`, focus trap, Escape, or focus
  return. The correct pattern already exists in-repo (reader drawers, FixMatchCard) — one shared
  Modal primitive fixes all six.
- **CLI-M6** — Home rows return `null` on error as well as loading (`HomePage.tsx:26-27` etc.) —
  a server hiccup renders a silently blank home page. PersistentPlayer audio elements have no
  `error` handler — dead tracks stall the bar silently.
- **CLI-M7** — No resume/start-over choice on detail pages (silent auto-resume; `?start=0`
  support already exists server/player-side).
- **CLI-M8** — Design-system drift: 451 hardcoded `violet/purple/blue-*` uses vs 236 semantic;
  ~9 hand-rolled brand-gradient re-declarations that don't match the canonical
  `bg-brand-gradient`; 51 raw hexes. A palette change would fracture the UI.
- **CLI-M9** — i18n is infrastructure-only (~40 strings wired of an app that is otherwise
  hardcoded English; no language picker). Decide extract-now vs de-scope-for-1.0 **before**
  native apps multiply the string inventory.
- **CLI-M10** — Library grid: infinite scroll appends unvirtualized cards (10k-item library =
  10k mounted framer-motion cards); `@tanstack/react-virtual` is already a dependency and used
  elsewhere.

### LOW

Bare error strings with no retry; unbounded silent HLS retry loop indicator; logout drops the
return path ("session expired" context lost); failed rescan trigger looks like a working button;
Combobox has no ARIA; icon buttons missing labels; micro-text contrast below AA; zustand persist
keys device-global (volume/theme leak between accounts; content state is fine);
`staleTime: 0` + focus refetch chatty over WAN; dead files (`ui/ProgressBar.tsx`,
`sampleData.ts`, `App.css`, `react.svg`, vite.svg favicon); no video mini-player/auto-PiP;
audio casting absent; Album Play before tracks resolve is a silent no-op.

Status check: the known-open `MediaDetailPage.handlePlay` hazard is **RESOLVED** (branches
Album/Artist → `playPlaylist` in the same file) — closable.

---

## 5. API & data layer

- **API-M1 — DateTime serialization skew.** Storage is consistently UTC, but SQLite round-trips
  `Kind=Unspecified` and there's no JSON converter — entity timestamps serialize **without `Z`**,
  fresh `UtcNow` values with `Z`. JS parses the un-suffixed form as local time. Fix: a
  `JsonConverter` stamping `DateTimeKind.Utc`. Do this before native clients bake it in.
- **API-M2 — Three-and-a-half error shapes, empty-body 500s.** No
  `UseExceptionHandler`/`AddProblemDetails`; manual `{error}` vs `{message}` vs RFC7807
  validation vs the middleware's `{error, message}`. Fix: global exception handler + one
  ProblemDetails envelope. (This is also the root of the "Vite preview 500" confusion.)
- **API-M3 — Missing indexes on hot columns.** `MediaItems` has FK indexes only — no
  `(LibraryId, Type)`, `DateAdded`, `Title`, `Year`, or `Path` (not even unique);
  `UserMediaInteractions` lacks `(UserId, LastPlayed)` for continue-watching.
- **API-M4 — DTO hygiene for native clients.** `MediaItemDto` exposes the full server filesystem
  `Path` (info disclosure + unwanted contract), an untyped `Metadata` dictionary, and `Duration`
  as string beside `DurationSeconds`.
- **API-M5 — No persistent log sink.** Console + 2,000-entry ring buffer only; post-crash
  forensics on bare Windows = nothing. Add a rolling file (or EventLog) for Warning+.
- **API-M6** — Pagination is two styles (offset `PagedResult` vs bare `?limit=`); document for
  native clients; consider keyset for `DateAdded` feeds.
- **API-M7** — `GetRecentMediaAsync` fetches up to `limit*25` fully-hydrated entities with three
  Includes (`MediaRepository.cs:224`).
- **API-L** — WAL/busy_timeout/synchronous never asserted (works by EF default; assert pragmas at
  open); scan parent-writes bypass the write lock (= LIB-M4); stray DBs/artifacts in the server
  project root hidden by the `data/` ignore; backup dir is CWD-relative; `EnableFileWatcher` is
  boot-only but its description doesn't say so (unlike `EnableDlna`); settings read-cache is 60 s.

Good: migrations clean (zero pending model drift, 68 migrations); hosted-service startup
ordering, crash policy, and graceful shutdown all verified sound; backup/restore story is
genuinely strong (online snapshot, SHA-256 manifest, staged boot-time restore, artwork repair);
versioning discipline good; AsNoTracking discipline good.

---

## 6. Competitive position vs Plex / Jellyfin

### Where SoftMedia already wins

- **Privacy & user-first posture** (the stated mission): no phone-home, no external account
  requirement, no ads/discover injection — vs Plex's mandatory plex.tv login drift and
  increasingly pushy monetization. Jellyfin shares this ethos; SoftMedia matches it.
- **Free features that are paywalled in Plex**: intro/credits skip (Plex Pass), trickplay
  previews (Plex Pass), hardware transcoding (Plex Pass) — all free here, as in Jellyfin.
- **Breadth of first-class media types**: movies/TV/music/photos + a real **book & comic reader**
  (epub/pdf/cbz/cbr with bookmarks, highlights, reading sessions, dictionary) + games catalog.
  Neither Plex nor Jellyfin has a comparable in-app reading experience.
- **Ops maturity unusual for its age**: first-class backup/restore with rotation + staged
  restore (neither competitor has this built in), admin session dashboard, background-task
  telemetry, in-app log viewer with runtime level, scoped API tokens, inbound *arr webhooks,
  hardened outbound webhooks, Quick Connect, 2FA + trusted devices.
- **Security posture**: this review found zero exploitable issues; Jellyfin's history here is
  rockier.

### Table-stakes gaps (adoption blockers, roughly ordered)

1. **Client reach** — no native/TV/mobile apps yet (planned); interim mobile web is blocked by
   CLI-H1/H2. Plex's app ecosystem is its moat; Jellyfin covers Android TV/Roku/etc.
2. **Deployment** — no Docker/Linux packaging (Phase C deferred). The self-host audience is
   overwhelmingly Docker-on-Linux; related: **no VAAPI**, so no Intel/AMD hardware transcode on
   Linux at all (AMF path is D3D11VA/Windows-only).
3. **External subtitle files** — `.srt`/`.ass` sidecars next to media are **not read at all**
   (embedded streams only). Both competitors treat this as basic; for many users this alone is
   disqualifying. No subtitle-provider (OpenSubtitles) integration either (deferred Q4).
4. **Adaptive streaming** — single rendition per session, no ABR ladder, no DASH; competitors
   adapt to bandwidth changes without a manual quality switch.
5. **Metadata ecosystem** — no TMDB/TVDB (OMDb/Wikidata/TVMaze are weaker for
   posters/localization); NFO read-only (no write-back = no portability story); English-only
   metadata (LIB-M9/L-2); no per-item refresh endpoint.
6. **Library semantics** — no multi-part stacking, no version grouping (1080p/4K), weak
   anime/specials parsing, no "Various Artists" (§2).
7. **Absent feature classes**: Live TV/DVR (Jellyfin free, Plex has it), SyncPlay/Watch Together,
   offline downloads/sync, lyrics, audiobooks, smart playlists, plugin system, OPDS (odd gap
   given the book focus), AirPlay, Tautulli-style analytics, PIN/kid profile switching (rating
   ceilings + library ACLs exist and are good, but there's no quick profile UX).

### Reliability bar

The incumbents handle the failure modes behind this review's High findings: session recovery
after pause/crash (STR-H1), software tone-mapping fallback (STR-H2), and library-unavailable
protection (LIB-H1 — Jellyfin learned this the hard way and grew guards). Matching that
resilience is part of competing, not polish.

---

## 7. Recommended priority order

1. **Resolve the ExtrasService working-tree regression** (revert or justify + re-cover).
2. **Data safety** — LIB-H1 purge circuit breaker / soft-delete + LIB-H4 rename reconciliation.
   "User-first" means never losing watch history to a flaky mount.
3. **Playback resilience** — STR-H1 dormant/crash revival, STR-H4 shutdown kill sweep, STR-H3
   subtitle extraction timeout parity, STR-H2 software tone mapping (+ STR-M3 debug sid so it's
   diagnosable).
4. **Scan correctness batch** — LIB-H2 (DB double-check), LIB-H3 (stop clobbering stills),
   LIB-H5 (year pattern order), LIB-M3 (watcher error → rescan; nonzero default scan interval).
5. **Interim mobile web** — CLI-H1 responsive shell + CLI-H2 route splitting.
6. **Native-client API prep** (fold into Session 2 follow-ups): API-M1 UTC converter, API-M2
   error envelope, API-M3 indexes, API-M4 DTO cleanup, API-M5 file log sink.
7. **Client bug batch** — CLI-M1..M7 (small, high-visibility).
8. **Competitive roadmap (post-1.0)** — external subtitle sidecars (highest leverage), VAAPI,
   Docker (reactivate Phase C), TMDB provider option, ABR, OPDS, audiobooks, SyncPlay, smart
   playlists, i18n go/no-go.
9. **Hygiene** — SEC-1 git debris purge, dead client files, SQLite pragma assertion.
