# Security Hardening — Wave 2: Implementation Plan & Task List

**Source:** [docs/reports/security-audit-2026-06-11.md](../reports/security-audit-2026-06-11.md) (4 High, 8 Medium, ~22 Low, ~16 Info; wave-1 fixes re-verified sound).
**Branch:** suggest `security/hardening-wave-2` off `main` (wave-1 is merged at `04a6988`).
**Goal:** Close the residual gaps the second audit found — the same systemic patterns as wave 1 (enforcement applied at most but not all entry points; state changes not propagated to issued credentials) plus a handful of new issues — while preserving streaming/transcoding/auth behaviour.

## Implementation status

**Branch `security/hardening-wave-2`. All four High findings + every Medium except M-5 + the large majority of Lows/Infos remediated, each with regression tests.** Suite `SoftMedia.Server.Tests`: **818 passing, 1 skipped (pre-existing .cbr fixture), 0 failing** (a pre-existing flaky in-memory-SQLite teardown + scanner-timing test occasionally fails in the full parallel run but passes in isolation/retry — unrelated to these changes). SPA `tsc` clean; `npm audit --omit=dev` clean.

| WS | Status | Summary |
|----|--------|---------|
| WS‑0 | ✅ done | Confirmed ⚠ findings: real advisories (STJ/Caching.Memory High, SharpCompress Moderate, react-router/axios High) + a **new** frame-cache ACL bypass + lock-eviction/web.archive.org. |
| WS‑1 | ✅ done | H-4/I-16 — server package bumps + RarFactory API migration + `TargetLatestRuntimePatch`; react-router-dom 7.17, axios 1.17, `@xmldom/xmldom` 0.8.13 override; `.github/workflows/security.yml` SCA gate. |
| WS‑2 | ✅ done | H-1/M-1/L-1/L-2 — recent-cache ACL+rating gate + `MediaItemDto.Path`→filename; collections/watchlist rating ceiling; playlists AddItems ACL; frame-cache ACL fix. |
| WS‑3 | ✅ done | H-2/L-6 — revoke refresh tokens + trusted devices on admin reset/ban/deny/delete/un-approve. |
| WS‑4 | ✅ done | H-3 — `ImageSafety` header-only pixel-budget guard at both SkiaSharp decode sites. |
| WS‑5 | ✅ done | M-7/L-13/L-22 — full per-component symlink canonicalisation; scanner skips reparse points + depth bound; MusicImageService routed through the central jail; symlink tests fail-loud on POSIX. |
| WS‑6 | ◑ partial | T6.3 done — media token re-checks live user state (L-3). **T6.1/T6.2/T6.4 deferred** (reject role-bearing query tokens + SPA hard-depend + GET-only): need SPA migration + E2E (hub + cold-load media URLs ride the full access token in the query string). |
| WS‑7 | ◑ partial | M-4/L-14/L-25/**L-21** done — `sid` validation + path re-jail; finite hard transcode ceiling; lock eviction; frame-preview concurrency cap. **M-5** (full count-and-reserve atomicity) deferred — L-14's hard ceiling already bounds the race to a small finite overage; the full fix needs concurrency load-testing. |
| WS‑8 | ✅ done | M-6/L-15 — opt-in DLNA per-rating ceiling on browse + metadata + media-stream; Browse page-size clamp. |
| WS‑9 | ✅ done | M-8/L-20 — stop bundling appsettings.json (secret) in backups; bounded restore extraction; reject a backup dir inside the web root (IWebHostEnvironment injected). |
| WS‑10 | ◑ partial | **M-3** (atomic 2FA lockout — closes the brute-force race), L-7 (password policy on CreateUser), L-8 (disable-path lockout), I-4 (enroll rate limit) done. **L-9/L-10/I-2/I-5 deferred** — L-10 recovery-code KDF invalidates existing codes (migration); I-2 enumeration is rate-limited with a UX tradeoff; I-5 refresh-rotation atomicity needs an interface change + InMemory-provider fallback. |
| WS‑11 | ✅ done | L-11/L-12 — webhook SSRF classifier blocks unspecified address (0.0.0.0/::) + CGNAT 100.64/10. |
| WS‑12 | ◑ partial | L-19 (rate limiter after auth) + **L-23** (SignalR Join throttle) done. **L-18 deferred** (Clients.All scan-progress powers the app-wide toast; scoping needs admin+group targeting + frontend coordination); L-24 (hub ACL re-check on revocation) deferred. |
| WS‑13 | ☐ todo | CSP + token-at-rest — needs the SPA run end-to-end (reader/player/casting/SignalR) to tune sources without white-screening. |
| WS‑14 | ◑ partial | **I-6** (MediaTracks → IBinaryLocationService) + **L-26** (image allowlist narrowed off web.archive.org) done. **I-9** (full ArgumentList migration — defense-in-depth, MediaPathSafety already closes the live vector) and **I-3** (DB-backed must_change) deferred. |

> **Remaining work for a follow-up session, by why it's deferred:**
> - **Needs the SPA run end-to-end** (can't be verified headless here): WS-6 T6.1/T6.2/T6.4 query-token rejection + SPA media-token migration; WS-13 CSP + token-in-memory; L-18 scan-progress scoping (the app-wide toast depends on the broadcast).
> - **Needs concurrency load-testing / interface surface**: M-5 atomic transcode cap (L-14 mitigates); I-5 atomic refresh rotation (nullable return + InMemory-provider fallback).
> - **Migration / UX tradeoff**: L-10 recovery-code KDF (invalidates existing codes); I-2 signup-enumeration uniform response (silent no-op on a typo'd existing username; already rate-limited).
> - **Defense-in-depth, low value**: I-9 full ArgumentList migration (MediaPathSafety already closes the H-2 vector); I-3 DB-backed must_change; L-24 hub group re-check on revocation.

---

## Guiding principles (same as wave 1)

- **Fix the root cause, not each symptom.** Two systemic helpers close ~half the list: a combined `ApplyAccess()` (library ACL **+** rating ceiling) reused by every read path, and a single `RevokeAllForUserAsync(...)` + `_trustedDevices.RevokeAllAsync(...)` call invoked from every account-state mutation.
- **Sequence by risk × blast-radius.** Ship the one-line runtime patch and the low-regression access-control fixes first; phase the higher-regression changes (path canonicalisation, media-token enforcement, transcode session integrity) with isolated tests.
- **Every fix gets a regression test.** Extend `src/SoftMedia.Server.Tests` (`Controllers/`, `Integration/`, `Services/Security/`), don't start fresh. The wave-1 suites are the templates: `MediaAccessControlTests`, `MediaTokenIntegrationTests`, `ResourceLimitTests`, `WebhookSecurityTests`, `TotpServiceLockoutTests`, `AuthHardeningTests`.
- **Small, reviewable commits per workstream** (≈ one commit/PR each).
- **Build/test conventions** (per project memory): the running backend locks `bin` — stop it before `dotnet build`; run tests via the explicit test project (`dotnet test src/SoftMedia.Server.Tests`), **not** the broken root csproj; `dotnet ef` needs explicit `--project`/`--startup-project`. Most tasks here need **no EF migration** (no schema change); the two exceptions are flagged inline (WS-10 recovery-code format is algorithm-only/no schema; nothing here adds a column).

> **Verification debt to clear first.** The audit's verification phase was cut short by a session limit, so several findings (frame-preview ACL, SignalR throttle, lock eviction, `*.archive.org` allowlist, the SPA dependency CVEs) are **reported-unverified** in the report. **WS-0 confirms them before they consume fix effort.** Items I already hand-verified are marked **(confirmed)** in the report and need no re-check.

---

## Workstream sequencing (overview)

| WS | Title | Closes | Priority | Regression risk |
|----|-------|--------|----------|-----------------|
| **WS‑0** | Clear verification debt (confirm ⚠ unverified findings) | L-21, L-23, L-25, L-26, L-27 | 🔴 P0 (gate) | None (read-only) |
| **WS‑1** | Patch the .NET runtime + wire dependency scanning | H‑4, I‑16, L‑27 | 🔴 P0 | Low |
| **WS‑2** | Access-control routing + rating-ceiling sweep | H‑1, M‑1, L‑1, L‑2 | 🔴 P0 | Medium |
| **WS‑3** | Revoke credentials on every account-state change | H‑2, L‑6 | 🔴 P0 | Low |
| **WS‑4** | Image decode-bomb (pixel-budget) guard | H‑3 | 🔴 P0 | Low‑Med |
| **WS‑5** | Path canonicalisation & symlink jail | M‑7, L‑13, L‑22 | 🟠 P1 | Medium |
| **WS‑6** | Media-token (H3) enforcement & scope | M‑2, L‑3, L‑4, L‑5, I‑8 | 🟠 P1 | Medium |
| **WS‑7** | Transcode session integrity (sid + caps + leaks) | M‑4, M‑5, L‑14, L‑21, L‑25 | 🟠 P1 | Med (phased) |
| **WS‑8** | DLNA rating ceiling + browse clamp | M‑6, L‑15 | 🟠 P1 | Low |
| **WS‑9** | Backup secret-handling & restore safety | M‑8, L‑20 | 🟠 P1 | Low |
| **WS‑10** | TOTP + auth-flow hardening | M‑3, L‑7, L‑8, L‑9, L‑10, I‑2, I‑4, I‑5 | 🟡 P2 | Low |
| **WS‑11** | Webhook SSRF classifier completeness | L‑11, L‑12, I‑7 | 🟡 P2 | Low |
| **WS‑12** | SignalR & pipeline-ordering hardening | L‑18, L‑19, L‑23, L‑24 | 🟡 P2 | Low |
| **WS‑13** | Content-Security-Policy + token-at-rest | I‑1 | 🟢 P3 | Med (E2E tuning) |
| **WS‑14** | Consistency cleanup (binary paths, ArgumentList, allowlist) | I‑6, I‑9, L‑26, I‑3 | 🟢 P3 | Low |

---

## WS‑0 — Clear verification debt 🔴 P0 (gate, read-only)  *(L-21, L-23, L-25, L-26, L-27)*

The report flags these ⚠ *reported-unverified*. Confirm before allocating fix effort (a re-run of the audit workflow after the session limit resets also covers this).

- [ ] **T0.1** — Confirm the **frame-preview ACL** path: read `VideoPreviewService.GetPreviewImageAsync` and verify it routes the item through `ValidateMediaAccessAsync` (the report's L-21 only confirmed the *no-concurrency-cap* half at the controller). If the ACL is missing there, this rises to High (sibling of H-1).
- [ ] **T0.2** — Confirm **`KeyedLock` / `TranscodeSessionManager` lock eviction** (L-25) is genuinely absent and attacker-reachable via distinct `?sid=` (feeds WS-7).
- [ ] **T0.3** — Confirm **SignalR `Join*` throttling** (L-23) — whether any per-connection invocation limit exists.
- [ ] **T0.4** — Confirm the **`*.archive.org` allowlist** (L-26) admits `web.archive.org` and assess whether it's a usable fetch-anything hop (feeds WS-14).
- [ ] **T0.5** — Run `npm audit --omit=dev` in `src/SoftMedia.Client` and `dotnet list package --vulnerable --include-transitive` in `src/SoftMedia.Server`; record the *real* advisory IDs (the report deliberately did not trust the agent's `CVE-2026-*` claims). Feeds WS-1.

---

## WS‑1 — Patch the .NET runtime + dependency scanning 🔴 P0  *(closes H-4, I-16, L-27)*

**Root cause:** the runtime/packages are several patch levels behind; no SCA in CI.

- [ ] **T1.1** — Bump the .NET 8 SDK/runtime used for build + deploy to the latest `8.0.x` (**≥ 8.0.21**, the CVE-2025-55315 fix line) and update the `8.0.2` package pins in [SoftMedia.Server.csproj](../../src/SoftMedia.Server/SoftMedia.Server.csproj) to match. Confirm the deployed `Microsoft.AspNetCore.App` shared framework is the patched version (that, not the NuGet pin, is what fixes Kestrel).
- [ ] **T1.2** — Triage T0.5 output: update any genuinely-vulnerable package (e.g. `axios` if `npm audit` confirms it's below a fix line) and re-test the SPA.
- [ ] **T1.3** — Add SCA to CI: a step running `dotnet list package --vulnerable --include-transitive` (fail on High/Critical) and `npm audit --audit-level=high`. Optionally enable Dependabot/Renovate for the two manifests.
- [ ] **T1.4** — Acceptance: CI red on a known-vulnerable pin; `dotnet --version`/runtime ≥ 8.0.21 documented in the deploy guide.

---

## WS‑2 — Access-control routing + rating-ceiling sweep 🔴 P0  *(closes H-1, M-1, L-1, L-2)*

**Root cause (systemic):** read paths apply `ApplyLibraryAccessFilter` but omit `ApplyContentRatingFilter`; one endpoint omits both; and `MediaItem.Path` is serialized to clients.

- [ ] **T2.1** — Introduce a single combined extension `ApplyAccess(this IQueryable<MediaItem>, LibraryAccess, UserRatingCeilings)` (or a repository method) that applies **both** filters, and a small helper to resolve both providers. Use it everywhere below so the two can't drift. (Sits next to [RatingFilterExtensions.cs](../../src/SoftMedia.Server/Services/Security/ContentRating/RatingFilterExtensions.cs) / [LibraryAccessFilterExtensions.cs](../../src/SoftMedia.Server/Services/Security/LibraryAccess/LibraryAccessFilterExtensions.cs).)
- [ ] **T2.2** — **H-1:** gate `LibraryService.GetRecentlyAddedAsync` on the caller's library access **before** reading the cache; re-apply the rating ceiling per request (or key the cache by access-class so a child can't read an adult cache entry). Have the controller 404 via the ACL-aware `_libraryRepository.ExistsAsync(id)` for a denied library. [LibrariesController.cs:130-135](../../src/SoftMedia.Server/Controllers/LibrariesController.cs#L130-L135), [LibraryService.cs:415-471](../../src/SoftMedia.Server/Services/Media/LibraryService.cs#L415-L471).
- [ ] **T2.3** — **H-1 (path leak):** stop serializing `MediaItem.Path` into the user-facing `MediaItemDto` ([MediaItemDto.cs:138](../../src/SoftMedia.Server/DTOs/MediaItemDto.cs#L138)). Audit other DTOs for on-disk paths; keep them server-internal.
- [ ] **T2.4** — **M-1:** inject `IUserContentRatingProvider` into `CollectionsController` and add the rating filter to both `MediaItems` queries and the visible-count threshold logic. [CollectionsController.cs:100-186](../../src/SoftMedia.Server/Controllers/CollectionsController.cs#L100-L186).
- [ ] **T2.5** — **L-1:** add `ApplyContentRatingFilter` to `WatchlistController.Get` alongside the existing ACL filter. [WatchlistController.cs:72-94](../../src/SoftMedia.Server/Controllers/WatchlistController.cs#L72-L94).
- [ ] **T2.6** — **L-2:** in `PlaylistsController.AddItems`, intersect requested ids with an ACL-filtered query so denied-library tracks can't be attached. [PlaylistsController.cs:212-240](../../src/SoftMedia.Server/Controllers/PlaylistsController.cs#L212-L240).
- [ ] **T2.7** — Tests: a rating-restricted user gets 404/empty from `/recent`, collections, watchlist for over-rating/denied items; no DTO contains a filesystem path. Extend `MediaAccessControlTests`.

---

## WS‑3 — Revoke credentials on every account-state change 🔴 P0  *(closes H-2, L-6)*

**Root cause (systemic):** only `ChangePassword` revokes sessions; admin reset / ban / delete / deny don't.

- [ ] **T3.1** — Add a private helper (or service method) `RevokeUserSessionsAsync(userId, reason)` = `RefreshTokenService.RevokeAllForUserAsync` + `ITrustedDeviceService.RevokeAllAsync`. Inject both services where missing.
- [ ] **T3.2** — **H-2:** call it from `UsersController.ResetUserPassword` after updating the hash. [UsersController.cs:336-376](../../src/SoftMedia.Server/Controllers/UsersController.cs#L336-L376).
- [ ] **T3.3** — **L-6:** call it from ban, soft-delete, and deny/un-approve. [UsersController.cs:164-270](../../src/SoftMedia.Server/Controllers/UsersController.cs#L164-L270).
- [ ] **T3.4** — Kill in-flight **access/media tokens** too: either shorten the media-token lifetime (WS-6 L-3) or have `OnTokenValidated` re-check `IsBanned/IsDeleted/IsApproved` for media tokens (the DB query already exists for cast tokens). [ServiceCollectionExtensions.cs:146-160](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L146-L160).
- [ ] **T3.5** — Tests: after admin reset/ban, the victim's existing refresh cookie 401s and a remembered device re-challenges 2FA. Extend `AuthHardeningTests`.

---

## WS‑4 — Image decode-bomb guard 🔴 P0  *(closes H-3)*

**Root cause:** SkiaSharp decode with no decoded-dimension cap on several paths.

- [ ] **T4.1** — Add a shared guard `TryGetSafeImageDimensions(stream/bytes)` using `SKCodec.Create` to read header W×H **without** decoding; reject (return null/404) when `W*H` > a budget (~50 MPixel) or `W`/`H` > a hard limit (e.g. 16384).
- [ ] **T4.2** — Apply it before every decode: [ThumbnailService.cs:62,84](../../src/SoftMedia.Server/Services/Media/ThumbnailService.cs#L62), [ComicPageThumbnailService.cs:87](../../src/SoftMedia.Server/Services/Media/ComicPageThumbnailService.cs#L87), [MusicScanner.cs:355](../../src/SoftMedia.Server/Services/Scanning/MusicScanner.cs#L355) (scan-time embedded art), [MusicController.cs:122](../../src/SoftMedia.Server/Controllers/MusicController.cs#L122), [ImageController.cs:279](../../src/SoftMedia.Server/Controllers/ImageController.cs#L279) (proxy cache, before caching).
- [ ] **T4.3** — For resizes, use a sampled/downscaled decode (`SKCodec` + downsampled `SKImageInfo`/`GetScaledDimensions`) so the full-res buffer is never allocated.
- [ ] **T4.4** — Tests: a small "pixel-flood" PNG/JPEG (e.g. 30000×30000) is rejected by each path without OOM. New `ImageDecodeSafetyTests`.

---

## WS‑5 — Path canonicalisation & symlink jail 🟠 P1  *(closes M-7, L-13, L-22)*

**Root cause:** `ResolveRealPath` resolves only the **leaf** symlink; `GetFullPath` doesn't canonicalise symlinked parent dirs; the scanner follows reparse points.

- [ ] **T5.1** — **M-7:** make `StreamSecurityService.ResolveRealPath` fully canonical — walk every path component resolving each `ResolveLinkTarget`, or compare OS-realpath of **both** the file and each library root. [StreamSecurityService.cs:72-92](../../src/SoftMedia.Server/Services/Security/StreamSecurityService.cs#L72-L92).
- [ ] **T5.2** — **L-22:** have the scanner skip reparse points (`FileAttributes.ReparsePoint`) when descending, and add a depth/visited-set guard so a cyclic/hostile symlink tree can't cause unbounded enumeration or out-of-library ingestion. [BaseMediaScanner.cs:246-279](../../src/SoftMedia.Server/Services/Scanning/BaseMediaScanner.cs#L246-L279).
- [ ] **T5.3** — **L-13:** route `MusicImageService` cover-art validation through `StreamSecurityService.IsPathAuthorized` (resolves symlinks + appends the separator) instead of its bespoke `GetFullPath`-only check. [MusicImageService.cs:167-211](../../src/SoftMedia.Server/Services/Media/MusicImageService.cs#L167-L211).
- [ ] **T5.4** — Tests: a symlinked **directory** inside a library pointing at `/etc` is rejected at serve time; the existing skipped symlink test ([StreamSecurityServiceTests.cs:222-237](../../src/SoftMedia.Server.Tests/Services/Security/StreamSecurityServiceTests.cs#L222-L237)) must **fail loudly** rather than skip when symlink creation is unavailable in CI.

---

## WS‑6 — Media-token (H3) enforcement & scope 🟠 P1  *(closes M-2, L-3, L-4, L-5, I-8)*

**Root cause:** the H3 fix is opt-in — full role-bearing JWTs are still accepted in query strings and the media token isn't re-validated/limited.

- [ ] **T6.1** — **M-2:** in `OnTokenValidated`, reject any token **lifted from the query string** that carries a `Role` claim (i.e. require `token_use ∈ {media, cast}` for query-string tokens). [ServiceCollectionExtensions.cs:95-133](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L95-L133).
- [ ] **T6.2** — **M-2 (SPA):** make the client hard-depend on the media token for `?token=`/`?access_token=` URLs — block the first media-URL render until `fetchMediaToken` resolves (or persist a freshly-minted media token), so the full access JWT is never placed in a URL. [mediaImageUrl.ts:22-32](../../src/SoftMedia.Client/src/lib/mediaImageUrl.ts#L22-L32), [authStore.ts:36-55](../../src/SoftMedia.Client/src/store/authStore.ts#L36-L55).
- [ ] **T6.3** — **L-3:** re-validate the media token against live user state (`IsBanned/IsDeleted/IsApproved`) in `OnTokenValidated`, mirroring the cast-token recheck (coordinate with T3.4).
- [ ] **T6.4** — **L-4/L-5:** restrict the media token to safe reads — require `GET`/`HEAD` when `token_use=media`, or move the bookmark/highlight **write** endpoints off the `/api/v1/books` prefix / out of `IsMediaRoute`. [BookController.cs:203-362](../../src/SoftMedia.Server/Controllers/BookController.cs#L203-L362).
- [ ] **T6.5** — **I-8:** scheme-check the initial proxy/cache URL (not just redirects) instead of relying on `HttpClient` to reject non-`http(s)`. [ImageCacheService.cs](../../src/SoftMedia.Server/Services/Media/ImageCacheService.cs).
- [ ] **T6.6** — **T4.1 from wave 1 deferral:** scrub `token`/`access_token` query params from request logs.
- [ ] **T6.7** — Tests: a role-bearing JWT in `?access_token=` is rejected on media routes; a media token is refused on a `POST` book-write and after the user is banned. Extend `MediaTokenIntegrationTests`.

---

## WS‑7 — Transcode session integrity 🟠 P1 (phased)  *(closes M-4, M-5, L-14, L-21, L-25)*

**Root cause:** client-supplied `?sid=` is unvalidated and used in on-disk paths + as the cap key; caps aren't atomic and have no hard floor; locks/sessions leak.

- [ ] **T7.1** — **M-4:** validate `?sid=` at the controller boundary — reject anything not matching `^[A-Za-z0-9_-]{1,32}$` (400). As defense-in-depth, after building `sessionDir` assert `Path.GetFullPath(sessionDir)` still starts with the canonical temp root + separator. [TranscodeController.cs:136-152](../../src/SoftMedia.Server/Controllers/TranscodeController.cs#L136-L152).
- [ ] **T7.2** — **M-5:** make the per-user cap atomic with session registration — an `Interlocked`/`SemaphoreSlim` counter keyed on `userId` acquired before spawning ffmpeg and released on stop/abort, so the active count can't be raced. [TranscodeService.cs:201-235](../../src/SoftMedia.Server/Services/Transcoding/TranscodeService.cs#L201-L235).
- [ ] **T7.3** — **L-14:** ship a non-zero hard ceiling that config cannot disable (a config value of `0` must not mean "unlimited").
- [ ] **T7.4** — **L-21:** count the frame-preview ffmpeg spawn against the transcode/preview budget (and apply the T0.1 ACL fix if needed). [TranscodeController.cs:279-299](../../src/SoftMedia.Server/Controllers/TranscodeController.cs#L279-L299).
- [ ] **T7.5** — **L-25:** ref-count + evict `KeyedLock` and `TranscodeSessionManager` session-lock entries on stop so distinct `?sid=` values can't grow them unboundedly. [KeyedLock.cs](../../src/SoftMedia.Server/Helpers/KeyedLock.cs), [TranscodeSessionManager.cs:35-40](../../src/SoftMedia.Server/Services/Transcoding/TranscodeSessionManager.cs#L35-L40).
- [ ] **T7.6** — Tests: a loop of distinct `?sid=` can't exceed the per-user cap, can't traverse out of the temp root, and doesn't grow the lock map. Extend `ResourceLimitTests`.

---

## WS‑8 — DLNA rating ceiling + browse clamp 🟠 P1  *(closes M-6, L-15)*

- [ ] **T8.1** — **M-6:** add an admin `DlnaMaxContentRating` setting (default the most restrictive; fail-safe on NULL `ContentRating`) and apply a rating predicate to every DLNA browse query and to `DlnaController.Media` — build `UserRatingCeilings` from it and call `ApplyContentRatingFilter` in `LibraryChildrenAsync`/`SeriesChildrenAsync`/`AlbumChildrenAsync` + the `BrowseMetadata` "I" case. [DlnaContentDirectory.cs:98-192](../../src/SoftMedia.Server/Services/Dlna/DlnaContentDirectory.cs#L98-L192), [DlnaController.cs:134-165](../../src/SoftMedia.Server/Controllers/DlnaController.cs#L134-L165).
- [ ] **T8.2** — **L-15:** clamp DLNA `Browse RequestedCount=0` → a sane page size instead of `Take(int.MaxValue)`.
- [ ] **T8.3** — Until T8.1 ships, document that exposing a mixed-rating library over DLNA defeats parental controls (create a dedicated family-safe library).
- [ ] **T8.4** — Tests: a restricted-rating title in an exposed library is absent from Browse and 404s on the media endpoint. Extend `DlnaContentDirectoryTests`.

---

## WS‑9 — Backup secret-handling & restore safety 🟠 P1  *(closes M-8, L-20)*

- [ ] **T9.1** — **M-8:** stop bundling `appsettings.json` verbatim in the backup, **or** redact secret keys (`JwtSettings:Secret`, provider API keys) before adding it, **or** encrypt the archive with an operator passphrase. Prefer redaction + a documented "restore your own config" step, since the DB already holds the sensitive rows. [BackupService.cs:80-96](../../src/SoftMedia.Server/Services/Infrastructure/BackupService.cs#L80-L96).
- [ ] **T9.2** — **L-20:** reject a `Maintenance.BackupDirectory` that resolves inside `wwwroot`/the static content root (validate on set); add an uncompressed-size cap to restore extraction (`dbEntry.Length` ceiling) to bound a decompression-bomb disk-fill. [BackupService.cs:204-265,309-313](../../src/SoftMedia.Server/Services/Infrastructure/BackupService.cs#L204-L265).
- [ ] **T9.3** — Recommend (docs) the JWT secret live in env/user-secrets, not appsettings — reduces M-8 blast radius even if T9.1 redaction is partial.
- [ ] **T9.4** — Tests: a backup archive contains no plaintext JWT secret; a `wwwroot` backup dir is rejected; an oversized restore entry is refused.

---

## WS‑10 — TOTP + auth-flow hardening 🟡 P2  *(closes M-3, L-7, L-8, L-9, L-10, I-2, I-4, I-5)*

- [ ] **T10.1** — **M-3:** make the TOTP lockout check-and-increment atomic — a single `TryRegisterAttempt(userId)` under one `AddOrUpdate` that increments and returns locked-state, called at the **top** of `CompleteTwoFactor` before `VerifyCode`. [AuthController.cs:229-243](../../src/SoftMedia.Server/Controllers/AuthController.cs#L229-L243), [TotpService.cs:158-174](../../src/SoftMedia.Server/Services/Identity/TotpService.cs#L158-L174).
- [ ] **T10.2** — **L-8/L-9:** apply the per-user lockout to the 2FA-**disable** code path, and add escalating backoff (don't fully reset every 15 min).
- [ ] **T10.3** — **L-10:** raise recovery-code entropy to ≥ 80 bits (`RandomNumberGenerator.GetBytes(10)` → 16+ base32 chars) **and** stop bare-SHA-256 hashing — run them through the Argon2id `PasswordHasher` or HMAC them under the existing JWT-derived key; constant-time compare on lookup. (Re-issues codes; no schema change.) [TotpService.cs:114-130](../../src/SoftMedia.Server/Services/Identity/TotpService.cs#L114-L130).
- [ ] **T10.4** — **L-7:** add `PasswordPolicy.Validate` to `UsersController.CreateUser` (signup + reset already guarded). [UsersController.cs:61-87](../../src/SoftMedia.Server/Controllers/UsersController.cs#L61-L87).
- [ ] **T10.5** — **I-4:** add the rate-limit policy to TOTP enroll/confirm endpoints. **I-2:** make signup return a uniform response whether or not the username exists (kill the enumeration oracle). **I-5:** add a concurrency guard to refresh-token rotation so simultaneous refreshes can't fork one token into multiple live chains. [RefreshTokenService.cs](../../src/SoftMedia.Server/Services/Identity/RefreshTokenService.cs).
- [ ] **T10.6** — Tests: parallel 2FA guesses can't exceed the lockout; weak password rejected on create; recovery code keyspace infeasible to brute-force offline. Extend `TotpServiceLockoutTests`/`PasswordPolicyTests`.

---

## WS‑11 — Webhook SSRF classifier completeness 🟡 P2  *(closes L-11, L-12, I-7)*

- [ ] **T11.1** — Prefer flipping `NetworkClassifier` from a **denylist** to a **global-unicast allowlist**: treat an address as routable-public only if it's global-unicast, blocking unspecified (`0.0.0.0`/`::`/`::ffff:0.0.0.0` — **L-11**), loopback, private, link-local, ULA, multicast, reserved, and CGNAT `100.64.0.0/10` (**L-12**) unless the matching opt-in is set. [NetworkClassifier.cs:17-49](../../src/SoftMedia.Server/Services/Infrastructure/NetworkClassifier.cs#L17-L49).
- [ ] **T11.2** — **I-7:** make the `ConnectCallback` **fail closed** when the pinned-IP option is absent (don't silently re-resolve DNS). [ServiceCollectionExtensions.cs:261-280](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L261-L280).
- [ ] **T11.3** — Tests: `0.0.0.0`, `::`, IPv4-mapped, and `100.64.x` targets are blocked by default. Extend `WebhookSecurityTests`.

---

## WS‑12 — SignalR & pipeline-ordering hardening 🟡 P2  *(closes L-18, L-19, L-23, L-24)*

- [ ] **T12.1** — **L-19:** move `app.UseRateLimiter()` **after** `app.UseAuthentication()`/`UseAuthorization()` so the image-proxy per-user partition sees `User` (today it always falls back to IP). Re-verify the auth rate-limit policy still partitions correctly. [Program.cs:225-230](../../src/SoftMedia.Server/Program.cs#L225-L230).
- [ ] **T12.2** — **L-18:** scope `ScanProgress` to the `library-{id}` group (or admins) instead of `Clients.All`, so library GUIDs/counts don't leak to ACL-restricted users. [MediaNotificationService.cs:143-146](../../src/SoftMedia.Server/Services/Media/MediaNotificationService.cs#L143-L146).
- [ ] **T12.3** — **L-24:** re-check the library ACL when *dispatching* group notifications (or evict group membership on access revocation), not only at join time. [MediaHub.cs:54-60](../../src/SoftMedia.Server/Hubs/MediaHub.cs#L54-L60).
- [ ] **T12.4** — **L-23:** add a modest per-connection invocation throttle to `MediaHub` join methods (each does a DB round-trip). [MediaHub.cs:30-146](../../src/SoftMedia.Server/Hubs/MediaHub.cs#L30-L146).
- [ ] **T12.5** — Tests: the image-proxy limiter partitions per user after T12.1; a restricted user receives no `ScanProgress` for denied libraries.

---

## WS‑13 — Content-Security-Policy + token-at-rest 🟢 P3  *(closes I-1)*

- [ ] **T13.1** — Add a `Content-Security-Policy` to `SecurityHeadersExtensions` (`default-src 'self'`; tune `img-src`/`media-src`/`connect-src`/`worker-src` for the SPA + hls.js + blob workers). Test live to avoid white-screening; keep the epub.js same-origin iframe working (`frame-src 'self'`).
- [ ] **T13.2** — Move the access token out of `localStorage` to in-memory (keep refresh in the httpOnly cookie), shrinking the XSS-exfil window. Coordinate with WS-6 (media token already keeps the access token out of URLs).
- [ ] **T13.3** — Acceptance: CSP present on SPA + API responses; reader/player/casting still function end-to-end.

---

## WS‑14 — Consistency cleanup 🟢 P3  *(closes I-6, I-9, L-26, I-3)*

- [ ] **T14.1** — **I-6:** delete `MediaTracksController`'s private hardcoded ffmpeg/ffprobe path resolver; use `IBinaryLocationService`/`FFmpeg:Path` like the rest of the codebase. [MediaTracksController.cs:338-376](../../src/SoftMedia.Server/Controllers/MediaTracksController.cs#L338-L376).
- [ ] **T14.2** — **I-9:** finish the deferred `ArgumentList` migration of the 7 interpolating ffmpeg/ffprobe builders so `MediaPathSafety` becomes defense-in-depth, not the only control. Isolate-test every HW-accel/subtitle/HDR branch. [MediaProbeService.cs:39](../../src/SoftMedia.Server/Services/Media/MediaProbeService.cs#L39) et al.
- [ ] **T14.3** — **L-26:** tighten the image host allowlist — drop or constrain `web.archive.org`/Wayback (a content-rewriting fetch hop) per T0.4. [ImageCacheService.cs](../../src/SoftMedia.Server/Services/Media/ImageCacheService.cs).
- [ ] **T14.4** — **I-3:** optionally back the `must_change` gate with a DB check (not only the JWT claim) for defense-in-depth across all auth schemes.

---

## Deferrals & sequencing notes

- **WS-13 (CSP)** stays P3 because a strict policy needs live E2E tuning against the SPA's media/worker sources — same reason it was deferred in wave 1. Ship the other layers first.
- **WS-7 / WS-14.2** touch the core transcode path; phase them with isolated branch testing (the wave-1 plan's rationale for deferring the ArgumentList migration applies).
- No EF migration is required for any task. T10.3 changes the recovery-code **algorithm**, not the schema (existing codes are re-issued on next enrol).
- Suggested commit cadence: WS-0 (read-only spike) → one commit per WS, P0 → P3.

---

## Appendix — broader hardening backlog (beyond the audit findings)

Not audit findings, but standard hardening for a self-hosted, internet-exposed media server. Worth a wave-3 grooming pass:

- **Per-account credential lockout.** The login limiter is per-**IP** (15/min); add a per-**account** failed-password counter with backoff so distributed credential-stuffing across IPs is also bounded. (Complements the per-IP limiter, mirrors the TOTP per-user lockout.)
- **Security audit log.** Persist security events — login success/failure, password change, role change, backup create/download/restore, webhook target change — to an admin-viewable log. Critical for incident response on a box with no SOC.
- **Reverse-proxy trust hardening.** `ForwardedHeaders:TrustedProxies` defaults to loopback only; if an operator forgets to configure it behind a non-loopback proxy, the per-IP limiter collapses and `X-Forwarded-For` could be attacker-influenced. Add a startup warning when the app sees forwarded headers from an untrusted hop, and document it prominently.
- **`__Host-` cookie prefix.** When HTTPS, use `__Host-refreshToken` (forces Secure + host-only + path `/`) for the refresh cookie to harden against subdomain/cookie-fixation tricks.
- **Backup encryption at rest + integrity.** Optional operator passphrase (ties to WS-9), and verify the per-file SHA-256 manifest on restore (currently written but not re-checked).
- **Upload content-type / magic-byte validation.** Validate restore-zip and any image upload by content sniffing, not just extension/declared type.
- **Optional admin 2FA enforcement.** A setting to require TOTP for `Admin`-role accounts.
- **Log redaction discipline.** Audit that no path ever logs a raw token/password/secret (the refresh diagnostic logs cookie *names* only — keep it that way; add a test/guard).
- **Swagger** is already `Development`-only ([Program.cs:219-223](../../src/SoftMedia.Server/Program.cs#L219-L223)) — keep it; don't expose it in production.
- **Response-size / global request limits.** Consider a conservative global request-body cap distinct from streaming routes, and review Kestrel limits for the reverse-proxy deployment.
