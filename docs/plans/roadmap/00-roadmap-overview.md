# SoftMedia Development Roadmap — Overview

**Version:** 1.0.0
**Status:** Active
**Date:** 2026-05-11
**Owner:** Project Maintainer
**Supersedes (rationale of):** `docs/plans/feature-roadmap-post-gap-analysis-2026-05-11.md` (retained as the decision-record document)

## 1. Purpose

This roadmap defines SoftMedia's development sequence following the 2026-05-07 feature gap analysis (`docs/reports/feature-gap-analysis-2026-05-07.md`). It is the authoritative reference for what work is planned, in what order, and with what acceptance criteria.

The roadmap is divided into five phases (0 through 4). Each phase has its own specification document. Phases are sequential by priority; individual items within a phase may be parallelised across contributors.

## 2. Phase Index

| Phase | Title | Status | Estimated Duration | Document |
|-------|-------|--------|--------------------|----------|
| 0 | Correctness Debt | **Complete** *(2026-05-13)* | 1-2 days | [phase-0-correctness-debt.md](./phase-0-correctness-debt.md) |
| 1 | Operational Trust | **Complete** *(2026-05-13; P1-WI-004 CI/key half blocked on maintainer)* | 2-3 weeks | [phase-1-operational-trust.md](./phase-1-operational-trust.md) |
| 2 | Quality of Life | **Complete** *(2026-05-13; P2-WI-004 partial — 3 events deferred)* | 4-6 weeks | [phase-2-quality-of-life.md](./phase-2-quality-of-life.md) |
| 3 | Differentiation | **Complete** *(2026-05-30; 2 of 5 items shipped per maintainer scope cut — see phase-3 §0)* | 2-3 weeks (revised from 6-10) | [phase-3-differentiation.md](./phase-3-differentiation.md) |
| 4 | Deferred (Reference Register) | n/a | n/a | [phase-4-deferred.md](./phase-4-deferred.md) |

## 3. One-Line Phase Goals

- **Phase 0** — Resolve known correctness debt before adding surface area.
- **Phase 1** — Make the server trustworthy for operators: backup, programmatic access, streaming-policy limits, OMDb shared-key rollout, background-task visibility.
- **Phase 2** — Make the everyday experience pleasant: trickplay previews, transcode explainer, PWA, outbound webhooks, optional 2FA.
- **Phase 3** — Build features where SoftMedia can plausibly beat Plex and Jellyfin.
- **Phase 4** — Catalogue features deliberately deferred, with rationale and re-activation criteria.

## 4. Filter Criteria

Every item in this roadmap was assessed against the following filters. Items that failed any filter were either re-scoped or moved to Phase 4.

1. **OSS-feasible.** No proprietary SDKs, paid APIs, or ongoing third-party fees beyond the maintainer-funded OMDb shared key (see `phase-1-operational-trust.md` § P1-WI-004).
2. **Bounded scope.** Each item is finishable by one engineer within a focused sprint.
3. **Privacy-charter aligned.** No telemetry. No first-party cloud relay. No mandatory account.
4. **Closes a real adoption objection.** Either fixes a "but does it…?" gap, or unblocks an integration ecosystem.
5. **Correctness over feature work.** Latent bugs in shipped features take precedence over new feature work.

## 5. Cross-Cutting Engineering Rules

The following rules apply to *every* work item in *every* phase. They restate constraints from `docs/rules/` and `docs/SDD.md` for the convenience of contributors picking up a roadmap item.

- **Back-to-front development.** Backend endpoint + DTO + xUnit tests exist and pass before any React component consumes them. (`docs/rules/01-core-philosophy.md`.)
- **Layering.** Controllers → Services → Repositories → DbContext. No new static globals; resolve via the DI container.
- **Universal Client.** Every interactive element must satisfy the accessibility, focus-visibility, and 44×44px touch-target rules in `docs/rules/01-core-philosophy.md`.
- **Type-locked metadata providers.** No work item may introduce a metadata provider that violates the `LibraryType → Provider` mapping in SDD §4.3.
- **Path canonicalisation.** Any work item that touches file paths must respect the jailing rules in SDD §6.2 — `Path.GetFullPath` alone is insufficient; symlinks must be resolved via `FileInfo.ResolveLinkTarget(returnFinalTarget: true)`.
- **Parameterised queries only.** Use EF Core; if raw SQL is unavoidable, use parameter binding — never string interpolation.

## 6. Document Conventions

Each work item carries:

| Field | Purpose |
|-------|---------|
| **ID** | Stable identifier `P{phase}-WI-{number}` (e.g. `P1-WI-002`). Used in commits, branches, and PR titles. |
| **Motivation** | Why this work exists. Connects to user-visible value or correctness debt. |
| **Specification** | The contract — what is being built, in implementation-ready detail. |
| **Files Affected** | Paths in the repository where the change anchors. New files marked accordingly. |
| **Acceptance Criteria** | Testable assertions that mark the item complete. Each criterion maps to a test or a manual-verification step. |
| **Estimated Effort** | Hours or days for a single engineer of average familiarity with the codebase. |
| **Dependencies** | Other work items (in any phase) that must complete first. |
| **Risks** | Known risks with mitigations. |

Status fields in each phase document use the controlled vocabulary: `Not Started`, `In Progress`, `Blocked`, `Complete`.

## 7. Status Tracking

Status is tracked in-document, not in a separate tool. Each phase document carries:

- A header `Status` field.
- A work-item table summarising per-item status.
- Phase Exit Criteria — the testable conditions that mark the phase complete.

Status updates are made by amending the phase document in the same commit that lands the relevant change.

## 8. Change Control

Material changes to this roadmap require:

1. A change-log entry in §9 below.
2. Maintainer sign-off.
3. Update of the affected phase document.

Minor edits (typo fixes, link updates, clarifications without scope change) do not require a change-log entry.

## 9. Change Log

| Date | Change | Author |
|------|--------|--------|
| 2026-05-11 | Initial roadmap derived from `docs/reports/feature-gap-analysis-2026-05-07.md`. | Engineering review |
| 2026-05-13 | P0-WI-002 rescoped after pre-implementation review. SDD §4.2 / §6.2 SameSite drift confirmed already resolved by the 2026-04-26 hardening pass (F1 in `docs/plans/hardening-and-closure-plan-2026-04-26.md`). Stale references corrected to actual locations: prompt template, two TODO files, SDD §4.4 video-player wording. | Engineering review |
| 2026-05-13 | **Phase 0 complete.** P0-WI-001 (Forwarded Headers) and rescoped P0-WI-002 (stale-reference cleanup) shipped on branch `security/hardening-wave-1`. New integration test `ForwardedHeadersIntegrationTests` passes; 15 existing auth + smoke tests pass without regression. See `phase-0-correctness-debt.md` §8 for the full verification log. | Engineering execution |
| 2026-05-13 | **Phase 1 pre-implementation review.** A 5-agent parallel verification found all five Phase 1 work items needed rescoping before coding (3 stale premises, 1 latent design bug in the API-token auth hook, 1 incomplete service inventory). Authoritative corrections recorded in `phase-1-rescope-2026-05-13.md`; phase-1 doc artifacts fixed and pointer added. Phase 1 status → In Progress. | Engineering review |
| 2026-05-13 | **Phase 1 implemented** on branch `security/hardening-wave-1` per the rescope. P1-WI-001/002/003/005 complete; P1-WI-004 UI+docs done with CI/real-key half blocked on the maintainer (OMDb tier + no CI pipeline). +33 tests (560→593 total; 592 pass / 1 skip, one transient harness flake). Notable: fixed a pre-existing 500-on-transcode-cap bug; built API-token auth as a policy scheme (the spec's `OnMessageReceived` approach would not work); added a `/api/v1/health` endpoint the spec assumed existed. Full log in `phase-1-operational-trust.md` §8. | Engineering execution |
| 2026-05-13 | **Phase 2 pre-implementation review.** A 5-agent parallel verification found all five Phase 2 items needed rescoping: trickplay's `data/` path fictional (→ `wwwroot/cache/`); explainer's structured reason keys don't exist (StreamPlanService emits free-form English — refactor to `{code,params}`); PWA premise that ASP.NET serves the SPA is false; webhooks lack clean hooks for 3/5 events (deferred to follow-up); TOTP needs a new (non-IP) rate-limit policy. Corrections in `phase-2-rescope-2026-05-13.md`; phase-2 status → In Progress. | Engineering review |
| 2026-05-13 | **Phase 2 implemented** on `security/hardening-wave-1`. P2-WI-001/002/003/005 complete; P2-WI-004 ships `library.scan.*` + `webhook.test` (the 3 events without clean hooks deferred). +21 server tests (603→624; 624 pass / 1 skip, same harness flake), 152 client tests pass, PWA build emits sw.js + manifest. Notable: reused the existing StreamPlan + i18next rather than new endpoint/JSON; trickplay uses `wwwroot/cache` + own semaphore; new `"2fa"` rate policy; webhook SSRF guard + HMAC signing. Full log in `phase-2-quality-of-life.md` §8. | Engineering execution |
| 2026-05-30 | **Security fixes (between phases)** — two findings from automated security review: (a) **SSRF via HTTP redirect** in `WebhookDispatchWorker` — `HttpClient` followed 3xx by default, bypassing the pre-send SSRF guard; fixed by configuring `AllowAutoRedirect=false` and treating any 3xx as a permanent block (regression guarded by `WebhookRedirectPolicyTests`). (b) **`ScopeAuthorization` fail-open** — anonymous requests passed scope-gated endpoints because the handler treated "no scope claim" as "full session"; fixed by failing closed for unauthenticated principals and adding `RequireAuthenticatedUser()` to every scope policy. New regression test `Anonymous_Is401_OnScopeGatedWriteEndpoint`. | Engineering execution |
| 2026-05-30 | **Phase 3 scope cut by maintainer.** Original 5 items reduced to 2: P3-WI-002 OpenSubtitles dropped (embedded+sidecar cover the case; privacy charter); P3-WI-004 smart playlists/tags dropped (no fit); P3-WI-005 reserved community slot dropped. Recorded in phase-3 §0. | Maintainer decision |
| 2026-05-30 | **Phase 3 implemented.** Two items shipped: P3-WI-001 Chromecast sender (Cast SDK + useCast hook + cast button in player) and P3-WI-003 Full manual match (MetadataLocked field + single-chokepoint guard at `MetadataQueueService.ProcessItemAsync` + `ISearchableMetadataProvider` implemented in all 5 providers + admin search/apply/edit/unlock endpoints + FixMatchCard UI). +11 server tests (624→635), 152 client tests pass. Verification finding paid off: providers already had private search paths, so `SearchAsync` was mostly refactor not net-new code. Full log in `phase-3-differentiation.md` §8. | Engineering execution |

## 10. Open Questions (Maintainer Sign-Off Pending)

These were enumerated in `docs/plans/feature-roadmap-post-gap-analysis-2026-05-11.md` and remain open here. Resolutions update the relevant work item.

1. Is `[Server] > Maintenance` the right home for backup/restore (P1-WI-001), or should it live under a top-level `[Admin]` tree?
2. Do per-user API tokens (P1-WI-002) need fine-grained scopes for v1, or is the four-scope coarse model sufficient?
3. Should the per-user bandwidth cap (P1-WI-003) be admin-only or self-service (a user-facing "limit my own bandwidth on cellular" toggle)?
4. Are we comfortable shipping Chromecast (P3-WI-001) before TOTP 2FA (P2-WI-005)? Default sequencing says no; Cast is technically the easier path.
5. Which OMDb tier (`free` / `basic` / `standard` / `pro`) does the project commit to funding (P1-WI-004)?

## 11. Companion Documents

- `docs/reports/feature-gap-analysis-2026-05-07.md` — the gap analysis that informed this roadmap.
- `docs/plans/feature-roadmap-post-gap-analysis-2026-05-11.md` — the prose rationale document this roadmap formalises.
- `docs/SDD.md` — the authoritative software design document.
- `docs/rules/` — always-on engineering rules.
- `docs/user-docs/features/` — per-feature behaviour specifications (the user-facing counterpart of this internal roadmap).
