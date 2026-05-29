# 04 · Implement refresh-token persistence + rotation

> **Historical note (added 2026-05-13).** This work item was completed in the 2026-04-24 refresh-token persistence wave (shipped commit `a16d50d` and its precursors). The implementation deviated from this spec on one detail: the refresh cookie uses `SameSite=Lax` rather than `SameSite=Strict` (see `src/SoftMedia.Server/Controllers/AuthController.cs:316-329` for the inline rationale citing Vite-dev-proxy interaction and OAuth 2.1 / OWASP guidance; SDD §4.2 and §6.2 carry the design-level statement). The body of this document is preserved as the *original* specification for historical reference; do not treat any section of it as current guidance.

---

**Severity:** P0 · **Layer:** Backend · **Est. size:** L (3–5 days)
**Depends on:** [02](02-jwt-signing-secret.md) — shortening access-token lifetime to 15 minutes is only meaningful once the signing secret is rotated off the committed placeholder. Ship 02 first or in the same PR.

**Config-section note:** the JWT section in `appsettings.json` is named `JwtSettings`, not `Jwt`. `ServiceCollectionExtensions.cs:34-36` and `TokenService.cs:28, :66` both read from `JwtSettings:*`. All config-key references in this todo use `JwtSettings:*` — do not rename the section.

## Problem

`AuthController.cs:209–222` — the `/api/v1/auth/refresh-token` endpoint is a stub:

```csharp
// TODO: Implement proper Refresh Token persistence and rotation.
// For now, we rely on the longer access token expiry (24h) and this endpoint exists
// to prevent 404 errors on the frontend, signalling re-login is required if access token DOES expire.
return Task.FromResult<ActionResult<AuthResponse>>(
    Unauthorized("Refresh token expired or invalid. Please login again."));
```

Two consequences:

1. The access-token expiry is set to **1440 minutes** (24 hours) at `appsettings.json:23` (`JwtSettings:ExpiryMinutes`) to compensate. This is far longer than best practice. A stolen or sniffed token gives the attacker a full day.
2. At the 24-hour boundary, every active user is force-logged-out with no refresh path. Combined with todo [05](05-frontend-401-interceptor.md), any *transient* 401 (including legitimate "this media item is forbidden") also triggers a full logout.

The SDD §4.2 design is correct: short-lived access token + long-lived refresh cookie with rotation. The implementation was simply never finished.

## Target state

- Refresh tokens are persisted server-side as hashes in a new `RefreshTokens` table.
- On login, the server issues:
  - Access token (JWT) — **15 minutes** lifetime, returned in the response body.
  - Refresh token — 7-day lifetime, returned as an `HttpOnly; SameSite=Strict; Secure` cookie. The cookie value is the raw token; the DB stores only a SHA-256 hash.
- On `POST /auth/refresh-token`:
  - Server looks up the hash, checks not-expired / not-revoked.
  - Issues a new access token **and** rotates the refresh token (old one is marked `RevokedAt = now` and `ReplacedById = newTokenId`).
- On `POST /auth/logout`: current refresh token is revoked.
- **Reuse detection:** if a revoked-and-replaced refresh token is presented, treat it as theft: revoke the entire token chain for that user and force re-login. Log a warning.
- Access-token expiry in `appsettings.json` is reduced to 15 minutes.

## Scope

**In scope:**
- New `RefreshToken` EF Core entity + migration.
- `IRefreshTokenService` with issue / validate / rotate / revoke / revoke-all-for-user.
- Updated `AuthController` endpoints: `Login`, `Refresh`, `Logout`, `ChangePassword` (changing password revokes all refresh tokens).
- Unit tests for the service + integration test for the full flow.

**Out of scope:**
- Device/session listing UI (deferred — a nice-to-have but not P0).
- Reasoning about token families or cryptographic refresh-chain signatures — the DB row + hash approach is sufficient.
- Sliding-expiry windows; use hard 7-day expiry and force re-login after that.
- Refresh-token binding to device fingerprint / user-agent — adds brittleness without much value for a self-hosted product.

## Data model

New table `RefreshTokens`:

| Column | Type | Notes |
|---|---|---|
| `Id` | `Guid` PK | |
| `UserId` | `Guid` FK → `Users.Id` | Indexed. Cascade on user delete. |
| `TokenHash` | `string` (64 hex chars) | SHA-256 of raw token. Unique index. Raw value is never stored. |
| `ExpiresAt` | `DateTime` (UTC) | |
| `CreatedAt` | `DateTime` (UTC) | `DateTime.UtcNow` at insert. |
| `CreatedByIp` | `string?` (45 chars for IPv6) | For audit. |
| `RevokedAt` | `DateTime?` (UTC) | Null if active. |
| `RevokedByIp` | `string?` | |
| `ReplacedByTokenId` | `Guid?` FK → `RefreshTokens.Id` | Populated on rotation; used for reuse detection. |
| `ReasonRevoked` | `string?` | Short enum-ish string: `"rotated"`, `"logout"`, `"password-change"`, `"reuse-detected"`. |

Index on `(UserId, RevokedAt)` to make "revoke all active tokens" fast.

## Service contract

```
interface IRefreshTokenService
{
    Task<(string rawToken, RefreshToken entity)> IssueAsync(User user, string? ip, CancellationToken ct);
    Task<RefreshTokenValidationResult> ValidateAsync(string rawToken, CancellationToken ct);
    Task<(string rawToken, RefreshToken entity)> RotateAsync(RefreshToken current, string? ip, CancellationToken ct);
    Task RevokeAsync(RefreshToken token, string reason, string? ip, CancellationToken ct);
    Task RevokeAllForUserAsync(Guid userId, string reason, CancellationToken ct);
}

record RefreshTokenValidationResult(
    bool IsValid,
    RefreshToken? Token,
    bool IsReuse /* true → chain compromise */);
```

Raw tokens are generated with `RandomNumberGenerator.GetBytes(64)` → `Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(bytes)` (URL-safe, no padding; ~86 chars). **Do not** use `Convert.ToBase64String` — the standard base64 alphabet includes `+` / `/` / `=` which are legal in cookie values but complicate logging and URL-round-tripping. Persist only the SHA-256 hash of the raw token (hex- or base64-encoded, pick one and stay consistent).

## Implementation steps

1. Add `RefreshToken.cs` model in `src/SoftMedia.Server/Models/`.
2. Add EF Core configuration (index, relationships) and create a migration: `dotnet ef migrations add AddRefreshTokens`.
3. Implement `RefreshTokenService` in `Services/Identity/`.
4. Register the service in `Extensions/ServiceCollectionExtensions.cs`.
5. Refactor `AuthController`:
   - Extract `SetRefreshToken` / `ClearRefreshToken` helpers.
   - `Login` (`:142`) now calls `IRefreshTokenService.IssueAsync`, sets cookie with the raw token.
   - `Refresh` (`:209`) now reads the cookie, validates, rotates, sets a new cookie, returns a fresh access token. On reuse detection → revoke all + return 401.
   - `Logout` (`:225`) now reads the cookie, calls `RevokeAsync`, clears the cookie.
   - `ChangePassword` — on success, call `RevokeAllForUserAsync` so old sessions cannot continue with the old credential assumptions.
6. Make the refresh cookie `Secure` flag environment-aware. Inject `IWebHostEnvironment` into `AuthController` (it is not currently a dependency — this is a constructor-signature change, not a drive-by). Then: `Secure = !env.IsDevelopment() || Request.IsHttps`. Add `IWebHostEnvironment env` to the `AuthController` constructor and store it in a field.
7. Update `appsettings.json:23` `JwtSettings:ExpiryMinutes` from `1440` → `15`.
8. Add a background cleanup task (hosted service) that runs daily and deletes rows where `ExpiresAt < UtcNow.AddDays(-30)` to keep the table small. This can be combined with existing cleanup services under `Services/Background/` — pick whichever is already running daily.

## Files to touch

- `src/SoftMedia.Server/Models/RefreshToken.cs` (new)
- `src/SoftMedia.Server/Data/AppDbContext.cs` — add `DbSet<RefreshToken> RefreshTokens` and the entity configuration (indexes, self-FK)
- `src/SoftMedia.Server/Migrations/*` (new migration from `dotnet ef migrations add AddRefreshTokens`)
- `src/SoftMedia.Server/Services/Abstractions/IRefreshTokenService.cs` (new)
- `src/SoftMedia.Server/Services/Identity/RefreshTokenService.cs` (new)
- `src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs` — register `IRefreshTokenService` inside the existing `AddIdentityServices()` method (the file already contains the JWT/password-hasher registrations; add this one alongside)
- `src/SoftMedia.Server/Controllers/AuthController.cs` — **constructor signature changes**: add `IRefreshTokenService refreshTokens` and `IWebHostEnvironment env` as new dependencies
- `src/SoftMedia.Server/appsettings.json` — update `JwtSettings:ExpiryMinutes` from `1440` to `15` at line 23
- `src/SoftMedia.Server/Services/Background/RefreshTokenCleanupService.cs` (new, or merge into existing daily cleanup job — grep `Services/Background/` first to find the current daily-schedule service)
- `src/SoftMedia.Server.Tests/Services/Identity/RefreshTokenServiceTests.cs` (new)
- `src/SoftMedia.Server.Tests/Controllers/AuthControllerRefreshTests.cs` (new)

## Tests required

Unit (service-level, in-memory SQLite):
- `Issue_ReturnsRawToken_AndPersistsHashOnly`
- `Validate_ValidToken_Succeeds`
- `Validate_ExpiredToken_Fails`
- `Validate_RevokedToken_Fails`
- `Rotate_RevokesOldAndIssuesNew`
- `Validate_RevokedAndReplacedToken_Returns_IsReuse_True`
- `RevokeAllForUser_MarksAllActiveTokensRevoked`
- `Cleanup_RemovesExpiredRowsOlderThan30Days`

Integration (`WebApplicationFactory`):
- `Login_IssuesAccessToken_AndRefreshCookie`
- `Refresh_RotatesTokenAndReturnsNewAccessToken`
- `Refresh_WithStolenReusedToken_InvalidatesChain` (present old token after rotation; expect 401 and verify all user tokens are revoked in DB)
- `Logout_RevokesRefreshToken`
- `ChangePassword_RevokesAllRefreshTokens`

## Acceptance criteria

- [ ] `RefreshTokens` table exists with the schema above.
- [ ] Raw tokens are never persisted; only SHA-256 hashes.
- [ ] `Login` / `Refresh` / `Logout` all work end-to-end in the integration test.
- [ ] Reuse detection revokes the entire user chain.
- [ ] `ChangePassword` revokes all refresh tokens.
- [ ] Access-token lifetime is 15 minutes.
- [ ] Background cleanup runs daily and prunes old rows.
- [ ] Dev environment no longer drops the cookie due to the `Secure` flag.
- [ ] All new tests pass.

## Risk / rollback

Medium. A migration plus an auth-flow change. Rollback requires rolling the migration back and reverting `AuthController`. Mitigations:

- Before merging, run `dotnet ef migrations script` to review the generated SQL.
- Keep the old `/refresh-token` 401 behaviour as a safety fallback behind a feature flag for the first release if wanted — but only briefly; unused flags rot.

## Dependencies

- Depends on todo [02](02-jwt-signing-secret.md): reducing access-token lifetime is only useful once the signing secret is out of the repo.
- Todo [05](05-frontend-401-interceptor.md) depends on this landing first. Once refresh actually works, the frontend interceptor can call it meaningfully.
