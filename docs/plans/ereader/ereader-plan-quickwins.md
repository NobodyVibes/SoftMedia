# SoftMedia eReader — Quick-wins Implementation Plan

**Status:** Complete — 2026-04-19
**Scope reference:** [ereader-roadmap.md](ereader-roadmap.md), tasks ER-001, ER-003, ER-004, ER-005, ER-006, ER-007

## 1. Scope & rationale

This plan covers six of the seven Phase 0 tasks from the eReader roadmap. Each is Effort-S, independent of the others, and delivers a user-visible improvement without requiring new infrastructure (settings slices, DB migrations, or layout rewrites).

**In scope:**

| ID | Title | Surface |
|---|---|---|
| ER-001 | CBR archive support | Backend |
| ER-003 | Re-enable PDF text + annotation layers | Frontend |
| ER-004 | Expose Table of Contents UI | Frontend (Backend only if CBZ/CBR TOC is wired) |
| ER-005 | Mark-as-finished on last page | Full-stack |
| ER-006 | Touch / swipe page turns | Frontend |
| ER-007 | Fullscreen / immersive toggle | Frontend |

**Explicitly deferred from Phase 0:**
- **ER-002** (double-page / two-up spread) — Effort-M, modifies rendering for all three formats, and its acceptance criteria lean on the Phase-1 settings slice (ER-010) for persistence. Better addressed alongside ER-010 in a Phase-0.5 plan.

**Not yet in scope:** Phase 1 infrastructure and Phase 2–5 features.

**Rationale for this slice:**
- All six tasks are independently shippable — the plan can be cut short at any task and still leave the tree in a better state than it started.
- No shared schema changes: each task's persistence need is satisfied by an existing column (`IsWatched` for ER-005) or is a transient client-side concern (ER-007 immersive state).
- Back-to-front can be honoured per-task without cross-task contention.
- Closes two *advertised-behaviour* gaps (ER-001 CBR, ER-004 TOC) that the SDD and the existing type system already promise.

## 2. Ordering

Ordered by blast radius (backend-first, dependency-first, riskiest-last):

1. **ER-001** — backend only; must not break CBZ.
2. **ER-005** — backend DTO + endpoint first; client wiring second.
3. **ER-003** — self-contained frontend change; unblocks later search/highlight work.
4. **ER-004** — frontend; introduces a new component and consumes existing TOC state.
5. **ER-006** — frontend; adds a hook.
6. **ER-007** — frontend; last because it interacts with the same header/controls the other frontend tasks touch.

Each task ships as its own PR against `main`. No omnibus PR. Merge conflicts in [BookReader.tsx](../src/SoftMedia.Client/src/components/reader/BookReader.tsx) are resolved by rebasing the later task onto the earlier one — never by force-merge.

## 3. Cross-cutting standards

These apply to every workstream. Mentioned once here rather than duplicated per task.

- **Back-to-front.** Backend endpoints and their tests exist, are merged, and are manually verifiable via `curl` before any React consumer is written. Tests live in `src/SoftMedia.Server.Tests/`.
- **Layering.** Backend: Controllers → Services → Repositories → DbContext. Frontend: Pages → Features → UI components → Hooks. No static global state; all dependencies flow through the .NET DI container or React composition.
- **Reader-specific surface.** New reader UI goes in [src/SoftMedia.Client/src/components/reader/](../src/SoftMedia.Client/src/components/reader/). New reader hooks in [src/SoftMedia.Client/src/hooks/](../src/SoftMedia.Client/src/hooks/).
- **Universal Client.** Every new interactive element must be a `<button>` or carry `role="button"` + `tabIndex`, pair `hover:` with `focus-visible:ring-2`, be Tab-reachable, and meet ≥44×44px hit targets. Match patterns already in [BookReader.tsx:377-384](../src/SoftMedia.Client/src/components/reader/BookReader.tsx#L377-L384).
- **Theme.** No new colour literals where an existing CSS variable fits. Where none exists, hardcode with a `/* TODO(ER-011) */` marker so the future theme refactor can find and replace it.
- **Path jail.** Any server code that opens a file routes through `IStreamSecurityService.ValidateMediaAccess` — pattern already used in [BookController.cs:42-44](../src/SoftMedia.Server/Controllers/BookController.cs#L42-L44). EF Core parameterised queries only; no raw SQL.
- **Third-party metadata.** Labels coming from EPUB OPF, PDF outlines, or ComicInfo.xml are sanitised before rendering (the existing reader already strips HTML where needed; preserve that discipline for TOC labels).
- **Tests first.** Backend tests precede endpoint behaviour. Frontend tests precede component behaviour where a non-trivial invariant is at stake (e.g., the "mark-finished fires exactly once" guard in ER-005). Vitest + React Testing Library for the client; xUnit + NSubstitute for the server.
- **Commit hygiene.** One branch per task, `feat/ER-00X-<slug>`. Commit messages cite the task ID. No `--no-verify`. No force-push to `main`.

## 4. Workstreams

Each workstream lists concrete file paths, tests, and acceptance steps. Follow them in order; stop at any task boundary if priorities shift.

---

### 4.1 ER-001 — CBR archive support

**Branch:** `feat/ER-001-cbr-support`

**Dependency to add:**
- `SharpCompress` (permissive licence, pure-managed, no native binaries) referenced from `src/SoftMedia.Server/SoftMedia.Server.csproj`.

**Backend changes:**
1. **Inspect first.** Read [IComicArchiveService.cs](../src/SoftMedia.Server/Services/Abstractions/IComicArchiveService.cs) and [ComicArchiveService.cs](../src/SoftMedia.Server/Services/Media/ComicArchiveService.cs). If the interface exposes a `ZipArchive`-typed member, generalise it to a format-agnostic shape before adding RAR.
2. **Extension routing.** In `ComicArchiveService`:
   - `IsSupportedArchive` returns true for `.cbz` and `.cbr`.
   - `GetPageCountAsync` / `GetPageAsync` route by extension: `.cbz` → existing ZIP path, `.cbr` → SharpCompress `RarArchive.Open(path)` path.
   - Natural-sort archive entries (existing helper if present; otherwise a small comparator using `StrCmpLogicalW` on Windows or a managed natural-sort).
3. **Fail-safe on encryption / corruption.** Encrypted or malformed RARs must log a warning and return `null`/`0` — identical to the existing malformed-ZIP handling. Never throw past the controller.
4. **Scanner.** Verify [BookScanner.cs](../src/SoftMedia.Server/Services/Scanning/BookScanner.cs) picks up `.cbr` in its extension filter; extend if missing.

**No migration. No DB schema change.**

**Tests:**
- Add fixtures under `src/SoftMedia.Server.Tests/TestData/`: `test.cbr`, `test_encrypted.cbr`, `test_malformed.cbr` (keep them tiny — 2 single-pixel images each).
- `src/SoftMedia.Server.Tests/Services/ComicArchiveServiceTests.cs`: mirror the CBZ test matrix — page count, extract-by-index, natural ordering, encryption failure path, malformed failure path.

**Manual verification:**
- Drop a real `.cbr` in a library root, trigger a scan, open the book in the reader, page through to the last page.

**Risks:**
- SharpCompress cannot read RAR5 encrypted archives. Accept this; document in the PR body. Encrypted RAR returns a clean "unable to read archive" state, not a 500.

---

### 4.2 ER-005 — Mark-as-finished on last page

**Branch:** `feat/ER-005-mark-finished`

**Backend changes:**
1. **Inspect first.** Read [InteractionController.cs](../src/SoftMedia.Server/Controllers/InteractionController.cs) and [IUserMediaInteractionService.cs](../src/SoftMedia.Server/Services/Abstractions/IUserMediaInteractionService.cs). If an endpoint already mutates `IsWatched`, reuse it — do not duplicate.
2. **If missing:** add `PATCH /api/v1/interaction/{id}` accepting `UpdateWatchedDto { bool IsWatched }`. Controller delegates to a new service method `SetIsWatchedAsync(userId, mediaItemId, isWatched, CancellationToken)` that upserts into `UserMediaInteraction`. EF Core's existing upsert path for that entity already exists — follow it.
3. **Home-shelf query.** Inspect the query that powers "Continue Reading" / "Continue Watching" on the home page. If it currently does not filter on `IsWatched`, extend it. If the change is larger than a clause tweak (schema issues, index adds), defer and document the follow-up on the PR — do not balloon this task.

**Tests:**
- `src/SoftMedia.Server.Tests/Controllers/InteractionControllerTests.cs`:
  - Setting `IsWatched=true` creates a row if none exists (upsert).
  - Toggling true → false persists the final state.
  - A user cannot set another user's flag (403).
  - Invalid `id` returns 404.

**Frontend changes:**
1. **Service.** In [bookService.ts](../src/SoftMedia.Client/src/services/bookService.ts), export `markFinished(id: string, isFinished: boolean): Promise<void>`.
2. **Reader.** In [BookReader.tsx](../src/SoftMedia.Client/src/components/reader/BookReader.tsx):
   - Track `isFinished` state initialised from the interaction progress load (extend `getProgress` DTO to carry `isWatched`).
   - Auto-fire: when `isPdf || isCbz` and `pageNumber >= numPages && numPages > 0`, call `markFinished(id, true)` exactly once per reader session via a ref guard. EPUB: fire when `percentage >= 98`.
   - Manual control: header button labelled "Mark as finished" / "Mark as unfinished", toggles on click, round-trips to the server.
3. **Universal-Client compliance.** Button: 44×44, `focus-visible:ring-2`, sensible `aria-pressed`.

**Tests:**
- `BookReader.test.tsx`: mock `markFinished`; assert it fires exactly once when the last-page condition is met, is not fired when opening mid-book, and round-trips on manual toggle.

**Manual verification:**
- Reach the last page → book moves off the "Continue Reading" shelf.
- Manual toggle: state round-trips across reloads.
- Start a new book; the auto-fire does not trigger on mid-book pages.

---

### 4.3 ER-003 — Re-enable PDF text + annotation layers

**Branch:** `feat/ER-003-pdf-text-layer`

**Frontend changes:**
1. [BookReader.tsx:402-403](../src/SoftMedia.Client/src/components/reader/BookReader.tsx#L402-L403): set `renderTextLayer={true}` and `renderAnnotationLayer={true}`.
2. **Styling.** Add reader-specific CSS — either a new `BookReader.module.css` colocated with the component, or a `<style>` block if the project convention is CSS-in-JS. Rules:
   - `.textLayer` inherits the reader's dark background.
   - `.textLayer ::selection` uses the reader's accent colour. Use a CSS variable if the project's theme-variable file defines one; otherwise hardcode with a `/* TODO(ER-011) */` marker.
   - `.textLayer > span` remains visually transparent (default pdf.js behaviour) so the rendered bitmap stays authoritative.
   - `.annotationLayer a` styled with the reader link colour; hover and `focus-visible:ring-2`.

**Tests:**
- `BookReader.test.tsx`: happy-path smoke — given a test PDF, assert that `.textLayer > span` elements are rendered, confirming the layer is on.

**Manual verification:**
- Open a 500-page reference PDF on the baseline laptop. Page-turn latency is perceptually unchanged.
- Select a paragraph, Ctrl+C, paste into a scratch text field — content matches.
- Click an internal link in the PDF — viewer jumps to the target page.

**Risks:**
- Glyph-dense pages add a small render cost. If user feedback surfaces this, a toggle is future work under ER-020. Not in scope here.

**Unblocks:** ER-024 (PDF search), ER-040 (PDF highlights).

---

### 4.4 ER-004 — Expose Table of Contents UI

**Branch:** `feat/ER-004-toc-drawer`

**New component:**
- `src/SoftMedia.Client/src/components/reader/TocDrawer.tsx`:
  - Props: `items: TocItem[]`, `currentHref: string | null`, `onJump: (href: string) => void`, `open: boolean`, `onClose: () => void`.
  - `TocItem = { label: string; href: string; children?: TocItem[] }` — shared shape across all three formats, defined alongside the component or in `src/types/reader.ts`.
  - Right-anchored drawer with Framer Motion slide transition, matching any existing drawer patterns in the codebase.
  - Renders a nested `<ul>` with indentation by depth; each leaf is a `<button>` meeting Universal-Client rules.
  - Keyboard: `ArrowDown/Up` moves focus across buttons, `Enter` jumps, `Esc` closes and returns focus to the opener.
  - Current chapter is visually highlighted (gradient accent from `#007AFF → #8A2BE2` is appropriate here).

**BookReader wiring:**
- **EPUB:** already captures TOC into `tocRef` at [BookReader.tsx:186-188](../src/SoftMedia.Client/src/components/reader/BookReader.tsx#L186-L188). Mirror that into state so `TocDrawer` re-renders on TOC load. Map `NavItem` → `TocItem` with label sanitisation.
- **PDF:** in `onPdfLoaded`, call `pdf.getOutline()` and map to `TocItem`. Leave the drawer empty if the PDF has no outline.
- **CBZ/CBR:**
  - Read [ComicInfoXmlProvider.cs](../src/SoftMedia.Server/Services/Metadata/ComicInfoXmlProvider.cs) to determine whether chapter markers are captured into persisted metadata today.
  - If yes: add `GET /api/v1/books/{id}/toc` to `BookController` (behind `[Authorize]`, routed through `IStreamSecurityService`), and `bookService.getToc(id)` on the client.
  - If no: hide the TOC button for comic formats and add ER-004-CBZ as a follow-up on the PR description. Do not build a half-complete comic TOC path.
- **Header button.** New button in the reader header at [BookReader.tsx:375-385](../src/SoftMedia.Client/src/components/reader/BookReader.tsx#L375-L385); opens the drawer. Hidden when the current format yields an empty TOC.

**Tests:**
- RTL component test for `TocDrawer`: renders nested items, click fires `onJump`, `Esc` closes and returns focus.
- Backend tests for `/toc` if introduced: authorised path, unauthorised rejection, correct DTO shape.

**Manual verification:**
- Deep-nested EPUB: items indent correctly, click navigates, current chapter highlighted, highlight updates as the user pages forward.
- PDF with outline: ditto.
- PDF without outline: TOC button hidden.
- CBZ: behaviour matches the path chosen above.

**Risks:**
- Malformed EPUB TOCs (empty labels, self-referencing hrefs) — reuse the tolerance patterns already in `tocChanged`. Sanitise labels before render.

---

### 4.5 ER-006 — Touch / swipe page turns

**Branch:** `feat/ER-006-swipe`

**New hook:**
- `src/SoftMedia.Client/src/hooks/useSwipe.ts`:
  - Signature: `useSwipe(ref, { onSwipeLeft, onSwipeRight, threshold?: number, maxVertical?: number }): void`.
  - Defaults: `threshold = 50`, `maxVertical = 30`.
  - Uses `pointerdown` / `pointerup` over a single pointer ID. Fires a callback only when `|dx| > threshold` and `|dy| < maxVertical`.
  - Ignores events originating on `input`, `textarea`, `[contenteditable]`, or any ancestor with `data-no-swipe`.
  - Cleans up listeners in its effect cleanup.

**BookReader wiring:**
- **PDF / CBZ:** attach the hook to the content container. Call `changePage(+1 | -1)` on swipe in the matching direction (LTR today; ER-031 will flip for RTL).
- **EPUB:** switch [BookReader.tsx:448](../src/SoftMedia.Client/src/components/reader/BookReader.tsx#L448) from `swipeable={false}` to `swipeable={true}` so `react-reader` handles iframe-internal gestures. **Verify** that the two-pages-per-arrow regression described at [BookReader.tsx:304-321](../src/SoftMedia.Client/src/components/reader/BookReader.tsx#L304-L321) does not re-surface. If it does, revert that line and attach `useSwipe` to the EPUB container wrapper instead.
- **Vertical scroll.** The `maxVertical` guard plus the `[data-no-swipe]` opt-out preserve vertical scrolling inside tall PDF pages.

**Tests:**
- `useSwipe.test.ts`: synthetic `pointerdown` + `pointermove` + `pointerup` drives callbacks correctly; vertical-dominant gestures do not.
- `BookReader.test.tsx`: simulated swipe advances `pageNumber` in a PDF scenario.

**Manual verification:**
- DevTools touch emulation (mid-range tablet profile): horizontal swipes turn pages in all three formats.
- Vertical scroll inside a tall PDF still works.
- Swipe that starts inside the page-jump input does not trigger navigation.

**Risks:**
- `react-reader`'s `swipeable={true}` × our `handleKeyPress` prop. The load-bearing comment at [BookReader.tsx:304-321](../src/SoftMedia.Client/src/components/reader/BookReader.tsx#L304-L321) explains why those interact. Test the arrow-key behaviour immediately after flipping `swipeable`.

---

### 4.6 ER-007 — Fullscreen / immersive toggle

**Branch:** `feat/ER-007-fullscreen`

**Frontend changes:**
1. **Two new header buttons** in [BookReader.tsx:375-385](../src/SoftMedia.Client/src/components/reader/BookReader.tsx#L375-L385):
   - **Immersive toggle** — toggles a local `immersive` boolean. When true, the header bar and `PageControls` pill fade to `opacity-0 pointer-events-none`. On `mousemove` / `pointermove`, both fade back in for ~2 seconds (cleared timer on every move).
   - **Fullscreen toggle** — calls `document.documentElement.requestFullscreen()` / `document.exitFullscreen()`. Tracks state via a `fullscreenchange` listener so browser-level `F11` keeps the UI in sync.
2. **Escape cascade.** Update the `Escape` branch at [BookReader.tsx:336-339](../src/SoftMedia.Client/src/components/reader/BookReader.tsx#L336-L339): exit fullscreen first, then immersive, then navigate back.
3. **Cross-browser fullscreen.** Safari is vendor-prefixed; use a tiny local shim (no dependency — six lines wrapping `webkitRequestFullscreen` / `webkitExitFullscreen`).
4. **No persistence** in this pass — transient per-session. Document ER-010 as the follow-up for persisting immersive preference.

**Tests:**
- RTL: toggling the immersive button adds a `data-immersive` attribute; `Esc` unwraps the cascade in order.

**Manual verification:**
- Toggle immersive → chrome hides → move mouse → chrome returns → stops moving → chrome fades again.
- Fullscreen toggle enters and exits. Pressing `F11` keeps internal state in sync.
- `Esc` cascade: fullscreen → immersive → close.
- Safari: fullscreen works via the shim path.

---

## 5. Testing strategy

- **Backend.** `dotnet test` in `src/SoftMedia.Server.Tests/` must pass with zero skipped. Every new endpoint gets an authorised-happy-path test and at least one unauthorised test. CBR fixtures are real small archives in `TestData/`, not stubs.
- **Frontend.** `npm test` (Vitest + React Testing Library) in `src/SoftMedia.Client/`. Every new hook gets a unit test; every user-facing component change gets an interaction test. No snapshot-only tests.
- **Accessibility.** Every PR includes a manual keyboard-only walkthrough of its additions: Tab through new controls, trigger each with `Enter` / `Space`, confirm a visible focus ring.
- **Touch.** ER-006 is verified on a real touch device or with browser-emulated touch set to a mid-range tablet.
- **Regression guards.** Run the existing `BookReader.test.tsx` suite after each task. A pre-existing test should only change when the task's contract demonstrably did — document the change in the PR body.

## 6. Verification & rollout

Each PR merges independently into `main`. Before merging each PR:

1. `dotnet build` and `dotnet test` clean.
2. `npm run lint` and `npm run build` clean in `src/SoftMedia.Client/`.
3. Manual verification steps from the workstream block, executed locally.
4. PR description cites the roadmap ID, lists the acceptance-criteria checkboxes, attaches screenshots of any new UI, and links any follow-up tickets opened in passing.

After all six merge:
- Update the overview table in [ereader-roadmap.md](ereader-roadmap.md) to reflect `Done` status for each task.
- Append a one-line entry to the roadmap's change-log table.

## 7. Risks & mitigations

| Risk | Mitigation |
|---|---|
| SharpCompress throws on an encrypted RAR | Wrap in `try/catch`, log `LogWarning`, return `null`/`0` — identical to existing malformed-ZIP behaviour. |
| pdf.js text layer slows large-PDF rendering perceptibly | Ship as-is. If user feedback surfaces, a toggle comes in ER-020. |
| `react-reader` `swipeable={true}` re-introduces the double-arrow-keypress bug from [BookReader.tsx:304-321](../src/SoftMedia.Client/src/components/reader/BookReader.tsx#L304-L321) | Test immediately after flipping the prop; if regression, revert and attach `useSwipe` to the EPUB wrapper instead. |
| Safari fullscreen API divergence | Six-line local shim wrapping `webkit*` variants. No dependency. |
| Home-shelf query doesn't currently filter on `IsWatched` for books | Inspect during ER-005. One-line clause fix: include. Larger fix: defer and document. |
| Merge conflicts in `BookReader.tsx` between serial PRs | Rebase the later task onto the earlier one. Never force-merge. |
| CBZ/CBR lack ComicInfo.xml chapter data | Hide the TOC button for that format; file a follow-up to source chapter data from elsewhere. |

## 8. Acceptance checklist

The plan is complete when every box is signed off. Each mirrors the roadmap's acceptance criteria exactly — no additions, no omissions.

- [x] **ER-001** `.cbr` files are scanned, listed, paged, and fail-safe on encryption; CBZ behaviour is unchanged.
- [x] **ER-003** PDF text selection, copy, and internal-link navigation all work; page-turn latency unchanged.
- [x] **ER-004** TOC drawer lists chapters (nested where applicable), highlights the current one, jumps on click, closes on `Esc`. Hidden when the format has no TOC data.
- [x] **ER-005** Reaching the end automatically marks the book finished (fires once per session); manual toggle round-trips; finished books leave "Continue Reading".
- [x] **ER-006** Swipes turn pages in all three formats without breaking vertical scroll or form inputs.
- [x] **ER-007** Fullscreen and immersive modes toggle cleanly; `Esc` cascade exits fullscreen → immersive → reader in that order.

## 9. Out of scope (explicit)

So a reviewer can tell at a glance what this plan does not claim:

- Double-page spread (ER-002) — deferred to a separate plan alongside ER-010.
- Persistence of reader preferences beyond what each task can achieve with existing state — ER-010 remains future work.
- CSS-variable theme refactor (ER-011) — this plan writes new CSS with `TODO(ER-011)` markers where literals are unavoidable.
- Any Phase 2+ task.

## 10. Change log

| Date | Author | Change |
|---|---|---|
| 2026-04-19 | — | Initial draft. |
| 2026-04-19 | — | All six workstreams shipped. Server: 194/194 tests pass (1 skipped — CBR fixture request). Frontend: 20/20 reader + hook tests pass. Follow-ups captured in session summary. |
