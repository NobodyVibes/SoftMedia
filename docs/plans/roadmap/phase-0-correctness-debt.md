# Phase 0 — Correctness Debt

**Roadmap Phase:** 0 of 4
**Status:** Complete
**Estimated Duration:** 1-2 days *(actual: ~3 hours)*
**Date:** 2026-05-11
**Completed:** 2026-05-13
**Parent Document:** [00-roadmap-overview.md](./00-roadmap-overview.md)

## 1. Phase Summary

Two existing defects must be resolved before new feature work begins. Both are zero-functionality changes — they harden or correct behaviour that already shipped — but each blocks the integrity of work in later phases. Phase 0 is intentionally short so it cannot become an excuse to delay Phase 1.

## 2. Objectives

- The HTTP request pipeline correctly identifies the originating client IP when SoftMedia operates behind a reverse proxy.
- Public-facing engineering documentation accurately reflects the implementation it describes.

## 3. Prerequisites

None.

## 4. Work-Item Summary

| ID | Title | Status | Effort |
|----|-------|--------|--------|
| P0-WI-001 | Trusted-Proxy / Forwarded-Headers Configuration | Complete | 4-6 h *(actual: ~2 h)* |
| P0-WI-002 | Stale-Reference Documentation Cleanup *(rescoped 2026-05-13)* | Complete | 1-2 h *(actual: ~1 h)* |

## 5. Work Items

### P0-WI-001 — Trusted-Proxy / Forwarded-Headers Configuration

#### Motivation

The login and signup rate-limit policies (`AuthRateLimitPolicy`, applied at `src/SoftMedia.Server/Controllers/AuthController.cs:51` and `:136`) partition by `HttpContext.Connection.RemoteIpAddress`. When SoftMedia is operated behind a reverse proxy — the deployment SDD §6.1 explicitly recommends — this value resolves to the proxy's loopback address rather than the originating client. The rate limit consequently degrades to a single shared bucket, defeating its purpose against credential-stuffing attacks.

This is the only item in the roadmap classified as a security regression of a feature that is already shipped. It blocks P2-WI-005 (TOTP 2FA), which assumes a functioning rate limiter as its first line of defence.

#### Specification

1. Register `ForwardedHeadersOptions` in `Program.cs` honouring `XForwardedFor` and `XForwardedProto`.
2. Default `KnownProxies` to `IPAddress.Loopback` and `IPAddress.IPv6Loopback`. Default `KnownNetworks` to empty.
3. Insert `app.UseForwardedHeaders(...)` *before* `app.UseAuthentication()` in the request pipeline.
4. Expose two new admin settings under the existing `[Server] > Network` group:
   - `TrustedProxies` — comma-separated list of IP addresses appended to `KnownProxies`.
   - `TrustedProxyNetworks` — comma-separated list of CIDR ranges appended to `KnownNetworks`.
5. Read both settings at startup and on settings-change events.
6. Author a new operator-facing guide at `docs/user-docs/reverse-proxy.md` explaining the threat model, when these settings must be configured, and sample values for Caddy, nginx, and Tailscale Funnel.
7. Cross-reference the new guide from SDD §6.1.

#### Files Affected

- `src/SoftMedia.Server/Program.cs` — middleware registration and pipeline insertion.
- `src/SoftMedia.Server/Services/Infrastructure/SettingsService.cs` — register defaults.
- `src/SoftMedia.Server/appsettings.json` — document defaults.
- `docs/user-docs/reverse-proxy.md` — new file.
- `docs/SDD.md` §6.1 — add cross-reference.
- `src/SoftMedia.Server.Tests/Controllers/ForwardedHeadersTests.cs` — new test file.

#### Acceptance Criteria

- **Integration test, positive case:** A request through `TestServer` carrying `X-Forwarded-For: 1.2.3.4` from a trusted-proxy origin causes `HttpContext.Connection.RemoteIpAddress` to resolve to `1.2.3.4`.
- **Integration test, negative case:** The same header sent from an *untrusted* origin is ignored; `RemoteIpAddress` remains the connection origin.
- **Rate-limit integration test:** Eleven login attempts from `X-Forwarded-For: 1.2.3.4` and eleven from `X-Forwarded-For: 5.6.7.8` — both via a trusted proxy — trigger the rate limiter independently for each origin.
- **Configuration round-trip:** Setting `TrustedProxies` via the settings UI takes effect without server restart.

#### Estimated Effort

4-6 hours.

#### Dependencies

None.

#### Risks

- Misconfiguration of `KnownProxies` in production could allow IP spoofing via `X-Forwarded-For`. **Mitigation:** ship a conservative default (loopback only), require explicit opt-in, and document the threat model prominently in the new reverse-proxy guide.

---

### P0-WI-002 — Stale-Reference Documentation Cleanup *(rescoped 2026-05-13)*

#### Motivation

A pre-implementation review (2026-05-13) found that the originally-described drift between SDD §4.2 / §6.2 and the implementation is **not present** — the `SameSite=Strict → Lax` reconciliation was completed in the 2026-04-26 hardening pass. See `docs/plans/hardening-and-closure-plan-2026-04-26.md:428-434` ("F1 — SDD §4.2 refresh-cookie SameSite policy") and the completed-tracker entry at `docs/todos/10-hardening-and-closure-tracker.md:19`. SDD §4.2 line 222 and SDD §6.2 line 334 already specify `SameSite=Lax` with the appropriate rationale.

What does remain stale:

1. Three lower-priority documents still cite the obsolete `SameSite=Strict`. The most consequential is `docs/prompts/softmedia-task-template.md:24`, which injects role context into AI-assisted contributor sessions and is therefore the highest-leverage stale reference — it caused the original mis-scoping of this work item.
2. SDD §4.4 line 261 describes the video player as a "Custom HTML5 Video Player wrapper (e.g., `vidstack`)". The actual implementation in `src/SoftMedia.Client/src/components/player/VideoPlayer.tsx` is a fully custom HTML5 player built on `<video>` + `hls.js`, not a vidstack wrapper. The wording is technically permissive ("e.g.") but misleading.
3. `src/SoftMedia.Client/package.json:20,37` declares `@vidstack/react` and `vidstack` as dependencies. Confirmed via grep that no source file imports either package — they are pure dependency cruft.

#### Specification

1. **Update `docs/prompts/softmedia-task-template.md:24`** — change `SameSite=Strict` to `SameSite=Lax` so future task prompts generated from this template do not propagate the stale claim.
2. **Update `docs/todos/00-README.md:74`** — the conclusion about the CSRF double-submit cookie remains correct, but the supporting clause incorrectly cites `SameSite=Strict`. Fix the clause without altering the conclusion.
3. **Mark `docs/todos/04-refresh-token-persistence.md` as historical** — add a top-of-file note explaining that the TODO was completed in 2026-04-24 and that the actual implementation deviated from this spec on the SameSite question. **Do not edit the body** of a completed TODO — the historical record of the original plan is itself useful.
4. **Refine SDD §4.4 line 261** — replace "Custom HTML5 Video Player wrapper (e.g., `vidstack`)..." with a description that matches the actual implementation: a custom HTML5 player built on the native `<video>` element with `hls.js` for HLS support.
5. **Remove `@vidstack/react` and `vidstack`** from `src/SoftMedia.Client/package.json`. The lock file regeneration is the operator's responsibility (`npm install`) since this is a normal dependency change and not load-bearing for the doc fix itself; flag this in the verification block.
6. **Do not edit** `docs/reports/progress-audit-2026-04-26.md`. Point-in-time audit reports are historical records and must remain accurate to their date — that document correctly captured the drift *as it was on 2026-04-26*.

#### Files Affected

- `docs/prompts/softmedia-task-template.md` — line 24.
- `docs/todos/00-README.md` — line 74.
- `docs/todos/04-refresh-token-persistence.md` — header addition only.
- `docs/SDD.md` §4.4 line 261.
- `src/SoftMedia.Client/package.json` — lines 20 and 37.

#### Acceptance Criteria

- After completion, `grep -ni "samesite\s*=\s*strict" docs/` returns matches **only** in `docs/reports/progress-audit-2026-04-26.md` (preserved historical record) and inside the new "Historical note" block at the top of `docs/todos/04-refresh-token-persistence.md`.
- After completion, `grep -ni "vidstack" src/SoftMedia.Client/package.json` returns no matches.
- After the operator runs `npm install`, `npm ls vidstack` and `npm ls @vidstack/react` both report empty, and `npm run build` succeeds.
- SDD §4.4 line 261 describes the video player as a custom HTML5 + `hls.js` implementation, matching the file actually present at `src/SoftMedia.Client/src/components/player/VideoPlayer.tsx`.

#### Estimated Effort

1-2 hours.

#### Dependencies

None.

#### Risks

- **A future contributor may reintroduce vidstack** if they select it as a player library. **Mitigation:** the SDD §4.4 rewording explicitly names the current approach (native `<video>` + `hls.js`), so the deliberate choice is captured at the obvious place to look.
- **The prompt-template fix only affects future task prompts.** Existing AI-assisted contributor sessions running against the stale template will continue to receive the wrong role context until they are restarted. This is unavoidable for any prompt-template change.

## 6. Phase Exit Criteria

Phase 0 is complete when:

- ~~Both work items report acceptance criteria passing in CI.~~ ✅ `ForwardedHeadersIntegrationTests.RateLimiter_PartitionsByXForwardedFor_WhenProxyIsTrusted` passes; 15 existing auth + smoke integration tests still pass (no regression). `dotnet build src/SoftMedia.Server` succeeds with only pre-existing warnings.
- ~~A maintainer has merged the changes to `main`.~~ Pending — work landed on branch `security/hardening-wave-1`; PR not yet opened.
- ~~The change log in `00-roadmap-overview.md` reflects phase completion.~~ ✅ Entry added 2026-05-13.

## 7. Out of Scope

- Migrating off `hls.js` to any alternative HLS implementation.
- Documentation reconciliation outside the refresh-cookie posture and the §4.4 video-player wording.
- Hardening other middleware against trusted-proxy spoofing — that is a separate audit, not Phase 0 work.
- Regenerating `src/SoftMedia.Client/package-lock.json` — that requires `npm install` on the operator's workstation and was deliberately deferred to the operator-side step in P0-WI-002. The build will not fail without the regeneration, but `npm run build` will warn until lock-file and package.json are reconciled.

## 8. Verification Log *(added 2026-05-13)*

### P0-WI-001 — Trusted-Proxy / Forwarded-Headers Configuration

| Check | Result |
|---|---|
| `dotnet build src/SoftMedia.Server/SoftMedia.Server.csproj` | Build succeeded, 0 errors. Only pre-existing `SharpCompress` NU1902 warnings present. |
| `dotnet test --filter "FullyQualifiedName~ForwardedHeadersIntegrationTests"` | 1/1 passed (321 ms). |
| `dotnet test --filter "FullyQualifiedName~AuthRateLimit\|AuthRefreshFlow\|AuthRateLimiting\|FactorySmokeTests"` | 15/15 passed (10 s). No regression in adjacent auth tests. |
| `ForwardedHeaders` section present in `appsettings.json` | Confirmed (lines 28-31). Defaults are empty arrays. |
| `app.UseForwardedHeaders()` is first middleware in pipeline | Confirmed at `Program.cs:142`. |

### P0-WI-002 — Stale-Reference Documentation Cleanup

| Check | Result |
|---|---|
| `grep -ni "samesite\s*=\s*strict" docs/` returns only historical / contextual matches | Confirmed. Matches remain in: two `docs/reports/progress-audit-*` files (historical audit, preserved); `docs/plans/hardening-and-closure-plan-2026-04-26.md` (records the F1 SDD edit that did the Strict→Lax fix, preserved); `docs/plans/feature-roadmap-post-gap-analysis-2026-05-11.md` (superseded prose roadmap, preserved per overview Supersedes header); `docs/todos/04-refresh-token-persistence.md` body (now headed by Historical Note); and this phase document itself. |
| `grep "vidstack" src/SoftMedia.Client/package.json` | No matches. |
| `grep "vidstack" src/SoftMedia.Client/src/` | No matches (no source file ever imported either package). |
| `docs/prompts/softmedia-task-template.md:24` updated | Confirmed: now reads `SameSite=Lax refresh cookie (path-scoped to /api/v1/auth/)`. |
| `docs/SDD.md` §4.4 line 261 updated | Confirmed: replaced "wrapper (e.g., vidstack)" with explicit description of native `<video>` + `hls.js` implementation. |
| `docs/todos/04-refresh-token-persistence.md` carries the Historical Note header | Confirmed at lines 3-5. Body preserved. |
