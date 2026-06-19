# SoftMedia Feature-Gap Report vs. Plex / Jellyfin / Emby

*Code-verified. Date: 2026-06-16. Supersedes the stale May-2026 analysis.*

## 1. Executive Summary

SoftMedia in June 2026 is a genuinely capable, security-hardened, privacy-first media server with a mature core: HLS transcoding with NVIDIA/QSV/AMF hardware paths, direct-play/remux decisioning, trickplay scrubbing, a full e-reader stack (EPUB/PDF/CBZ with TTS, highlights, bookmarks), a robust identity layer (Argon2id, rotating refresh tokens, TOTP 2FA with trusted devices, API tokens, invites, RBAC, per-library ACL, parental content ratings), a real DLNA Media Server (DMS), HMAC-signed outbound webhooks, scheduled backups with restore, and a PWA shell. Since the May analysis, a large slate of previously-"missing" features has actually shipped. The remaining gaps cluster in three areas: **(a) the "couch/family/Apple ecosystem" surface** (Continue Watching row, SyncPlay, AirPlay, TV layout, offline download), **(b) metadata/artwork richness** (no TMDB/Fanart provider — the single biggest visible quality gap), and **(c) deployment + operations** (no Docker image, no log viewer, no now-playing/active-sessions admin monitor). Two items the source review flagged as gaps are in fact already implemented and are explicitly excluded below: **Swagger/OpenAPI** (present, dev-gated, `Program.cs:153,223-224`) and **per-user simultaneous-stream enforcement** (present, `TranscodeService.cs:247`).

## 2. Closed Since the May Analysis

The following were flagged "missing" in the stale May report but are now verified present in code:

- **DLNA / UPnP Media Server** — full DMS-1.50 (ContentDirectory Browse/BrowseMetadata, ConnectionManager, SSDP announce, LAN-only stream endpoint). `Services/Dlna/*`, `DlnaController`.
- **Outbound webhooks** — HMAC-SHA256 signed delivery, retry/backoff, dead-letter, SSRF guards, CRUD UI. `WebhooksController`, `WebhookSubscription`, `WebhookDispatcher`.
- **Trickplay / BIF scrubber sprites** — `TrickplayController`, `TrickplayService`.
- **In-app system notifications** — DB-backed `SystemNotification`, `NotificationsController`.
- **User-scoped API tokens with scopes** + reduced-privilege media token + Cast token. `ApiToken`.
- **TOTP 2FA** — enrollment, login challenge, disable, recovery codes (80-bit entropy), trusted-device remember window, admin force-disable recovery. `UserTotp`, `TrustedDevice`, `TotpService`.
- **Invite-based signup + approval workflow.** `InvitesController`.
- **PWA shell** — manifest, Workbox service worker, offline fallback page. `OfflinePage.tsx`, `vite.config.ts`.
- **Swagger / OpenAPI** — `AddSwaggerGen` + Bearer security def + `UseSwagger`/`UseSwaggerUI` (dev-gated). `Program.cs:153,223-224`; smoke-tested at `FactorySmokeTests.cs:16`.
- **Per-user concurrent-transcode cap** — enforced at session start with a hard ceiling. `TranscodeService.cs:224,247`.

## 3. Critical Gaps (adoption blockers — Plex *and* Jellyfin both have these)

### 3.1 No "Continue Watching" row (and no in-progress API) — *confirmed missing*
The single most-used home-screen feature in all three incumbents. Progress data is fully captured (`VideoPlayer.tsx:323` posts every 10s; `MediaItem.progress`/`playbackPosition` exist; `LibraryService.cs:410-412` maps it), but there is no list endpoint and no UI row. `HomePage.tsx` renders only `WatchlistRow` + per-library `LibraryRecentRow` (verified lines 108-121); `InteractionController` has only single-item `GET /{id}/progress` (`:111`); `IUserMediaInteractionService` has no `GetInProgressAsync`. **Why it matters:** users cannot resume across the library from the landing page — a daily friction point. **Scope:** Small — add `GET /api/v1/interaction/in-progress` (position>0, IsWatched=false, sort by LastPlayed desc, ACL-filtered) + a `ContinueWatchingRow`. ~2-3 days.

### 3.2 No TMDB / TheTVDB / Fanart.tv metadata + artwork provider — *partial*
**What exists:** OMDb (one poster), Wikidata, TVMaze (series + per-season posters + episode stills). **What's missing:** no TMDB/TheTVDB/Fanart provider anywhere (verified: zero matches in `Services/Metadata`; the lone "tmdb" hit is a Kodi-ratings code comment at `NfoXmlParser.cs:134`). Consequence: movie backdrops are essentially always null (`MetadataResult` has no `BackdropUrl` field so providers cannot return one), no alternate poster choices, thin crew/cast/keyword depth, no `Writer` field on `MediaItem` (`MediaItem.cs:61` has Director only), and `MediaItemDto.FromMediaItem` never serializes the `Extra` blob so the client's `metadata.writer` fallback always resolves undefined. **Why it matters:** TMDB-grade metadata is the most visible quality difference new users notice; sparse library walls and missing backdrops read as "unfinished." **Scope:** Large — one TMDB provider covers metadata + images; add `BackdropUrl` to `MetadataResult`, structured crew, API-key admin UI. ~4-6 days.

### 3.3 No Docker image / container deployment path — *confirmed missing*
No `Dockerfile`, `docker-compose.yml`, or `.dockerignore` exist (verified: glob returns nothing); no Unraid/TrueNAS/Synology/Helm references. The only install path is `dotnet run` + `npm run dev` (`README.md:21-27`) plus a Windows-only `setup.ps1`. **Why it matters:** self-hosted media servers are overwhelmingly deployed as containers (Jellyfin's image has 500M+ pulls). Without one, SoftMedia is effectively install-only for .NET-SDK-literate users — a hard adoption barrier for the NAS/VPS audience. **Scope:** Medium — multi-stage Dockerfile, compose with media/data volume mounts, deploy docs, CI publish.

### 3.4 HDR tone-mapping is NVIDIA-only; no software/AMD/Intel fallback — *confirmed missing*
`useToneMappingPipeline` is gated on `HardwareAcceleration == "nvidia"` (`TranscodeProfileBuilder.cs:133`); all tonemap logic lives inside that branch (lines 176-208). For AMD/QSV/CPU + HDR source + SDR client, raw HDR pixels are encoded into an SDR container → washed-out/clipped picture. No `zscale`/`tonemap`/`libplacebo` software chain exists. **Why it matters:** Jellyfin ships a CPU `zscale+tonemap` fallback; non-NVIDIA self-hosters currently get broken HDR-on-SDR output. **Scope:** Medium-high — add software tonemap chain when HW≠nvidia and source IsHdr. ~2-3 days.

### 3.5 No VAAPI hardware acceleration (Linux GPUs) — *confirmed missing*
`GetHardwareDecodeOptions` supports only nvidia(cuda)/intel(qsv)/amd(d3d11va) (`TranscodeProfileBuilder.cs:395-406`); `d3d11va` is Windows-only and the QSV path is not a reliable Linux substitute. Zero `vaapi` matches in the server. The settings UI lists only none/nvidia/amd/intel. **Why it matters:** VAAPI is the primary HW path for the dominant Linux/Docker deployment scenario; its absence forces CPU-only transcoding on most self-hosting hardware. **Scope:** Medium — add `vaapi` decode/encode (h264/hevc/av1_vaapi) + scale filter + settings option. ~1.5-2 days.

### 3.6 No external subtitle download or sidecar (.srt/.ass) support — *confirmed missing*
Two related, both critical for non-English libraries. (a) **No OpenSubtitles/SubDL/Addic7ed integration** (zero matches; `P3-WI-002` dropped). (b) **No sidecar scanning** — `.srt/.ass/.vtt/.sub` appear in no scanner extension list (`MediaExtensions.cs`), `SubtitleTrack` has no `ExternalPath`/`IsExternal`, and `SubtitleService` only extracts embedded streams. Only muxed subtitles work. **Why it matters:** downloading fan-translation SRTs alongside media is table-stakes; OpenSubtitles is the single most-requested subtitle feature in self-hosted communities. **Scope:** Medium each — sidecar discovery in scanners (~2 days) + an `ISubtitleDownloadProvider` with OpenSubtitles v3 + post-scan job (~3-5 days).

### 3.7 Offline media download / sync is non-functional — *confirmed missing*
`OfflinePage.tsx` is a pure "you're offline" splash; Workbox config explicitly excludes media/API from cache (`vite.config.ts:31-44`); no IndexedDB/dexie/localforage, no download button, no sync queue. **Why it matters:** offline download is a top Jellyfin request and a core Plex Pass draw. **Scope:** Large — download endpoint, per-item manager UI, Cache/IndexedDB storage, downloaded-content view. ~3-5 weeks.

### 3.8 Photo library is hard-blocked — *partial (scaffold only)*
`LibraryType.Photo`, `ExifMetadataProvider`, and `PhotoDetailView.tsx` exist, but no `PhotoScanner` exists (glob returns nothing) and `LibraryService.CreateLibraryAsync`/`UpdateLibraryAsync` throw `ArgumentException` on Photo (`LibraryService.cs:66-68`). Photos are non-functional end-to-end; the UI hides the type. **Why it matters:** photo libraries are first-class in Plex/Jellyfin; the hard block means zero photos can be added. **Scope:** Medium — write `PhotoScanner` (mirror BookScanner), remove guard, re-enable UI. ~2-3 days. (Albums/timeline/map/face-detection are separate, larger nice-to-haves.)

### 3.9 No Live TV / DVR / IPTV — *confirmed missing*
No m3u/EPG/XMLTV/HDHomeRun/tuner/recording-scheduler anywhere (verified zero matches; only `.m3u8` HLS manifests and MusicBrainz "recording" terms). Explicitly deferred (`phase-4-deferred.md:27-33`). **Why it matters:** the single largest feature class Jellyfin has that SoftMedia entirely lacks; an adoption blocker for cord-cutters. **Scope:** Very large (4-8 weeks). Reasonable to remain deferred with a documented "run TVHeadend/Channels alongside" workaround.

## 4. Important Gaps (noticed within the first hours)

### Casting & remote
- **SyncPlay / Watch-Together** — *confirmed missing.* `MediaHub` has only `JoinLibrary`/`JoinMedia` (verified `:56,:136`); no room/play/pause/seek/clock-sync at any layer. Scope: Large.
- **DLNA admin settings UI** — *confirmed missing.* Settings seeded server-side (`SettingsService.cs:115-120`) but `renderSettingsGroup('DLNA')` is never called and the Sidebar nav has no DLNA entry (verified: zero `dlna` matches in client `src`). Admins can only configure via raw API/DB. Scope: Small. *(High-leverage: small fix, removes a real support burden.)*
- **DLNA renderer (DMR) push-to-device** — *partial.* DMS exists; no AVTransport/RenderingControl, no renderer discovery, no "Play on device" UI. Scope: Large.
- **Cast subtitles** — *confirmed missing.* `useCast.ts` sets no `textTracks`/`activeTrackIds` (verified zero matches); subtitles work locally but vanish on Chromecast. Scope: Small-medium.
- **Reverse-proxy / remote-access UI** — *partial.* LAN/WAN bitrate caps exist server-side; no external-URL/trusted-proxy/port-forward/"test remote access" UI. Scope: Medium.

### Identity & access
- **OIDC / OAuth / LDAP SSO** — *confirmed missing.* Only `JwtBearer` + custom ApiToken scheme; no external-auth packages or endpoints (`AuthController` has no callback). Scope: Large. Workaround: reverse-proxy (Authelia/Authentik).
- **Self-service "forgot password" via email** — *confirmed missing.* No SMTP/email service, no reset-token model, no `email` field on `User`; only admin reset + authenticated change. Scope: Medium (blocked on §4 SMTP).
- **Guest/managed (kids) profiles** — *confirmed missing.* `User.ParentId` is a DB stub referenced by no controller/service; rating ceilings ignore parent linkage. Scope: Medium.
- **Login activity log** — *confirmed missing.* No `LoginEvent`/audit model, no failed-attempt persistence, no history endpoint/UI. Scope: Small-medium.
- **Self-initiated "sign out everywhere"** — *partial.* `RevokeAllForUserAsync` exists and runs on password change/ban, but there is no user-invokable `POST /auth/logout-all` (the `DELETE /account/trusted-devices` route clears 2FA devices only, not refresh tokens). Scope: Trivial (~half a day). *(High-leverage.)*
- **Per-user stream-count limit + per-user bitrate admin UI** — *partial.* Global per-user transcode cap is enforced (`TranscodeService.cs:247`); `User.MaxStreamBitrateKbps` exists and is read but has **no admin write endpoint or UI** (`UsersController` has no bitrate route; `UserDto` omits it); no per-user concurrent-stream override and no direct-play counting (`StreamController` serves `PhysicalFile` with no tracking). Scope: Small-medium.

### Operations & admin
- **Now-playing / active-sessions monitor with stop control** — *confirmed missing.* `GetAllSessions()` is internal-only; `AdminController` has no session endpoint; no admin kill-stream or send-message. Scope: Medium. *(High-leverage for shared servers.)*
- **Server logs viewer** — *confirmed missing.* No log API, no file sink (console-only default provider), no UI; the `LogLevel` DB setting was deleted by a migration leaving dead UI. Scope: Medium (add Serilog rolling-file + `GET /admin/logs`).
- **System/health dashboard** — *confirmed missing.* `/health` returns only `{status, utc}` (verified `HealthController.cs:17`); no CPU/RAM/disk/uptime/version/per-library counts. Scope: Medium.
- **Email/SMTP delivery** — *confirmed missing.* No SMTP client/package; invite codes are copy-paste only. Scope: Medium. Underpins forgot-password + admin alerts.
- **Watch-history & admin statistics** — *partial.* Per-item progress + per-book reading summary exist (user-scoped); no per-user history list endpoint, no admin aggregate stats (most-played, hours-per-user, top-10). Scope: Medium.
- **Webhook event taxonomy** — *partial.* Solid dispatcher but only `library.scan.completed`/`failed` (+ synthetic `test`); `media.added`/`media.played`/`transcode.failed` deferred (`WebhookSubscription.cs:41-43`). Each new event is largely a one-liner at the call site. Scope: Small-medium.
- **First-party push fan-out (Discord/Telegram/ntfy/Pushover/Gotify/Apprise)** — *confirmed missing* (intentional per privacy charter; generic webhook is the relay). Scope: Large, or one Apprise URL setting.
- **Update/version checker** — *confirmed missing.* Assembly version is in backup metadata only; no GitHub poll, no UI version. Scope: Small.

### Metadata & content
- **People/cast browse pages** — *partial.* Person/MediaItemCast stored and rendered in cast strips, but no `/person/:id` route, no `PersonController`, clicking an actor is non-navigable. Scope: Medium.
- **Multi-image / alternate artwork picker + upload** — *confirmed missing.* Single `PosterUrl`/`BackdropUrl`; FixMatch allows pasting one URL; the old `MediaImages` table was dropped. No gallery/upload. Scope: Medium.
- **Bulk metadata edit** — *confirmed missing.* Only single-item PATCH; no multi-select endpoint/UI. Scope: Medium.
- **NFO write-back** — *confirmed missing* (read-only by design; edits save to DB only). Scope: Medium.
- **Subtitle appearance controls (font/size/color/bg/position)** — *partial.* Language + burn-in only; native WebVTT with no `::cue` overrides. Scope: Small-medium. *(High-leverage accessibility win.)*
- **TV "coming soon" / release calendar** — *partial.* Future `ReleaseDate` is stored (`TvMetadataEnricher.cs:82`) but no calendar endpoint/view/"airs on" line. Scope: Small (data already present).

### Content types & library
- **Music: synced lyrics (LRC), ReplayGain normalization, audio normalization (loudnorm)** — all *confirmed missing.* Audio transcode is hard-coded `-c:a aac -ac 2 -b:a 128k` (`TranscodeProfileBuilder.cs:333`) with no `-af loudnorm`; `MusicScanner` never reads `tag.Lyrics`/`ReplayGain*`. Scope: Small-medium each.
- **Surround/AC3 passthrough on transcode** — *partial.* `StreamPlanService` computes AC3 5.1 / `DefaultAudioChannels`, but these reach only the client DTO — `TranscodeProfileBuilder` has no audio params, so the hard-coded stereo AAC always wins. Dead-end metadata. Scope: Medium.
- **Multi-version / editions of one title** — *confirmed missing* (deferred `P4-005`; duplicate cards). Scope: Medium.
- **Mixed-content / Home Videos / Music Videos / Audiobooks / Podcasts as distinct types** — all *confirmed missing.* Notably **Home Videos** (`P-medium`): files must go in a Movie library where metadata lookups permanently fail and pollute the retry queue. Scope: Small-medium (Home Videos) to Large (Podcasts).
- **Smart/rule-based playlists + User Tags** — *confirmed missing.* Only static audio-only playlists + favorites. Scope: Large (smart) / Medium (tags).

### Client & UX
- **PWA in-app install prompt (A2HS)** — *confirmed missing.* No `beforeinstallprompt` handler; install relies on browser ambient UI. Scope: Small (~1 day).
- **First-run setup wizard** — *confirmed missing.* Forced password change only; admins land on empty home with no guided library/provider/remote-access setup. Scope: Medium.
- **Global search depth** — *confirmed missing.* 5-result dropdown, no `/search` page, no type/year/genre facets, no voice input. Scope: Medium.
- **10-foot / TV layout + D-pad spatial focus** — *confirmed missing.* No `/tv` route, no focus manager, no overscan/large-type. Scope: Large.

## 5. Nice-to-have / Differentiators

- **WebAuthn / Passkeys** — *confirmed missing* (TOTP only). Forward-looking, phishing-resistant 2FA; Plex already has passkeys. Scope: Medium.
- **Profile PINs** — *confirmed missing* (no `PinHash`). Pairs with managed profiles. Scope: Small-medium.
- **AirPlay in custom player** — *confirmed missing.* No `x-webkit-airplay`, no `webkitShowPlaybackTargetPicker()`; Safari/iOS users have no "send to TV." Scope: Small (client-only) — an easy Apple-ecosystem win.
- **Phone-as-remote** — *confirmed missing.* No control hub channel/route. Scope: Large.
- **Recommendations / "Because You Watched" / Similar Items** — *confirmed missing* (`IRecommendationService` is next-episode + hero-cache only). High discovery value. Scope: Medium for basic genre/cast overlap.
- **Scrobbling (Trakt/Last.fm/AniList/MAL)** — *confirmed missing* (blocked on `media.played` webhook). Top social ask. Scope: 2 days for the event + 1-2 weeks per integration.
- **Recommend-to-user / social sharing**, **mood/decade browse** (`LibraryItemFilter` has exact-year only), **sleep timer in video/audio players** (reader-TTS only), **playback-speed persistence**, **freetext reviews**, **client-side EQ**, **instant-mix/radio**, **play-count tracking** (`MediaItem.PlayCount` is dead schema, never incremented) — all *confirmed missing/partial*, all low-to-medium scope, each a small delight or a way to beat the incumbents on polish.
- **Custom theme / accent color** — *partial.* Fixed dark-only chrome; reader has its own 3 themes. No app-level switcher/accent picker. Scope: Small-medium. A privacy-first server differentiating on **per-user theming + custom CSS** (a Jellyfin strength) is cheap upside.
- **App-level keyboard shortcuts** (`/` search, `?` help, G+H nav) — *confirmed missing* (player/reader only). Scope: Small.
- **Plugin/extension system** — *partial.* A real `IMetadataProvider` contract exists (compile-time DI), but no runtime plugin loader. Long-term ecosystem play. Scope: Very large.
- **Data portability (import Plex/Jellyfin/Trakt watch state)** — *confirmed missing.* A top migration friction point; even an IsWatched+rating MVP would lower switching cost. Scope: Large.
- **LAN/WAN + audio bitrate settings polish** — *partial.* `MaxStreamingBitrateLan` and `MaxAudioStreamingBitrate` are enforced server-side and *do* render via the generic text fallback, but lack dedicated labeled controls (missing from `streamingOrder`). Trivial cleanup; corrects the "never rendered" overstatement.
- **a11y CI guard** — *partial.* A real regex-based `a11yGuards.test.ts` exists (aria-labels, div-onClick, focus-visible) but is NOT wired into the only CI workflow (`security.yml` runs only dotnet/npm vuln scans), so "CI-enforced a11y" is aspirational. Add axe-core to render paths and gate CI. Scope: Medium.

## 6. Recommended Priority Order (impact × ease)

1. **Continue Watching row + in-progress API** — highest daily-use impact, data already exists, ~2-3 days. The biggest "feels incomplete" perception fix.
2. **DLNA admin settings UI** — backend complete, removes a real support burden, Small. Pure win.
3. **Self-service "sign out everywhere"** — service method exists; ~half a day; closes a security-hygiene gap users expect.
4. **Docker image + compose + deploy docs** — unlocks the entire NAS/VPS adoption channel; Medium effort, outsized reach.
5. **Cast subtitles (textTracks/activeTrackIds)** — Small fix to a daily-noticed regression for international users.
6. **TMDB/Fanart metadata + artwork provider** — biggest visible quality lift; Large but highest single-provider leverage (fixes backdrops, posters, crew depth, and unblocks artwork picker).
7. **Sidecar subtitle scanning (.srt/.ass)** — Medium; table-stakes for non-English libraries, cheaper than full OpenSubtitles.
8. **HDR software/AMD/Intel tonemap fallback + VAAPI** — Medium each; eliminates broken HDR output and CPU-only Linux transcoding for the majority hardware base.
9. **Now-playing/active-sessions admin monitor + server logs viewer** — Medium; the two operations features self-hosters reach for first when something breaks.
10. **First-run setup wizard + PWA install prompt + AirPlay button** — cluster of Small client-side wins that sharply improve first-impression and Apple-ecosystem coverage.

*Deferred-by-design and reasonable to keep so for now: Live TV/DVR, native mobile/TV apps, plugin runtime, multi-server federation, SyncPlay (re-evaluate — it is a primary reason users pick Jellyfin).*

## 7. Appendix — Verification Method

Every gap above was code-verified against the working tree at `security/hardening-wave-2`. The adversarial evidence in the supplied dataset was independently spot-checked by reading source and running targeted searches, including: `Program.cs:145-234` and `HealthController.cs` (Swagger present, health bare); `TranscodeProfileBuilder.cs:128-208,390-406` (NVIDIA-only tonemap, no VAAPI); `TranscodeService.cs:218-253` (per-user transcode cap **is** enforced); `LibraryService.cs:58-73` + Photo-scanner glob (hard block, no scanner); `HomePage.tsx` (no Continue Watching row); `MediaHub.cs` (no SyncPlay); `useCast.ts` (no cast subtitle tracks); metadata-provider grep (no TMDB/TheTVDB/Fanart — lone hit is a code comment); and globs confirming **no Dockerfile/compose** and **no Live TV/OpenSubtitles** code. **Two source-review claims were found FALSE and excluded from the gap list:** Swagger/OpenAPI is implemented (`Program.cs:153,223-224`, dev-gated) and per-user simultaneous-stream enforcement is implemented (`TranscodeService.cs:247`). The May-2026 report was treated strictly as a re-verification checklist, never as current truth; all "Closed Since" items were re-confirmed present in code.

---

*Method: 114-agent code-verified audit — 8 feature domains each inventoried → gap-identified vs Plex/Jellyfin/Emby → every candidate adversarially verified against source (refute-first) → completeness critic → synthesis. 89 candidate gaps evaluated; this report lists only those with verified status `confirmed-missing` or `partial`.*
