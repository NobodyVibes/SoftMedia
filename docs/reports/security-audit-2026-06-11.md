# SoftMedia Security Audit — 2026-06-11 (Wave 2)

**Scope:** Whole repository — `src/SoftMedia.Server` (.NET 8 / ASP.NET Core backend, SQLite/EF Core) and `src/SoftMedia.Client` (React 19 + TypeScript SPA), on branch `security/hardening-wave-1`. 395 server source files (~68k LOC) plus the SPA.

**Method:** Multi-agent source review. 27 domain finders ran in parallel — 12 re-verifying the *wave-1 hardening remediations* (commit `04a6988`) for bypasses/incompleteness, and 15 hunting new vulnerabilities — followed by an adversarial skeptic panel that re-read the cited code to confirm or refute each candidate (3 verifiers for High/Critical, 2 for Medium, 1 for Low/Info). 151 agents total. The headline findings and every wave-1 fix were additionally re-read by hand (auth controller, token service, the JWT pipeline, ffmpeg builders, path-jail, SignalR hub, backup service, DI/pipeline setup).

> **Verification caveat (read this).** The audit account hit its session limit partway through the verification phase, so ~39 candidate findings — concentrated in the **DoS, SignalR/transport, scanning, frontend, dependency-CVE, backup, and parts of the crypto/SSRF/admin** domains — did **not** receive their automated skeptic panel. Where those findings were security-relevant, **I verified them myself by re-reading the source** and they are marked accordingly below. The remainder are flagged ⚠️ *reported-unverified* and should be confirmed before action. This means the Low/Info tables are likely **non-exhaustive** for those domains.

**Threat model (unchanged from wave 1):** A free, open-source, **self-hosted at-home media server** (à la Plex/Jellyfin), commonly exposed to the internet via a reverse proxy + dynamic DNS per its own docs, sometimes LAN-only. Attackers considered: (1) unauthenticated internet, (2) authenticated low-privilege user (open or invite signup), (3) cross-user access between family members / housemates (IDOR/BOLA), (4) malicious media **files** and embedded metadata dropped into a watched library, (5) malicious **upstream** metadata responses, (6) other devices on the LAN (DLNA/SSDP). Admin is the highest web privilege. Severities are calibrated to *this* model — not an enterprise/multi-tenant one.

---

## Executive summary

**The wave-1 hardening is real and, in the main, well-built.** Every Critical/High from the 2026-06-07 audit that I could re-test holds up:

- **C1 (default admin):** genuinely remediated. A random 144-bit password is generated per install; a legacy `admin123` row is actively *re-hashed* to a random value (not merely re-flagged); and `MustChangePassword` is enforced by a **deny-by-default pipeline gate** ([Program.cs:237-258](../../src/SoftMedia.Server/Program.cs#L237-L258)) that 403s every route except change-password/logout/refresh for a flagged principal. I could not bypass it (forged claim blocked by the strong-secret boot validator; cast/media/API-token minting endpoints are all behind the gate).
- **H1/M4/L3 (ACL routing):** `MediaTracksController`, `MediaController.GetMediaItem`, `MusicController`, `AudioController`, `TrickplayController`, `BookController` now route through the ACL-aware repository + `StreamSecurityService.ValidateMediaAccessAsync` and 404 on denial.
- **H2/M2 (ffmpeg arg-injection):** **no live RCE.** `AudioStreamController` was migrated to `ProcessStartInfo.ArgumentList`, and the compensating control `MediaPathSafety` (rejects `"`/control chars) is correctly placed on **both** the scan boundary ([BaseMediaScanner.CanHandleFile](../../src/SoftMedia.Server/Services/Scanning/BaseMediaScanner.cs#L339-L352)) and the stream boundary ([StreamSecurityService.IsPathAuthorized](../../src/SoftMedia.Server/Services/Security/StreamSecurityService.cs#L27-L34)).
- **M5/M6 (webhook SSRF + rebind):** default-deny of private/link-local/loopback plus **validated-IP pinning** via `SocketsHttpHandler.ConnectCallback` is sound for the standard cases.
- **Token design:** the JWT validator is symmetric-key-only (HS/RS algorithm confusion structurally unreachable); cast tokens are single-media-scoped with a live user-state recheck every request; the new H3 "media" token omits the role claim and is route-confined in `OnTokenValidated`. Login is constant-time (dummy Argon2 verify); invites consume atomically; signup honours the approval gate.

**Residual risk concentrates in a familiar pattern and a few new gaps.** The recurring theme from wave 1 — *enforcement applied at most entry points but not re-applied at every one* — still produces findings: one more list endpoint (`/libraries/{id}/recent`) bypasses the ACL entirely, and several read paths apply the library ACL but **drop the content-rating ceiling** (collections, watchlist). The genuinely new issues are: an **image decode-bomb (pixel-flood) DoS**, **admin password reset not revoking sessions**, the **H3 token-in-URL fix being only half-closed**, **intermediate-directory symlinks escaping the path-jail**, and **backups bundling the JWT signing key**. Plus one external item: the **ASP.NET Core runtime should be patched to ≥ 8.0.21** (CVE-2025-55315).

### Findings tally (post-verification)

| Severity | Count | Notes |
|---|---:|---|
| **Critical** | 0 | — |
| **High** | 4 | 1 external (runtime CVE) |
| **Medium** | 8 | |
| **Low** | ~22 | non-exhaustive in unverified domains |
| **Info / hardening** | ~16 | incl. 11 confirmations that wave-1 fixes are sound |
| **Refuted** | 7 | documented so they aren't re-raised |

### Two systemic themes (carried over from wave 1)

1. **Library ACL is re-checked broadly; the content-rating ceiling is not.** Several endpoints correctly call `ApplyLibraryAccessFilter` but omit `ApplyContentRatingFilter`, so a *rating-restricted* user (e.g. a child with library access but a PG ceiling) still sees over-rating metadata. A single combined `ApplyAccess()` helper used everywhere would close this class.
2. **State changes don't propagate to already-issued credentials.** `ChangePassword` revokes refresh tokens + trusted devices, but admin password-reset, ban, delete, and un-approve do **not** — and the 120-minute media token isn't re-validated against live user state. Revocation should be centralised and called from every account-state mutation.

---

## High

### H-1 — `GET /api/v1/libraries/{id}/recent` serves the recently-added cache with **no** library ACL or rating filter (cross-library metadata + on-disk path leak)
*authz · authenticated low-priv · panel 3/3 · [LibrariesController.cs:130-135](../../src/SoftMedia.Server/Controllers/LibrariesController.cs#L130-L135), [LibraryService.cs:415-471](../../src/SoftMedia.Server/Services/Media/LibraryService.cs#L415-L471)*

`LibraryService.GetRecentlyAddedAsync` reads a pre-built "recently added" cache and returns it without consulting `IUserLibraryAccessProvider` or the content-rating ceiling. A low-privilege user who is **denied** a library (or capped below its rating) can request its recent items and receive full `MediaItemDto` metadata — overview, cast, genres — **including `MediaItem.Path`** (the on-disk file path, serialized at [MediaItemDto.cs:138](../../src/SoftMedia.Server/DTOs/MediaItemDto.cs#L138)). This is the same root cause as wave-1 H1/M4, on an endpoint the prior audit didn't cover, and it leaks the filesystem layout on top of the metadata.

**Fix:** gate `GetRecentlyAddedAsync` on the caller's library access before reading the cache (or 404 via the ACL-aware `_libraryRepository.ExistsAsync(id)`); re-apply the rating ceiling per request (or key the cache by access-class); and stop serializing `MediaItem.Path` into user-facing DTOs.

### H-2 — Admin password reset does not revoke refresh tokens / trusted devices (account-takeover persistence)
*authn · admin remediation ineffective · panel 3/3 · [UsersController.cs:336-376](../../src/SoftMedia.Server/Controllers/UsersController.cs#L336-L376)*

The self-service `ChangePassword` flow correctly revokes all refresh tokens ([AuthController.cs:326](../../src/SoftMedia.Server/Controllers/AuthController.cs#L326)) and trusted 2FA devices on a password change. The **admin** `ResetUserPassword` path does neither. So the canonical "an account is compromised → admin resets its password" incident response **fails to evict the attacker**: their refresh-token chain keeps minting fresh 15-minute access tokens, and a remembered-device cookie keeps skipping 2FA, for up to the refresh lifetime (7 days).

**Fix:** after updating the hash in `ResetUserPassword`, call `RevokeAllForUserAsync(user.Id, …PasswordChange)` and `_trustedDevices.RevokeAllAsync(user.Id)`, mirroring the self-service flow. (See also L-15: ban/delete/deny have the same omission.)

### H-3 — Image decode-bomb (pixel-flood) in the SkiaSharp thumbnail / cover-art paths → OOM DoS of the home server
*dos · malicious file + authenticated · panel 3/3 · [ThumbnailService.cs:62](../../src/SoftMedia.Server/Services/Media/ThumbnailService.cs#L62), [ComicPageThumbnailService.cs:87](../../src/SoftMedia.Server/Services/Media/ComicPageThumbnailService.cs#L87), [MusicScanner.cs:355](../../src/SoftMedia.Server/Services/Scanning/MusicScanner.cs#L355), [ImageController.cs:279](../../src/SoftMedia.Server/Controllers/ImageController.cs#L279)*

Several paths hand an image straight to SkiaSharp (`SKBitmap.Decode` / `SKImage`) with **no decoded-dimension cap**. A small file declaring enormous pixel dimensions (e.g. a 64000×64000 PNG, a few KB compressed) forces a multi-gigabyte raw-pixel allocation and OOM-kills the process. Reachable three ways, including **without any authenticated request**: scan-time embedded-art extraction (`MusicScanner`) and the image-proxy cache run automatically on files/URLs the server ingests; plus the authenticated comic-thumbnail and image endpoints. This is the missing *pixel-budget* analogue of the wave-1 L4/L5 byte caps.

**Fix:** before decoding, read header dimensions only (`SKCodec.Create`) and reject anything over a sane budget (~50 MPixel) or a hard W/H limit (e.g. 16384). For resizes, use a sampled/downscaled decode so the full-resolution buffer is never allocated. Apply the same guard in `MusicScanner` before persisting embedded art and in the image-proxy cache before caching.

### H-4 — ASP.NET Core runtime: patch to ≥ 8.0.21 for CVE-2025-55315 (request smuggling, CVSS 9.9)
*dependency · external · author-verified (MSRC) · [SoftMedia.Server.csproj](../../src/SoftMedia.Server/SoftMedia.Server.csproj)*

The project pins ASP.NET Core packages at `8.0.2`, and the corresponding Kestrel runtime range (8.0.0–8.0.20) is affected by **CVE-2025-55315** — an HTTP request/response-smuggling flaw in Kestrel's chunk-extension handling, rated **CVSS 9.9**, fixed in **.NET 8.0.21**. Because SoftMedia is documented to run **behind a reverse proxy** (the exact front-end/back-end interpretation-mismatch scenario request smuggling exploits), this is directly in-scope and can enable request-routing bypass, CSRF-guard bypass, and SSRF-style effects.

> **Accuracy note:** the *effective* Kestrel version is the **deployed .NET 8 runtime** (the `Microsoft.AspNetCore.App` shared framework), not just the NuGet pins — a host already on 8.0.21+ is patched regardless of the csproj. Treat this as "ensure build + deploy use ≥ 8.0.21," and wire `dotnet list package --vulnerable` into CI (see I-16).

**Fix:** bump the .NET 8 SDK/runtime and the `8.0.2` package pins to the latest `8.0.x` patch (≥ 8.0.21). I did **not** independently confirm the agent's other dependency CVE claims (several carried implausible `CVE-2026-*` identifiers) — verify those with `dotnet list package --vulnerable` and `npm audit` rather than the IDs cited.

---

## Medium

| ID | Finding | Location | Verify |
|---|---|---|---|
| **M-1** | **Collections endpoints omit the content-rating ceiling.** Library ACL is applied but `ApplyContentRatingFilter` is not, so a rating-restricted user sees over-rating movie metadata via collection membership (and hidden items still bump a collection over the ≥2-visible display threshold). | [CollectionsController.cs:100-186](../../src/SoftMedia.Server/Controllers/CollectionsController.cs#L100-L186) | panel 2/2 |
| **M-2** | **H3 only half-closed: the full access JWT is still accepted in `?token=`/`?access_token=`**, and the SPA still emits it on a cold page load before the media token resolves. `OnMessageReceived` lifts any query token without requiring `token_use=media`. Referer leakage *is* fixed (global `Referrer-Policy: no-referrer`), but proxy-access-logs and browser history still capture the full-privilege token. | [ServiceCollectionExtensions.cs:95-133](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L95-L133), [mediaImageUrl.ts:22-32](../../src/SoftMedia.Client/src/lib/mediaImageUrl.ts#L22-L32) | panel 2/2 |
| **M-3** | **TOTP lockout TOCTOU.** The per-user lockout (wave-1 M3) checks `IsLockedOut` and `RegisterFailedAttempt` non-atomically, so N parallel `/auth/2fa` guesses for one challenge all pass the check before any increments land — defeating the bound on a 10⁶ keyspace. | [AuthController.cs:229-243](../../src/SoftMedia.Server/Controllers/AuthController.cs#L229-L243), [TotpService.cs:158-174](../../src/SoftMedia.Server/Services/Identity/TotpService.cs#L158-L174) | panel 2/2 |
| **M-4** | **Transcode `?sid=` is never validated → path traversal + unbounded segments.** The client-supplied session id flows into the on-disk ffmpeg output directory with no charset/length check, allowing directory traversal out of the temp root and accumulation of segment folders **outside** the cleanup scope. | [TranscodeController.cs:136-152](../../src/SoftMedia.Server/Controllers/TranscodeController.cs#L136-L152), [TranscodeProfileBuilder.cs:64](../../src/SoftMedia.Server/Services/Transcoding/TranscodeProfileBuilder.cs#L64) | panel 2/2 |
| **M-5** | **Per-user transcode cap is race-bypassable.** The count-then-start check (wave-1 M9) isn't atomic with session registration, so a burst of requests with distinct `?sid=` each clears the cap → CPU/disk exhaustion (each spawns an independent ffmpeg). | [TranscodeService.cs:201-235](../../src/SoftMedia.Server/Services/Transcoding/TranscodeService.cs#L201-L235) | panel 2/2 |
| **M-6** | **Per-rating DLNA ceiling deferred.** DLNA now has a library allow-set (M7) and is AV-only (L9), but with no rating filter: any LAN device can browse/stream **restricted-rating** titles inside an exposed library, silently defeating parental controls. Opt-in + LAN-only mitigates. | [DlnaContentDirectory.cs:98-192](../../src/SoftMedia.Server/Services/Dlna/DlnaContentDirectory.cs#L98-L192), [DlnaController.cs:134-165](../../src/SoftMedia.Server/Controllers/DlnaController.cs#L134-L165) | panel 2/2 |
| **M-7** | **Intermediate-directory symlinks escape the path-jail (LFI / cross-library read).** `StreamSecurityService.ResolveRealPath` resolves only the **leaf** with `ResolveLinkTarget`; `Path.GetFullPath` does not canonicalise a symlinked *parent* directory. A symlink dropped *inside* a watched library (by anyone who can write to the media folder) pointing at `/etc` resolves at serve time and passes the `StartsWith(root)` check. | [StreamSecurityService.cs:72-92](../../src/SoftMedia.Server/Services/Security/StreamSecurityService.cs#L72-L92) | panel 1/1 + author |
| **M-8** | **Backups bundle the JWT signing key alongside the full DB.** `appsettings.json` is embedded verbatim in every backup zip next to `softmedia.db`. If the JWT secret lives in appsettings (the obvious place; it is *also* the root of the TOTP-secret AES key), one leaked backup = total auth compromise — on top of the Argon2 hashes, encrypted TOTP secrets, and recovery-code hashes already in the DB. Worsened by M-9/L-19. | [BackupService.cs:80-96](../../src/SoftMedia.Server/Services/Infrastructure/BackupService.cs#L80-L96) | author-verified |

---

## Low

> Verified by the skeptic panel unless marked **(author)** = I confirmed it from source after its panel was lost to the session limit, or **(⚠ unverified)** = reported by a finder but not re-checked.

| ID | Finding | Location |
|---|---|---|
| L-1 | **Watchlist listing omits the content-rating ceiling** (library ACL applied, rating not). | [WatchlistController.cs:72-94](../../src/SoftMedia.Server/Controllers/WatchlistController.cs#L72-L94) |
| L-2 | **`PlaylistsController.AddItems` queries `MediaItems` without the library ACL** — a restricted user can attach denied-library tracks to their own playlist (metadata leak via playlist render). | [PlaylistsController.cs:212-240](../../src/SoftMedia.Server/Controllers/PlaylistsController.cs#L212-L240) |
| L-3 | **Media token (H3) not re-validated against live user state** — a banned/deleted/un-approved user keeps media/stream access for up to the token lifetime (120 min). Cast tokens *do* re-check; media tokens don't. | [ServiceCollectionExtensions.cs:121-126](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L121-L126) |
| L-4 | **Media token is session-scoped, not item-scoped** — one leaked media URL replays across the user's entire accessible library for 120 min (broad route surface incl. `/api/v1/books`, `/api/v1/image`). | [TokenService.cs:96-113](../../src/SoftMedia.Server/Services/Identity/TokenService.cs#L96-L113) |
| L-5 | **Media token accepted on state-mutating `BookController` endpoints** (bookmark/highlight writes under `/api/v1/books`) — a streaming token should be read/stream-only. | [BookController.cs:203-362](../../src/SoftMedia.Server/Controllers/BookController.cs#L203-L362) |
| L-6 | **Ban / delete / un-approve do not revoke refresh tokens or trusted devices** (same class as H-2, write-side). | [UsersController.cs:164-270](../../src/SoftMedia.Server/Controllers/UsersController.cs#L164-L270) |
| L-7 | **Password policy not enforced on admin `CreateUser`** — empty/1-char passwords accepted for family accounts (signup & reset are guarded; create is not). | [UsersController.cs:61-87](../../src/SoftMedia.Server/Controllers/UsersController.cs#L61-L87) |
| L-8 | **2FA-disable endpoint has no per-user lockout** (IP-only, re-armable) — TOTP brute-force bound (M3) isn't applied on the disable path. | [AccountController.cs](../../src/SoftMedia.Server/Controllers/AccountController.cs) |
| L-9 | **TOTP lockout fully resets every 15 min** — no escalating backoff, enabling indefinite slow brute force. | [TotpService.cs:158-174](../../src/SoftMedia.Server/Services/Identity/TotpService.cs#L158-L174) |
| L-10 | **TOTP recovery codes: ~40-bit entropy, unsalted single-round SHA-256.** Offline-crackable from a DB/backup leak, bypassing the 2FA second factor. | [TotpService.cs:114-130](../../src/SoftMedia.Server/Services/Identity/TotpService.cs#L114-L130) |
| L-11 | **Webhook SSRF: the unspecified address (`0.0.0.0`/`::`/`::ffff:0.0.0.0`) classifies as PUBLIC** — a hostile DNS record reaching `0.0.0.0` hits loopback on Linux, bypassing `AllowLoopback=false`. | [NetworkClassifier.cs:17-49](../../src/SoftMedia.Server/Services/Infrastructure/NetworkClassifier.cs#L17-L49) |
| L-12 | **NetworkClassifier omits CGNAT `100.64.0.0/10`** (Tailscale/ISP) and other special-use ranges — they pass the SSRF guard as "public." | [NetworkClassifier.cs:23-46](../../src/SoftMedia.Server/Services/Infrastructure/NetworkClassifier.cs#L23-L46) |
| L-13 | **`MusicImageService` cover-art jail uses `GetFullPath`-only (no symlink resolution) + prefix check without a trailing separator** — diverges from the hardened central guard (`AudioController` already delegates correctly). | [MusicImageService.cs:167-211](../../src/SoftMedia.Server/Services/Media/MusicImageService.cs#L167-L211) |
| L-14 | **Transcode caps have no hard ceiling** — a config value of `0` disables them entirely (unlimited). Ship a non-zero floor independent of config. | [TranscodeService.cs](../../src/SoftMedia.Server/Services/Transcoding/TranscodeService.cs) |
| L-15 | **DLNA `Browse RequestedCount=0` maps to `Take(int.MaxValue)`** — an unclamped list over `MediaItems` (LAN-reachable when DLNA is enabled). | [DlnaContentDirectory.cs](../../src/SoftMedia.Server/Services/Dlna/DlnaContentDirectory.cs) |
| L-16 | **CBZ/CBR entry-count DoS** — `GetOrderedImageEntries` enumerates + natural-sorts every archive entry with no count cap (~6 s CPU + ~170 MB per ~49 MB request). | [ComicArchiveService.cs:224-365](../../src/SoftMedia.Server/Services/Media/ComicArchiveService.cs#L224-L365) |
| L-17 | **Comic page/thumbnail `MemoryCache` has no `SizeLimit`** — the L4 64 MB per-page cap doesn't bound aggregate memory; cached pages accumulate to GBs. | [ComicPageThumbnailService.cs](../../src/SoftMedia.Server/Services/Media/ComicPageThumbnailService.cs) |
| L-18 | **`ScanProgress` is broadcast via `Clients.All`** while every other notification is group-scoped — every authenticated user (incl. ACL-restricted) receives the GUIDs + item counts of all libraries being scanned. **(author)** | [MediaNotificationService.cs:143-146](../../src/SoftMedia.Server/Services/Media/MediaNotificationService.cs#L143-L146) |
| L-19 | **`UseRateLimiter` runs before `UseAuthentication`** — the image-proxy "per-user" partition reads `User` before it's populated, so it always falls back to client IP (per-user fairness/limit silently degraded). **(author — confirmed in [Program.cs:226-229](../../src/SoftMedia.Server/Program.cs#L226-L229))** | [Program.cs:226-230](../../src/SoftMedia.Server/Program.cs#L226-L230) |
| L-20 | **Admin-configurable `Maintenance.BackupDirectory` has no `wwwroot` guard** — pointing it inside the served static root makes secret-bearing backups anonymously web-downloadable; restore extraction also has no uncompressed-size cap (admin-triggered disk-fill). **(author)** | [BackupService.cs:309-313](../../src/SoftMedia.Server/Services/Infrastructure/BackupService.cs#L309-L313) |
| L-21 | **Frame-preview endpoint spawns ffmpeg per request, outside the transcode session manager** — not counted against any concurrency cap. **(author — [TranscodeController.cs:279-299](../../src/SoftMedia.Server/Controllers/TranscodeController.cs#L279-L299))** ⚠ ACL-on-this-path needs confirming in `VideoPreviewService`. | [TranscodeController.cs:279-299](../../src/SoftMedia.Server/Controllers/TranscodeController.cs#L279-L299) |
| L-22 | **Library scan follows directory symlinks with no cycle/depth limit** (`Directory.EnumerateDirectories(…, SearchOption.AllDirectories)` follows reparse points) → unbounded enumeration + out-of-library ingestion; pairs with M-7. **(author — [BaseMediaScanner.cs:246-279](../../src/SoftMedia.Server/Services/Scanning/BaseMediaScanner.cs#L246-L279))** | [BaseMediaScanner.cs:246-279](../../src/SoftMedia.Server/Services/Scanning/BaseMediaScanner.cs#L246-L279) |
| L-23 | **SignalR `MediaHub` JoinLibrary/JoinMedia are unthrottled** and issue 1–2 SQLite queries each — floodable connection/DB pressure. **(⚠ unverified)** | [MediaHub.cs:30-146](../../src/SoftMedia.Server/Hubs/MediaHub.cs#L30-L146) |
| L-24 | **`MediaHub` ACL is checked only at group-join time** — revoking a user's library access does not evict an existing SignalR group membership, so live notifications keep flowing until reconnect. **(author)** | [MediaHub.cs:54-60](../../src/SoftMedia.Server/Hubs/MediaHub.cs#L54-L60) |
| L-25 | **`KeyedLock` and `TranscodeSessionManager` session-locks are never evicted** (wave-1 I2 deferral) — now attacker-reachable via distinct `?sid=` values (compounds M-4/M-5). **(⚠ unverified)** | [KeyedLock.cs](../../src/SoftMedia.Server/Helpers/KeyedLock.cs), [TranscodeSessionManager.cs](../../src/SoftMedia.Server/Services/Transcoding/TranscodeSessionManager.cs) |
| L-26 | **Image host allowlist admits all `*.archive.org`** including `web.archive.org` (the Wayback content-rewriting fetcher) — a fetch-anything-via-Wayback hop. **(⚠ unverified)** | [ImageCacheService.cs](../../src/SoftMedia.Server/Services/Media/ImageCacheService.cs) |
| L-27 | **`axios 1.13.2` (SPA)** — re-confirm against current advisories with `npm audit`; update if below the fix line. The finder's specific CVE IDs were not independently verifiable. **(⚠ unverified)** | [package.json](../../src/SoftMedia.Client/package.json) |

---

## Info / hardening

**New hardening notes**
- **I-1 — No Content-Security-Policy is shipped** (wave-1 deferral). The access token persists in `localStorage` (zustand `persist`); the *only* compensating control is the current absence of an XSS sink in the SPA. A CSP (`default-src 'self'`, no inline) plus moving the token in-memory would harden this materially — especially given the SPA renders untrusted EPUB (epub.js) and PDF (react-pdf) content.
- **I-2 — Signup is a username-enumeration oracle** — an existing username returns 400 "Username already exists" while a free name returns 202 pending. Low impact for a home server; align responses if desired. [AuthController.cs:82-85](../../src/SoftMedia.Server/Controllers/AuthController.cs#L82-L85)
- **I-3 — `MustChangePassword` is enforced by a single JWT-derived claim** absent from the API-token/cast/media auth paths. Not currently exploitable (those tokens can't be minted by a flagged user), but a latent single-point-of-enforcement; consider a DB-backed check for defense-in-depth. [Program.cs:237-258](../../src/SoftMedia.Server/Program.cs#L237-L258)
- **I-4 — TOTP enroll/confirm endpoints are omitted from the wave-1 rate-limit set** (L12 partially complete). [AccountController.cs](../../src/SoftMedia.Server/Controllers/AccountController.cs)
- **I-5 — Refresh-token rotation has no concurrency guard** — two simultaneous refreshes of one token can fork it into multiple live chains (reuse-detection won't fire). [RefreshTokenService.cs](../../src/SoftMedia.Server/Services/Identity/RefreshTokenService.cs)
- **I-6 — `MediaTracksController` carries a private hardcoded ffmpeg/ffprobe path resolver** that bypasses `BinaryLocationService`/`FFmpeg:Path` config (no injection — paths are MediaPathSafety-guarded; portability/consistency only). **(author)** [MediaTracksController.cs:338-376](../../src/SoftMedia.Server/Controllers/MediaTracksController.cs#L338-L376)
- **I-7 — Webhook `ConnectCallback` silently re-resolves DNS when the pinned-IP option is absent** — sound today (the worker always pins) but a latent rebind footgun if the call path changes. [ServiceCollectionExtensions.cs:261-280](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L261-L280)
- **I-8 — Image proxy initial URL is host-checked but not scheme-checked** (redirects are) — relies on `HttpClient` rejecting non-`http(s)` schemes. [ImageCacheService.cs](../../src/SoftMedia.Server/Services/Media/ImageCacheService.cs)
- **I-9 — Full ArgumentList migration still deferred** — 7 ffmpeg/ffprobe builders interpolate the path; `MediaPathSafety` (a single-character `"` denylist) is the sole control. Sound today but fragile; finish the `ArgumentList` migration so it's defense-in-depth, not the only line. [MediaProbeService.cs:39](../../src/SoftMedia.Server/Services/Media/MediaProbeService.cs#L39)
- **I-16 — No automated dependency-vulnerability scanning** is wired into the repo. Add `dotnet list package --vulnerable --include-transitive` and `npm audit` to CI (ties to H-4/L-27).

**Confirmations — wave-1 fixes verified sound (documented so they aren't re-litigated)**
- **I-10** C1 default-admin remediation (random password, live `admin123` rotation, deny-by-default `MustChangePassword` gate) — verified non-bypassable.
- **I-11** H1/M4/L3 ACL routing on `MediaTracksController`/`MediaController`/`MusicController`/`AudioController`/`TrickplayController`/`BookController`.
- **I-12** M2 `AudioStreamController` `ArgumentList` migration; MediaPathSafety on every input-path sink (scan + stream).
- **I-13** M5/M6 webhook SSRF default-deny + validated-IP pinning; M1 signup-approval gate; M3 per-user TOTP lockout core; L1 constant-time login; L11 atomic invite consumption.
- **I-14** L4/L5 decompression-bomb caps (ComicInfo.xml, EPUB OPF/container — streaming early-abort); NFO XML parser is XXE-safe.
- **I-15** L7/L8 baseline security headers (`nosniff`, `X-Frame-Options`, `Referrer-Policy: no-referrer`) + HSTS, proxy-aware, covering API + static SPA + error responses; M7/L9 DLNA library allow-set + AV-only.

---

## Candidates investigated and **refuted** (false positives)

The skeptic panel killed these — included so they aren't re-raised:

1. **Frame-preview LFI / no path validation** (rv-ffmpeg-args-2) — the frame path *does* validate the media item through the access layer; refuted. (Its DoS/no-cap angle survives as **L-21**.)
2. **CORS reflect-any-origin in production** — credentialed wildcard is correctly confined to `Development`; production falls back to the explicit allowlist. ([Program.cs:107-142](../../src/SoftMedia.Server/Program.cs#L107-L142))
3. **`InteractionController` ACL bypass** (rate/favorite/mark-watched on denied media) — refuted on the cited path.
4. **Backup-restore zip-slip** — extraction uses fixed entry names to a fixed temp path and a traversal-guarded id; not slip-able. (Missing size cap survives as **L-20**.)
5. **Object-ownership re-checks** (playlists, webhooks, API tokens, trusted devices, bookmarks/highlights, reading sessions) — verified correctly enforced against the caller claim.
6. **`Trickplay`/`Dictionary` path traversal** — not traversable.
7. **NFO XML XXE / bomb** — `DtdProcessing.Prohibit` + bounded reads; safe.

---

## Prioritised remediation roadmap

1. **Now (High):**
   - **H-4** patch the .NET 8 runtime to ≥ 8.0.21 (one-line build/deploy change, highest CVSS).
   - **H-1** gate `/libraries/{id}/recent` on the ACL + rating ceiling and stop leaking `MediaItem.Path`.
   - **H-2** revoke sessions/devices on admin password-reset (and L-6: on ban/delete/deny) — centralise revocation.
   - **H-3** add a decoded-dimension cap before every SkiaSharp decode (thumbnail, comic, scan-time art, image proxy).
2. **Next (Medium):** M-1 (collections rating filter — fold into a shared `ApplyAccess()` helper with L-1/L-2), M-2 (reject query-string tokens carrying a Role claim; make the SPA hard-depend on the media token), M-3/M-5 (make the TOTP and transcode count-checks atomic), M-4 (validate `?sid=` charset/length + re-jail the output dir), M-6 (implement the per-rating DLNA ceiling), M-7 (fully canonicalise paths incl. parent symlinks; skip reparse points during scan), M-8 (don't bundle `appsettings.json`/secrets in backups — or encrypt the archive).
3. **Hardening (Low/Info):** the rating-ceiling sweep (L-1/L-2), media-token tightening (L-3/L-4/L-5), revocation-on-state-change (L-6), password policy on create (L-7), TOTP lockout completeness + recovery-code entropy/KDF (L-8/L-9/L-10), webhook classifier (L-11/L-12), the symlink/jail consistency (L-13, M-7), pipeline ordering (L-19), backup directory guard (L-20), `Clients.All` scoping (L-18), CSP (I-1), finish ArgumentList migration (I-9), and wire dependency scanning into CI (I-16).

---

## Methodology & limitations

- **Coverage:** authentication, authorization/IDOR, path traversal/LFI/zip-slip, command/argument injection, SSRF, untrusted parsing (XXE/zip/image/ReDoS), crypto/secrets, transport/web (CORS/CSRF/headers/SignalR), DLNA/SSDP, DoS/rate-limiting, backup/restore, scanning/watcher, dependencies, and the frontend. 27 finders → adversarial panel (3/2/1 verifiers by severity) → completeness critic. Findings here are the panel survivors plus author-verified items.
- **This is a source review, not a live pentest.** No exploits were run against a running instance. Confirm against your specific deployment.
- **Incomplete verification phase.** The audit account hit a session limit during verification; ~39 candidates (DoS/SignalR/scanning/frontend/deps/backup-heavy) lost their automated panel. Security-relevant ones were re-verified by hand and marked **(author)**; the rest are **⚠ unverified**. The completeness critic also did not run, so the Low/Info layer is not exhaustive in those domains — a re-run after the limit resets is recommended to close the gap.
- **Out of scope / not exhaustively covered:** dependency CVEs beyond the runtime (no full SCA run — H-4 verified, others flagged for `npm audit`/`dotnet list package --vulnerable`), EF migration history, business-logic abuse beyond access control, and host/physical security. EF Core is used with parameterised queries throughout — no SQL-injection surface found.
- Severities are calibrated for the **self-hosted-behind-reverse-proxy** threat model. LAN-only deployments may downgrade the unauthenticated-network and DLNA findings.
