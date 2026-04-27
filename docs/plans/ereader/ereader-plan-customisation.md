# SoftMedia eReader — Customisation Implementation Plan

**Status:** Complete — 2026-04-19
**Scope reference:** [ereader-roadmap.md](ereader-roadmap.md), tasks ER-002, ER-010, ER-011, ER-020, ER-021
**Predecessor milestone:** [ereader-plan-quickwins.md](ereader-plan-quickwins.md) (Complete)

## 1. Scope & rationale

This plan covers the next coherent milestone after the quick-wins shipment. It closes the last Phase 0 item (double-page spread) and lays the Phase 1 foundation that most of Phase 2+ depends on, then spends that foundation on the two P1 Phase 2 text-customisation features.

**In scope:**

| ID | Title | Phase | Priority | Effort | Surface |
|---|---|---|---|---|---|
| ER-010 | Reader settings slice + persistence | 1 | P1 | M | Frontend |
| ER-011 | CSS-variable theme refactor | 1 | P2 | S | Frontend |
| ER-002 | Double-page / two-up spread | 0 | P0 | M | Frontend |
| ER-020 | EPUB font, size, line-height, margin controls | 2 | P1 | M | Frontend |
| ER-021 | Reading themes (dark / sepia / high-contrast) | 2 | P1 | S | Frontend |

**Explicitly deferred to Milestone 3:**
- **ER-012** (per-book setting overrides) — depends on ER-010 and on at least one consumer shipping first so the override UX has something meaningful to override. Leaves one clean seam: the slice reads a `perBookOverrides` record, but the read path is a no-op until ER-012 fills it.
- **ER-022** (publisher-style override toggle) — best assessed after ER-021 has been in the wild long enough to surface publisher CSS that fights the theme. Adding a toggle first is speculative.

**Not yet in scope:** any Phase 2 task beyond ER-020/ER-021, any Phase 3+ task, and any backend work. This milestone is entirely frontend.

**Rationale for this slice:**
- Two user-visible wins on top of two foundation layers — the foundation cost doesn't need justification beyond what it enables here, and the proportion is 3:2 UX:infra.
- Back-to-front is trivially satisfied: no backend changes.
- Ordered so every task ships against a stable tree — each PR either adds infrastructure that has no runtime behaviour on its own, or consumes existing infrastructure without churning it.
- Closes the SDD-advertised double-page feature that the quick-wins plan deliberately deferred.

## 2. Ordering

Strict sequence — later tasks hard-depend on earlier ones, so the plan does not parallelise well.

1. **ER-010** — frontend only; introduces the slice with no consumers yet. Safe to land independently.
2. **ER-011** — frontend only; lifts reader colour tokens to CSS variables so ER-021 can switch palettes without JS changes.
3. **ER-002** — first consumer of the slice (`spread` mode persists via ER-010).
4. **ER-020** — second consumer (font/size/line-height/margin persist via ER-010 and use the variables from ER-011).
5. **ER-021** — last consumer (theme persists via ER-010 and pivots the variables from ER-011).

Each task ships as its own PR. Merges linearise through `main`; if a conflict appears in `BookReader.tsx` or `ReaderSettingsPanel.tsx`, the later task rebases. No force-merge.

## 3. Cross-cutting standards

These apply to every workstream below and are not repeated per task.

- **Frontend layering.** Pages → Features → UI components → Hooks/Stores. Reader UI lives in [src/SoftMedia.Client/src/components/reader/](../src/SoftMedia.Client/src/components/reader/). Hooks in [src/SoftMedia.Client/src/hooks/](../src/SoftMedia.Client/src/hooks/). Global stores in [src/SoftMedia.Client/src/store/](../src/SoftMedia.Client/src/store/).
- **Universal Client.** Every new control is a `<button>` (or `role="button"` + `tabIndex`), pairs `hover:` with `focus-visible:ring-2`, is Tab-reachable, and hits ≥44×44px on touch. Match patterns already used in [BookReader.tsx](../src/SoftMedia.Client/src/components/reader/BookReader.tsx) and [TocDrawer.tsx](../src/SoftMedia.Client/src/components/reader/TocDrawer.tsx).
- **Dark app chrome stays dark.** Only the *reader viewport* gets alternate palettes (sepia / high-contrast). Frame it in copy as "Reader theme," never "Light mode," so users don't expect the whole app to switch.
- **No backend changes.** Everything persists client-side via `zustand/middleware/persist`. ER-012 will later add a server round-trip for per-book overrides; this milestone stops short.
- **No new dependencies.** Zustand, Framer Motion, Tailwind, lucide-react are already in the tree. If anything else is genuinely required, flag it on the PR before adding.
- **Schema versioning.** The settings slice stores a `schemaVersion` integer. Any structural change to stored data bumps the version and provides a migration — never hand-edit stored shapes in place.
- **Tests first.** Unit tests for the slice (read, write, persist, migrate); component tests for the panel and for each control's live effect. React Testing Library + Vitest.
- **Commit hygiene.** One branch per task (`feat/ER-0XX-<slug>`). Commit messages cite the ID. No `--no-verify`. No force-push to `main`.
- **Colour literals.** Once ER-011 lands, any subsequent task that introduces a colour literal fails review — use the `--reader-*` variables or extend the palette via ER-011's refactor.

## 4. Workstreams

---

### 4.1 ER-010 — Reader settings slice + persistence

**Branch:** `feat/ER-010-reader-settings-slice`

**New files:**
- `src/SoftMedia.Client/src/store/readerStore.ts` — Zustand store, persisted.
- `src/SoftMedia.Client/src/components/reader/ReaderSettingsPanel.tsx` — right-anchored drawer mirroring the pattern established by [TocDrawer.tsx](../src/SoftMedia.Client/src/components/reader/TocDrawer.tsx). **Shipped empty** in this task (just the shell + open/close plumbing); ER-020/ER-021 populate it. A placeholder "No settings yet" body is acceptable until consumers land.
- `src/SoftMedia.Client/src/store/readerStore.test.ts` — unit tests.
- `src/SoftMedia.Client/src/components/reader/ReaderSettingsPanel.test.tsx` — interaction tests.

**Slice shape (authoritative for this plan — individual controls are added by later tasks, not this one):**
```ts
interface ReaderPrefs {
    schemaVersion: 1;
    // Added in this task — spread is populated by ER-002, the rest by ER-020/ER-021.
    spread: 'single' | 'double';
    theme: 'dark' | 'sepia' | 'high-contrast';
    fontFamily: 'inter' | 'georgia' | 'merriweather' | 'open-dyslexic' | 'system-serif' | 'system-sans';
    fontSize: number;          // pct of default, 80–160, step 10
    lineHeight: 'tight' | 'normal' | 'loose';
    margin: 'narrow' | 'normal' | 'wide';
    // Reserved fields for later milestones — typed now so the schema stabilises early.
    immersive: boolean;        // ER-007 follow-up (persist what is currently session-only)
    zoom: 'fit-width' | 'fit-page' | number;  // ER-030
    rtl: boolean;              // ER-031
    ttsVoice: string | null;   // ER-050
}
```

**API surface:**
- `useReaderStore()` — hook returning full state.
- Individual selectors: `useSpread()`, `useTheme()`, … — each returns `[value, setValue]` to keep component code terse and avoid unrelated re-renders.
- `resetReaderPrefs()` — reverts to defaults; exposed via a "Reset" button at the bottom of the settings panel.

**Persistence:**
- `persist` middleware with storage key `softmedia.reader.prefs.v1`.
- `version: 1` + `migrate: (persisted, from) => ReaderPrefs` — returns defaults for unknown/older schemas. No in-place mutation; always emit a fresh object.

**Panel component:**
- Right-anchored drawer, slide-in via Framer Motion. Matches [TocDrawer.tsx](../src/SoftMedia.Client/src/components/reader/TocDrawer.tsx) visual treatment.
- Accessible via a gear icon in the reader header, next to the TOC button.
- Closes on Esc + backdrop click + explicit X. Returns focus to the opener.
- `<section>` per future group (`Display`, `Typography`, `Theme`, `Advanced`). Task this ships empty sections — ER-020/ER-021 each fill theirs.

**Tests:**
- Slice: default shape matches the interface; setters update in isolation; persistence round-trips via a fake storage; unknown schemaVersion yields defaults (migration path).
- Panel: opens/closes, focus-returns-to-opener, Esc cascade does not interfere with the reader's own Esc.

**Manual verification:**
- Open the reader, open the settings panel via gear icon, verify empty-but-visible state, close, reopen — state survives reload.

**Risks:**
- **Stored-data corruption.** A bug that writes garbage to storage bricks the reader. Mitigate with a try/catch in the migrator that defaults to known-good on parse failure.
- **Selector churn.** Naive `useReaderStore(s => s)` re-renders on every change. Ship typed selectors up-front.

**Dependencies:** none. Unblocks ER-002, ER-011, ER-020, ER-021, ER-030, ER-031, ER-050.

---

### 4.2 ER-011 — CSS-variable theme refactor

**Branch:** `feat/ER-011-css-variable-theme`

**Files touched:**
- [src/SoftMedia.Client/src/index.css](../src/SoftMedia.Client/src/index.css) — add a scoped variable block.
- [src/SoftMedia.Client/src/components/reader/BookReader.tsx](../src/SoftMedia.Client/src/components/reader/BookReader.tsx) — replace the three `READER_*` literals with CSS-var consumption.

**Variable definitions (go under `[data-reader-root]`, not `:root`, so they don't leak to the rest of the app):**
```css
[data-reader-root] {
    --reader-bg: #111827;
    --reader-fg: #e5e7eb;
    --reader-link: #60a5fa;
    --reader-selection: rgba(96, 165, 250, 0.45);
    --reader-font-family: 'Inter', system-ui, sans-serif;
    --reader-line-height: 1.7;
    --reader-padding-inline: 2rem;
    --reader-padding-block: 1.5rem;
}
```

**BookReader.tsx changes:**
- `READER_BG`, `READER_TEXT`, `READER_LINK` constants → read from a helper `getCssVar(name)` that calls `getComputedStyle(el).getPropertyValue(name)`.
- The `rendition.themes.register('softmedia-dark', { … })` block: replace hardcoded values with the CSS-var reads so a theme switch (ER-021) can re-register without JS recompile.
- `unifiedReaderStyles.readerArea.backgroundColor` switches to a CSS-var read.

**Replace the `/* TODO(ER-011) */` markers** left by the quick-wins milestone:
- [index.css:75-89](../src/SoftMedia.Client/src/index.css#L75-L89): the pdf.js layer rules now reference `var(--reader-selection)` and `var(--reader-link)` directly.

**Tests:**
- Snapshot-free component test: render with an alternate `data-reading-theme` attribute and assert the panel background resolves via `getComputedStyle` against the variable, not a literal.
- Purely cosmetic change — existing reader tests must stay green without edits.

**Manual verification:**
- Reader visually identical before/after on the dark palette (baseline).
- Temporarily set `[data-reading-theme="sepia"]` in devtools on the reader root and confirm the EPUB iframe body picks up the new palette without a React re-mount.

**Risks:**
- **epub.js CSS injection timing.** `rendition.themes.register` is called once in `getRendition`; if we read CSS vars at registration time, the values are frozen at that moment. The fix is to re-register / re-select on theme change (ER-021 will do this), so ER-011 must expose a `refreshReaderTheme(rendition)` helper even if it's a no-op today.

**Dependencies:** ER-010 (only because ER-010 ships the `data-reader-root` attribute — already present from ER-007; this task extends its usage).

---

### 4.3 ER-002 — Double-page / two-up spread

**Branch:** `feat/ER-002-double-page`

**Slice wiring:**
- Read `spread` from `useSpread()` ([readerStore.ts](../src/SoftMedia.Client/src/store/readerStore.ts)).
- Two new controls in `ReaderSettingsPanel.tsx` under the `Display` section: `Single` / `Double` toggle + short helper text ("Side-by-side pages — best for comics and landscape screens.").
- Default is `single`. Persist via ER-010.

**Rendering changes in [BookReader.tsx](../src/SoftMedia.Client/src/components/reader/BookReader.tsx):**
- **CBZ:** when `spread === 'double'`, render the CBZ content area as two `<img>` elements side by side — left page at `pageNumber`, right at `pageNumber + 1` (unless `pageNumber + 1 > numPages`, in which case the right slot renders an empty placeholder). Use `flex gap-2` layout.
- **PDF:** same pattern with two `<Page>` elements from `react-pdf`. Calculate widths so `(pageWidth × 2) + gap` fits `maxContentWidth`.
- **EPUB:** `react-reader` exposes a `epubOptions={{ spread: 'always' | 'auto' | 'none' }}` prop. Map `single` → `'none'`, `double` → `'always'`. Investigate whether `react-reader`'s layout needs a container-width tweak to actually render two spreads on typical viewports — it's the main unknown.

**Navigation:**
- In spread mode, `changePage(+1)` advances by `+2`; `changePage(-1)` retreats by `-2`. EPUB's `rendition.next()` / `prev()` already step by spread, so no change there.
- `PageControls` label when spread is active and current pair is `(n, n+1)`: display `"12–13 / 340"`. Keep `"12 / 340"` when single or when `n+1 > numPages` (odd-total book at the end).
- Keyboard/swipe unchanged in code — they call `changePage` which now spread-aware.

**Tests:**
- CBZ: render with `spread === 'double'`, assert two `<img>` elements appear with `pageNumber` and `pageNumber+1` alt text.
- CBZ odd-total: on the last odd page, only one `<img>` renders; the other slot is an empty placeholder (`role="presentation"` or hidden).
- PDF: with two `<Page>` stubs, assert both render.
- Label: `12–13 / 340` when `spread === 'double' && pageNumber === 12 && numPages >= 13`.
- Navigation step: `ArrowRight` advances `pageNumber` by 2 in spread mode.

**Manual verification:**
- CBZ on a 40-page comic in both modes; last spread of an odd-total book behaves gracefully.
- PDF on a landscape-oriented book; layout doesn't overflow horizontally.
- EPUB on a 600-page novel; spreads appear in `double` mode and reflow on window resize.

**Risks:**
- **react-reader spread support.** Behavioural contract with `epubOptions.spread` is documented but minor-version-dependent; verify on the pinned 2.0.15 before committing to the UX copy.
- **PDF width calc.** Each `<Page>` currently locks width at `Math.min(800, innerWidth - 40)`. Double-page needs `Math.min(780, (innerWidth - 60) / 2)` to leave a 20px gap. Watch for clipped pages on narrow laptop widths.
- **Mid-book toggle.** Flipping spread at page 35 of a single-mode read should land on spread 34–35 (round down to an even starting page) to avoid flashing.

**Dependencies:** ER-010 (for persistence). No ER-011 dependency at the behaviour level, but ships cleaner after ER-011 because two-page layouts make heavy use of the `--reader-padding-*` variables.

---

### 4.4 ER-020 — EPUB font, size, line-height, margin controls

**Branch:** `feat/ER-020-epub-typography`

**Panel additions (under a new `Typography` section in [ReaderSettingsPanel.tsx](../src/SoftMedia.Client/src/components/reader/ReaderSettingsPanel.tsx)):**
- **Font family** — segmented control, 6 options (Inter / Georgia / Merriweather / OpenDyslexic / System Serif / System Sans). OpenDyslexic is a public-domain font bundled under `src/SoftMedia.Client/public/fonts/` if not already present — if the license or asset isn't on hand, drop it from the options and file a follow-up rather than ship a broken selector.
- **Font size** — `−` / value / `+` buttons, 80–160%, 10% steps.
- **Line height** — segmented: Tight (1.4) / Normal (1.7) / Loose (2.0).
- **Margin** — segmented: Narrow (1rem inline) / Normal (2rem) / Wide (3.5rem).

**Live application in [BookReader.tsx](../src/SoftMedia.Client/src/components/reader/BookReader.tsx):**
- Subscribe to the four prefs in `getRendition`. Replace the current hardcoded `rendition.themes.register('softmedia-dark', { body: {...} })` block with a dynamic one driven by the slice.
- When any of the four change, call `rendition.themes.override('body', { … })` with the new values. Preserve the dark palette while these properties change; palette changes come from ER-021.

**PDF and CBZ are out of scope for this task** — typography settings don't apply to rasterised content. Hide the Typography section in the panel when `!isEpub` so the controls don't appear to do nothing.

**Tests:**
- Slice: each setter updates the corresponding field in isolation.
- Panel: changing font size fires `rendition.themes.override` once with the new value (via a spy on the rendition mock).
- Persistence: open book, change font, reload — preference survives.
- Accessibility: keyboard-only walk through every control; `aria-valuenow` on the size +/- buttons reflects the current size; segmented controls use radio semantics, not tab.

**Manual verification:**
- EPUB: all four controls apply live with no reload and survive app restart.
- EPUB with publisher CSS (e.g., Tor.com publications): margins and font respect our settings. If publisher CSS wins, file as ER-022 follow-up — do not hack `!important` into this task.
- PDF/CBZ: Typography section is hidden.

**Risks:**
- **OpenDyslexic font availability.** If the font isn't already bundled, adding it means a font file in `public/fonts/` and a `@font-face` in index.css — increases repo size by ~100KB. Pre-flight that addition on the PR.
- **Publisher-style bleed.** Many EPUBs ship their own `@font-face` / body rules. Expect ~20% of books to partially ignore our overrides. Document on the PR; do not fix here — that's ER-022.

**Dependencies:** ER-010, ER-011.

---

### 4.5 ER-021 — Reading themes

**Branch:** `feat/ER-021-reading-themes`

**Themes (each a CSS-variable set in [index.css](../src/SoftMedia.Client/src/index.css), targeting `[data-reader-root][data-reading-theme="<name>"]`):**

| Theme | `--reader-bg` | `--reader-fg` | `--reader-link` | `--reader-selection` |
|---|---|---|---|---|
| dark (default) | `#111827` | `#e5e7eb` | `#60a5fa` | `rgba(96,165,250,0.45)` |
| sepia | `#f4ecd8` | `#3a2f1f` | `#8b4513` | `rgba(139,69,19,0.25)` |
| high-contrast | `#000000` | `#ffffff` | `#66ff66` | `rgba(102,255,102,0.35)` |

**Panel addition (new `Theme` section in [ReaderSettingsPanel.tsx](../src/SoftMedia.Client/src/components/reader/ReaderSettingsPanel.tsx)):**
- Three radio-like buttons with a swatch + label. Selected state uses `ring-2 ring-blue-400` (matches existing Universal-Client conventions).
- Explicit copy just above: "Reader theme — the rest of SoftMedia stays dark."

**Application in [BookReader.tsx](../src/SoftMedia.Client/src/components/reader/BookReader.tsx):**
- Mirror the slice value to `data-reading-theme={theme}` on the reader root (`data-reader-root`).
- Call `refreshReaderTheme(rendition)` (from ER-011) whenever `theme` changes so the EPUB iframe's body picks up the new palette. epub.js caches the registered theme, so we re-register with the new CSS-var reads before re-selecting.
- Header chrome (the `bg-gray-800` bar) stays dark — theme only flips the reading surface. Verify visually.

**Tests:**
- Component test: changing theme sets `data-reading-theme` on the reader root.
- Component test: `refreshReaderTheme` is called exactly once per theme change (spy on rendition mock).
- RTL: panel radio buttons focus correctly and Tab order is sensible.

**Manual verification:**
- Toggle through all three themes in EPUB. Dark is pixel-identical to pre-milestone (regression guard). Sepia and high-contrast apply to both app chrome around the reading surface AND the EPUB iframe body.
- Switching theme mid-book does not scroll or reflow the reading position.
- PDF: theme applies to the page background and the text-layer selection colour (via ER-003's selectors).
- CBZ: theme applies to the gutter / frame; the actual comic images are unaffected (they're rasterised).

**Risks:**
- **Reader-only light mode is still a carve-out from the project's dark-mode rule.** Surface the decision explicitly to the maintainer on the PR — not hidden in settings copy.
- **EPUB iframe theme flash.** Changing a registered epub.js theme can cause a brief restyle. Test for visible flashing on a slow spread; if it's noticeable, lift the theme change to happen in a `requestAnimationFrame`.
- **High-contrast accessibility.** Real high-contrast needs more than colour flips — font weight, letter spacing, removed underlines, etc. Version 1 ships the colour flip only; treat the rest as a separate accessibility task.

**Dependencies:** ER-010, ER-011.

---

## 5. Testing strategy

- **Unit.** The settings slice gets direct tests (readerStore.test.ts): default shape, each setter in isolation, persistence round-trip through a fake storage, migration from an unknown version → defaults.
- **Component.** The panel gets interaction tests (ReaderSettingsPanel.test.tsx): opens/closes, focus returns, each control wires to the slice, controls hide appropriately when not applicable (e.g., Typography hidden for PDF/CBZ).
- **Integration.** [BookReader.test.tsx](../src/SoftMedia.Client/src/components/reader/BookReader.test.tsx) gains per-task behaviour tests without churning the existing seven:
  - Spread mode renders two pages (ER-002).
  - Font size change invokes `rendition.themes.override` (ER-020).
  - Theme change mutates `data-reading-theme` on the root (ER-021).
- **Regression.** After every task, the full `npx vitest run src/components/reader/ src/hooks/ src/store/` must pass. Server tests (`dotnet test --configuration Release`) remain green with zero changes — this is a frontend-only milestone.
- **Accessibility.** Every PR includes a keyboard-only walkthrough: Tab through the panel, activate every control with Enter/Space, confirm a visible focus ring and a matching `aria-pressed` / `aria-valuenow` / radio state.
- **Visual regression.** Baseline screenshot of the reader in dark mode before each task; compare after. Pixel-level diff is overkill here, but a side-by-side check catches palette leaks.

## 6. Verification & rollout

Each PR merges independently into `main`. Before merging each PR:

1. `dotnet build` and `dotnet test` clean (should be no-op this milestone but catch accidental cross-layer regressions).
2. `npx tsc -b` clean — this milestone is a good moment to clear the two pre-existing TS errors flagged in the quick-wins session summary, if the maintainer opts in.
3. `npm run lint` and `npm run build` clean.
4. Manual verification steps from the workstream block, executed locally on at least EPUB + PDF + CBZ.
5. PR description cites the roadmap ID, lists the acceptance-criteria checkboxes, attaches screenshots of every new UI state (settings panel empty / with controls / in each theme), and links any follow-up tickets opened in passing.

After all five merge:
- Update the overview table in [ereader-roadmap.md](ereader-roadmap.md) to reflect `Done` for each task.
- Append a one-line entry to the roadmap's change-log.
- File any publisher-CSS-bleed observations from ER-020 as a concrete ER-022 scoping doc.

## 7. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Stored-data corruption in the settings slice bricks the reader | `migrate` returns defaults on any parse failure; add a try/catch around the JSON rehydrate; ship a "Reset reader settings" button in the panel from day 1. |
| `react-reader` spread prop behaves differently than its docs on 2.0.15 | Prototype the EPUB double-page path before writing the panel UI. If the library can't deliver, degrade to single-mode for EPUB and document as a follow-up (CBZ/PDF remain). |
| Publisher EPUB CSS defeats ER-020 font/size changes | Ship without `!important` first — that's ER-022's job. Document on the PR the subset of books observed to be affected. |
| CSS-variable refactor (ER-011) produces a subtle palette drift in dark mode | Baseline screenshot before ER-011; diff after. If any pixel change is detected, fix the var definition to match the prior literal exactly. |
| "Reader theme" confuses users into expecting an app-wide light mode | Dedicated copy in the Theme section; do not add a system-light-mode toggle elsewhere in the app. Confirm on the PR that product is aligned. |
| High-contrast theme ships as colour-only and fails real accessibility audit | Explicitly scoped to a colour flip in this plan; a separate task covers full high-contrast (font weight, spacing, focus treatment). |
| Double-page mid-book toggle causes visible page flash | Round `pageNumber` down to an even starting page before rendering the new spread; test on a 500-page PDF. |
| OpenDyslexic font adds repo weight without being used by most users | Lazy-load the font file behind a `@font-face` declaration with `font-display: swap`; only the CSS rule ships if the user has not yet selected that font. |
| Merge conflicts in `BookReader.tsx` between serial PRs | Rebase the later task; if the conflict touches the rendition wiring, pair-review before pushing. |

## 8. Acceptance checklist

The plan is complete when every box is signed off. Each mirrors the roadmap criteria exactly.

- [x] **ER-010** Zustand slice holds the 10-field `ReaderPrefs` shape, persists via `zustand/middleware/persist`, survives reload, migrates from unknown version → defaults. Panel shell opens/closes with focus restore.
- [x] **ER-011** All reader colour tokens read from `--reader-*` variables scoped to `[data-reader-root]`. Dark-mode visual is byte-identical to pre-refactor. `refreshReaderTheme(rendition)` helper exposed for ER-021 use.
- [x] **ER-002** Single / Double toggle in the panel. Double mode renders side-by-side pages for CBZ and PDF, and spreads for EPUB. Nav advances by a spread. Page label reads `12–13 / 340`. Persists via ER-010. Odd-total last-page behaves gracefully.
- [x] **ER-020** Font family, size, line-height, and margin controls live-apply to EPUB with no reload. Panel hides Typography section for PDF/CBZ. Persists via ER-010. Keyboard-navigable.
- [x] **ER-021** Dark / Sepia / High-contrast selectable. Reader viewport switches; app chrome stays dark. EPUB iframe picks up the palette via `refreshReaderTheme`. Persists via ER-010.

## 9. Out of scope (explicit)

- **ER-012** (per-book setting overrides) — Milestone 3. This plan writes prefs only to the global slice; an `overrides` field is reserved in the schema but not consumed.
- **ER-022** (publisher-style override toggle) — Milestone 3. Ship ER-020 without `!important` and see how many books misbehave before deciding the UX for the override.
- **PDF typography.** Text layer is on (ER-003), but font-family / line-height changes on a rasterised page make no sense. Out of scope here and forever.
- **OCR for CBZ.** Would unlock search/highlights for comics. Separate project.
- **Any Phase 3+ task** (zoom, RTL, page thumbnails, annotations, TTS, dictionary, stats, brightness, power shortcuts).
- **Backend work.** This plan is 100% frontend. Adding a server-side preferences endpoint is ER-012.

## 10. Change log

| Date | Author | Change |
|---|---|---|
| 2026-04-19 | — | Initial draft. |
| 2026-04-19 | — | All five workstreams shipped. Frontend: 43/43 tests pass across 6 files. Server: 194/194 (unchanged — milestone was frontend-only). OpenDyslexic + Merriweather font assets deferred to a follow-up. |
