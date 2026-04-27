# SoftMedia eReader Roadmap

**Status:** Draft — 2026-04-19
**Scope:** Feature gaps in the book-reading experience, measured against [SDD §4.4 / §4.5](SDD.md), the current [BookReader component](../src/SoftMedia.Client/src/components/reader/BookReader.tsx), [BookController](../src/SoftMedia.Server/Controllers/BookController.cs), and the progress schema in [UserMediaInteraction.cs](../src/SoftMedia.Server/Models/UserMediaInteraction.cs).

## 1. Purpose

SoftMedia's eReader today supports paginated viewing of PDF, EPUB, and CBZ with remembered position. This document tracks the work required to bring it up to a production-grade reading experience comparable to Calibre-Web, Kavita, and Komga — while respecting SoftMedia's local-first, privacy-focused, dark-only constraints.

Tasks are grouped into **phases ordered by dependency, not perceived value** — earlier phases unblock later ones. Within a phase, tasks can generally be parallelised.

This is a living document. Add tasks as new gaps surface; do not recycle IDs.

## 2. How to use

- Each task has a stable ID (`ER-###`). Cite it in branch names, commits, and PRs (e.g., `feat/ER-012-bookmarks`).
- Status values: `Not started`, `In progress`, `In review`, `Done`, `Deferred`.
- Update the status column in the overview table when work starts and finishes. Use the detailed block for completion notes, PR links, or discovered follow-ups.
- **Do not expand a task's scope once it's in progress.** A bookmarks PR must not start adding highlights. If new work surfaces, open a new task.
- If a task turns out to be wrong or obsolete, mark it `Deferred` with a one-line reason. Never recycle the ID.
- Before starting any task, skim its **Dependencies** row — several P1 items chain off the Phase 1 settings infrastructure.

## 3. Legend

| Marker | Meaning |
|---|---|
| **P0** | Blocker — breaks advertised behaviour (SDD) or a hard Universal-Client rule |
| **P1** | High — table-stakes for a modern eReader |
| **P2** | Medium — quality-of-life; users will notice the absence |
| **P3** | Low — nice-to-have, pursue after P0–P2 land |
| Effort **S** | < 0.5 day |
| Effort **M** | 0.5–2 days |
| Effort **L** | 2–5 days |
| Effort **XL** | > 5 days; needs its own design note before implementation |

## 4. Overview

| ID | Title | Phase | Priority | Effort | Surface | Status |
|---|---|---|---|---|---|---|
| ER-001 | CBR archive support | 0 | P0 | S | Backend | Done |
| ER-002 | Double-page / two-up spread | 0 | P0 | M | Frontend | Done |
| ER-003 | Re-enable PDF text + annotation layers | 0 | P1 | S | Frontend | Done |
| ER-004 | Expose Table of Contents UI | 0 | P1 | S | Frontend | Done |
| ER-005 | Mark-as-finished on last page | 0 | P2 | S | Full-stack | Done |
| ER-006 | Touch / swipe page turns | 0 | P1 | S | Frontend | Done |
| ER-007 | Fullscreen / immersive toggle | 0 | P2 | S | Frontend | Done |
| ER-010 | Reader settings slice + persistence | 1 | P1 | M | Frontend | Done |
| ER-011 | CSS-variable theme refactor | 1 | P2 | S | Frontend | Done |
| ER-012 | Per-book setting overrides | 1 | P2 | M | Full-stack | Done |
| ER-020 | EPUB font, size, line-height, margin controls | 2 | P1 | M | Frontend | Done |
| ER-021 | Reading themes (dark / sepia / high-contrast) | 2 | P1 | S | Frontend | Done |
| ER-022 | Publisher-style override toggle | 2 | P2 | S | Frontend | Done |
| ER-023 | Bookmarks | 2 | P1 | M | Full-stack | Done |
| ER-024 | In-book search | 2 | P1 | L | Full-stack | Done |
| ER-030 | Zoom / fit-to-width / fit-to-page (PDF + CBZ) | 3 | P1 | M | Frontend | Done |
| ER-031 | Right-to-left reading direction | 3 | P1 | S | Frontend | Done |
| ER-032 | Page-thumbnail scrubber | 3 | P2 | L | Full-stack | Done |
| ER-040 | Highlights | 4 | P2 | L | Full-stack | Done |
| ER-041 | Notes attached to highlights | 4 | P2 | M | Full-stack | Done |
| ER-050 | Text-to-speech for EPUB | 5 | P3 | M | Frontend | Done |
| ER-051 | Offline dictionary lookup | 5 | P3 | L | Full-stack | Done |
| ER-052 | Reading stats and session tracking | 5 | P3 | M | Full-stack | Done |
| ER-053 | Brightness / colour-temperature overlay | 5 | P3 | S | Frontend | Done |
| ER-054 | Power-user keyboard shortcuts | 5 | P3 | S | Frontend | Done |

---

## Phase 0 — Advertised behaviour & quick wins

Close the gap between what the SDD and type system claim today and what the code actually delivers. These items are mostly small and independent.

### ER-001 — CBR archive support
- **Priority / Effort:** P0 / S · **Surface:** Backend
- **Problem:** [bookService.ts:4](../src/SoftMedia.Client/src/services/bookService.ts#L4) advertises `'cbr'` as a `BookFormat`, and [SDD §4.5.3](SDD.md) promises CBR handling. The server rejects it: [BookController.cs:82-85](../src/SoftMedia.Server/Controllers/BookController.cs#L82-L85) returns `BadRequest` for anything `IComicArchiveService.IsSupportedArchive` doesn't accept, and that service is CBZ-only today.
- **Acceptance:**
  1. `IComicArchiveService` handles RAR archives (SharpCompress or UnRAR bindings).
  2. Scanner picks up `.cbr` files in library roots.
  3. `BookController.GetInfo` and `GetPage` return correct responses for CBR.
  4. Test coverage in `SoftMedia.Server.Tests` mirrors existing CBZ tests.
- **Implementation notes:** SharpCompress is pure-managed and already plays well with the path-jail model. Watch for encrypted RARs — fail gracefully with a logged warning, mirroring the malformed-ZIP path.
- **Dependencies:** none.

### ER-002 — Double-page / two-up spread
- **Priority / Effort:** P0 / M · **Surface:** Frontend
- **Problem:** [SDD §4.4](SDD.md) explicitly commits to "Double Page" view. No code path renders two pages side-by-side for any format.
- **Acceptance:**
  1. User-togglable in the reader chrome: `Single | Double`.
  2. Works for PDF, CBZ/CBR, and EPUB (EPUB already paginates two-up natively in most viewports — ensure `react-reader`'s `spread` option is honoured).
  3. Keyboard/arrow navigation advances by a spread, not by a single page, when two-up is active.
  4. Page indicator reflects spread state (`12–13 / 340`, not `12 / 340`).
  5. Persists via the Phase-1 settings slice (ER-010).
- **Dependencies:** ER-010 (for persistence); ER-030 plays well with this but is not a prerequisite.

### ER-003 — Re-enable PDF text + annotation layers
- **Priority / Effort:** P1 / S · **Surface:** Frontend
- **Problem:** [BookReader.tsx:402-403](../src/SoftMedia.Client/src/components/reader/BookReader.tsx#L402-L403) disables both layers. This silently blocks text selection, copy, in-PDF links, and any future search/highlight work on PDFs.
- **Acceptance:**
  1. Text layer renders on PDF pages; user can select and copy.
  2. Annotation layer renders; internal PDF links are clickable.
  3. Layers respect the dark reading theme (pdf.js text-layer background stays transparent).
  4. Measure: no regression on page-turn latency for a 500-page reference PDF on a baseline laptop.
- **Implementation notes:** The rendered text layer will need CSS overrides for selection colour and link styling to match the theme variables introduced in ER-011.
- **Dependencies:** none; unlocks ER-024 (PDF search) and ER-040 (PDF highlights).

### ER-004 — Expose Table of Contents UI
- **Priority / Effort:** P1 / S · **Surface:** Frontend
- **Problem:** [BookReader.tsx:186-188](../src/SoftMedia.Client/src/components/reader/BookReader.tsx#L186-L188) captures the TOC into `tocRef`, but [BookReader.tsx:39-41](../src/SoftMedia.Client/src/components/reader/BookReader.tsx#L39-L41) hides the library-provided TOC chrome and nothing replaces it. The reader already identifies the current chapter but never surfaces it.
- **Acceptance:**
  1. TOC drawer/sheet opens from a header button; lists nested chapter entries; supports keyboard navigation and Tab-reachable items ≥44×44px (Universal-Client rule).
  2. Clicking an entry navigates via `rendition.display(href)`.
  3. Current chapter is highlighted in the list and shown in the reader header.
  4. Works for EPUB; for PDF, surface the embedded outline (pdf.js exposes `pdf.getOutline()`); for CBZ, surface the `ComicInfo.xml` chapter list if present via [ComicInfoXmlProvider.cs](../src/SoftMedia.Server/Services/Metadata/ComicInfoXmlProvider.cs) — otherwise hide the TOC button.
- **Dependencies:** none. PDF branch depends on ER-003 if we want reliable outline behaviour, but `getOutline` does not require the text layer.

### ER-005 — Mark-as-finished on last page
- **Priority / Effort:** P2 / S · **Surface:** Full-stack
- **Problem:** `UserMediaInteraction.IsWatched` exists at [UserMediaInteraction.cs:19](../src/SoftMedia.Server/Models/UserMediaInteraction.cs#L19) but the reader never sets it. Books never move off the "Continue Reading" shelf.
- **Acceptance:**
  1. When the user reaches the last page (PDF/CBZ) or an EPUB CFI past a 98% threshold, the client POSTs to the interaction endpoint to set `IsWatched = true`.
  2. A manual "Mark as finished / unfinished" control is also available from the reader.
  3. Home shelf ("Continue Reading") excludes finished books.
- **Implementation notes:** Debounce this so scrubbing to the end and back doesn't toggle repeatedly. Consider a server-side guard that only flips `IsWatched` once per item per session.
- **Dependencies:** none.

### ER-006 — Touch / swipe page turns
- **Priority / Effort:** P1 / S · **Surface:** Frontend
- **Problem:** [BookReader.tsx:448](../src/SoftMedia.Client/src/components/reader/BookReader.tsx#L448) passes `swipeable={false}` to `ReactReader`, and no equivalent gesture handling exists for PDF or CBZ. This violates the Universal-Client rule for tablets and touch laptops.
- **Acceptance:**
  1. Horizontal swipes turn pages in all three formats.
  2. Swipe threshold and velocity feel natural (test on a tablet).
  3. Vertical scroll inside a tall PDF page still works — swipe detection must not swallow scroll.
  4. Accessibility: swipe is additive to existing keyboard/button controls, not a replacement.
- **Implementation notes:** A single `useSwipe` hook wrapping the reader viewport is enough. Keep `preventDefault` scoped so TOC sheets and inputs aren't captured.
- **Dependencies:** none.

### ER-007 — Fullscreen / immersive toggle
- **Priority / Effort:** P2 / S · **Surface:** Frontend
- **Problem:** The header bar and the `PageControls` pill are always visible. No way to hide chrome for distraction-free reading, and no way to go browser-fullscreen.
- **Acceptance:**
  1. A header button toggles two modes: "Immersive" (hides app chrome but keeps controls reachable via hover/tap) and "Fullscreen" (`document.fullscreenElement`).
  2. `Esc` exits fullscreen first, then immersive, then the reader (matches existing `Esc` behaviour in [BookReader.tsx:336](../src/SoftMedia.Client/src/components/reader/BookReader.tsx#L336)).
  3. State persists via the settings slice (ER-010).
- **Dependencies:** ER-010 for persistence (acceptable to ship without persistence first).

---

## Phase 1 — Foundation

These unblock every later text-customisation and comic-viewing task. Do Phase 1 before Phase 2+.

### ER-010 — Reader settings slice + persistence
- **Priority / Effort:** P1 / M · **Surface:** Frontend
- **Problem:** There is no place to put reader preferences. Font, theme, zoom, and spread choices all live as local state or are hardcoded inside `BookReader.tsx`.
- **Acceptance:**
  1. A dedicated Zustand slice — e.g., `src/store/readerStore.ts` — holds: font family, font size, line height, margin, reading theme, spread mode, zoom mode, swipe enabled, immersive mode, TTS voice, RTL.
  2. Persisted to `localStorage` via `zustand/middleware/persist` with a versioned schema.
  3. `BookReader.tsx` reads from the slice; no reader-related state remains hardcoded in the component.
  4. A shared `ReaderSettingsPanel` component renders the controls; docked in the reader header.
- **Implementation notes:** Keep server sync out of scope here — ER-012 handles per-book overrides and any cross-device sync.
- **Dependencies:** none. Blocks ER-020, ER-021, ER-022, ER-030, ER-031.

### ER-011 — CSS-variable theme refactor
- **Priority / Effort:** P2 / S · **Surface:** Frontend
- **Problem:** [BookReader.tsx:28-30](../src/SoftMedia.Client/src/components/reader/BookReader.tsx#L28-L30) hardcodes `#111827`, `#e5e7eb`, `#60a5fa`. The project-wide theme is driven by CSS variables, so the reader today silently violates that rule and cannot adopt alternate reading themes (ER-021) without further JS edits.
- **Acceptance:**
  1. Reader colour tokens come from CSS variables (`--reader-bg`, `--reader-fg`, `--reader-link`, `--reader-selection`).
  2. `unifiedReaderStyles` and the epub.js theme registration consume the variables.
  3. Switching the `data-reading-theme` attribute on the reader root changes the palette without React re-render.
- **Dependencies:** none. Required by ER-021.

### ER-012 — Per-book setting overrides
- **Priority / Effort:** P2 / M · **Surface:** Full-stack
- **Problem:** A single global font size is wrong for mixed libraries — a reflow novel and a scanned technical PDF want different defaults. There is no per-book preference store today.
- **Acceptance:**
  1. Backend: a new table keyed on `(UserId, MediaItemId)` persists an opaque `ReaderPreferencesJson` blob.
  2. Endpoints: `GET/PUT /api/v1/interaction/{id}/reader-preferences`.
  3. Frontend: reader hydrates with global defaults overlaid by per-book overrides; a toggle in the settings panel lets the user save current choices as the override or revert to global.
- **Implementation notes:** Resist the urge to make each preference a typed column — it churns as ER-020+ land. Store as JSON with a `schemaVersion` field.
- **Dependencies:** ER-010.

---

## Phase 2 — Core text UX

With settings infrastructure in place, light up the text-customisation and navigation features users expect from any modern reader.

### ER-020 — EPUB font, size, line-height, margin controls
- **Priority / Effort:** P1 / M · **Surface:** Frontend
- **Problem:** [BookReader.tsx:197-210](../src/SoftMedia.Client/src/components/reader/BookReader.tsx#L197-L210) registers one hardcoded theme (Inter, 1.7 line-height, fixed padding). No user control.
- **Acceptance:**
  1. Controls for: font family (at least Inter, Georgia, Merriweather, OpenDyslexic, system-serif, system-sans), font size (±), line height (tight / normal / loose), margin (narrow / normal / wide).
  2. Changes apply live via `rendition.themes.override` — no reload.
  3. Values persist via ER-010 globally and ER-012 per-book.
- **Dependencies:** ER-010, ER-011.

### ER-021 — Reading themes (dark / sepia / high-contrast)
- **Priority / Effort:** P1 / S · **Surface:** Frontend
- **Problem:** App is dark-only by policy, but the eReader is an accepted carve-out for multiple reading palettes industry-wide. Today there is exactly one.
- **Acceptance:**
  1. Theme picker offers at minimum: Dark, Sepia, High-Contrast.
  2. Themes are CSS-variable sets (ER-011). Reader gradient branding does not bleed into the reading surface.
  3. Theme persists via ER-010 and ER-012.
- **Implementation notes:** Confirm with the project maintainer that a sepia/high-contrast palette inside the reader is an acceptable deviation from the dark-only rule. Frame it explicitly in the settings panel as "Reader theme" to avoid implying an app-wide light mode.
- **Dependencies:** ER-010, ER-011.

### ER-022 — Publisher-style override toggle
- **Priority / Effort:** P2 / S · **Surface:** Frontend
- **Problem:** EPUBs that ship their own `@font-face` declarations or heavy CSS override the SoftMedia theme. Users need a "force my theme" escape hatch.
- **Acceptance:**
  1. Toggle: `Use publisher styles | Override with my settings`.
  2. Override mode injects themed rules with `!important` and strips publisher `<style>` tags from rendered chunks.
  3. Default: on (override), so the dark theme is never silently defeated.
- **Dependencies:** ER-020, ER-021.

### ER-023 — Bookmarks
- **Priority / Effort:** P1 / M · **Surface:** Full-stack
- **Problem:** No multi-bookmark support. The only stored position is the resume point.
- **Acceptance:**
  1. New backend entity: `Bookmark { Id, UserId, MediaItemId, Position (int?), Cfi (string?), Label (string?), CreatedAt }`.
  2. Endpoints under `/api/v1/books/{id}/bookmarks`: `GET list`, `POST create`, `DELETE {bookmarkId}`, `PATCH label`.
  3. Reader UI: add-bookmark button (with keyboard shortcut `b`), bookmark sheet listing all bookmarks for the book, click to jump.
  4. Bookmarks survive book re-scans (keyed on `MediaItemId`, independent of file path).
- **Dependencies:** none technical, but ships better alongside ER-004.

### ER-024 — In-book search
- **Priority / Effort:** P1 / L · **Surface:** Full-stack
- **Problem:** No way to find text inside an open book.
- **Acceptance:**
  1. EPUB: use epub.js `book.search` across all spine items; return hits with CFI + surrounding context.
  2. PDF: use pdf.js `find` controller across the document (requires ER-003).
  3. CBZ/CBR: disabled in UI (no text layer) unless OCR is later added (separate future task).
  4. UI: search bar in the reader (shortcut `/`), result list with context snippets, click to jump and transiently highlight the match.
  5. Large-book performance: search runs off the main thread where the libraries support it; UI stays responsive.
- **Dependencies:** ER-003 (for PDF branch).

---

## Phase 3 — Comic & visual UX

Closes the usability gap for comics and scanned PDFs, where reflow settings don't apply.

### ER-030 — Zoom / fit-to-width / fit-to-page
- **Priority / Effort:** P1 / M · **Surface:** Frontend
- **Problem:** PDF width is a fixed `Math.min(800, innerWidth-40)` at [BookReader.tsx:405](../src/SoftMedia.Client/src/components/reader/BookReader.tsx#L405); CBZ uses `object-contain` at [BookReader.tsx:422](../src/SoftMedia.Client/src/components/reader/BookReader.tsx#L422). No user zoom, no pinch-zoom, no pan.
- **Acceptance:**
  1. Zoom controls: `−`, `+`, `Fit width`, `Fit page`, `100%`.
  2. Pinch-to-zoom on touch devices; Ctrl-scroll on desktop.
  3. When zoomed beyond viewport, user can pan by drag or arrow keys; page-turn shortcuts shift to `PageUp/PageDown` to disambiguate.
  4. Zoom state persists per-book (ER-012).
- **Dependencies:** ER-010, ER-012.

### ER-031 — Right-to-left reading direction
- **Priority / Effort:** P1 / S · **Surface:** Frontend
- **Problem:** Manga and some comics read right-to-left. No toggle today.
- **Acceptance:**
  1. Setting: `LTR | RTL`.
  2. CBZ/CBR: reverses page ordering and swipe/arrow direction.
  3. EPUB: honours the publication's `page-progression-direction` by default; user can override.
  4. Two-up spread (ER-002) places the leading page on the correct side in RTL.
- **Dependencies:** ER-010. Interacts with ER-002 — test together.

### ER-032 — Page-thumbnail scrubber
- **Priority / Effort:** P2 / L · **Surface:** Full-stack
- **Problem:** The page-number input (ER today) is fast to type but blind. Comic/photo-heavy PDF readers need a visual scrubber. Video already has [scrubber-preview](user-docs/features/scrubber-preview.md) — the parallel is obvious.
- **Acceptance:**
  1. Backend: thumbnail endpoint `GET /api/v1/books/{id}/thumbnail/{pageNumber}?size=sm`, cached to disk per existing image-cache conventions.
  2. Frontend: hovering/scrubbing the progress bar shows a thumbnail preview; tap-and-hold on touch.
  3. Works for CBZ, CBR, and PDF (render via pdf.js off-main-thread). EPUB is out of scope (no page images).
- **Implementation notes:** Use the existing image-caching layer; size budget must not balloon the per-book cache beyond sensible limits — pick `sm` = 160px wide.
- **Dependencies:** ER-001 for CBR coverage.

---

## Phase 4 — Annotations

Persistent marginalia. Doable only after Phase 2's text layer, search, and per-book infrastructure are in place.

### ER-040 — Highlights
- **Priority / Effort:** P2 / L · **Surface:** Full-stack
- **Problem:** Users cannot mark passages.
- **Acceptance:**
  1. New backend entity: `Highlight { Id, UserId, MediaItemId, LocationJson (CFI range for EPUB, rect+page for PDF), Colour, QuotedText, CreatedAt, UpdatedAt }`.
  2. Endpoints under `/api/v1/books/{id}/highlights`: CRUD.
  3. Selection → context menu with colour picker → POST highlight.
  4. Highlights render overlaid on their original location on every load; not coupled to the filesystem path.
  5. Viewable in a sheet with jump-to-highlight.
  6. Export: a single "Copy all highlights as Markdown" action (local-first; no cloud).
- **Dependencies:** ER-003 (PDF selection), ER-023 (shares UI conventions).

### ER-041 — Notes attached to highlights
- **Priority / Effort:** P2 / M · **Surface:** Full-stack
- **Problem:** A highlight without a note is a bookmark-plus. Notes let users capture why.
- **Acceptance:**
  1. `Highlight` entity gains a `Note (string?)` column.
  2. Editor UI: a small textarea shown when clicking a highlight.
  3. Notes are included in the Markdown export (ER-040).
- **Dependencies:** ER-040.

---

## Phase 5 — Advanced & power features

Unlocks after the core is solid. Low-risk to defer.

### ER-050 — Text-to-speech for EPUB
- **Priority / Effort:** P3 / M · **Surface:** Frontend
- **Problem:** No listening mode.
- **Acceptance:**
  1. Uses the browser's `speechSynthesis` API (local-first; no cloud TTS).
  2. Reads from the current CFI forward, auto-advancing pages.
  3. Voice and rate are selectable; persists in the settings slice.
  4. Controls: play / pause / stop / skip sentence.
  5. PDF and CBZ out of scope here (need OCR).
- **Dependencies:** ER-010.

### ER-051 — Offline dictionary lookup
- **Priority / Effort:** P3 / L · **Surface:** Full-stack
- **Problem:** No definition lookup on selection. Online dictionaries are off the table under the privacy policy.
- **Acceptance:**
  1. Backend: bundled WordNet (or similar permissively-licensed dataset) exposed via `GET /api/v1/dictionary/{word}`.
  2. Frontend: selection → "Define" context action → popover.
  3. Fallback for an unknown word: a clearly empty state — no network call ever.
- **Implementation notes:** Ship the dictionary as an opt-in download from the installer; the server returns 501 until the dataset file is present.
- **Dependencies:** ER-003 (PDF selection); EPUB works today.

### ER-052 — Reading stats and session tracking
- **Priority / Effort:** P3 / M · **Surface:** Full-stack
- **Problem:** Current `UserMediaInteraction` only stores last-position and watched status. No history of reading sessions means no "pages/minute", no "estimated time to finish", no streaks.
- **Acceptance:**
  1. New entity: `ReadingSession { Id, UserId, MediaItemId, StartedAt, EndedAt, PagesRead }`.
  2. Client instruments session start (reader mount) and end (unmount / idle-timeout).
  3. Per-book panel: total time read, pages/min, estimated time to finish current chapter and book.
  4. Data stays local — no aggregation endpoints beyond per-user queries.
- **Implementation notes:** Respect the idle timer aggressively; a user who left the reader open overnight should not show a 10-hour session.
- **Dependencies:** none.

### ER-053 — Brightness / colour-temperature overlay
- **Priority / Effort:** P3 / S · **Surface:** Frontend
- **Problem:** Users reading at night benefit from a client-side dimmer and warmth overlay. No OS-level change; purely visual.
- **Acceptance:**
  1. Two sliders: brightness (0.3–1.0), warmth (cool–neutral–warm).
  2. Implemented as a fixed overlay with `mix-blend-mode: multiply` or a CSS `filter`.
  3. Persists via ER-010.
- **Dependencies:** ER-010.

### ER-054 — Power-user keyboard shortcuts
- **Priority / Effort:** P3 / S · **Surface:** Frontend
- **Problem:** Only arrows and `Esc` are wired today. Power users expect more.
- **Acceptance:** At minimum: `b` bookmark, `/` search, `t` TOC, `g` then digits → go to page, `f` fullscreen, `+` / `-` zoom, `[` / `]` font size, `z` cycle reading theme. Document the full list in an in-app help sheet reachable via `?`.
- **Dependencies:** the features each shortcut drives.

---

## 5. Cross-cutting concerns

Not tasks in themselves — things every task above should keep in mind.

- **Universal-Client rule.** Every new control must be Tab-reachable, pair `hover:` with `focus-visible:`, and hit ≥44×44px on touch surfaces. Audit as part of each PR.
- **Path jail.** Any new endpoint that reads from disk (ER-001, ER-032, ER-051) must go through `IStreamSecurityService` and the canonicalisation path, consistent with [BookController.cs:42-44](../src/SoftMedia.Server/Controllers/BookController.cs#L42-L44).
- **Privacy.** No feature introduces analytics or telemetry. Dictionary (ER-051) and TTS (ER-050) must be demonstrably local-only.
- **Sanitization.** Any HTML injected into the EPUB iframe (publisher content, highlights, notes, search snippets) must be sanitised before rendering.
- **Migrations.** New entities (bookmarks, highlights, sessions, per-book prefs) each get their own EF Core migration. No consolidating unrelated schema changes into one migration.

## 6. Explicitly out of scope

Captured to prevent scope creep and to signal decisions, not oversights.

- Cloud sync of bookmarks/highlights/notes across servers. Local-first policy.
- OCR for scanned PDFs and CBZ/CBR. Large dependency footprint; revisit as a standalone project.
- Collaborative annotations (shared highlights between users). Single-user-per-item model stays.
- EPUB3 media overlays (synchronised audio narration). Separate media type; defer.
- Kindle-style adaptive layouts / vocabulary builder. Out of project scope.

## 7. Change log

| Date | Author | Change |
|---|---|---|
| 2026-04-19 | — | Initial draft — 25 tasks identified from source-code audit vs SDD §4.4 / §4.5. |
| 2026-04-19 | — | Quick-wins milestone shipped: ER-001, ER-003, ER-004, ER-005, ER-006, ER-007 marked Done. ER-002 still deferred pending ER-010 (settings slice) for persistence. See `ereader-plan-quickwins.md` for implementation notes. |
| 2026-04-19 | — | Customisation milestone shipped: ER-002 (double-page), ER-010 (settings slice), ER-011 (CSS variables), ER-020 (EPUB typography), ER-021 (reading themes) marked Done. Phases 0 and 1 complete except for ER-012 (per-book overrides). See `ereader-plan-customisation.md` for notes. |
| 2026-04-19 | — | Reading-comfort + bookmarks milestone shipped: ER-012 (per-book overrides), ER-022 (publisher-style override), ER-030 (zoom), ER-031 (RTL), ER-023 (bookmarks) marked Done. Phase 2 is complete except for ER-024 (search); Phase 3's P1s are done. See `ereader-plan-comfort-bookmarks.md`. |
| 2026-04-19 | — | Search + annotations milestone shipped: ER-024 (in-book search), ER-040 (highlights), ER-041 (notes) marked Done. Phase 2 is now complete. Phase 4 P2s are in. See `ereader-plan-search-annotations.md`. |
| 2026-04-19 | — | Scrubber + polish milestone shipped: ER-032 (thumbnail scrubber), ER-053 (brightness/warmth overlay), ER-054 (power keyboard shortcuts + help sheet) marked Done. Phase 3 is now complete. Only Phase 5 mid-weight items (ER-050 TTS, ER-051 dictionary, ER-052 stats) remain. See `ereader-plan-scrubber-polish.md`. |
| 2026-04-19 | — | Final milestone shipped: ER-050 (EPUB TTS), ER-052 (reading stats + session tracking), ER-051 (offline dictionary) marked Done. **All 25 roadmap tasks are now Done.** See `ereader-plan-tts-stats-dictionary.md`. |
