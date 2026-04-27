# SoftMedia eReader — Search + Annotations

**Status:** Complete — 2026-04-19
**Scope reference:** [ereader-roadmap.md](ereader-roadmap.md), tasks ER-024, ER-040, ER-041
**Predecessors:** [ereader-plan-quickwins.md](ereader-plan-quickwins.md), [ereader-plan-customisation.md](ereader-plan-customisation.md), [ereader-plan-comfort-bookmarks.md](ereader-plan-comfort-bookmarks.md)

## 1. Scope & rationale

Milestone 4 closes Phase 2 (search) and opens Phase 4 (annotations). These three tasks turn the reader from a page-turning surface into a research-capable tool.

**In scope:**

| ID | Title | Phase | Priority | Effort | Surface |
|---|---|---|---|---|---|
| ER-024 | In-book search (EPUB + PDF) | 2 | P1 | L | Frontend (mostly) |
| ER-040 | Highlights | 4 | P2 | L | Full-stack |
| ER-041 | Notes attached to highlights | 4 | P2 | M | Full-stack |

**Deferred:** ER-032 (page thumbnail scrubber) — standalone, full-stack; cleaner as its own milestone. ER-050+ Phase 5 items remain future work.

## 2. Ordering

1. **ER-024** first — pure frontend, depends only on ER-003 (PDF text layer, already shipped).
2. **ER-040** — introduces a new DB table + CRUD + selection UI. Tests ship alongside the entity migration.
3. **ER-041** — additive: a single `Note` column on the existing `Highlight` entity + editor in the existing highlight UI.

## 3. Cross-cutting standards

Same contracts as prior milestones — back-to-front for backend tasks, Universal-Client for all controls, CSS-variable theming, no raw SQL, per-migration-per-schema-change, tests first on the backend.

## 4. Workstreams — acceptance summary

- **ER-024:** EPUB uses `book.search(query)` across all spine items; PDF uses pdf.js `find` controller. Result list shows context snippets; clicking a result jumps and transiently highlights the match. Search bar opens via `/` keyboard shortcut. CBZ/CBR disabled in the UI.
- **ER-040:** New `Highlight` entity keyed on `(Id, UserId, MediaItemId)` with `LocationJson` (CFI range for EPUB, page+rect for PDF), `Colour`, `QuotedText`, `CreatedAt`, `UpdatedAt`. CRUD under `/api/v1/books/{id}/highlights`. Selection → colour picker → POST highlight. Highlights render overlaid on their original location on every load. Viewable in a sheet with jump-to-highlight. Markdown export.
- **ER-041:** Adds `Note string?` to `Highlight`. Click on highlight shows a textarea editor. Notes included in the Markdown export.

## 5. Risks & mitigations

| Risk | Mitigation |
|---|---|
| EPUB search is O(n spines) and can freeze large books | Spawn one promise per spine item, chain with `Promise.all`; throttle the UI to show results incrementally. Cancel on query change. |
| PDF find controller API varies by pdf.js minor version | Feature-detect the controller before calling; if missing, hide PDF search with a log warning. |
| Highlight CFI/rect format divergence between formats | One JSON column, two discriminated shapes. Frontend builds the shape based on active format; backend treats as opaque. |
| Selection events don't fire consistently inside the EPUB iframe | Hook into `rendition.hooks.content` to attach `selectionchange` / `mouseup` inside the iframe document. |
| Highlight rendering race with epub.js re-layout | Re-apply on `rendition.on('relocated')` — not on mount only. |
| Markdown export buffer grows large on heavily-highlighted books | Generate on demand (button press), not eagerly. Use a `Blob` + `URL.createObjectURL` to stream to the user's file system rather than allocating a DOM string. |

## 6. Acceptance checklist

- [x] **ER-024** Search bar in the reader (shortcut `/`). EPUB returns hits with surrounding snippets; PDF returns pages. Click jumps. CBZ disabled with explanatory copy.
- [x] **ER-040** Highlight CRUD + colour picker; EPUB highlights render via `rendition.annotations`; sheet jumps; Markdown export.
- [x] **ER-041** Notes added via inline textarea on each highlight row; included in Markdown export.

## 7. Out of scope (explicit)

- ER-032 (thumbnail scrubber) — Milestone 5 candidate.
- OCR for CBZ/CBR search — separate project.
- Collaborative / shared highlights — local-first policy.
- Highlight export formats beyond Markdown (CSV, JSON) — trivial follow-ups if users ask.

## 8. Change log

| Date | Author | Change |
|---|---|---|
| 2026-04-19 | — | Initial draft. |
| 2026-04-19 | — | All three workstreams shipped. Frontend: 54/54 reader + hook + store tests pass. Server: 221/221 tests pass (1 skipped CBR fixture request). One new DB migration (`AddHighlights`). ER-041 rode on ER-040's Note column — no separate migration. |
