# SoftMedia Configuration Guide

This document covers the runtime configuration values an operator must supply before SoftMedia will start. The server **refuses to boot** with a clear error if any required value is missing or unsafe.

## JWT signing secret (required)

SoftMedia signs access tokens with a single symmetric HS256 secret. The secret **must** be supplied by the operator at deploy time — it is no longer shipped with a working default. A server that cannot read a valid secret on startup prints `[FATAL] JwtSettings:Secret is not configured.` and exits immediately.

### Generate a secret

```
dotnet run --project src/SoftMedia.Server -- --generate-jwt-secret
```

The server prints an 86-character URL-safe base64 string (64 random bytes) and exits. Pipe this into whichever storage mechanism fits your deployment.

### Supply the secret — development

Use [.NET user-secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets). User-secrets live outside the source tree and are not committed:

```
dotnet user-secrets --project src/SoftMedia.Server init
dotnet user-secrets --project src/SoftMedia.Server set "JwtSettings:Secret" "<paste-generated-value>"
```

This setup is also required before running `dotnet ef` design-time commands (`migrations add`, `database update`, etc.) — the secret validator runs in the same `Main` path that EF tooling enters. A fresh checkout therefore needs:

**Bash / zsh:**

```
cd src/SoftMedia.Server
dotnet user-secrets init
SECRET=$(dotnet run --no-launch-profile -- --generate-jwt-secret)
dotnet user-secrets set "JwtSettings:Secret" "$SECRET"
# now `dotnet run` and `dotnet ef migrations list` both work
```

**PowerShell (Windows):**

```powershell
Set-Location src/SoftMedia.Server
dotnet user-secrets init
$secret = dotnet run --no-launch-profile -- --generate-jwt-secret | Select-Object -Last 1
dotnet user-secrets set "JwtSettings:Secret" $secret
# now `dotnet run` and `dotnet ef migrations list` both work
```

**Note on `--no-launch-profile`:** user-secrets are only auto-loaded when `ASPNETCORE_ENVIRONMENT=Development`. The default `Properties/launchSettings.json` sets that variable, so plain `dotnet run` (which picks up the launch profile) works after the secret is stored. If you intentionally bypass the launch profile with `dotnet run --no-launch-profile`, set `ASPNETCORE_ENVIRONMENT=Development` manually or pass the secret as `JwtSettings__Secret` env var.

### Supply the secret — production

Set the `JwtSettings__Secret` environment variable. On Linux:

```
export JwtSettings__Secret="<paste-generated-value>"
```

On Windows (PowerShell):

```
$env:JwtSettings__Secret = "<paste-generated-value>"
```

For systemd units, put it in the service's `EnvironmentFile`. For Docker, set it in the container's env or read it from a secret.

**Never** commit the secret to `appsettings.json`, `appsettings.Development.json`, or any other tracked file. The startup validator rejects the known committed placeholder specifically so that upgraders who copied the old default cannot silently run with a publicly-known key.

### Operational note: query-string access tokens in logs

Browser `<img>` tags cannot set `Authorization: Bearer` headers, so SoftMedia's image, music, books, transcode, and stream endpoints accept a `?access_token=<jwt>` (or `?token=<jwt>`) query parameter. The JwtBearer middleware lifts this to `context.Token` before any controller runs.

Because the token appears in the URL's query string, it **will** show up in:
- Reverse-proxy access logs (nginx, Caddy, IIS) by default
- ASP.NET Core `HttpLogging` middleware, if enabled
- Browser history and the `Referer` header of cross-origin navigations

Mitigations already in place:
- Every `<img>` rendered by the client sets `referrerPolicy="no-referrer"` so the token does not leak to external domains.
- Access tokens have a **15-minute lifetime**, so a log leak has a bounded exposure window before the token becomes worthless.
- Refresh tokens are never passed in the URL — they live in an `HttpOnly; SameSite=Lax` cookie scoped to `Path=/api/v1/auth/`. (Lax is sufficient against CSRF for this design because mutating requests carry the JWT in the `Authorization` header — browsers do not auto-attach `Authorization` cross-origin — and the `Path` scope further prevents the cookie from riding on cross-site sub-resource POSTs to other API routes. See SDD §6.2.)

Operator responsibilities:
- Configure your reverse proxy to **strip `access_token` and `token` query parameters** before logging. nginx: `map $request_uri $logged_uri { default $request_uri; "~(.*?)([?&])(access_token|token)=[^&]*(.*)$" $1$4; }` then `log_format scrubbed ... $logged_uri; access_log /var/log/nginx/access.log scrubbed;`
- Do **not** enable ASP.NET Core `HttpLogging` with `HttpLoggingFields.RequestQuery` without an equivalent filter.
- Rotate the `JwtSettings:Secret` if you suspect log exposure — this invalidates every in-flight token.

### Rotating the secret

Changing `JwtSettings:Secret` invalidates every access token currently in circulation — all active users must re-login once. There is no in-place hot-rotation path; restart the server after updating the secret.

### Validation rules

The startup validator rejects a secret that is:

- Missing, empty, or whitespace.
- Equal to the old committed placeholder.
- Shorter than 32 UTF-8 bytes (HMAC-SHA256 minimum per RFC 7518 §3.2).

## Other configuration

| Key | Default | Notes |
|---|---|---|
| `JwtSettings:Issuer` | `SoftMediaServer` | Embedded in issued tokens. Change only if you operate multiple servers and need to distinguish them. |
| `JwtSettings:Audience` | `SoftMediaClient` | Embedded in issued tokens. |
| `JwtSettings:ExpiryMinutes` | `15` | Access-token lifetime in minutes. Short by design — the refresh-token flow (see `/api/v1/auth/refresh-token`) rotates cookies transparently. Bump only if you fully understand the security trade-off. |
| `ConnectionStrings:DefaultConnection` | `Data Source=softmedia.db` | SQLite file path. |
| `Cors:AllowedOrigins` | `["http://localhost:5173","http://127.0.0.1:5173"]` | Explicit origin allowlist. |
| `Cors:AllowAnyOriginForLAN` | `false` (production) / `true` (development override) | When `true`, any origin is permitted — **only safe on a trusted LAN**. The shipped `appsettings.json` defaults this to `false`; `appsettings.Development.json` overrides to `true` so the Vite dev proxy works. The server logs a startup `[WARN]` when the flag is on, so an operator who flips it on for production is reminded of the implication. |
