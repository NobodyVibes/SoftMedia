# 02 · Rotate JWT signing secret off the committed placeholder

**Severity:** P0 · **Layer:** Backend · **Est. size:** S (< 1 day)

## Problem

`src/SoftMedia.Server/appsettings.json:20` (inside the `JwtSettings` section opened at line 19) ships the JWT signing key as:

```json
"Secret": "ThisIsASecretKeyForSoftMediaDevelopmentOnly_ChangeInProduction"
```

**Config section note:** the section name is `JwtSettings`, not `Jwt`. Confirm against `appsettings.json:19`, `Extensions/ServiceCollectionExtensions.cs:34-36` (`config["JwtSettings:Secret"]`), and `Services/Identity/TokenService.cs:28, :66` (`_configuration.GetSection("JwtSettings")`). Every config-key reference below uses `JwtSettings:*` for this reason — do not rename the section to `Jwt`.

SoftMedia is a self-hosted product distributed via `git clone`. Every user who runs the defaults has a signing key that is **publicly readable in the repository**. Anyone with the repo URL can craft forged JWTs that validate against any default-configured SoftMedia install and impersonate any user including an admin.

The `_ChangeInProduction` suffix is aspirational, not enforced. Nothing in `Program.cs` detects that the placeholder is still in use.

## Target state

1. The default `appsettings.json` ships with an **empty** `JwtSettings:Secret` or with the placeholder tagged in a way that the server recognises as "not configured."
2. On startup, the server:
   - Reads `JwtSettings:Secret` from (in precedence order) environment variable (`JwtSettings__Secret`) → user-secrets → `appsettings.*.json`.
   - If the value is empty, matches the placeholder, or is shorter than 32 bytes of entropy, the server **refuses to start** with a clear error telling the operator how to set the secret.
   - A first-run helper (CLI subcommand or `--generate-secret` flag) prints a cryptographically strong secret and the exact command to persist it.
3. The secret **never** appears in `appsettings.json` again. The shipped file contains an empty string or is omitted.
4. Documentation (`README.md` or `docs/SDD.md` §6) explains the configuration flow for new operators.

## Scope

**In scope:**
- Startup validation in `Program.cs` (or a dedicated `JwtOptionsValidator`).
- Updating `appsettings.json` and `appsettings.Development.json`.
- A `dotnet user-secrets`-based workflow for local dev.
- A one-paragraph operator-facing doc entry.

**Out of scope:**
- Key rotation while running (deferred; operators restart the server).
- Asymmetric (RS256) keys — stay on HS256 for now.
- Storing the secret in a keychain / vault — the single-binary self-hosted model does not need this yet.

## Implementation steps

1. Remove the placeholder from `appsettings.json`. Leave the key present with an empty string or omit it entirely.
2. Add a `JwtOptions` record (`Issuer`, `Audience`, `Secret`, `ExpiryMinutes`) bound via `builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("JwtSettings"))`. **Do not** rename the config section to `Jwt` — `ServiceCollectionExtensions.cs` and `TokenService.cs` both read from `JwtSettings:*` and should continue to do so (migrate them to the new `IOptions<JwtOptions>` binding in a follow-up; for this todo the goal is only startup validation).
3. Add `.ValidateOnStart()` with a validator that rejects:
   - Null, empty, or whitespace `Secret`.
   - `Secret` equal to the old committed placeholder (keep the literal in the validator as a blocklist entry so upgraders who copied the default cannot silently run it).
   - `Secret` shorter than 32 UTF-8 bytes.
4. Write a clear error message:
   > "JwtSettings:Secret is not configured. Generate one with `dotnet run -- --generate-jwt-secret` and save it via `dotnet user-secrets set \"JwtSettings:Secret\" \"<value>\"` for development, or set the `JwtSettings__Secret` environment variable in production. See docs/user-guide/configuration.md."
5. Add a startup argument handler that, when `--generate-jwt-secret` is passed, prints a 64-byte `RandomNumberGenerator.GetBytes` secret (base64-encoded) and exits.
6. Update `docs/SDD.md` §6 or add `docs/user-guide/configuration.md` with the three-sentence explanation.
7. Update `src/SoftMedia.Server.Tests` if any test relies on the hardcoded secret — tests should set their own secret via `WebApplicationFactory` configuration overrides.

## Files to touch

- `src/SoftMedia.Server/appsettings.json` — empty out the `JwtSettings:Secret` value
- `src/SoftMedia.Server/appsettings.Development.json` — same treatment; confirm the file exists and contains no duplicate secret (must also be scrubbed if present)
- `src/SoftMedia.Server/Program.cs` (or a new `Extensions/JwtOptionsExtensions.cs`) — startup validator + `--generate-jwt-secret` argument handler
- `src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs` — no rename, but confirm the existing `config["JwtSettings:Secret"]!` reads still work after the validator asserts presence
- `src/SoftMedia.Server/Models/Options/JwtOptions.cs` (new, if not already split out)
- `src/SoftMedia.Server.Tests/...` — any test that injects auth config (override via `WebApplicationFactory` config rather than relying on the default)
- `docs/user-guide/configuration.md` (new) or `docs/SDD.md` §6

## Tests required

- `JwtOptionsValidator_EmptySecret_ThrowsOnStart`
- `JwtOptionsValidator_PlaceholderSecret_ThrowsOnStart`
- `JwtOptionsValidator_ShortSecret_ThrowsOnStart`
- `JwtOptionsValidator_ValidSecret_Starts`
- Integration test: `Program_DevelopmentEnv_WithoutSecret_FailsFast` using `WebApplicationFactory`.

## Acceptance criteria

- [ ] `appsettings.json` and `appsettings.Development.json` contain no usable default JWT secret (empty string or key absent).
- [ ] Server refuses to start if `JwtSettings:Secret` is missing, placeholder, or too short, with a helpful error message naming `JwtSettings:Secret` (not `Jwt:Secret`).
- [ ] A `--generate-jwt-secret` CLI option prints a fresh random secret.
- [ ] All existing tests still pass; new tests above exist and pass.
- [ ] A file at `docs/user-guide/configuration.md` exists and contains the strings `JwtSettings__Secret` and `dotnet user-secrets` (mechanically greppable).
- [ ] `git grep 'ThisIsASecretKeyForSoftMedia'` returns only the validator blocklist entry and tests, never as a live configuration value.

## Risk / rollback

Medium: any operator who has been running the default secret and re-starts after upgrade will find the server refuses to boot until they configure a secret. This is the intended behaviour. Ship with a prominent upgrade note. Rollback is straightforward: revert the validator.

**Migration note for existing deployments:** All currently issued JWTs signed with the old placeholder will fail validation after the operator rotates the secret, forcing re-login once. This is acceptable — it is the desired side-effect of plugging the hole.
