# SoftMedia eReader — Reading Comfort + Bookmarks

**Status:** Complete — 2026-04-19
**Scope reference:** [ereader-roadmap.md](ereader-roadmap.md), tasks ER-012, ER-022, ER-031, ER-030, ER-023
**Predecessors:** [ereader-plan-quickwins.md](ereader-plan-quickwins.md), [ereader-plan-customisation.md](ereader-plan-customisation.md)

## 1. Scope & rationale

Milestone 3 finishes Phase 2's text-UX tail, lights up Phase 3's P1 visual features, and adds the first multi-annotation surface (bookmarks) — the foundation under ER-040 highlights later.

**In scope:**

| ID | Title | Phase | Priority | Effort | Surface |
|---|---|---|---|---|---|
| ER-012 | Per-book setting overrides | 1 | P2 | M | Full-stack |
| ER-022 | Publisher-style override toggle | 2 | P2 | S | Frontend |
| ER-031 | Right-to-left reading direction | 3 | P1 | S | Frontend |
| ER-030 | Zoom / fit-to-width / fit-to-page | 3 | P1 | M | Frontend |
| ER-023 | Bookmarks | 2 | P1 | M | Full-stack |

**Deferred to Milestone 4:** ER-024 (in-book search — L effort, unlocks once ER-023 UX proves out), ER-032 (page thumbnail scrubber — L, uses existing image cache), all Phase 4+ work.

## 2. Ordering

1. **ER-012** — full-stack foundation. Subsequent frontend tasks persist per-book where the roadmap says they should.
2. **ER-022** — trivial once ER-012 is in (per-book boolean).
3. **ER-031** — small, interacts with ER-002 spread.
4. **ER-030** — larger, per-book by nature.
5. **ER-023** — full-stack, independent of the others.

## 3. Cross-cutting standards

Same as prior milestones. Back-to-front for every backend change. No raw SQL, EF Core migrations only. Universal-Client rules on every new control. CSS-variable-driven styling — no literals except inside `FALLBACK` constants. Every migration ships alone.

## 4. Workstreams — acceptance summary

- **ER-012:** New `UserReaderPreferences` entity keyed on `(UserId, MediaItemId)` with a JSON blob + `SchemaVersion`. `GET /api/v1/interaction/{id}/reader-preferences` and `PUT` of the same shape. Frontend merges global defaults with per-book overrides; a "Save for this book" toggle in the panel writes current prefs as the override.
- **ER-022:** Toggle in Typography section, `Override publisher styles` default-on. When on, theme rules get injected with `!important` and publisher `<style>` tags are stripped from rendered chunks.
- **ER-031:** `Reading direction` segmented control (LTR / RTL). CBZ: swaps nav direction. EPUB: toggles `rendition.direction('rtl')` on mount + remounts. Double-page leading page stays on the correct side.
- **ER-030:** Zoom controls (`−`, `+`, `Fit width`, `Fit page`, `100%`) applied to PDF and CBZ. Pinch-to-zoom on touch; Ctrl-scroll on desktop. Arrow keys pan when zoomed; `PageUp`/`PageDown` always turn pages.
- **ER-023:** New `Bookmark` entity keyed on `(Id, UserId, MediaItemId)` + position/CFI/label. CRUD under `/api/v1/books/{id}/bookmarks`. Reader: Add-bookmark button (shortcut `b`), sheet lists all bookmarks.

## 5. Risks & mitigations

| Risk | Mitigation |
|---|---|
| JSON blob schema drift in ER-012 | `SchemaVersion` column alongside the JSON; migration on unknown version → reset that book's overrides with a log warning. |
| Publisher `!important` in ER-022 wins anyway via specificity | Strip `<style>` tags from chunks on `rendition.hooks.content`; accept that inline `style=""` attributes still win (document as limitation). |
| RTL + double-page ordering off-by-one | Dedicated test for RTL + spread; verify at pages 1, 2, N-1, N. |
| Zoom-pan gesture fights swipe nav | Disable horizontal swipe when zoom > 1.0; `PageUp/Down` remain. |
| Bookmark CFI invalid after book re-scan moves file | Bookmarks keyed on MediaItemId (stable across path changes); CFI validity depends only on the EPUB structure, not path. |

## 6. Acceptance checklist

- [x] **ER-012** Per-book overrides table + endpoints; client merges global + override, can save/clear per book.
- [x] **ER-022** Publisher-override toggle in Typography; default on.
- [x] **ER-031** LTR/RTL toggle; nav direction flips for CBZ; EPUB honours; works with double-page.
- [x] **ER-030** Zoom controls for PDF/CBZ; Ctrl-scroll wheel; scrollbar pan when zoomed; persists per-book.
- [x] **ER-023** Bookmarks CRUD; add-button + `b` shortcut; list sheet; per-user ownership enforced server-side.

## 7. Change log

| Date | Author | Change |
|---|---|---|
| 2026-04-19 | — | Initial draft. |
| 2026-04-19 | — | All five workstreams shipped. Frontend: 50/50 reader + hook + store tests pass. Server: 212/212 tests pass (1 skipped CBR fixture request). Two new DB migrations (`AddUserReaderPreferences`, `AddBookmarks`). One new runtime dependency (`SharpCompress`, carried over from M1). |
