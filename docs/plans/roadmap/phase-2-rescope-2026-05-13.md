# Phase 2 — Pre-Implementation Review & Rescope

**Date:** 2026-05-13
**Status:** Authoritative — supersedes the original specifications in `phase-2-quality-of-life.md` where they conflict
**Method:** Five parallel deep-recon agents verified each work-item spec against the live codebase before any code was written, plus hand-verification of the trickplay anchors. Same process that caught a latent auth bug and a fictional `data/` tree in Phase 1.

## Why this document exists

As in Phases 0 and 1, **every Phase 2 work item rests on at least one stale premise.** Read this before implementing. The original spec bodies remain in `phase-2-quality-of-life.md` for context; where they conflict, this document wins.

---

## P2-WI-001 — Trickplay Sprite Sheets — `proceed-with-rescope`

### Corrections
1. **`data/trickplay/` is fictional** (same `data/`-dir myth as Phase 1). Real cache root is `wwwroot/cache/` — see `ThumbnailService.cs:23-27` (`{WebRootPath}/cache/images/thumbnails`). **Use `wwwroot/cache/trickplay/{itemId}/`.**
2. **Cannot reuse the concurrency cap.** The spec says generation honors `MaxConcurrentTranscodesGlobal` — that key never existed (the live key is `MaxSimultaneousTranscodes`), and it is enforced *only inside* `TranscodeService.StartTranscodeAsync`. A standalone worker can't reach it. **Use the worker's own `SemaphoreSlim`** (e.g. max 1–2 concurrent generations) so trickplay never competes with user-initiated transcodes.
3. FFmpeg path comes from `IBinaryLocationService.ResolveFFmpegPath()` (the pattern `VideoPreviewService.cs:59` uses). Reuse it.
4. The player already fetches frames at `VideoPlayer.tsx:1535` (`/api/transcode/{id}/frame?time=`). Keep that as the **fallback**; prefer the sprite manifest when present.
5. Register telemetry into the **existing** `IScheduledTaskRegistry` (P1-WI-005), not a new one.

---

## P2-WI-002 — Transcode Explainer — `proceed-with-rescope`

### Corrections
1. **`TranscodeDebugService` emits NO reason strings.** `GetDebugInfoAsync` returns `playbackMode`/`decision` booleans but no reason field, and it does not even surface `StreamPlan.Reason`. The spec's dotted keys (`audio.codec.unsupported.client`, `bitrate.clamp.wan`, …) **exist nowhere in the code.**
2. **The real reason source is `StreamPlanService`** — free-form English sentences on `StreamPlan.Reason` built by `DetermineTranscodeReason` (`:472-489`), `CreateDirectPlayPlan` (`:362`), `CreateRemuxPlan` (`:385`), and the P1 bitrate `AppendNote` (`:237-242`). Codec names/resolutions are interpolated *into* the strings.
3. **Decision:** refactor `StreamPlanService` to emit a structured `List<{code, params}>` of reason objects alongside the English `Reason` (keep `Reason` for back-compat), then the explainer translates `code` → human sentence client-side. This is cleaner than pattern-matching English and makes the completeness test feasible.
4. **`/debug` is NOT admin-gated** — class-level `[Authorize]` only; only `serverSettings` and full `probe.filePath` are admin-redacted *inside* the service. So a non-admin explainer needs no auth relaxation, but it MUST NOT surface `serverSettings.hardwareAcceleration`, `preset`, `crf`, or `probe.filePath`.
5. **Reuse the existing i18next system** (`src/lib/i18n.ts` inline resources) — do NOT create a parallel `src/i18n/explanations.json`.
6. Endpoint cite fix: the debug endpoint is `GetPlaybackDebug` at `TranscodeController.cs:242-257`, not `:223-238`.

---

## P2-WI-003 — PWA Shell — `proceed-with-rescope`

### Corrections
1. **ASP.NET does NOT serve the SPA.** `app.UseStaticFiles()` (`Program.cs:211`) serves only `wwwroot/cache/*` (images). There is no `index.html`/JS bundle in `wwwroot`, no `MapFallback`, no SPA-fallback. The SPA + `manifest.webmanifest` + `sw.js` are served by the Vite build output behind the reverse proxy. So there's no server-side fallback to intercept `/sw.js` — but also no need to touch the .NET server. SW scope is `/` (base path default).
2. **No blanket `/api/* network-first`.** Media and image-proxy live *under* `/api/v1/`, and transcode is `/api/transcode/*` (no `/v1`). The SW MUST explicitly bypass: `/api/v1/stream/*`, `/api/transcode/*`, `.m3u8`/segment URLs, `/api/v1/image/proxy`, and `/cache/*`. Cache-first only the built app shell.
3. **No icons exist** (`public/` has only `vite.svg`). The 192/512/512-maskable PNGs must be generated from the brand gradient `#007AFF → #8A2BE2`.
4. **`background_color`**: use `#0f172a` (the app's real `--color-bg`, `index.css:8`), not the spec's `#0a0a0a`.
5. Add `vite-plugin-pwa` to `vite.config.ts` ONLY, not `vitest.config.ts`.
6. **No CI exists** → the "Lighthouse ≥ 90 in CI" gate has no runner. Verify Lighthouse manually; treat "bootstrap CI" as a separate prerequisite (same posture as P1-WI-004).

---

## P2-WI-004 — Outbound Webhooks — `proceed-with-rescope` (largest scope gap)

### Corrections
1. **The event trigger points are NOT clean hooks** — this is materially larger than the spec implied:
   - `library.scan.completed` / `library.scan.failed` — **clean**: `LibraryScanQueueService.CompleteJob` (`:220`) / `FailJob` (`:240`) are explicit terminal methods.
   - `media.added` — **no per-item event**; items are added inside a parallelized `BaseMediaScanner` loop with batched saves. A clean proxy is to include the new-item count in `library.scan.completed`.
   - `media.played` — **no PlayCount/threshold logic exists** at all. `UserMediaInteraction` has no PlayCount; `MarkWatchedAsync` just sets a client-supplied bool. Requires net-new detection.
   - `transcode.failed` — **no `TranscodeState.Failed`** enum value; failures are only log lines. Requires a new failure signal.
2. **Decision for v1:** ship `library.scan.completed`, `library.scan.failed`, and `webhook.test` (the events with clean hooks). **Defer** `media.added`/`media.played`/`transcode.failed` to a follow-up with their required new signals, noted explicitly. This keeps the feature shippable and honest rather than wiring a half-baked event bus.
3. **Dead-letter design is wrong.** `NotificationService.CreateAsync` has **no `userId`** param and `SystemNotification` is admin-global; severity values are **lowercase** (`"warning"`). Record an admin-visible notification (owner encoded in metadata), not a per-user one.
4. **No `[Server] > Webhooks` subgroup** — settings are flat `Group` strings. Use a new flat `Group = "Webhooks"` with prefixed keys, seeded in `InitializeDefaultsAsync` (the `Maintenance.*` precedent).
5. **SSRF helper gap:** `NetworkClassifier.IsLan` collapses loopback+RFC1918+link-local+ULA into one bool. **Extend it** with `IsLoopback`/`IsPrivate` predicates to drive `AllowLoopbackWebhooks` vs `AllowHttpWebhooks` distinctly; resolve hostnames before classifying.
6. `IScheduledTaskRegistry` already exists (P1) — report into it.

---

## P2-WI-005 — TOTP 2FA — `proceed-with-rescope`

### Corrections
1. **New packages required:** server `Otp.NET` (no TOTP lib referenced); client `qrcode.react` (no QR lib). No CI to validate the .NET restore — verify `dotnet build` locally.
2. **Rate-limiter premise wrong.** `AuthRateLimitPolicy` partitions strictly by **client IP** (`ServiceCollectionExtensions.cs:333`). The spec's "per challenge ID and per user" + "6 wrong codes in 5 min" needs a **new policy** (e.g. `"2fa"`) partitioned on challenge/user. Add it alongside the existing policies.
3. **No AES/DataProtection helper exists** — the encrypt-secret-with-JWT-key logic is fully new. Inject `IConfiguration` into the new `TotpService` to read `JwtSettings:Secret` (as `TokenService.cs:25-26` does) and derive the key. Document that rotating the JWT secret forces 2FA re-enrollment.
4. Login branch inserts at `AuthController.cs:149` (after password+account-state checks, before token issuance). `LoginPage.tsx` already has a multi-step pattern (`showChangePasswordModal`) to mirror for the 2FA step.
5. Enrollment UI as a sibling `TotpCard` in `MyAccountPage` (next to `ApiTokensCard`). Admin-disable in `AdminController` (admin-only already).
6. `Require2FAForAdmins` seeded via the `SettingsService` default pattern.

---

## Implementation order (revised)

1. **P2-WI-002 Explainer** — contained; reuses existing decision data; the StreamPlanService structured-reason refactor also benefits future work.
2. **P2-WI-001 Trickplay** — self-contained worker; correct cache dir + own concurrency gate.
3. **P2-WI-003 PWA** — client-only; generate icons; careful SW caching rules.
4. **P2-WI-005 TOTP** — well-anchored; new packages + new rate-limit policy + AES.
5. **P2-WI-004 Webhooks** — largest; ship the clean-hook events (`library.scan.*`, `webhook.test`); defer the three that need new signals.

## Maintainer decisions captured (Auto Mode; redirect if wrong)
- **Webhook v1 events:** `library.scan.completed/failed` + `webhook.test` only; `media.added/played` + `transcode.failed` deferred (need new signals).
- **Explainer reason model:** refactor `StreamPlanService` to emit structured `{code, params}` reason objects.
- **Trickplay concurrency:** worker-owned semaphore (1–2), not the transcode cap.
