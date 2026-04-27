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
| `[ ]` | **A1.** Patch frame-preview auth bypass — add `[Authorize]`, delete bespoke `ReadJwtToken` block, drop `?token` param | [TranscodeController.cs:241-272](../../src/SoftMedia.Server/Controllers/TranscodeController.cs#L241-L272) | [§A1](../plans/hardening-and-closure-plan-2026-04-26.md#a1-patch-the-frame-preview-authentication-bypass) |
| `[ ]` | **A2.** Resolve symlinks in `StreamSecurityService` — use `FileInfo.ResolveLinkTarget(true)` on file path *and* library roots | [StreamSecurityService.cs:24-44](../../src/SoftMedia.Server/Services/Security/StreamSecurityService.cs#L24-L44) | [§A2](../plans/hardening-and-closure-plan-2026-04-26.md#a2-resolve-symlinks-in-streamsecurityserviceispathauthorized) |
| `[ ]` | **A3.** Default `Cors:AllowAnyOriginForLAN=false`; move dev override to `appsettings.Development.json`; warn at startup when true | [appsettings.json:17](../../src/SoftMedia.Server/appsettings.json#L17), [Program.cs:55-76](../../src/SoftMedia.Server/Program.cs#L55-L76) | [§A3](../plans/hardening-and-closure-plan-2026-04-26.md#a3-default-corsallowanyoriginforlan-to-false-and-move-the-dev-override) |
| `[ ]` | **A4.** Lower JWT access-token TTL to 15 min | [appsettings.json:23](../../src/SoftMedia.Server/appsettings.json#L23) | [§A4](../plans/hardening-and-closure-plan-2026-04-26.md#a4-reduce-jwt-access-token-ttl-to-15-minutes) |
| `[ ]` | **A5.** Strip `ex.Message` from generic 500 responses; keep server-side `LogError` | [TranscodeController.cs:128](../../src/SoftMedia.Server/Controllers/TranscodeController.cs#L128), [:237](../../src/SoftMedia.Server/Controllers/TranscodeController.cs#L237), [AudioStreamController.cs:66](../../src/SoftMedia.Server/Controllers/AudioStreamController.cs#L66) | [§A5](../plans/hardening-and-closure-plan-2026-04-26.md#a5-strip-exmessage-from-generic-500-responses) |

**Tests added in this phase:**
- `Controllers/TranscodeControllerFramePreviewAuthTests.cs` (A1)
- `Services/Security/StreamSecurityServiceTests.cs` — symlink fixtures, cross-platform (A2)
- `Integration/CorsConfigurationTests.cs` (A3)
- Error-body assertion in `ControllerAuthorizationTests.cs` or new `ControllerErrorResponsesTests.cs` (A5)

**Phase 1 sign-off:**
- [ ] All five tasks complete
- [ ] `dotnet test src/SoftMedia.Server.Tests/` green
- [ ] Manual: log in, play a transcoded MKV, scrub timeline; frame preview thumbnails still appear
- [ ] Manual: dev proxy still works at `http://localhost:5173` after the CORS config split

---

## Phase 2 — Branch split + repo hygiene  **[P1]**

**Goal:** Separate the in-flight reader feature from the security wave so Phases 3–5 can be reviewed cleanly. Ship the housekeeping tasks alongside the split.
**Effort:** 1 day.
**Blocks:** Phases 3, 4, 5 (cannot review their PRs cleanly until E4 is done).
**Critical path:** **E4 first** — split before any other Phase 2 work.

| # | Task | Files | Plan |
|---|---|---|---|
| `[ ]` | **E4.** Split current `security/hardening-wave-1` branch — move reader feature drop (TTS, EPUB highlights, dictionary, search drawer, bookmarks, reading sessions) onto a new `feature/reader-enhancements` branch; keep only auth/refresh-token/rate-limit/test-scaffolding on the security branch | many — see plan | [§E4](../plans/hardening-and-closure-plan-2026-04-26.md#e4-split-the-current-securityhardening-wave-1-branch) |
| `[ ]` | **E1.** Delete 17 stale log files; add ignore patterns | `src/SoftMedia.Server.Tests/*.log`, `*.txt` (build/test runs); root `.gitignore` | [§E1](../plans/hardening-and-closure-plan-2026-04-26.md#e1-clean-stale-log-files-in-the-test-project) |
| `[ ]` | **E2.** Fold loose `Program.cs` `AddScoped` calls into extension methods (new `AddSecurityServices` for `IStreamSecurityService`) | [Program.cs:40-47](../../src/SoftMedia.Server/Program.cs#L40-L47), [ServiceCollectionExtensions.cs](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs) | [§E2](../plans/hardening-and-closure-plan-2026-04-26.md#e2-fold-programcs-di-lines-into-extension-methods) |
| `[ ]` | **E3.** Reconcile rate-limit comment vs code — pick fixed-window/30 (preferred) and update comment, OR change code to sliding-window/15 | [ServiceCollectionExtensions.cs:188-241](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L188-L241) | [§E3](../plans/hardening-and-closure-plan-2026-04-26.md#e3-fix-the-rate-limit-comment--window-type-mismatch) |

**Phase 2 sign-off:**
- [ ] `git diff main..security/hardening-wave-1 --stat` shows zero reader-feature files
- [ ] Repo tree clean of `*.log`/`test_*.txt`/`build_*.txt` artifacts; `.gitignore` updated
- [ ] `Program.cs` contains no inline `AddScoped`/`AddSingleton`/`AddTransient` calls

---

## Phase 3 — Parental controls  **[P0]** *(family-safety promise)*

**Goal:** Implement the enforcement layer the SDD §4.2 / §6.2 calls for.
**Effort:** 3–5 days. Single dedicated PR (`feature/parental-controls`).
**Depends on:** Phase 2 (branch split).
**Parallelisable with:** Phases 4 and 5.

### 3a. Foundations
| # | Task | Files | Plan |
|---|---|---|---|
| `[ ]` | **B1.** Define `MovieRating`, `TvRating`, `GameRating` ordered enums + `RatingComparator.IsAllowed(type, itemRating, userCeiling)`. Null/Unrated content treated as restricted under any ceiling. | new `Services/Security/ContentRating/RatingComparator.cs` | [§B1](../plans/hardening-and-closure-plan-2026-04-26.md#b1-introduce-ordered-rating-enums-and-a-comparator) |
| `[ ]` | **B2.** Build request-scoped `IUserContentRatingProvider` resolving JWT-claim user ceilings; admin short-circuit | new `Services/Security/ContentRating/IUserContentRatingProvider.cs` + impl | [§B2](../plans/hardening-and-closure-plan-2026-04-26.md#b2-rating-resolution-service-injected-into-the-request-scope) |

### 3b. Repository-layer filter
| # | Task | Files | Plan |
|---|---|---|---|
| `[ ]` | **B3.** Apply rating filter inside `MediaRepository` and `LibraryRepository` *before* pagination; ensure `COUNT(*)` paths apply it too. Use EF-translatable `Where` lambdas (no client-eval) | [MediaRepository.cs](../../src/SoftMedia.Server/Services/Infrastructure/MediaRepository.cs), [LibraryRepository.cs:64-196](../../src/SoftMedia.Server/Services/Infrastructure/LibraryRepository.cs#L64-L196) | [§B3](../plans/hardening-and-closure-plan-2026-04-26.md#b3-apply-the-filter-in-repositories) |

### 3c. Per-ID stream-time enforcement
| # | Task | Files | Plan |
|---|---|---|---|
| `[ ]` | **B4.** Block direct stream-by-ID for ratings above ceiling; return 404 to mirror jail-mismatch behaviour | [MediaService.cs](../../src/SoftMedia.Server/Services/Media/MediaService.cs), `BookController` / `AudioController` / `MusicController` service paths | [§B4](../plans/hardening-and-closure-plan-2026-04-26.md#b4-per-id-streamaccess-checks) |

### 3d. Frontend & tests
| # | Task | Files | Plan |
|---|---|---|---|
| `[ ]` | **B5.** *(Optional in-wave)* Surface per-type rating ceilings in `MyAccountPage` user form | [pages/MyAccountPage.tsx](../../src/SoftMedia.Client/src/pages/MyAccountPage.tsx) | [§B5](../plans/hardening-and-closure-plan-2026-04-26.md#b5-admin-ui-parity-optional-within-this-wave) |
| `[ ]` | **B6a.** Unit tests — `RatingComparatorTests.cs` exhaustive matrix per type | new test file | [§B6](../plans/hardening-and-closure-plan-2026-04-26.md#b6-tests) |
| `[ ]` | **B6b.** Integration tests — `Integration/ParentalControlIntegrationTests.cs` covering child listings, direct stream 404, admin bypass, null-rating fail-safe | new test file | [§B6](../plans/hardening-and-closure-plan-2026-04-26.md#b6-tests) |

**Phase 3 sign-off:**
- [ ] PG-13 child cannot list or stream R-rated movies (listings empty; direct ID returns 404)
- [ ] Admin sees everything (regression check)
- [ ] Items with null `ContentRating` are hidden from any user with a ceiling, visible to admin
- [ ] Existing pagination and search behaviours still pass their tests
- [ ] `.ToQueryString()` on the filtered IQueryable shows the rating predicate translated to SQL (no client-side evaluation)

---

## Phase 4 — Infrastructure & reliability  **[P1]**

**Goal:** Plug the long-running-install reliability gaps.
**Effort:** 2–3 days. Single PR (`feature/infra-hardening`).
**Depends on:** Phase 2 (branch split).
**Parallelisable with:** Phases 3 and 5.

| # | Task | Files | Plan |
|---|---|---|---|
| `[ ]` | **C1.** New `TranscodeSegmentCleanupService` hosted worker — `PeriodicTimer(5min)`, deletes session subdirs whose key is not in `ITranscodeSessionManager.OpenKeys` and whose `LastWriteTime` > threshold (default 10 min, configurable via `Settings:TranscodeCleanupAgeMinutes`). Log info-level summary. | new `Services/Background/TranscodeSegmentCleanupService.cs`; register in [ServiceCollectionExtensions.cs:263-294](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L263-L294) | [§C1](../plans/hardening-and-closure-plan-2026-04-26.md#c1-hls-segment-janitor-ihostedservice) |
| `[ ]` | **C1-tests.** `Services/Background/TranscodeSegmentCleanupServiceTests.cs` — closed/open/age matrix; safety check that paths outside the temp root are never deleted | new test file | [§C1](../plans/hardening-and-closure-plan-2026-04-26.md#c1-hls-segment-janitor-ihostedservice) |
| `[ ]` | **C2.** Image proxy `User-Agent` compliance — register named client `"ImageProxy"` with `SoftMediaUserAgentHandler`; remove the spoofed `Mozilla/...` UA | [ImageController.cs:120-123](../../src/SoftMedia.Server/Controllers/ImageController.cs#L120-L123), [ServiceCollectionExtensions.cs](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs) | [§C2](../plans/hardening-and-closure-plan-2026-04-26.md#c2-image-proxy-user-agent-compliance) |
| `[ ]` | **C2-test.** Outbound-request capture asserting `User-Agent: SoftMedia/...` | new or extend existing test | [§C2](../plans/hardening-and-closure-plan-2026-04-26.md#c2-image-proxy-user-agent-compliance) |
| n/a | **C3.** ~~Photos / EXIF scanner~~ — deferred to Phase 2 (post-1.0). No work this round. | — | [§C3](../plans/hardening-and-closure-plan-2026-04-26.md#c3-photos--exif--phase-2-placeholder) |

**Phase 4 sign-off:**
- [ ] Force a transcode, close the tab without DELETE, advance system time 11 min in tests; session directory removed
- [ ] Wireshark or message-handler fake confirms outbound proxy fetches send the SoftMedia UA

---

## Phase 5 — Frontend a11y closure  **[P1]**

**Goal:** Pass SDD §8.3 across the player and card surfaces; expand the a11y guard test.
**Effort:** 2–3 days. Single PR (`feature/a11y-closure`).
**Depends on:** Phase 2 (branch split).
**Parallelisable with:** Phases 3 and 4.

### 5a. Critical: player keyboard support
| # | Task | Files | Plan |
|---|---|---|---|
| `[ ]` | **D1.** `ProgressBar.tsx` — add `role="slider"`, `tabIndex={0}`, ARIA value attrs, focus-visible ring, full keymap (`ArrowLeft/Right`=±5s, `Shift+Arrow`=±10s, `Home`/`End`, `PageUp/Down`=±60s; **do not** capture `Space`). Wrap track in 44px hit-area without enlarging the visible 6/10px track. | [components/player/ProgressBar.tsx:104-208](../../src/SoftMedia.Client/src/components/player/ProgressBar.tsx#L104-L208) | [§D1](../plans/hardening-and-closure-plan-2026-04-26.md#d1-progressbartsx--slider-semantics--keyboard-handler) |
| `[ ]` | **D1-test.** `ProgressBar.test.tsx` — `role="slider"` present; ArrowRight advances `aria-valuenow`; `Home` calls `onSeek(0)` | new test file | [§D1](../plans/hardening-and-closure-plan-2026-04-26.md#d1-progressbartsx--slider-semantics--keyboard-handler) |

### 5b. Player control bar
| # | Task | Files | Plan |
|---|---|---|---|
| `[ ]` | **D2.** `VideoPlayer.tsx` — add `aria-label` to every control button (Prev Episode, Prev Chapter, Skip Back, Play/Pause, Skip Forward, Mute, Subtitle/Audio, Settings, Fullscreen); add paired `focus-visible:ring-2 focus-visible:ring-blue-400`; ensure `min-w-[44px] min-h-[44px]` on each | [components/player/VideoPlayer.tsx:1479-1610](../../src/SoftMedia.Client/src/components/player/VideoPlayer.tsx#L1479-L1610) | [§D2](../plans/hardening-and-closure-plan-2026-04-26.md#d2-videoplayertsx--control-button-labels-and-focus-rings) |

### 5c. Card overlay polish
| # | Task | Files | Plan |
|---|---|---|---|
| `[ ]` | **D3.** `MediaCard.tsx` — add `group-focus-within/card:opacity-100` next to existing `group-hover/card:opacity-100` so keyboard users see the play-button overlay | [components/items/MediaCard.tsx](../../src/SoftMedia.Client/src/components/items/MediaCard.tsx) | [§D3](../plans/hardening-and-closure-plan-2026-04-26.md#d3-mediacardtsx--focus-within-overlay) |

### 5d. Guard expansion
| # | Task | Files | Plan |
|---|---|---|---|
| `[ ]` | **D4.** Extend `a11yGuards.test.ts` — every `<button>` has text or non-empty `aria-label`; every `role="button"` has `tabIndex` + `onKeyDown`; every hover class has paired `focus-visible:` class. Apply to player, card, cast strip. | [test/a11yGuards.test.ts](../../src/SoftMedia.Client/src/test/a11yGuards.test.ts) | [§D4](../plans/hardening-and-closure-plan-2026-04-26.md#d4-universal-a11y-guard-expansion) |

**Phase 5 sign-off:**
- [ ] Tab-only run-through: Tab to ProgressBar; ArrowRight ten times advances video 50 s; focus ring visible
- [ ] VoiceOver/NVDA announces a meaningful name for every player control button
- [ ] `npm run test` passes including the expanded a11y guard

---

## Phase 6 — Sign-off & release readiness

| # | Task |
|---|---|
| `[ ]` | Run `dotnet test` + `npm test`; both fully green |
| `[ ]` | Manual end-to-end: signup → first-run admin → add library → scan → play movie → scrub → next episode → log out → refresh-cookie clears |
| `[ ]` | Manual end-to-end as a child account (after Phase 3): listings hide above-rating items; direct stream 404 |
| `[ ]` | Reverse-proxy log scrub note added to `docs/user-guide/` (or equivalent) — calls out `?access_token=` exposure |
| `[ ]` | Update `docs/todos/00-README.md` with completion status of items 01–10 |
| `[ ]` | Tag release once all P0/P1 phases done |

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
