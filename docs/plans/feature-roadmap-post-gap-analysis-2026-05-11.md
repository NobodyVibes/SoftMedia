# SoftMedia — What to Build Next (Post Gap-Analysis Plan)

**Date:** 2026-05-11
**Status:** Proposal — awaiting maintainer sign-off
**Companion to:** `docs/reports/feature-gap-analysis-2026-05-07.md`

This plan converts the gap analysis into a build order. It is filtered for the *actual* shape of SoftMedia: a free, self-hosted, single-binary media server maintained by a small team (likely one or two people at a time), with no corporate SDK budgets, no telemetry, and no cloud-side infra to maintain.

## Filter Criteria

Every gap from the report was scored against these:

1. **OSS-feasible** — no proprietary SDKs, no paid APIs, no ongoing third-party fees. (Drops things like first-party AirPlay receiver and SAML SSO.)
2. **Bounded scope** — finishable by one engineer in a focused sprint, or splittable into chunks that each ship value. (Defers Live TV/DVR and native mobile apps.)
3. **Privacy-charter aligned** — no telemetry, no opt-in cloud relay, no first-party hosted endpoints. (Webhooks stay because the *user* configures the endpoint; "metric upload" never enters scope.)
4. **Closes a "but does it…?" objection** that hobbyists actually ask before adopting (backup, casting, mobile, 2FA, scrub previews). De-prioritises things only power users ask for.
5. **Correctness debt first.** A latent bug that breaks an in-tree feature (e.g. the rate limiter behind a reverse proxy) ships before any new capability.

---

## Phase 0 — Correctness Debt (1–2 days)

Not features. Things that are *wrong today* and should be fixed before adding more surface area.

### 0.1 Trusted-proxy / `X-Forwarded-For` configuration
- **Why first.** `Program.cs` has no `app.UseForwardedHeaders(...)`. When SoftMedia runs behind Caddy/nginx/Tailscale Funnel (the recipes the SDD itself recommends in §6.1), the rate-limit middleware sees the proxy's loopback IP, so per-IP login rate-limiting effectively becomes "one bucket for the whole world." That is a security regression of an *existing* security feature.
- **Scope.** Wire `ForwardedHeadersOptions` (XForwardedFor + XForwardedProto), expose `KnownProxies` / `KnownNetworks` as admin settings under `Server > Network`, document the default ("loopback only — set this if you're behind a reverse proxy").
- **Acceptance.** Login from behind a proxy reports the real client IP in `HttpContext.Connection.RemoteIpAddress` and the rate-limit partition; integration test with `TestServer` and a fake forwarded chain.

### 0.2 Doc drift cleanup
- **Why.** `SDD §6.2` says the refresh cookie is `SameSite=Strict`. The actual code at `AuthController.cs:322-329` is `Lax`, with an inline rationale comment about Vite's dev proxy. The SDD should be the source of truth, not the misleading version.
- **Scope.** Edit SDD §4.2 and §6.2 to match the implementation; copy the rationale comment into the SDD so the *why* survives even if someone "fixes" the doc later. Remove unused `@vidstack/react` and `vidstack` from `package.json` (the live player uses native HTML5 + `hls.js`, not vidstack).
- **Acceptance.** `grep -ni "samesite.*strict" docs/SDD.md` returns nothing in the refresh-cookie context. `npm ls vidstack` reports "empty".

---

## Phase 1 — Operational Trust (2–3 weeks)

Things hobbyists need *before* they fully commit a library to SoftMedia. Each item here builds operator confidence.

### 1.1 Backup / Restore admin endpoint
- **Why now.** SQLite + WAL is trivially backup-able, but users need a button. Without it, "I lost my watch state when my drive died" becomes a recurring support thread.
- **Scope.** `POST /api/v1/admin/backup` → returns a zip of `softmedia.db` (via the SQLite `BACKUP` command, *not* a raw file copy — WAL safety), `appsettings.json`, and the `data/` config tree. `POST /api/v1/admin/restore` reverses it with a server-restart prompt. Add a `BackupSchedule` setting under a new `Server > Maintenance` category with on-disk rotation (default: 7 daily, 4 weekly).
- **Notes.** A todo stub already exists at `docs/todos/feature-shortlist/02-admin-backup-endpoint.md` — adopt that scope.
- **Acceptance.** Integration test: write rows → backup → wipe DB → restore → rows return; xUnit assertion on file-level checksum equality of the backup payload.

### 1.2 Per-user API tokens (long-lived, scoped)
- **Why now.** Unblocks every "give my dashboard / Home Assistant / Sonarr companion / mobile app a way in" request. Today the only ingress is the rotating refresh cookie, which can't be used by third-party tools. This is the single most-requested feature in the Plex/Jellyfin parity gap.
- **Scope.** New `ApiToken` table (`UserId`, `HashedToken`, `Label`, `Scopes` JSON, `CreatedAt`, `LastUsedAt`, `RevokedAt`). Settings UI in `My Account` lets the user mint a token, see last-used, and revoke. `JwtBearerEvents.OnMessageReceived` extended to also accept `Authorization: Bearer sm_<token>` and resolve to a User claims principal. Scopes start coarse: `read:library`, `read:state`, `write:state`, `admin` (admins only).
- **Acceptance.** Token created → reused on `/api/v1/media/{id}` → `LastUsedAt` updated; revoked → 401; admin scope required for `/api/v1/admin/*`.

### 1.3 Streaming policy: bandwidth caps + concurrent-transcode caps
- **Why now.** A single 4K HDR transcode saturates a home upload pipe. Without a cap, one teenager's transcode kills everyone else's stream. The infrastructure already exists (`TranscodeProfileBuilder` accepts `maxBitrate`; `TranscodeSessionManager` tracks sessions). The missing piece is *policy*.
- **Scope.** Three new settings under `Playback > Streaming`:
  - `MaxConcurrentTranscodes` (int, default 2) — `TranscodeSessionService` rejects with HTTP 503 + retry-after when exceeded.
  - `MaxStreamBitrateLAN` and `MaxStreamBitrateWAN` (Kbps, default unlimited / 10000) — `StreamPlanService` clamps the requested bitrate. LAN vs WAN decided by whether the client IP falls inside the same private subnet as the server (RFC 1918 check; honour Phase 0.1 trusted-proxy resolution).
  - Per-user override on the User table (`MaxStreamBitrateKbps`, nullable) — admin-set, defaults to inherit.
- **Acceptance.** Third concurrent transcode → 503. WAN client asking for 20 Mbps → server clamps to 10000 + `Reason` in the stream plan reflects "WAN cap".

### 1.4 OMDb shared-key rollout

- **Why.** The `OMDbProvider` is already coded for the maintainer-paid model (see `OMDbProvider.cs:25-72` — `softmedia` / `custom` / `disabled` modes, tier-aware daily counter, low-quota notifications). What's missing is (a) replacing the placeholder string `SOFTMEDIA_OMDB_KEY_PLACEHOLDER` in release builds, (b) deciding the paid OMDb tier the project commits to, and (c) making the *user-override fallback* visible and trivial so the project's exit ramp is real.
- **Scope.**
  - Pick an OMDb tier consistent with expected user-base scale (the existing tier table at `OMDbProvider.cs:29-35` enumerates `free`/`basic`/`standard`/`pro`).
  - Add a release-build step that injects `OMDb:SoftMediaApiKey` into `appsettings.json` (or via env var) from a release secret — keep the placeholder in committed source so the OSS build still compiles without the secret.
  - In the Settings UI, when the shared key approaches or exceeds its daily limit, surface a **prominent** "Use your own OMDb key" prompt with a link to OMDb's signup page and a one-click switch to `OMDbApiKeyCustom` mode. The notification plumbing already exists (`SystemNotifications` table + `NotificationService`), so this is mostly a frontend affordance.
  - Document the policy: shared key is best-effort, user can self-host their own key any time, and a single page (e.g. `docs/user-docs/features/omdb-key.md`) lays out what happens if SoftMedia stops shipping a key.
- **Acceptance.** Released binary fetches OMDb without any user configuration. Setting `OMDbApiKeyMode=custom` and supplying a user key short-circuits the shared key entirely (assert in an existing OMDb provider test). When the shared key's `OMDbDailyCount` exceeds the configured tier limit, the settings page shows the override prompt within one polling interval.

### 1.5 Scheduled-tasks admin page (read-only first)
- **Why.** Several background workers exist already (`HeroCacheWorker`, `RefreshTokenCleanupService`, `MetadataRefreshService`, the file-watcher, the transcode-cleanup job). Today the admin has no visibility into when any of them last ran. This is *both* a debugging tool and a trust signal ("the server says it scanned 2 hours ago, so I believe it").
- **Scope.** Lightweight `IScheduledTask` registry; each background worker emits `LastRunUtc` / `LastResult` / `NextRunUtc` into an in-memory store on each tick. `GET /api/v1/admin/tasks` returns the list; settings UI shows it with a "run now" button for tasks that opt into manual triggering (start with `MetadataRefreshService` — endpoint already exists at `SettingsController.cs:42`).
- **Acceptance.** Admin page lists all 5 known workers with last-run timestamps; "run now" on metadata refresh triggers the existing service.

---

## Phase 2 — Quality of Life (4–6 weeks)

Phase 1 made the server trustworthy; Phase 2 makes it pleasant. These are the screenshots in the eventual README.

### 2.1 Pre-generated trickplay sprite sheets
- **Why.** The on-demand `/api/transcode/{id}/frame?time=` is correct but wasteful — every scrub spawns an FFmpeg process. A pre-baked `WxH` sprite grid per video (Plex calls this BIF, Jellyfin calls it trickplay) is what makes Plex's hover-scrub feel instant.
- **Scope.** New background job that, on scan complete for a video item, runs FFmpeg once with `fps=1/10` (10s interval) and `tile=10x10` to produce a single JPEG plus a JSON manifest of `{tileWidth, tileHeight, interval, count}`. Sprite + manifest stored under `data/trickplay/{itemId}.jpg|json`. `VideoPlayer.tsx` uses the sprite when present and falls back to the existing on-demand endpoint when not. Admin setting `Playback > Trickplay > Enabled` (default true), `Interval` (default 10s).
- **Acceptance.** New file scanned → sprite present within ~1 minute on test hardware → scrub preview loads instantly from a single cached JPEG. Existing per-frame endpoint still works as fallback.

### 2.2 "Why is this transcoding?" user-visible panel
- **Why.** This is *the* highest-leverage UX win in the report — the data is already produced by `TranscodeDebugService` (used by `PlayerDebugPanel` for admins), and Plex famously hides this while Jellyfin exposes only raw FFmpeg. A plain-English explanation panel solves 50% of homelab forum questions.
- **Scope.** Add a button to the player's gear menu: "Why is this transcoding?" Opens a modal that pulls from the existing debug endpoint and renders human-readable reasons (e.g. "Your browser cannot decode H.265 → server is converting to H.264," "DTS audio cannot play in Chrome → converting to AAC," "HDR being tone-mapped because subtitles are burned in"). Reuse `TranscodeDebugService` — *no new backend code*.
- **Acceptance.** A non-admin viewer can self-diagnose why a stream is transcoding without reading server logs.

### 2.3 PWA shell (Add-to-Home-Screen + offline shell)
- **Why.** "Do you have an app?" is the question. A PWA doesn't ship media offline (see §2.5 for that), but it does install to a phone's home screen, run full-screen without the browser chrome, and present an offline error page instead of a Chrome dino. That covers 80% of the perceived gap for free.
- **Scope.** Add `vite-plugin-pwa`, write `public/manifest.webmanifest` (icons in the existing brand gradient — `#007AFF → #8A2BE2`), implement a minimal service worker (cache-first for the app shell, network-first for `/api/*`). Explicitly *not* doing media caching yet.
- **Acceptance.** Lighthouse PWA score ≥ 90; "Install" prompt appears on Chrome/Android; offline visit shows a branded "you're offline" screen, not a browser error.

### 2.4 Generic outbound webhooks
- **Why.** Closes the "ping me on Discord / ntfy / Home Assistant when X happens" gap *without* shipping any first-party SDK. User configures the URL; SoftMedia POSTs JSON. Aligned with the privacy charter.
- **Scope.** New `WebhookSubscription` table (`UserId`, `Url`, `Events` JSON, `Secret`, `CreatedAt`). Event taxonomy starts small: `media.added`, `media.played` (=watched threshold reached), `transcode.failed`, `library.scan.completed`. Outbound POST signs with `X-SoftMedia-Signature: sha256=…` over the body using the per-subscription secret (Discord/ntfy ignore the header; HA validates it). Retries: 3 attempts, exponential, then dead-letter into the existing `SystemNotifications` table.
- **Acceptance.** Adding a new movie → ntfy phone notification within 5s. Webhook fails → after retries, a dismissable notification appears in the admin dashboard.

### 2.5 TOTP 2FA (optional, opt-in)
- **Why.** Once an admin follows the SDD §6.1 DuckDNS+Caddy recipe, the login is reachable from the open internet and password-only stops being enough. Solo-dev-scoped: `Otp.NET` is ~one file of integration. Passkeys (WebAuthn) are deferred to Phase 4 because the library landscape is messier.
- **Scope.** New `UserTotp` row (`UserId`, `EncryptedSecret`, `EnabledAt`, `RecoveryCodes`). Enrollment flow renders a QR (use a tiny QR library on the *client*, never the server, to avoid bundling QRCoder server-side just for this). Login flow adds a "2FA code" step when `EnabledAt != null`. Admin can require 2FA on the admin role globally (`Require2FAForAdmins` setting).
- **Acceptance.** Enroll with Google Authenticator → next login asks for code; recovery code path works once and then invalidates that code; admin bypass through DB only (no UI override — fail-safe).

---

## Phase 3 — Differentiation (6–10 weeks, parallelisable)

Phase 1+2 reaches credible parity with Jellyfin on the points that hobbyists actually feel. Phase 3 starts pulling ahead. Items here are roughly equal-priority; pick whichever your contributor base is excited about.

### 3.1 Chromecast sender (SPA-only)
- **Why.** Biggest single perceived gap vs Plex. Chromecast support is a free, browser-side feature — the `cast.framework` JS library is from Google but does not require a developer account for receivers that play standard formats.
- **Scope.** SPA-only change inside `VideoPlayer.tsx`: detect cast availability, render a Cast button, hand the current `StreamPlan` URL to the Cast SDK. Auth: SoftMedia stream URLs already accept `?access_token=`, which the Cast device honours; the operational risk is documented in SDD §6.2.

### 3.2 Subtitle auto-download (OpenSubtitles)
- **Why.** High user-visible value, free API tier, fits the existing rate-limited / User-Agent-disciplined provider pattern. Builds on the existing `PreferredAudioLanguage` user setting.
- **Scope.** New `OpenSubtitlesProvider : ISubtitleProvider` (the interface doesn't exist yet — introduce it). Triggered on first play of a video item that has no embedded subs in the user's preferred language, *not* on scan (avoids torching the rate limit). Cached sidecar `.vtt` stored next to the source file (or in a configured central path), respects the existing subtitle ingestion path. API key optional but recommended.

### 3.3 Smart-transcoding "Why" panel for the user (covered above — moved to Phase 2.2).

### 3.4 Bulk metadata edit + manual match override
- **Why.** Every long-lived library accumulates wrong matches. Right now the only fix is delete-row-and-rescan-with-renamed-file. Plex's "Fix Match" is a power-user staple.
- **Scope.** Two endpoints: `POST /api/v1/admin/match/{itemId}/search?query=…` (re-runs the type-locked provider with an explicit query, returns ranked candidates), `POST /api/v1/admin/match/{itemId}/apply` (writes the chosen candidate's metadata, locks it against future overrides via a `MetadataLocked: true` field on `MediaItem`). Frontend: a "Fix Match…" affordance on detail pages; admin-only.
- **Acceptance.** Wrongly-matched movie → admin searches "Blade Runner 2049 (2017)" → picks the correct Wikidata candidate → metadata updates, `MetadataLocked=true`, future scans won't overwrite.

### 3.5 Smart playlists / tags
- **Why.** Music-server staple ("everything 4K + HDR + unwatched", "all 80s synthwave"). Needs a tag model first, which itself unblocks user-curated collections beyond the existing franchise-link `CollectionId`.
- **Scope.** `Tag` table (`Id`, `Name`, `Color`), `MediaItemTag` join, `SmartPlaylist` table whose `RulesJson` encodes filter predicates (genre, tag, year-range, rating-range, watched-state, codec, resolution). Existing `Playlist` table stays for hand-curated playlists.

---

## Phase 4 — Defer to Community / Later (no commitments)

Listed so they're not forgotten — but explicitly **not** in the near roadmap. Each is either too big for the current team, or has a workaround the user can adopt today.

| Feature | Why deferred | Today's workaround |
|---|---|---|
| Live TV / DVR | Whole subsystem; needs HDHomeRun tuner protocol, EPG parsing, scheduler, recording cleanup | Use Jellyfin/Channels in parallel for now |
| AirPlay receiver | Apple platform-restricted; library landscape (e.g. RPiPlay) is shaky on .NET | Use AirPlay-receiver appliance in front of TV |
| DLNA renderer | Niche audience (older TVs), large protocol surface, security footguns | Cast (Phase 3.1) covers most use cases |
| Native mobile apps (Android/iOS) | Multi-platform native build introduces a whole second toolchain | PWA (Phase 2.3) gets to "install on phone" without it |
| Multi-version / editions | Needs schema reshuffle + UI work for "play which version" | Users can keep editions in separate libraries |
| OIDC / SSO (Authelia, Authentik, Keycloak) | Implementable in ~1 week of focused work; useful but lower-leverage than 2FA + API tokens | Authelia's `auth_request` directive in Caddy fronts SoftMedia today |
| Trakt / Last.fm / AniList scrobbling | Once Phase 2.4 webhooks ship, this becomes user-written glue, not first-party code | Community can publish webhook → Trakt translators |
| Music lyrics / ReplayGain / EQ | Each is a self-contained mini-feature; great "good first contributions" | None |
| Photos library | `MediaType.Photo` + `ExifMetadataProvider` partially exist; finishing it is its own arc | Use Immich / PhotoPrism in parallel |

---

## What This Plan Deliberately Avoids

- **First-party cloud anything.** No relay, no metric upload, no "SoftMedia Account." The closest we come is letting the *user* point a webhook at *their* Discord/ntfy.
- **Vendor SDKs that have free-tier surprises.** Cast SDK is included because it's a static JS file from Google with no per-server quota; Trakt/Last.fm SDKs are *not* included as first-party code because their terms shift.
- **Paid metadata providers beyond OMDb.** OMDb is intended to ship with a *maintainer-funded shared key* as the default (`OMDbApiKeyMode=softmedia` — already the default in `SettingsService.cs:118`), with a `custom` mode that lets the end user supply their own key as a fallback should the shared key be quota-exhausted, or the project stop being maintained. This shared-key model does **not** introduce a cloud dependency for the user — clients still hit `api.omdbapi.com` directly; only the API quota is funded centrally. We are *not* adding TMDb / IMDb / Fanart.tv as first-party providers — Wikidata + TVMaze + MusicBrainz + Open Library remain the keyless backbone.
- **Polish work that doesn't move the parity needle.** Themes, custom CSS, second dark-mode palettes, font-pack downloads — defer indefinitely until the parity items are done.

---

## Suggested Sequencing Summary

| Sprint | Items | Why this block |
|---|---|---|
| 0 | 0.1, 0.2 | Fix the rate-limiter regression and doc drift before anything new lands. |
| 1 | 1.1, 1.2 | Backup and API tokens are the foundation everything else builds on. |
| 2 | 1.3, 1.4, 1.5 | Now that operators trust the server, give them the dials and dashboards (and finish OMDb's shared-key path so first-run metadata Just Works). |
| 3 | 2.1, 2.2 | Trickplay sprites + "why transcoding" — the two highest-perceived-value UX items. |
| 4 | 2.3, 2.4 | PWA + webhooks — closes "do you have an app?" and unlocks the integration ecosystem. |
| 5 | 2.5 | 2FA — enables the "expose to internet" guidance to be safely written. |
| 6+ | Phase 3, parallel | Differentiation work, parallelisable across contributors. |

Each phase is a real shippable release. After Phase 2 SoftMedia is *competitive* with self-hosted Jellyfin for the median user; after Phase 3 it has a couple of clear advantages.

## Open Questions for Maintainer Sign-off

1. Is the "Server > Maintenance" settings category the right home for backup/restore, or should it live under a top-level `[Admin]` tree?
2. Do API tokens (1.2) need scope-per-token or is "admin / user" sufficient for v1?
3. Should the per-user bandwidth cap (1.3) be admin-only or self-service (a "limit my own bandwidth on mobile data" toggle)?
4. Are we OK with Chromecast (3.1) shipping before TOTP 2FA (2.5)? Sequencing them as written says "no" — Cast first, 2FA after — but Cast is technically easier and could ship anytime.
5. Which OMDb tier does the project commit to funding (1.4)? `basic` covers ~100k requests/day for a small fee and is probably enough for the early user base; `free` (1k/day) saturates fast once the project has any traction. This choice determines how aggressively the "use your own key" affordance needs to be surfaced.
