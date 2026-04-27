# Hardening & Closure Task Tracker

**Source plan:** [docs/plans/hardening-and-closure-plan-2026-04-26.md](../plans/hardening-and-closure-plan-2026-04-26.md)
**Audit:** [progress-audit-2026-04-26.md](../reports/progress-audit-2026-04-26.md) · [peer review](../reports/progress-audit-peer-review-2026-04-26.md)
**Created:** 2026-04-26

A working tracker for the implementation plan. Each task points back to a numbered section of the plan (e.g. *§A1*) for full context: file lines, code change, tests, and acceptance criteria. This file is for "what's left and who's doing what" — open the plan when you actually pick up a task.

**Status legend:** `[ ]` not started · `[~]` in progress · `[x]` done · `[!]` blocked

**Severity:** **P0** ship-blocker · **P1** important before tagged release · **P2** polish

---

## Phase 0 — Pre-flight (DONE)

SDD edits applied with plan acceptance. No further action.

- [x] **F1** — SDD §4.2 refresh cookie `SameSite=Lax` clarified with rationale
- [x] **F2** — SDD §6.2 CSRF model rewritten (Bearer-in-header explicit)
- [x] **F3** — SDD §6.2 symlink resolution requirement added
- [x] **F4** — SDD §4.1 Photos library type marked *Phase 2 (post-1.0)*
- [x] **F5** — SDD §4.5 auth-on-stream-endpoints subsection added; bespoke `ReadJwtToken` checks forbidden

---

## Phase 1 — Critical security patches  **[P0]**

**Goal:** Close every high-severity finding before any other wave merges. Ship as one PR (`security/critical-patches`).
**Effort:** 1–2 days.
**Blocks:** Phase 2 cannot merge before Phase 1 lands.

| # | Task | Files | Plan |
|---|---|---|---|
| `[x]` | **A1.** Patch frame-preview auth bypass — add `[Authorize]`, delete bespoke `ReadJwtToken` block, drop `?token` param. **Note (2026-04-26 execution):** `TranscodeController` already had `[Authorize]` at the **class level**, so the original "bypass" claim was wrong — the bespoke block was dead, non-validating decode code (a copy-paste hazard), not an active vulnerability. Cleanup applied as planned. | [TranscodeController.cs:241-272](../../src/SoftMedia.Server/Controllers/TranscodeController.cs#L241-L272) | [§A1](../plans/hardening-and-closure-plan-2026-04-26.md#a1-patch-the-frame-preview-authentication-bypass) |
| `[x]` | **A2.** Resolve symlinks in `StreamSecurityService` — use `FileInfo.ResolveLinkTarget(true)` on file path *and* library roots | [StreamSecurityService.cs:24-44](../../src/SoftMedia.Server/Services/Security/StreamSecurityService.cs#L24-L44) | [§A2](../plans/hardening-and-closure-plan-2026-04-26.md#a2-resolve-symlinks-in-streamsecurityserviceispathauthorized) |
| `[x]` | **A3.** Default `Cors:AllowAnyOriginForLAN=false`; move dev override to `appsettings.Development.json`; warn at startup when true | [appsettings.json:17](../../src/SoftMedia.Server/appsettings.json#L17), [Program.cs:55-76](../../src/SoftMedia.Server/Program.cs#L55-L76) | [§A3](../plans/hardening-and-closure-plan-2026-04-26.md#a3-default-corsallowanyoriginforlan-to-false-and-move-the-dev-override) |
| `[x]` | **A4.** Lower JWT access-token TTL to 15 min | [appsettings.json:23](../../src/SoftMedia.Server/appsettings.json#L23) | [§A4](../plans/hardening-and-closure-plan-2026-04-26.md#a4-reduce-jwt-access-token-ttl-to-15-minutes) |
| `[x]` | **A5.** Strip `ex.Message` from generic 500 responses; keep server-side `LogError` | [TranscodeController.cs:128](../../src/SoftMedia.Server/Controllers/TranscodeController.cs#L128), [:237](../../src/SoftMedia.Server/Controllers/TranscodeController.cs#L237), [AudioStreamController.cs:66](../../src/SoftMedia.Server/Controllers/AudioStreamController.cs#L66) | [§A5](../plans/hardening-and-closure-plan-2026-04-26.md#a5-strip-exmessage-from-generic-500-responses) |

**Tests added in this phase:**
- `Controllers/TranscodeControllerFramePreviewAuthTests.cs` (A1)
- `Services/Security/StreamSecurityServiceTests.cs` — symlink fixtures, cross-platform (A2)
- `Integration/CorsConfigurationTests.cs` (A3)
- Error-body assertion in `ControllerAuthorizationTests.cs` or new `ControllerErrorResponsesTests.cs` (A5)

**Phase 1 sign-off:**
- [x] All five tasks complete
- [x] `dotnet test` — Phase 1 targeted tests **42/42 pass**; full suite **345/349** with 3 pre-existing failures unrelated to Phase 1 (the 3 failures are `AuthRateLimitingTests` / `AuthRateLimitIntegrationTests` from the rate-limit comment-vs-code mismatch — covered by Phase 2 task **E3**)
- [ ] Manual: log in, play a transcoded MKV, scrub timeline; frame preview thumbnails still appear *(deferred to user verification on real media)*
- [ ] Manual: dev proxy still works at `http://localhost:5173` after the CORS config split *(deferred to user verification)*

---

## Phase 2 — Branch split + repo hygiene  **[P1]**

**Goal:** Separate the in-flight reader feature from the security wave so Phases 3–5 can be reviewed cleanly. Ship the housekeeping tasks alongside the split.
**Effort:** 1 day.
**Blocks:** Phases 3, 4, 5 (cannot review their PRs cleanly until E4 is done).
**Critical path:** **E4 first** — split before any other Phase 2 work.

| # | Task | Files | Plan |
|---|---|---|---|
| `[!]` | **E4.** Split current `security/hardening-wave-1` branch — move reader feature drop (TTS, EPUB highlights, dictionary, search drawer, bookmarks, reading sessions) onto a new `feature/reader-enhancements` branch; keep only auth/refresh-token/rate-limit/test-scaffolding on the security branch. **Blocked: destructive op — needs explicit user sign-off.** | many — see plan | [§E4](../plans/hardening-and-closure-plan-2026-04-26.md#e4-split-the-current-securityhardening-wave-1-branch) |
| `[x]` | **E1.** Delete 24 stale log files; add ignore patterns | `src/SoftMedia.Server.Tests/*.log`, `*.txt` (build/test runs); root `.gitignore` | [§E1](../plans/hardening-and-closure-plan-2026-04-26.md#e1-clean-stale-log-files-in-the-test-project) |
| `[x]` | **E2.** Fold loose `Program.cs` `AddScoped` calls into extension methods (new `AddSecurityServices` for `IStreamSecurityService`) | [Program.cs:36-40](../../src/SoftMedia.Server/Program.cs#L36-L40), [ServiceCollectionExtensions.cs](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs) | [§E2](../plans/hardening-and-closure-plan-2026-04-26.md#e2-fold-programcs-di-lines-into-extension-methods) |
| `[x]` | **E3.** Reconcile rate-limit comment vs code — restored design intent: sliding-window/15 (matches inline rationale). Updated `AuthRateLimitIntegrationTests` to assert the (Limit+1)-th attempt 429s; removed `ChangePassword` from the rate-limit theory and added an explicit `ChangePassword_IsIntentionallyNotRateLimited` guard. | [ServiceCollectionExtensions.cs:188-241](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L188-L241), [AuthRateLimitIntegrationTests.cs](../../src/SoftMedia.Server.Tests/Integration/AuthRateLimitIntegrationTests.cs), [AuthRateLimitingTests.cs](../../src/SoftMedia.Server.Tests/Controllers/AuthRateLimitingTests.cs) | [§E3](../plans/hardening-and-closure-plan-2026-04-26.md#e3-fix-the-rate-limit-comment--window-type-mismatch) |

**Phase 2 sign-off:**
- [!] `git diff main..security/hardening-wave-1 --stat` shows zero reader-feature files *(blocked on E4 user sign-off)*
- [x] Repo tree clean of `*.log`/`test_*.txt`/`build_*.txt` artifacts; `.gitignore` updated
- [x] `Program.cs` contains no inline `AddScoped`/`AddSingleton`/`AddTransient` calls

---

## Phase 3 — Parental controls  **[P0]** *(family-safety promise)*

**Goal:** Implement the enforcement layer the SDD §4.2 / §6.2 calls for.
**Effort:** 3–5 days. Single dedicated PR (`feature/parental-controls`).
**Depends on:** Phase 2 (branch split).
**Parallelisable with:** Phases 4 and 5.

### 3a. Foundations
| # | Task | Files | Plan |
|---|---|---|---|
| `[x]` | **B1.** Built rating tables for Movie/TV/Game (label arrays, ascending strictness). `RatingTables.AllowedAtOrBelow(table, ceiling)` returns the `Contains`-able allow-list. Null/Unrated content treated as restricted under any ceiling. Unknown ceilings fail OPEN for that type only (avoids accidentally hiding a child's whole library because of a typo). | new [RatingTables.cs](../../src/SoftMedia.Server/Services/Security/ContentRating/RatingTables.cs), [UserRatingCeilings.cs](../../src/SoftMedia.Server/Services/Security/ContentRating/UserRatingCeilings.cs), [RatingFilterExtensions.cs](../../src/SoftMedia.Server/Services/Security/ContentRating/RatingFilterExtensions.cs) | [§B1](../plans/hardening-and-closure-plan-2026-04-26.md#b1-introduce-ordered-rating-enums-and-a-comparator) |
| `[x]` | **B2.** Built `IUserContentRatingProvider` + impl. Resolves JWT principal → DB user lookup → `UserRatingCeilings`. Per-request cache via `HttpContext.Items`. Admin short-circuit + no-context (scanner) short-circuit both return `UserRatingCeilings.Unrestricted`. `IHttpContextAccessor` registered. | new [IUserContentRatingProvider.cs](../../src/SoftMedia.Server/Services/Security/ContentRating/IUserContentRatingProvider.cs), [UserContentRatingProvider.cs](../../src/SoftMedia.Server/Services/Security/ContentRating/UserContentRatingProvider.cs); wired in `AddSecurityServices` | [§B2](../plans/hardening-and-closure-plan-2026-04-26.md#b2-rating-resolution-service-injected-into-the-request-scope) |

### 3b. Repository-layer filter
| # | Task | Files | Plan |
|---|---|---|---|
| `[x]` | **B3.** Applied `IQueryable.ApplyContentRatingFilter(ceilings)` in every user-facing read in `MediaRepository` (`GetByIdWithLibraryAsync`, `GetByIdAsync`, `GetSeriesSeasonsAsync`, `GetSeriesEpisodesWithInteractionsAsync`, `GetDistinctSeasonNumbersAsync`, `GetEpisodeCountAsync`, `GetRecentMediaAsync`, `GetEpisodesAsync`, `GetByIdsAsync`) and in `LibraryRepository.GetLibraryItemsAsync` BEFORE the type-narrowing `Where` so `COUNT(*)` and pagination operate on the filtered set. Music/Comic/Audio/Photo paths intentionally not gated this iteration. | [MediaRepository.cs](../../src/SoftMedia.Server/Services/Infrastructure/MediaRepository.cs), [LibraryRepository.cs:64-196](../../src/SoftMedia.Server/Services/Infrastructure/LibraryRepository.cs#L64-L196) | [§B3](../plans/hardening-and-closure-plan-2026-04-26.md#b3-apply-the-filter-in-repositories) |

### 3c. Per-ID stream-time enforcement
| # | Task | Files | Plan |
|---|---|---|---|
| `[x]` | **B4.** No bespoke gate needed: `MediaService.GetStreamInfoAsync` calls `_mediaRepository.GetByIdWithLibraryAsync`, which now returns null for blocked items. `StreamController` already maps null to `NotFound()`. The 404-vs-403 anti-probe behaviour is preserved. | [MediaService.cs:23-51](../../src/SoftMedia.Server/Services/Media/MediaService.cs#L23-L51) | [§B4](../plans/hardening-and-closure-plan-2026-04-26.md#b4-per-id-streamaccess-checks) |

### 3d. Frontend & tests
| # | Task | Files | Plan |
|---|---|---|---|
| `[x]` | **B5.** Already implemented as the `RatingsModal` admin tool — wired into `UserListTable`'s row actions. `RatingsModal` lets an admin set per-type ceilings (Movies/MPAA, TV/US, Games/ESRB) for any user via the existing `PUT /api/v1/users/{id}/ratings` endpoint. The plan's original placement in `MyAccountPage` was incorrect: a child account should not be able to relax its own ceiling. `MyAccountPage` deliberately shows nothing rating-related — admin-only by design. | [components/modals/RatingsModal.tsx](../../src/SoftMedia.Client/src/components/modals/RatingsModal.tsx), [components/admin/UserListTable.tsx](../../src/SoftMedia.Client/src/components/admin/UserListTable.tsx) | [§B5](../plans/hardening-and-closure-plan-2026-04-26.md#b5-admin-ui-parity-optional-within-this-wave) |
| `[x]` | **B6a.** Unit-of-EF tests — [RatingFilterTests.cs](../../src/SoftMedia.Server.Tests/Services/Security/RatingFilterTests.cs) — 7 cases against an in-memory SQLite DB exercising the IQueryable extension end-to-end through EF Core: unrestricted returns all, PG-13 movie ceiling, per-type ceilings independent, per-type override of MaxRating fallback, unknown ceiling fails-open, null `ContentRating` hidden when any ceiling set, malformed `ContentRatings` JSON treated as empty. | new test file | [§B6](../plans/hardening-and-closure-plan-2026-04-26.md#b6-tests) |
| `[x]` | **B6b.** Integration tests — [ParentalControlIntegrationTests.cs](../../src/SoftMedia.Server.Tests/Integration/ParentalControlIntegrationTests.cs) — 4 end-to-end HTTP cases through `WebApplicationFactory<Program>`: child cannot stream R, admin can stream R, child library listing hides R/NC-17/Unrated items, admin listing includes everything. | new test file | [§B6](../plans/hardening-and-closure-plan-2026-04-26.md#b6-tests) |

**Phase 3 sign-off:**
- [x] PG-13 child cannot list or stream R-rated movies (listings hide them; direct stream 404 — `ChildAccount_CannotStreamRRatedMovie`, `ChildAccount_LibraryListingHidesRRatedItems`)
- [x] Admin sees everything (`AdminAccount_LibraryListingIncludesEverything`)
- [x] Items with null `ContentRating` are hidden from any user with a ceiling, visible to admin (`NullContentRatingHiddenWhenAnyCeilingSet` + admin listing test)
- [x] Existing pagination and search behaviours still pass their tests — full server suite **367/367 pass, 0 failures, 1 unrelated skip** (CBR fixture)
- [x] EF-translatable: tests run against a real SQLite-backed `IQueryable<MediaItem>`; `Contains` translates to `WHERE col IN (...)`; no client-side evaluation warnings observed in build logs

---

## Phase 4 — Infrastructure & reliability  **[P1]**

**Goal:** Plug the long-running-install reliability gaps.
**Effort:** 2–3 days. Single PR (`feature/infra-hardening`).
**Depends on:** Phase 2 (branch split).
**Parallelisable with:** Phases 3 and 5.

| # | Task | Files | Plan |
|---|---|---|---|
| `[x]` | **C1.** New `TranscodeSegmentCleanupService` hosted worker — `PeriodicTimer(5min)`, deletes session subdirs whose canonical path is not in the active set of `ITranscodeSessionManager.GetAllSessions().SessionDirectory` and whose `LastWriteTime` > 10 min. Refuses to act on paths that escape the configured temp root. Logs info-level summary on each non-trivial tick. | new [Services/Background/TranscodeSegmentCleanupService.cs](../../src/SoftMedia.Server/Services/Background/TranscodeSegmentCleanupService.cs); registered in [ServiceCollectionExtensions.cs](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs) | [§C1](../plans/hardening-and-closure-plan-2026-04-26.md#c1-hls-segment-janitor-ihostedservice) |
| `[x]` | **C1-tests.** [TranscodeSegmentCleanupServiceTests.cs](../../src/SoftMedia.Server.Tests/Services/Background/TranscodeSegmentCleanupServiceTests.cs) — five tests: stale-deletion, fresh-retention, open-session retention, missing-root no-op, sibling-root safety net | new test file | [§C1](../plans/hardening-and-closure-plan-2026-04-26.md#c1-hls-segment-janitor-ihostedservice) |
| `[x]` | **C2.** Image proxy `User-Agent` compliance — registered named client `"ImageProxy"` with `SoftMediaUserAgentHandler`; deleted spoofed `Mozilla/5.0 ... AppleWebKit` UA from controller | [ImageController.cs:120-123](../../src/SoftMedia.Server/Controllers/ImageController.cs#L120-L123), [ServiceCollectionExtensions.cs](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs) | [§C2](../plans/hardening-and-closure-plan-2026-04-26.md#c2-image-proxy-user-agent-compliance) |
| `[x]` | **C2-test.** [SoftMediaUserAgentHandlerTests.cs](../../src/SoftMedia.Server.Tests/Services/Infrastructure/SoftMediaUserAgentHandlerTests.cs) — handler-level UA enforcement + named-client wiring assertion. Plus a file-content regression scan in `ControllerAuthorizationTests` (`Phase4_ImageController_DoesNotSpoofBrowserUserAgent`) so a future revert can't slip through | new test file | [§C2](../plans/hardening-and-closure-plan-2026-04-26.md#c2-image-proxy-user-agent-compliance) |
| n/a | **C3.** ~~Photos / EXIF scanner~~ — deferred to Phase 2 (post-1.0). No work this round. | — | [§C3](../plans/hardening-and-closure-plan-2026-04-26.md#c3-photos--exif--phase-2-placeholder) |

**Phase 4 sign-off:**
- [x] Janitor unit tests prove stale + age + safety-net behaviour without needing a real transcode. Manual real-FFmpeg smoke is deferred to user verification.
- [x] Message-handler fake (`CapturingHandler`) in `SoftMediaUserAgentHandlerTests.NamedImageProxyClient_PipelineAttachesSoftMediaHandler` confirms the named `"ImageProxy"` client emits `User-Agent: SoftMedia/...`

---

## Phase 5 — Frontend a11y closure  **[P1]**

**Goal:** Pass SDD §8.3 across the player and card surfaces; expand the a11y guard test.
**Effort:** 2–3 days. Single PR (`feature/a11y-closure`).
**Depends on:** Phase 2 (branch split).
**Parallelisable with:** Phases 3 and 4.

### 5a. Critical: player keyboard support
| # | Task | Files | Plan |
|---|---|---|---|
| `[x]` | **D1.** Added `role="slider"`, `tabIndex={0}`, full ARIA value attrs (`aria-valuemin/max/now/text` formatted via `formatDuration`), `focus-visible:ring-2 focus-visible:ring-blue-400 focus-visible:ring-offset-*`, and a full keymap (`ArrowLeft/Right` ±5s, `Shift+Arrow` ±10s, `Home`/`End`, `PageUp`/`Down` ±60s; `Space`/`Enter` deliberately not captured so they bubble to play/pause). | [ProgressBar.tsx](../../src/SoftMedia.Client/src/components/player/ProgressBar.tsx) | [§D1](../plans/hardening-and-closure-plan-2026-04-26.md#d1-progressbartsx--slider-semantics--keyboard-handler) |
| `[ ]` | **D1-test.** `ProgressBar.test.tsx` standalone unit test — **deferred to Phase 5b**: the new `a11yGuards.test.ts` checks (icon-button aria-label + hover/focus-visible pairing, both narrowed to `STRICT_A11Y_FILES`) catch attribute regressions; a behaviour-driven `userEvent.tab + arrow` test would be a useful addition but does not block Phase 5 sign-off. | new test file | [§D1](../plans/hardening-and-closure-plan-2026-04-26.md#d1-progressbartsx--slider-semantics--keyboard-handler) |

### 5b. Player control bar
| # | Task | Files | Plan |
|---|---|---|---|
| `[x]` | **D2.** Added `type="button"`, dynamic `aria-label`, `min-w-[44px] min-h-[44px]`, and `focus-visible:ring-2 focus-visible:ring-blue-400 focus-visible:outline-none` to every control-bar button: Prev Episode, Prev Chapter, Skip Back, Play/Pause, Skip Forward, Next Chapter, Next Episode, Mute, Subtitle/Audio, Playback Speed, Quality, Picture-in-Picture, Fullscreen. Toggleable controls also carry `aria-pressed` (PiP, Fullscreen) or `aria-expanded` (menus). The notification-dismiss button at line 1891 was caught and fixed too. | [VideoPlayer.tsx:1479-1801](../../src/SoftMedia.Client/src/components/player/VideoPlayer.tsx#L1479-L1801) | [§D2](../plans/hardening-and-closure-plan-2026-04-26.md#d2-videoplayertsx--control-button-labels-and-focus-rings) |

### 5c. Card overlay polish
| # | Task | Files | Plan |
|---|---|---|---|
| `[x]` | **D3.** Added `group-focus-within/card:opacity-100` (and `translate-y-0`) to the play-button overlay container and the badge stack. Keyboard users tabbing into the card now see the play button instead of focusing an invisible overlay. | [MediaCard.tsx](../../src/SoftMedia.Client/src/components/items/MediaCard.tsx) | [§D3](../plans/hardening-and-closure-plan-2026-04-26.md#d3-mediacardtsx--focus-within-overlay) |

### 5d. Guard expansion
| # | Task | Files | Plan |
|---|---|---|---|
| `[x]` | **D4.** Extended `a11yGuards.test.ts` with two new strict checks: (1) icon-only `<button>` elements must have `aria-label`; (2) any `<button>` carrying `hover:` utility classes must also carry a paired `focus-visible:` class. Both checks are scoped to `STRICT_A11Y_FILES` (currently `ProgressBar.tsx`, `VideoPlayer.tsx`, `MediaCard.tsx`) so newly-fixed files lock in their state without erupting on adjacent unfixed files. Phase 5b extends the allow-list as more files are cleaned up. | [a11yGuards.test.ts](../../src/SoftMedia.Client/src/test/a11yGuards.test.ts) | [§D4](../plans/hardening-and-closure-plan-2026-04-26.md#d4-universal-a11y-guard-expansion) |

**Phase 5 sign-off:**
- [x] `npm run test` — frontend suite **126/126 pass**, 14/14 test files green, including the new a11y guards
- [x] `dotnet test` — backend suite **367/367 pass**, 1 unrelated skip; no regression from the parental-control filter or the player edits
- [ ] Manual: Tab-only run-through of a video page (Tab to ProgressBar; ArrowRight ten times advances video 50s; focus ring visible) — *deferred to user verification*
- [ ] Manual: VoiceOver/NVDA announces a meaningful name for every player control button — *deferred to user verification*

**Phase 5b — completed.** All 31 pre-existing `hover:`-without-`focus-visible:` violations cleared:
- `player/VisualizerSelector.tsx` (2 buttons): main toggle gets `aria-label`/`aria-haspopup`/`aria-expanded` + focus ring + `min-w/h-[44px]`; menu options get `role="menuitemradio"`, `aria-checked`, and a focus ring.
- `player/PlayerDebugPanel.tsx` (2 buttons): export + close, both get `type="button"`, `aria-label`, focus ring, 44px target.
- `player/NextEpisodeOverlay.tsx` (8 buttons): X-dismiss / countdown / pause-countdown icons get `aria-label` + focus ring; the four 2×2 action buttons (Play Next: Beginning / Resume, Keep Watching, Library) get focus rings; "Return to Library" gets a focus ring.
- `player/PersistentPlayer.tsx` (~25 buttons across the full-screen and mini-player UIs): every control button (shuffle, prev/next, seek ±30s, play/pause, repeat, mute, fullscreen, queue, expand, close) now carries `type="button"`, an action-specific `aria-label`, `aria-pressed` for toggles, `aria-expanded` for menus, focus rings, and ≥44px touch targets.

All four files added to `STRICT_A11Y_FILES`. Reader subtree probed and added too — `BookReader.tsx`, `BookmarksDrawer.tsx`, `EpubView.tsx`, `HighlightsDrawer.tsx`, `PdfHighlightOverlay.tsx`, `ReaderSettingsPanel.tsx`, `SearchDrawer.tsx`, `ShortcutHelpSheet.tsx`, `TocDrawer.tsx`, `TtsNowPlayingBar.tsx` all passed the strict guards on first probe (the reader feature drop included a11y from day one).

**Final strict allow-list:** 14 files. Frontend suite **126/126 pass**.

---

## Phase 6 — Sign-off & release readiness

| # | Task |
|---|---|
| `[x]` | Run `dotnet test` + `npm test`; both fully green — backend **367/367** (1 unrelated CBR-fixture skip), frontend **126/126** |
| `[ ]` | Manual end-to-end: signup → first-run admin → add library → scan → play movie → scrub via keyboard → log out → refresh-cookie clears *(deferred to user)* |
| `[ ]` | Manual end-to-end as a child account: listings hide above-rating items; direct stream 404 *(deferred to user)* |
| `[x]` | Reverse-proxy log scrub note in `docs/user-guide/configuration.md` — covers `nginx` rewrite map; refresh-cookie posture and CORS-default lines updated to match the post-Phase-1 reality |
| `[ ]` | Update `docs/todos/00-README.md` with completion status of items 01–10 *(item 10 entry already added when this tracker was created; older items are tracked individually)* |
| `[ ]` | Tag release once all P0/P1 phases done *(operator decision — outside the assistant's scope)* |

---

## Dependency graph

```
Phase 0 (DONE — SDD edits applied)
   │
Phase 1 (critical security)        ───►  Public-readiness baseline
   │
Phase 2 (branch split + hygiene)   ───►  Unblocks reviewable PRs
   │
   ├── Phase 3 (parental controls)  ──┐
   ├── Phase 4 (infrastructure)     ──┼──►  Phase 6 (sign-off)
   └── Phase 5 (a11y)               ──┘
```

Recommended execution: land Phase 1 → land Phase 2 → fan out Phases 3/4/5 in parallel as separate PRs → converge at Phase 6.

---

## Out of scope (deliberately deferred)

Inherited from the implementation plan; **do not** open todos for these without revisiting:

- **Photos/EXIF scanner** — Phase 2 (post-1.0).
- **Typed settings tree validation** — schema-instability observation; not blocking 1.0.
- **CSRF double-submit cookie** — SDD §6.2 now documents intentional non-implementation.
- **Schema-instability deep dive** (8 settings migrations on 2026-01-14) — observation only.
- **Frontend N+1 / hooks audit** — out of scope this round.
- **Hardware-acceleration profile validation** — separate testing pass, not part of hardening.
