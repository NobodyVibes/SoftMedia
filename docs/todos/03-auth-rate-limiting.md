# 03 · Wire up the rate limiter on `/auth/login`, `/auth/signup`, and `/auth/change-password`

**Severity:** P0 · **Layer:** Backend · **Est. size:** S (< 1 day)

## Problem

`src/SoftMedia.Server/Program.cs` around line 71–81 registers a named `"fixed"` rate-limiting policy (100 requests / 10 seconds), and `app.UseRateLimiter()` is called near line 127. But:

- `options.GlobalLimiter` is **never set**, so the middleware has no default policy.
- No controller or endpoint uses `[EnableRateLimiting("fixed")]`.
- Result: the rate limiter middleware is wired up but **enforces nothing**. `/api/v1/auth/login` (`AuthController.cs:142`) and `/api/v1/auth/signup` (`AuthController.cs:34`) are effectively infinite-attempts-per-second to any attacker.

SDD §6.2 explicitly requires "Rate Limiting: Login endpoints limited to prevent brute-force attacks." This is not implemented.

## Target state

- A named policy `"auth"` keys on client IP with a strict window — e.g. fixed window of **5 requests per minute per IP**, with optional extension to 20 per 10 minutes for legitimate multi-user NAT scenarios.
- `[EnableRateLimiting("auth")]` decorates the `Login`, `Signup`, and `ChangePassword` actions on `AuthController`.
- The existing generic `"fixed"` policy is either bound as `options.GlobalLimiter` (so every endpoint has a sane ceiling) or deleted to reduce confusion — pick one.
- An integration test confirms the 6th login attempt from the same IP inside one minute returns `429 Too Many Requests`.

## Scope

**In scope:**
- Add a named `"auth"` policy that partitions by `HttpContext.Connection.RemoteIpAddress` (with `X-Forwarded-For` handling if the project supports reverse proxies — check `ForwardedHeadersOptions` in `Program.cs`).
- Decorate auth endpoints.
- Decide and document the fate of the generic `"fixed"` policy.
- Integration test coverage.

**Out of scope:**
- Distributed rate limiting (single-process SoftMedia box; in-memory is fine).
- CAPTCHA (SDD §6.2 marks it optional; deferred).
- Lockout on repeated failures (different control — rate limiting alone is sufficient for MVP).
- Per-user rate limiting — the threat model is credential stuffing before auth exists, so IP-based is correct here.

## Implementation steps

1. In `Extensions/ServiceCollectionExtensions.cs` (preferred — this is where other cross-cutting registrations live in this codebase; alternatively add a new `Extensions/RateLimitExtensions.cs` and call it from `Program.cs`), add a new policy:
   - Name: `"auth"`
   - Partition key: client IP (fallback to a constant "unknown" partition for null IPs — this is a deliberate fail-safe so bugs in IP extraction don't accidentally disable the limit).
   - Fixed window, 1-minute duration, permit limit 5, queue limit 0.
   - Rejection status: `429`.
2. **Decide** the fate of the existing `"fixed"` policy:
   - **Option A (preferred):** Bind it as the global limiter (`options.GlobalLimiter = PartitionedRateLimiter.Create<...>`) so every endpoint has a 100/10s ceiling.
   - **Option B:** Delete the `"fixed"` policy and rely solely on `[EnableRateLimiting("auth")]`.
   - Document the choice in the PR description.
3. Decorate `AuthController.cs`:
   - `[EnableRateLimiting("auth")]` on `Login` (`:142`), `Signup` (`:34`), and `ChangePassword` wherever it lives.
4. Confirm `ForwardedHeadersOptions` is configured if SoftMedia is ever run behind Caddy (SDD §6.1). Without it, the IP partition key will always be the proxy's IP and the limiter becomes global.
5. Add an integration test.

## Files to touch

- `src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs` — primary location for the new `"auth"` policy and the decision about the global limiter
- `src/SoftMedia.Server/Program.cs` — only if a new extension call needs to be wired in; leave the existing `app.UseRateLimiter()` line at `:127` alone
- `src/SoftMedia.Server/Controllers/AuthController.cs` — add `[EnableRateLimiting("auth")]` to `Login` (`:142`), `Signup` (`:34`), and `ChangePassword` (search the file; action route is `/auth/change-password`)
- `src/SoftMedia.Server.Tests/Controllers/AuthRateLimitTests.cs` (new)

## Tests required

Integration test using `WebApplicationFactory<Program>`:

- `Login_FiveAttemptsFromSameIp_Succeed` (or fail on bad credentials, but not 429)
- `Login_SixthAttemptFromSameIp_Returns429`
- `Login_AttemptFromDifferentIp_NotRateLimited` (use `TestServer.CreateClient` with a distinct `X-Forwarded-For` after enabling forwarded headers in test config)
- `Signup_SixthAttemptFromSameIp_Returns429`
- `PolicyWindow_ResetsAfterOneMinute` (may be flaky in CI — use a fake `TimeProvider` if available; otherwise make the test deterministic by injecting the window length via test config)

## Acceptance criteria

- [ ] A named `"auth"` policy exists and is bound to `Login`, `Signup`, and `ChangePassword`.
- [ ] A sixth login request from the same IP inside the window returns `429`.
- [ ] The existing `"fixed"` policy is either wired as the global limiter or removed, and the choice is documented.
- [ ] `ForwardedHeadersOptions` is verified sane for reverse-proxy scenarios; a comment explains why.
- [ ] All new tests pass.
- [ ] A grep for `[EnableRateLimiting` confirms the attribute is on every auth-adjacent endpoint.

## Risk / rollback

Low. Misconfigured partitioning could lock out legitimate users behind large NATs. The 5/min window is deliberately conservative; adjust if a specific deployment reports false positives. Rollback is a single attribute removal.

**Reminder:** the rate limiter is a defence-in-depth layer, not a replacement for strong password hashing (already in place via Argon2id — see `PasswordHasher.cs`).
