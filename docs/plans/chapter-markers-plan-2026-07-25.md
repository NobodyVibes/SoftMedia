# Chapter-Marker Priority Plan — 2026-07-25

**Input:** maintainer report — VLC shows embedded chapter markers ("Intro", "Scene 1",
"Credits") for `Futurama 09x10 Otherwise …mkv` but SoftMedia's skip system ignores them.
Verified state before this plan:

- Chapters ARE probed (`MediaProbeService`) and stored (`Chapters` table, 803 rows) and the
  player uses them for hover labels + chapter seeking. The DTO already ships
  IntroStart/End/Source + CreditsStart/End/Source.
- A credits-titled chapter seeds `CreditsStart` only — no `CreditsEnd`, and crucially **no
  `CreditsSource = Chapter`**, so detection is permitted to overwrite the authoritative value.
- Intro-titled chapters are ignored entirely.
- `DetectionSource.Chapter` is written by NOTHING — the precedence guards in
  `IntroCreditsDetectionService.TryWrite*` (and their test) protect values that can never
  exist. Designed, never wired.

**Goal:** embedded chapters become the authoritative source for intro/credits timecodes;
fingerprint detection fills the gaps where chapters don't exist. No client changes needed.

## Design decisions (quality-first)

- **D1 — one mapper, two call sites.** A pure, static `ChapterMarkerMapper` maps an ordered
  chapter list + duration → optional intro/credits spans. Both the scan path
  (`VideoAnalysisStrategy`) and the boot-time backfill call it, so scan and backfill can
  never disagree. Input shape is `(StartTime, Title)` + duration — deliberately matching the
  stored `Chapters` schema so no migration is needed.
- **D2 — span ends derive from the next chapter start** (last chapter → duration), NOT from
  ffprobe's per-chapter `end_time`. For skip purposes "next chapter start" is the *better*
  target (skip lands exactly where content resumes), and it makes DB-based backfill exactly
  equivalent to probe-based mapping.
- **D3 — conservative title matching + positional sanity guards.** Intro: exact-match against
  a normalized keyword set (many rips use meaningless "Chapter 1" names; substring matching
  is how false positives happen). Credits: exact set plus `contains("credit")` for variants
  ("End Credits & Outtakes"), with a negative guard for post/mid-credits scene chapters —
  those are *content*, and choosing the FIRST credits match means `CreditsEnd` lands exactly
  at the post-credits scene start. Positional guards: intro must start in the first third
  (and ≤ 10 min); credits must start in the second half; spans must be ≥ 5 s.
- **D4 — chapter-sourced columns mirror the file; detected columns belong to detection.**
  On every successful probe, chapter-sourced values are recomputed from the file's current
  chapters: written on match, **cleared when a previously chapter-sourced value no longer has
  a matching chapter** (file replaced with a chapterless/different cut). Detected values are
  never touched by the scan path; the existing `TryWrite*` guards keep detection from
  touching chapter-sourced values. Chapter always beats Detected (the file's own authoring is
  ground truth; detection is a statistical fallback).
- **D5 — detection keeps fingerprinting chaptered episodes.** Skipping extraction for
  chaptered episodes was considered and rejected: fingerprints are one-time-cached, measured
  cheap (~0.3 s CPU/episode), and every extracted episode improves all-pairs anchor coverage
  for its chapterless season-mates. Correctness of neighbors > marginal CPU saving.
- **D6 — backfill is a boot-time, idempotent sweep** (`ChapterMarkerBackfillService`,
  modeled on `ArtworkRepairOnRestoreService`): items with stored chapters get the mapper
  applied without waiting for a rescan. Re-running is harmless (writes identical values);
  logs only when it changes something.

## Work items

### CM-WI-001 `ChapterMarkerMapper` + unit test matrix
Pure static mapper in `Services/Media/Detection`. Tests: title matrix (positive sets,
"Chapter 1"/"Scene 1"/"Recap" negatives, post/mid-credits negatives), positional guard
rejections, span derivation incl. post-credits ends, first-intro/first-credits selection,
degenerate inputs (empty list, zero duration, intro as last chapter).

### CM-WI-002 Scan-path integration
`VideoAnalysisStrategy.MapProbeToMediaItem` calls the mapper after persisting chapters;
writes all four timecodes + `Source = Chapter` on match; clears stale chapter-sourced values
per D4. The inline credits matching in `MediaProbeService` (and `MediaProbeResult.CreditsStart`)
is retired — the mapper is the single source of truth. Strategy-level tests cover write,
clear, and don't-touch-Detected paths.

### CM-WI-003 Boot-time backfill
`ChapterMarkerBackfillService` (Services/Background): settle delay, query Movie/Episode items
that have chapters, apply mapper, save only diffs, summary log. Tests: maps unsourced items,
overrides Detected with Chapter, leaves chapterless/Detected items alone, idempotent second run.

### CM-WI-004 Verification
Full suite (baseline 1554/0/0, no count decrease) + live: boot server against the real DB,
confirm the backfill log, then verify Futurama 09x10 gets IntroStart=0/IntroEnd=32.324/
IntroSource=Chapter and CreditsStart=1437.853/CreditsEnd≈1486.7/CreditsSource=Chapter, and
that detection re-runs do not disturb them.

### CM-WI-005 Docs
`docs/user-guide/background-jobs.md`: chapters-beat-detection precedence, what titles are
recognized, and that chapterless libraries lose nothing.

## Status

| Item | Status |
|---|---|
| CM-WI-001 mapper + tests | done (2026-07-25) |
| CM-WI-002 scan-path integration | done (2026-07-25) |
| CM-WI-003 backfill service | done (2026-07-25) |
| CM-WI-004 verification | done (2026-07-25) |
| CM-WI-005 docs | done (2026-07-25) |

## Session log

- 2026-07-25 — plan created; implementation in the same session (single-session scope).
- 2026-07-25 — CM-WI-001..003 implemented; 38 tests green on first run. First live backfill
  against the real library: **updated 166 of 185 chaptered items** (158 chapter-sourced
  intros where there had been zero; 166 credits with proper ends). Futurama 09x10 verified
  exact: intro 0→32.324 s, credits 1437.853→1486.777 s, both `Chapter`-sourced.
- 2026-07-25 — live QA found real mis-authored chapters ("My Three Suns": missing chapter
  entry → 471 s "Opening Credits" span). Added span-cap rejection thresholds to the mapper
  (intro ≤ 300 s, credits ≤ 900 s) and a clear-path to the backfill (a Chapter-sourced
  value whose stored chapters no longer map is stale by our own rules and must be cleared;
  detection re-fills from cached fingerprints). +4 tests.
- 2026-07-25 — verification ran in an ISOLATED git worktree (HEAD + exactly this plan's
  files): the primary working tree concurrently hosted a separate in-flight feature
  session (book metadata) plus a long-running integration-test loop holding the build
  outputs, so a mixed-tree suite run could not cleanly attribute results or validate this
  commit in isolation. The worktree run validates precisely what was committed.
- 2026-07-25 — CM-WI-004 complete. Isolated suite **1595/0/0** (HEAD baseline 1554 + 41).
  Second live backfill (worktree binaries, real DB): updated exactly 1 item — the
  "My Three Suns" clear-path — leaving 157 chapter-sourced intros (span max now 98 s,
  was 471) + 166 chapter-sourced credits; detection covers the remaining 46/53. Futurama
  09x10 confirmed stable across the re-run. Plan complete.
