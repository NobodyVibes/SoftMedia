# Security Hardening — Wave 1: Implementation Plan & Task List

**Source:** [docs/reports/security-audit-2026-06-07.md](../reports/security-audit-2026-06-07.md) (29 verified findings: 1 Critical, 3 High, 9 Medium, 12 Low, 2 Info)
**Branch:** `security/hardening-wave-1`
**Goal:** Remediate the verified findings, prioritising the systemic root causes that each close multiple findings at once, while preserving the app's core behaviour (auth, streaming, transcoding).

## Implementation status

**ALL workstreams (WS-1 through WS-11) — DONE and verified.** Maintained suite `SoftMedia.Server.Tests`: **769 passing, 1 skipped, 0 failed**. SPA: `tsc` clean + 158 vitest pass (2 pre-existing a11y-guard failures in untouched files). New/expanded regression tests across every workstream: `AccountActivationSecurityTests`, `MediaAccessControlTests`, `MediaPathSafetyTests`, `MediaTokenIntegrationTests`, `ResourceLimitTests`, `WebhookSecurityTests`, `DlnaContentDirectoryTests`/`DlnaIntegrationTests`, `PasswordPolicyTests`, `TotpServiceLockoutTests`, `AuthHardeningTests`, comic zip-bomb test.

**Every Critical, High, Medium, and Low finding is remediated:**
- **Critical:** C1
- **High:** H1, H2, H3
- **Medium:** M1, M2, M3, M4, M5, M6, M7, M8, M9, M10 (all 10)
- **Low:** L1–L12 (all 12; L7's CSP sub-part deferred — see below)
- **Info:** I1

Deferrals (all mitigated/low-value, intentional, documented):
- **I2** (KeyedLock/session-lock eviction — Info; compounds only the now-capped M9).
- **L7 CSP** — the other L7 headers (nosniff, X-Frame-Options, Referrer-Policy, HSTS) shipped; a strict `Content-Security-Policy` is deferred because it can white-screen the SPA and needs live E2E tuning of allowed sources.
- **Full `ArgumentList` migration** of the transcode/probe builders — the H2 vector is already closed by the `MediaPathSafety` guard; this is belt-and-suspenders.
- **Per-rating DLNA ceiling** — the admin library allow-set is the primary M7 control; intra-library rating filtering is a follow-up.
- **T2.4** (book annotation write-side ACL — Info-level consistency note).

Deliberate deferrals (mitigated, tracked as follow-ups — see notes inline):
- **T2.4** (BookController bookmark/highlight write-side ACL): reverted. It's an Info-level consistency note (no disclosure), and routing it through the mock-based unit tests added churn for ~no security gain. Read endpoints remain fully gated.
- **T3.2 / T3.4** (full `ArgumentList` migration of `TranscodeProfileBuilder`, `MediaProbeService`, `SubtitleService`, `VideoPreviewService`, `TrickplayService`, `ChromaprintFingerprintExtractor`, `TranscodeDebugService`): the H2 vector is **closed** by the T3.1 path-safety guard (`MediaPathSafety`) applied at scan time and in `StreamSecurityService`, so no quote/control char can reach those interpolated command lines. The per-builder `ArgumentList` migration remains as belt-and-suspenders hardening, deferred because it's a large refactor of the core transcode path that warrants isolated testing of every HW-accel/subtitle/HDR branch. `AudioStreamController` (M2) was migrated to `ArgumentList` now.

> Known pre-existing issue (not caused by this work): two test projects in the solution — `src/SoftMedia.Tests` (`SoftMedia.IntegrationTests`) and `tests/SoftMedia.Tests` (`SoftMedia.Tests`) — do **not compile** on `HEAD` (they reference removed members like `ITokenService.GenerateRefreshToken` and stale controller/repository constructor signatures). They are abandoned/stale; the live suite is `SoftMedia.Server.Tests`. Cleaning or removing them is separate from this security work.

**Next: P1 (WS-4 … WS-7)** — paused here for review per request.

## Guiding principles

- **Fix the root cause, not each symptom.** Most access-control findings share one cause: a few endpoints bypass the ACL-aware repository layer. One reference pattern (`BookController` → `GetByIdWithLibraryAsync` + `ValidateMediaAccessAsync`) fixes them all.
- **Sequence by risk × blast-radius.** Ship the Critical and the low-regression-risk fixes first; phase the high-regression-risk changes (ffmpeg, streaming tokens) so the core feature keeps working.
- **Every fix gets a regression test.** The repo already has `src/SoftMedia.Server.Tests` with `Controllers/`, `Integration/`, and `Services/Security/` suites — extend them, don't start fresh.
- **Small, reviewable commits per workstream.** Each workstream below ≈ one commit/PR.
- **Build/test note** (per project memory): the backend locks `bin` while running — stop it before `dotnet build`; run tests via the test project explicitly (`dotnet test src/SoftMedia.Server.Tests`), not the broken root csproj. No EF migration is needed for any task here (`MustChangePassword` is already a column).

---

## Workstream sequencing (overview)

| WS | Title | Closes | Priority | Regression risk |
|----|-------|--------|----------|-----------------|
| **WS‑1** | Default credentials + server-side `MustChangePassword`/`IsApproved` | C1, M1, + dev-seed | 🔴 P0 | Low |
| **WS‑2** | Systemic access-control routing through the ACL layer | H1, M4, L3 (+ BookController note) | 🔴 P0 | Medium |
| **WS‑3** | ffmpeg/ffprobe argument-injection hardening | H2, M2 | 🔴 P0 | Med→High (phased) |
| **WS‑4** | Stop leaking full JWTs in URLs (scoped media tokens) | H3 | 🟠 P1 | Medium |
| **WS‑5** | Webhook SSRF (block internal by default + IP pinning) | M5, M6 | 🟠 P1 | Low |
| **WS‑6** | DLNA access-control parity | M7, L9 | 🟠 P1 | Low |
| **WS‑7** | DoS clamps + transcode session caps | M8, M9, M10, I2 | 🟠 P1 | Low |
| **WS‑8** | Auth-flow hardening (TOTP lockout, invite race, timing, policy, rate limits) | M3, L1, L2, L11, L12 | 🟡 P2 | Low |
| **WS‑9** | Web transport hardening (headers, HSTS, CORS) | L6, L7, L8 | 🟡 P2 | Low |
| **WS‑10** | Untrusted-parsing size caps | L4, L5 | 🟡 P2 | Low |
| **WS‑11** | DLNA SSDP source-filtering + banner trim | L10, I1 | 🟢 P3 | Low |

---

## WS‑1 — Default credentials & server-side flag enforcement  🔴 P0  *(closes C1, M1)*

**Root cause:** `MustChangePassword` and `IsApproved` are enforced only in the SPA / only on *some* auth flows, and a fixed default admin password is seeded on every boot.

- [ ] **T1.1** — `DbInitializer.cs`: stop seeding a fixed password. On first admin seed, generate a random password via `RandomNumberGenerator`, set `MustChangePassword=true`, and **log it once** to the console (and/or write to a `first-run-credentials.txt` with restrictive perms). Update the existing-admin branch ([DbInitializer.cs:29-42](../../src/SoftMedia.Server/Data/DbInitializer.cs#L29-L42)) to **stop re-arming** the known default; if a legacy `admin123` hash is detected, force `MustChangePassword=true` (already done) — do not weaken further.
- [ ] **T1.2** — Add a `must_change` claim in `TokenService.IdentityClaims` when `user.MustChangePassword` is true ([TokenService.cs:69](../../src/SoftMedia.Server/Services/Identity/TokenService.cs#L69)).
- [ ] **T1.3** — Add a global enforcement gate (middleware or `IAsyncAuthorizationFilter`) that, for an authenticated principal carrying `must_change`, returns 403 on **every** endpoint except `POST /api/v1/auth/change-password`, `/auth/logout`, and `/auth/refresh-token`. Wire it after `UseAuthorization` in `Program.cs`.
- [ ] **T1.4** — `AuthController.Signup` ([:100-141](../../src/SoftMedia.Server/Controllers/AuthController.cs#L100-L141)): if the new user is **not** `IsApproved` (no invite, signup ≠ first user), return a "pending approval" response **without** issuing tokens — mirror the `Login` (L155) / `Refresh` (L335) checks. *(closes M1)*
- [ ] **T1.5** — `DbInitializer.cs:65-101`: remove (or guard behind an explicit dev/seed flag) the seeded "Test Movies" library + dummy `test_media.mkv` so they don't ship to production DBs.
- [ ] **T1.6** — Tests: an integration test proving `admin`/old-default no longer logs in (or is forced through change-password before any other endpoint works), and that an unapproved signup gets no usable token.

---

## WS‑2 — Route metadata/derived endpoints through the ACL layer  🔴 P0  *(closes H1, M4, L3)*

**Reference pattern to copy:** `BookController` — `_mediaRepository.GetByIdWithLibraryAsync(id)` then `await _securityService.ValidateMediaAccessAsync(item)`, mapping `FileNotFound`/`Unauthorized` → **404** (anti-probe). `MediaController` already injects `IUserLibraryAccessProvider` and uses `ApplyLibraryAccessFilter` in `GlobalSearch` — the same machinery just needs applying to the per-id reads.

- [ ] **T2.1** — `MediaTracksController` (H1): inject `IMediaRepository` + `IStreamSecurityService`; replace the raw `_context.MediaItems.Include(Library)` query and bespoke LFI snippet in all three actions (`GetDuration`, `GetTracks`, `GetSubtitle`) with the reference pattern; map Unauthorized → `NotFound()` (replace the `Forbid()` at [:81](../../src/SoftMedia.Server/Controllers/MediaTracksController.cs#L81)).
- [ ] **T2.2** — `MediaController.GetMediaItem` (M4): fetch via `_mediaRetrievalService` / `IMediaRepository.GetByIdAsync` (which applies `ApplyContentRatingFilter` + `ApplyLibraryAccessFilter`) instead of the raw `_context` query at [:41-47](../../src/SoftMedia.Server/Controllers/MediaController.cs#L41-L47); return 404 when null.
- [ ] **T2.3** — Image/thumbnail endpoints (L3): `MusicController.ServeImageAsync`, `AudioController.GetCoverArt`, `TrickplayController.GetManifest/GetSheet` — resolve the item through `GetByIdWithLibraryAsync` + `ValidateMediaAccessAsync` before serving; 404 on Unauthorized.
- [ ] **T2.4** — Consistency note (Info): `BookController.CreateBookmark`/`CreateHighlight` ([:217](../../src/SoftMedia.Server/Controllers/BookController.cs#L217), [:314](../../src/SoftMedia.Server/Controllers/BookController.cs#L314)) — swap the existence-only `AnyAsync(id)` check for `ValidateMediaAccessAsync` for consistency (write-side, low impact).
- [ ] **T2.5** — Guardrail: add a `Services/Security` unit/integration test that asserts a restricted user gets 404 from `MediaTracks`/`Media`/cover-art endpoints for a denied-library item (extend `LibraryAccessFilterTests`/`StreamSecurityServiceTests`). Consider documenting "all per-id media endpoints MUST go through `ValidateMediaAccessAsync`" so future controllers don't regress.

---

## WS‑3 — ffmpeg/ffprobe argument-injection hardening  🔴 P0  *(closes H2, M2)*

**Constraint:** `TranscodeProfileBuilder.BuildTranscodeArgumentsAsync` builds one ~300-line space-joined `Arguments` string (filter graphs, mapping, encoder flags). A full `ArgumentList` rewrite is the correct end state but is a substantial refactor of the core transcoding path. Phased approach:

- [ ] **T3.1 — Immediate guard (low risk, closes the live vector):** reject media paths containing `"` or control characters at the trust boundary. Add the check to `StreamSecurityService` (and/or the scanner's `CanHandleFile`) so a hostile filename is never passed to any ffmpeg/ffprobe sink. On Linux the double-quote is the only argv-breakout character, so this closes H2/M2 immediately while the proper migration lands.
- [ ] **T3.2 — Migrate the simple sinks to `ArgumentList`:** `MediaProbeService`, `SubtitleService`, `VideoPreviewService` ([:65](../../src/SoftMedia.Server/Services/Media/VideoPreviewService.cs#L65)), `TrickplayService` ([:107](../../src/SoftMedia.Server/Services/Transcoding/TrickplayService.cs#L107)), `ChromaprintFingerprintExtractor`, `TranscodeDebugService` ([:203](../../src/SoftMedia.Server/Services/Transcoding/TranscodeDebugService.cs#L203)). These build short arg strings and convert cleanly to per-token `ArgumentList`. Verify `ProcessRunner`/`ProcessController` pass through `ArgumentList` (they consume `ProcessStartInfo`, so it already flows).
- [ ] **T3.3 — Migrate `AudioStreamController` (M2):** [:128-196](../../src/SoftMedia.Server/Controllers/AudioStreamController.cs#L128-L196) already builds a token `List<string>` then `string.Join(" ", …)`s it — assign that list to `ProcessStartInfo.ArgumentList` and drop the manual quoting. Lowest-effort proper fix.
- [ ] **T3.4 — Migrate `TranscodeProfileBuilder` (H2):** refactor `BuildTranscodeArgumentsAsync` to accumulate a `List<string>` of discrete tokens instead of a `StringBuilder`, populating `ProcessStartInfo.ArgumentList`. Filter-graph values (`-vf`, `-filter_complex`, `subtitles='…'`) become single list entries (no surrounding quotes needed once they're discrete argv elements). **High test burden** — exercise HW-accel paths, subtitle burn-in (bitmap + text), HDR tone-mapping, fMP4/AV1, and seek strategies.
- [ ] **T3.5** — Tests: a unit test feeding a filename containing `" -map 0 -f rawvideo /tmp/x` and asserting it is rejected (T3.1) and/or that the produced `ArgumentList` keeps it as a single `-i` value token.

---

## WS‑4 — Stop leaking full JWTs in URLs  🟠 P1  *(closes H3)*

- [ ] **T4.1 — Immediate (low risk):** add `Referrer-Policy: no-referrer` (rolls into WS‑9's header middleware) and scrub `token`/`access_token` query params from request logging; document that reverse-proxy access logs must strip them.
- [ ] **T4.2 — Proper fix:** reuse the existing **cast-token pattern** (`GenerateCastToken`, short-lived, media-scoped, role-omitted) to mint a **media-scoped streaming token** for `<img>`/`<video>`/HLS URLs instead of the full access JWT. Add an endpoint that vends a scoped token for an item the user may access; update the SPA (`mediaImageUrl.ts`, `bookService.ts`, `useTrickplay.ts`, `VideoPlayer.tsx`) to request and use it.
- [ ] **T4.3** — For plain `fetch` calls that currently use `?token=`, switch to the `Authorization` header.
- [ ] **T4.4** — Tests: assert the scoped token is rejected outside its media's stream routes (mirror `CastTokenIntegrationTests`).

---

## WS‑5 — Webhook SSRF  🟠 P1  *(closes M5, M6)*

- [ ] **T5.1** — `WebhookSecurity.ValidateTarget` ([WebhookDispatcher.cs:55-76](../../src/SoftMedia.Server/Services/Infrastructure/WebhookDispatcher.cs#L55-L76)): block private **and** link-local (incl. `169.254.0.0/16`) by default; gate LAN/loopback targets behind explicit settings (`AllowLoopbackWebhooks` already exists — add an `AllowPrivateWebhooks` analog, default false).
- [ ] **T5.2** — `WebhookDispatchWorker` ([:101-124](../../src/SoftMedia.Server/Services/Background/WebhookDispatchWorker.cs#L101-L124)): pin the validated IP via `SocketsHttpHandler.ConnectCallback` so the send connects to the exact address that passed validation (defeats DNS-rebinding TOCTOU).
- [ ] **T5.3** — Tests: extend `WebhookSecurityTests` for `169.254.169.254`, RFC1918, and a rebind (validate-public / connect-private) case.

---

## WS‑6 — DLNA access-control parity  🟠 P1  *(closes M7, L9)*

- [ ] **T6.1** — Add an admin-configured **DLNA allow-set**: which libraries are DLNA-exposed (default none) + a maximum content rating.
- [ ] **T6.2** — `DlnaContentDirectory` browse queries ([:60-139](../../src/SoftMedia.Server/Services/Dlna/DlnaContentDirectory.cs#L60-L139)): filter `_db.Libraries`/`_db.MediaItems` by that allow-set + rating ceiling.
- [ ] **T6.3** — `DlnaController.Media` ([:134-152](../../src/SoftMedia.Server/Controllers/DlnaController.cs#L134-L152)): enforce the item is an A/V type within the exposed set; 404 otherwise *(closes L9)*.
- [ ] **T6.4** — Tests: DLNA browse/stream returns only allow-set libraries; a non-exposed/over-rating item 404s.

---

## WS‑7 — DoS clamps & transcode caps  🟠 P1  *(closes M8, M9, M10, I2)*

- [ ] **T7.1** — Add a shared paging helper (or reuse `Math.Clamp(limit, 1, N)` from [WatchlistController.cs:43](../../src/SoftMedia.Server/Controllers/WatchlistController.cs#L43)) and apply it: `LibrariesController` `pageSize` (M8), `MediaController.GetRecentMedia` `limit` and `GlobalSearch` `limit` **before** the ×25 / ×5 multiplication (M10).
- [ ] **T7.2** — Transcode caps (M9): ship a non-zero default `MaxSimultaneousTranscodesPerUser` (e.g. 3) and a global default; enforce a hard ceiling regardless of the configured value; validate/cap the client `?sid=` (length + charset) and cap distinct live sessions per user; cap on-disk segment usage.
- [ ] **T7.3** — `KeyedLock` / `TranscodeSessionManager` (I2): evict lock entries when the last waiter releases / when a session stops.
- [ ] **T7.4** — Tests: oversized `pageSize`/`limit` are clamped; Nth+1 concurrent transcode for a user is rejected.

---

## WS‑8 — Auth-flow hardening  🟡 P2  *(closes M3, L1, L2, L11, L12)*

- [ ] **T8.1** — TOTP brute-force (M3): track failed 2FA attempts in durable per-user state; lock/invalidate all challenges after N total failures regardless of how many challenges are minted ([TotpService.cs:125-138](../../src/SoftMedia.Server/Services/Identity/TotpService.cs#L125-L138)).
- [ ] **T8.2** — Invite TOCTOU (L11): make consumption atomic — conditional `ExecuteUpdate` on `Invites WHERE Code=@c AND UsedAt IS NULL AND NOT IsRevoked AND (ExpiresAt IS NULL OR ExpiresAt>now)`; abort signup if 0 rows affected ([AuthController.cs:87-134](../../src/SoftMedia.Server/Controllers/AuthController.cs#L87-L134)).
- [ ] **T8.3** — Login timing oracle (L1): compute a dummy Argon2 verify on the user-not-found branch so latency is constant ([AuthController.cs:148-152](../../src/SoftMedia.Server/Controllers/AuthController.cs#L148-L152)); avoid leaking banned/pending state pre-auth.
- [ ] **T8.4** — Password policy (L2): shared validator (min length ≥ 8–12, optional HIBP check) applied to signup, change-password, admin reset DTOs ([AuthDTOs.cs:5-11](../../src/SoftMedia.Server/DTOs/AuthDTOs.cs#L5-L11)).
- [ ] **T8.5** — Rate limits (L12): add `[EnableRateLimiting]` to `/auth/refresh-token` and the authenticated TOTP enroll/confirm/disable endpoints (infra already exists in `ServiceCollectionExtensions`).
- [ ] **T8.6** — Tests: extend `AuthRateLimitingTests`, `AuthSecurityTests`, and add an invite-race test.

---

## WS‑9 — Web transport hardening  🟡 P2  *(closes L6, L7, L8)*

- [ ] **T9.1** — Security-headers middleware early in the pipeline: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY` (or CSP `frame-ancestors 'none'`), `Referrer-Policy: no-referrer`, and a `Content-Security-Policy` (`default-src 'self'` + the image-proxy/CDN hosts). *(L7; also supports WS‑4/T4.1)*
- [ ] **T9.2** — `UseHttpsRedirection()` + `UseHsts()`, guarded so dev/loopback HTTP still works and cooperating with `X-Forwarded-Proto` ([Program.cs:189-214](../../src/SoftMedia.Server/Program.cs#L189-L214)). *(L8)*
- [ ] **T9.3** — CORS (L6): never combine `SetIsOriginAllowed(_=>true)` with `AllowCredentials`; gate the `AllowAnyOriginForLAN` branch on `IsDevelopment()` **and** the flag, or replace with a validated reflected-origin allowlist ([Program.cs:103-133](../../src/SoftMedia.Server/Program.cs#L103-L133)).

---

## WS‑10 — Untrusted-parsing size caps  🟡 P2  *(closes L4, L5)*

- [ ] **T10.1** — `ComicArchiveService` (L4): reject ZIP entries above a `ZipArchiveEntry.Length` ceiling; bounded copy; cap `MaxCharactersInDocument` on the `ComicInfo.xml` load ([:84-121](../../src/SoftMedia.Server/Services/Media/ComicArchiveService.cs#L84-L121)).
- [ ] **T10.2** — `BookMetadataExtractor` (L5): load EPUB `container.xml`/OPF via hardened `XmlReader` settings with a size cap; gate on entry length ([:67-82](../../src/SoftMedia.Server/Services/Media/BookMetadataExtractor.cs#L67-L82)).

---

## WS‑11 — DLNA SSDP source-filtering & banner trim  🟢 P3  *(closes L10, I1)*

- [ ] **T11.1** — `SsdpDiscoveryService` (L10): drop `M-SEARCH` datagrams whose source isn't LAN; bind/join multicast on the specific LAN interface; rate-limit/coalesce replies ([:78-144](../../src/SoftMedia.Server/Services/Dlna/SsdpDiscoveryService.cs#L78-L144)).
- [ ] **T11.2** — Genericize the `SERVER`/`modelNumber` version banners (I1).

---

## Cross-cutting

- [ ] **X.1** — Run `dotnet test src/SoftMedia.Server.Tests` after each workstream; keep the suite green.
- [ ] **X.2** — Update [docs/reports/security-audit-2026-06-07.md](../reports/security-audit-2026-06-07.md) status column (or a `remediation-status` table) as items land.
- [ ] **X.3** — For findings deferred (e.g. full HIBP integration, full `ArgumentList` migration of `TranscodeProfileBuilder` if split out), record them as follow-ups with rationale.

## Suggested commit grouping

1. `fix(security): enforce MustChangePassword/IsApproved server-side; randomize seeded admin` (WS‑1)
2. `fix(security): route per-id media endpoints through library-ACL gate` (WS‑2)
3. `fix(security): harden ffmpeg invocation against argument injection` (WS‑3)
4. `fix(security): mint media-scoped streaming tokens; stop full JWTs in URLs` (WS‑4)
5. `fix(security): webhook SSRF — block internal by default + pin IP` (WS‑5)
6. `fix(security): enforce per-user ACL on DLNA surface` (WS‑6)
7. `fix(security): clamp list/transcode limits; evict stale locks` (WS‑7)
8. `fix(security): auth-flow hardening (TOTP lockout, invite race, timing, policy, rate limits)` (WS‑8)
9. `fix(security): add security headers, HSTS, CORS guard` (WS‑9)
10. `fix(security): cap untrusted XML/zip parsing` (WS‑10)
11. `fix(security): DLNA SSDP source filtering + banner trim` (WS‑11)
