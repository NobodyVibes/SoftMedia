# Background-FFmpeg CPU Remediation Plan — 2026-07-24

**Input:** live investigation of the maintainer-reported symptom "CPU shoots to 100% (7800X3D,
16 threads) during intro/credits detection, attributed to ffmpeg." All numbers below were
measured on this machine against the production binaries (`src/SoftMedia.Server/ffmpeg-bin/`,
jellyfin-ffmpeg 7.1.4) and real library media (Futurama x265 1080p, Arcane HEVC 10-bit), using
the exact argument strings the server builds. Methodology and reproduction commands are in §9.

**Relationship to other plans:** independent of
`docs/plans/system-review-remediation-plan-2026-07-24.md` (SR). Small enough to run as a single
session anywhere; it touches only `TrickplayService`, `TrickplayWorker`,
`ChromaprintFingerprintExtractor`, and (Phase 2) `LibraryScanQueueService`/`TranscodeService`
wiring. No schema changes, no client changes.

**How to work this plan:** same session protocol as the SR plan — read §7/§8 first, work one
phase, run each item's verification, update §7/§8 in the same commit. Capture suite baselines at
session start (server `dotnet test src/SoftMedia.Server.Tests`, client untouched by this plan).
Stop the dev server before building (bin lock).

---

## 1. Findings (evidence, not hypothesis)

Measurements are CPU-seconds of the ffmpeg process (TotalProcessorTime) over a 600-second media
window; "eff. cores" = CPU-time ÷ wall-time; machine has 16 logical cores.

| # | Workload (exact production args) | CPU | eff. cores | machine-wide |
|---|---|---|---|---|
| M1 | Chromaprint head window (`-vn -f chromaprint`), synthetic 1080p | 0.25 s | 0.24 | 1.5% |
| M2 | Chromaprint tail window (`-sseof -360`), synthetic | 0.28 s | 0.44 | 2.8% |
| M3 | Chromaprint head+tail ×9 real Futurama episodes (18 spawns) | 2.8 s total | — | negligible |
| M4 | Trickplay current args, synthetic easy-decode 1080p h264 | 36 s | **4.05** | 25% |
| M5 | Trickplay current args, real Arcane HEVC 10-bit | 10.1 s | 0.56 | 3.5% |
| M6 | Trickplay current args, real Futurama x265, cold + concurrent load | **53–54 s** | ~3 each | see F4 |
| M7 | Trickplay current args, same Futurama files, warm re-run | 9.0–9.6 s | ~0.8 | 18% total |
| M8 | Trickplay `-threads 2`, synthetic | 3.8 s | 0.34 | 2.1% |
| M9 | Trickplay `-skip_frame nokey -threads 2`, real Futurama | **0.09 s** | 0.05 | 0.3% |
| M10 | Matcher (`LongestCommonSegmentMatcher`), 36 real pairs ×2 windows, Server GC | 0.02 s, 26 MB alloc, **0 GCs** | — | negligible |

**F1 — The intro/credits detection pipeline itself is innocent.** Fingerprint extraction is
audio-only (`-vn` verified effective: M1–M3) and the all-pairs matcher is sub-second per season
with zero GC pressure (M10; a 22-episode season is 6.42× M10 ≈ 0.13 s). A full ~140-episode
detection run costs under a minute of ffmpeg CPU spread over several minutes of wall time.
**Non-goal confirmed: no matcher optimization, no detection throttling for CPU reasons.**

**F2 — Trickplay generation is the only unbounded CPU consumer, and it runs in the same window
as detection.** [TrickplayService.cs:132](../../src/SoftMedia.Server/Services/Media/TrickplayService.cs#L132)
decodes every frame of the whole episode with unbounded decoder threads at Normal priority;
[TrickplayWorker.cs:27](../../src/SoftMedia.Server/Services/Background/TrickplayWorker.cs#L27)
runs **two concurrently**, up to 25 items per 10-minute sweep. New episodes trigger both the
detection queue (at scan completion) and the trickplay backfill (next sweep ≤10 min later), so
the operator watches "detection running" while trickplay's ffmpeg processes burn the CPU.
Attribution to detection is understandable and wrong.

**F3 — Unbounded frame-threading is not just spiky, it multiplies total work.** `-threads 2` cut
*total CPU* 9.4× on the fast-decode case (M4→M8) — at high decode throughput, 16+ frame threads
mostly generate synchronization overhead. Capping threads is a pure win (wall time +26% on the
600 s window, irrelevant for a background sweep).

**F4 — The 100% condition reproduced once and is variance-dominated.** Simulating the post-scan
window (2 trickplay + detection extraction loop, real Futurama files) produced **98.4% average /
100% peak machine CPU for the window's duration** on the first (cold) run — with per-process
ffmpeg CPU inflated ~5× vs. the warm re-run (M6 vs M7) and ~9 cores unattributable in
retrospect. Identical re-runs stayed ≤18% total. Conclusion: trickplay's unbounded decode is the
enabling condition (it is the only component *able* to expand), but exact severity depends on
content, cache state, and co-tenants (AV scanning etc.). The plan therefore includes permanent
lightweight attribution (BG-WI-004) instead of pretending one-shot benchmarks settle it.

**F5 — Keyframe-only decode is safe for trickplay and ~110× cheaper on real content.**
`-skip_frame nokey` produced complete, correctly-ordered, visually-good sprite sheets on real
Futurama (M9; sheet inspected) and on synthetic fixtures. Trade-off: each tile is the nearest
keyframe at-or-before its 10 s slot, so a preview can lag its nominal timestamp by up to one
GOP (typically ≤10 s on WEB-DL/BluRay encodes). Accepted for scrub previews (industry-standard
technique); §6 Q1 records the sign-off, and BG-WI-001 includes a self-healing fallback for
exotic files.

**F6 — Everything else that spawns ffmpeg is on-demand/playback-path** (transcode sessions,
audio streaming, subtitle burn-in, single-frame previews behind a 4-slot gate, ffprobe) and out
of scope: those *should* compete at normal priority because a user is waiting on them.

---

## 2. Conventions

- IDs: `BG-WI-###`. Phases are ordered; later phases assume earlier ones are merged.
- Every item lists scope → acceptance → verification. "Live verify" = against a running server
  on `127.0.0.1:5011` (IPv4; IPv6 localhost stalls ~210 ms/request).
- Regression rule: net server-suite count must not decrease; behavior changes to
  trickplay/detection get tests in the same commit.
- Each phase ends with an adversarial diff review of the phase's changes.

---

## 3. Phase 1 — No-regret CPU fixes (single session)

### BG-WI-001 [H] Trickplay: keyframe-only decode with self-healing fallback
In `TrickplayService.GenerateAsync`, change the argument string to include, as **input** options
(before `-i`): `-threads 2 -skip_frame nokey`. Keep the filter chain unchanged.
- Fallback: after a successful run, if produced tile count < 50% of `duration/interval`
  expectation, log a warning and retry **once** without `-skip_frame nokey` (still with
  `-threads 2`). Covers hypothetical codecs/containers where keyframe skipping starves the fps
  filter. The retry decision must be logged with the file path so recurrences are visible.
- Existing sheets are untouched (generation is skipped when `HasTrickplay`); no regeneration
  sweep. Operators can delete `wwwroot/cache/trickplay/{id}` to regenerate individual items.
- Acceptance: unit test asserting the built argument string contains both flags and orders them
  before `-i`; test for the fallback trigger (inject a fake expectation shortfall).
- Verification (live): delete one real episode's trickplay dir, let the sweep regenerate it,
  inspect the sheet visually, and confirm logged CPU (BG-WI-004) is <1 s for a ~20-min episode.
- Measured basis: M9 (0.09 s CPU / 600 s window real content) vs M5–M7 (9–54 s today).

### BG-WI-002 [H] BelowNormal priority for background ffmpeg
Immediately after `Process.Start(...)` in `TrickplayService.GenerateAsync` and
`ChromaprintFingerprintExtractor.ExtractAsync`, set
`process.PriorityClass = ProcessPriorityClass.BelowNormal` inside a `try/catch`
(`InvalidOperationException` — the process may already have exited; log at Debug and continue).
- Rationale: the universal hardware-safety net. Whatever a pathological file costs, the Windows
  scheduler gives live transcodes/streams absolute preference over BelowNormal processes. This
  is also the honest answer to "options for a variety of hardware": adaptive behavior, zero new
  settings, correct on a quad-core NAS and on a 7800X3D alike.
- Do NOT touch playback-path spawns (transcode, audio stream, subtitle burn-in, previews) — a
  user is waiting on those.
- Acceptance: unit-testable via a small seam or verified in the live check below; no test-count
  decrease.
- Verification (live): during a sweep-triggered generation, `Get-Process ffmpeg | Select
  PriorityClass` shows `BelowNormal`; a concurrently playing stream shows no stutter.

### BG-WI-003 [M] Trickplay worker: serialize generations
Reduce the `TrickplayWorker` semaphore from `(2, 2)` to `(1, 1)`.
- Rationale: with BG-WI-001 a generation is sub-second CPU; concurrency 2 no longer buys
  meaningful throughput and doubles worst-case pressure (M6 showed the ×2 case is what
  saturates). Sweep cadence (10 min, 25 items) still clears a full season per sweep.
- Acceptance: existing worker tests updated; comment updated to reflect the reasoning.
- Verification: sweep of ≥3 pending items completes; log shows sequential generation.

### BG-WI-004 [M] Per-spawn CPU/wall telemetry for background ffmpeg
At process exit in `TrickplayService.GenerateAsync` and
`ChromaprintFingerprintExtractor.ExtractAsync`, log one Information line:
`"<Component> ffmpeg: {File} cpu={CpuSeconds:F1}s wall={WallSeconds:F1}s exit={Code}"` using
`process.TotalProcessorTime` (guard with try/catch — reading it after `Kill` can throw).
Additionally accumulate per-sweep totals in the existing `ScheduledTaskRegistry.Report` calls
(trickplay sweep + detection job) so the admin task page shows CPU cost per run.
- Rationale: F4 — the one-time 98% event had unattributable residual CPU. Next occurrence must
  be diagnosable from logs alone, without a live profiling session.
- Acceptance: unit test for the accumulation; log lines present in live run.
- Verification (live): run a sweep + a detection job; confirm both the per-file lines and the
  registry totals.

**Phase-1 exit review:** adversarial diff review; server suite green; no count decrease.

---

## 4. Phase 2 — Contention policy (defer background decode to playback)

### BG-WI-005 [M] Playback-activity gate for trickplay sweep and detection extraction
Introduce a tiny read-only helper (e.g. `IPlaybackActivityService` backed by the singleton
`TranscodeService`): *active playback* = any session in `Transcoding` or `Throttled` state whose
`LastClientRequestTime` is within `ClientInactivityTimeoutSeconds` (90 s — same constant the
throttle monitor uses; direct-play HTTP range serving needs no gate, it is I/O-bound).
- `TrickplayWorker.SweepAsync`: check before **each item**; if playback is active, end the sweep
  early (remaining items are picked up next cycle — the sweep model is already self-healing).
  Log once per aborted sweep.
- `IntroCreditsDetectionService.DetectAsync`: check between episodes in the fingerprint
  extraction loop (the natural checkpoint — fingerprints are already persisted per episode); if
  playback is active, stop cleanly with the existing preemption/requeue machinery
  (`LibraryScanQueueService` already re-runs detection jobs and resumes from checkpoints).
  Matching (post-extraction) is exempt — it is sub-second (F1).
- **No new setting** (see §6 Q2): with BG-WI-001/002 merged this gate is polish, and every knob
  is a support burden; unconditional-on is correct for the target deployment size.
- Acceptance: unit tests — sweep stops at the gate; detection stops between episodes and the
  requeued job resumes past already-fingerprinted episodes.
- Verification (live): start a stream, trigger a sweep + detection; logs show both deferring;
  stop the stream; both complete on the following cycle.

**Phase-2 exit review:** adversarial diff review; suite green.

---

## 5. Phase 3 — Live QA and documentation

### BG-WI-006 [M] Whole-scenario live verification on the real library
On the dev machine with the real TV library:
1. Delete trickplay for one full Futurama season + clear its fingerprints
   (`MediaFingerprints` rows) and intro/credits timecodes for a clean slate.
2. Trigger a scan → let detection + trickplay run their natural post-scan convergence.
3. During the window: play one transcoded stream and one direct-play stream.
- Acceptance: machine CPU attributable to SoftMedia background ffmpeg stays low (spot-check
  Task Manager; telemetry from BG-WI-004 sums to expectations — order of seconds, not minutes);
  both streams play without stutter; detection results (intro/credits timecodes) are unchanged
  vs. pre-plan values for the same season (sanity: detection touches audio only — this asserts
  no accidental coupling).
- If a 100%-CPU event still occurs during this QA, capture the BG-WI-004 log lines plus a
  per-process snapshot (`Get-Process | Sort CPU`) before touching anything — that evidence
  feeds a follow-up item rather than speculation. Check `MsMpEng` (Defender) specifically; if
  Defender is the residual, the remediation is an operator-level exclusion for
  `wwwroot/cache/trickplay/`, documented, not code.

### BG-WI-007 [L] Documentation
Update the scheduled-tasks/admin docs (`docs/user-guide/`): trickplay and detection are
low-priority background jobs, defer to playback, and log per-run CPU. Note the keyframe-only
trade-off (previews keyframe-aligned) and the per-item regeneration recipe.

---

## 6. Open questions (maintainer sign-off, non-blocking defaults)

- **Q1 — Keyframe-aligned previews acceptable?** Default: yes (F5, sheet inspected on real
  content). If ever rejected: drop `-skip_frame nokey`, keep `-threads 2` + BelowNormal — still
  a ~9× CPU reduction plus scheduler protection.
- **Q2 — Setting for the playback gate?** Default: no setting, unconditionally on (≤5-user
  deployments; fewer knobs). If multi-tenant/NAS profiles ever matter, revisit as a single
  "background jobs yield to playback" toggle — never a hardware-profile matrix.
- **Q3 — Regenerate existing trickplay?** Default: no. Existing sheets are visually identical
  in structure; only future generations change cost.

## 7. Status

| Item | Phase | Status |
|---|---|---|
| BG-WI-001 keyframe-only trickplay + fallback | 1 | done (2026-07-24) |
| BG-WI-002 BelowNormal background ffmpeg | 1 | done (2026-07-24) |
| BG-WI-003 serialize trickplay worker | 1 | done (2026-07-24) |
| BG-WI-004 per-spawn CPU telemetry | 1 | done (2026-07-24, see deviation note in §8) |
| BG-WI-005 playback gate | 2 | not started |
| BG-WI-006 whole-scenario live QA | 3 | not started |
| BG-WI-007 docs | 3 | not started |

## 8. Session log

- 2026-07-24 — plan created from live investigation (this file). No code changed yet.
- 2026-07-24 — Phase 1 implemented. Baseline 1540/0/0.
  - BG-WI-001: `GenerateAsync` now attempts `-threads 2 -skip_frame nokey` first, with one
    full-decode (`-threads 2`) fallback on nonzero exit or zero sheets. **Refinement vs.
    plan text:** the fallback trigger is *nonzero-exit-or-zero-sheets*, not a <50%-of-expected
    tile count — the `fps` filter duplicates sparse keyframes to hold cadence (verified on real
    content), so partial starvation is impossible and a duration plumb-through would have been
    dead complexity. Timeouts deliberately do NOT retry (a stuck source must not burn a second
    30-min ceiling); the existing heartbeat-stability test doubles as the no-retry proof.
  - BG-WI-002: `ProcessPriorityClass.BelowNormal` set best-effort after `Process.Start` in
    `TrickplayService.RunFfmpegAsync` and `ChromaprintFingerprintExtractor.ExtractAsync`.
  - BG-WI-003: `TrickplayWorker` gate reduced to `(1,1)` with rationale comment.
  - BG-WI-004: per-spawn Information lines (`Trickplay ffmpeg: … cpu= wall= exit=`,
    `[Fingerprint] ffmpeg: … cpu= wall=`); `GenerateAsync` returns
    `TrickplayGenerationResult(Success, CpuSeconds, WallSeconds)` and the sweep reports
    `"Success — N generated, X.Xs ffmpeg CPU"` to the task registry. **Deviation:** detection-job
    registry CPU totals were dropped as measured-trivial (2.8 s CPU per 9 episodes, §1 M3) —
    per-spawn lines cover attribution; plumbing extractor→queue totals wasn't worth the coupling.
  - Tests: 3 new (attempt sequencing + flag placement, single fallback on nonzero exit,
    no-fallback on success incl. manifest publish); 2 updated for the result-record signature.
  - Scope addition: client `ScheduledTasksCard.ResultBadge` now prefix-matches
    Success/Failed (the enriched sweep string would otherwise lose its green badge);
    `npm run build` green.
  - Suite after Phase 1: server 1543/0/0 (baseline 1540 + 3).
  - Live verification (real library): deleted trickplay for Futurama S01E09, server sweep
    regenerated it — 2 sheets, `cpu=0.3s wall=3.9s exit=0 keyframesOnly=True` (was 9–54 s
    CPU pre-change), ffmpeg observed at BelowNormal in 22/22 samples, sweep reported
    "1 sheet sets (0.3s ffmpeg CPU)" to log + registry. Fallback path also exercised live
    by a pre-existing 0-byte library file (`Doctor Who S00E01 …mp4`, AVERROR_INVALIDDATA):
    one keyframe-only attempt, one full-decode retry, clean failure — as designed. That
    file re-fails every sweep at ~0 s cost; operator should replace it (pre-existing issue,
    out of scope).

## 9. Methodology / reproduction

All commands use `src/SoftMedia.Server/ffmpeg-bin/ffmpeg.exe`. CPU measured via
`Process.TotalProcessorTime` at exit; machine-wide via `\Processor(_Total)\% Processor Time`.

- Chromaprint head (exact `ChromaprintFingerprintExtractor` args):
  `-hide_banner -loglevel error -ss 0 -t 600 -i <file> -ac 1 -ar 11025 -vn -f chromaprint -fp_format raw -`
  (tail: `-sseof -360` instead of `-ss 0 -t 600`).
- Trickplay current (exact `TrickplayService` args):
  `-y -i <file> -vf "fps=1/10,scale=320:180,tile=10x10" -an -start_number 0 -q:v 5 <dir>\sheet-%d.jpg`
  (benchmarks bounded with `-t 600` for comparability).
- Proposed: same with `-threads 2 -skip_frame nokey` inserted before `-i`.
- Matcher bench: console project compiling the production
  `LongestCommonSegmentMatcher.cs`/`ISegmentMatcher.cs` sources directly,
  `<ServerGarbageCollection>true</ServerGarbageCollection>`, fed with fingerprints extracted
  from Futurama S01E01–E09 by the exact command above; 36 pairs × head+tail; measured wall/CPU/
  `GC.GetTotalAllocatedBytes`/`GC.CollectionCount`.
- Post-scan simulation: 2 concurrent trickplay (current args) + continuous chromaprint respawn
  loop on 4 real episodes while sampling total CPU at 1 Hz. First (cold) run: 98.4% avg /
  100% peak, ffmpeg CPU 53+54 s per 600 s window; warm re-runs ≤18% total, 9–10 s each —
  variance documented as F4.

*Origin note: severity numbers in F-findings are from the 2026-07-24 investigation on the dev
machine (7800X3D, 16 threads, Windows 11); rerun M-rows after any ffmpeg binary upgrade.*
