# Phase 1 — Operational Trust

**Roadmap Phase:** 1 of 4
**Status:** In Progress *(pre-implementation review applied 2026-05-13)*
**Estimated Duration:** 2-3 weeks
**Date:** 2026-05-11
**Parent Document:** [00-roadmap-overview.md](./00-roadmap-overview.md)

## 1. Phase Summary

> **⚠ Pre-implementation review applied (2026-05-13).** All five work items were verified against the live codebase before coding, and **all five required rescoping** (three on stale premises, one on a latent design bug in the API-token auth hook, one on an incomplete service inventory). The authoritative corrections live in [`phase-1-rescope-2026-05-13.md`](./phase-1-rescope-2026-05-13.md). **Read that document alongside each work item below.** Where the original specifications here conflict with the rescope document, the rescope document wins.

Phase 1 establishes operator trust. After Phase 1 ships, an administrator can responsibly commit a production media library to SoftMedia. Five work items: backup-restore, per-user API tokens, streaming-policy limits, OMDb shared-key rollout, and a read-only background-task dashboard.

## 2. Objectives

- An administrator can take a complete server-state backup with a single API call and restore it without external tooling.
- Third-party tools can authenticate to SoftMedia with long-lived, revocable tokens scoped at the user level.
- Operators can enforce concurrent-transcode and bitrate ceilings to protect home network resources.
- OMDb metadata works out of the box without user configuration, with a frictionless user-key fallback always available.
- Administrators have visibility into every scheduled background task running on the server.

## 3. Prerequisites

- Phase 0 complete (the rate-limiter integrity required for the API-token authentication path).

## 4. Work-Item Summary

| ID | Title | Status | Effort |
|----|-------|--------|--------|
| P1-WI-001 | Backup / Restore Administrative Endpoint | **Complete** (2026-05-13) | 3-5 d |
| P1-WI-002 | Per-User API Tokens | **Complete** (2026-05-13) | 4-6 d |
| P1-WI-003 | Streaming Policy: Bandwidth and Concurrency Caps | **Complete** (2026-05-13) | 2-3 d |
| P1-WI-004 | OMDb Shared-Key Rollout | **Partial** (UI+docs done; CI/key blocked on maintainer) | 2-3 d |
| P1-WI-005 | Scheduled-Tasks Administrative Page (Read-Only) | **Complete** (2026-05-13) | 3-4 d |

> **Implementation status (2026-05-13).** All five items implemented per the corrected approach in [`phase-1-rescope-2026-05-13.md`](./phase-1-rescope-2026-05-13.md). The full verification log is in §8 below. Branch: `security/hardening-wave-1`. Server test suite: 592 passed / 1 skipped / 0 failed (was 560 before Phase 1; +33 tests). Client typecheck clean. P1-WI-004's CI + real-key-injection half is **blocked on the maintainer** (OMDb tier decision + no CI pipeline exists yet) and is the only Phase 1 work not landed.

## 5. Work Items

### P1-WI-001 — Backup / Restore Administrative Endpoint

#### Motivation

SoftMedia's data layer is SQLite with WAL mode — trivially backup-able via the SQLite Online Backup API or a coordinated copy of `softmedia.db`, `softmedia.db-wal`, and `softmedia.db-shm`. Today, however, users have no in-product mechanism to take or restore that backup. The first drive failure that costs a user their watch state, ratings, playlists, and watchlist would damage trust unrecoverably. A stub for this item exists at `docs/todos/feature-shortlist/02-admin-backup-endpoint.md`; this work item adopts and supersedes it.

#### Specification

##### Endpoints

All require `[Authorize(Roles = "Admin")]`.

- `POST /api/v1/admin/backup`
  Initiates an on-demand backup. Returns `200 OK` with `{ "backupId": "<guid>", "createdAt": "...", "sizeBytes": <int> }` and streams a `.zip` body containing the artefact.
- `POST /api/v1/admin/restore`
  Multipart upload of a previously-issued backup zip. Validates manifest checksum, takes the server into a maintenance state, replaces state, and returns `202 Accepted` with restart instructions.
- `GET /api/v1/admin/backup/history`
  Lists rotation-directory backups with creation timestamps, sizes, and `Pinned` flag.
- `POST /api/v1/admin/backup/{id}/pin` and `DELETE /api/v1/admin/backup/{id}/pin`
  Toggle a backup's `Pinned` flag (pinned backups are exempt from rotation).

##### Backup Contents

- `softmedia.db` — taken via `Microsoft.Data.Sqlite.SqliteConnection.BackupDatabase(...)`. **Raw file copy is forbidden** while WAL is active.
- `appsettings.json` — current configuration.
- `data/` directory tree, excluding cache: `data/transcode-temp/`, `data/image-cache/`, `data/trickplay/` are omitted.
- `manifest.json` — `{ softMediaVersion, schemaVersion, takenAtUtc, files: [{ path, sha256, sizeBytes }] }`.

##### Rotation

New hosted service `BackupRotationService` runs daily at the time configured by `Server.Maintenance.BackupSchedule`. Retains 7 daily, 4 weekly (Sunday), and all `Pinned` backups indefinitely.

##### Settings (new `[Server] > Maintenance` group)

- `BackupEnabled` (bool, default `true`)
- `BackupSchedule` (string, default `04:00`)
- `BackupDirectory` (string, default `./data/backups`)
- `BackupRetentionDaily` (int, default `7`)
- `BackupRetentionWeekly` (int, default `4`)

#### Files Affected

- `src/SoftMedia.Server/Controllers/AdminController.cs` — extend with backup endpoint group.
- `src/SoftMedia.Server/Services/Infrastructure/BackupService.cs` — **new**.
- `src/SoftMedia.Server/Services/Background/BackupRotationService.cs` — **new**.
- `src/SoftMedia.Server/Services/Infrastructure/SettingsService.cs` — register defaults.
- `src/SoftMedia.Server.Tests/Controllers/AdminBackupControllerTests.cs` — **new**.

#### Acceptance Criteria

- **Round-trip integration test:** seed a known set of `MediaItem` and `User` rows → take backup → delete `softmedia.db` → restore from backup → assert row-count equality and sampled-field deep equality.
- **Manifest integrity:** the per-file SHA-256 in the manifest matches the contained file bytes on every backup.
- **Schema-version safety:** restore rejects a backup whose `schemaVersion` exceeds the running server's schema version, with a structured error response.
- **Maintenance-state behaviour:** during restore, all routes return `503 Service Unavailable` except `GET /api/v1/health`.
- **Rotation:** rotation service deletes backups beyond the retention window; pinned backups are never deleted.

#### Estimated Effort

3-5 days.

#### Dependencies

None within Phase 1.

#### Risks

- **Restore during active writes corrupts the database.** Mitigation: enforce the maintenance-state behaviour above; reject restore if any active transactions are detected at start.
- **Backup zip leaks sensitive material** (refresh-token hashes, JWT signing key in `appsettings.json`). Mitigation: document explicitly in the operator-facing security guide; if the JWT signing key lives in a separate secrets store, exclude it from the backup with a clear log line.

---

### P1-WI-002 — Per-User API Tokens

#### Motivation

The current authentication surface — short-lived JWT access tokens plus a rotating HttpOnly refresh cookie — is correct for browser clients but excludes every other integration category. Sonarr-style companion tools, Home Assistant integrations, dashboard widgets, and any future native or community mobile client require long-lived programmatic credentials. The absence of this primitive is the single largest blocker for SoftMedia's third-party ecosystem and a prerequisite for the Phase 2 webhook work to be useful in both directions.

#### Specification

##### Data Model

New table `ApiTokens`:

| Column | Type | Notes |
|--------|------|-------|
| `Id` | Guid | PK |
| `UserId` | Guid | FK → Users, cascade delete |
| `TokenHash` | string | SHA-256 of the raw token; indexed |
| `Label` | string | user-supplied, max 100 chars |
| `Scopes` | string | JSON array |
| `CreatedAt` | DateTime UTC | required |
| `LastUsedAt` | DateTime? UTC | updated on each successful request |
| `LastUsedIp` | string? | updated on each successful request |
| `RevokedAt` | DateTime? UTC | non-null = revoked |
| `ExpiresAt` | DateTime? UTC | null = never expires |

##### Token Format

Raw tokens: `sm_` + 40 base32 characters. The `sm_` prefix is intentionally recognisable to secret-scanning tools (e.g. GitHub secret scanning) so leaked tokens can be detected automatically.

##### Scopes (v1, deliberately coarse)

- `read:library` — read media metadata and library structure.
- `read:state` — read playback state, watchlist, playlists.
- `write:state` — modify playback state, watchlist, playlists.
- `admin` — full admin access. May be granted **only** to users whose `Role == Admin`; the server enforces this independently of UI restrictions.

##### Authentication Path

Extend `JwtBearerEvents.OnMessageReceived` (currently lifts `?token=` per SDD §4.5) to also recognise `Authorization: Bearer sm_*` and resolve to a `ClaimsPrincipal` matching the token's owning user, with scope claims annotated.

##### Endpoints (all under `/api/v1/account/api-tokens`)

- `GET /` — lists the calling user's tokens (label, scopes, created, last used, last IP — **never** the raw token).
- `POST /` — mints a token. Body: `{ "label": "...", "scopes": ["read:library"], "expiresAt": null }`. Returns the raw token *exactly once*.
- `DELETE /{id}` — revokes by setting `RevokedAt`.

##### UI

New `My Account > API Tokens` section: token list, "New Token" action with label and scope checkboxes, raw-token modal displaying the token once with a copy-to-clipboard control and an explicit warning that the token cannot be retrieved again.

#### Files Affected

- `src/SoftMedia.Server/Models/ApiToken.cs` — **new**.
- `src/SoftMedia.Server/Data/AppDbContext.cs` — register `DbSet<ApiToken>` and index on `TokenHash`.
- `src/SoftMedia.Server/Controllers/AccountController.cs` — extend.
- `src/SoftMedia.Server/Services/Identity/ApiTokenService.cs` — **new**.
- `src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs` — extend bearer-events configuration.
- `src/SoftMedia.Client/src/pages/MyAccountPage.tsx` — extend.
- `src/SoftMedia.Server/Migrations/` — new EF migration.

#### Acceptance Criteria

- Mint → list → use against `/api/v1/media/{id}` → `LastUsedAt` and `LastUsedIp` updated within the request lifecycle.
- Revoked tokens return `401` on the next use.
- A `read:library` token receives `403` on `POST /api/v1/interaction/...` (which requires `write:state`).
- `admin` scope cannot be granted to non-admin users — assertion in service unit test, not solely UI.
- Raw token is returned only on mint; subsequent `GET` calls return only metadata.
- `TokenHash` is the only persisted form of the secret — DB-inspection assertion in integration test.

#### Estimated Effort

4-6 days.

#### Dependencies

- P0-WI-001 (rate limiter must operate against real client IP for token-brute-force defence-in-depth).

#### Risks

- **Token leakage via logging.** Mitigation: `Authorization` header is on the default redaction list; verify via integration test that requests carrying `sm_*` tokens produce no log entries containing the raw value.
- **Scope creep.** v1 deliberately uses coarse scopes. Resist requests for fine-grained scopes until at least one external integration exists that requires them.

---

### P1-WI-003 — Streaming Policy: Bandwidth and Concurrency Caps

#### Motivation

A single 4K HDR transcode can saturate a typical home upload connection. The transcode infrastructure already accepts `maxBitrate` per request (`src/SoftMedia.Server/Controllers/TranscodeController.cs:103`) and tracks active sessions per user (`src/SoftMedia.Server/Services/Transcoding/TranscodeSessionManager.cs`), but no operator-facing policy enforces either. One late-night transcoding spree by one household member can destroy a video call elsewhere in the house.

#### Specification

##### Settings (new `[Playback] > Streaming` subgroup)

- `MaxConcurrentTranscodesGlobal` (int, default `2`) — server-wide cap on simultaneous active transcodes.
- `MaxConcurrentTranscodesPerUser` (int, default `1`) — per-user cap.
- `MaxStreamBitrateKbpsLAN` (int, default `0` meaning unlimited) — clamps effective `maxBitrate` for LAN clients.
- `MaxStreamBitrateKbpsWAN` (int, default `10000`) — clamps for non-LAN clients.

##### Per-User Override

Add `User.MaxStreamBitrateKbps` (int?). When non-null, overrides the network-based cap for that user. Admin-only edit.

##### LAN vs WAN Determination

A client is LAN if its resolved IP (after the P0-WI-001 forwarded-headers resolution) is loopback, link-local, an RFC 1918 private range, or a unique-local IPv6 (`fc00::/7`). All other origins are WAN.

##### Enforcement

- `StreamPlanService.ComputeStreamPlanAsync` clamps the requested `maxBitrate` to the lesser of user override and network cap. `StreamPlan.Reason` is annotated with the clamp source (`"clamped by WAN cap"`, `"clamped by user policy"`).
- `TranscodeSessionService.StartTranscode` rejects new transcodes with `429 Too Many Requests` and `Retry-After: 30` when the global or per-user concurrency cap is reached.
- Audio-only transcodes count toward the per-user cap with weight `0.25` (a user listening to music can still start a video).

#### Files Affected

- `src/SoftMedia.Server/Services/Media/StreamPlanService.cs` — apply bitrate clamps.
- `src/SoftMedia.Server/Services/Transcoding/TranscodeSessionService.cs` — apply concurrency check.
- `src/SoftMedia.Server/Models/User.cs` — add `MaxStreamBitrateKbps?`.
- `src/SoftMedia.Server/Services/Infrastructure/SettingsService.cs` — register defaults.
- `src/SoftMedia.Server/Services/Infrastructure/NetworkClassifier.cs` — **new** helper.
- `src/SoftMedia.Server.Tests/Services/Media/StreamPlanServiceTests.cs` — extend.
- `src/SoftMedia.Server/Migrations/` — new EF migration.

#### Acceptance Criteria

- Third concurrent transcode when `MaxConcurrentTranscodesGlobal=2` returns `429` with `Retry-After: 30`.
- Second concurrent transcode by the same user when `MaxConcurrentTranscodesPerUser=1` returns `429`.
- WAN client requesting 20 Mbps when `MaxStreamBitrateKbpsWAN=10000` receives a stream plan clamped to 10 Mbps with `Reason` annotating the clamp source.
- LAN client at `192.168.1.10` is unaffected by the WAN cap.
- Per-user override of `5000` Kbps takes precedence over a `10000` Kbps WAN cap for that user.

#### Estimated Effort

2-3 days.

#### Dependencies

- P0-WI-001 (LAN/WAN classification requires correct client IP resolution).

#### Risks

- The `0.25` audio weighting is arbitrary; revisit if user feedback indicates the wrong shape. Tracked as a `// TODO(P1-WI-003)` comment with rationale.

---

### P1-WI-004 — OMDb Shared-Key Rollout

#### Motivation

`OMDbProvider` is already coded for a three-mode key arrangement (`softmedia` / `custom` / `disabled`) per `src/SoftMedia.Server/Services/Metadata/OMDbProvider.cs:25-72`, but no real key is shipped — the source contains a placeholder `SOFTMEDIA_OMDB_KEY_PLACEHOLDER`. The maintainer has elected to fund a shared OMDb key for the project's user base. The engineering work to operationalise that decision — and to ensure the user-funded fallback path remains frictionless should the shared key ever be withdrawn — is captured here.

#### Specification

##### Build Pipeline

- Add a release-build step that injects `OMDb:SoftMediaApiKey` into `appsettings.json` (or `appsettings.Production.json`) from a release-time secret.
- Committed source retains the placeholder string so OSS contributors can build without the secret.
- Document the build secret in a new release runbook at `docs/release/runbook.md`.

##### Tier Selection

- Adopt an OMDb tier consistent with expected user-base scale. Tier table at `OMDbProvider.cs:29-35` enumerates `free` (1k/day), `basic` (100k/day), `standard` (250k/day), `pro` (unlimited).
- The chosen tier is recorded in the `OMDbApiTier` setting; the existing low-quota notification path already keys off this.
- Maintainer decision required — Open Question #5 in `00-roadmap-overview.md`.

##### UI Affordance for User-Key Fallback

When `OMDbDailyCount` (see `SettingsService.cs:121`) crosses the tier limit *or* the shared key returns HTTP 401 / 402:

- A non-dismissable banner in the Settings UI: *"OMDb shared-key quota exhausted. [Use your own OMDb key →]"*
- The link navigates to `Settings > Metadata > Data Sources` with the `OMDbApiKeyMode` selector pre-focused.
- Below the selector, a prominent help block explains how to register a free OMDb key, where to enter it (`OMDbApiKeyCustom`), and that custom keys completely bypass the shared key.

##### Documentation

New user-facing document `docs/user-docs/features/omdb-key.md`:

- Explains the shared-key default and what the maintainer covers.
- Documents the user-key fallback procedure end-to-end.
- States explicitly: *"If the SoftMedia project ceases to maintain the shared key, the only required action is to obtain a free OMDb key and switch the mode to `custom`. No other changes are needed and no functionality is lost."*

##### Scope Boundary

This item ships no new metadata-fetching paths. All existing code in `OMDbProvider.GetActiveApiKey`, the `OMDbDailyCount` tracking, and the `NotificationService` low-quota notification remains unchanged. The change set is: build pipeline + UI affordance + documentation.

#### Files Affected

- CI workflow (`.github/workflows/release.yml` or equivalent) — add secret injection step.
- `src/SoftMedia.Client/src/pages/SettingsPage.tsx` — quota-exhausted banner and help block.
- `src/SoftMedia.Client/src/components/notifications/QuotaBanner.tsx` — **new**.
- `docs/user-docs/features/omdb-key.md` — **new**.
- `docs/release/runbook.md` — **new**.

#### Acceptance Criteria

- A release-build artefact contains a non-placeholder value at `OMDb:SoftMediaApiKey` in the shipped `appsettings.json`.
- A debug build still compiles and runs with the committed placeholder untouched.
- When `OMDbDailyCount > tier limit`, the Settings UI banner renders within one polling interval.
- Manual verification: setting `OMDbApiKeyMode=custom` with a personal key results in OMDb fetches succeeding with the personal key, with no requests attempted using the shared key (verify via `OMDbProvider` request logs).

#### Estimated Effort

2-3 days (predominantly CI and UX work; minimal backend feature code).

#### Dependencies

- Maintainer decision on tier (Open Question #5 in `00-roadmap-overview.md`).

#### Risks

- **Bundled key extracted from a public binary and abused on third-party projects, exhausting the shared quota.** Mitigations: (a) the user-key fallback is one click; (b) consider periodic shared-key rotation in the release runbook; (c) the existing per-server rate limiter bounds per-instance consumption.
- **A future contributor accidentally commits a non-placeholder key.** Mitigation: pre-commit hook (or CI check) that rejects commits where `OMDb:SoftMediaApiKey` differs from `SOFTMEDIA_OMDB_KEY_PLACEHOLDER` in any committed `appsettings.json` file.

---

### P1-WI-005 — Scheduled-Tasks Administrative Page (Read-Only)

#### Motivation

Several background services run on every SoftMedia instance — `HeroCacheWorker`, `RefreshTokenCleanupService`, `MetadataRefreshService`, `LibraryWatcher`, `ImageDownloadQueueService`, and (once Phase 1 ships) `BackupRotationService`. Today, none of them report when they last ran or what they did. The administrator has neither a debugging tool ("did the metadata refresh actually fire?") nor a trust signal ("the server says it scanned 2 hours ago — I believe it").

#### Specification

##### Task Registry

New interface `IScheduledTask`:

| Member | Purpose |
|--------|---------|
| `Name` (string) | friendly name |
| `Description` (string) | one-line purpose |
| `LastRunUtc` (DateTime?) | most recent execution timestamp |
| `LastRunDurationMs` (long?) | wall-clock duration of last run |
| `LastResult` (string?) | `"Success"` / `"Failed"` / `"Skipped"` / `null` |
| `LastError` (string?) | exception message if `Failed` |
| `NextRunUtc` (DateTime?) | next scheduled run; `null` for event-driven tasks |
| `SupportsManualTrigger` (bool) | whether `TriggerAsync` is exposed via API |
| `TriggerAsync()` | manual invocation entry point |

New singleton `ScheduledTaskRegistry` maintains an in-memory list of registered tasks. Each existing background service registers itself in `Program.cs` and updates its row in the registry on each cycle.

##### Endpoints (admin-only)

- `GET /api/v1/admin/tasks` — full registry as JSON.
- `POST /api/v1/admin/tasks/{name}/trigger` — invokes `TriggerAsync()` if `SupportsManualTrigger`. Returns `202 Accepted`; result reflected on next `GET`.

##### UI

New `Settings > Administration > Background Tasks` page: table with columns Name, Description, Last Run (relative), Result, Next Run, and a `Run Now` action when supported.

##### Initial Coverage

Tasks expected in the registry after Phase 1 completion:

- `HeroCacheWorker`
- `RefreshTokenCleanupService`
- `MetadataRefreshService` — already supports manual trigger via the legacy path `SettingsController.cs:42`; adapt to the new generic endpoint.
- `LibraryWatcher` — event-driven; `NextRunUtc` is null.
- `ImageDownloadQueueService`
- `BackupRotationService` — added by P1-WI-001.

The legacy `SettingsController.cs:42` endpoint is retained for backwards compatibility but marked `[Obsolete]` in favour of the generic path.

#### Files Affected

- `src/SoftMedia.Server/Services/Abstractions/IScheduledTask.cs` — **new**.
- `src/SoftMedia.Server/Services/Infrastructure/ScheduledTaskRegistry.cs` — **new**.
- `src/SoftMedia.Server/Services/Background/*.cs` — each background service implements `IScheduledTask` and reports state.
- `src/SoftMedia.Server/Controllers/AdminController.cs` — extend with task endpoints.
- `src/SoftMedia.Client/src/pages/SettingsPage.tsx` — add Background Tasks section.

#### Acceptance Criteria

- `GET /api/v1/admin/tasks` returns ≥ 6 tasks after Phase 1 completion.
- Each task's `LastRunUtc` updates after the next scheduled cycle.
- Manual trigger of `MetadataRefreshService` reflects in `LastRunUtc` within one second.
- A task that throws on its cycle reports `LastResult="Failed"` with `LastError` populated; the registry survives (the singleton is not replaced).

#### Estimated Effort

3-4 days.

#### Dependencies

- Best implemented after P1-WI-001 so `BackupRotationService` can be registered alongside.

#### Risks

- **Adding `IScheduledTask` to legacy background services could regress their existing contracts.** Mitigation: `IScheduledTask` adoption is additive; existing `IHostedService` contracts are not altered.

## 6. Phase Exit Criteria

Phase 1 is complete when:

- All five work items report acceptance criteria passing in CI.
- A maintainer has merged the changes to `main`.
- A demonstration in a fresh installation shows: backup-restore round-trip, mint-and-use API token, hit a bitrate cap, and view the background-task page.
- Change log in `00-roadmap-overview.md` records phase completion.

## 7. Out of Scope

- Fine-grained API-token scopes beyond the four enumerated in P1-WI-002.
- Restore of backups taken from a *different* SoftMedia schema version.
- Backup encryption-at-rest. The backup zip contains sensitive material; that is documented but not encrypted at this phase. Operator chooses the destination.
- Cron-class user-defined scheduled tasks. The page in P1-WI-005 is read-only for non-built-in tasks.
- Remote backup destinations (S3, B2, etc.). Local-only in this phase.

## 8. Verification Log *(added 2026-05-13)*

Build: `dotnet build src/SoftMedia.Server` succeeds (0 errors). Client `tsc -b --noEmit` clean. Server tests: 592 passed / 1 skipped / 0 failed (one transient harness flake in `ResetSeedNoiseAsync` cleared on re-run — a pre-existing parallel-SQLite race, not introduced here).

### P1-WI-001 — Backup / Restore
- **New:** `Controllers/HealthController.cs` (anonymous `GET /api/v1/health` — the spec wrongly assumed this existed), `Helpers/PendingRestore.cs` (boot-time DB swap), `Services/Infrastructure/BackupService.cs`, `Services/Background/BackupRotationService.cs`, `Services/Abstractions/IBackupService.cs`, `DTOs/BackupDtos.cs`, `components/admin/BackupCard.tsx`.
- **Changed:** explicit `Microsoft.Data.Sqlite` package reference added; `Program.cs` applies `PendingRestore` before DbInitializer; `Maintenance.*` settings seeded; `AdminController` backup/restore/history/pin/download endpoints; `adminService.ts` + dashboard card.
- **Design corrections applied:** restore is non-destructive (stage `.restore-pending`, swap on next boot) so the spec's "all routes 503 during restore" maintenance-state was dropped as unnecessary; DB path resolved from the live connection's `DataSource` (not hardcoded); real cache dirs excluded (`transcode-temp/`, `wwwroot/cache/`), not the fictional `data/` tree; `Pooling=False` on temp connections (a real bug the round-trip test caught).
- **Tests:** `BackupServiceTests` (10) + `AdminBackupIntegrationTests` (4) — round-trip, manifest SHA-256, path-traversal rejection, schema-version guard, rotation+pinning, auth.

### P1-WI-002 — Per-User API Tokens
- **New:** `Models/ApiToken.cs` (+ scopes), `Services/Identity/{ApiTokenService,ApiTokenAuthenticationHandler,ScopeAuthorization}.cs`, migration `AddApiTokens`, `components/account/ApiTokensCard.tsx`.
- **Critical correction applied:** the spec's "extend `OnMessageReceived` to accept `sm_`" design **would not work** (the JWT validator rejects opaque tokens). Implemented instead as a **policy scheme** (`SmartAuth`) forwarding `Bearer sm_*` to a dedicated `ApiToken` AuthenticationHandler, JWT otherwise. Scope enforcement (which the spec assumed existed) was built from scratch: `ScopeRequirement` + handler + per-scope policies; JWT sessions satisfy all scopes, API tokens must hold the scope.
- **Tests:** `ApiTokenIntegrationTests` (8) — mint/use/revoke, read-only token 403 on write, write token passes, admin-scope-non-admin rejected, hash-only persistence.

### P1-WI-003 — Streaming Policy
- **Reconciliation (per rescope):** reused existing enforced `MaxSimultaneousTranscodes` (global) and `MaxStreamingBitrate` (WAN); added only `MaxSimultaneousTranscodesPerUser`, `MaxStreamingBitrateLan`, and `User.MaxStreamBitrateKbps` (migration `AddUserMaxStreamBitrate`). No duplicate settings created.
- **New:** `Services/Infrastructure/NetworkClassifier.cs`. **Changed:** `StreamPlanService.ComputeStreamPlanAsync` takes client IP + per-user bitrate, picks LAN/WAN/user cap, annotates `Reason`; `TranscodeService` throws `TranscodeCapacityException` (global + per-user) which `TranscodeController` maps to **429 + Retry-After** — fixing the pre-existing 500-on-cap bug.
- **Tests:** `NetworkClassifierTests` (15) + `StreamPlanServiceBitrateTests` (5).

### P1-WI-005 — Scheduled-Tasks Page
- **New:** `Services/Infrastructure/{ScheduledTaskRegistry,ScheduledTaskRegistrySeeder}.cs`, `components/admin/ScheduledTasksCard.tsx`. **Changed:** all 11 known background tasks seeded (spec listed only 5); `HeroCacheWorker` / `BackupRotationService` / `MetadataRefreshService` report telemetry; `AdminController` `GET /tasks` + `POST /tasks/{name}/trigger`; legacy `SettingsController.refresh-metadata` marked `[Obsolete]`.
- **Honest classification:** queue/watcher services are `EventDriven` (null NextRun); only clock-driven services show a next-run.
- **Tests:** `ScheduledTasksIntegrationTests` (4) — auth, registry seeded (≥6), manual trigger reflects in LastRun, unsupported task 400.

### P1-WI-004 — OMDb (partial)
- **Done (UI + docs):** standing "use your own key" helper in shared mode (`SettingsPage.tsx`); `docs/OMDB_API_KEY_SETUP.md` extended with the maintainer-funded model + the exit-ramp guarantee + rollout status.
- **Blocked (maintainer):** real-key injection needs (1) the OMDb tier decision (Open Q #5) and (2) a CI pipeline — `.github/workflows/` does not exist. Tracked as a separate prerequisite.
- **Correction applied:** the spec's quota-driven banner cannot fire for the shared key (its `OMDbDailyCount` is only tracked in `custom` mode), so the UI is a standing helper rather than an exhaustion banner.
