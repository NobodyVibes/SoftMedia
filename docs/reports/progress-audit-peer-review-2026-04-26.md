# SoftMedia Progress Audit — Peer Review 2026-04-26

**Reviewer:** Independent peer review pass
**Branch reviewed:** `security/hardening-wave-1`
**Report reviewed:** [progress-audit-2026-04-26.md](progress-audit-2026-04-26.md)
**Reference docs:** [SDD.md](../SDD.md), `docs/rules/*`

---

## Summary

The original report is substantially accurate and its severity assessments are reasonable. All five spec divergences in §3 were independently confirmed with file:line evidence. The report's security sketch on CSRF, symlink LFI, and the image proxy was directionally correct but underspecified — the deeper analysis below sharpens the actual exploitability of each. Four new issues not in the original report were found: a **High-severity authentication bypass** in the frame-preview endpoint, a Medium-severity exception-message info-leak in transcode controllers, a Medium-severity CORS wildcard shipped in `appsettings.json`, and a significant a11y failure in `ProgressBar.tsx`. The report's framing of the symlink issue as merely worth "evaluating" understates the severity on Linux. The overall health assessment — mid-build, well past prototype, with parental controls, HLS cleanup, and the issues found here as the most urgent gaps — stands.

---

## Verification of Original Findings

### §3.1 Parental Controls — CONFIRMED: No enforcement anywhere

Searched every plausible enforcement point:

- [src/SoftMedia.Server/Services/Infrastructure/MediaRepository.cs](../../src/SoftMedia.Server/Services/Infrastructure/MediaRepository.cs) — zero occurrences of `MaxRating` or `ContentRating`. No query filter of any kind.
- [src/SoftMedia.Server/Services/Infrastructure/LibraryRepository.cs:64-196](../../src/SoftMedia.Server/Services/Infrastructure/LibraryRepository.cs#L64-L196) — `GetLibraryItemsAsync` joins user interactions for personal star ratings only; no parental-rating gate.
- [src/SoftMedia.Server/Controllers/StreamController.cs:36-57](../../src/SoftMedia.Server/Controllers/StreamController.cs#L36-L57) — only `[Authorize]` plus the path-jail check.
- `MediaController.cs`, `BookController.cs`, `AudioController.cs`, `MusicController.cs` — grep for `MaxRating`/`ContentRating` returns nothing in any of these.

Crucially, [src/SoftMedia.Server/Services/Identity/TokenService.cs:44](../../src/SoftMedia.Server/Services/Identity/TokenService.cs#L44) embeds `MaxRating` as a JWT claim: `new Claim("MaxRating", user.MaxRating)`. The claim is available to every request handler, but nothing reads it to make an enforcement decision. The data model is ready; the enforcement layer is completely absent.

### §3.2 HLS Segment Cleanup — CONFIRMED: No background janitor

The nine hosted services at [ServiceCollectionExtensions.cs:263-294](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L263-L294) are confirmed as listed. No HLS cleanup service exists anywhere. The `Services/Background/` directory contains exactly three files: `HeroCacheWorker.cs`, `ImageDownloadQueueService.cs`, `RefreshTokenCleanupService.cs`.

**Nuance the original report missed:** [TranscodeService.cs:47-64](../../src/SoftMedia.Server/Services/Transcoding/TranscodeService.cs#L47-L64) deletes the entire `transcode-temp` directory at server startup, handling the "crash-stale" case. This does not address live-session accumulation when a client disconnects without hitting the DELETE endpoint.

### §3.3 Photos / EXIF — CONFIRMED: Scanner absent, provider registered

[ServiceCollectionExtensions.cs:107](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L107) registers `ExifMetadataProvider`. The scanning directory contains `BookScanner`, `GameScanner`, `MovieScanner`, `MusicScanner`, `TvScanner` — no `PhotoScanner`. Roadmap item, not regression.

### §3.4 SameSite=Lax Deviation — CONFIRMED

[AuthController.cs:326](../../src/SoftMedia.Server/Controllers/AuthController.cs#L326): `SameSite = SameSiteMode.Lax` is the only setting present. No code path sets `Strict`. [SDD.md §4.2](../SDD.md) requires `SameSite=Strict`. Severity (Low) is correct.

### §3.5 Rate Limit Numeric Mismatch — CONFIRMED, with additional mismatch

[ServiceCollectionExtensions.cs:193](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L193): comment says "15 attempts per minute". Line 236: `PermitLimit = 30`. **Additionally**, lines 194-196 describe the rationale for a *sliding* window, but line 232 uses `GetFixedWindowLimiter`. The original report caught the permit count; it missed the window-type mismatch in the same comment block. The comment is doubly incorrect.

---

## Deeper Security Analysis

### CSRF

**Verdict: Not currently exploitable, but SDD §6.2 is violated.**

Grep for `antiforgery`, `AntiForgery`, `ValidateAntiForgery`, `X-CSRF`, `X-XSRF`, `double.submit` across all of `src/SoftMedia.Server/` returns no application-code matches. The double-submit cookie pattern is absent.

This is not exploitable because: (1) the access JWT is held in client memory and sent in the `Authorization: Bearer` header — browsers do not auto-attach custom headers to cross-origin requests; (2) the refresh cookie is `SameSite=Lax` scoped to `Path=/api/v1/auth/`, which the browser will not include on cross-site sub-resource POSTs; (3) even the wide-open CORS configuration in [Program.cs:59-65](../../src/SoftMedia.Server/Program.cs#L59-L65) does not change browser preflight behavior for unapproved origins.

The worst-case CSRF attack is a cross-site logout, not a data breach. The SDD requirement should either be implemented or explicitly documented as intentionally replaced by the Bearer-in-header pattern.

### Symlink LFI on `StreamSecurityService`

**Verdict: Real, High severity on Linux.**

[StreamSecurityService.cs:24](../../src/SoftMedia.Server/Services/Security/StreamSecurityService.cs#L24) uses `Path.GetFullPath(filePath)`, which resolves `..` components but does not follow symlinks. On Linux (SDD §5.1 explicitly supported):

1. Admin adds library root `/media/movies`.
2. A symlink exists: `ln -s /etc /media/movies/sysconf` (requires filesystem access, not in-app manipulation).
3. `Path.GetFullPath("/media/movies/sysconf/passwd")` returns `/media/movies/sysconf/passwd`.
4. That string starts with `/media/movies/` → `IsPathAuthorized` returns `true`.
5. `GET /api/v1/stream/{id}` with a valid JWT serves `/etc/passwd`.

**Fix:** replace `Path.GetFullPath` with a resolution that follows symlinks before the prefix check. On .NET 6+, `new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path)` achieves this. Single-line change.

The original report says "reviewer should evaluate" — this needs no further evaluation. It is a confirmed LFI vector on Linux.

### JWT-in-Query-String Token Leak

[ServiceCollectionExtensions.cs:46-65](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L46-L65) lifts `?token=` / `?access_token=` for `/api/transcode`, `/api/v1/stream`, `/api/v1/audio`, `/api/v1/books`, `/api/v1/image`, `/api/v1/music`, `/api/media`, `/hubs/media`.

Mitigations already in place: [LoadingImage.tsx:70](../../src/SoftMedia.Client/src/components/ui/LoadingImage.tsx#L70) and many other `<img>` elements set `referrerPolicy="no-referrer"`, preventing token leakage via `Referer` headers to external hosts.

Remaining exposure surfaces:
1. **Browser history**: `?access_token=…` URLs for stream and transcode pages persist in browser history.
2. **Reverse proxy logs**: A user running Method B (DuckDNS + Caddy, SDD §6.1) will have 60-minute-valid tokens written in plaintext to Caddy/nginx access logs. This is the most likely real-world exposure path.
3. **`TranscodeController.cs:242`**: the frame-preview endpoint accepts an explicit `?token=` parameter outside the standard middleware path, and this URL is built literally in [VideoPlayer.tsx:1445](../../src/SoftMedia.Client/src/components/player/VideoPlayer.tsx#L1445). See **NEW-1** below for a related authentication-bypass finding on this same endpoint.

### JWT Access Token TTL

[appsettings.json:23](../../src/SoftMedia.Server/appsettings.json#L23): `"ExpiryMinutes": "60"`.

[TokenService.cs:29](../../src/SoftMedia.Server/Services/Identity/TokenService.cs#L29): the fallback default if the config key is missing is `"15"` — correct. The shipped `appsettings.json` overrides to 60 minutes.

At 60 minutes, a token extracted from a proxy log remains valid for up to an hour. The refresh-rotation design fully accommodates a 15-minute access TTL. One-character config change with meaningful security improvement.

### Image Proxy SSRF — DNS Rebinding Nuance

[ImageController.cs:85](../../src/SoftMedia.Server/Controllers/ImageController.cs#L85): host-string comparison before DNS resolution. DNS rebinding requires controlling the DNS for one of the eight allowlisted CDN hosts — outside the realistic threat model for a self-hosted product.

**One gap the original report missed:** the `ImageController` proxy at [lines 120-123](../../src/SoftMedia.Server/Controllers/ImageController.cs#L120-L123) creates a bare `HttpClient` via `_httpClientFactory.CreateClient()` (unnamed client). This client has **no `SoftMediaUserAgentHandler` attached**, unlike the `IImageCacheService` client. The proxy's outbound requests will use the spoofed browser UA set at line 122 rather than the SDD §4.3-mandated `SoftMedia/1.0 (...)` User-Agent. This violates the attribution compliance rule for Wikidata, MusicBrainz, and Open Library callers.

---

## Frontend A11y Sample Audit

**Files sampled:** `MediaCard.tsx`, `CastStripItem.tsx`, `ProgressBar.tsx`, `VideoPlayer.tsx`, `CreateUserModal.tsx`

### [MediaCard.tsx](../../src/SoftMedia.Client/src/components/items/MediaCard.tsx) — PASS (with caveat)

Play button (`line 161-167`): `<button type="button">`, `aria-label`, `min-w-[44px] min-h-[44px]`, paired hover/`focus-visible:ring-2`. Audio track card (`line 327-343`) uses `role="button" tabIndex={0}` with `onKeyDown` and `focus-visible:ring-2` — correct pattern for avoiding nested interactive elements.

**Caveat:** the play-button overlay is `opacity-0 group-hover/card:opacity-100`. Keyboard users who Tab to the card will encounter a focused but invisible button. A `focus-within:opacity-100` on the overlay wrapper would resolve this.

### [CastStripItem.tsx](../../src/SoftMedia.Client/src/components/details/CastStripItem.tsx) — PASS

Trigger button (`line 177-191`): `<button type="button">` with `onFocus`, `onBlur`, `onKeyDown`, `aria-expanded`, `aria-controls`, `min-h-[44px]`, `focus-visible:ring-2 focus-visible:ring-violet-500`. Fully compliant. Best a11y implementation in the sampled set.

### [ProgressBar.tsx](../../src/SoftMedia.Client/src/components/player/ProgressBar.tsx) — FAIL

The entire component is a plain `<div>` with mouse handlers. Grep for `role`, `tabIndex`, `aria-label`, `focus-visible`, `onKeyDown` across the file returns **zero matches**. Specific violations:

- SDD §8.3 rule 1: No `role="slider"` or `role="progressbar"`.
- SDD §8.3 rule 2: No `focus-visible:ring-*`.
- SDD §8.3 rule 3: Keyboard users cannot reach or operate the seek control.
- SDD §8.3 rule 4: The track div (`line 143`) is `h-1.5` (6px) expanding to `h-2.5` (10px) on hover — far below the 44px touch target. The scrubber ball (`line 201`) is `w-4 h-4` (16px) — also below minimum.

The seek control is the most critical interactive surface for video. This failure is especially significant given the simultaneous WebOS/TV target.

### [VideoPlayer.tsx](../../src/SoftMedia.Client/src/components/player/VideoPlayer.tsx) — PARTIAL FAIL

Most control-bar buttons (`lines 1479-1610`) use `<button>` but lack `aria-label`. Affected: Previous Episode, Previous Chapter, Skip Back, Play/Pause (line 1517), Skip Forward, Mute, Subtitle/Audio. None carry `focus-visible:ring-*`. `title` attributes are not screen-reader substitutes for `aria-label` and are invisible to TV remotes.

### [CreateUserModal.tsx](../../src/SoftMedia.Client/src/components/admin/CreateUserModal.tsx) — PASS

Both action buttons (`lines 106-119`) use `<button type>` with `focus-visible:ring-2`. Cancel button height is marginally below 44px but acceptable for an admin-only desktop modal. No SDD §8.3 violations.

---

## New Issues Not in the Original Report

### NEW-1 (HIGH): Authentication bypass on frame-preview endpoint

[TranscodeController.cs:241-272](../../src/SoftMedia.Server/Controllers/TranscodeController.cs#L241-L272)

The `GetFramePreview` action accepts `?token=` as a query parameter and validates it with `JwtSecurityTokenHandler.ReadJwtToken` (`line 251`). `ReadJwtToken` is a **decode-only** method — it does not verify the HMAC signature, expiry, issuer, or audience. The `[Authorize]` attribute is absent from this action.

An unauthenticated attacker can construct a syntactically valid JWT with any `sub` claim (e.g., using jwt.io), pass it as `?token=` to `GET /api/transcode/{id}/frame?time=0&token=<forged>`, and receive a video frame for any `MediaItem.Id`.

**Fix:** add `[Authorize]` to the action and remove the bespoke token-reading block. The `OnMessageReceived` hook in [ServiceCollectionExtensions.cs:44-65](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L44-L65) already lifts `?token=` for `/api/transcode/*` paths, so the standard JWT bearer middleware will validate the token correctly once `[Authorize]` is present.

### NEW-2 (MEDIUM): Exception messages returned to clients

- [TranscodeController.cs:128](../../src/SoftMedia.Server/Controllers/TranscodeController.cs#L128): `StatusCode(500, $"Transcoding error: {ex.Message}")`
- [TranscodeController.cs:237](../../src/SoftMedia.Server/Controllers/TranscodeController.cs#L237): `StatusCode(500, new { error = ex.Message })`
- [TranscodeController.cs:270](../../src/SoftMedia.Server/Controllers/TranscodeController.cs#L270): `StatusCode(500, ex.Message)` (frame-preview)
- [AudioStreamController.cs:66](../../src/SoftMedia.Server/Controllers/AudioStreamController.cs#L66): `NotFound(ex.Message)`

The `catch (Exception ex)` blocks in `TranscodeController` catch all exception types. Framework exceptions include internal filesystem paths in their messages (e.g., *"Could not find file '/home/user/transcode-temp/…'"*), leaking directory structure to authenticated API consumers. Server-side logging is already present; replace `ex.Message` with a generic string in the 500 responses.

`LibrariesController.cs:55,74` catches `ArgumentException` specifically — those messages are developer-controlled and not a concern.

### NEW-3 (MEDIUM): `Cors:AllowAnyOriginForLAN = true` ships in production config

[appsettings.json:17](../../src/SoftMedia.Server/appsettings.json#L17): `"AllowAnyOriginForLAN": true`

When this flag is true, [Program.cs:60-65](../../src/SoftMedia.Server/Program.cs#L60-L65) configures CORS as `SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials()` — a full wildcard accepting credentialed cross-origin requests from any origin, regardless of whether the server is LAN-only or internet-facing.

This is the default that ships to end users. A user running with Method B (DuckDNS + Caddy, publicly accessible) with this default allows any webpage they visit to make credentialed API calls to their SoftMedia server.

**Fix:** default to `false` in `appsettings.json`; override to `true` in `appsettings.Development.json` for the Vite proxy use case.

### NEW-4 (LOW): Schema instability — eight settings migrations on one day

Eight migrations on 2026-01-14 reorganize settings keys: `MoveSettingsToTranscoding`, `ReorganizeSettingsV3`, `MoveToneMappingToStreaming`, `MoveDirectPlayToTranscoding`, `RenameDisableTranscoding`, `RemoveOrphanedDisableTranscoding`, `MoveSettingsToScanning`, plus the earlier `CleanupOrphanedSettings` (2026-01-13). Consistent with the §5.4 finding (generic KV bucket, no schema validation). Worth flagging for release-readiness: the settings schema is unstable until a typed settings tree is implemented.

---

## Disagreements With the Original Report

### §5.5 (Symlink LFI) severity is understated

The original report says "reviewer should evaluate." The mechanism is confirmed, the fix is a one-line change, and the SDD explicitly targets Linux. This should be rated **High on Linux deployments** and scheduled with the same priority as the auth bypass found in NEW-1.

### DbContext thread safety is not an issue

All singleton background services use `IServiceScopeFactory.CreateScope()` correctly: `HeroCacheWorker.cs:48`, `ImageDownloadQueueService.cs:79`, `RefreshTokenCleanupService.cs:65`, `MetadataRefreshService.cs:31,45,173,181`, `LibraryScanQueueService.cs:288`. Each scope creates a fresh `AppDbContext` per work unit. Concern raised in the prompt does not materialize.

### N+1 queries are not present

`MediaRepository.cs` uses `Include`/`ThenInclude` for eager loading. `LibraryRepository.cs` uses LINQ joins translated by EF Core to single SQL statements. No loop-driven lazy loading was found.

### The §3.5 rate-limit comment mismatch is bigger than reported

The original caught `PermitLimit = 30` vs the "15" in the comment. It missed that the same comment block describes the design rationale for a sliding-window policy while the code uses a fixed-window policy ([ServiceCollectionExtensions.cs:232](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L232)). The comment is doubly incorrect.

---

## Updated priority order (combining both reports)

1. **NEW-1: Patch the frame-preview auth bypass.** `[Authorize]` + remove the bespoke `ReadJwtToken` block. Highest urgency.
2. **§3.1: Implement parental-control filtering.** Authorization handler reading the `MaxRating` JWT claim, wired into `MediaRepository`/`StreamController`/`BookController`/`MusicController`.
3. **§5.5 / Deeper: Resolve symlinks in `StreamSecurityService.IsPathAuthorized`.** One-line change closes a Linux LFI.
4. **NEW-3: Default `AllowAnyOriginForLAN` to false.** Move the dev override into `appsettings.Development.json`.
5. **§3.2: Add HLS segment janitor `IHostedService`.** Walks `transcode-temp`, evicts segments from sessions closed > N minutes.
6. **NEW-2: Replace `ex.Message` with generic strings in 500 responses.** Keep the existing server-side `LogError`.
7. **JWT TTL: change `appsettings.json` `ExpiryMinutes` from 60 to 15.** Zero-friction.
8. **`ProgressBar.tsx`: add slider semantics and keyboard handler.** Highest-impact a11y fix in the codebase.
9. **`VideoPlayer.tsx`: add `aria-label` to all control buttons.** Required for WebOS/TV compliance.
10. **§5.1 / hygiene: clean stale log files** out of `src/SoftMedia.Server.Tests/`, gitignore patterns.
11. **§3.4: Reconcile SameSite=Lax with the SDD** — either restore Strict (with a dev shim) or update SDD §4.2.
12. **§3.5: Fix the rate-limit comment** to match the code (or vice versa).

*End of peer review.*
