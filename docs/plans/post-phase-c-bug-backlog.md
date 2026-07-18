# Post-Phase-C Bug Backlog

**Purpose.** Bugs and defects discovered during Phase C development (via adversarial
reviews and live verification) that are OUT OF SCOPE for the item that surfaced them.
Per maintainer instruction (2026-07-17): track here, fix **after** Phase C
(R-WI-016…020) is finished and properly tested. Each entry names where it was found
so the original review context is recoverable from the plan's §9 checkpoint notes.

**Status (2026-07-18): bug-fix wave complete.** Every engineering item below is
✅ FIXED except B-18 (parked — blocked on the maintainer's §7 Q1 scope-model
decision; enforcing would break existing tokens). Fix details in
[§ Fix wave summary](#fix-wave-summary-2026-07-18). Verification: full server
suite 1067/1067 green (twice, including a parallel run that exercised the T-01
fix), client 237/237 green, `tsc --noEmit` clean, production CSS build verified
for B-12.

Severity: **B-HIGH** = user-facing broken behavior or security hardening gap;
**B-MED** = wrong/confusing behavior with a workaround; **B-LOW** = cosmetic/latent.

## Product bugs

| # | Sev | Status | Found during | Bug |
|---|-----|--------|--------------|-----|
| B-01 | B-HIGH | ✅ fixed | R-WI-003 review (2026-07-16) | **Direct play bypasses the per-user bitrate cap** — the cap is enforced only on the transcode path; direct play serves the original file at full bitrate. |
| B-02 | B-HIGH | ✅ fixed | R-WI-002/005 review (2026-07-16) | **Fabricated-sid `master.m3u8` bypasses server-wide `MaxTranscodeResolution`/`OutputVideoCodec`** — the per-user bitrate half was closed; the server-wide-settings half remained. |
| B-03 | B-MED | ✅ fixed | R-WI-017 review (2026-07-17) | **Player bar shows "Unknown Artist" for album-page playback** — the album-tracks query never loaded the Artist/Album navigations, so `MediaItemDto.Metadata` stayed empty. |
| B-04 | B-MED | ✅ fixed | R-WI-016 review (2026-07-17) | **`MediaDetailPage.handlePlay` latent album-zombie** — called `playTrack(item)` for anything in a Music library; only safe because the layout hides Play for Album/Artist. |
| B-05 | B-MED | ✅ fixed | R-WI-017 review (2026-07-17) | **Per-library TV search cannot find episodes** — `LibraryRepository` narrowed to `Type == Series` before the search predicate; episodes were global-search-only. |
| B-06 | B-MED | ✅ fixed | R-WI-017 review (2026-07-17) | **Comic issues globally searchable while hidden from library browse**, and matching genre/description — a genre query flooded with individual issues. |
| B-07 | B-LOW | ✅ fixed | R-WI-017 review (2026-07-17) | Search dropdown rendered nothing (no "no results" message) for a zero-hit query; result rows were button-in-button invalid HTML. |
| B-08 | B-LOW | ✅ fixed | R-WI-017 review (2026-07-17) | A search-played single track that ended resumed a stale leftover queue (`audioStore.playTrack` didn't clear `queue`/`originalQueue`). |
| B-09 | B-LOW | ✅ fixed | R-WI-015 review (2026-07-17) | `PersistentPlayer.getImageUrl` fallback branch built tokenless `/api/v1` URLs (latent). |
| B-10 | B-LOW | ✅ fixed | R-WI-015 review (2026-07-17) | Media-session artwork entries lacked `sizes` — Android/ChromeOS lock-screen art could render scaled or fall back to a default icon. |
| B-11 | B-LOW | ✅ fixed | R-WI-016 review (2026-07-17) | `ActiveSessionsCard` i18n nits: concatenated aria-label/count strings; server state strings rendered raw. |
| B-12 | B-LOW | ✅ fixed | R-WI-009 review (2026-07-16) | Dead Tailwind `bg-primary`/`text-primary`/`bg-background` classes project-wide — the palette lived in a v3 `tailwind.config.js` that Tailwind v4 never reads. |

## Test infrastructure

| # | Sev | Status | Found during | Issue |
|---|-----|--------|--------------|-------|
| T-01 | B-MED | ✅ fixed | R-WI-015/017 full-suite runs | **Two integration tests flaked under CPU contention** (`AdminBackupIntegrationTests.CreateBackup_NonAdmin_Returns403`, `ApiTokenIntegrationTests.ReadOnlyToken_Is403_OnPlaylistCreate`). Root cause: the test factory handed ONE open SQLite connection to every EF scope and every background hosted service — same-connection transaction collisions, which `busy_timeout` (a cross-connection remedy) cannot fix. |

## Fix wave summary (2026-07-18)

Executed as four waves on branch `security/hardening-wave-2` (baseline commit
`b0d63a2`), each with tests; one adversarial review across the whole diff at the
end. **Review outcome:** 1 HIGH — the B-15 guard also skipped the removal of the
PREVIOUS `<track>` element when the user switched subtitles to Off, so its loaded
cues kept rendering (the `<video>` element survives HLS re-setup). Fixed by
hoisting the cleanup out of the guard; only track CREATION stays gated on a real
selection. Everything else verified clean. Live verification additionally covered
B-03/05/07/12/13/14 and the far-seek VTT offset path end-to-end.

**Wave 1 — server hardening.**
- **B-01** `StreamPlanService`: `SourceFitsBitrateCeiling` now gates BOTH direct
  play and remux (was transcode-only); over-cap plans transcode with reason code
  `bitrate.cap-forces-transcode`. Defense-in-depth at serve time:
  `StreamController` 403s a capped user's over-cap VIDEO (Movie/Episode) on
  `/stream/{id}`; Audio exempt by design (the cap is a video-streaming control).
  Tests: `StreamPlanServiceBitrateTests` (+2), `StreamBitrateCapIntegrationTests` (2).
- **B-02** `TranscodeController.GetMasterPlaylist`: when `storedPlan == null`
  (fabricated sid), resolution is clamped via `ResolutionRank` ordering and codec
  to `OutputVideoCodec` (unless "auto"). Tests: `TranscodeResolutionClampTests` (13).
- **B-19** `RecommendationService.GetHeroItemsAsync`: the live-ratings re-hydrate
  query applies `ApplyContentRatingFilter`; items filtered out are dropped from
  the shared (unfiltered) hero cache per user at read time.
  Test: `HeroRatingCeilingIntegrationTests`.
- **B-20** logged catch in `MediaController.GetRecentMedia`; **B-21** comment dedup.

**Wave 2 — subtitles/HLS.**
- **B-13/B-14** the master rendition now references a compliant WebVTT MEDIA
  PLAYLIST — new endpoint `GET /api/transcode/{id}/subtitles.m3u8`
  (`StreamResultService.GetSubtitlePlaylistResult`, single-segment VOD wrapper,
  `no-store`) — with `DEFAULT=NO,AUTOSELECT=NO` so the web client's own `<track>`
  doesn't double-render while native/iOS players can enable it manually.
- **B-15** `VideoPlayer` track creation excludes the `-1` Off sentinel;
  **B-16** subtitle-change DELETE cleanup is unconditional on position.
- **B-17** `OffsetWebVttTimestamps` buffers a pending cue-identifier line and
  flushes it only if its cue is kept — dropped pre-seek cues leave no orphans.
  Tests: `SubtitleRenditionTests` (4).

**Wave 3 — client polish.**
- **B-03** `MediaRepository.GetAlbumTracksWithInteractionsAsync` Includes
  Artist/Album (test proves the Includes survive the interaction-join projection:
  `AlbumTracksNameContextTests`).
- **B-04** `MediaDetailPage.handlePlay`: Album→`playPlaylist(albumTracks)`,
  Artist→`playPlaylist(artistTracks)`, plain track→`playTrack`.
- **B-07** `GlobalSearchResults`: explicit "No results found" empty state; row is
  `div[role=button]` with keyboard handling, play control stays the real button.
- **B-08** `audioStore.playTrack` resets `queue: []`, `originalQueue: [track]`
  (repeat-all loops the track, not the stale album).
- **B-09** tokenless fallback now routes through `attachAuthToApiUrl`.
- **B-10** artwork entry gets `sizes: '512x512'` (type omitted deliberately —
  covers may be jpg or png; a wrong hint can get the candidate filtered).
- **B-11** interpolated `t('{{count}} active')` / `t('Stop the stream for {{name}}')`;
  `t()` on server state strings.

**Wave 4 — search/theme/tests.**
- **B-05** TV-library search admits Episodes on TITLE-only matches (overview/genre
  would flood with inherited series text); browse without search stays Series-only.
- **B-06** global search: `ComicIssue` matches TITLE only; client routes issue
  results to `/read/{id}` (the detail page renders an empty shell for issues).
  Tests: 2 added to `GlobalSearchIntegrationTests`.
- **B-12** palette + `brand-gradient` moved into `@theme` in `index.css`; dead
  `tailwind.config.js` deleted; `--color-bg` kept as an alias var. Verified in the
  production build (`.bg-primary` etc. now emitted).
- **T-01** `SoftMediaWebApplicationFactory` rewritten: uniquely-named
  `Mode=Memory;Cache=Shared` SQLite DB, per-scope connections, keep-alive
  connection pins the DB, pool cleared on dispose.

**Parked.** B-18 — see below; unchanged.

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

| # | Sev | Status | Bug |
|---|-----|--------|-----|
| B-13 | B-MED | ✅ fixed | **HLS master emits a non-compliant subtitle rendition**: `#EXT-X-MEDIA:TYPE=SUBTITLES,…,URI=` points at the raw `.vtt` file (not a WebVTT media playlist) with `DEFAULT=YES` — hls.js tries to parse it as m3u8 (wasted retried requests, console errors) and creates a duplicate "Subtitles" TextTrack; native HLS players can't use it at all. |
| B-14 | B-MED | ✅ fixed | **iOS/native-HLS has no text subtitles at all**: the sidecar `<track>` is only added in the hls.js MANIFEST_PARSED path — the native-HLS branch never gets subtitles. Fixed jointly with B-13: the compliant rendition is what native players consume. |
| B-15 | B-LOW | ✅ fixed | A `<track>` element was appended even when subtitles are OFF (the −1 Off sentinel passed the `!== null` check) — one guaranteed 404 `subtitles.vtt?sub=-1` request + a phantom TextTrack per stream start. |
| B-16 | B-LOW | ✅ fixed | Subtitle change at position <1s never DELETEd the previous session — the old sub-index session's ffmpeg kept running alongside the new one until idle cleanup. |
| B-17 | B-LOW | ✅ fixed | `OffsetWebVttTimestamps` left orphan cue-identifier lines when dropping pre-seek cues. |

## Found during R-WI-019/020 review (2026-07-18, out of scope for the items)

| # | Sev | Status | Bug |
|---|-----|--------|-----|
| B-18 | B-MED | ⏸ PARKED | **The `read:library` scope is decorative** — `ScopePolicies.ReadLibrary` is defined but applied to ZERO endpoints; `MediaController` (search/recent/hero/home-rows) is plain `[Authorize]`, so any API token (e.g. `write:state`-only) reads all media metadata. Pre-existing model-wide gap from R-WI-006's partial rollout. **Blocked on §7 Q1** (enforce scopes vs collapse the scope model) — enforcing now would break existing tokens; needs the maintainer's call. |
| B-19 | B-MED | ✅ fixed | **The hero rotation never applies the content-rating ceiling** — the cache was built unfiltered and read-time applied only the library ACL; a ceiling-restricted user got over-ceiling titles in the hero. |
| B-20 | B-LOW | ✅ fixed | `MediaController.GetRecentMedia` wrapped its interaction fetch in a bare `catch {}`. |
| B-21 | B-LOW | ✅ fixed | Duplicated comment line in `LibraryScanQueueService.ProcessScanJobAsync`. |

**Accepted-by-design additions (R-WI-019/020):** `IsUnderRoot` is lexical — a path through a symlinked alias of a library root 404s (documented in the user guide: configure *arr with the library's own path); root matching is case-insensitive even on Linux (SMB-mount reality); the `alreadyQueued` response flag is best-effort (the queue's own dedup is atomic); home rows refresh at a 5-minute staleTime (a just-finished movie lingers briefly); users of small libraries (<4 candidates per row) see no personalized rows (spec: self-suppressing); the watched-exclusion approximates the completion rule (explicit flag OR ≥95% position — credits markers not consulted in SQL).

**Accepted-by-design additions (R-WI-018):** the sync control renders (inert) for burn-in/bitmap subtitle sessions — the client can't cheaply know bitmap-ness; Safari's system caption preferences can override `::cue` author styles; Firefox paints cue backgrounds as one box; appearance edits apply to an already-open player only on remount; the ClientSettings preview approximates player scale (`em`-based vs video-height-based).
