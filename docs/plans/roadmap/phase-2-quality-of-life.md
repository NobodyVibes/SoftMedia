# Phase 2 — Quality of Life

**Roadmap Phase:** 2 of 4
**Status:** Complete *(2026-05-13; P2-WI-004 partial — 3 events deferred)*
**Estimated Duration:** 4-6 weeks
**Date:** 2026-05-11
**Parent Document:** [00-roadmap-overview.md](./00-roadmap-overview.md)

## 1. Phase Summary

> **⚠ Pre-implementation review applied (2026-05-13).** All five work items were verified against the live codebase before coding; **all five required rescoping**. Notably: trickplay's `data/` path is fictional (use `wwwroot/cache/`); the transcode explainer's structured reason keys don't exist (`StreamPlanService` emits free-form English); the PWA premise that ASP.NET serves the SPA is false; webhooks have **no clean event hooks** for 3 of 5 events (deferred); TOTP needs a new rate-limit policy (the existing one is IP-only). Authoritative corrections: [`phase-2-rescope-2026-05-13.md`](./phase-2-rescope-2026-05-13.md). Where the spec below conflicts with the rescope doc, the rescope doc wins.

Phase 2 lifts SoftMedia from "trustworthy" to "pleasant to use day-to-day." Five work items: pre-generated trickplay previews, a user-facing transcode explainer, a Progressive Web App shell, outbound webhooks, and optional TOTP two-factor authentication.

## 2. Objectives

- Scrubbing a video produces instant thumbnail previews without spawning new FFmpeg processes per scrub.
- Non-administrators can self-diagnose why a stream is being transcoded.
- The web client installs to a phone or desktop as a Progressive Web App.
- Operators can wire SoftMedia events into Discord, ntfy, Home Assistant, or any HTTPS endpoint of their choosing.
- Users can optionally protect their account with a TOTP second factor.

## 3. Prerequisites

- Phase 0 complete.
- Phase 1 complete. P2-WI-005 (TOTP) depends on the working rate limiter from P0-WI-001 and the admin task visibility from P1-WI-005.

## 4. Work-Item Summary

| ID | Title | Status | Effort |
|----|-------|--------|--------|
| P2-WI-001 | Pre-Generated Trickplay Sprite Sheets | **Complete** (2026-05-13) | 3-4 d |
| P2-WI-002 | User-Facing Transcode Decision Panel | **Complete** (2026-05-13) | 2-3 d |
| P2-WI-003 | Progressive Web App Shell | **Complete** (2026-05-13) | 2-3 d |
| P2-WI-004 | Outbound Webhook Subscriptions | **Partial** (library.scan.* + webhook.test shipped; media.added/played + transcode.failed deferred — no clean hooks) | 4-5 d |
| P2-WI-005 | TOTP Two-Factor Authentication | **Complete** (2026-05-13) | 4-6 d |

> **Implementation status (2026-05-13).** All five items implemented per the corrected approach in [`phase-2-rescope-2026-05-13.md`](./phase-2-rescope-2026-05-13.md); full verification log in §8 below. Branch: `security/hardening-wave-1`. Server tests: 624 passed / 1 skipped / 0 failed (was 603 before Phase 2; +21). Client: 152 vitest tests pass; production build emits `sw.js` + `manifest.webmanifest`. P2-WI-004 ships the two events with clean server hooks (`library.scan.completed/failed`) plus `webhook.test`; the three events needing new signals (`media.added`, `media.played`, `transcode.failed`) are deferred to a follow-up with their required instrumentation.

## 5. Work Items

### P2-WI-001 — Pre-Generated Trickplay Sprite Sheets

#### Motivation

The current on-demand frame endpoint at `src/SoftMedia.Server/Controllers/TranscodeController.cs:240-260` spawns an FFmpeg process per scrub event. A pre-generated sprite sheet — a single JPEG containing a grid of thumbnails — is the industry standard (Plex calls it BIF, Jellyfin calls it trickplay) and renders instantly from one cached file regardless of how many users scrub the same video.

#### Specification

##### Generation Worker

New background worker `TrickplayWorker` runs after each scan completion for video items.

- FFmpeg invocation: `-vf "fps=1/{interval},scale={width}:-1,tile={cols}x{rows}"` producing one or more JPEG tiles.
- Output location: `data/trickplay/{itemId}/sheet-{n}.jpg` plus `data/trickplay/{itemId}/manifest.json`.
- Generation cost: bounded by the same global concurrency cap that applies to transcoding (P1-WI-003). Trickplay generation is treated as a low-priority transcode and yields to user-initiated transcodes.

##### Manifest

`data/trickplay/{itemId}/manifest.json`:

```json
{
  "version": 1,
  "tileWidth": 320,
  "tileHeight": 180,
  "intervalSeconds": 10,
  "columns": 10,
  "rows": 10,
  "sheets": [
    { "file": "sheet-0.jpg", "tileCount": 100 }
  ]
}
```

##### Endpoints

- `GET /api/v1/trickplay/{itemId}/manifest.json` — returns the manifest. Auth: same as media endpoints.
- `GET /api/v1/trickplay/{itemId}/{sheetFile}` — serves the JPEG sheet with aggressive cache headers (`Cache-Control: public, max-age=31536000, immutable`).

##### Player Integration

`src/SoftMedia.Client/src/components/player/VideoPlayer.tsx` requests the manifest on load; when present, uses the sheet for scrub previews; when absent, falls back to the existing on-demand `/api/transcode/{id}/frame?time=` endpoint.

##### Settings (new `[Playback] > Trickplay` subgroup)

- `TrickplayEnabled` (bool, default `true`)
- `TrickplayIntervalSeconds` (int, default `10`)
- `TrickplayThumbnailWidth` (int, default `320`)

#### Files Affected

- `src/SoftMedia.Server/Services/Media/TrickplayService.cs` — **new**.
- `src/SoftMedia.Server/Services/Background/TrickplayWorker.cs` — **new**.
- `src/SoftMedia.Server/Controllers/TrickplayController.cs` — **new**.
- `src/SoftMedia.Server/Services/Infrastructure/SettingsService.cs` — register defaults.
- `src/SoftMedia.Client/src/components/player/VideoPlayer.tsx` — extend scrub preview.

#### Acceptance Criteria

- A newly-scanned video item has its trickplay manifest present within wall-clock duration ≤ 2× the source video length on test hardware.
- Scrub preview loads in ≤ 50 ms P95 once the manifest is cached client-side.
- The existing on-demand `/frame` endpoint continues to operate for items without a manifest (verified by deletion of the trickplay directory and replay).
- Trickplay generation honours `MaxConcurrentTranscodesGlobal` from P1-WI-003 — assertion via integration test.

#### Estimated Effort

3-4 days.

#### Dependencies

- P1-WI-003 (concurrency cap honoured by the worker).

#### Risks

- **Disk-space growth on large libraries.** Mitigation: document expected size (~5 MB per hour of video at default settings); expose `TrickplayThumbnailWidth` so operators can reduce.

---

### P2-WI-002 — User-Facing Transcode Decision Panel

#### Motivation

`TranscodeDebugService` already produces structured "why this stream chose direct/remux/transcode" information, used by the admin-only `PlayerDebugPanel`. Surfacing this information to non-administrators in plain English is one of the highest-leverage UX improvements in the roadmap: it requires no new backend feature work, and it pre-empts an entire class of user support questions ("why is my stream transcoding?") that consume disproportionate community-support time on Plex and Jellyfin.

#### Specification

##### Endpoint

`GET /api/transcode/{id}/explanation` — accessible to any authenticated user (not admin-restricted).

- Internally calls the existing `TranscodeDebugService.GetDebugInfoAsync`.
- Returns the user-safe subset only: reason strings, codec names, and bitrate values.
- Excludes: internal file paths, raw FFmpeg argv, hardware-acceleration device names, server-side temp directories.

##### Translation Layer

New mapping module `TranscodeExplanationTranslator` converts internal reason strings to plain-English explanations.

Example mapping:

| Internal reason | User-facing explanation |
|-----------------|-------------------------|
| `audio.codec.unsupported.client` | "Your browser doesn't support DTS audio. Converting to AAC for compatibility." |
| `video.codec.unsupported.client` | "Your browser cannot play H.265. Converting to H.264." |
| `hdr.tonemap.subtitle-burn` | "HDR is being tone-mapped because subtitles are being burned into the video." |
| `bitrate.clamp.wan` | "Stream is being limited to {value} Kbps because you're on a remote connection." |

##### UI

- "Why is this transcoding?" entry in the video player's gear menu.
- Modal showing the explanation list. For Direct-Play streams, the modal shows: "Playing directly — no conversion needed."

#### Files Affected

- `src/SoftMedia.Server/Controllers/TranscodeController.cs` — extend with `/explanation` endpoint.
- `src/SoftMedia.Server/Services/Transcoding/TranscodeExplanationTranslator.cs` — **new**.
- `src/SoftMedia.Client/src/components/player/VideoPlayer.tsx` — gear-menu entry.
- `src/SoftMedia.Client/src/components/player/TranscodeExplanationModal.tsx` — **new**.
- `src/SoftMedia.Client/src/i18n/explanations.json` — translation strings.

#### Acceptance Criteria

- A non-admin user playing a transcoded stream can open the modal and see at least one human-readable explanation.
- Direct-Play streams show "Playing directly — no conversion needed."
- No internal paths, FFmpeg argv, or admin-only fields appear in the response payload — assert via integration test.
- Every reason string emitted by `TranscodeDebugService` has a corresponding translation entry — assert via test that diffs `TranscodeDebugService` output reasons against the translator's known keys.

#### Estimated Effort

2-3 days.

#### Dependencies

- None.

#### Risks

- **Translation strings drift as new transcode reasons are added.** Mitigation: the missing-key assertion test above fails the build when a new reason is added without a translation.

---

### P2-WI-003 — Progressive Web App Shell

#### Motivation

Closes the "do you have an app?" objection without the cost of a native build. Phase 2 PWA explicitly does **not** include offline media — that is a deferred concern. Phase 2 ships install-to-home-screen, full-screen running, and a branded offline error shell.

#### Specification

##### Build Integration

- Add `vite-plugin-pwa` to `src/SoftMedia.Client/package.json`.
- Configure with Workbox-based runtime caching.

##### Manifest

`src/SoftMedia.Client/public/manifest.webmanifest`:

```json
{
  "name": "SoftMedia",
  "short_name": "SoftMedia",
  "display": "standalone",
  "theme_color": "#007AFF",
  "background_color": "#0a0a0a",
  "start_url": "/",
  "icons": [
    { "src": "/icons/icon-192.png", "sizes": "192x192", "type": "image/png" },
    { "src": "/icons/icon-512.png", "sizes": "512x512", "type": "image/png" },
    { "src": "/icons/icon-512-maskable.png", "sizes": "512x512", "type": "image/png", "purpose": "maskable" }
  ]
}
```

Icon design: brand gradient `#007AFF → #8A2BE2` over a dark background.

##### Service Worker Strategy

- App shell (HTML, JS, CSS, fonts): cache-first.
- `/api/*` requests: network-first with a 5-second timeout, falling back to cache only for `GET` requests that were previously cached.
- Media streams, segment URLs, image proxy: **never cached**.
- Cache versioning via Vite content-hashed filenames; service worker cleans old caches on activation.

##### Offline Shell

New page `src/SoftMedia.Client/src/pages/OfflinePage.tsx` rendered when navigation fails due to offline state. Branded with the SoftMedia gradient and a "Retry" button.

#### Files Affected

- `src/SoftMedia.Client/package.json` — add `vite-plugin-pwa`.
- `src/SoftMedia.Client/vite.config.ts` — configure PWA plugin.
- `src/SoftMedia.Client/public/manifest.webmanifest` — **new**.
- `src/SoftMedia.Client/public/icons/*` — **new** icon assets.
- `src/SoftMedia.Client/src/sw-register.ts` — **new**.
- `src/SoftMedia.Client/src/pages/OfflinePage.tsx` — **new**.
- `src/SoftMedia.Client/src/main.tsx` — register the service worker.

#### Acceptance Criteria

- Lighthouse PWA audit returns ≥ 90.
- Install prompt appears on Chrome (Android) and Edge (Windows).
- After installation, the app runs in `standalone` display mode (no browser chrome).
- Offline navigation shows the branded offline page, not a browser-default error.
- A deploy with a new asset hash invalidates the old cache on next visit.

#### Estimated Effort

2-3 days.

#### Dependencies

- None.

#### Risks

- **Service worker caches a broken deploy and locks users out.** Mitigation: SW registration includes a `skipWaiting` policy; SW activation cleans old caches; document the cache-flush procedure in the operator guide.

---

### P2-WI-004 — Outbound Webhook Subscriptions

#### Motivation

Plugs SoftMedia into the user's existing notification stack (Discord, ntfy, Home Assistant, Slack, etc.) without first-party SDKs or hosted relays. The user configures the URL; SoftMedia POSTs JSON. This work item also positions SoftMedia for the Phase 4 deferred scrobbling features (Trakt, Last.fm, AniList), which can then be implemented as user-written webhook translators rather than first-party code.

#### Specification

##### Data Model

New table `WebhookSubscriptions`:

| Column | Type | Notes |
|--------|------|-------|
| `Id` | Guid | PK |
| `UserId` | Guid | FK → Users |
| `Url` | string | target URL |
| `Events` | string | JSON array of event names |
| `Secret` | string | per-subscription HMAC secret |
| `Active` | bool | default `true` |
| `CreatedAt` | DateTime UTC | |
| `LastDeliveryAt` | DateTime? UTC | |
| `LastDeliveryStatus` | string? | last-delivery HTTP status or error |

##### Event Taxonomy (v1)

- `media.added`
- `media.played` — fired when an interaction crosses the watched threshold
- `transcode.failed`
- `library.scan.completed`
- `library.scan.failed`
- `webhook.test` — synthetic event for the "Test" affordance

##### Payload Format

```json
{
  "event": "media.added",
  "timestamp": "2026-05-11T20:00:00Z",
  "actor": { "userId": "...", "username": "..." },
  "payload": { /* event-specific fields */ }
}
```

Headers on every delivery:

- `X-SoftMedia-Event: media.added`
- `X-SoftMedia-Signature: sha256=<hex>`
- `User-Agent: SoftMedia-Webhooks/1.0`

Signature = `HMAC-SHA256(secret, requestBodyBytes)`.

##### Delivery Pipeline

- In-memory queue consumed by `WebhookDispatchWorker`.
- Retry policy: 3 attempts at exponential backoff (1 s, 5 s, 30 s).
- On final failure, a `SystemNotification` is recorded for the owning user with severity `Warning`.

##### Settings

Under `[Server] > Webhooks`:

- `WebhooksEnabled` (bool, default `true`)
- `WebhookRequestTimeoutSeconds` (int, default `10`)
- `AllowHttpWebhooks` (bool, default `false`) — when `false`, only HTTPS URLs accepted, or HTTP if the target is in RFC 1918.
- `AllowLoopbackWebhooks` (bool, default `false`) — when `false`, loopback URLs are rejected.

##### UI

`My Account > Webhooks`: list, add, edit, delete, "Test" button that enqueues a `webhook.test` synthetic event.

#### Files Affected

- `src/SoftMedia.Server/Models/WebhookSubscription.cs` — **new**.
- `src/SoftMedia.Server/Services/Infrastructure/WebhookDispatcher.cs` — **new**.
- `src/SoftMedia.Server/Services/Background/WebhookDispatchWorker.cs` — **new**.
- `src/SoftMedia.Server/Controllers/WebhooksController.cs` — **new**.
- `src/SoftMedia.Client/src/pages/MyAccountPage.tsx` — extend.
- `src/SoftMedia.Server/Migrations/` — new migration.

#### Acceptance Criteria

- Adding an ntfy URL → adding a new media item → ntfy notification arrives within 5 seconds.
- Webhook with a deliberately invalid URL fails 3× → admin sees a `SystemNotification` referencing the failure.
- HMAC signature validates correctly against the recorded request body when verified externally with the per-subscription secret.
- Loopback URL is rejected with `400 Bad Request` when `AllowLoopbackWebhooks=false`.
- HTTP-scheme URL targeting a public IP is rejected when `AllowHttpWebhooks=false`.

#### Estimated Effort

4-5 days.

#### Dependencies

- None.

#### Risks

- **SSRF via webhook URL pointing at internal services.** Mitigation: HTTPS-only by default; loopback-blocked by default; document operator-side risk for cases where they opt these protections out.

---

### P2-WI-005 — TOTP Two-Factor Authentication

#### Motivation

Once an administrator follows the SDD §6.1 DuckDNS+Caddy recipe, the login page becomes reachable from the open internet. Password-only authentication becomes the weakest link. TOTP closes that gap with a small integration footprint via `Otp.NET`. Passkeys / WebAuthn are deliberately deferred to a later phase because the library landscape is messier and the user-benefit gradient over TOTP is small in the homelab context.

#### Specification

##### Data Model

New table `UserTotp`:

| Column | Type | Notes |
|--------|------|-------|
| `UserId` | Guid | PK, FK → Users |
| `EncryptedSecret` | string | AES-encrypted TOTP shared secret |
| `EnabledAt` | DateTime? UTC | non-null = enrolled |
| `RecoveryCodes` | string | JSON array of SHA-256 hashes of recovery codes |
| `UsedRecoveryCodes` | string | JSON array of consumed recovery-code hashes |

##### Enrollment Flow

1. `POST /api/v1/account/totp/enroll` returns `{ secret, qrUri }`. Client renders the QR using a **client-side** QR library (e.g. `qrcode.react`). Server does not bundle QRCoder.
2. User scans QR with authenticator app and enters a current code.
3. `POST /api/v1/account/totp/enroll/confirm` body `{ code }` — on success, sets `EnabledAt`, generates 10 recovery codes (returned once, never retrievable again), persists hashed forms.

##### Login Flow

- Successful password validation when `UserTotp.EnabledAt != null` returns `{ status: "2fa_required", challengeId: "..." }` *instead of* issuing tokens. The challenge is server-side state with a 5-minute TTL.
- Client posts `POST /api/v1/auth/2fa` with `{ challengeId, code }` to complete the login. The `code` may be a TOTP code or a recovery code; recovery codes are single-use.
- 2FA challenges are rate-limited per challenge ID and per user (using the existing `AuthRateLimitPolicy` infrastructure — depends on P0-WI-001 for correct partitioning).

##### Disable Flow

- `POST /api/v1/account/totp/disable` with `{ currentPassword, code }` where `code` is a TOTP code or recovery code.
- Admins disabling TOTP on another user's account go through a different path (`AdminController`) that requires the admin to re-authenticate with their own 2FA before action.

##### Admin Policy

- `Require2FAForAdmins` setting (default `false` until project audience grows).
- When `true`, admin users without `UserTotp.EnabledAt` are forced into enrollment on next login.

#### Files Affected

- `src/SoftMedia.Server/Models/UserTotp.cs` — **new**.
- `src/SoftMedia.Server/Services/Identity/TotpService.cs` — **new**.
- `src/SoftMedia.Server/Controllers/AccountController.cs` — extend.
- `src/SoftMedia.Server/Controllers/AuthController.cs` — extend with 2FA challenge path.
- `src/SoftMedia.Server/Controllers/AdminController.cs` — extend with admin-disable.
- `src/SoftMedia.Client/src/pages/LoginPage.tsx` — extend with 2FA step.
- `src/SoftMedia.Client/src/pages/MyAccountPage.tsx` — extend with enrollment UI.
- `src/SoftMedia.Server/Migrations/` — new migration.

#### Acceptance Criteria

- Enroll → next login asks for code → correct code logs in.
- Recovery code works once; second use of same code returns 401.
- Wrong code 6× within 5 minutes for the same challenge triggers the rate limiter — verified with the real client IP after P0-WI-001.
- `EncryptedSecret` is AES-encrypted at rest using a key derived from the existing JWT signing key; documented in the security guide.
- `Require2FAForAdmins=true` forces unenrolled admin users into enrollment on next login.

#### Estimated Effort

4-6 days.

#### Dependencies

- P0-WI-001 (rate limiter must work properly for 2FA-code attempts).

#### Risks

- **Lost device + lost recovery codes = locked-out user.** Mitigation: admin can disable a user's 2FA via the admin panel; admin must re-authenticate with their own 2FA before action; remote bypass is not possible. Procedure documented prominently in the user guide.
- **Encrypted-secret key reuse with the JWT signing key.** Mitigation: documented trade-off; rotating the JWT signing key requires re-enrollment of all 2FA users. Operators are warned in the security guide.

## 6. Phase Exit Criteria

Phase 2 is complete when:

- All five work items report acceptance criteria passing in CI.
- A maintainer has merged the changes to `main`.
- Lighthouse PWA audit ≥ 90 on the production build.
- The change log in `00-roadmap-overview.md` records phase completion.

## 7. Out of Scope

- Offline media caching in the PWA. Phase 2 ships only the shell and offline error page.
- Passkey / WebAuthn enrollment. Deferred — `Phase 4` register may pick this up under a future phase.
- Backup of webhook delivery history beyond `LastDeliveryAt` / `LastDeliveryStatus`. A full audit log is out of scope.
- A user-curated "favourites" trickplay variant (e.g. higher density). Defaults only in this phase.

## 8. Verification Log *(added 2026-05-13)*

Server build clean (0 errors). Client `tsc -b` clean; `npm run build` emits PWA artifacts; `vitest` 152/152. Server tests: 624 passed / 1 skipped / 0 failed (one transient `ResetSeedNoiseAsync` parallel-SQLite harness flake clears on re-run — pre-existing, not introduced here). Net Phase 2: +21 server tests.

### P2-WI-002 — Transcode explainer
- **Reused** the existing `StreamPlan` (already fetched by the player at `/plan`) instead of adding a new `/explanation` endpoint. Added a structured `List<StreamReasonCode>` (`{code, params}`) to the DTO alongside the free-form `Reason`, populated in `StreamPlanService` (direct/remux/transcode + bitrate-clamp). Client `TranscodeExplanationModal` translates codes via the **existing** i18next system (`src/lib/i18n.ts`, en+es), reached from a player info button.
- **Corrections applied:** the spec's dotted reason keys didn't exist and `TranscodeDebugService` emits no reasons — so reasons are sourced from `StreamPlanService` as designed in the rescope; no parallel `explanations.json`.
- **Tests:** 2 new structured-code assertions in `StreamPlanServiceBitrateTests`.

### P2-WI-001 — Trickplay sprites
- `TrickplayService` writes sheets + manifest to **`wwwroot/cache/trickplay/{itemId}/`** (not the fictional `data/`); `TrickplayWorker` backfills via a self-healing sweep gated by its **own** `SemaphoreSlim(2)` (not the transcode cap, which is unreachable). `TrickplayController` serves manifest/sheets (JWT `?token=` lift added for `/api/v1/trickplay`). Player prefers the sprite tile (CSS background-position via `useTrickplay`), falls back to the on-demand frame endpoint.
- **Tests:** 8 (`TrickplayServiceTests` — path-traversal guard, manifest/sheet resolution).

### P2-WI-003 — PWA shell
- `vite-plugin-pwa` (autoUpdate); `manifest.webmanifest` (`#0f172a` bg, `#007AFF` theme, 3 generated brand-gradient icons); Workbox precaches the shell only, `navigateFallbackDenylist` excludes `/api`, `/cache`, `/hubs`; `maximumFileSizeToCacheInBytes` raised for the ~2.5 MB shell bundle. Offline detector renders a branded `OfflinePage`. SW registered in `main.tsx`; plugin added to `vite.config.ts` **only** (not vitest).
- **Correction applied:** ASP.NET does not serve the SPA — the SW/manifest ship in the Vite dist behind the reverse proxy, so no server change was needed.
- **Verification:** production build emits `dist/sw.js`, `dist/workbox-*.js`, `dist/manifest.webmanifest`, icons (13 precache entries). Lighthouse-in-CI gate N/A (no CI pipeline — manual check).

### P2-WI-005 — TOTP 2FA
- `Otp.NET` (server) + `qrcode.react` (client). `UserTotp` table (AES-encrypted secret, key derived from JWT secret; SHA-256 recovery-code hashes). **New `"2fa"` rate-limit policy** partitioned per challengeId (the existing policy is IP-only — corrected). Login returns a `2fa_required` challenge when enabled; `/auth/2fa` completes it (TOTP or single-use recovery code). Enrollment `TotpCard` (QR) + login 2FA step + admin recovery endpoint.
- **Tests:** 5 (`TotpIntegrationTests` — enroll/confirm, challenge-then-complete, wrong code, single-use recovery, disable); adjacent `AuthControllerRefreshTests` constructor updated.

### P2-WI-004 — Webhooks (partial)
- `WebhookSubscription` table; `WebhookDispatcher` (singleton queue) + `WebhookDispatchWorker` (HMAC-SHA256 signed POST, 3-retry backoff, admin dead-letter notification). `WebhooksController` CRUD + `/test`; secret returned once. SSRF guard via `WebhookSecurity.ValidateTarget` (DNS-resolve + classify; rejects loopback/public-HTTP/DNS-rebind per settings). `NetworkClassifier` extended with `IsLoopback`/`IsPrivate`. Wired into `LibraryScanQueueService.CompleteJob/FailJob`. New `Webhooks` settings group; `Webhooks` named HttpClient. Client `WebhooksCard`.
- **Deferred (per rescope):** `media.added`, `media.played`, `transcode.failed` — no clean hooks exist (no per-item scan event, no PlayCount/threshold, no `TranscodeState.Failed`). Tracked for a follow-up that adds those signals.
- **Tests:** 7 `WebhookSecurityTests` (sign + SSRF) + 7 `WebhooksIntegrationTests` (CRUD/auth/secret-once/ownership) + 1 `WebhookRedirectPolicyTests`; `LibraryScanQueueServiceTests` constructor updated.
- **SSRF-via-redirect hardening (2026-05-13, automated security review):** the pre-send IP validation could be bypassed if `HttpClient` transparently followed a 3xx to an internal address. Fixed by configuring the `Webhooks` named client with `SocketsHttpHandler { AllowAutoRedirect = false }` and treating any 3xx as a permanent, non-retryable block in the worker. Guarded by `WebhookRedirectPolicyTests`.
