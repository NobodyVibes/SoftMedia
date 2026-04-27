# SoftMedia eReader — Thumbnail Scrubber + Polish

**Status:** Complete — 2026-04-19
**Scope reference:** [ereader-roadmap.md](ereader-roadmap.md), tasks ER-032, ER-053, ER-054
**Predecessors:** [ereader-plan-search-annotations.md](ereader-plan-search-annotations.md), plus the three earlier milestones

## 1. Scope & rationale

Milestone 5 closes Phase 3 (thumbnail scrubber) and opens Phase 5 with two small
quality-of-life features — brightness/warmth overlay for night reading, and a
power-user keyboard-shortcut set with an in-app help sheet.

**In scope:**

| ID | Title | Phase | Priority | Effort | Surface |
|---|---|---|---|---|---|
| ER-032 | Page thumbnail scrubber | 3 | P2 | L | Full-stack |
| ER-053 | Brightness / warmth overlay | 5 | P3 | S | Frontend |
| ER-054 | Power-user keyboard shortcuts | 5 | P3 | S | Frontend |

**Deferred:** ER-050 (TTS), ER-051 (dictionary), ER-052 (reading stats) —
each sizable on their own; better as future milestones.

## 2. Ordering

1. **ER-032** — back-to-front: backend thumbnail endpoint + tests, then frontend preview.
2. **ER-053** — frontend-only.
3. **ER-054** — frontend-only. Written last so it can cite the final shortcut surface.

## 3. Workstreams — acceptance summary

- **ER-032:** Backend `GET /api/v1/books/{id}/thumbnail/{pageNumber}?size=sm` endpoint returning a ~160px-wide JPEG for CBZ and CBR pages. Memory-cached by `(path, mtime, page, size)`. PDF thumbnails render client-side via pdf.js because the server has no PDF rasteriser. Frontend: when the page-number label in `PageControls` is hovered (or the page-jump input is active), a thumbnail of the target page appears above it.
- **ER-053:** Two sliders in the Settings panel — brightness (0.3–1.0) and warmth (-1 to +1). Applied as a fixed overlay with CSS `filter` + a tinted layer over the reader viewport only. Persists via the existing `readerStore` (adds two fields).
- **ER-054:** Keyboard shortcuts wired in `BookReader`:
  - `t` — Table of Contents
  - `f` — Fullscreen
  - `i` — Immersive
  - `z` — Cycle reading theme (dark → sepia → high-contrast)
  - `+` / `-` — font size (EPUB) or zoom (PDF/CBZ)
  - `?` — Shows an in-app shortcut help sheet. Lists every shortcut including the pre-existing `b` (bookmark), `/` (search), `Esc` (cascade close), arrows/PageUp/Down.

## 4. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Thumbnail endpoint unbounded memory growth | Wrap in existing `IMemoryCache` with a size-limited region; cap entry size at 1. Evict LRU. |
| Server-side PDF rasterisation is a big rabbit hole | PDF thumbnails render client-side via pdf.js — the server returns 400 for PDF. Documented limitation. |
| Brightness overlay bleeds into TOC/Bookmarks/Highlights drawers | Scope the overlay to the content container, not the reader root. Drawers mount at a higher z-index. |
| Keyboard shortcuts collide with publisher-rendered EPUB inputs | Existing `INPUT/TEXTAREA` filter in the keydown handler already guards against this. |
| Help sheet lists stale shortcuts as features evolve | Derive the list from a single constant so adding/removing a shortcut updates the doc automatically. |

## 5. Acceptance checklist

- [x] **ER-032** CBZ thumbnail endpoint returns valid JPEG; 400 for PDF/EPUB; memory-cached via `IMemoryCache` keyed by (path, mtime, page, size); preview renders above PageControls while the page-number input is active.
- [x] **ER-053** Brightness + warmth sliders persist; applied as a z-10 overlay inside the content container so drawers at z-50+ stay unaffected; reset via existing "Reset to defaults" button.
- [x] **ER-054** `t / f / i / z / + / - / ?` wired; help sheet sourced from `SHORTCUTS` const — updating the const updates the sheet.

## 6. Change log

| Date | Author | Change |
|---|---|---|
| 2026-04-19 | — | Initial draft. |
| 2026-04-19 | — | All three workstreams shipped. Frontend: 57/57 reader + hook + store tests pass. Server: 227/227 tests pass (1 skipped CBR fixture request). No new DB migrations — ER-032 uses in-memory thumbnail cache only. |
