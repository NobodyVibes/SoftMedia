# Native-App Readiness — Verified Findings & Implementation Plan

**Version:** 1.1.0 *(2026-07-21: Phase C / Docker deferred by maintainer — see §4 note)*
**Status:** Active — awaiting maintainer sign-off on §6 open questions
**Date:** 2026-07-21
**Owner:** Project Maintainer
**Branch at time of writing:** `security/hardening-wave-2` (56 commits ahead of `main`, working tree clean)
**Companion documents:** `docs/plans/security-hardening-wave-2-implementation-plan.md` (deferred-item register), `docs/plans/roadmap/phase-4-deferred.md` (P4-002 native mobile, P4-014 desktop mpv client), `docs/api/stream-plan-negotiation.md` (the client contract this plan builds on).

---

## 1. Purpose

The 2026-07-20 project review concluded that SoftMedia's foundation is genuinely multi-client-ready at the protocol layer (capability-negotiated stream plans, scoped token model, byte-range direct play, root-relative media URLs, ~1,070 server + ~237 client tests), but that a set of verified gaps must close **before** any native desktop/mobile client work begins. This document is the authoritative task list for that pre-native phase.

Every finding below was **verified against the code on 2026-07-21** (evidence paths cited inline). Two claims from the initial review were corrected during verification and are recorded as such in §2.

**This plan is deliberately structured as five sequential AI-agent sessions** (§4). Each session is scoped to fit comfortably in one focused working session, ends at a stable, committed, test-passing state, and §5 defines the handoff each session must leave for the next.

---

## 2. Verified Findings Register

Status vocabulary: **Confirmed** (verified true on 2026-07-21), **Corrected** (initial review claim was wrong or has since changed), **Inherited** (tracked in another live document; listed for completeness).

### 2.1 Client-onboarding gaps (block native/third-party clients)

| ID | Status | Finding | Evidence |
|----|--------|---------|----------|
| F-01 | Confirmed | **No device-pairing / quick-connect flow.** The only login path is username + password (+ TOTP) into a form. No pairing-code endpoints exist anywhere in `src/`. | Repo-wide search for quick-connect/pairing variants: zero relevant hits |
| F-02 | Confirmed | **Refresh tokens are HttpOnly-cookie-only.** `Refresh()` reads `Request.Cookies["refreshToken"]` and has no request-body fallback, so non-browser clients must emulate a cookie jar scoped to `/api/v1/auth/`. | `Controllers/AuthController.cs:387-402` |
| F-03 | Confirmed | **OpenAPI/Swagger is Development-only.** Native-client authors get no hosted contract from a production server. | `Program.cs:221-225` |
| F-04 | Confirmed | **Transcode routes are un-versioned.** `TranscodeController` maps `api/transcode` while every other controller maps `api/v1/...`; cast/media-token scope checks hardcode both prefixes. | `Controllers/TranscodeController.cs:24` |

### 2.2 Deployment gaps (block the server the apps would talk to)

| ID | Status | Finding | Evidence |
|----|--------|---------|----------|
| F-05 | Confirmed | **No Docker/compose/systemd artifacts.** Setup is Windows-first PowerShell (`setup.ps1`, `install_ffmpeg.ps1`). `BinaryLocationService` already handles Linux ffmpeg discovery (incl. jellyfin-ffmpeg apt path), so the server itself is portable — only the packaging is missing. | `git ls-files` — no Dockerfile/compose/service unit |
| F-06 | Confirmed | **No in-app network settings.** No published Base URL / port / HTTPS guidance anywhere in the product settings surface; reverse-proxy and CORS knobs exist only in `appsettings.json`. | `SettingsService.cs` seeds — no BaseUrl/port/HTTPS rows |

### 2.3 Settings-surface defects and gaps

| ID | Status | Finding | Evidence |
|----|--------|---------|----------|
| F-07 | Confirmed | **Dead settings UI branches.** `SettingsPage.tsx` has render branches for `Language` (L732) and `LogLevel` (L740) but `SettingsService.InitializeDefaultsAsync()` never seeds those rows, so the controls can never appear. | `SettingsPage.tsx:732,740`; `SettingsService.cs` |
| F-08 | Confirmed | **`Webhooks.*` server flags are seeded but unreachable from the UI.** Five settings (`Enabled`, `RequestTimeoutSeconds`, `AllowHttp`, `AllowLoopback`, `AllowPrivateNetwork`) exist in the DB; the client contains zero references and `renderSettingsGroup('Webhooks')` is never called. | Client-wide grep for `Webhooks.`: zero hits |
| F-09 | Confirmed | **No in-app logging configuration or log viewer.** Log level lives only in `appsettings.json`. (Related to F-07 — the dead `LogLevel` branch suggests this was once intended.) | `appsettings.json`; F-07 |
| F-10 | Confirmed | **No server branding settings** (server name for the web app / login page message). `DlnaServerName` is the only server-name setting and is DLNA-scoped. | `SettingsService.cs` seeds |

### 2.4 Half-built or invisible features

| ID | Status | Finding | Evidence |
|----|--------|---------|----------|
| F-11 | Confirmed | **Photos is half-built and user-visible.** `LibraryType.Photo` / `MediaType.Photo`, `PhotoDetailView.tsx`, and `ExifMetadataProvider` exist, but there is **no `PhotoScanner`** — a photo library can be created and never populates. A hide-the-option todo already exists. | `Services/Scanning/` contains Movie/Tv/Music/Book/Game scanners only; `docs/todos/feature-shortlist/01-hide-photo-library.md` |
| F-12 | Confirmed | **Trailers/extras are scanned but unreachable.** Companion files (`-trailer`, `-sample`, `-extra` suffixes) are recognized and grouped during scan, but no client UI lists or plays them. | `LocalArtworkService.cs:145`; client-wide grep for `trailer`: zero hits |

### 2.5 Corrections to the 2026-07-20 review

| ID | Status | Finding | Evidence |
|----|--------|---------|----------|
| F-13 | **Corrected** | The review flagged `login.json`/`token.json` as possibly tracked. **They are untracked and gitignored** (`.gitignore:71-73`, purged from tracking by R-WI-001). Residual risk is local-only: the on-disk `login.json` holds the default `admin/admin123` credentials. Action reduced to: delete the local files / rotate the default admin password before any release artifact is cut. | `git ls-files` (no hits); `.gitignore:71-73` |
| F-14 | **Corrected** | The review described an uncommitted in-flight scan-progress refactor. **It has since landed** as commit `981e387`; the working tree is clean. Remaining action is only the branch merge (F-15). | `git status` clean, 2026-07-21 |

### 2.6 Inherited open items (tracked elsewhere, sequenced here)

| ID | Status | Finding | Tracked in |
|----|--------|---------|-----------|
| F-15 | Confirmed | `security/hardening-wave-2` is **56 commits ahead of `main`, unmerged**. Everything since `04a6988` (wave-1 merge) lives only on this branch. | — |
| F-16 | Inherited | Wave-2 deferred items needing the operator's live environment: flip `Security:EnforceCsp` to enforcing (+ token-in-memory T13.2), WS-6 T6.5 (proxy scheme check), T6.6 (log scrubbing), L-18 scan-progress broadcast scoping, L-24 hub ACL re-check on revocation. | `security-hardening-wave-2-implementation-plan.md` §status |
| F-17 | Inherited | M-5 atomic transcode-cap reservation — deferred pending concurrency load-testing; L-14's hard ceiling bounds the race meanwhile. | same |
| F-18 | Inherited | Test-infra: occasional SQLite teardown flake under full parallel runs; recommended fix is a non-parallel xunit `[Collection]` for SQLite-backed integration tests or `SqliteConnection.ClearAllPools()` on teardown. | same |

---

## 3. Work Items

Conventions follow `docs/plans/roadmap/00-roadmap-overview.md` §6: back-to-front development (endpoint + tests before UI), layering, Universal Client accessibility rules, path jailing, parameterised queries. IDs are `NR-WI-{n}` (Native Readiness). Status vocabulary: `Not Started` / `In Progress` / `Blocked` / `Complete`. Update this document's §7 table in the same commit that lands each item.

### Phase A — Land the plane (Session 1)

#### NR-WI-001 — Merge `security/hardening-wave-2` into `main`
- **Closes:** F-15
- **Motivation:** 56 commits of security and playback work exist only on a feature branch; everything downstream should build from `main`.
- **Specification:** Full-suite verification on the branch (`dotnet test src/SoftMedia.Server.Tests/SoftMedia.Server.Tests.csproj`, expect ≥1070 pass / 1 skip / 0 fail; `npm run build` + `npm test` in `src/SoftMedia.Client` — note per project memory the client type gate is `npm run build` ONLY). Then merge (or PR) into `main`. Tag the merge commit `v0.9.0-rc1` (or maintainer's preferred pre-1.0 scheme, §6-Q5).
- **Acceptance criteria:** `main` contains the wave-2 head; both suites green on `main`; tag pushed.
- **Effort:** 0.5 day. **Risk:** low — no code change, but do not skip the pre-merge suites.

#### NR-WI-002 — Wave-2 closeout: headless-verifiable deferred items
- **Closes:** part of F-16, F-18
- **Motivation:** Clear the wave-2 remainder that does *not* need the operator's live environment, so the security plan can be marked done-except-operator-gated.
- **Specification:** (a) WS-6 T6.5 proxy scheme check; (b) T6.6 log scrubbing of token material; (c) F-18 test-infra fix (single non-parallel `[Collection]` for SQLite-backed integration tests, or `ClearAllPools` teardown); (d) re-assess L-18 scan-progress scoping — the `981e387` refactor removed `scanProgressStore`/`ScanProgressToast`, so the original deferral reason ("the app-wide toast depends on `Clients.All`") may no longer hold; implement group-scoped broadcast if now unblocked, otherwise document why it remains deferred.
- **Acceptance criteria:** Each sub-item lands with a regression test or a written re-deferral rationale in the wave-2 plan's status table.
- **Effort:** 1–1.5 days. **Dependencies:** NR-WI-001 (work on `main` or a fresh branch off it).

#### NR-WI-003 — Local credential hygiene
- **Closes:** F-13
- **Specification:** Delete local `login.json` (root + `src/SoftMedia.Server/`) and `token.json`; rotate the admin password off `admin/admin123`; verify first-run flow forces a strong admin password (`MustChangePassword` path) so no future install ships usable defaults.
- **Acceptance criteria:** Files gone; default-credential login fails; first-run forces a password change (test exists or is added).
- **Effort:** 0.25 day.

### Phase B — API contract & client onboarding (Session 2)

#### NR-WI-004 — Version the transcode routes; keep a compatibility alias
- **Closes:** F-04
- **Motivation:** One route scheme for third-party clients; today they must special-case `api/transcode`.
- **Specification:** Move `TranscodeController` to `api/v1/transcode` as the canonical route while keeping `api/transcode` mapped (dual `[Route]` attributes) for the shipped web client and any minted URLs; update media/cast-token path-prefix checks to cover both; migrate the SPA to the v1 path; document the alias as deprecated-not-removed in `docs/api/`.
- **Acceptance criteria:** All transcode integration tests pass against `api/v1/transcode`; a regression test proves the legacy prefix still serves and still enforces token scope; SPA uses the new prefix.
- **Effort:** 0.5–1 day. **Risk:** medium (token scope checks hardcode prefixes — this is exactly the "enforcement at most but not all entry points" pattern the audits kept finding; test both prefixes).

#### NR-WI-005 — Body-based refresh-token flow for non-browser clients
- **Closes:** F-02
- **Specification:** Extend `POST /api/v1/auth/refresh-token` to accept the refresh token in the JSON body when no cookie is present (cookie wins if both). Login/refresh responses gain an opt-in mechanism for receiving the refresh token in the body instead of the cookie (e.g. a `client=native` request field), so browser behaviour is unchanged by default. All existing protections (rotation, reuse-detection chain revocation, rate limit) apply identically to the body path.
- **Acceptance criteria:** Integration tests: native-style login→refresh→rotate→reuse-detection entirely without cookies; browser cookie flow unchanged; reuse across the two delivery modes revokes the chain.
- **Effort:** 1 day. **Risk:** low-medium; auth surface — reviewer pass required (per the wave-2 lesson: `AuthController` has class-level `[AllowAnonymous]`, so any gate must be in-method).

#### NR-WI-006 — Quick Connect device pairing
- **Closes:** F-01
- **Motivation:** TV/mobile onboarding must not require typing a password + TOTP on a remote. This unblocks third-party clients (the P4-002 workaround explicitly anticipates them) before any first-party app exists.
- **Specification (Jellyfin-style):** (a) admin setting `EnableQuickConnect` (default off); (b) unauthenticated `POST /api/v1/quickconnect/initiate` → short-lived (≤10 min, single-use) 6-char code + polling secret, rate-limited per IP, codes from a non-ambiguous alphabet; (c) authenticated `POST /api/v1/quickconnect/authorize` from a logged-in session approves a code (user sees device name/IP before approving); (d) device polls `GET /api/v1/quickconnect/state`; on approval receives a normal token pair via the NR-WI-005 body flow; (e) surface in the web UI: an "Authorize device" entry point + code-entry screen; (f) audit log entry per authorization.
- **Acceptance criteria:** Full pairing integration test (initiate→authorize→poll→tokens); expiry, single-use, wrong-code, rate-limit, and disabled-setting tests; approval UI meets Universal Client rules.
- **Effort:** 2–3 days. **Dependencies:** NR-WI-005. **Risk:** medium — new unauthenticated surface; design review before implementation, following the wave-2 threat patterns (enumeration, brute-force, token minting).

#### NR-WI-007 — Publish the API contract in production
- **Closes:** F-03
- **Specification:** Serve the OpenAPI JSON (and optionally Swagger UI) outside Development, gated by an admin setting (`EnableApiDocs`, default on for the spec, maintainer may prefer off — §6-Q2); ensure the document is accurate for the routes third-party clients need (auth, quickconnect, stream plan, transcode v1); link it and `docs/api/stream-plan-negotiation.md` from the README.
- **Acceptance criteria:** `GET /swagger/v1/swagger.json` serves in a production-environment integration test when enabled and 404s when disabled.
- **Effort:** 0.5 day.

### Phase C — Deployment story — **DEFERRED (maintainer decision, 2026-07-21)**

> The maintainer deferred the Docker/Linux packaging work for now. Findings F-05/F-06(packaging half) remain valid and the specs below are retained as the starting brief for when this is picked back up. **Reassessment trigger:** before any public release announcement, or when a non-Windows deployment is actually needed. No other phase depends on Phase C; Sessions 4 and 5 proceed without it (NR-WI-016's install verification is Windows-only until then).

#### NR-WI-008 — Dockerfile + compose
- **Closes:** F-05
- **Specification:** Multi-stage Dockerfile (client `npm run build` → server `dotnet publish` → `mcr.microsoft.com/dotnet/aspnet:8.0` runtime with `jellyfin-ffmpeg` installed at the path `BinaryLocationService` already probes); volumes for the SQLite DB/config and media (read-only recommended); non-root user; healthcheck against the existing `/api/v1/health`; `docker-compose.yml` example with sensible env (JWT secret via env/secret, forwarded-headers trust for a reverse proxy); document image size and that ffmpeg is pulled at build (AGPL de-vendoring stays intact).
- **Acceptance criteria:** `docker compose up` on a clean Linux host reaches first-run setup; a scan + a transcode succeed in-container; DB persists across container recreation; documented in a new `docs/user-guide/docker.md`.
- **Effort:** 1–2 days (plus on-host verification). **Risk:** medium — first non-Windows end-to-end run; expect path/case-sensitivity and ffmpeg-discovery edges (symlink jail tests already fail-loud on POSIX, which will help).

#### NR-WI-009 — Linux service + release packaging docs
- **Closes:** F-05 (remainder)
- **Specification:** systemd unit example + hardening flags; `docs/user-guide/linux.md` (bare-metal) and a release checklist (publish artifacts per-RID, version stamping); CI job building the Docker image on tag if CI exists by then (the SCA workflow in `.github/workflows/security.yml` is the template).
- **Acceptance criteria:** Docs complete; unit file verified on one distro; checklist committed.
- **Effort:** 0.5–1 day. **Dependencies:** NR-WI-008.

### Phase D — Settings surface & product polish (Session 4)

#### NR-WI-010 — Network & server-identity settings page
- **Closes:** F-06, F-10
- **Specification:** New admin settings group `Network`: `PublishedBaseUrl` (used in webhook payload links, OpenAPI server entry, and anywhere absolute URLs are emitted), `ServerName` (web app title/login page, reuse as DLNA default), optional login-page message. A read-only "connection info" card (LAN addresses, port, HTTPS status, reverse-proxy detected via forwarded headers) with links to `docs/user-docs/reverse-proxy.md`. Port/HTTPS remain infra-level (Kestrel) — the card explains where, rather than pretending to control them.
- **Acceptance criteria:** Settings seeded + rendered; `PublishedBaseUrl` consumed by at least webhooks and OpenAPI; server name visible in the SPA; tests for seeding and DTO exposure.
- **Effort:** 1–1.5 days.

#### NR-WI-011 — Logging settings + log viewer; fix dead settings branches
- **Closes:** F-07, F-09
- **Specification:** Seed a real `LogLevel` setting wired to a runtime-adjustable level (e.g. `LoggingLevelSwitch`-equivalent for the configured provider); admin log viewer (tail last N lines, download, severity filter — read-only, path-jailed to the log directory). Remove or wire the dead `Language` branch (server-side `Language` is per-user via `UserPreference`; the dead branch is likely vestigial — remove it).
- **Acceptance criteria:** Level change takes effect without restart and persists; viewer denies non-admins; dead branches gone; tests for the level switch and the viewer endpoint's jail.
- **Effort:** 1–1.5 days.

#### NR-WI-012 — Surface the webhook server flags
- **Closes:** F-08
- **Specification:** Render the `Webhooks` settings group on the admin settings page (master enable, timeout, and the three SSRF-guard escape hatches with strong warning copy — these are security-relevant toggles and should say so).
- **Acceptance criteria:** Group renders; saving round-trips; SSRF-guard tests still pass with flags toggled.
- **Effort:** 0.5 day.

#### NR-WI-013 — Photos: COMPLETE (finish path; maintainer decided 2026-07-21)
- **Closes:** F-11. **Status: Complete 2026-07-21.**
- **What shipped:** `PhotoScanner` (inline EXIF + header-only dimension read, never enqueues the metadata queue), `MediaItem.ExifJson` column + migration `AddMediaItemExifJson`, EXIF persisted through both scan and manual-refresh paths (`MetadataAggregator` now stores `MetadataResult.Extra` for photos — it was previously dropped), `PhotosController` (`/api/v1/photos/{id}/image?width=`, ACL + symlink-jailed + media-token query auth via `IsMediaRoute`), EXIF-orientation baking in `ThumbnailService` (portrait photos no longer render sideways as thumbs), `MetadataEnrichmentPolicy` photo short-circuit (prevents an infinite re-enrichment loop — photos have no PosterUrl), LibraryService Photo guards removed, client: Photo library type enabled, `PhotoDetailView` now displays the actual image (full-res, letterboxed, open-original, ←/→ keyboard nav, resolution card), Play/Watched/Watchlist hidden for photos (the non-streamable-type bug class). Docs: `docs/user-docs/features/photos.md`. Tests: +19 server (scanner, controller ACL/fallbacks, DTO EXIF merge, enrichment-policy regression), suites green.
- **Known limitations:** HEIC indexed but not thumbnailable/displayable in browsers; no timeline/map view (post-1.0 polish).

#### NR-WI-014 — Extras & trailers row
- **Closes:** F-12
- **Specification:** Expose the already-scanned companion files: server returns an `extras` collection on the movie/series detail DTO (respecting library ACL + rating ceiling — remember the wave-2 lesson: every new read path goes through `ApplyAccess`); client renders an "Extras" row on the detail page playing through the normal stream-plan flow.
- **Acceptance criteria:** A `-trailer` file appears and plays from the detail page; ACL/rating tests for the new read path; no orphan cards in browse/search (extras stay non-primary items).
- **Effort:** 1–1.5 days.

### Phase E — Live verification & release cut (Session 5, operator present)

#### NR-WI-015 — Operator-gated wave-2 closeout
- **Closes:** remainder of F-16
- **Specification:** With the operator's live environment (and Cast device): review CSP report-only violations → flip `Security:EnforceCsp` on; implement token-in-memory (T13.2) if the CSP flip requires it; verify Cast, DLNA (real TV), and quickconnect on a real second device; L-24 hub ACL re-check decision.
- **Acceptance criteria:** CSP enforcing with zero functional regressions across play/cast/DLNA/reader; wave-2 plan status table fully closed or explicitly re-deferred with rationale.
- **Effort:** 1 day (operator time required).

#### NR-WI-016 — Release-readiness sweep and 1.0 RC
- **Specification:** Full manual QA pass per `docs/plans/roadmap/manual-qa-2026-05-30.md` (update it first for the features added since); fresh-install run on Windows from the docs alone (docs-accuracy test; Docker/Linux install verification moves to whenever Phase C is reactivated); first-run forces admin password (NR-WI-003); cut and tag the RC; update README feature matrix.
- **Acceptance criteria:** QA checklist green; both install paths succeed from docs; RC tagged.
- **Effort:** 1 day.

---

## 4. Session Plan (for multi-session AI-agent execution)

Each session = one fresh AI-agent session. **Do not combine sessions**: each ends at a committed, test-green checkpoint, and later sessions assume the earlier ones are merged. Point the agent at this document and the target phase.

| Session | Phase | Work items | Est. effort | Prerequisites / notes |
|---------|-------|-----------|-------------|----------------------|
| **1 — Land & stabilize** | A | NR-WI-001, 002, 003 | ~2 days | Needs maintainer available for the merge decision and §6-Q5 (version scheme). Everything else builds on this. |
| **2 — API contract & onboarding** | B | NR-WI-004, 005, 006, 007 | ~4–5 days | Largest session; if it must split, cut after NR-WI-005 (004+005 are self-contained; 006 depends on 005). Design-review NR-WI-006 before coding it. |
| **3 — Deployment** | C | NR-WI-008, 009 | ~2–3 days | **DEFERRED** (maintainer, 2026-07-21). Session numbering kept stable; skip straight from Session 2 to Session 4. When reactivated it can run any time after Session 2. |
| **4 — Settings & polish** | D | NR-WI-010, 011, 012, 013, 014 | ~4 days | Runs directly after Session 2 (Session 3 deferred). Needs §6-Q3 (photos) answered. |
| **5 — Live verify & RC** | E | NR-WI-015, 016 | ~2 days | **Operator must be present** (live environment, Cast device, real TV, second device for pairing). Final gate before native-app work begins. |

**After Session 5**, native-client work may start per the deferred register: desktop mpv shell (P4-014) first, mobile (P4-002) stays on the PWA until proven insufficient.

## 5. Session Handoff Protocol

Every session must end by:
1. Updating the §7 status table in this document (same commit as the work, per roadmap convention §7).
2. Committing all work to a branch off `main` (Session 1 merges wave-2 first) and stating the branch name in the status table.
3. Recording deviations, discovered bugs, and new deferrals in a short **§8 Session Log** entry (append-only) — the next session reads §7 + §8 before starting.
4. Leaving the suites green: server `dotnet test src/SoftMedia.Server.Tests/SoftMedia.Server.Tests.csproj`; client `npm run build` + `npm test` (build is the only type gate).

## 6. Open Questions (maintainer sign-off pending)

All questions were resolved 2026-07-21. The maintainer directed "complete photo implementation as you see best; also make any other decisions" — Q3 was decided by the maintainer, the rest by engineering under that delegation.

| # | Question | Decision (2026-07-21) |
|---|----------|----------------------|
| Q1 | Merge wave-2 via PR or direct merge? | **Direct merge** (sole maintainer; PR overhead buys nothing here) |
| Q2 | Production API docs default: enabled or admin-opt-in? | **Opt-in setting, default on** (self-hosted server; the contract is a feature, and an operator who wants it dark can toggle it) |
| Q3 | Photos for 1.0: hide or finish? | **Finish** — decided by maintainer; shipped same day, see NR-WI-013 |
| Q4 | Opt-in OpenSubtitles-style subtitle provider? | **Deferred post-1.0** (privacy-charter exception needs its own design pass; embedded+sidecar cover the current need) |
| Q5 | Version scheme for the RC tag? | **`v0.9.0-rc1`** at NR-WI-001, `v1.0.0` after Session 5 |

## 7. Status Tracking

| Item | Status | Branch / commit | Notes |
|------|--------|-----------------|-------|
| NR-WI-001 | Not Started | | |
| NR-WI-002 | Not Started | | |
| NR-WI-003 | Not Started | | |
| NR-WI-004 | Not Started | | |
| NR-WI-005 | Not Started | | |
| NR-WI-006 | Not Started | | |
| NR-WI-007 | Not Started | | |
| NR-WI-008 | Deferred | | Maintainer decision 2026-07-21; see Phase C note |
| NR-WI-009 | Deferred | | Maintainer decision 2026-07-21; see Phase C note |
| NR-WI-010 | Not Started | | |
| NR-WI-011 | Not Started | | |
| NR-WI-012 | Not Started | | |
| NR-WI-013 | **Complete** (2026-07-21) | `security/hardening-wave-2` (uncommitted at completion; see §8) | Finish path; +19 server tests; see item body |
| NR-WI-014 | Not Started | | |
| NR-WI-015 | Not Started | | Operator required |
| NR-WI-016 | Not Started | | Operator required |

## 8. Session Log

*(append-only; one entry per session)*

### 2026-07-21 — NR-WI-013 (photos) completed out of band; §6 decisions recorded

Maintainer directed completing the photo implementation immediately (ahead of Session 1) and delegated the remaining §6 decisions. Work done on `security/hardening-wave-2` (not yet committed at session end — commit before starting Session 1):

- **Server:** `PhotoScanner` (+ DI registration), `MediaItem.ExifJson` + migration `20260721163546_AddMediaItemExifJson`, shared `PhotoExifReader` (scanner + `ExifMetadataProvider` both use it; provider now also promotes `ReleaseDate`), `MetadataAggregator` persists `MetadataResult.Extra` → `ExifJson` for photos (**pre-existing bug: Extra was silently dropped for every provider**), `MetadataEnrichmentPolicy` Photo short-circuit (**pre-existing hazard: photos would have re-enqueued forever** — no PosterUrl), `PhotosController` (ACL → jail → thumb/original, 404 anti-probe), `/api/v1/photos` added to `IsMediaRoute`, `ResolvePosterPath` Photo case (`?width=480`), DTO merges `ExifJson` into `Metadata` for Photo rows, `ThumbnailService` bakes EXIF orientation (all 8 origins, matrix-mapped), `.heic` MIME mapping, LibraryService Photo guards removed.
- **Client:** Photo enabled in `LibraryForm`; `PhotoDetailView` displays the image (full-res letterbox, open-original control, ←/→ keys, resolution card); `MediaDetailLayout` hides Play/Watched/Watchlist for photos and uses a square poster crop. Verified: hero rotation and home rows already exclude photos; search photo hits route to the detail page.
- **Verification:** server suite green (full run) incl. 19 new photo tests; client `npm run build` clean; client 262/262.
- **Discovered for the backlog:** none blocking; HEIC display and timeline/map grouping noted as post-1.0 polish in `docs/user-docs/features/photos.md`.
