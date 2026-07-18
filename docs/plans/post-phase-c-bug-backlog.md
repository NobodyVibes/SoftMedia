# Post-Phase-C Bug Backlog

**Purpose.** Bugs and defects discovered during Phase C development (via adversarial
reviews and live verification) that are OUT OF SCOPE for the item that surfaced them.
Per maintainer instruction (2026-07-17): track here, fix **after** Phase C
(R-WI-016…020) is finished and properly tested. Each entry names where it was found
so the original review context is recoverable from the plan's §9 checkpoint notes.

Severity: **B-HIGH** = user-facing broken behavior or security hardening gap;
**B-MED** = wrong/confusing behavior with a workaround; **B-LOW** = cosmetic/latent.

## Product bugs

| # | Sev | Found during | Bug |
|---|-----|--------------|-----|
| B-01 | B-HIGH | R-WI-003 review (2026-07-16) | **Direct play bypasses the per-user bitrate cap** — the cap is enforced only on the transcode path; direct play serves the original file at full bitrate. Deferred as a separate hardening item. |
| B-02 | B-HIGH | R-WI-002/005 review (2026-07-16) | **Fabricated-sid `master.m3u8` bypasses server-wide `MaxTranscodeResolution`/`OutputVideoCodec`** — the per-user bitrate half was closed; the server-wide-settings half remains. |
| B-03 | B-MED | R-WI-017 review (2026-07-17) | **Player bar shows "Unknown Artist" for album-page playback** — `LibraryService.MapToDto` (album-tracks endpoint) never populates `MediaItemDto.Metadata`; only the search endpoint does (R-WI-017). Same nav-conditional pattern fixes it. |
| B-04 | B-MED | R-WI-016 review (2026-07-17) | **`MediaDetailPage.handlePlay` latent album-zombie** — calls `playTrack(item)` for anything in a Music library; only safe because `MediaDetailLayout` (a different file) hides the Play button for Album/Artist. Same `/stream/{albumId}` 404-zombie class fixed elsewhere. |
| B-05 | B-MED | R-WI-017 review (2026-07-17) | **Per-library TV search cannot find episodes** — `LibraryRepository` narrows to `Type == Series` before the search predicate; episodes are global-search-only. |
| B-06 | B-MED | R-WI-017 review (2026-07-17) | **Comic issues globally searchable while hidden from library browse**, and they now match genre/description — a genre query can flood with individual issues for comic users. |
| B-07 | B-LOW | R-WI-017 review (2026-07-17) | Search dropdown renders nothing (no "no results" message) for a zero-hit query; result rows are button-in-button invalid HTML (React `validateDOMNesting` warning). |
| B-08 | B-LOW | R-WI-017 review (2026-07-17) | A search-played single track that ends resumes a stale leftover queue from earlier in the session (`audioStore.playTrack` doesn't clear `queue`/`originalQueue`; repeat-all can restart the old album). |
| B-09 | B-LOW | R-WI-015 review (2026-07-17) | `PersistentPlayer.getImageUrl` fallback branch builds tokenless `/api/v1` URLs (latent — no current posterPath shape hits it). |
| B-10 | B-LOW | R-WI-015 review (2026-07-17) | Media-session artwork entries lack `sizes`/`type` — Android/ChromeOS lock-screen art may render scaled or fall back to a default icon. |
| B-11 | B-LOW | R-WI-016 review (2026-07-17) | `ActiveSessionsCard` i18n nits: concatenated aria-label/count strings; server state strings ("Serving"/"Streaming"/"Paused") rendered raw, bypassing `t()`. |
| B-12 | B-LOW | R-WI-009 review (2026-07-16) | Dead Tailwind `bg-primary`/`text-primary`/`bg-background` classes project-wide (colors live in `:root`, not `@theme`) — components using them silently render unstyled. Repo-wide `@theme` fix. |

## Test infrastructure

| # | Sev | Found during | Issue |
|---|-----|--------------|-------|
| T-01 | B-MED | R-WI-015/017 full-suite runs | **Two integration tests flake under CPU contention** in full parallel runs: `AdminBackupIntegrationTests.CreateBackup_NonAdmin_Returns403`, `ApiTokenIntegrationTests.ReadOnlyToken_Is403_OnPlaylistCreate`. Both pass in isolation and in clean runs. A flake firing during a real regression would be wrongly dismissed — needs a stabilization pass (likely shared in-memory SQLite busy-timeout under load). |

## Accepted-by-design (no fix without maintainer decision — listed for completeness)

- Dashboard transcode playhead leads the true viewer position by the client's prefetch buffer (~30s).
- ≤60s transient double-listing on a direct-play→transcode quality switch.
- Unrated episodes hidden under a TV ceiling (fail-safe; consistent with all browse paths).
- Non-ASCII case-sensitive search on both search paths (SQLite without ICU).
- Sessions registry entry cap is soft against concurrently-OPEN connections (Kestrel bounds those).
- Admins see titles from libraries they are themselves ACL-restricted from in the sessions dashboard (admin-oversight surface).

## Maintainer-gated (not engineering items)

- R-WI-001 remainder: rotate JWT secret + admin password; git-history purge of old binaries/secrets.
- Plan §7 open questions: Q1 (scope granularity), Q3 (bitrate-cap self-service), Q4 (history-rewrite timing), Q6 (WS-6 sequencing).

---
*New findings from R-WI-018/019/020 reviews are appended below as they are discovered.*

## Found during R-WI-018 review (2026-07-18, out of scope for the item)

| # | Sev | Bug |
|---|-----|-----|
| B-13 | B-MED | **HLS master emits a non-compliant subtitle rendition**: `#EXT-X-MEDIA:TYPE=SUBTITLES,…,URI=` points at the raw `.vtt` file (not a WebVTT media playlist) with `DEFAULT=YES` (`HlsManifestService.cs:49-52`) — hls.js tries to parse it as m3u8 (wasted retried requests, console errors) and creates a duplicate "Subtitles" TextTrack; native HLS players can't use it at all. Emit a proper subtitle playlist or drop the line (the client adds its own `<track>`). |
| B-14 | B-MED | **iOS/native-HLS has no text subtitles at all**: the sidecar `<track>` is only added in the hls.js MANIFEST_PARSED path — the native-HLS branch (`VideoPlayer.tsx` no-MSE path) never gets subtitles, so the R-WI-018 appearance/sync features silently don't exist there (platform gap; pairs with B-13's rendition fix). |
| B-15 | B-LOW | A `<track>` element is appended even when subtitles are OFF (`selectedSubtitleTrack !== null` is true for the −1 Off sentinel at the track-creation site) — one guaranteed 404 `subtitles.vtt?sub=-1` request + a phantom TextTrack per stream start. |
| B-16 | B-LOW | Subtitle change at position <1s never DELETEs the previous session (`isSubtitleChange && startPosition > 0` guard) — the old sub-index session's ffmpeg keeps running alongside the new one until idle cleanup. |
| B-17 | B-LOW | `OffsetWebVttTimestamps` leaves orphan cue-identifier lines when dropping pre-seek cues (cosmetic; parsers discard them). |

## Found during R-WI-019/020 review (2026-07-18, out of scope for the items)

| # | Sev | Bug |
|---|-----|-----|
| B-18 | B-MED | **The `read:library` scope is decorative** — `ScopePolicies.ReadLibrary` is defined but applied to ZERO endpoints; `MediaController` (search/recent/hero/home-rows) is plain `[Authorize]`, so any API token (e.g. `write:state`-only) reads all media metadata. Pre-existing model-wide gap from R-WI-006's partial rollout. |
| B-19 | B-MED | **The hero rotation never applies the content-rating ceiling** — `UpdateHeroCacheAsync` builds the cache unfiltered and `GetHeroItemsAsync` applies only the library ACL at read time; a ceiling-restricted user gets over-ceiling titles (name/poster/overview) in the hero, unlike every browse path. Pre-existing. |
| B-20 | B-LOW | `MediaController.GetRecentMedia` wraps its interaction fetch in a bare `catch {}` (silent-failure pattern). Pre-existing. |
| B-21 | B-LOW | Duplicated comment line in `LibraryScanQueueService.ProcessScanJobAsync`. Trivial, pre-existing. |

**Accepted-by-design additions (R-WI-019/020):** `IsUnderRoot` is lexical — a path through a symlinked alias of a library root 404s (documented in the user guide: configure *arr with the library's own path); root matching is case-insensitive even on Linux (SMB-mount reality); the `alreadyQueued` response flag is best-effort (the queue's own dedup is atomic); home rows refresh at a 5-minute staleTime (a just-finished movie lingers briefly); users of small libraries (<4 candidates per row) see no personalized rows (spec: self-suppressing); the watched-exclusion approximates the completion rule (explicit flag OR ≥95% position — credits markers not consulted in SQL).

**Accepted-by-design additions (R-WI-018):** the sync control renders (inert) for burn-in/bitmap subtitle sessions — the client can't cheaply know bitmap-ness; Safari's system caption preferences can override `::cue` author styles; Firefox paints cue backgrounds as one box; appearance edits apply to an already-open player only on remount; the ClientSettings preview approximates player scale (`em`-based vs video-height-based).
