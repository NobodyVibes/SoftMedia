# Feature Shortlist — Task Index

This folder is the actionable engineering ticket list for the five-item feature shortlist derived from the 2026-04-30 self-hosted gap-analysis report.

**Plan document (sequencing, ground rules, behavioral overview):** [docs/plans/feature-shortlist-plan-2026-04-30.md](../../plans/feature-shortlist-plan-2026-04-30.md)

## Tasks (recommended landing order)

| # | Task | Wave | Effort | Dependencies |
|---|------|------|--------|--------------|
| 01 | [Hide the Photo library type until Phase 2](./01-hide-photo-library.md) | A | 0.5 day | None |
| 02 | [Admin database backup endpoint](./02-admin-backup-endpoint.md) | B | 1 day | None |
| 03 | [Per-library access control](./03-per-library-acl.md) | C | 3–4 days | None — but blocks task 05 |
| 04 | [.nfo (Kodi/XBMC) sidecar metadata reader](./04-nfo-metadata-reader.md) | D | 2 days | None |
| 05 | [Persisted Playlists, Collections, and Watchlist](./05-playlists-collections-watchlist.md) | E | 5–7 days, three sub-PRs | **Requires task 03** |

## Conventions

Every task file follows the same structure:

- **Background** — why this exists, what code is already in place that we're mirroring.
- **Behavior after this task** — observable user-facing changes. Acceptance-test fodder.
- **Files to add / modify** — exact paths, exact changes. No implementation guesswork.
- **Tests** — exact test files and cases. xUnit for backend, Vitest + RTL for frontend.
- **Acceptance criteria** — checklist a reviewer can run.
- **Out of scope** — captured so the work stays focused.

When picking up a task, read the linked plan document first for sequencing rationale.
