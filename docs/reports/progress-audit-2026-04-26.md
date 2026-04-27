# SoftMedia Progress Audit — 2026-04-26

**Author:** Senior engineering review (initial pass)
**Branch reviewed:** `security/hardening-wave-1`
**Reference docs:** [SDD.md](../SDD.md), `docs/rules/*`, `docs/user-docs/features/*`

This report measures the current state of SoftMedia against its stated goal — a self-hosted, privacy-first media server that a hobbyist can run at home for free — and flags spec divergences, gaps, and code quality concerns. A second agent has been asked to verify and deepen the findings; their report sits next to this one.

---

## 1. Headline assessment

SoftMedia is **mid-build, well past prototype**. The backend has substantive coverage of the core media-server problem: scanners for Movie/TV/Music/Book/Game, a metadata-aggregation pipeline routed by library type, FFmpeg-driven probe + HLS transcode + range-streaming endpoints, persistent audio player wiring, image proxying with SSRF allow-listing, JWT + rotating refresh-token auth with reuse detection, an admin/invite/approval flow, EF Core migrations covering ~60 schema revisions, and a real React client with detail views, a player, and a reader. There is enough in place that a single user could plausibly run a private library today.

What is missing is not so much **breadth** (most pillars are scaffolded) as **enforcement, finishing, and security depth**. The most significant gap is that the parental-control feature called out as a non-negotiable in SDD §4.2 — middleware that filters media metadata and streams by `User.MaxRating` / `User.ContentRatings` — does not exist in code. Several hosted background services that the SDD describes (HLS segment cleanup, Photos/EXIF library type) are also absent, and a few security choices contradict the rules document with only an inline justification.

---

## 2. Scope of investigation

Files actually opened during this pass:

- [docs/SDD.md](../SDD.md) — the spec being measured against (full read).
- [src/SoftMedia.Server/Program.cs](../../src/SoftMedia.Server/Program.cs) — DI graph, pipeline order, JWT validation entry.
- [src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs) — service registration + rate limiter policies.
- [src/SoftMedia.Server/Controllers/AuthController.cs](../../src/SoftMedia.Server/Controllers/AuthController.cs) — login/signup/refresh/change-password flow.
- [src/SoftMedia.Server/Controllers/SettingsController.cs](../../src/SoftMedia.Server/Controllers/SettingsController.cs) — admin settings surface.
- [src/SoftMedia.Server/Controllers/StreamController.cs](../../src/SoftMedia.Server/Controllers/StreamController.cs) — primary video streaming endpoint.
- [src/SoftMedia.Server/Controllers/ImageController.cs](../../src/SoftMedia.Server/Controllers/ImageController.cs) — outbound image proxy + cache.
- [src/SoftMedia.Server/Services/Security/StreamSecurityService.cs](../../src/SoftMedia.Server/Services/Security/StreamSecurityService.cs) — library-jail path check.
- [src/SoftMedia.Server/Models/User.cs](../../src/SoftMedia.Server/Models/User.cs) — user shape vs. SDD §4.1.
- Directory listings of `Controllers/`, `Services/`, `Migrations/`, `Models/`, frontend `pages/` and `components/`.

Cross-cutting greps were also run for `MaxRating` enforcement, `Photos`/`EXIF` scanning, `TODO`/`FIXME`/`NotImplementedException`, and HLS cleanup keywords.

---

## 3. Spec divergences (highest priority)

### 3.1 Parental controls are a stub — no enforcement anywhere
SDD §4.2 states: *"Middleware checks `User.Role` and `User.MaxRating` before serving media metadata or streams. Child accounts cannot see content above their rating."*

The data model implements `User.MaxRating` and a JSON `User.ContentRatings` per-type map ([User.cs:25-28](../../src/SoftMedia.Server/Models/User.cs#L25-L28)), and `User.ParentId` exists, and individual metadata providers populate `MediaItem.ContentRating` (e.g. [WikidataProvider.cs:85](../../src/SoftMedia.Server/Services/Metadata/WikidataProvider.cs#L85), [OMDbProvider.cs:388](../../src/SoftMedia.Server/Services/Metadata/OMDbProvider.cs#L388), [ComicInfoXmlProvider.cs:177](../../src/SoftMedia.Server/Services/Metadata/ComicInfoXmlProvider.cs#L177)). But:

- A grep for any `Where(... rating ...)` / `MaxRating` reference inside `Services/Media/` and `Services/Infrastructure/MediaRepository.cs` returns nothing. Listings are not filtered by rating.
- [StreamController.cs](../../src/SoftMedia.Server/Controllers/StreamController.cs) only checks `[Authorize]` and the library-path jail. A child account can request `GET /api/v1/Stream/{id}` for an R-rated movie and will receive bytes if the JWT is valid.
- `Role` exists but only `Admin`-vs-`User` is gated — there is no "Child" role nor a downstream check.

**Severity:** High. This is called out as a non-negotiable in the SDD and is a privacy/family-safety promise the README/marketing implies. Reviewer should dig into how invasive the fix is — likely an `IAuthorizationFilter` or a query-side `IMediaQueryFilter` injected into `MediaRepository`.

### 3.2 No HLS segment cleanup background worker
SDD §4.5 explicitly mandates: *"Background service must aggressively clean up old segments to save disk space."*

Background services registered in [ServiceCollectionExtensions.cs:263-297](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L263-L297) are: `LibraryScanQueueService`, `LibraryWatcher`, `ThrottleMonitorService`, `RefreshTokenCleanupService`, `MetadataRefreshService`, `HeroCacheWorker`, `MetadataQueueService`, `MetadataRetryService`, `ImageDownloadQueueService`. No transcode-temp janitor. The only cleanup-related text inside `Services/Transcoding/` is a logged warning at [TranscodeService.cs:759](../../src/SoftMedia.Server/Services/Transcoding/TranscodeService.cs#L759). For a self-hosted product where the temp dir defaults to `./transcode-temp`, this is the kind of issue that bites users a week after install when their drive fills up.

**Severity:** High for the "lived-in install" reliability story.

### 3.3 Photos library type / EXIF flow is half-built
SDD §4.1 lists Photos as a first-class type with EXIF-derived metadata (`CameraModel`, `FNumber`, `GPSLatitude`, etc.).

Implemented:
- `ExifMetadataProvider` is registered as an `IMetadataProvider`.

Missing:
- No `PhotoScanner` (`Services/Scanning/` has Movie/Tv/Music/Book/Game scanners only).
- A grep across `Models/` and `Services/Scanning/` for "Photo" / "EXIF" returns nothing user-facing.
- Frontend has no Photo grid/page.

**Severity:** Medium — this is a roadmap item more than a regression.

### 3.4 Refresh cookie deviates from `SameSite=Strict`
The project context document explicitly mandates "JWT access token + HttpOnly/SameSite=Strict refresh cookie." [AuthController.cs:322-329](../../src/SoftMedia.Server/Controllers/AuthController.cs#L322-L329) sets `SameSite=Lax` instead, with an inline comment explaining that Strict broke against Vite's dev proxy. The reasoning is defensible (OAuth 2.1 / OWASP guidance allows Lax for refresh on same-origin POST), but the rules-doc text has not been updated — code and rules disagree.

**Severity:** Low (defensible technical choice, documentation drift) but worth resolving so future contributors do not re-litigate.

### 3.5 Auth rate-limit numbers do not match their own comment
[ServiceCollectionExtensions.cs:194](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L194) comment says *"15 attempts per minute is comfortable headroom"*; the code at [line 236](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L236) sets `PermitLimit = 30`. Cosmetic but the kind of drift reviewers catch.

**Severity:** Trivial.

---

## 4. Implementation breadth — what is solid

To balance the negatives: the following are in genuinely good shape.

- **Auth chain.** Argon2id hashing (`PasswordHasher`), short-lived JWT + rotating refresh tokens with reuse detection ([AuthController.cs:212-223](../../src/SoftMedia.Server/Controllers/AuthController.cs#L212-L223)), per-IP sliding-window rate limit on signup/login, refresh-token revocation on password change. A `RefreshTokenCleanupService` is registered.
- **Library jail.** [StreamSecurityService.cs:24-44](../../src/SoftMedia.Server/Services/Security/StreamSecurityService.cs#L24-L44) canonicalizes via `Path.GetFullPath`, appends `DirectorySeparatorChar` before `StartsWith` to defeat partial-prefix matches (e.g. `/media/movies-secret` vs `/media/movies`). Returns 404 (not 403) so a holder of a stolen JWT cannot probe for IDs.
- **Image proxy.** [ImageController.cs:35-45](../../src/SoftMedia.Server/Controllers/ImageController.cs#L35-L45) pins outbound fetches to an SSRF allow-list, validates content-type, enforces a 10 MB size cap with a streaming check (so a chunked response declaring small `Content-Length` cannot smuggle in a giant body), uses a negative-cache sentinel to suppress retries against 404s, and bounds concurrency to 8 with a `SemaphoreSlim`.
- **Metadata routing.** Per-type provider locking is in place (`MetadataRouter`, `MetadataAggregator`), with `RateLimitingDelegatingHandler` and `SoftMediaUserAgentHandler` wrapped around every external HTTP client (SDD §4.3 compliance scaffolding). Retry queue + provider cache + image-download queue are all hosted services.
- **EF Core schema evolution** is disciplined — ~70 sequential migrations including normalization passes (`DropMetadataJson`, `NormalizeGenresAndCast`, `PromoteMetadataColumns`).
- **Tests exist.** 50 server `.cs` test files, including new `IntegrationTestBase` + `SoftMediaWebApplicationFactory`, plus dedicated `AuthRateLimitingTests`, `AuthControllerRefreshTests`, `ControllerAuthorizationTests`. Frontend has 14 `.test.*` plus an `a11yGuards.test.ts`. This is far better discipline than typical hobbyist projects.

---

## 5. Code quality / hygiene findings

### 5.1 Test directory is a temp-file dumping ground
`src/SoftMedia.Server.Tests/` contains 17 root-level scratch files: `build.log`, `build_error.log`, `build_log.txt`, `build_log_full.txt`, `build_log_utf8.txt`, `errors.log`, `test_build_log*.txt`, `test_log*.txt`, `test_log_2..6.txt`, `test_run_debug*.txt`, `test_run_fixed*.txt`, `test_run_log*.txt`, `test_run_nofilter*.txt`. These are checked-in. They're noise for anyone navigating the repo.

**Action:** delete and add `*.log`, `test_*.txt`, `build_*.txt` to `.gitignore`.

### 5.2 In-line "thinking-out-loud" comments left in production code
[ServiceCollectionExtensions.cs:170-180](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L170-L180) is a paragraph of stream-of-consciousness commentary ending in *"Actually, wait: ImageCacheService downloads from MANY hosts. ... Given the complexity, ... using the TVMaze limiter for *all* image downloads is a safe starting point."* This is thinking notes, not docs. Either pick the design and write a short why, or open a TODO with a tracking ID.

### 5.3 `Program.cs` mixes per-service `AddScoped` calls with extension registrations
[Program.cs:40-47](../../src/SoftMedia.Server/Program.cs#L40-L47) pulls eight individual `AddScoped` lines for security/library/media services into the entry point, after the `AddIdentityServices`/`AddMediaServices`/`AddBackgroundServices` extensions ran. This is the kind of drift that breaks the layering rule (`No static global state; use the DI container`) less than it suggests, but it does mean the extension methods are not the single source of truth for the DI graph. Fold these into the appropriate extension.

### 5.4 `SettingsController` is a generic key/value bucket
[SettingsController.cs:26-37](../../src/SoftMedia.Server/Controllers/SettingsController.cs#L26-L37) returns and accepts `List<AppSetting>` with no validation against the SDD §7.2 settings tree. A misspelled key (e.g. `EnableRemoteAcess`) silently writes a new row. This is fine for v1 dev velocity, but worth typing once the tree stabilises — even a hard-coded `HashSet<string>` of allowed keys would prevent stale/orphaned rows that the migration `CleanupOrphanedSettings` already had to fix once.

### 5.5 Path canonicalisation does not resolve symlinks
[StreamSecurityService.cs:24](../../src/SoftMedia.Server/Services/Security/StreamSecurityService.cs#L24) uses `Path.GetFullPath`, which collapses `..` but does **not** resolve symlinks. On Linux (an OS the SDD claims to support), an admin who unknowingly adds a library root containing a symlink can re-introduce LFI. `FileInfo.ResolveLinkTarget(true)` exists in .NET 6+ for this. Reviewer should evaluate.

### 5.6 Modified-but-uncommitted surface is large and crosses concerns
`git status` shows 53 modified files on a "security/hardening-wave-1" branch, plus dozens of untracked new files spanning reader features (TTS, EPUB, bookmarks, highlights, dictionary, search drawer), refresh-token infra, and integration test scaffolding. Mixing a security wave with a sizable reader feature drop will make the PR very hard to review. Recommend splitting before merge.

---

## 6. Summary of gaps vs. SDD checklist

| SDD section | Item | Status |
|---|---|---|
| §4.1 | User: ParentId, MaxRating, ContentRatings | Stored ✓ — Enforced ✗ |
| §4.1 | Photos library + EXIF metadata | Provider exists, scanner missing ✗ |
| §4.2 | Argon2id, JWT + refresh cookie | ✓ |
| §4.2 | Parental-control middleware | ✗ |
| §4.3 | Provider type-locking, User-Agent, rate limiting | ✓ |
| §4.5 | Range-request video / audio streaming | ✓ |
| §4.5 | HLS transcoding (`.m3u8`/`.ts`) | ✓ (TranscodeService) |
| §4.5 | HLS segment cleanup background service | ✗ |
| §4.5 | CBZ/EPUB page API | ✓ (BookController + ComicArchiveService) |
| §6.1 | Tailscale/DuckDNS guidance docs | Not yet shipped to users |
| §6.2 | Login/signup rate limiting, sanitization, jail | ✓ |
| §6.2 | CSRF double-submit cookie | Not visible in code (worth reviewer follow-up) |
| §7.2 | Settings tree validated against schema | ✗ (generic KV) |
| §8.x | Universal-client a11y rules | Partial — `a11yGuards.test.ts` exists, but every modified `*.tsx` in this branch needs review |

---

## 7. Recommended next-step priority order

1. **Implement parental-control filtering.** Inject a query filter into `MediaRepository`/list endpoints that strips items above the caller's `MaxRating`/`ContentRatings`, and short-circuit `StreamController` for unauthorized streams. Add tests.
2. **Add an HLS-temp janitor `IHostedService`.** Walks `transcode-temp`, evicts segments whose owning session was closed > N minutes ago, and runs every couple of minutes.
3. **Resolve the `SameSite` rule/code disagreement.** Either update the rules doc or escalate the cookie back to Strict with a separate dev-mode shim.
4. **Verify CSRF double-submit cookie pattern.** SDD §6.2 calls for it; reviewer should confirm whether it was implicitly dropped because Bearer-in-header replaces same-site form posts (defensible) or genuinely missing.
5. **Clean repo hygiene.** Remove the 17 stale log files, gitignore the patterns, fold `Program.cs` DI lines into the extensions, prune the verbose comment in `AddMediaServices`.
6. **Split the current branch before PR.** Security hardening, refresh-token rework, and the reader feature drop are three separate stories.

---

## 8. Peer review handoff

This document has been handed to a second agent (`feature-dev:code-reviewer`) with explicit instructions to:

- Confirm or rebut each spec divergence in §3 by reading the referenced code rather than trusting the claim.
- Drill into security findings in §3.4, §5.5, and §6 row "CSRF" with concrete attack scenarios.
- Audit the modified frontend files on this branch against the universal-client a11y rules in §8.3 of the SDD.
- Cite `file_path:line_number` for every confirmed or new issue.

Their report is committed alongside this one.
