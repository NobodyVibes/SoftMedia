# SoftMedia — Feature & Settings Gap Analysis vs Plex / Jellyfin

**Author:** Engineering review
**Date:** 2026-05-07
**Status:** Peer-reviewed
**Scope:** What is missing from SoftMedia that a self-hostable home media server is realistically expected to have, and where SoftMedia can plausibly do *better* than Plex and Jellyfin.

This report intentionally only flags gaps that (a) are absent from the code today and (b) are commonly relied on by Plex/Jellyfin/Emby users. It does **not** propose work that conflicts with the project's privacy/local-first charter (e.g. no cloud relay, no analytics).

---

## 1. Executive Summary

SoftMedia already covers the hard parts that most "tutorial" media servers never finish: a real metadata pipeline with provider-typed routing, HLS transcoding with NVENC/QSV/AMF/AV1 paths, HDR tone-mapping with subtitle-burn override, intro/credits detection (chapters + audio fingerprint), per-user library ACLs, refresh-token rotation with reuse detection, image-proxy hardening, parental-rating enforcement on both list and direct-stream paths, comic/EPUB readers, and gapless music playback. That is a strong baseline.

The main gaps fall into three buckets:

1. **Operational/admin features** that hobbyists expect from a "real" media server — backup/restore, system health dashboards, notifications/webhooks, bulk metadata edit, API tokens.
2. **Playback ecosystem** — DLNA/Chromecast/AirPlay casting, SyncPlay/Watch-Together, offline downloads/sync, native mobile apps, and trickplay (pre-generated scrubber sprites).
3. **Identity & remote access polish** — 2FA/passkeys, optional OIDC/SSO, and a first-party reverse-proxy/Tailscale onboarding flow.

There is also a credible "do better than Plex/Jellyfin" story around (a) **no telemetry, no account wall, no remote-disable kill-switch**, (b) **first-class read-along ebook + comic experience** (already partially built), (c) **HDR-aware transcoding with subtitle-burn override** (already built and is genuinely better than Jellyfin's default), and (d) a **smart-transcoding decision panel** that explains *why* a stream is being remuxed/transcoded — Plex hides this, Jellyfin shows raw FFmpeg args.

---

## 2. What SoftMedia Already Has (baseline, verified in code)

Listed so the reader can calibrate the gaps below. Each line cites a file I opened during this audit.

- HLS transcoding with hardware acceleration paths for NVIDIA NVENC, Intel QSV, AMD AMF, plus software libx264/libx265 — `src/SoftMedia.Server/Services/Transcoding/TranscodeProfileBuilder.cs:395-568`.
- AV1 encoder selection (`av1_nvenc` / `av1_qsv` / `av1_amf`) and fMP4 segment output when AV1 or HDR-passthrough is in play — same file, `useFmp4` branch around line 338.
- HDR (HDR10/HLG) detection + CUDA tone-mapping pipeline with subtitle-burn override that forces tone-mapping when subtitles must be burned — `TranscodeProfileBuilder.cs:127-145`.
- Range-request video/audio streaming, on-demand HLS, on-demand frame preview at arbitrary timestamp — `Controllers/TranscodeController.cs:240-260` (`GET /api/transcode/{id}/frame?time=`).
- Intro & credits detection (chapter-source priority + audio fingerprint cross-episode), with player skip pill — `Models/MediaItem.cs:63-105`, feature doc `docs/user-docs/features/skip-intro-credits.md`.
- Type-locked metadata providers: TVMaze, Wikidata SPARQL, OMDb (key), MusicBrainz, Open Library, ComicInfo.xml + Comic-Wikidata, EXIF for photos — `Services/Metadata/*Provider.cs`.
- ComicInfo.xml reading and CBZ/CBR/EPUB readers with bookmarks, highlights, reading sessions, TTS, in-book search, dictionary lookup — `Models/{Bookmark,Highlight,ReadingSession,UserReaderPreferences}.cs`, `components/reader/*`.
- Watchlist, Playlists, Collections, Per-user Library ACL — `Controllers/{Watchlist,Playlists,Collections}Controller.cs`, `Models/UserLibraryAccess.cs`.
- Auth: Argon2id, JWT access + rotating HttpOnly refresh-cookie, replay-detected chain revocation, per-IP rate limit on signup/login — `Controllers/AuthController.cs:212-223`, `Services/Identity/`.
- Parental controls enforced on both list filter and direct stream-by-ID — SDD §6.2, code in `Services/Security/`.
- Image proxy with host allow-list, MIME validation, size cap, negative-cache sentinel — `Services/Media/ImageCacheService.cs`.
- Real-time file watcher + admin "file watcher issues" dashboard with retry — `Services/Scanning/LibraryWatcher.cs`, `pages/SettingsPage.tsx:29-65`.
- HLS session manager with throttle/pause/resume + transcode-temp cleanup — `Services/Transcoding/TranscodeSessionService.cs`, `docs/user-docs/features/transcode-cleanup.md`.
- SignalR hub for live scan/notification push — `Hubs/MediaHub.cs`.
- Music: gapless dual-engine playback, queue persistence, audio visualizers (FFT analyser hook present) — `useAudioAnalyser.ts`, `components/player/visualizers/`.
- A11y/universal-client rules baked into the styleguide; focus-visible + 44px targets enforced as a project rule.

---

## 3. Critical Missing Features (a self-hosted media server is expected to have these)

Each row says (a) what's missing, (b) verification — which file/grep proves it's missing today, and (c) why it matters.

### 3.1 Casting / Throwing to a TV

- **Missing:** No DLNA/UPnP renderer, no Chromecast sender, no AirPlay sender. Grep for `DLNA|chromecast|airplay|UPnP` returns only `package-lock.json` noise.
- **Why it matters:** "Cast to my LG/Samsung/Apple TV" is the #1 reason hobbyists keep paying for Plex Pass. Without it, the user has to side-load via WebOS or use a browser tab on the TV.
- **Realistic scope:** Chromecast sender via `cast.framework` in the SPA is the cheapest win; DLNA renderer is nice-to-have for older TVs; AirPlay receiver is hard and platform-restricted.

### 3.2 Native / PWA Mobile Story

- **Missing:** No service worker, no offline shell, no `vite-plugin-pwa` config in the repo. SDD §8.4 lists PWA as a goal but no PWA manifest or `vite-plugin-pwa` import is present in the client today.
- **Verification:** grep for `service.worker|workbox|vite-plugin-pwa` returned no source matches in `SoftMedia.Client/src/`.
- **Why it matters:** Plex/Jellyfin/Emby all ship native apps. SoftMedia's "Universal Client" story explicitly defers mobile to Phase 2 — that's defensible, but at minimum a PWA + Add-to-Home-Screen + offline UI shell would close most of the perceived gap.

### 3.3 Offline / Sync Downloads

- **Missing:** No "download for offline" feature for movies/episodes/albums/books. The grep hits for `offline|download` are dictionary download UI (book reader) and a `service worker` mention only in unrelated docs.
- **Why it matters:** Plex Pass "Sync" and Jellyfin's mobile-app downloads are the most-used premium features. Even a "download original file when client supports the codec" path would be valuable.

### 3.4 SyncPlay / Watch Together

- **Missing:** No code path for synchronised multi-client playback. Grep for `SyncPlay|WatchTogether|group.watch` returns nothing.
- **Why it matters:** Jellyfin SyncPlay is one of its headline community features. SoftMedia already has SignalR (`Hubs/MediaHub.cs`); the transport is in place — only the room/state-machine is missing.

### 3.5 Live TV / DVR / IPTV Playlists

- **Missing:** No HDHomeRun integration, no XMLTV/EPG ingestion, no `.m3u` IPTV playlist support, no DVR scheduler. Grep for `livetv|hdhomerun|tvheadend|epg|m3u.playlist|dvr` returns nothing relevant.
- **Why it matters:** This is one of Plex's biggest power-user features. Skipping it is defensible (it's a huge feature surface), but a basic *IPTV-playlist passthrough* (load `.m3u`, list channels, transcode through the existing HLS pipeline) is a few hundred lines and would punch above its weight.

### 3.6 Subtitle Auto-Download

- **Missing:** No OpenSubtitles/Subscene/Addic7ed integration. Subtitle service only extracts embedded subs and adjusts timestamps — `Services/Media/SubtitleService.cs`.
- **Why it matters:** Sidecar subtitle ingestion exists, but auto-fetching on missing-subs is the standard expectation. Open-source providers exist; respecting per-user language preference would dovetail with the existing `PreferredAudioLanguage` setting.

### 3.7 Backup / Restore (one-click)

- **Missing:** No export/import of `softmedia.db` + settings + per-user state. There's a `docs/todos/feature-shortlist/02-admin-backup-endpoint.md` noting it's a backlog item.
- **Why it matters:** SQLite + WAL is trivially backupable, but users need a button. Without it, "I lost my watch state" becomes a support burden.

### 3.8 Notifications / Webhooks / Email

- **Missing:** Internal `NotificationService` exists (`Services/Infrastructure/NotificationService.cs`) but it only writes rows to the in-app `SystemNotifications` table. No webhook senders, no Discord/Slack/Telegram/Pushover/ntfy fan-out, no SMTP. Grep for `Webhook|Discord|Slack.notify|Telegram|Pushover|gotify|ntfy|smtp` returns no source matches.
- **Why it matters:** Hobbyists want "ping me when new episode added" and "alert me when transcode failed". A single generic outbound-webhook posting JSON would cover 90% of demand and stay aligned with the privacy charter (user-configured endpoints only, no first-party cloud).

### 3.9 API Tokens / Personal Access Tokens

- **Missing:** Auth only issues short-lived JWT + rotating refresh cookie. No long-lived API tokens for third-party tools (e.g. mobile homepages, dashboards, custom scripts). Grep for `api.token|api.key|personal.access` returns only third-party metadata API keys (OMDb).
- **Why it matters:** Every mature self-hosted app — Sonarr, Radarr, Jellyfin, Plex — exposes per-user API tokens with scoped permissions. Without them, "give my Home Assistant my Recently-Added feed" is impossible.

### 3.10 2FA / Passkeys / Optional SSO

- **Missing:** No TOTP, no WebAuthn/passkey, no OIDC. Grep for `two.factor|2fa|TOTP|webauthn|passkey|OIDC|OpenID` returns zero relevant source matches (only `OAuth` mentions in docs and OMDb test fixtures).
- **Why it matters:** Once SoftMedia is exposed via DuckDNS/Caddy (SDD §6.1 Method B), password-only login is the weak link. TOTP via `Otp.NET` is ~150 lines. OIDC (Authelia/Authentik/Keycloak) closes the SSO story for homelabs and is a notable Jellyfin gap.

### 3.11 Trickplay / Scrubber Sprite Sheets

- **Missing:** Player has on-demand `/api/transcode/{id}/frame?time=` (good for low cost), but no pre-generated sprite sheet (Plex "BIF", Jellyfin "trickplay"). Grep for `trickplay|sprite.thumb|BIF` returns nothing.
- **Why it matters:** On-demand frames mean every scrub triggers an FFmpeg invocation. A pre-baked `WxH-grid.jpg` per episode is what makes Plex's hover-scrub feel instant. The on-demand path is acceptable as a fallback but should not be the only mode.

### 3.12 Photos Library

- **Missing:** `MediaType.Photo = 6` exists and `ExifMetadataProvider` extracts EXIF, but there's no `PhotosController`, no photo scanner, no album/timeline frontend. Grep for `PhotosController|photo.scan|/api/v1/photos` returns no source matches.
- **Why it matters:** Photos are explicitly Phase 2 in SDD §4.1 — not a defect, but worth flagging as the most common "but does it do photos?" question.

### 3.13 Multi-Version / Edition Support

- **Missing:** No way to attach Director's Cut / Theatrical / Extended / 4K-vs-1080p as alternate playable versions of one logical movie. Grep for `multi.version|alternate.version|edition|director.cut` returns only metadata-test noise (`OpenLibrary` editions).
- **Why it matters:** Plex's "Versions" and Jellyfin's "Multiple Versions" are how power users dedupe their libraries. Without it, two copies of *Blade Runner 2049* show as two cards.

### 3.14 Smart Playlists / Tags

- **Missing:** No `Tag` model, no smart/rule-based playlists. Grep for `class.*Tag|TagsController|smart.playlist|rule.based` returns no source matches.
- **Why it matters:** Smart playlists ("everything 4K + HDR + unwatched", "all 80s synthwave") are a music-server staple. A user-tag system would also unblock collection curation that doesn't fit movie-franchise links.

### 3.15 Scrobbling / External Sync

- **Missing:** No Trakt, Last.fm, AniList, MyAnimeList scrobbling. Grep for `Trakt|Last.?fm|scrobble|AniList|MyAnimeList` returns no source matches.
- **Why it matters:** Optional, but a notable Plex feature (Trakt via Webhooks, Last.fm via Plex). Could be implemented as outbound-webhook recipes once §3.8 lands.

### 3.16 TV Calendar / "Coming Soon"

- **Missing:** No upcoming-episode calendar view despite TVMaze providing airdate data. Grep for `upcoming|calendar.episode|next.aired` returns no source matches.
- **Why it matters:** Cheap UI win on top of metadata that's already being fetched.

### 3.17 Music Lyrics / EQ / ReplayGain

- **Missing:** Music doc explicitly says "lyrics (coming soon)". No equalizer, no ReplayGain/normalization, no LRC parser. Grep for `lyric|equalizer|replaygain|normalize` returns no source matches outside docs.
- **Why it matters:** Plexamp is the gold standard here; Jellyfin's music is famously weak. SoftMedia could land a strong middle ground with synced LRC + ReplayGain.

### 3.18 Bulk Metadata Edit / Manual Match-Override

- **Missing:** No bulk edit grid (set genre on N items at once), no "replace this match with that match" search-and-pick UI for metadata corrections.
- **Verification:** No relevant controller/page found in `Controllers/` or `pages/`. The metadata refresh endpoint at `Controllers/SettingsController.cs:42` only triggers a global refresh.
- **Why it matters:** Every long-lived library accumulates wrong matches. Without manual override, the only fix is to delete the row and re-scan with a renamed file.

---

## 4. Missing / Thin Settings (compared to Plex / Jellyfin admin panels)

The settings tree in SDD §7 is solid for v1 but several common admin knobs are absent. None are blockers; they're "users will notice these are missing within the first hour."

| Setting | Where it would live | Notes |
|---|---|---|
| **Bandwidth caps per-user / per-network** ("LAN unlimited, WAN 8 Mbps") | Playback › Transcoding | The transcode planner already accepts `maxBitrate` (`TranscodeController.cs:103`); just needs a per-user/per-network policy in front of it. |
| **Maximum concurrent transcodes** | Playback › Transcoding | Session manager exists (`TranscodeSessionManager.cs`); no documented hard cap. |
| **Per-codec/container blacklist** ("never direct-play DTS") | Playback › Transcoding | Stream planner is binary today. |
| **Subtitle styling defaults** (font/size/colour/position) | Playback › Subtitles | Player burns or passes through but no admin/per-user style. |
| **Audio normalization / volume gain** (per-user, mobile vs LAN) | Playback › Audio | Doc §4.5 only discusses transcode AAC. |
| **Daily rescan time, watcher debounce, ignore patterns** | Media Management › Scanning | First two are in SDD §7 defaults; verify they're actually wired (see §5.3 below). |
| **Library refresh cadence per provider** | Metadata › Data Sources | Currently a global "Auto-Refresh Metadata" boolean. |
| **Image quality / cache ceiling** | Metadata › Images | Image cache exists; size cap is in code, not in UI. |
| **HTTPS cert/key path or ACME email** | Server › Network | SDD §6.1 mentions Caddy but server has no in-app cert management. |
| **Trusted proxies / `X-Forwarded-For`** | Server › Network | Required when behind Caddy/nginx — missing settings means real client IP is wrong (affects rate limiting). |
| **Backup schedule + destination** | Server › Maintenance | Whole category does not exist. |
| **Log file rotation / max size / sink** | Server › General | Only Log Level today (SDD §7.2). |
| **Scheduled tasks page** (cron-like, with last/next run + manual trigger) | Server › Tasks | Background services exist (HeroCacheWorker, RefreshTokenCleanupService, MetadataRefreshService) but no admin visibility. |
| **Default audio language / default subtitle language / "forced subs only"** | Playback › Subtitles | Per-user `PreferredAudioLanguage` exists but defaults / forced-subs policy is thin. |
| **Hardware acceleration: device index / DXVA / D3D11VA on Windows** | Playback › Transcoding | `HardwareAcceleration` is a single string (`none|nvidia|intel|amd`) at `TranscodeProfileBuilder.cs:7-18`; no device picker, no DXVA option for AMD on Windows. |
| **Per-library "scanner" choice** (e.g. choose between embedded-tags-first vs API-first for music) | Media Management › Libraries | Today the routing is type-locked, not user-configurable. |

---

## 5. Where SoftMedia Can Realistically Beat Plex and Jellyfin

These are "punch above your weight" plays — features where a small focused codebase has the advantage.

### 5.1 Trust posture

- **No telemetry, no account requirement, no remote disable.** Plex famously requires plex.tv login (even for LAN-only) and has remotely revoked features in the past. Jellyfin doesn't, but its third-party app ecosystem is fragmented. Make this a *visible* selling point in onboarding.
- **Auditable provider compliance.** SoftMedia already enforces TVMaze/Wikidata/MusicBrainz/Open Library rate-limits and User-Agent headers (`Services/Infrastructure/SoftMediaUserAgentHandler.cs`, `Services/Infrastructure/RateLimitingDelegatingHandler.cs`). That's *better than Jellyfin's* default (which trusts the user not to hammer providers).

### 5.2 HDR-aware transcoding

- The HDR + subtitle-burn override path in `TranscodeProfileBuilder.cs:127-145` is a meaningful improvement on what most home users actually run into: when subtitles must be burned into HDR content, SoftMedia *forces* tone-mapping so the burned text is legible, even if the user enabled `PreserveHDR`. Jellyfin's HDR-tone-mapping is configurable but does not couple subtitle-burn state to HDR passthrough decisions; Plex's HDR transcoding is gated behind Plex Pass for non-Direct-Play paths. Document this trade-off in the player UI — most users won't know to look for it.
- The fMP4-when-AV1-or-HDR branch (`TranscodeProfileBuilder.cs:338-352`) avoids a class of subtle MPEG-TS issues. Worth surfacing in the docs as "we got the segment container right."

### 5.3 Smart-Transcoding Decision Panel

- The `TranscodeDebugService` and `/debug` endpoint (`TranscodeController.cs:223-238`) already returns *why* a stream chose Direct/Remux/Transcode. Plex hides this; Jellyfin shows raw FFmpeg. SoftMedia could land a user-facing "Why is this transcoding?" panel that's plain-English. Half of Plex's r/PleX support volume is exactly this question.

### 5.4 Read-along ebook + comic experience

- The reader has bookmarks, highlights, reading sessions, TTS, in-book search, dictionary lookup (`components/reader/{BookReader,SearchDrawer,HighlightsDrawer,TocDrawer,TtsNowPlayingBar}.tsx`). Jellyfin's book support is essentially "serve the file" and Plex doesn't really do books at all. **This is the single biggest differentiator on the table today.** It deserves a marketing-page screenshot.

### 5.5 Intro/Credits detection that's chapter-aware

- Detection prefers embedded chapters over fingerprint, and *will not overwrite* a chapter-source value (`Models/MediaItem.cs:80-105`). Jellyfin's intro-skip plugin overrides chapters in some configurations; this is a small correctness win worth keeping.

### 5.6 First-party reverse-proxy onboarding (proposed)

- A "Connect to the internet" wizard that bundles a Caddyfile generator for DuckDNS + Let's Encrypt would close the most painful Plex-vs-Jellyfin gap (Plex hides this behind "Plex Relay"; Jellyfin punts to docs). SDD §6.1 already documents the recipe — turning it into a wizard is novel.

### 5.7 Per-user library ACL with fail-safe parental defaults

- Both per-library allow-list (`Models/UserLibraryAccess.cs`) and per-content-type rating (`Models/User.cs:25-28`) are already present and enforced fail-safe (null content rating treated as restricted, 404 on direct stream). Plex's parental controls are notoriously easy to bypass via direct URL. Make this a documented selling point.

---

## 6. Recommended Priority Order

Ordered by (impact × ease) on a homelab user. Top of list = land first.

1. **API tokens (§3.9)** — unblocks third-party integrations and §3.8/§3.15.
2. **Backup/restore admin endpoint (§3.7)** — small code, huge ops trust.
3. **Generic outbound webhooks (§3.8)** — covers Discord/Slack/ntfy/Trakt without any first-party SDKs.
4. **PWA shell + manifest (§3.2)** — closes "do you have an app?" without a real native build.
5. **Pre-generated trickplay sprites (§3.11)** — meaningful UX upgrade with bounded scope.
6. **Bandwidth-cap and concurrent-transcode admin settings (§4)** — protects the home upload pipe.
7. **Subtitle auto-download (§3.6)** — high user-visible value once §3.9 exists.
8. **Chromecast sender (§3.1)** — biggest perceived gap vs Plex; SPA-only change.
9. **TOTP 2FA (§3.10)** — straightforward; prereq for any "ship a public URL" guidance.
10. **Smart-transcoding "Why is this transcoding?" user-facing panel (§5.3)** — pure marketing/UX win on infrastructure that already exists.

Deferred (defensible to skip for v1): native mobile apps, Live TV/DVR, AirPlay receiver, Last.fm/Trakt SDK integrations beyond webhooks.

---

## Appendix A — Files Inspected

- `docs/SDD.md` (full)
- `docs/directory_structure.md` (full)
- `docs/user-docs/features/{music-player,skip-intro-credits}.md`
- `src/SoftMedia.Server/Models/{MediaItem,User,AppSetting,UserLibraryAccess}.cs`
- `src/SoftMedia.Server/Controllers/{TranscodeController,SettingsController,AuthController,MediaController}.cs`
- `src/SoftMedia.Server/Services/Transcoding/TranscodeProfileBuilder.cs`
- `src/SoftMedia.Server/Services/Media/SubtitleService.cs`
- `src/SoftMedia.Server/Services/Infrastructure/NotificationService.cs`
- `src/SoftMedia.Server/Services/Metadata/ExifMetadataProvider.cs`
- `src/SoftMedia.Client/src/pages/{PlayerPage,SettingsPage}.tsx`
- `src/SoftMedia.Client/src/components/player/VideoPlayer.tsx`

Plus repo-wide greps documented inline at each finding.
