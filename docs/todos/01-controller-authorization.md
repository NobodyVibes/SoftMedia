# 01 · Authorize `AudioController` and `ImageController`

**Severity:** P0 · **Layer:** Backend · **Est. size:** S (< 1 day)

## Problem

Four controllers ship without any `[Authorize]` attribute (audit surfaced two; a reflection-based CI guard found two more during implementation). Anonymous callers can currently:

1. **Dump the entire book catalog** — `GET /api/v1/audio/dump-books` → `AudioController.cs:21` returns `Id`, `Title`, `PosterUrl`, `CoverArtPath`, `Overview` for every `MediaItem` with `Type == Book`. This leaks titles and local file paths. It reads like a debugging helper that was never removed.
2. **Fetch any book cover art** — `GET /api/v1/audio/{id}/cover` → `AudioController.cs:28` serves cover files without auth.
3. **Use the server as an anonymous image proxy/cache** — `GET /api/v1/image/proxy?url=...` → `ImageController.cs:9` has a strict host allowlist but no authentication. An attacker can warm the cache, consume disk space, and probe whether specific URLs 200 or 404 (the negative-cache sentinel files leak that distinction).
4. **Stream any media file by ID** — `GET /api/v1/stream/{id}` → `StreamController.cs:13` serves media via HTTP Range without auth. The JWT `OnMessageReceived` handler in `ServiceCollectionExtensions.cs` *supports* query-string tokens for this path, but nothing *required* a token because the controller carried no `[Authorize]`.
5. **Fetch music resources** — `MusicController` (`/api/v1/music/...`) likewise exposed album covers and artist images without auth.

All five are one-line fixes (class-level `[Authorize]`, or deletion in the case of `DumpBooks`).

## Target state

- Both controllers require a valid JWT. Anonymous callers receive `401` before any business logic runs.
- The `DumpBooks` endpoint is deleted (it looks like a debug artifact and is not used by the frontend). If a maintainer disagrees and wants to keep it, it moves to `AdminController` and is gated `[Authorize(Roles = "Admin")]`.
- A regression test confirms each endpoint returns `401` without a token.

## Scope

**In scope:**
- Class-level `[Authorize]` on `AudioController` and `ImageController`.
- Removal (or admin-gating) of `DumpBooks`.
- New controller tests for unauthenticated access.

**Out of scope:**
- Path-jailing of `AudioController.GetCoverArt` (see [07](07-audio-cover-path-jailing.md)).
- Role-based access (Admin vs User) on individual cover-art endpoints — normal users legitimately need them.
- Revisiting the image proxy's host allowlist.

## Implementation steps

1. Confirm the frontend does not call `/api/v1/audio/dump-books`. Grep `src/SoftMedia.Client/src/` for `dump-books`. If unreferenced, delete the action entirely.
2. Add `[Authorize]` at the class level on `AudioController` (directly above the `public class AudioController` declaration around line 10 — note: class body opens near line 10; `[HttpGet("dump-books")]` is at line 21 and `[HttpGet("{id}/cover")]` is at line 28).
3. Add `[Authorize]` at the class level on `ImageController` (directly above the `public class ImageController` declaration around line 11; `[ApiController]` is already at line 9, so the new attribute sits between the two existing attributes and the class keyword).
4. If any endpoint on either controller must remain anonymous (e.g., for a public landing-page hero image), decorate that one method with `[AllowAnonymous]` and add a comment explaining why. Prefer not doing this — everything in this product runs behind auth.
5. Run the test suite; confirm nothing breaks. If a test was silently depending on unauthenticated access, fix the test to authenticate first rather than weakening auth.

## Files to touch

- `src/SoftMedia.Server/Controllers/AudioController.cs`
- `src/SoftMedia.Server/Controllers/ImageController.cs`
- `src/SoftMedia.Server.Tests/Controllers/AudioControllerTests.cs` (new)
- `src/SoftMedia.Server.Tests/Controllers/ImageControllerTests.cs` (new)

## Tests required

Create `AudioControllerTests` and `ImageControllerTests` using `WebApplicationFactory<Program>`:

- `GetCoverArt_WithoutToken_Returns401`
- `GetCoverArt_WithValidToken_Returns200OrNotFound` (either is fine — the point is the auth gate passes)
- `DumpBooks_IsNotRegistered` (after deletion) **or** `DumpBooks_WithoutAdminToken_Returns401Or403` (if retained as admin-only)
- `ImageProxy_WithoutToken_Returns401`
- `ImageProxy_WithValidToken_AndDisallowedHost_Returns400`

These tests can reuse the auth-token-issuing helper that todo [09](09-security-regression-tests.md) formalises; until then, write a minimal helper inside the test class.

## Acceptance criteria

- [ ] `AudioController` has class-level `[Authorize]`.
- [ ] `ImageController` has class-level `[Authorize]`.
- [ ] `DumpBooks` is deleted, or moved behind `[Authorize(Roles = "Admin")]` with justification.
- [ ] The five tests above exist and pass.
- [ ] `grep -rn "\[Authorize\]" src/SoftMedia.Server/Controllers/` shows every controller is gated (`AuthController` and `InvitesController` signup actions may still be `[AllowAnonymous]` — that is expected).
- [ ] `npm run build` on the client and a smoke test confirm no UI feature regressed (cover art still loads for logged-in users).

## Risk / rollback

Low risk. Worst case: a missed anonymous caller somewhere starts returning 401. Rollback is a one-line revert of each `[Authorize]` attribute. Keep this PR small and focused so the blast radius is obvious in review.
