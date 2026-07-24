# Native Client Onboarding — Auth & Pairing

**Since:** 2026-07-21 (NR-WI-005 / NR-WI-006 / NR-WI-007); error envelope + timestamp
contract since 2026-07-24 (SR-WI-060 / SR-WI-061)
**Audience:** authors of SoftMedia playback clients (desktop, mobile, TV, headless).
**Companion:** [stream-plan-negotiation.md](./stream-plan-negotiation.md) (playback contract);
the live OpenAPI document at `GET /swagger/v1/swagger.json` (served in production, admin
setting `EnableApiDocs`, default on).

All routes are versioned under `/api/v1/`. The transcode surface's canonical prefix is
`/api/v1/transcode` (the un-versioned `/api/transcode` is a deprecated alias).

## 0. Wire contracts every client must know

### 0.1 Error envelope — RFC 7807 ProblemDetails (SR-WI-061)

JSON error responses use the RFC 7807 shape, `Content-Type: application/problem+json`:

```
{
  "type":    "https://tools.ietf.org/html/rfc9110#section-15.5.4",   // optional
  "title":   "Not found",                    // short human summary
  "status":  404,
  "detail":  "Episode not found or no next episode",   // specific human text — show this
  "traceId": "00-4bf9…-…-00"                 // correlate with server logs
}
```

Rules for clients:

- **Key behavior on the HTTP status code**, never on message text.
- Display `detail` when present, falling back to `title`.
- Machine-read discriminators appear as **extension members** (top-level JSON fields
  beside the standard ones). Current discriminators:
  - `"error": "password_change_required"` — 403 from any endpoint while the account
    must rotate its password (only `/auth/change-password`, `/auth/logout`,
    `/auth/refresh-token` are reachable).
  - `"error": "transcode_failed"` — 409 from HLS playlist/segment endpoints when the
    server-side transcode crashed terminally; stop retrying and surface an error.
- Validation failures (400) are RFC 7807 `ValidationProblemDetails` with an added
  `errors` object mapping field names to message arrays.
- Unhandled server exceptions return `500` in this same shape (a `title` plus
  `traceId`; never a stack trace).
- A few endpoints still return **plain-string** bodies for some 4xx/5xx (e.g.
  `text/plain` "Playback was stopped by an administrator." with 410) — tolerate both:
  if the body isn't JSON, treat it as the display text.

### 0.2 Timestamps — always UTC, always marked (SR-WI-060)

Every `DateTime` the API emits is serialized in round-trip ISO-8601 **with the `Z`
suffix** (e.g. `2026-07-24T18:03:12.4567890Z`). On input the server parses tolerantly:
offsets are honored, and bare ISO strings without an offset are **assumed UTC** — send
`Z`-suffixed UTC and you can never be bitten by either rule.

## 1. Cookie-free auth (body delivery)

Browsers get the refresh token as an HttpOnly cookie. Clients without a cookie jar ask
for **body delivery** instead — the refresh token appears in the JSON response and no
cookie is set:

```
POST /api/v1/auth/login
{ "username": "...", "password": "...", "tokenDelivery": "body" }

→ 200 { "accessToken": "...", "refreshToken": "...", "user": { ... } }
```

- If the account has TOTP enabled you receive `{ "status": "2fa_required", "challengeId" }`;
  complete with `POST /api/v1/auth/2fa` `{ challengeId, code, tokenDelivery: "body" }`.
- **Refresh** (rotating; single-use tokens):
  `POST /api/v1/auth/refresh-token` `{ "refreshToken": "..." }` →
  `{ accessToken, refreshToken, user }`. Store the NEW refresh token; reusing a rotated
  token revokes the whole chain (reuse detection) and forces a fresh sign-in.
- **Logout / unlink:** `POST /api/v1/auth/logout` `{ "refreshToken": "..." }`.
- Access tokens are short-lived (~15 min) — send as `Authorization: Bearer`.
- For `<img>`-style URL contexts use the reduced-privilege **media token**
  (`GET /api/v1/auth/media-token`), never the access token in a query string.

## 2. Quick Connect (pairing without a keyboard)

Opt-in server setting `EnableQuickConnect` (Settings → Account Management; default off).
When disabled, every endpoint below returns 404.

Device side (anonymous, rate-limited per IP):

```
POST /api/v1/quickconnect/initiate        { "deviceName": "Living Room TV" }
→ 200 { "code": "XK7P2M", "secret": "<64-hex>", "expiresInSeconds": 600, "pollIntervalSeconds": 3 }
```

Display `code` to the user, then poll (respect `pollIntervalSeconds`). The secret goes
in the POST body — never in a URL, where it would land in request logs:

```
POST /api/v1/quickconnect/state          { "secret": "<secret>" }
→ 200 { "status": "Pending" }                       — keep polling
→ 200 { "status": "Approved", "accessToken", "refreshToken" }   — done; delivered ONCE
→ 404                                                — expired / already claimed / disabled: restart pairing
```

User side (from their logged-in web session, Settings → My Account → "Link a Device"):
review `GET /api/v1/quickconnect/pending/{code}` (device name, IP, age), approve with
`POST /api/v1/quickconnect/authorize` `{ "code": "XK7P2M" }`. Approval requires a **full
session** — API tokens (403) and media/cast tokens (401) can never approve a device.

Properties clients can rely on: codes are 6 chars from an unambiguous alphabet (no
I/O/0/1, case-insensitive); the secret claim is single-use; codes expire after 10
minutes; the paired tokens are ordinary NR-WI-005 body-delivery credentials — continue
with the refresh flow from §1.
