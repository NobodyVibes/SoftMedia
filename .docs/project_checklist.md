# SoftMedia Project Checklist

## Global Tasks

### Completed
- [x] Integrate Phase 1 (Critical Infrastructure) fixes 
- [x] Integrate Phase 2 (Architecture Improvements) 
- [x] Integrate Phase 3 (Duplication & Efficiency)
- [x] Integrate Phase 4 (Correctness & Storage)
- [x] Integrate Phase 5 (Resilience)

### Pending
- [x] Investigate and resolve warnings (e.g., `CS1998` in scanners and analysis strategies, `CS8604` in transcoding services).
- [x] Add explicit Unit tests regarding `MusicMetadataResolver` and `OpenLibraryProvider` scoring.

## Scanning & Metadata Fixes (March 2026)
- [x] Phase 1: Database schema normalization (Person, Genre, Cast, MediaItemGenre tables)
- [x] Phase 2: File watcher fixes (MediaExtensions.All, single-file processing)
- [x] Phase 3: Metadata provider fixes (SPARQL GROUP_CONCAT, BookScanner title fix)
- [x] Phase 4: TvScanner O(N²) JSON parse optimization
- [x] Phase 5: Persistent retry queue (MetadataRetry table)
- [x] Phase 6: Documentation & scanner improvements (SDD fix, BookScanner/GameScanner IMediaAnalysisService)
- [ ] Deferred: Music routing consolidation into MetadataRouter
- [ ] Future: Book/Game analysis strategies, frontend normalized genre/cast display

## Metadata Architecture Refactoring (April 2026)
- [x] Phase 1: Schema & Column Promotions (PosterUrl, BackdropUrl, IsRetryExhausted)
- [x] Phase 2: MetadataAggregator Batching & Dedup Fix (Genres, Cast batch persistence)
- [x] Phase 3: Settings Caching & Enrichment Policy Optimization (IMemoryCache, Single JSON parse)
- [x] Phase 4: DTO & Image Resolution Consolidation (ResolvePosterPath, ResolveBackdropPath)
- [x] Phase 5: Scanner Performance Optimization (FileDiscoveryResult caching, O(1) TV Episode lookups)
- [x] Phase 6: MetadataJson Dual-Write Cleanup (Removed legacy Genres column)

## Licensing & Repo Hygiene (June 2026)
_Plan: `docs/plans/licensing-and-repo-hygiene-plan-2026-06-18.md` (rev. 3)._
- [x] Relicense under AGPL-3.0-or-later (LICENSE + SPDX in csproj/package.json)
- [x] THIRD-PARTY-NOTICES.md + `scripts/gen-licenses.ps1` regenerator
- [x] De-vendor ffmpeg from git (4 binaries untracked, .gitignore, csproj ItemGroup removed)
- [x] Fetch jellyfin-ffmpeg at setup (install_ffmpeg.ps1 rewrite + install_ffmpeg.sh; chromaprint gate)
- [x] Harden BinaryLocationService (assembly-relative + jellyfin-ffmpeg candidates; warn on bare-PATH)
- [x] CONTRIBUTING.md + CLA.md + CLA-assistant workflow + SECURITY.md
- [x] README: License, Privacy/egress, AGPL §13 (repo-level) sections; CHANGELOG.md
- [ ] In-app (UI) AGPL §13 source link (repo-level offer done; UI link pending)
- [ ] Git-history purge of the old binaries (deferred pending repo public/private status)
- [ ] Wire CLA-assistant PAT secret + a CI license-compatibility gate (maintainer action)

## Continue Watching Row (June 2026)
_Feature plan CW1–CW5 (`docs/plans/feature-implementation-plan-2026-06-16.md` §4.C); adversarially reviewed (14 findings → 10 confirmed → all fixed)._
- [x] `MediaCompletionHelper` — shared "finished" rule (IsWatched > credits timecode > 95%)
- [x] `GET /api/v1/continue-watching` + `ContinueWatchingService` (per-user, newest-first, ACL/rating-filtered at the join, paged scan, resolver budget)
- [x] Series collapse to ONE show card; Play resumes the correct episode via the shared next-episode resolver
- [x] Resolver hardening: `IsSeriesComplete` now means EVERY episode finished (wrap-scan for first unfinished)
- [x] Client: `ContinueWatchingRow` first below the hero; `['continueWatching']` invalidated on watched-marks + player unmount
- [x] Tests: 30 CW-related (incl. real-resolver regressions); suites 851 server / 164 client green; live-verified in browser

## Movie Post-Play Overlay (July 2026)
_Netflix/Plex-style end-of-movie experience; extends the episode credits detector to movies._
- [x] Movie completion detection in the player (credits marker > 98%), mirroring the episode rule
- [x] Watched flag set automatically at the threshold (retried at `ended` if the POST failed)
- [x] `GET /api/v1/movie/{id}/post-play` + `RecommendationService.GetMoviePostPlayAsync` — same-collection films first (release order, next-after-current leads; marathon path), then genre matches; ACL/rating filtered; finished movies excluded by the shared completion rule
- [x] `MovieEndOverlay`: built to the SAME compact card as the TV "Play Next" overlay (max-w-xl, X-dismiss, 10 s pausable countdown ring that pauses the video too) — recommendation poster strip (click = play), star rating, Watch Credits, Back to Library, countdown auto-return to the movie's source library
- [x] `ended` while the overlay is visible does NOT force-navigate (the countdown owns navigation); only after Watch Credits/no-overlay does the true end return to the library
- [x] `ended` event on a movie navigates to its library (episodes keep their Play Next flow)
- [x] Fixed pre-existing broken `/library/{id}` links (route is `/libraries/:id`) in NextEpisodeOverlay + HomePage "View All"
- [x] Tests: 5 server (post-play ordering/exclusion/ACL) + 4 client (overlay) — suites 856 server / 168 client green; full flow live-verified (overlay → watched flag → countdown nav → card click plays next film)

## Stream Quality Tier 1 (July 2026)
_Parity with Plex/Jellyfin best practice for older/low-res content. Tier 2 (web shader upscaler / AV1 film-grain synthesis) DEFERRED post-release by decision 2026-07-10 — briefs registered as P4-012 / P4-013 in `docs/plans/roadmap/phase-4-deferred.md`. Tier 3 (per-platform clients) closed 2026-07-10 with documentation only: client API contract in `docs/api/stream-plan-negotiation.md`; mpv desktop client registered as P4-014._
- [x] Interlace detection: ffprobe `field_order` → `MediaProbeResult.IsInterlaced` (tt/bb/tb/bt)
- [x] Deinterlacing on every transcode branch — `bwdif=mode=send_frame` (software), `yadif_cuda` (CUDA frames incl. tonemap chain); inserted BEFORE subtitle burn/overlay so subs land on progressive frames
- [x] No-upscale clamp: all scale targets are `min(W,iw)` — the transcoder can never exceed source resolution (a 720p request on the 704×264 movie now encodes 704×264, was 1280×480)
- [x] Lanczos scaling (`flags=lanczos` / `interp_algo=lanczos`) on the real downscales
- [x] 13 new `TranscodeProfileBuilder` arg-construction tests; suite 869 server green
- [x] Live-verified: real 720p transcode of the low-res movie encodes at source res via CUDA path; synthetic interlaced clip (field_order tb) runs both exact filter chains through jellyfin-ffmpeg → progressive output

## Immersive Full-Viewport Player (July 2026)
- [x] `/play/:id` now fills the browser window (`fixed inset-0`, no viewport-unit quirks); video letterboxes/pillarboxes via `object-contain` so any source aspect (incl. 704×264 ultra-wide) renders undistorted at every window size — verified live at 1684×919 and 800×900, no scrollbars
- [x] Title/year + Back moved into a top overlay bar that fades with the controls (Back: in-app history via `history.state.idx`, else the media detail page)
- [x] Keyboard-shortcut hints relocated into the controls gradient (no page space below the video anymore)

## Remediation & Gap Closure (July 2026)
_Report: `docs/reports/feature-gap-analysis-2026-07-15.md` (8-domain audit, 103 candidate gaps, 17-claim adversarial peer review: 13 confirmed / 4 partial / 0 refuted). Plan: `docs/plans/remediation-and-gap-closure-plan-2026-07-15.md` (R-WI-001..020)._
### Phase A — P0 (security & defects) — _**COMPLETE 2026-07-16** (all live-verified + adversarially diff-reviewed); server suite 896 pass / 1 skip / 0 fail; changes uncommitted_
- [~] R-WI-001 ✅ untracked 61 debris files (login.json ×2, token.json, 4 DB snapshots, build/test noise) + extended `.gitignore`; ⏳ rotate JWT key/admin pw + history-purge maintainer-gated
- [x] R-WI-002 ✅ **live-verified** — dedicated `sid`-keyed `StreamPlanStore` + resolver; far-seek with minimal URL restored 720p + `-maxrate 2000k`, injected `bitrate=50000` ignored (4 store tests)
- [x] R-WI-003 ✅ **live-verified** — real remux (`-c copy` → fMP4); H.264/AAC MKV plays via stream-copy. Review fixes: remux bitrate-cap gate + fMP4-muxable-codec restriction (Vorbis→transcode) + sliding-TTL plan store (+10 tests)
- [x] R-WI-005 ✅ **live-verified** — client keeps `burnSubtitles` on seek + server resolver authoritative; **fixed a pre-existing far-seek 500** (`SessionLock.Dispose` ObjectDisposedException on session-restarting seeks) (2 tests)
- [x] R-WI-006 ✅ role claim admin-scope-conditional (closes D-5) + `WriteState` on Playlists/UserPreferences/Book/Webhooks writes + **`FullSession` locks token-mint/account/2FA to JWT sessions** (diff-review CRITICAL — a token could otherwise mint itself a higher scope); +4 tests. ⏳ `write:library` scope rides with R-WI-019
- [x] R-WI-007 ✅ `RefreshWatchersAsync()` on library create/edit (+ pending-file prune, boot-disabled no-op, best-effort isolation from the request); 5 tests; 3 LOW follow-ups noted in plan
### Phase B — P1 (finish half-built + high-leverage gaps) — _✅ COMPLETE: R-WI-004/008/009/010 done 2026-07-16, R-WI-011/012/013/014/015 done 2026-07-17; server 998/1/0, client 208_
- [x] R-WI-004 ✅ **live-verified** — surround audio ladder (copy / AC3 5.1 encode / stereo); AC3 5.1→`-c:a copy`, FLAC 5.1→`-c:a ac3 -ac 6`. Review fixes: pin `-map 0:a:0` (multi-track HIGH), bounded encode when capped, selected-track neutral AAC (+11 tests)
- [x] R-WI-008 ✅ **live-verified** — `ScheduledScanService` + `LibraryScanIntervalHours` (0=off); scheduled scan fired on its own (watcher off) and discovered a new file; runtime pickup ≤5 min, cadence survives reboots (persisted anchor). Trigger endpoint generalised (`IManuallyTriggerableTask`) — task page Run-now works for it + metadata refresh. Review fixes: failed runs retry at check period (not a full interval), atomic queue dedup (parallel-enqueue race), honest "(Disabled)" suffix (+13 server, +3 client tests)
- [x] R-WI-009 ✅ **live-verified** — `PUT /users/{id}/streaming` admin setter + `StreamingModal`; admin sets 3000→transcode `-maxrate 3000k`. Removes the DB-edit workaround for cap testing (+6 server, +4 client tests)
- [x] R-WI-010 ✅ **live-verified** — seed `DlnaMaxContentRatings` + `DlnaSettingsCard` (enable/name/exposed-library checklist/per-type rating JSON); PUT→GET round-trip + admin-gating confirmed live. Review fix: 3-way `mergeSettingsPreservingEdits` so a card save no longer clobbers unsaved edits in other settings groups (+2 server, +8 client tests)
- [x] R-WI-011 ✅ **live-verified** — maintainer decision: new users NEVER rating-restricted by default (`MaxRating` default now `""`); visible Content-limits fieldset in Create User; `GET /account/content-limits` + account-page display of effective ceilings; single validated write path syncs legacy `MaxRating` (fixes "None (Unrestricted)" silently keeping the PG-13 movie cap). Review: no findings; took adjacent fixes (logout clears query cache, EC game rating, modal a11y/colors) (+13 server, +3 client tests)
- [x] R-WI-012 ✅ **live-verified** — burn-in pre-extracts to session-local `burnin.ass` (+ sanitized font-attachment dump, `:fontsdir=.`); apostrophe guard + broken escape removed; media paths never enter filter strings. Frame-level proof: burned subtitle visible in a segment transcoded from an apostrophe path. Review fixes: exit-code-strict extraction (no truncated burns, no 30s cap on big files), partial-output deletion, session-file reuse (+13 server tests)
- [x] R-WI-013 ✅ **live-verified** — `PlaybackHistory` table + migration; plays recorded in the progress-beat flow (threshold min(240s/60s, 50%), 6h dedup, MediaCompletionHelper completion); dead `PlayCount`/`LastPlayed` now real; **music player now emits listen beats**; self-scoped, ACL+rating-gated `GET /interaction/history`. Review fixes: completion→reopen cascade (was ~18 phantom plays/movie — live re-verified as 1), history ACL/rating leak, concurrent-beat race documented (+25 server, +3 client tests)
- [x] R-WI-013b ✅ **live-verified** — history privacy (maintainer-decided): user-owned "Record my history" toggle (existing users backfilled to ON — caught the scaffolder's silent OFF default) + "Clear my history" with confirm + atomic aggregate recompute; no anonymous mode by design; endpoints FullSession-only. Review fixes: MarkWatched wrote to the diary after opt-out (frozen now), clear made atomic, privacy logs demoted to Debug, prefs GET token-proofed (+9 server, +3 client tests)
- [x] R-WI-014 ✅ **live-verified** — poster.jpg/folder.jpg/fanart sidecars + NFO local `<thumb>`; cache-copied under source-distinct keys (`_local`/`_nfo`), symlink-resolving jail, local-wins precedence, enrichment invariant intact (byte-identical magenta-poster proof + OMDb still queried). Hardest review of the plan: 20 findings → rework → 2-verifier pass → 8 residuals, all closed (+19 server tests)
- [x] R-WI-015 ✅ **live-verified** — shared `useMediaSession` hook arbitrates the OS media controls between the music and video players (last-to-play owns; paused keeps for lock-screen resume; fallback on unmount; full clear when empty); video `seekto`/position ride the offset-aware seek logic per spec; episode next/prev (resume-preserving) bound from mount. 3-reviewer pass → 6 MED fixed (new-track re-claim, fastSeek restart storm, far-seek transient, double-offset raced seek, next-button semantics, `ratechange` sync) (+16 hook tests). **Discovered pre-existing bug (open):** album-card Play enqueues the album itself → `/stream/{albumId}` 404 → silent zombie "playing" bar; fix by enqueuing the album's tracks (see plan Checkpoint 7)
### Phase C — P2/P3 (valued features) — _✅ COMPLETE 2026-07-18 (all five items); server 1044/1/0, client 237. Out-of-scope bugs tracked in `docs/plans/post-phase-c-bug-backlog.md` (21 + 1 entries) for the next fix wave_
- [x] R-WI-016 ✅ **live-verified** — admin "Now Playing" card (15s poll): transcodes from the session registry + direct plays (video + all music) from the new `ActiveStreamRegistry` (response lifetime + 10s beat heartbeat, 60s idle expiry, handle-based release); confirm-gated Stop kills ffmpeg, deletes segments, frees the cap slot, audit-logged; direct plays read-only per spec. Live verification found + fixed 3 listing gaps (finished-encode vanish, preload phantom "Playing"→"Streaming", restart/cached-play invisibility via beat-creation). Review: 2 HIGH (dormant-session suppression + 24h phantom "Paused" rows), beat-creation ACL/rating gate, prune-race handle fix, terminate-404/error-state UX (+19 server, +12 client tests). **Adjacent fixes:** album-card play/queue enqueues TRACKS (Checkpoint 7 bug), hero "Play Now" on albums no longer opens the broken video player (HIGH), search play button no longer navigates to a dead route
- [x] R-WI-017 ✅ **live-verified** — **D-12 CLOSED**: global search now applies the rating ceiling (G-ceiling account live-proven blind to PG-13 titles by title/cast/genre) + episodes gated on the parent series passing the filter. Multi-field LIKE-over-joins: title/description/genre/cast/artist (top-level), tracks by title/artist/album (playable from the dropdown), episodes by title (rows route to their series); prefix-first ranking; LIKE metachars escaped + length cap; duplicate-library-group bug + unauthenticated dropdown thumbnails fixed; artist/album/series context lines on result rows. Perf: ~430ms @ 25k-item fixture worst case (FTS5 stays the specced follow-up) (+10 server, +6 client tests)
- [x] R-WI-018 ✅ **live-verified** — ::cue appearance settings (size/color/bg/edge + live preview, per-device) and a per-cue-anchored in-player sync nudge; live verification corrected the sync model (server already stream-aligns the VTT — user offset only); review fixes: Off-sentinel menu gating, no-store + cache-busted VTT, fail-closed alignment (+11 client tests)
- [x] R-WI-019 ✅ **live-verified** — POST /api/v1/scan behind the new `write:library` scope; review-hardened: admin-only sessions, ACL-filtered + anti-probe branches, real nested *arr payloads parsed, Test no-op, follow-up scan behind running jobs, rate-limited; Sonarr/Radarr guide in docs/user-guide/arr-webhook.md (+9 tests)
- [x] R-WI-020 ✅ **live-verified** — genre-affinity rows from VIDEO history (music excluded per review), visible-seeds-only steering, watched/seed exclusion, self-suppressing; rendered below Continue Watching (+5 tests)
### Post-Phase-C bug-fix wave — _✅ COMPLETE 2026-07-18 (baseline commit `b0d63a2`); server 1070/1/0, client 237, tsc clean. Full close-out in `docs/plans/post-phase-c-bug-backlog.md` §Fix wave summary_
- [x] Wave 1 server hardening ✅ — B-01 (bitrate cap gates direct play + remux at plan time, `bitrate.cap-forces-transcode` reason code, serve-time 403 backstop on `/stream/{id}` for over-cap video), B-02 (fabricated-sid master.m3u8 clamps resolution via `ResolutionRank` + codec to server settings), B-19 (hero rotation applies the content-rating ceiling — **live-tested integration**), B-20/B-21 (logged catch, comment dedup) (+18 tests)
- [x] Wave 2 subtitles/HLS ✅ **live-verified** — B-13/B-14 (master rendition → compliant single-segment WebVTT VOD playlist at new `GET /api/transcode/{id}/subtitles.m3u8`, `DEFAULT=NO,AUTOSELECT=NO`, no-store; verified live end-to-end incl. the far-seek offset), B-15 (no phantom `<track>`/404 when subs are Off), B-16 (unconditional subtitle-change session DELETE), B-17 (no orphan cue identifiers on dropped pre-seek cues) (+5 tests)
- [x] Wave 3 client polish ✅ — B-03 (album tracks carry artist/album name context — Include survives the join projection, **live-verified**), B-04 (detail-page Play enqueues album/artist TRACKS, never `/stream/{albumId}`), B-07 (search dropdown "No results found" + valid row markup — **live-verified**, no validateDOMNesting), B-08 (playTrack replaces the stale queue; repeat-all loops the track), B-09 (tokenized image fallback), B-10 (media-session artwork `sizes`), B-11 (interpolated i18n + `t()` on state strings) (+1 test)
- [x] Wave 4 search/theme/tests ✅ — B-05 (TV-library search finds episodes by TITLE only; browse stays series-only — **live-verified**), B-06 (comic issues title-only in global search + routed to the reader), B-12 (palette + brand-gradient moved to Tailwind v4 `@theme`; dead v3 `tailwind.config.js` deleted; **live-verified** + production CSS emits the utilities), T-01 (test factory → per-scope named shared-cache SQLite; the CPU-contention flakes' root cause was same-connection transaction collisions; two consecutive clean full parallel runs) (+2 tests)
- [x] Adversarial review of the whole diff ✅ — 1 HIGH found + fixed (B-15 guard skipped old-`<track>` cleanup when switching to Off → cleanup hoisted, re-verified green); all other fixes verified clean
- [ ] ⏸ B-18 PARKED — `read:library` scope enforcement blocked on §7 Q1 (enforce vs collapse; enforcing breaks existing tokens)

