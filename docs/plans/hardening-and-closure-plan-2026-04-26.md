# SoftMedia Hardening & Closure — Implementation Plan

**Author:** Senior engineering plan, derived from the 2026-04-26 audit + peer review
**Inputs:** [progress-audit-2026-04-26.md](../reports/progress-audit-2026-04-26.md), [progress-audit-peer-review-2026-04-26.md](../reports/progress-audit-peer-review-2026-04-26.md)
**Status:** Ready for execution
**Owner:** Project maintainer
**Branch posture:** Each wave is its own branch + PR. The current `security/hardening-wave-1` branch must be split (see Wave E, item E4) before any of this lands.

---

## 0. Overview & ground rules

This plan turns the audit and peer-review findings into concrete, sequenced work. It is organised into six **waves**, each scoped to a single PR. Waves are ordered by priority and by dependency: A and F land first because they unblock everything else. Each work item lists exact files to touch, the change to make, the tests to add, and an acceptance criterion that a reviewer can check.

The SDD (`docs/SDD.md`) is treated as authoritative but mutable. Where the audit identified that the spec disagrees with a decision the team has already made (e.g. `SameSite=Lax` for the refresh cookie), the SDD has been updated as part of Wave F so the rules-doc and the code stay in sync. Where a finding is a missing implementation rather than a spec disagreement, the SDD remains the standard the code must meet.

**Scope discipline reminder:** Each wave is a focused PR. Do not bundle housekeeping into the security wave, and do not bundle a11y into infrastructure. The peer review explicitly flagged that the current branch already mixes security hardening with a substantial reader feature drop — that gets split (E4) before merge.

### Waves at a glance

| Wave | Theme | Severity | Est. effort | Blocks |
|------|-------|----------|-------------|--------|
| **A** | Critical security patches | High | 1–2 days | Public release |
| **B** | Parental controls — enforcement layer | High | 3–5 days | Family-use marketing claim |
| **C** | Infrastructure & reliability | Medium | 2–3 days | Lived-in install reliability |
| **D** | Frontend a11y closure | Medium | 2–3 days | Universal-client / WebOS target |
| **E** | Repo hygiene & branch split | Low | 1 day | Reviewability of all other waves |
| **F** | SDD spec alignment | Low (docs) | applied with this plan | Future contributor coherence |

Recommended landing order: **F → A → E → B → C → D**. F and A together unblock review of everything else; E enables clean PRs; B is the longest-running work and can run in parallel with C and D once A is in.

---

## Wave A — Critical security patches

Single PR. Target: 1–2 days. Each item below is a separate commit inside that PR so reviewers can step through them.

### A1. Patch the frame-preview authentication bypass

**Severity:** High (peer review NEW-1 — confirmed authentication bypass).

**Where:** [src/SoftMedia.Server/Controllers/TranscodeController.cs:241-272](../../src/SoftMedia.Server/Controllers/TranscodeController.cs#L241-L272).

**Problem:** The action accepts `?token=` and validates it with `JwtSecurityTokenHandler.ReadJwtToken` at line 251. `ReadJwtToken` is a decode-only method — it does not verify the HMAC signature. `[Authorize]` is missing on the action. A forged JWT with any `sub` claim returns frames.

**Change:**
1. Add `[Authorize]` to the action attribute set (line 241).
2. Delete the bespoke token-reading block at lines 246-259 entirely. The standard `JwtBearerEvents.OnMessageReceived` hook in [ServiceCollectionExtensions.cs:46-65](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L46-L65) already lifts `?token=` for `/api/transcode/*` and `[Authorize]` will then run the full validation chain.
3. Remove the `string? token = null` parameter — once `[Authorize]` is in place the parameter is no longer read by the action body.
4. Remove the now-unused `using System.IdentityModel.Tokens.Jwt;` and `using System.Security.Claims;` imports if no other action references them.

**Tests:**
- New test in `src/SoftMedia.Server.Tests/Controllers/` — `TranscodeControllerFramePreviewAuthTests.cs`:
  - Forged token with valid shape but wrong signing key returns `401`.
  - Expired-but-otherwise-valid token returns `401`.
  - Missing token returns `401`.
  - Valid signed token returns `200` with image bytes.

**Acceptance:** A request to `GET /api/transcode/{id}/frame?time=0` with a forged HS256 JWT (signed with any key other than `JwtSettings:Secret`) returns 401, not the frame bytes.

### A2. Resolve symlinks in `StreamSecurityService.IsPathAuthorized`

**Severity:** High on Linux deployments (peer review §3, Symlink LFI).

**Where:** [src/SoftMedia.Server/Services/Security/StreamSecurityService.cs:24-44](../../src/SoftMedia.Server/Services/Security/StreamSecurityService.cs#L24-L44).

**Problem:** `Path.GetFullPath` collapses `..` but does not follow symlinks. On Linux, an admin-declared library root containing a symlink can re-introduce LFI; `GET /api/v1/stream/{id}` for a `MediaItem.Path` that traverses the symlink will be served because the literal string still starts with the canonical library prefix.

**Change:**
1. Introduce a private helper `ResolveRealPath(string)` that uses `FileInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path)`. Call it on both sides of the prefix comparison (the file path and each library root).
2. The library roots themselves can also be symlinks; resolve them once when iterating.
3. Preserve the existing case-insensitive `StringComparison.OrdinalIgnoreCase` for the prefix check (Windows-friendly).

**Tests:**
- `src/SoftMedia.Server.Tests/Services/Security/StreamSecurityServiceTests.cs`:
  - Authorised path inside a library root → `true`.
  - Path containing `..` traversal that escapes the root → `false`.
  - On Linux test agents only (`[SkippableFact]` based on `RuntimeInformation.IsOSPlatform(OSPlatform.Linux)`), a symlink inside the root pointing outside the root → `false`. Use `Directory.CreateSymbolicLink` to set up the test fixture.
  - On Windows test agents only, a junction-point path that escapes the root → `false`.
  - The library root itself being a symlink → `true` for valid file inside it.

**Acceptance:** With a library root `/tmp/lib` containing `ln -s /etc /tmp/lib/sysconf`, calling `IsPathAuthorized("/tmp/lib/sysconf/passwd", ["/tmp/lib"])` returns `false`. Existing tests still pass.

### A3. Default `Cors:AllowAnyOriginForLAN` to `false` and move the dev override

**Severity:** Medium (peer review NEW-3).

**Where:**
- [src/SoftMedia.Server/appsettings.json:17](../../src/SoftMedia.Server/appsettings.json#L17)
- [src/SoftMedia.Server/Program.cs:55-76](../../src/SoftMedia.Server/Program.cs#L55-L76)
- New: `src/SoftMedia.Server/appsettings.Development.json` (if absent)

**Problem:** The shipped production config sets `AllowAnyOriginForLAN: true`, which makes the CORS policy `SetIsOriginAllowed(_ => true).AllowCredentials()` — a wildcard accepting credentialed cross-origin requests from anywhere. A user running with Method B (DuckDNS + Caddy, SDD §6.1) will allow any webpage they visit to make credentialed API calls.

**Change:**
1. In `appsettings.json`, set `AllowAnyOriginForLAN: false`.
2. Create or edit `appsettings.Development.json` to override `AllowAnyOriginForLAN: true` so the Vite dev proxy still works locally without configuration.
3. In `Program.cs`, when the flag is `true`, log a startup warning so an operator who flips it on is aware of the implication.
4. Verify the `AllowedOrigins` array in `appsettings.json` still contains the two localhost entries — those keep dev working even with the flag off.

**Tests:**
- `src/SoftMedia.Server.Tests/Integration/CorsConfigurationTests.cs`:
  - With production config (`AllowAnyOriginForLAN=false`), a preflight request from `https://attacker.example` to `/api/v1/media` does not get an `Access-Control-Allow-Origin: *` response.
  - With dev config, the same request from `http://localhost:5173` is allowed.

**Acceptance:** A `curl -H "Origin: https://attacker.example" -X OPTIONS https://server/api/v1/media` does not return `Access-Control-Allow-Origin: https://attacker.example` when running with the default production config.

### A4. Reduce JWT access-token TTL to 15 minutes

**Severity:** Medium (peer review JWT TTL section).

**Where:** [src/SoftMedia.Server/appsettings.json:23](../../src/SoftMedia.Server/appsettings.json#L23).

**Problem:** Shipped value is 60 minutes. The refresh-rotation design accommodates 15 minutes with no UX cost. Reverse-proxy log exposure of `?access_token=` URLs (Method B users) means a stolen token is valid for an hour with the current setting.

**Change:** Edit `ExpiryMinutes` from `"60"` to `"15"`. The `TokenService.cs:29` fallback default is already `"15"`, so this just aligns the shipped config with the safer default.

**Tests:** No new tests required — existing `TokenService` tests cover claim shape and signing. The TTL is a config value.

**Acceptance:** A token issued by `POST /api/v1/auth/login` has an `exp` claim ≤ 15 minutes from `iat`.

### A5. Strip `ex.Message` from generic 500 responses

**Severity:** Medium (peer review NEW-2).

**Where:**
- [src/SoftMedia.Server/Controllers/TranscodeController.cs:128](../../src/SoftMedia.Server/Controllers/TranscodeController.cs#L128)
- [src/SoftMedia.Server/Controllers/TranscodeController.cs:237](../../src/SoftMedia.Server/Controllers/TranscodeController.cs#L237)
- [src/SoftMedia.Server/Controllers/TranscodeController.cs:270](../../src/SoftMedia.Server/Controllers/TranscodeController.cs#L270) (becomes moot after A1 deletes this block)
- [src/SoftMedia.Server/Controllers/AudioStreamController.cs:66](../../src/SoftMedia.Server/Controllers/AudioStreamController.cs#L66)

**Problem:** Framework exceptions caught by `catch (Exception ex)` include internal filesystem paths in `ex.Message`. Returning that to the client leaks directory structure (e.g., *"Could not find file '/home/user/transcode-temp/abc/seg_001.ts'"*). The server-side `_logger.LogError(ex, …)` already captures the full detail.

**Change:**
1. Replace `ex.Message` references in the listed locations with a generic string like `"An unexpected error occurred. See server logs for details."` (or `"Transcoding failed."` where the controller-context name is appropriate).
2. Leave `LibrariesController.cs:55,74` alone — those catch `ArgumentException` whose messages are developer-controlled and intended for the client.

**Tests:** Extend `ControllerAuthorizationTests.cs` (or create a new `ControllerErrorResponsesTests.cs`) that triggers a known-failing path (e.g., transcode for a missing file) and asserts the response body does not contain a `/` filesystem-path delimiter.

**Acceptance:** Inspecting a 500 response from a forced-failure transcode call shows no internal paths in the body.

### Wave A test plan

- `dotnet test src/SoftMedia.Server.Tests/` — full suite must pass.
- Manual: log in, play a transcoded video, scrub the timeline. Frame preview still appears (validates A1 didn't break the legitimate path).
- Manual: with the dev override, the Vite proxy still works (validates A3 dev split).

---

## Wave B — Parental controls (the actual enforcement layer)

Single PR. Target: 3–5 days. This is the longest wave and the only one with substantial schema and architectural shape; spend the first hour aligning on the design before writing code.

### Design sketch (read before coding)

The right architecture is **filter at the data layer, not at the controller**. Controllers stay simple; a query filter applied inside `MediaRepository` and `LibraryRepository` strips disallowed items from every list response. A separate stream-time check inside `MediaService` blocks direct-by-ID access to a blocked item.

Filter inputs (per request):
- `User.MaxRating` (legacy single string) — used as a fallback when `ContentRatings` is empty.
- `User.ContentRatings` (per-type map: `{ "Movie": "PG-13", "TV": "TV-14", "Game": "T" }`).
- `User.Role` — `Admin` bypasses all filtering.

Filter inputs (per item):
- `MediaItem.ContentRating` (already populated by Wikidata/OMDb/ComicInfo providers).
- `MediaItem.Type` — selects which entry from `ContentRatings` to compare against.

The comparison itself needs an ordered enum per type:
- Movie ratings: `G < PG < PG-13 < R < NC-17 < Unrated/null` — null/Unrated treated as **most permissive** with respect to admin trust but **most restrictive** with respect to a child user (i.e., a child cannot see Unrated).
- TV ratings: `TV-Y < TV-Y7 < TV-G < TV-PG < TV-14 < TV-MA`.
- Game ratings (ESRB): `EC < E < E10+ < T < M < AO`.

A null/unknown `MediaItem.ContentRating` is treated as **above** the child's ceiling — fail-safe when metadata is missing.

### B1. Introduce ordered rating enums and a comparator

**Where:** New file `src/SoftMedia.Server/Services/Security/ContentRating/RatingComparator.cs`.

**Change:**
1. Define `MovieRating`, `TvRating`, `GameRating` enums with explicit ordinal values matching real-world ascending strictness.
2. Static helper `bool IsAllowed(MediaType type, string? itemRating, string? userCeiling)`:
   - If `userCeiling` is null/empty → `true` (no ceiling configured = unrestricted).
   - If `itemRating` is null/empty → `false` (fail-safe: unrated content blocked when a ceiling is set).
   - Parse both into the per-type enum; return `itemRating ≤ userCeiling`.
3. A `MediaType` whose rating system is not implemented (Music, Book, Photo) is **always allowed** for now — parental controls do not apply.

**Tests:** `RatingComparatorTests.cs` — exhaustive matrix per type.

### B2. Rating-resolution service injected into the request scope

**Where:** New `src/SoftMedia.Server/Services/Security/ContentRating/IUserContentRatingProvider.cs` + impl.

**Change:**
1. Resolves the current request's `User.ContentRatings` JSON (with fallback to `User.MaxRating` for `MediaType.Movie`) into a typed `UserRatingCeilings` struct.
2. Caches the parse per-request (avoid re-deserialising the JSON for every item in a list).
3. `Admin` role short-circuits — returns null (= no ceilings).

### B3. Apply the filter in repositories

**Where:**
- [src/SoftMedia.Server/Services/Infrastructure/MediaRepository.cs](../../src/SoftMedia.Server/Services/Infrastructure/MediaRepository.cs)
- [src/SoftMedia.Server/Services/Infrastructure/LibraryRepository.cs:64-196](../../src/SoftMedia.Server/Services/Infrastructure/LibraryRepository.cs#L64-L196)

**Change:**
1. Inject `IUserContentRatingProvider` (which itself depends on `IHttpContextAccessor`).
2. After the existing query is built, apply an `IQueryable.Where(m => RatingComparator.IsAllowed(m.Type, m.ContentRating, ceilings.For(m.Type)))` clause **before** pagination.
3. Because the comparator references EF-incompatible code, prefer to express the filter as inline `Where` lambdas using the enum-mapped string columns. Build a compiled expression in `RatingComparator` for use inside `IQueryable`.
4. Item counts (`COUNT(*)`) must apply the same filter so pagination remains correct.

### B4. Per-ID stream/access checks

**Where:**
- [src/SoftMedia.Server/Services/Media/MediaService.cs](../../src/SoftMedia.Server/Services/Media/MediaService.cs) (the method that returns a `StreamInfo` for `StreamController`)
- The corresponding service paths used by `BookController`, `AudioController`, `MusicController`.

**Change:** Before returning the stream payload, compare the item's `ContentRating` against the resolved user ceiling. If denied, return 404 (not 403 — same anti-probe behaviour as the path-jail mismatch already does at `StreamController.cs:50-55`).

### B5. Admin UI parity (optional within this wave)

**Where:** [src/SoftMedia.Client/src/pages/MyAccountPage.tsx](../../src/SoftMedia.Client/src/pages/MyAccountPage.tsx) and any user-edit modal.

**Change:** Surface the per-type rating ceilings in a form (the `ContentRatings` JSON map already exists in the API). This is QoL — the backend filter works without it — but ships the visible feature.

### B6. Tests

- Unit: `RatingComparatorTests.cs` matrix.
- Integration: `Integration/ParentalControlIntegrationTests.cs`:
  - User with `MaxRating="PG-13"` listing movies does not see an R-rated `MediaItem`.
  - Same user requesting `GET /api/v1/stream/{r-rated-id}` directly returns 404.
  - User with `ContentRatings={"TV":"TV-14"}` does not see TV-MA episodes but does see PG-13 movies (because no Movie ceiling set → unrestricted).
  - Admin sees everything.
  - Items with null `ContentRating` are hidden from any user with a ceiling, visible to admin.

### Wave B acceptance

- A child account (Role=User, MaxRating=PG, ContentRatings={"Movie":"PG"}) can list and play G/PG content but receives empty results for R-rated items in listings and 404 for direct R-rated stream requests.
- An admin account behaves identically to today.
- All existing tests pass; no listing/pagination regression.

### Note on schema

`User.ContentRatings` already exists as a JSON string column ([User.cs:28](../../src/SoftMedia.Server/Models/User.cs#L28)). No migration is required for this wave. If a future iteration moves to a typed table, that's a separate plan.

---

## Wave C — Infrastructure & reliability

Single PR. Target: 2–3 days.

### C1. HLS segment janitor `IHostedService`

**Where:** New file `src/SoftMedia.Server/Services/Background/TranscodeSegmentCleanupService.cs`. Registered in [ServiceCollectionExtensions.cs:263-294](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L263-L294) (`AddBackgroundServices`).

**Problem:** [TranscodeService.cs:47-64](../../src/SoftMedia.Server/Services/Transcoding/TranscodeService.cs#L47-L64) deletes `transcode-temp` only at startup. If a client closes the tab without hitting `DELETE /api/transcode/{sessionId}`, the session directory accumulates `.ts` segments forever.

**Change:**
1. Hosted service runs on a `PeriodicTimer` (every 5 minutes).
2. Asks `ITranscodeSessionManager` for the set of currently-open session keys.
3. Walks the configured transcode-temp directory; any session subdirectory whose key is NOT in the open set, AND whose `LastWriteTime` is older than a threshold (default 10 minutes), is deleted.
4. Logs an info-level summary (`Cleaned N sessions, freed X MB`).
5. Reads its threshold from `Settings:TranscodeCleanupAgeMinutes` (default 10), so an admin can tune it.

**Tests:**
- `Services/Background/TranscodeSegmentCleanupServiceTests.cs`:
  - Closed session > threshold → directory removed.
  - Closed session < threshold → directory retained.
  - Open session of any age → directory retained.
  - Files outside the temp root are never touched (sanity).

**Acceptance:** Run a transcode, close the tab without DELETE, wait 11 minutes, observe the session directory removed. Disk usage stays bounded over a long-running install.

### C2. Image proxy `User-Agent` compliance

**Where:** [src/SoftMedia.Server/Controllers/ImageController.cs:120-123](../../src/SoftMedia.Server/Controllers/ImageController.cs#L120-L123).

**Problem:** The proxy creates an unnamed `HttpClient` and overrides `DefaultRequestHeaders.UserAgent` with a spoofed browser UA. This violates SDD §4.3 attribution requirements for upstream callers (Wikidata, MusicBrainz, Open Library).

**Change:**
1. Either (a) inject `IHttpClientFactory` and request a *named* client `"ImageProxy"` registered in `AddMediaServices` with `SoftMediaUserAgentHandler` attached, or (b) inject `SoftMediaUserAgentHandler` directly and stop overriding UA in the controller.
2. Remove the spoofed `Mozilla/...` UA.

**Tests:** Wiremock-style test (or `HttpMessageHandler` fake) that captures the outgoing request and asserts the `User-Agent` matches the SoftMedia pattern.

**Acceptance:** A proxied image fetch sends `User-Agent: SoftMedia/1.x (...)` matching what `SoftMediaUserAgentHandler` produces.

### C3. Photos / EXIF — Phase 2 placeholder

**Decision:** Photos library type is a Phase 2 deliverable, not part of 1.0. The peer review and audit agree on this. Wave F applies the SDD edit; Wave C does **not** implement the scanner.

What we do here: nothing in code. Tracking issue created. Listed for completeness.

### Wave C acceptance

- HLS segment janitor running, observable via INFO-level log lines.
- Image proxy outbound requests carry the SoftMedia User-Agent.

---

## Wave D — Frontend a11y closure

Single PR. Target: 2–3 days.

### D1. `ProgressBar.tsx` — slider semantics + keyboard handler

**Severity:** High a11y impact (peer review FAIL — fails all four SDD §8.3 rules).

**Where:** [src/SoftMedia.Client/src/components/player/ProgressBar.tsx:142-205](../../src/SoftMedia.Client/src/components/player/ProgressBar.tsx#L142-L205).

**Change:**
1. Add to the track div (line 142): `role="slider"`, `tabIndex={0}`, `aria-label="Seek"`, `aria-valuemin={0}`, `aria-valuemax={Math.floor(duration)}`, `aria-valuenow={Math.floor(currentTime)}`, `aria-valuetext={formatDuration(currentTime)}`.
2. Add `onKeyDown` handler:
   - `ArrowLeft` / `ArrowRight` → seek by ±5 seconds.
   - `Shift+ArrowLeft` / `Shift+ArrowRight` → ±10 seconds.
   - `Home` → 0; `End` → `duration`.
   - `PageUp` / `PageDown` → ±60 seconds.
   - `Space` should not be intercepted here (let it fall through to play/pause toggle).
3. Add focus-visible classes paired with the existing hover treatment: `focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 focus-visible:ring-offset-2 focus-visible:ring-offset-black`.
4. Touch target — increase the *clickable* hit area to 44px without enlarging the *visual* track. Wrap the track in a 44px-tall transparent div with `cursor-pointer` that forwards `onMouseDown`/`onMouseMove`/`onMouseLeave` to the track. The visible track stays 6px / 10px on hover for aesthetics.

**Tests:**
- `src/SoftMedia.Client/src/components/player/ProgressBar.test.tsx`:
  - `role="slider"` is present.
  - `ArrowRight` advances `aria-valuenow` by 5.
  - `Home` calls `onSeek(0)`.

**Acceptance:** Tab to the progress bar; press Right Arrow ten times; video advances 50 seconds. Focus ring is visible.

### D2. `VideoPlayer.tsx` — control button labels and focus rings

**Severity:** Partial fail (peer review).

**Where:** [src/SoftMedia.Client/src/components/player/VideoPlayer.tsx:1479-1610](../../src/SoftMedia.Client/src/components/player/VideoPlayer.tsx#L1479-L1610).

**Change:** For each `<button>` in the control bar (Previous Episode, Previous Chapter, Skip Back, Play/Pause, Skip Forward, Mute, Subtitle/Audio, Settings, Fullscreen):
1. Add an `aria-label` describing the action (e.g. `aria-label="Skip back 10 seconds"`).
2. Add `focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400` paired with the existing hover treatment.
3. Verify each button is `min-w-[44px] min-h-[44px]` in mobile/responsive contexts. Where `p-2` produces ~36px, bump to `p-3` or add explicit `min-h-[44px]`.
4. Keep `title` attributes — they remain useful as tooltip hints — but `aria-label` is the screen-reader source of truth.

**Tests:** Extend `src/SoftMedia.Client/src/test/a11yGuards.test.ts` — programmatic check that every `<button>` rendered by `VideoPlayer` has a non-empty `aria-label`.

**Acceptance:** Render `VideoPlayer`; the a11y guard test passes.

### D3. `MediaCard.tsx` — focus-within overlay

**Severity:** Caveat in peer review.

**Where:** [src/SoftMedia.Client/src/components/items/MediaCard.tsx](../../src/SoftMedia.Client/src/components/items/MediaCard.tsx) — wherever the overlay container has `opacity-0 group-hover/card:opacity-100`.

**Change:** Add `group-focus-within/card:opacity-100` to the same class list. Keyboard users tabbing into the card see the play button.

**Tests:** Vitest assertion that the overlay element is visible after `userEvent.tab()` reaches the card.

### D4. Universal a11y guard expansion

**Where:** [src/SoftMedia.Client/src/test/a11yGuards.test.ts](../../src/SoftMedia.Client/src/test/a11yGuards.test.ts).

**Change:** Add coverage for: every `<button>` has either textual content OR a non-empty `aria-label`; every `role="button"` element has `tabIndex` and `onKeyDown`; every interactive element with a hover class has a paired `focus-visible` class. Run against a curated list of components (the player, the card, the cast strip).

This is a guardrail, not a gate — failures are warnings until D1–D3 land, then treated as test failures.

### Wave D acceptance

- Tab-only run-through of a video page: every interactive element receives focus, has a visible ring, and announces meaningfully under VoiceOver/NVDA.
- The a11y guard test passes for the player, card, and cast strip.

---

## Wave E — Repo hygiene

Single PR. Target: 1 day.

### E1. Clean stale log files in the test project

**Where:** `src/SoftMedia.Server.Tests/` — 17 stale text files identified in the original audit (`build_log*.txt`, `test_log*.txt`, etc.).

**Change:**
1. `git rm` the 17 files.
2. Add to `.gitignore` at repo root (or extend an existing ignore): `**/test_log*.txt`, `**/build_log*.txt`, `**/test_run_*.txt`, `**/errors.log`, `**/build.log`, `**/build_error.log`.

**Acceptance:** A fresh `dotnet test` run does not re-introduce any tracked files.

### E2. Fold `Program.cs` DI lines into extension methods

**Where:**
- [src/SoftMedia.Server/Program.cs:40-47](../../src/SoftMedia.Server/Program.cs#L40-L47) — eight loose `AddScoped` calls.
- [src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs) — destination.

**Change:** Move each registration into the appropriate extension method (`AddMediaServices` for media/library/repositories, a small new `AddSecurityServices` for `IStreamSecurityService`). `Program.cs` stays focused on pipeline order and host config.

**Acceptance:** `Program.cs` has no `AddScoped`/`AddSingleton`/`AddTransient` calls outside of the extension method invocations.

### E3. Fix the rate-limit comment / window-type mismatch

**Where:** [src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs:188-241](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L188-L241).

**Change:** Either (a) update the comment to match the code (`30 per minute, fixed window`) — preferred, since 30 is more permissive and 30/min for legit users with fat-fingered passwords is fine; or (b) change `PermitLimit` to 15 and switch to `GetSlidingWindowLimiter` to match the original design intent. Pick one; whichever is picked, the comment and code agree afterwards.

### E4. Split the current `security/hardening-wave-1` branch

**Severity:** Reviewability blocker.

**Problem:** `git status` shows 53 modified files plus a sizeable untracked reader-feature drop (TTS, EPUB highlights, dictionary, search drawer, bookmarks). Mixing security hardening with a reader feature in one PR makes review impossible.

**Change:**
1. Create a new branch `feature/reader-enhancements` from `main`.
2. Cherry-pick or `git checkout -- ` the reader-related files into that branch:
   - `src/SoftMedia.Client/src/components/reader/*` (new files)
   - `src/SoftMedia.Client/src/hooks/useTts.*`, `useSwipe.*`
   - `src/SoftMedia.Client/src/store/readerStore.*`
   - Server-side: `Bookmarks`, `Highlights`, `ReadingSessions`, `UserReaderPreferences`, `Dictionary*` controllers/models/migrations.
3. Keep on `security/hardening-wave-1`: only the auth, refresh-token, rate-limit, and integration-test scaffolding files.
4. PR each branch separately.

**Acceptance:** `git diff main..security/hardening-wave-1 --stat` no longer mentions any file under `components/reader/`, `hooks/useTts*`, `hooks/useSwipe*`, `store/readerStore*`, or the reader-feature controllers/migrations.

### Wave E acceptance

- Repo tree is clean.
- `Program.cs` is concise.
- Branch is reviewable.

---

## Wave F — SDD spec alignment (applied alongside this plan)

Four targeted SDD edits, applied as part of accepting this plan. Each edit either reflects a decision the team already made (e.g., SameSite=Lax) or adds an explicit requirement that closes an audit finding (e.g., symlink resolution).

### F1. SDD §4.2 — refresh-cookie SameSite policy

**Edit:** Replace the line *"`Refresh Token` (HttpOnly, SameSite=Strict Cookie)"* with *"`Refresh Token` (HttpOnly, SameSite=Lax, Path-scoped to `/api/v1/auth/`)"*. Add a one-sentence rationale: *"Lax is sufficient against CSRF for this design because mutations carry the JWT in the Authorization header (browsers do not auto-attach Authorization on cross-origin requests) and the refresh cookie is path-scoped."*

### F2. SDD §6.2 — CSRF model clarification

**Edit:** Replace *"CSRF Protection: Double-Submit Cookie pattern for API requests"* with *"CSRF Protection: All mutating requests carry the JWT access token in the `Authorization` header. Browsers do not auto-attach `Authorization` to cross-origin requests, which closes the classic CSRF surface. The refresh-token cookie is `SameSite=Lax` and `Path=/api/v1/auth/`-scoped, so it is not delivered on cross-site sub-resource POSTs. A double-submit cookie would add no enforcement here and is intentionally not implemented."*

### F3. SDD §6.2 — strengthen path-canonicalization requirement

**Edit:** Append to the "File Access" bullet: *"Path canonicalisation MUST resolve symlinks (e.g., via `FileInfo.ResolveLinkTarget(true)`) in addition to collapsing `..`. `Path.GetFullPath` alone is insufficient on Linux."*

### F4. SDD §4.1 — mark Photos as Phase 2

**Edit:** Prefix the Photos bullet with *"**(Phase 2 — post-1.0)**"*. The existing field list stays intact as the future contract.

### F5 (additional). SDD §4.5 — operational note on tokens-in-query

**Edit:** Append a paragraph at the end of the Streaming section: *"Operational note: `?access_token=…` URLs used for `<video>`, `<audio>`, and image elements appear in reverse-proxy access logs. Operators running SoftMedia behind nginx/Caddy SHOULD configure log scrubbing of the `access_token` and `token` query parameters, or shorten the access-token TTL to limit the exposure window."*

These edits are committed as part of accepting this plan (see *Status* line at the top of the SDD after acceptance).

---

## Risk register

| Risk | Wave | Mitigation |
|------|------|------------|
| Symlink resolution change breaks Windows path handling | A2 | Cross-platform tests. Preserve `OrdinalIgnoreCase` comparison. |
| EF Core cannot translate `RatingComparator.IsAllowed` to SQL | B3 | Express the filter as inline `Where(...)` lambdas using string columns; profile with `.ToQueryString()`. |
| Pagination counts diverge after rating filter | B3 | Apply the same filter to `COUNT(*)` query; covered by B6 test. |
| Janitor deletes a session that is opening at exactly the threshold boundary | C1 | Acquire the open-session set fresh on each tick; the 10-minute default is comfortable margin. |
| `[Authorize]` on the frame endpoint breaks the legitimate `<img src>` flow | A1 | The `OnMessageReceived` hook already lifts `?token=` for `/api/transcode`; manual smoke test is part of Wave A acceptance. |
| Branch split loses uncommitted work | E4 | Capture the current branch state in a `WIP-snapshot` tag before splitting. |

---

## Cross-wave dependency graph

```
F (docs) ── independent, applied with this plan
A (critical security) ── independent, can ship immediately after F
E1, E2, E3 (hygiene) ── independent of A; can ship in parallel
E4 (branch split) ── BLOCKS B/C/D PRs (must complete before they can be reviewed)

B (parental controls) ── depends on E4
C (infra) ── depends on E4; independent of B
D (a11y) ── depends on E4; independent of B and C
```

Practical execution order: **apply F now → land A as PR #1 → land E as PR #2 (split + hygiene) → fan out B/C/D as parallel PRs**.

---

## Success criteria — definition of "hardened"

Every box checked when this plan is fully executed:

- [ ] No public endpoint accepts a forged JWT (`A1`).
- [ ] No symlink under any library root can escape the jail on any OS (`A2`).
- [ ] CORS does not wildcard credentials in production by default (`A3`).
- [ ] Access-token TTL ≤ 15 minutes (`A4`).
- [ ] No 500 response body contains internal filesystem paths (`A5`).
- [ ] A child account cannot list or stream content above its rating ceiling (`B1–B6`).
- [ ] HLS segment temp directory is bounded over a long-running install (`C1`).
- [ ] All outbound metadata fetches carry the SoftMedia User-Agent (`C2`).
- [ ] The video player is fully keyboard-operable (`D1, D2`).
- [ ] Repo contains no stale log files; `Program.cs` carries no loose DI (`E1, E2`).
- [ ] `security/hardening-wave-1` branch is split (`E4`).
- [ ] SDD §4.2, §6.2, §4.1, §4.5 reflect the implemented design (`F1–F5`).

---

## Out of scope (deliberately deferred)

- **Photos library type / EXIF scanner.** Phase 2.
- **Typed settings tree validation.** Tracking item; not blocking 1.0.
- **CSRF double-submit cookie pattern.** F2 documents that this is intentionally not implemented.
- **Schema instability investigation.** The 8-migrations-in-one-day settings reorganisation is noted but not a current bug.
- **Frontend N+1 / hooks audit.** Out of scope for this round; the peer review confirmed backend repos do not have N+1 issues.
- **Hardware-accelerated transcoding profiles.** Already in code (`TranscodeProfileBuilder`) but separate testing pass needed before being declared production-ready — not part of this hardening plan.

---

*End of plan.*
