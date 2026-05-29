# Phase 1 — Pre-Implementation Review & Rescope

**Date:** 2026-05-13
**Status:** Authoritative — supersedes the original specifications in `phase-1-operational-trust.md` where they conflict
**Method:** Five parallel deep-recon agents verified each work-item spec against the live codebase before any code was written. Every claim below is grounded in a file actually read during the review; high-stakes claims were additionally spot-verified by hand.

## Why this document exists

The Phase 1 specifications in `phase-1-operational-trust.md` were authored from the gap analysis and memory. A Phase 0 review had already caught one work item built on a stale premise (a SameSite "drift" that was already fixed). This review applied the same scrutiny to Phase 1 and found that **all five work items required rescoping** — three on demonstrably stale premises, one on a latent correctness bug in the proposed design, and one on an incomplete inventory.

Read the corrected approach here **before** implementing any Phase 1 item. The original spec bodies are retained in `phase-1-operational-trust.md` for context.

---

## P1-WI-001 — Backup / Restore — `proceed-with-rescope`

### Already correct in the spec
- `AdminController.cs:18-21` carries class-level `[Authorize(Roles="Admin")]` on route `api/v1/admin` — new backup endpoints need no per-action auth attribute.
- No pre-existing backup code or `Maintenance` settings to collide with.
- `AddBackgroundServices` (`ServiceCollectionExtensions.cs:328`) is the correct registration point for `BackupRotationService`.

### Corrections (stale / incorrect premises)
1. **The cited "stub" is a complete design, not a stub.** `docs/todos/feature-shortlist/02-admin-backup-endpoint.md` (232 lines) fully specs `IBackupService` + `BackupService` with a working `SqliteConnection.BackupDatabase` implementation, the controller endpoint, and tests — for the **download half only** (it explicitly defers restore/rotation/encryption). **Adopt that design verbatim for the download half**, then layer restore/history/pin/rotation on top.
2. **`GET /api/v1/health` does NOT exist.** No health controller, no `MapHealthChecks`, no `AddHealthChecks`. The restore maintenance-state ("all routes 503 except `/api/v1/health`") depends on it. **Adding the health endpoint is a new sub-task of this item.**
3. **`Microsoft.Data.Sqlite` is only a transitive dependency** (via `Microsoft.EntityFrameworkCore.Sqlite 8.0.2`). `SqliteConnection.BackupDatabase` is load-bearing, so add an explicit `<PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.2" />` to `SoftMedia.Server.csproj`.
4. **The `data/` backup-contents tree is fictional.** The DB is configured as `Data Source=softmedia.db` (a bare relative path, not under `data/`). There is no `data/` directory in the repo. The real cache dirs to exclude are `transcode-temp/` (CWD root) and `wwwroot/cache/` (image/thumbnail cache). `data/trickplay` is an unbuilt Phase 2 feature — remove it from the exclusion list. **`BackupService` must resolve the live DB path from the open `SqliteConnection.DataSource` at runtime**, not by re-reading `appsettings.json`, because dev/prod may override the connection string via env or user-secrets (the on-disk dev DB is actually `app.db`).
5. **No WAL mode is configured.** `BackupDatabase` is correct under any journal mode, so keep it, but drop the "raw copy forbidden while WAL active" rationale (restate as defense-in-depth if desired).
6. **Open Question #1 (Server>Maintenance vs top-level [Admin]) is unresolved.** *Decision for this implementation:* use settings group `Maintenance` within the existing DB-backed settings system (consistent with the existing `Streaming`/`Scanning`/`Metadata` groups). This is reversible — it is a string category, not a schema commitment.

### Net effect
Larger than the original estimate in one respect (a health endpoint is now in scope) and smaller in another (the download half is already designed). Restore must rely on `DbInitializer.InitializeAsync` (runs migrations+seed at startup, `Program.cs`) on the post-restore restart rather than re-implementing seed logic.

---

## P1-WI-002 — Per-User API Tokens — `proceed-with-rescope`

### Already correct in the spec
- `AccountController.cs` exists with a clean pattern; tokens endpoints fit (or use a dedicated `ApiTokensController`).
- No pre-existing `ApiToken` model/table.
- `AppDbContext` + `OnModelCreating` + `HasIndex` pattern is the template for the new table.
- `User.GetUserId()` resolves the caller; `RefreshTokenService` (SHA-256, issue-raw-once, lookup-by-hash, Base64Url) is the **template to mirror** for `ApiTokenService`.

### Corrections
1. **CRITICAL — the proposed auth hook is wrong and will not work.** The spec says to extend `JwtBearerEvents.OnMessageReceived` to accept `Authorization: Bearer sm_*`. But `OnMessageReceived` only sets the raw token string; the JwtBearer handler then tries to **validate it as a JWT**, and an opaque `sm_` token has no signature/claims, so validation fails before any custom code runs. **Correct design:** register a second `AuthenticationScheme` (e.g. `"ApiToken"`) with a custom `AuthenticationHandler`, and a **policy scheme** as the default whose `ForwardDefaultSelector` inspects the `Authorization` header — `sm_`-prefixed → `ApiToken` scheme, everything else → JWT bearer. Keep the existing query-string token lift for JWT only.
2. **Scope enforcement needs explicit wiring.** The spec lists scopes (`read:library`, `read:state`, `write:state`, `admin`) but no enforcement. Add authorization policies backed by a scope-claim check, and decide which existing endpoints require which scope. For v1, the `ApiToken` principal carries scope claims; `[Authorize(Policy="scope:write")]` etc. gate the relevant actions.
3. **Admin-scope decision.** *Decision for this implementation:* an `admin` scope may be minted **only** by a user whose `Role == Admin`, and the resulting `ApiToken` principal carries the `Role=Admin` claim so it can reach `[Authorize(Roles="Admin")]` endpoints. This is a deliberate, documented security decision (a leaked admin token is equivalent to admin access — hence revocation + last-used tracking matter).
4. **`MyAccountPage.tsx` is already large** — add the tokens UI as a child component, not inline.

---

## P1-WI-003 — Streaming Policy — `proceed-with-rescope` (most severe)

### Hand-verified findings
- `MaxSimultaneousTranscodes` (`SettingsService.cs:95`, "0 = unlimited") is **enforced**: `TranscodeService.cs:230-232` throws when `GetActiveTranscodeCount() >= maxConcurrent`.
- `MaxStreamingBitrate` (`SettingsService.cs:105`, 20000) is **applied**: `StreamPlanService.cs:240-241` clamps to it (`> 0 ? maxBitrate : null`).
- `MaxAudioStreamingBitrate` (`SettingsService.cs:108`) feeds the audio plan.
- `User` model has **no** bitrate field.
- Audio transcodes are **not** counted by the transcode throttle (it is video-HLS oriented).

### Corrections — the spec's four new settings collapse to one new setting + one split
| Spec proposed | Reality | Action |
|---|---|---|
| `MaxConcurrentTranscodesGlobal` | duplicate of enforced `MaxSimultaneousTranscodes` | **DROP** — reuse existing key |
| `MaxConcurrentTranscodesPerUser` | genuinely new | **ADD** |
| `MaxStreamBitrateKbpsWAN` | duplicate of applied `MaxStreamingBitrate` (remote cap) | **DROP** — treat `MaxStreamingBitrate` as the WAN/remote cap |
| `MaxStreamBitrateKbpsLAN` | genuinely new (no network awareness today) | **ADD** as a LAN override, default `0` = unlimited |
| `User.MaxStreamBitrateKbps` | genuinely new | **ADD** (+ migration) |

Additional corrections:
- **Extend the existing throttle/session accounting** (`TranscodeSessionManager.GetActiveTranscodeCount`) to support a per-user count keyed by `userId`; do not add a parallel session registry.
- **Drop the "0.25 audio weight" for v1.** Audio is not counted by the throttle at all today; including it is a separate change. v1 scope: video transcodes only count toward the per-user cap.
- **Add `NetworkClassifier`** (RFC1918 / loopback / ULA `fc00::/7`) and thread `HttpContext.Connection.RemoteIpAddress` (now correct post-P0) from the stream controllers into `StreamPlanService.ComputeStreamPlanAsync` — it is not currently a parameter.
- **Reconcile the 429 behavior.** Today the global cap throws `InvalidOperationException` (`TranscodeService.cs:232`), which surfaces as a 500. The rescoped item should make both the global and per-user caps return `429 Too Many Requests` with `Retry-After`, fixing the existing 500 as part of the work.

---

## P1-WI-004 — OMDb Shared-Key Rollout — `proceed-with-rescope` (key half blocked)

### Already correct in the spec
- `OMDbProvider.cs` three-mode switch, placeholder constant, tier table, daily-count tracking — all confirmed.
- `appsettings.json` placeholder present (added in P0).
- Five `OMDb*` settings present (`SettingsService.cs:118-122`).
- `SettingsPage.tsx` already renders an OMDb usage widget (`notificationService.getOMDbUsage`, 30s refetch, 75/90/100% color thresholds).

### Corrections
1. **No `.github/workflows` directory exists — there is no CI pipeline at all.** "Add a key-injection step" is really "create the first CI/release pipeline." **Split CI bootstrap into its own ticket** (it is a prerequisite, materially larger than a step). The key-injection half is additionally **blocked on Open Question #5** (which OMDb tier the maintainer funds) and on a real key being available as a CI secret.
2. **`docs/OMDB_API_KEY_SETUP.md` already exists.** **Extend it** (optionally relocate to `docs/user-docs/features/omdb-key.md`) rather than creating a duplicate.
3. **The quota banner extends the existing usage widget** in `SettingsPage.tsx` — reuse `getOMDbUsage`, do not build a new component from scratch.
4. **Verify the low-quota → `SystemNotification` path actually fires** at the tier limit; wire the comparison if missing rather than adding a parallel notifier.

### Net effect
This item splits into: **(a) UI + docs half** — unblocked, can proceed (extend the usage widget into a quota banner with a "use your own key" CTA; extend the existing OMDb doc). **(b) CI + key-injection half** — blocked on maintainer (tier decision + real key + first CI pipeline). Track (b) separately.

---

## P1-WI-005 — Scheduled-Tasks Page — `proceed-with-rescope`

### Already correct in the spec
- `AddBackgroundServices` (`ServiceCollectionExtensions.cs:328-361`) is the registration point.
- `SettingsController.cs:39-47` `TriggerMetadataRefresh` → `MetadataRefreshService.TriggerRefreshNow()` is the cited manual trigger.
- No `IScheduledTask` abstraction or task-status tracking exists — genuinely new.
- `AdminController.cs` is the right home; it already exposes operational data (FileWatcherIssues), a good precedent.
- `MediaHub` exists (optional live updates; polling is fine for v1).

### Corrections
1. **The service inventory is incomplete.** The spec lists 5; there are ~10 hosted services: `HeroCacheWorker`, `RefreshTokenCleanupService`, `MetadataRefreshService`, `LibraryWatcher`, `ImageDownloadQueueService`, **plus** `ThrottleMonitorService`, `TranscodeSegmentCleanupService`, `LibraryScanQueueService`, `MetadataQueueService`, `MetadataRetryService`. The registry must cover all of them or explicitly document exclusions.
2. **Loop patterns are heterogeneous** (`BackgroundService.ExecuteAsync` while-loops, Timer callbacks, queue readers) — there is no single uniform cycle method to wrap. Introduce a `ScheduledTaskRegistry` singleton + an explicit reporting call each service invokes at the top/bottom of its cycle.
3. **Classify each service** as schedule-driven (has a meaningful `NextRunUtc`) vs event/queue-driven (`NextRunUtc` = null) so the UI is honest. Several are queue-driven.
4. **Reuse the existing admin polling UI pattern** (TanStack `useQuery` `refetchInterval`, already used for FileWatcherIssues).
5. **Have `settings/refresh-metadata` delegate to the new generic trigger** (or mark `[Obsolete]`) to avoid two divergent trigger paths.

---

## Recommended implementation order (revised)

Dependencies and blocker status drive this order:

1. **P1-WI-001 Backup/Restore** (+ health endpoint) — foundational; `BackupRotationService` must exist before P1-WI-005 registers it. Fully unblocked.
2. **P1-WI-002 API tokens** — self-contained; unblocked; the auth-scheme correction must be applied.
3. **P1-WI-003 Streaming caps** — rescoped smaller (reuse existing); unblocked; also fixes the existing 500-on-cap bug.
4. **P1-WI-005 Scheduled tasks** — after 001 so `BackupRotationService` is in the inventory.
5. **P1-WI-004 OMDb** — UI/docs half only this phase; CI/key half tracked separately, blocked on maintainer.

## Maintainer decisions captured (made under Auto Mode; redirect if wrong)

- **Open Q #1:** settings group = `Maintenance` (DB-backed, consistent with existing groups).
- **Open Q #2:** API-token scopes = the four coarse scopes; admin scope mintable only by admins and carries the role claim.
- **Open Q #5 (OMDb tier):** unresolved — remains a maintainer decision; only the OMDb UI/docs half proceeds.
