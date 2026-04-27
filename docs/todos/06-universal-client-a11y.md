# 06 · Universal Client accessibility pass

**Severity:** P1 · **Layer:** Frontend · **Est. size:** M (1–3 days)

## Problem

The Universal Client rule in `docs/rules/01-core-philosophy.md` mandates that every interactive element:

- be a `<button>` or `role="button"` + `tabIndex`,
- pair `hover:` with `focus-visible:`,
- be Tab-reachable,
- have ≥44×44px touch targets in responsive contexts.

The audit + peer review identified **8–10 violations** across the codebase. Keyboard users today cannot use core actions like "play" or "add to queue" on any media card. The admin UI has no focus-visible styling at all.

## Known violations (fix all of these)

Verified against the tree on 2026-04-23. The original audit listed `TVDetailView.tsx:130/:215` and `CastStripItem.tsx:182` as violations — those elements have since been made accessible (`role="button"`, `tabIndex={0}`, `onKeyDown` with Enter/Space, `focus-visible:ring-2`). They have been **removed** from this list; leave them alone.

`<div onClick>` without `role="button"` / `tabIndex` / keyboard handlers:

| File | Line(s) | Element | Status |
|---|---|---|---|
| `src/SoftMedia.Client/src/components/items/MediaCard.tsx` | 161–165 | Play overlay button | ✅ Fixed: converted to `<button>` |
| `src/SoftMedia.Client/src/components/items/MediaCard.tsx` | 170–177 | Add-to-queue button | ✅ Fixed: converted to `<button>` |
| `src/SoftMedia.Client/src/components/items/MediaCard.tsx` | 319–325 | Audio card wrapper | ✅ Fixed: `role="button"` + `tabIndex={0}` + `onKeyDown` (see note below) |
| `src/SoftMedia.Client/src/components/admin/UserListTable.tsx` | 332–341 | Sortable column header `<th onClick>` | ✅ Fixed: label wrapped in `<button>` inside `<th>`; `aria-sort` added |
| `src/SoftMedia.Client/src/components/admin/InviteManager.tsx` | ~282 | Sortable column header `<th onClick>` | ✅ Fixed: same pattern as UserListTable |
| `src/SoftMedia.Client/src/components/player/PlayerDebugPanel.tsx` | ~150 | Modal backdrop click-to-close | ✅ Fixed: `role="button"` + `tabIndex={-1}` + `onKeyDown` for Escape (surfaced by the CI guard) |
| `src/SoftMedia.Client/src/components/player/SortableQueueItem.tsx` | ~58 | Queue row clickable body | ✅ Fixed: `role="button"` + `tabIndex={0}` + `onKeyDown` (surfaced by the CI guard) |
| `src/SoftMedia.Client/src/components/player/VideoPlayer.tsx` | ~1412 | Centre play overlay | ✅ Fixed: converted to `<button>` (surfaced by the CI guard) |
| `src/SoftMedia.Client/src/components/ui/HorizontalScrollList.tsx` | ~221 | Scroll-slider track | ✅ Fixed: `role="button"` + `tabIndex` + `onKeyDown` (surfaced by the CI guard) |

**Audio-wrapper pattern change.** This doc originally proposed converting MediaCard's audio-wrapper `<div onClick>` to a `<button>`. During implementation that produced invalid HTML (the wrapper contains another inner `<button>`; nesting interactive elements is forbidden). The resolved pattern is `role="button"` + `tabIndex={0}` + `onKeyDown` that maps Enter/Space to the same handler as `onClick`. Any new row-style "click anywhere to activate" targets should follow the same pattern rather than a real `<button>` if they contain other interactive children.

**CI guard status.** `src/SoftMedia.Client/src/test/a11yGuards.test.ts` scans every `.tsx` under `src/components/` on every `npm test`. The four "surfaced by the CI guard" rows above were caught by the guard immediately after the first-pass fixes landed and were remediated in the same PR. Any future `<div onClick>` without the required attributes, or any new `<th onClick>`, will fail CI.

`hover:` without `focus-visible:` — verified 25 `hover:` occurrences across the four admin files below, and **zero** `focus-visible:` occurrences across the same files. Every `hover:` in these files is missing its keyboard pair; fix all of them.

| File | `hover:` count |
|---|---|
| `src/SoftMedia.Client/src/components/admin/UserListTable.tsx` | 11 |
| `src/SoftMedia.Client/src/components/admin/InviteManager.tsx` | 10 |
| `src/SoftMedia.Client/src/components/admin/CreateUserModal.tsx` | 2 |
| `src/SoftMedia.Client/src/components/admin/ResetPasswordModal.tsx` | 2 |

Add a matching `focus-visible:` variant to every `hover:` class (e.g. `hover:bg-gray-700 focus-visible:bg-gray-700 focus-visible:ring-2 focus-visible:ring-violet-500`). Run `grep -c 'focus-visible:' <file>` after — the count should equal or exceed the `hover:` count.

Touch-target size:
- Play/queue buttons in `MediaCard.tsx` are `p-4` on an 8×8 icon = 32×32 effective. Target 44×44 minimum on non-hover devices.

## Target state

- All the known violations above are fixed.
- A CI guard prevents new violations of the same shape from landing.
- A handful of component tests assert keyboard reachability on the hottest paths (play button, episode row, admin actions).

## Scope

**In scope:**
- Convert `<div onClick>` to `<button>` where semantically a button (MediaCard play, add-to-queue, CastStripItem toggle, sortable `<th>`).
- For row-level click targets where a button does not fit (TVDetailView episode rows), add `role="button"`, `tabIndex={0}`, and `onKeyDown` that handles Enter / Space.
- Add `focus-visible:ring-2 focus-visible:ring-white` (or the theme-appropriate variant) to every element that has `hover:`.
- Ensure tap targets on interactive buttons in responsive layouts are ≥44×44.
- Add a CI guardrail.

**Out of scope:**
- Spatial navigation for WebOS/TV (see SDD §8 deferral in `00-README.md`).
- Rewriting admin table to use a headless table library (future refactor).
- Changing visual design.

## Implementation steps

1. **Fix the three MediaCard violations first** — highest blast radius. Convert each `<div onClick>` to `<button type="button">`. Remove `cursor-pointer` (button implies it). Ensure the outer card does not swallow focus states. Add `focus-visible:ring-2 focus-visible:ring-violet-500` on each.
2. **Fix the sortable `<th>` in UserListTable** — wrap the label inside each sortable header in a `<button type="button">` that carries the `onClick`, and add `aria-sort={col.key === sort.key ? sort.direction : 'none'}` on the `<th>` for screen-reader sort state.
3. **Sweep admin `hover:` classes** — for each admin file, add a matching `focus-visible:` class as described above. Verify with `grep -c 'focus-visible:'` per file.
4. **Audit tap-target sizes** — add `min-w-[44px] min-h-[44px]` to MediaCard play/queue buttons in responsive contexts (or wrap in an accessible larger hit area).
5. **Add CI guardrail** — prefer an ESLint rule; fall back to a Grep-based script if ESLint config churn is too large.

### CI guardrail options

**Option A — ESLint (preferred):**
- Enable `jsx-a11y/no-static-element-interactions`.
- Enable `jsx-a11y/click-events-have-key-events`.
- Consider the core `no-restricted-syntax` rule (it is an ESLint core rule, not part of `@typescript-eslint`) to forbid `JSXOpeningElement[name.name='div'][attributes.some(...name='onClick')]`. Install `eslint-plugin-jsx-a11y` if it is not already a devDependency.

**Option B — Grep gate (fast fallback):**
Add a script in `package.json` that a CI job runs:

```
# fails if any <div...onClick=... pattern is introduced outside allowlisted files
grep -rn -E '<div[^>]*onClick=' src/SoftMedia.Client/src/
```

Allowlist tracks: empty initially (every hit is a violation after this todo lands). If a case genuinely needs it (e.g., full-card link), document it inline with `// eslint-disable-next-line` + justification.

## Files to touch

- `src/SoftMedia.Client/src/components/items/MediaCard.tsx` — three `<div onClick>` conversions
- `src/SoftMedia.Client/src/components/admin/UserListTable.tsx` — sortable `<th>` fix + `hover:`/`focus-visible:` pairs
- `src/SoftMedia.Client/src/components/admin/InviteManager.tsx` — `hover:`/`focus-visible:` pairs
- `src/SoftMedia.Client/src/components/admin/CreateUserModal.tsx` — `hover:`/`focus-visible:` pairs
- `src/SoftMedia.Client/src/components/admin/ResetPasswordModal.tsx` — `hover:`/`focus-visible:` pairs
- `src/SoftMedia.Client/.eslintrc.*` (or new Grep script + CI config)
- `.github/workflows/*.yml` (or existing CI config) — add the guardrail step; coordinate with todo [09](09-security-regression-tests.md) which also touches CI

**Not touching** `TVDetailView.tsx` or `CastStripItem.tsx` — both already compliant (verified 2026-04-23).

## Tests required

Vitest + React Testing Library:

- `MediaCard_PlayButton_IsReachableByKeyboard_AndActivatesOnEnterAndSpace`
- `MediaCard_AddToQueueButton_IsReachableByKeyboard`
- `MediaCard_AudioCardWrapper_IsReachableByKeyboard_AndActivatesOnEnter`
- `UserListTable_SortableHeader_IsButtonWithAriaSort`
- `InviteManager_ActionButtons_HaveFocusVisibleClass` — assert `focus-visible:ring` is in the rendered className

Optional but recommended: add one `axe-core`/`jest-axe` assertion on `<MediaCard>` and `<InviteManager>` to catch future regressions.

## Acceptance criteria

- [ ] Every violation in the table above is fixed.
- [ ] Every `hover:` in the four audited admin files has a matching `focus-visible:` (verify with `grep -c` per file).
- [ ] MediaCard play/queue buttons measure ≥44 CSS pixels wide and tall in the rendered DOM at a 375px viewport (`button.clientWidth >= 44 && button.clientHeight >= 44` — assert this in the component test).
- [ ] CI guardrail (ESLint rule or Grep script) fails the build on new `<div onClick>` without `role="button"` + `tabIndex` + keyboard handler.
- [ ] All new component tests pass.
- [ ] `grep -rnE '<div[^>]*onClick=' src/SoftMedia.Client/src/components/` and the multiline-enabled Grep (matching `<div` through `onClick` across line breaks) return only elements that also carry `role="button"`, `tabIndex`, and `onKeyDown`.

## Risk / rollback

Low. Pure frontend changes, mechanical for the most part. Biggest risk is visual regression from swapping `<div>` → `<button>` (default button styles). Mitigate by adding `appearance-none` / matching Tailwind utilities and spot-checking every converted element.
