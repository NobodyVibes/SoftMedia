# SoftMedia Gap Analysis & Defect Report — 2026-07-15

*Code-verified and adversarially peer-reviewed. Supplements (does not supersede) the 2026-06-16 report: that report's gaps are largely tracked in the Master Implementation Plan; this one records what the July review found **beyond** it.*

**Method.** An 8-domain audit (playback/transcoding, library/metadata, music, books/games, users/security, client UX, integrations, admin/ops) produced 103 candidate gaps, each cross-checked against the roadmap (`docs/plans/roadmap/`), the Master Implementation Plan (`docs/plans/feature-implementation-plan-2026-06-16.md`), and the Phase-4 deferred register. The 17 highest-impact claims were then independently re-verified by adversarial review (each reviewer instructed to refute the claim): **13 confirmed, 4 partially true (corrected below), 0 refuted.**

**Downstream plan:** `docs/plans/remediation-and-gap-closure-plan-2026-07-15.md`.

## 1. Executive Summary

SoftMedia's core is mature and the June master plan already covers the biggest classic gaps (Docker, sidecar subtitles, HDR fallback, VAAPI, photos, OpenSubtitles). What this review adds: **(a)** a set of confirmed *defects and half-built features* — negotiated stream plans that are discarded before reaching ffmpeg (fake remux, forced stereo, far-seek parameter loss), an API-token scope system that is advertised but unenforced, a file watcher that ignores libraries created at runtime, and committed credentials in git; **(b)** a set of *verified-absent, untracked* features clustering in multi-user/family, admin observability, discovery, and music depth.

## 2. Confirmed Defects & Half-Built Features

Each entry carries its peer-review verdict. File references were verified against source on 2026-07-15.

### D-1 — Libraries created/edited at runtime get no file watcher — *confirmed*
`LibraryWatcher.ExecuteAsync` calls `InitializeWatchersAsync()` exactly once at startup (`Services/Scanning/LibraryWatcher.cs:136`); `CreateWatcher` is private; `LibraryService` touches the watcher only on **delete** (`Services/Media/LibraryService.cs:211`). Create (`:59`) and update (`:103`) never register watchers. Worse: a path-edit also leaks stale watchers on removed paths (only delete calls `RemoveWatchersForLibrary`). Real-time detection is silently absent for any library added after boot until restart. Manual scans still work, so the loss is real-time detection, not total invisibility.

### D-2 — Every video transcode forces stereo AAC 128k — *confirmed*
`TranscodeProfileBuilder` appends `-c:a aac -ac 2 -b:a 128k` unconditionally on every branch (`Services/Transcoding/TranscodeProfileBuilder.cs:366`); `TranscodeSettings` has no audio fields. Compounding it, `StreamPlanService.CreateTranscodePlan` already computes `targetAudioCodec="ac3", targetChannels=6` for surround-capable clients (`Services/Media/StreamPlanService.cs:447-454`) and **advertises it in the plan DTO**, but no parameter carries it to the transcode endpoint — the plan can promise ac3/6ch while ffmpeg emits aac/2ch. Direct play is unaffected.

### D-3 — "Remux" is not implemented; it re-encodes — *confirmed*
`CreateRemuxPlan` points at the identical `/api/transcode/{id}/master.m3u8` URL as a full transcode with no distinguishing parameter (`StreamPlanService.cs:387`); the endpoint cannot branch on playback method, and no `-c copy` exists anywhere in the server. The comment at `StreamPlanService.cs:336` ("we just copy streams") documents intent never implemented. Remux-eligible sources pay full decode/encode cost with quality loss.

### D-4 — Far seeks drop all negotiated stream parameters (incl. the admin bitrate cap) — *confirmed*
On a seek beyond the transcoded range, `VideoPlayer.handleSeekToTime` (client) kills the session and re-requests `master.m3u8` with only `token/seek/sid/sub/audio` — losing the resolution cap, client bitrate cap, negotiated codec, and HDR-preserve flag, and **bypassing the per-user `MaxStreamBitrateKbps`**, which is enforced only at plan time (`Controllers/TranscodeController.cs:117`), not on `master.m3u8` requests (`:136`). Quality/subtitle/audio *changes* are unaffected (they re-run the plan).

### D-5 — API-token scopes are advertised but essentially unenforced — *confirmed*
`ScopePolicies.ReadLibrary/ReadState` are defined (`Services/Identity/ScopeAuthorization.cs:67-68`) but attached to **zero** endpoints; `WriteState` guards only `InteractionController` (`Controllers/InteractionController.cs:16`). A read-only token passes every other plain-`[Authorize]` mutating endpoint (playlists, watchlist, preferences…). Worse: the token principal carries the owner's role claim, so a read-only token minted by an admin also satisfies `[Authorize(Roles="Admin")]`. The scope model constrains essentially nothing outside `InteractionController`.

### D-6 — Per-user bitrate cap has no write surface — *confirmed*
`User.MaxStreamBitrateKbps` is enforced (`TranscodeController.cs:68`) but no endpoint or UI writes it; settable only by direct DB edit. Known-unfinished: the phase-1 spec calls for "Admin-only edit" (`docs/plans/roadmap/phase-1-operational-trust.md:213`); roadmap open question #3 (admin-only vs self-service) was never resolved.

### D-7 — Plays are timestamped but never counted — *partially true (corrected)*
`MediaItem.PlayCount` (`Models/MediaItem.cs:30`) and `MediaItem.LastPlayed` (`:32`) are dead columns — never written. **Correction to the original claim:** user-level `UserMediaInteraction.LastPlayed` *does* work (set on every progress update / mark-watched, `Services/Media/UserMediaInteractionService.cs:114,141`) and drives Continue Watching. What's missing: play *counts* and per-play history rows for audio/video (the book reader has real `ReadingSession` rows; media has nothing comparable).

### D-8 — New non-admin users silently default to a PG-13 movie ceiling — *partially true: core confirmed, API-visibility clause corrected*
`User.MaxRating` defaults to `"PG-13"` (model initializer; never overridden at creation), filtering R/NC-17/unrated movies from browse and 404ing them on stream (see `Services/Security/ContentRating/UserContentRatingProvider.cs:82` and `UserRatingCeilings`). No signup or admin create-user UI mentions it. Nuance: the value *is* machine-visible in `UserDto.maxRating` — the invisibility is a UI problem, not an API one. Ceiling gates movies only by default.

### D-9 — DLNA cannot be enabled or configured from the UI — *confirmed*
Zero DLNA references in the client. `DlnaMaxContentRatings` is not even seeded into the settings table (read with default `""` only); the security-critical `DlnaExposedLibraries` allowlist (default: expose nothing) has no UI either — while `EnableDlna`'s own seeded description instructs admins to configure it. The shipped P4-004 feature is unreachable without direct API/DB manipulation.

### D-10 — Subtitle burn-in silently skipped for apostrophe paths; the escape code is wrong but dead — *partially true (corrected)*
**Correction:** there is no silent runtime breakage. `TranscodeProfileBuilder.cs:98-104` deliberately detects apostrophes in the input path and disables text-subtitle burn-in with a `LogWarning`. The escaping at `:313` (`.Replace("'", @"\\'")`) *is* incorrect for ffmpeg's two-level filter quoting (inside single quotes `av_get_token` copies verbatim; the `'` would close the quote and the path would resolve wrongly) — but it is unreachable dead code behind the guard. Net defect: apostrophe-titled media (e.g. *It's a Wonderful Life*) never gets burned-in text subtitles, by logged design workaround. No test covers apostrophe paths.

### D-11 — Credentials, an admin JWT, and DB snapshots are committed to git — *partially true as first claimed; reality worse (corrected upward)*
Tracked in git: `login.json` at the repo root **and** `src/SoftMedia.Server/login.json` (both `admin`/`admin123`); `src/SoftMedia.Server/token.json` (an admin JWT access token); **four** full DB snapshots `src/SoftMedia.Server/softmedia.db.pre-restore-2026…` (the `.pre-restore-*` suffix escapes the `*.db` ignore rule; snapshots contain user password hashes and library data); plus ~25 debris files (`build*.log`, `dump.txt`, `dunedump.*`, `test_*.txt`, `ef_errors*.txt`, `DumpDune.cs`, `plan_copy.md`, ad-hoc `*.ps1` diagnostics). Deleting is insufficient — history retains them; rotation + the already-queued history purge are required before the repo goes public.

### D-12 — Global search bypasses the content-rating ceiling — *confirmed (found 2026-07-15 during plan design review)*
`MediaController.GlobalSearch` filters with `.ApplyLibraryAccessFilter(access)` **only** (`Controllers/MediaController.cs:176-185`), whereas every other browse path — `GetMediaItem` (`:52`), the per-library repository path (`LibraryRepository.cs:127`), watchlist, collections, Continue Watching, and DLNA — also applies `.ApplyContentRatingFilter`. A rating-restricted (e.g. child) account can surface blocked titles by searching their names; the planned multi-field search expansion would widen the leak to descriptions and cast. Fix owned by R-WI-017 in the remediation plan (a standalone one-line fix, cherry-pickable ahead of the full search work).

## 3. Verified-Absent Feature Gaps (untracked, not deferred)

Everything below was grep/read-verified absent and does **not** appear in the master plan, roadmap phases, or the P4 register. Items with a P4/plan adjacency say so.

**Users & families:** kid profiles / profile switching; parental PIN gate (rating ceilings exist — only the PIN mechanism is missing); Quick Connect-style short-code device login; user-facing active-session/device list with sign-out; self-service password recovery (no email infra); admin action audit log; access schedules; avatars.

**Admin & observability:** "Now Playing" sessions dashboard with terminate — `TranscodeSessionManager` keeps a rich in-memory registry (`GetAllSessions()`) to build on, but **direct play is tracked nowhere, even internally** (`StreamController` has no session concept); server stats (disk free per library path, counts, transcode load); log viewer + file logging with runtime level; restart/shutdown button; About page with version; server-side folder picker for library paths; hardware-transcode capability probe/test; encode-failure → software fallback; SQLite VACUUM/integrity task; run-now for all scheduled tasks (only Metadata Refresh has it); settings UI for orphaned server-side groups (backup schedule, webhook policy — DLNA is D-9); Linux systemd unit/docs (Docker is planned; systemd is not); release/CI packaging (no CI exists — adjacent to blocked P1-WI-004 half).

**Playback & player:** Media Session API — zero `navigator.mediaSession` references (lock-screen/media-key control absent for video and music); subtitle appearance settings (size/color/background — language and burn-in settings exist in `ClientSettings.tsx:56,216`; appearance does not); in-player subtitle timing offset; SyncPlay/watch-together; download-original button; Chromecast subtitle/audio-selection carry-over (custom-receiver future option noted in the Chromecast plan).

**Library automation:** scheduled/periodic scans (watcher + manual only; `MetadataRefreshService` re-enriches existing DB items and does not rediscover files); local artwork sidecars for movies/TV — **nothing in the repo reads `poster.jpg`/`fanart.jpg` at all**; music reads `cover/folder/album/front/artist.*` (`Services/Scanning/MusicScanner.cs:31,451`); NFO art is http(s)-URL-only (`Services/Metadata/Nfo/NfoXmlParser.cs:155`); artwork picker/upload; missing-episode tracking; extras recognition; NFO write-back; per-library settings; scan cancellation; opt-in delete-from-disk; duplicate report.

**Discovery & UX:** search matches **Title only** via `EF.Functions.Like` (`Controllers/MediaController.cs:185`); the per-library search is likewise title-only (`LibrariesController.cs:142` → `Services/Infrastructure/LibraryRepository.cs:179`); music tracks and episodes are globally unsearchable (excluded types); global search also skips the content-rating filter — see **D-12**. Personalized home rows; person/actor pages (the frontend half is the checklist's "Future" item); "More Like This"; watch-history page (server data exists in `UserMediaInteraction`; no page); home-row customization; genre chips; filter/sort persistence; Ctrl+K; favorites page.

**Music depth:** play counts/history (D-7); compilation/Various-Artists handling; instant mix; queue persistence across refresh; artist images/bios; Chromecast audio; Subsonic/OpenSubsonic compatibility layer (strategic: unlocks mature third-party clients without first-party apps, sidestepping the P4-002 deferral); audiobooks (a `Chapter` entity exists for video chapters; audiobook support does not); podcasts.

**Integrations:** inbound webhook / path-targeted scan trigger for *arr; playback webhook events (`media.played` is planned; start/stop/progress are not); Plex/Jellyfin migration importer; webhook payload presets (ntfy/Gotify/Home Assistant); readiness endpoint with dependency checks (`/api/v1/health` is deliberately liveness-only); committed OpenAPI artifact; per-user notification center; persistent webhook outbox; OPDS feed for book libraries (books are otherwise the most complete domain).

## 4. Excluded From This Report (already tracked or deferred)

Tracked in the Master Implementation Plan (not re-reported): data-root foundation, Docker, sidecar subtitles, HDR tone-mapping fallback, VAAPI, photos, OpenSubtitles, remaining webhook events, CI/OMDb-key half of P1-WI-004, in-app AGPL §13 link. Deferred by maintainer decision (respected): Live TV (P4-001), native apps (P4-002), AirPlay (P4-003), editions (P4-005), OIDC (P4-006), scrobbling (P4-007), lyrics/ReplayGain/EQ (P4-008), SAML/LDAP (P4-010), passkeys (P4-011), shader upscaler (P4-012), AV1-FGS (P4-013), mpv client (P4-014), smart playlists/tags (P3-WI-004 drop), OpenSubtitles hash-matching variant (P3-WI-002 drop; re-scoped as SB1-SB5).

## 5. Verification Log

- 2026-07-15 — 8-domain fan-out audit (24 agents): domain maps + 103 candidate gaps; roadmap catalog compiled from `docs/plans/**`, `.docs/project_checklist.md`, `CHANGELOG.md`.
- 2026-07-15 — inline verification of 20+ load-bearing claims (grep/read against source; ripgrep's gitignore quirk bypassed for `Services/Media` and `Data`).
- 2026-07-15 — adversarial peer review, 17 claims × 1 independent refuter each: 13 CONFIRMED / 4 PARTIALLY-TRUE / 0 REFUTED. The four partially-true claims are D-7, D-8, D-10, D-11; corrections folded into their §2 entries (each carries a "partially true …" verdict label).
- 2026-07-15 — design review of the derived remediation plan (5 independent reviewers: consistency, playback design, library/identity design, data/UX design, conflicts/completeness): 2 critical spec flaws in the plan's R-WI-002 corrected (plan-store lifecycle vs session teardown), ~20 further spec corrections applied, and one **new live defect discovered and recorded as D-12** (search rating-filter bypass).
