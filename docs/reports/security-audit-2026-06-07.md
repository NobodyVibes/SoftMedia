# SoftMedia Security Audit — 2026-06-07

**Scope:** Whole repository (`src/SoftMedia.Server` .NET 8 / ASP.NET Core backend + `src/SoftMedia.Client` React/TS SPA), on branch `security/hardening-wave-1`.
**Method:** Multi-agent review — 11 security domains hunted in parallel, then **every candidate finding adversarially verified** by an independent skeptic panel (3 verifiers for High/Critical, 1 for the rest) that re-read the cited code to refute or confirm. 67 agents total. Findings below are the **survivors** of that verification; 7 candidates were refuted as false positives and are listed at the end. The author also independently re-read the crown-jewel files (auth controller, token/identity services, process exec, file-serving controllers, SSRF guards, DI/pipeline setup) to confirm the headline issues.

**Threat model:** A self-hosted media server commonly exposed to the internet via reverse proxy + dynamic DNS (per its own docs). Attackers considered: (1) unauthenticated network/internet, (2) authenticated low-privilege user (self-registered via invite), (3) cross-user access (IDOR/BOLA), (4) malicious media files & embedded metadata, (5) malicious upstream metadata responses. Admin is the highest web privilege.

---

## Executive summary

SoftMedia is, for the most part, a **deliberately and competently hardened** codebase — this is clearly not a first security pass. The fundamentals are strong:

- **Passwords:** Argon2id with a random per-user salt and constant-time verify.
- **Tokens:** JWTs validate issuer/audience/lifetime/signing-key (symmetric key only, so HS/RS algorithm-confusion is structurally unreachable); the JWT secret is empty in committed config with a **startup validator that aborts boot** on a missing/short/blocklisted secret. Refresh tokens are 64 random bytes, stored SHA-256-hashed, with **rotation + reuse-detection** that revokes the whole chain on replay. API tokens are 30 random bytes, hashed at rest, scope-checked via a dedicated auth scheme. Cast tokens omit the role claim and are hard-scoped to a single media id and re-validated live every request.
- **Access control (the common case):** Per-library ACLs and content-rating ceilings are enforced centrally in the repository layer and `StreamSecurityService`, and the *streaming* path canonicalises paths **and resolves symlinks** before a library-root jail check. Object ownership (playlists, watchlist, bookmarks, API tokens, TOTP, webhooks) is consistently re-checked against the caller's claim, with 404-over-403 anti-probe responses. Admin surfaces are correctly gated with `[Authorize(Roles="Admin")]`, and there is no role/`userId` over-posting.
- **Outbound safety:** The image proxy and image cache use a **host allowlist with manual redirect re-validation on every hop** (auto-redirect disabled), plus size/content-type caps. SSRF redirect-chasing is explicitly defended.
- **Process exec:** `UseShellExecute=false` everywhere — there is **no OS shell injection**.
- **Web auth shape:** The access token is a Bearer header (not an ambient cookie), so the API is structurally CSRF-resistant; the one cookie (refresh) is HttpOnly + `SameSite=Lax` + path-scoped + Secure-on-HTTPS. The SignalR hub carries `[Authorize]` and re-checks library ACLs.

The residual risk therefore concentrates in **a handful of specific gaps**, the most serious being a classic default-credential problem and a small set of endpoints that bypass the otherwise-central access-control layer.

### Findings tally (post-verification)

| Severity | Count |
|---|---|
| **Critical** | 1 |
| **High** | 3 |
| **Medium** | 9 |
| **Low** | 12 |
| **Info** | 2 |

### Two systemic themes

1. **Security enforced in the SPA / repository layer but not re-enforced at every server entry point.** The default-password change, and the per-library ACL / content-rating ceiling, are correctly enforced *most* places — but a few controllers query `AppDbContext.MediaItems` directly instead of going through the ACL-aware repository, and `MustChangePassword` / `IsApproved` are enforced only client-side or only on *some* auth flows. Every confirmed access-control finding is an instance of this single theme.
2. **The DLNA/UPnP surface is a parallel, unauthenticated access path** that does not participate in the per-user ACL or rating system at all.

---

## Critical

### C1 — Default admin credentials (`admin` / `admin123`) are a live, full-privilege login; `MustChangePassword` is never enforced server-side
*Domain: authn + crypto · Reachability: unauthenticated · Panel: 3/3 confirmed (raised to Critical) · Files: [DbInitializer.cs:28-63](../../src/SoftMedia.Server/Data/DbInitializer.cs#L28-L63), [AuthController.cs:146-177](../../src/SoftMedia.Server/Controllers/AuthController.cs#L146-L177)*

`DbInitializer.InitializeAsync` runs on **every** startup (no `IsDevelopment()` guard) and, when no `admin` user exists, seeds `Username="admin"`, `PasswordHash=Hash("admin123")`, `Role=Admin`, `IsApproved=true`, `MustChangePassword=true`. The `MustChangePassword` flag is **only echoed into the response DTO** — no middleware, action filter, or JWT event ever rejects a request from a principal whose row has it set. `Login` checks ban/delete/approval/TOTP but never the flag, and unconditionally mints a full `Role=Admin` JWT.

Worse, the *existing-admin* branch ([DbInitializer.cs:29-42](../../src/SoftMedia.Server/Data/DbInitializer.cs#L29-L42)) only **re-asserts** `MustChangePassword=true` when the default password still verifies — it never rotates or disables the credential. So the default login stays valid until a human explicitly changes it.

**Exploit:** Against an internet-exposed instance, `POST /api/v1/auth/login {"username":"admin","password":"admin123"}` returns a fully privileged admin token, bypassing the SPA's (purely client-side) first-login password prompt. The auth rate limiter is irrelevant — the first guess succeeds. Full takeover: user management, settings, **backups (which can exfiltrate the DB and secrets)**.

**Fix (in priority order):** (a) Generate a random admin password at first boot and print it once to the console/log, *or* require a first-run setup that creates the admin — do not ship a fixed default. (b) Enforce `MustChangePassword` server-side via a global authorization filter that 403s every endpoint except `change-password` for such principals (and/or embed a `must_change` claim). (c) When the default password is detected on an existing admin, **invalidate** it rather than just re-flagging.

> Related dev-artifact (Low, same file): [DbInitializer.cs:65-101](../../src/SoftMedia.Server/Data/DbInitializer.cs#L65-L101) seeds a "Test Movies" library pointing at `C:\TestMedia` and writes a dummy `test_media.mkv` on every fresh prod DB. Remove or guard behind a dev/seed flag.

---

## High

### H1 — `MediaTracksController` bypasses the per-user library ACL: restricted users can read track metadata, true duration, and **extracted subtitle (dialogue) text** of media they are denied
*Domain: authz · Reachability: authenticated low-priv · Panel: 3/3 confirmed · File: [MediaTracksController.cs:38-185](../../src/SoftMedia.Server/Controllers/MediaTracksController.cs#L38-L185)*

Unlike the rest of the per-id media surface (which routes through `MediaRepository.GetByIdWithLibraryAsync` → `ApplyLibraryAccessFilter` + `ApplyContentRatingFilter`, and `StreamSecurityService.ValidateMediaAccessAsync`), this controller is decorated only with class-level `[Authorize]`, loads the item with a raw `_context.MediaItems.Include(m => m.Library)` query, and gates **only** on an LFI path-jail (`canonicalPath.StartsWith(library root)`). That jail is effectively a no-op for cross-library access — a scanned item's path is by construction under its own library's root, so it's always satisfied. The per-user `UserLibraryAccess` allow-list and content-rating ceiling are **never consulted**.

`GetSubtitle` spawns ffmpeg and returns the full extracted **WebVTT dialogue text** (a transcript) of the file; `GetTracks`/`GetDuration` run ffprobe and leak the audio/subtitle track structure and true runtime.

**Exploit:** A kid/guest account whose allow-list excludes the "Adults" library obtains a `MediaItem` GUID in that library (GUIDs leak via shared public playlists/collections/recommendations) and calls `GET /api/media/{id}/subtitles/0` — receiving the dialogue text of a title they were explicitly denied. Defeats both the per-library ACL and parental controls for that derived content. (It does **not** leak the A/V stream bytes — streaming stays gated — which is why this is High, not Critical.)

**Fix:** Replace the raw query with `IMediaRepository.GetByIdWithLibraryAsync(id)` and gate every action through `IStreamSecurityService.ValidateMediaAccessAsync`, mapping `FileNotFound`/`Unauthorized` → 404, exactly as `StreamController`/`BookController`/`TranscodeController` already do. (Note: replace the `Forbid()` at [MediaTracksController.cs:81](../../src/SoftMedia.Server/Controllers/MediaTracksController.cs#L81) with `NotFound()` to match the anti-probe convention.)

### H2 — ffmpeg/ffprobe **argument injection** via attacker-controlled media filename
*Domain: cmdinjection · Reachability: malicious file (Linux only) · Panel: 3/3 confirmed · Files: [TranscodeProfileBuilder.cs:153](../../src/SoftMedia.Server/Services/Transcoding/TranscodeProfileBuilder.cs#L153), and the same pattern in `MediaProbeService`, `SubtitleService`, `VideoPreviewService`, `TrickplayService`, `ChromaprintFingerprintExtractor`, `TranscodeDebugService`; plus [AudioStreamController.cs:128-196](../../src/SoftMedia.Server/Controllers/AudioStreamController.cs#L128-L196) (M2 below is the same class)*

Every ffmpeg/ffprobe command line is built as a single **interpolated string** assigned to `ProcessStartInfo.Arguments` (`-i "{inputPath}"`), not via `ArgumentList`. `inputPath` is the verbatim on-disk filename (set by the scanners from `file.FullName`); the only validation anywhere is an extension check. With `UseShellExecute=false`, .NET still re-tokenizes the `Arguments` string into `argv` (MSVCRT quoting rules) before `execvpe`. On Linux a filename may legally contain a double-quote, so a crafted name closes the `-i "..."` token early and **injects additional ffmpeg/ffprobe options** (`-map`, `-f`, `concat:`/`file:`/`lavfi` protocols).

A panel verifier reproduced .NET's exact Unix tokenizer and confirmed a path like `evil" -map 0 -f rawvideo /etc/cron.d/pwn #.mkv` splits into attacker-controlled argv elements.

**Impact:** This is **not** OS command injection (no shell; `;`/`|`/`$()` are inert) — it is argument injection bounded by ffmpeg's capabilities: **arbitrary file read** (add a second `-i` at `/etc/shadow` and mux it into a streamable output), **file write/overwrite** as the server account, and SSRF via ffmpeg network protocols. Reachability is high and partly **unauthenticated-by-proxy**: the scan-time `MediaProbeService` and the background `TrickplayWorker` run the same injectable ffprobe/ffmpeg automatically on any file dropped into a watched library, with no user interaction. **Windows hosts are not affected** (`"` is an illegal filename character); the documented production target is Linux.

**Fix:** Switch all process invocations to `ProcessStartInfo.ArgumentList` (one token per element — no string tokenization), and reject media paths containing control/quote characters as defense-in-depth.

### H3 — Full access-token JWT placed in URL query strings (leaks via proxy logs, browser history, `Referer`)
*Domain: client · Reachability: authenticated · Panel: 3/3 confirmed · Files: [mediaImageUrl.ts:22-31](../../src/SoftMedia.Client/src/lib/mediaImageUrl.ts#L22-L31) and `bookService.ts`, `useTrickplay.ts`, `VideoPlayer.tsx`; server lift at [ServiceCollectionExtensions.cs:77-98](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L77-L98)*

To authenticate `<img>`/`<video>`/HLS and other media requests, the SPA appends the **full Bearer JWT** as `?token=`/`?access_token=` on image/stream/book/music/trickplay/transcode/hub URLs, and the server lifts those query tokens for those route prefixes. Because the token is a full-privilege access JWT (carries `Role`, valid 15 min) and there is no `Referrer-Policy`, it lands in **internet-exposed reverse-proxy access logs**, browser history, and the `Referer` header on any outbound navigation.

**Exploit:** Anyone with proxy-log access (or a captured `Referer` from an outbound link click) replays the JWT as the victim for up to 15 minutes — as admin if the victim is admin.

**Fix:** Use the `Authorization` header for plain `fetch` calls. For media elements that can't set headers, mint a **short-lived, media-scoped streaming token** (the cast-token pattern already in the codebase) instead of the full access JWT; strip `token`/`access_token` from proxy logs; add `Referrer-Policy: no-referrer` and a CSP.

---

## Medium

### M1 — Public signup issues tokens immediately, bypassing the `IsApproved` approval gate
*authn · unauthenticated · [AuthController.cs:100-141](../../src/SoftMedia.Server/Controllers/AuthController.cs#L100-L141)* — With `AllowUserSignup=Enabled`, a self-registered user is created `IsApproved=false` but `Signup` still mints an access token + refresh cookie. `Login` and `Refresh` both reject unapproved users — proving approval is an intended precondition — so the signup response defeats it (and re-signup renews access within the 15/min cap). **Fix:** if the new user isn't `IsApproved`, return a pending-approval response without issuing tokens, mirroring the login-time check.

### M2 — `AudioStreamController` ffmpeg arg injection (same class as H2)
*cmdinjection · malicious file · [AudioStreamController.cs:128-196](../../src/SoftMedia.Server/Controllers/AudioStreamController.cs#L128-L196)* — Flattens a token list with `string.Join(" ", …)` into `Arguments` and literally quotes the input path. Same Unix quote-breakout as H2. **Fix:** assign the list to `ArgumentList` and drop the manual quoting. (Fold into the H2 remediation.)

### M3 — TOTP brute-force bounded only by a re-armable per-challenge rate limit
*authn · authenticated · [TotpService.cs:125-138](../../src/SoftMedia.Server/Services/Identity/TotpService.cs#L125-L138), [ServiceCollectionExtensions.cs:447-462](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L447-L462)* — The 6-digit code's only guard is 6 attempts / 5 min **partitioned per `challengeId`**, with no per-account lockout. A fresh `challengeId` is minted by re-calling `/auth/login`, so an attacker re-arms the 6-guess budget indefinitely (and can fan out across IPs) against the 10⁶ keyspace. **Fix:** track failed 2FA attempts in durable per-user state and lock/invalidate all challenges after N total failures, independent of how many challenges are minted.

### M4 — `MediaController.GetMediaItem` leaks full metadata across the library ACL and rating ceiling
*authz · cross-user IDOR · [MediaController.cs:38-67](../../src/SoftMedia.Server/Controllers/MediaController.cs#L38-L67)* — `GET /api/v1/media/{id}` loads the item with a raw `_context.MediaItems.Include(...)` and returns a full `MediaItemDto` (overview, cast, genres, series/album) without the ACL or content-rating filter that `IMediaRepository.GetByIdAsync` applies. Same root cause as H1. **Fix:** fetch through the ACL-aware repository and 404 when null.

### M5 — Webhook SSRF: internal/link-local targets allowed by default, reachable by any authenticated user
*ssrf · authenticated low-priv · [WebhookDispatcher.cs:55-76](../../src/SoftMedia.Server/Services/Infrastructure/WebhookDispatcher.cs#L55-L76), [WebhooksController.cs:46-74](../../src/SoftMedia.Server/Controllers/WebhooksController.cs#L46-L74)* — `WebhooksController` is `[Authorize]` (any user, **not** admin), and `WebhookSecurity.ValidateTarget` treats purely private/link-local targets as allowed (a deliberate LAN feature). But because link-local counts as "private", `169.254.169.254` (cloud-metadata) and any RFC1918 host pass. A low-priv user can register a webhook to an internal address and trigger delivery via `…/test`. This is **blind** SSRF (the worker delivers the POST; the response is not returned to the attacker), which is why it's Medium not High — but it still gives an unprivileged user an internal-network reach/port-probe primitive from the server. **Fix:** block private + link-local by default, with an explicit opt-in setting for LAN webhook targets.

### M6 — Webhook DNS-rebinding: validate-then-send with no IP pinning
*ssrf · authenticated · [WebhookDispatchWorker.cs:101-124](../../src/SoftMedia.Server/Services/Background/WebhookDispatchWorker.cs#L101-L124)* — The worker resolves the host to validate it, then hands the **hostname** to `HttpClient`, which resolves again at send time. A hostile DNS record that returns a public IP during validation and `127.0.0.1`/internal at send time bypasses the guard (auto-redirect is already disabled, but re-resolution is the gap). **Fix:** pin the validated IP via `SocketsHttpHandler.ConnectCallback` so the connection uses the exact address that passed validation.

### M7 — DLNA exposes the entire A/V library, bypassing per-user ACL and rating ceilings, to any unauthenticated LAN device
*dlna · unauthenticated LAN · [DlnaContentDirectory.cs:60-139](../../src/SoftMedia.Server/Services/Dlna/DlnaContentDirectory.cs#L60-L139), [DlnaController.cs:134-152](../../src/SoftMedia.Server/Controllers/DlnaController.cs#L134-L152)* — DLNA is gated opt-in (`EnableDlna`, default off) + LAN-only, but the content directory queries `_db.Libraries`/`_db.MediaItems` directly with **no** ACL or rating filter. Once an admin enables it, any UPnP device on the LAN (guest Wi-Fi, a child's tablet, an IoT device) can browse and stream **every** library — including ones restricted to specific users and adult/restricted-rating titles. This is partly by-design for DLNA (TVs can't log in), but it silently defeats SoftMedia's own parental controls. **Fix:** make DLNA a privileged surface — an admin opt-in that designates *which* libraries and a maximum content rating are DLNA-exposed (default none); filter the browse/stream queries by that set.

### M8 — Unbounded `pageSize` enables single-request memory exhaustion
*dos · authenticated low-priv · [LibraryRepository.cs:231-235](../../src/SoftMedia.Server/Services/Infrastructure/LibraryRepository.cs#L231-L235) ← [LibrariesController.cs:137-167](../../src/SoftMedia.Server/Controllers/LibrariesController.cs#L137-L167)* — `GET /api/v1/libraries/{id}/items?pageSize=…` flows unclamped into `.Take(pageSize)` over a query that eagerly `.Include()`s Series/Album/Genre. `pageSize=100000000` makes EF hydrate the whole library into memory; a few concurrent requests OOM the host. **Fix:** `pageSize = Math.Clamp(pageSize, 1, 100)` server-side (the `WatchlistController` clamp pattern), ideally via a shared helper across all paged endpoints.

### M9 — Unlimited concurrent transcodes per user (default cap 0) + arbitrary `sid` ⇒ CPU/disk exhaustion
*dos · authenticated low-priv · [TranscodeService.cs:208-229](../../src/SoftMedia.Server/Services/Transcoding/TranscodeService.cs#L208-L229), [TranscodeController.cs:135-152](../../src/SoftMedia.Server/Controllers/TranscodeController.cs#L135-L152)* — `MaxSimultaneousTranscodes` and `…PerUser` default to `"0"` = unlimited, and the session key includes the **raw client-supplied `?sid=`**, so the de-dup never triggers. A loop of `GET /api/transcode/{id}/master.m3u8?sid=<random>` spins up an independent ffmpeg encode each time (each writing a 24-hour segment folder). **Fix:** ship a non-zero default per-user cap (e.g. 3), enforce a hard ceiling regardless of config, validate/cap `sid`, and bound on-disk segment usage.

### M10 — Unbounded `limit` multiplier in recent-media / global-search
*dos · authenticated low-priv · [MediaController.cs:103-170](../../src/SoftMedia.Server/Controllers/MediaController.cs#L103-L170) → MediaRepository `Take(limit*25)` / `Take(limit*5)`* — `limit` is forwarded unclamped, then multiplied (×25 / ×5) into `.Take()` over `.Include()`-heavy queries — the inline comment even claims it's "bounded." `?limit=5000000` requests ~125M rows. **Fix:** `Math.Clamp(limit, 1, 100)` before the multiplication.

---

## Low

| ID | Finding | Location | Note |
|---|---|---|---|
| L1 | **Username enumeration via login timing oracle** — `user == null \|\| !VerifyPassword(...)` short-circuits, so Argon2id (64 MB, t=4) is skipped for unknown usernames; fast vs slow response reveals account existence. | [AuthController.cs:148-152](../../src/SoftMedia.Server/Controllers/AuthController.cs#L148-L152) | Compute a dummy Argon2 verify on the not-found branch; keep ban/pending state out of pre-auth responses. |
| L2 | **No password length/complexity policy** anywhere (signup, change-password, admin reset accept empty/1-char). | [AuthDTOs.cs:5-11](../../src/SoftMedia.Server/DTOs/AuthDTOs.cs#L5-L11) | Shared validator: min length + optional HIBP breached-password check. |
| L3 | **Cover-art / trickplay image endpoints bypass the library ACL** (image-only sibling of H1/M4) — serve artwork & scrubber thumbnails for denied libraries. | [MusicController.cs:42-123](../../src/SoftMedia.Server/Controllers/MusicController.cs#L42-L123), [AudioController.cs:32-97](../../src/SoftMedia.Server/Controllers/AudioController.cs#L32-L97), [TrickplayController.cs:21-37](../../src/SoftMedia.Server/Controllers/TrickplayController.cs#L21-L37) | Route through `ValidateMediaAccessAsync`; 404 on Unauthorized. |
| L4 | **CBZ/CBR page & ComicInfo.xml zip-bomb** — entry copied to `MemoryStream` with no uncompressed-size cap (XXE itself is blocked by the `DtdProcessing.Prohibit` default). | [ComicArchiveService.cs:84-121](../../src/SoftMedia.Server/Services/Media/ComicArchiveService.cs#L84-L121) | Reject entries above a `ZipArchiveEntry.Length` ceiling; cap `MaxCharactersInDocument`. |
| L5 | **EPUB OPF/container `XDocument.Load` with no size cap** — small-compressed XML inflates to a huge tree during scan. | [BookMetadataExtractor.cs:67-82](../../src/SoftMedia.Server/Services/Media/BookMetadataExtractor.cs#L67-L82) | Load via hardened `XmlReader` settings; gate on entry length. |
| L6 | **Credentialed CORS reflects any origin** when `Cors:AllowAnyOriginForLAN=true` (`SetIsOriginAllowed(_=>true)` + `AllowCredentials`) — the Development default and a production footgun. | [Program.cs:103-133](../../src/SoftMedia.Server/Program.cs#L103-L133) | Never combine reflect-any-origin with credentials; gate the LAN branch on `IsDevelopment()` *and* the flag, or use a validated allowlist. |
| L7 | **No HTTP security-response headers** at all — no HSTS, CSP, `X-Content-Type-Options`, `X-Frame-Options`/`frame-ancestors`, or `Referrer-Policy`, despite serving the SPA via `UseStaticFiles`. | [Program.cs:189-214](../../src/SoftMedia.Server/Program.cs#L189-L214) | Add a security-headers middleware early in the pipeline. (Also mitigates H3's `Referer` leak and adds clickjacking defense.) |
| L8 | **No HTTPS redirection / HSTS** — a plain-HTTP request issues the refresh cookie without `Secure`. | [Program.cs:189-214](../../src/SoftMedia.Server/Program.cs#L189-L214) | `UseHttpsRedirection()` + `UseHsts()` (cooperating with `X-Forwarded-Proto`); refuse refresh cookies over non-loopback HTTP. |
| L9 | **DLNA media endpoint serves any item by GUID without restricting to A/V types** — can hand back a private PDF/ebook with no auth. | [DlnaController.cs:134-152](../../src/SoftMedia.Server/Controllers/DlnaController.cs#L134-L152) | After load, require an A/V type within the DLNA-exposed set; else 404. (Fold into M7.) |
| L10 | **SSDP responder is an unvalidated UDP reflection/amplification participant** — binds `0.0.0.0:1900`, no source-IP check, emits 5 replies per `M-SEARCH`. | [SsdpDiscoveryService.cs:78-144](../../src/SoftMedia.Server/Services/Dlna/SsdpDiscoveryService.cs#L78-L144) | Drop datagrams whose source isn't LAN; bind to the specific interface; rate-limit/coalesce replies. |
| L11 | **Invite consumption TOCTOU** — the used-check and used-stamp aren't atomic, so N parallel `signup` calls with one invite code all succeed. | [AuthController.cs:87-134](../../src/SoftMedia.Server/Controllers/AuthController.cs#L87-L134) | Atomic conditional `UPDATE … WHERE Code=@c AND UsedAt IS NULL` and abort if 0 rows affected. |
| L12 | **No rate limit on `/auth/refresh-token` or authenticated TOTP enroll/confirm/disable** — each refresh forces a DB round-trip + hash verify + rotation, uncapped. | [AuthController.cs:285-359](../../src/SoftMedia.Server/Controllers/AuthController.cs#L285-L359), [AccountController.cs:185-231](../../src/SoftMedia.Server/Controllers/AccountController.cs#L185-L231) | Add modest `[EnableRateLimiting]` policies (the infra already exists). |

---

## Info / hardening

- **I1 — DLNA banners disclose `SoftMedia/1.0`** in SSDP `SERVER` headers and device descriptions, easing version fingerprinting. [SsdpDiscoveryService.cs:152](../../src/SoftMedia.Server/Services/Dlna/SsdpDiscoveryService.cs#L152), [DlnaDescriptions.cs:17-20](../../src/SoftMedia.Server/Services/Dlna/DlnaDescriptions.cs#L17-L20). Genericize the banner.
- **I2 — `KeyedLock` and transcode session-lock semaphores are never evicted** — a small steady memory/handle leak that compounds the M9 unlimited-`sid` abuse. [KeyedLock.cs:34-49](../../src/SoftMedia.Server/Helpers/KeyedLock.cs#L34-L49), [TranscodeSessionManager.cs:37-40](../../src/SoftMedia.Server/Services/Transcoding/TranscodeSessionManager.cs#L37-L40). Use a ref-counted keyed lock; remove a session's lock entry on stop.
- **Observed but write-side only (note, not a numbered finding):** `BookController.CreateBookmark`/`CreateHighlight` gate on `MediaItems.AnyAsync(id)` (existence) rather than `ValidateMediaAccessAsync` — the same "existence-only check" pattern as H1/M4, but write-side and per-user, so impact is limited to attaching one's own annotation to an unguessable GUID. Worth aligning for consistency. [BookController.cs:217](../../src/SoftMedia.Server/Controllers/BookController.cs#L217), [:314](../../src/SoftMedia.Server/Controllers/BookController.cs#L314).

---

## Candidates investigated and **refuted** (false positives)

The verification panel killed these — included to show what was checked and *why it's safe*, so they aren't re-raised:

1. **"LFI in `MediaTracksController`"** — the divergent path check is real, but the route input is a `Guid` id, not a path, so traversal is unreachable. (The *ACL bypass* via the same code is real — see H1.)
2. **"CBZ page-sorter `long.Parse` overflow"** — reachable but throws a caught exception → at most a single failed page render, not a crash.
3. **"DLNA SOAP reads unbounded request body"** — Kestrel's default 30 MB `MaxRequestBodySize` bounds it; no amplification.
4. **"TOTP secrets use unauthenticated AES-CBC"** — the ciphertext is only ever written server-side (no endpoint accepts attacker ciphertext), so the bit-flip/oracle premise doesn't hold.
5. **"BREACH via response compression of authenticated JSON"** — the compression surface exists, but auth is a Bearer header an attacker can't set cross-site, so the secret can't be reflected into a victim-driven compressed response. Not exploitable.
6. **"Prod CORS hardcodes dev localhost origins"** — true but impact ≈ nil (an attacker can't host on the victim's `localhost:5173`).
7. **"Access-token JWT in `localStorage`"** — factually accurate (zustand `persist` → `localStorage`, attached as Bearer); the panel rated it a defense-in-depth concern rather than a standalone vuln **because there is no XSS sink in the SPA** (no `dangerouslySetInnerHTML`/`eval`). Still worth moving to an in-memory token + httpOnly refresh as hardening, and it raises the stakes on the missing CSP (L7). *(The genuinely exploitable token-exposure issue is H3 — tokens in URLs — which is confirmed.)*

---

## Prioritized remediation roadmap

1. **Now (Critical/High):**
   - C1 — kill the default credential (random first-boot password + server-side `MustChangePassword` enforcement); remove the prod test-library seed.
   - H1/M4/L3 — route `MediaTracksController`, `MediaController.GetMediaItem`, and the cover-art/trickplay endpoints through the ACL-aware repository + `ValidateMediaAccessAsync`. *(One systemic fix; consider a global filter or analyzer so new controllers can't repeat it.)*
   - H2/M2 — convert all ffmpeg/ffprobe invocations to `ArgumentList`; reject quote/control chars in media paths.
   - H3 — stop putting the full JWT in URLs; mint short-lived media-scoped streaming tokens; add `Referrer-Policy`.
2. **Next (Medium):** M1 (signup approval), M3 (TOTP lockout), M5/M6 (webhook SSRF + IP pinning), M7 (DLNA ACL), M8–M10 (clamp all list/transcode limits — one shared helper).
3. **Hardening (Low/Info):** security-headers middleware (L7, also helps H3) + HTTPS redirect/HSTS (L8); CORS branch guard (L6); password policy + login-timing fix (L1/L2); zip-bomb caps (L4/L5); invite atomicity (L11); rate-limit refresh/TOTP (L12); SSDP source filtering (L10); banner/lock cleanup (I1/I2).

---

## Methodology & limitations

- **Coverage:** 11 domains — authentication, authorization/IDOR, path traversal, command/process injection, SSRF, untrusted parsing (XXE/zip/ReDoS), crypto/secrets, transport/web (CORS/CSRF/headers/SignalR), DLNA/unauthenticated surfaces, DoS/rate-limiting, and frontend. Each finder read its target files and traced data flow; each candidate was then re-verified by an independent panel that re-read the code (3 skeptics for High/Critical with majority vote, 1 otherwise).
- **This is a source review, not a live pentest.** No exploits were executed against a running instance (the ffmpeg tokenizer claim was reproduced offline by a verifier). Findings should be confirmed against the specific deployment.
- **Out of scope / not exhaustively covered:** dependency CVEs (no SCA run), the EF migration history, business-logic abuse beyond access control, and physical/host security. EF Core is used throughout with parameterized queries — no SQL injection surface was found (and none claimed).
- **Generated with multi-agent assistance and human-verified on the highest-severity items.** Treat severities as calibrated estimates for the documented self-hosted-behind-reverse-proxy threat model; re-rate for your exposure (e.g. LAN-only deployments downgrade the unauthenticated-network findings).
