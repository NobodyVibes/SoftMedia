# SoftMedia — Remediation & Gap-Closure Plan (2026-07-15)

**Status:** In Progress — **Phases A + B + C COMPLETE** (all 20 R-WI items landed, LIVE-verified, adversarially diff-reviewed; final items 2026-07-18). Server suite 1044 pass / 1 skip / 0 fail; client 237 pass. Next: the tracked **post-Phase-C bug backlog** (`docs/plans/post-phase-c-bug-backlog.md`, 21 product + 1 test-infra entries), then maintainer-gated R-WI-001 remainder + §7 open questions. See §9 Implementation Log.
**Source report:** [docs/reports/feature-gap-analysis-2026-07-15.md](../reports/feature-gap-analysis-2026-07-15.md)
**Suggested branch:** `remediation/gap-closure-wave-1` off `main`
**Relationship to other plans:** Complements the [Master Implementation Plan](./feature-implementation-plan-2026-06-16.md) (Docker, subtitles, HDR, VAAPI, photos, OpenSubtitles) and the [roadmap](./roadmap/00-roadmap-overview.md). This plan covers *defects and untracked gaps the July review found beyond those documents*. Where an item interacts with a master-plan task, the dependency is called out.

---

## 1. Purpose & Scope

The July 2026 review confirmed a set of defects (half-built features whose "last wire" was never connected) and a set of verified-absent, untracked gaps. This plan sequences their remediation into work items (`R-WI-###`), each with the standard fields from `docs/plans/roadmap/00-roadmap-overview.md §6`, so the work can be picked up and carried across sessions without re-deriving context.

**In scope:** the D-* defects and the highest-leverage §3 gaps from the source report.
**Out of scope:** anything in the Master Implementation Plan or the P4 deferred register (§4 of the source report lists the exclusions). Enterprise features, telemetry, and first-party cloud are excluded by the standing charter (`00-roadmap-overview.md §4`). §3 gaps *considered but not selected for this wave* are triaged in §5b — deliberately, not by omission.

## 2. Guiding Principles (inherited from the charter)

These are not optional; they restate `00-roadmap-overview.md §5` and `docs/rules/` for convenience.

1. **Privacy-first / local-first.** No telemetry, no phone-home, no mandatory account. Any new outbound call must be opt-in and documented in the README egress list.
2. **Back-to-front.** Backend endpoint + DTO + xUnit tests exist and pass before any React consumer is written.
3. **Layering.** Controllers → Services → Repositories → DbContext. No new static globals; resolve via DI.
4. **Universal client.** Every interactive element meets the accessibility, focus-visibility, and 44×44px touch-target rules.
5. **Path safety.** Any path-touching item resolves symlinks via `FileInfo.ResolveLinkTarget(returnFinalTarget: true)`; `Path.GetFullPath` alone is insufficient.
6. **Correctness over features.** The P0 security/defect items land before net-new feature work.
7. **No regressions.** Every item ships with tests; the full suite (currently ~869 server / ~168 client) stays green.

## 3. Priority Model

| Priority | Meaning |
|----------|---------|
| **P0** | Security exposure or a defect actively degrading shipped behaviour. Do first. |
| **P1** | Completes a half-built feature, or a high-impact/low-effort gap. |
| **P2** | Valued feature; moderate effort. |
| **P3** | Nice-to-have; larger or lower-reach. |

## 4. Workstream Overview

| ID | Title | Priority | Effort | Closes | Status |
|----|-------|----------|--------|--------|--------|
| R-WI-001 | Purge committed secrets & debris; history scrub; rotate | **P0** | S | D-11 | ✅ untrack+gitignore landed; ⏳ rotate + history-purge maintainer-gated |
| R-WI-002 | Persist the stream plan server-side (foundation) | **P0** | M | enables D-2/D-3/D-4 | ✅ landed + live-verified |
| R-WI-003 | Real remux (stream-copy) path | **P0** | M | D-3 | ✅ landed + live-verified + reviewed |
| R-WI-004 | Surround-preserving audio in transcode | P1 | M | D-2 | ✅ landed + live-verified + reviewed |
| R-WI-005 | Far-seek preserves negotiated parameters + bitrate cap | **P0** | S–M | D-4 | ✅ landed + live-verified (+ fixed pre-existing far-seek 500) |
| R-WI-006 | Enforce API-token scopes; role claim only for admin scope | **P0** | M | D-5 | ✅ core landed; ⏳ Account/Webhooks + write:library deferred |
| R-WI-007 | Register/refresh file watchers on library create/edit | **P0** | S | D-1 | ✅ landed |
| R-WI-008 | Scheduled periodic library scans | P1 | S–M | §3 library | ✅ landed + live-verified + reviewed |
| R-WI-009 | Admin write surface for per-user bitrate cap | P1 | S | D-6 | ✅ landed + live-verified + reviewed |
| R-WI-010 | Surface & seed the DLNA settings group in the UI | P1 | S | D-9 | ✅ landed + live-verified + reviewed |
| R-WI-011 | Explicit, visible content-rating choice at user creation | P1 | S | D-8 | ✅ landed + live-verified + reviewed |
| R-WI-012 | Robust subtitle-path handling (temp-file extract) | P1 | S | D-10 | ✅ landed + live-verified + reviewed |
| R-WI-013 | Play-count & per-play history | P1 | M | D-7 | ✅ landed + live-verified + reviewed |
| R-WI-014 | Local artwork sidecars for movies/TV | P1 | S–M | §3 library | ✅ landed + live-verified + reviewed |
| R-WI-015 | Media Session API (lock-screen / media keys) | P1 | S | §3 playback |
| R-WI-016 | "Now Playing" active-sessions admin dashboard + terminate | P2 | M | §3 admin |
| R-WI-017 | Multi-field library search | P2 | M | §3 discovery + **D-12** |
| R-WI-018 | Subtitle appearance + timing-offset settings | P2 | M | §3 playback |
| R-WI-019 | Inbound scan-trigger webhook for *arr | P2 | S | §3 integrations |
| R-WI-020 | Personalized home rows ("Because you watched") | P3 | M | §3 discovery |

**Sequencing.** Phase A (P0): R-WI-001, 002→(003,005), 006, 007. Phase B (P1): 004, 008, 009, 010, 011, 012, 013, 014, 015. Phase C (P2+): 016, 017, 018, 019, 020. Within a phase, items are independent unless a dependency is listed.

---

## 5. Work Items

### R-WI-001 — Purge committed secrets & debris 🔴 P0 *(closes D-11)*

**Motivation.** `login.json` (admin/admin123, two copies), `token.json` (admin JWT), and four `softmedia.db.pre-restore-*` snapshots (real password hashes) are tracked in git; the README advertises a public repo URL. Deleting files does not remove them from history.

**Specification.**
1. `git rm --cached` all tracked secrets/debris: both `login.json`, `src/SoftMedia.Server/token.json`, the four `softmedia.db.pre-restore-*`, and the debris set (`build*.log`, `dump.txt`, `dunedump.*`, `test_*.txt`/`.log`, `ef_errors*.txt`, `DumpDune.cs`, `plan_copy.md`, `image_test.ps1`, `library_test.ps1`, `music_diagnostic.ps1`, and the `src/SoftMedia.Server/build_*` logs).
2. Extend `.gitignore` to cover **every pattern removed in step 1**: `login.json`, `token.json`, `*.db`, `*.db-shm`, `*.db-wal`, `*.db.pre-restore-*`, `build*.log`, `build_*.txt`, `*.trx`, `dump*.txt`, `dunedump.*`, `test_*.txt`, `test_*.log`, `test_results*.txt`, `ef_errors*.txt`, plus a scratch convention (e.g. `*.local.ps1`) for ad-hoc diagnostics. One-off files (`DumpDune.cs`, `plan_copy.md`) are prevented by convention, not ignore rules — the acceptance criterion applies to the patterned classes.
3. **Rotate the exposed secrets** (deletion is not remediation for anything ever committed): change the default admin password and rotate the JWT signing key so the committed token is void. Verify no real secrets live in `appsettings.json` (only in `appsettings.Development.json` / env / user-secrets).
4. Fold the object removal into the **already-queued ffmpeg-binary history purge** (`.docs/project_checklist.md` "Git-history purge"; licensing plan) — one `git filter-repo`/BFG pass over both. **Maintainer-gated:** coordinate with the repo public/private decision; history rewrite requires force-push and collaborator re-clone.

**Files affected.** `.gitignore`; repo-root and `src/SoftMedia.Server/` cleanup; JWT key config; admin-seed path.

**Acceptance criteria.** `git ls-files` shows none of the named files. A fresh clone contains no `*.json` credentials, no `*.db*`, no build logs. The previously-committed JWT no longer authenticates (key rotated). `.gitignore` prevents recurrence (verified by `git status` after regenerating each artifact).

**Effort.** S (½–1 day; the history-purge half is maintainer-gated and can trail).
**Dependencies.** None for steps 1–3; step 4 coordinates with the licensing history purge.
**Risks.** History rewrite invalidates existing clones/PRs — announce and time it. Rotating the JWT key logs everyone out once (acceptable).

**Implementation status (2026-07-16).** ✅ Steps 1–2 landed: 61 tracked debris files untracked via `git rm --cached` (working-tree copies preserved; the set was larger than first catalogued — it also included `src/SoftMedia.Server/login.json`, `src/SoftMedia.Server/token.json`, four `*.pre-restore-*` snapshots, and whole `tests/…/TestResults` + `src/SoftMedia.Tests/…` output trees). `.gitignore` extended to cover every removed pattern (secrets, `*.db.pre-restore-*`, `*.trx`, `test_*.txt|log`, `dunedump.*`, `ef_errors*.txt`, ad-hoc `*.ps1`); verified all 9 sample paths are now ignored and none remain tracked. The staged deletions are **uncommitted** (left for maintainer review). ⏳ **Not done (maintainer-gated):** step 3 (rotate the admin password + JWT signing key — the committed secrets remain valid until rotated) and step 4 (git-history purge). Deletion is not remediation for anything already committed.

---

### R-WI-002 — Persist the stream plan server-side 🔴 P0 *(foundation for D-2/D-3/D-4)*

**Motivation.** The root cause behind three defects: `StreamPlanService` negotiates method/codec/channels/resolution/bitrate/HDR, but the transcode endpoint only sees whatever the client puts in the URL, so the plan's decisions are lost. Fixing remux, surround, and far-seek independently would each re-derive the plan; persisting it once fixes all three and removes a cap-bypass.

**Specification.** Add a small, dedicated **plan store**: a `sid`-keyed map (e.g. `ConcurrentDictionary<(Guid mediaId, Guid userId, string sid), PersistedPlan>` hung off `TranscodeSessionManager`, or a new `IPlanRegistry`) populated by `StreamPlanService` at plan time, with its own TTL. Deliberately **not** the `TranscodeSession` objects: those are process-bound (SessionDirectory/Process/State), do not exist at `POST /plan` time, are keyed by `TranscodeSessionKey(MediaId, UserId, SubtitleTrackIndex, StreamId)` (`Models/TranscodeSession.cs:8` — the subtitle index changes mid-playback), and — decisively — are destroyed by the client's own `DELETE`-before-reseek and by every parameter-change restart (`StopSessionInternalAsync` → `TryRemoveSession`, `TranscodeService.cs:297-312,751-756`), which are exactly the events the plan must survive. `TranscodeSession` entries remain pure process-lifecycle objects; `StartTranscodeAsync`/`GetMasterPlaylist` resolve missing or omitted quality params from the plan store, with URL query params as fallback for first contact only. **Scope:** persistence applies to Remux/Transcode plans with a non-empty `sid`; DirectPlay plans are stateless by design (they carry no `sid` — `StreamPlanService.cs:362-382`), and `sid`-less transcode requests keep today's URL-param behaviour. The `sid` is already validated server-side (WS-7 hardening) — reuse that; never trust client-supplied quality params once a plan exists.

**Sequencing note (WS-6).** The wave-2 security follow-up T6.1 will reject role-bearing tokens in query strings; the `master.m3u8` query token must be (or become) a **media/cast token**, not a full access token. Write the new integration tests using media tokens so they survive T6.1/T6.2 landing.

**Files affected.** `Services/Media/StreamPlanService.cs`, `Services/Transcoding/TranscodeSessionManager.cs` (new plan store; `TranscodeSession` untouched), `Controllers/TranscodeController.cs`.

**Acceptance criteria.** Given a computed plan, a `master.m3u8` request carrying only `token`(media)+`sid` reproduces the plan's resolution/codec/bitrate/HDR/audio decisions (unit test on the resolver; integration test asserting the ffmpeg args match the plan). **The exact client sequence `DELETE /api/transcode/{id}?sid=…` → `GET master.m3u8`** (what `handleSeekToTime` does, `VideoPlayer.tsx:1459,1479`) still resolves the persisted plan — session teardown must not evict it (integration test). No behavioural change for a first request that still carries full params.

**Effort.** M.
**Dependencies.** None (unblocks 003, 004, 005).
**Risks.** The evictors that matter are the client `DELETE`-on-seek and the server param-change restart — both must leave the plan store intact (the hourly `TranscodeSegmentCleanupService` janitor is benign here; it only walks disk directories). Coordinate with the deferred WS-7 **M-5** (atomic count-and-reserve on the transcode cap): this item reworks the same session-registration area — consider closing M-5 here, and do not widen the race.

**Implementation status (2026-07-16).** ✅ Landed as the Rev.2 spec directs — a **dedicated `sid`-keyed store** (`Services/Transcoding/StreamPlanStore.cs`, `IStreamPlanStore`, singleton, 12h TTL), **NOT** on the `TranscodeSession` objects. `StreamPlanService.CreateTranscodePlan` exposes the resolved quality params on the `StreamPlan` DTO (`Transcode{Resolution,Codec,MaxBitrate,PreserveHdr}`, `[JsonIgnore]`); `GetStreamPlan` persists (Transcode/Remux + valid sid); `GetMasterPlaylist` overrides query values from the store (sub/audio/seek/burnSubtitles stay from the request). **Live-verified** (real transcode of a synthetic 1080p clip): far-seek `master.m3u8` with only `token+sid` restored 720p scaling and `-maxrate 2000k`; injected `bitrate=50000` on a *negotiated* sid ignored.

**Diff-review fixes (2026-07-16, 3-reviewer adversarial pass on the playback diff):** the live test above only covered the *happy path*; review found and I fixed two holes. **(HIGH)** the per-user bitrate cap was enforced only when a plan existed — a client using a **never-negotiated / fabricated sid** (so `storedPlan` is null) could pass `?bitrate=50000` straight to ffmpeg. Fix: `GetMasterPlaylist` now clamps the effective bitrate against `GetUserMaxBitrateAsync` on **every** request, plan or no plan. **Live-verified**: with the admin cap set to 3000, `master.m3u8?sid=fabricated999&bitrate=50000` produced `-maxrate 3000k` (not 50000). **(MEDIUM)** the store's "soft cap" only pruned expired entries → unbounded growth by cycling unique sids, and the sid was unvalidated on the `Save` path. Fix: `Save` now rejects malformed sids (`TranscodeSid.IsValid`) and enforces a hard 2048-entry cap evicting soonest-to-expire. Tests: `StreamPlanStoreTests` (7 — round-trip, isolation, sid-less, overwrite, malformed-sid reject, hard-cap bound). **Residual (documented follow-up, pre-existing, lower severity):** a fabricated-sid direct `master.m3u8` can still request a higher resolution/codec than server policy (`MaxTranscodeResolution`/`OutputVideoCodec`) allows — the per-user *bitrate* control (the D-4 defect) is fully closed; server-wide resolution/codec/network-cap enforcement on the direct path is a separate hardening item (ideally: require a resolvable plan for transcode).

---

### R-WI-003 — Real remux (stream-copy) path 🔴 P0 *(closes D-3)*

**Motivation.** A `Method=Remux` plan currently re-encodes (full CPU/GPU cost + quality loss) where `-c copy` would suffice.

**Specification.** With R-WI-002 in place, the transcode endpoint branches on the persisted method: for Remux, emit an ffmpeg command that stream-copies both tracks (`-c copy`) into HLS segments. **Segment container decision: the remux path always uses fMP4 segments** — the current pipeline defaults to MPEG-TS and uses fMP4 only for HDR/AV1 (`TranscodeProfileBuilder.cs:369-389`), but the prime remux case is HEVC-in-MKV for an HEVC-capable client, and HEVC copied into TS segments will not play on the Safari/hls.js clients that advertise HEVC; `init.mp4` handling already exists on the fMP4 path. Mixed sources (only one track compatible) stay on the **Transcode** path — `CanRemux` already requires both tracks compatible (`StreamPlanService.cs:334-360`), and R-WI-004's audio-copy preference banks the copy-the-compatible-track saving there; do not change the `CanRemux` boundary in this item. Add a distinct arg-builder path (do not thread copy flags through the full transcode arg builder).

**Files affected.** `Services/Transcoding/TranscodeProfileBuilder.cs` (or a sibling remux builder), `Services/Transcoding/FFmpegService.cs`, `StreamPlanService.cs` (ensure Remux vs Transcode is recorded in the persisted plan).

**Acceptance criteria.** A remux-eligible source produces an ffmpeg command containing `-c copy` and no encoder options — asserted by an arg-construction test mirroring the existing `TranscodeProfileBuilder` test style, **including an HEVC-in-MKV case asserting fMP4 segments**. Live: a container-mismatch-only file plays via HLS with copied streams (no re-encode); CPU stays near-idle vs the transcode baseline.

**Effort.** M.
**Dependencies.** R-WI-002. **Same-file sequencing:** this item, R-WI-004, and R-WI-012 all touch `TranscodeProfileBuilder.cs`, which master-plan Phase 3 (P3a HDR / P3b VAAPI) also rewrites under an explicit "must not be developed in parallel — same file" rule; serialize these against P3a/P3b (recommended order: this plan's transcode items first, they are smaller).
**Risks.** Not every "remux" is copy-safe (timestamp/keyframe issues over HLS); keep a documented fallback to transcode when copy fails, and log it. Throttling buffer math assumes 6-second segments (`TranscodeService.HlsSegmentDurationSeconds`) and becomes approximate under copy (segments follow source keyframes) — acceptable; do not "fix" it blindly.

**Implementation status (2026-07-16).** ✅ Landed + **live-verified**. New `session.IsRemux` (set from the persisted plan's `Method`); `TranscodeProfileBuilder.BuildRemuxArguments` emits `-map 0:v:0 -map 0:a:0 -c copy` into **fMP4** segments (init.mp4 + `.m4s`, `independent_segments`); `StartFFmpegProcessAsync` takes it only when `IsRemux && no bitmap burn-in` (bitmap subs still transcode to burn in); a remux↔transcode switch is a params-change restart; the controller derives `remux` **only** from the stored plan (no client-forcable query param). Live-verified against a real H.264/AAC MKV: plan negotiated Remux, ffmpeg ran `-c copy … -hls_segment_type fmp4`, and a valid `init.mp4` + 550 KB `.m4s` segment played. Tests: `TranscodeProfileBuilderTests` (+6 arg-construction incl. fMP4/`-c copy`/no-encoder/seek).

**Diff-review fixes (2-reviewer adversarial pass).** **(HIGH — caught proactively before review returned)** a stream-copy has no `-maxrate`, so making remux real *removed* the incidental bitrate-cap enforcement the old re-encoding "remux" had → a capped user could pull a high-bitrate remux-eligible source uncapped. Fix: `RemuxFitsBitrateCeiling` gates the Remux decision on `probe.Bitrate` ≤ the effective cap; over-cap sources transcode (which applies `-maxrate`). **(HIGH/regression)** `CanRemux` reused the *direct-play* codec sets, but the fMP4 muxer rejects some (Vorbis → "no tag for codec"; VP8/MP3 unreliable) — a Vorbis-in-MKV source would negotiate Remux then *fail* at ffmpeg (503, worse than the old re-encode that played). Fix: new `RemuxVideoCodecs`={h264,hevc} / `RemuxAudioCodecs`={aac,ac3,eac3} — remux only fMP4-safe codecs; others transcode. **(LOW)** a plan-store expiry mid-playback could flip a running remux session to a re-encode → the store now uses **sliding TTL** (refresh on resolve). Tests: `StreamPlanServiceBitrateTests` (+4: within-cap remux, over-cap transcode, no-cap remux, Vorbis→transcode). Full suite **896/1/0**.

**Remaining LOW follow-ups (documented, not blocking, pre-existing-adjacent):** (a) the throttle buffer's fixed-6 s assumption drifts for copy (real-EXTINF-based sizing would be exact) — playback unaffected (hls.js uses real durations); (b) the admin debug panel infers `toneMapped` from HDR+PreserveHdr and mis-labels a remux HDR copy (cosmetic, admin-only — the user-facing explainer uses the `/plan` response); (c) **DirectPlay also serves the original bitrate**, so it bypasses the cap the same way remux would have — same class, pre-existing, broader; a separate hardening item (gate direct play / require a plan for streaming).

---

### R-WI-004 — Surround-preserving audio in transcode 🟠 P1 *(closes D-2)*

**Motivation.** Transcodes force stereo AAC 128k even when the plan computed AC3 5.1 for a capable client; the plan already advertises surround it never delivers.

**Specification.** Add audio codec/channel/bitrate to the persisted plan (R-WI-002) and to the transcode arg builder. Preference order: **copy** audio when the client supports it → **encode at the plan's channel count/codec** (e.g. AC3 5.1) when supported → **stereo AAC** as the last resort. Drive the choice from the capabilities `StreamPlanService` already parses.

**Files affected.** `Services/Transcoding/TranscodeProfileBuilder.cs`, `TranscodeSettings`, `Controllers/TranscodeController.cs`, `Services/Media/StreamPlanService.cs`.

**Acceptance criteria.** Arg-construction tests: surround-capable plan → `-c:a ac3 -ac 6` (or copy); stereo-only client → the existing stereo args. Plan DTO and emitted args agree (closes the advertise/deliver mismatch). Live-verify a 5.1 source transcodes to 5.1 for a capable client.

**Effort.** M.
**Dependencies.** R-WI-002. Same-file sequencing note as R-WI-003 (serialize against master-plan P3a/P3b).
**Risks.** AC3 must survive the **client** path, not just ffmpeg (which muxes AC3 into TS or fMP4 trivially): the pipeline emits TS segments by default, and hls.js's AC-3 passthrough through TS transmuxing is version/browser-dependent, while the client capability probe tests AC3 **in fMP4** (`useMediaCapabilities.ts:102`). Either verify the pinned hls.js passes AC3-in-TS on an AC3-capable browser, or emit fMP4 segments whenever audio is AC3 (matching what the client actually probes). Keep the stereo fallback. The ladder itself is safely capability-gated (`MediaSource.isTypeSupported` — Chrome, which lacks an AC3 license, never sees the ac3 advertisement).

**Implementation status (2026-07-16).** ✅ Landed + **live-verified**. `StreamPlanService.CreateTranscodePlan` now resolves the audio ladder — **copy** the source audio when it is fMP4/TS-muxable (`RemuxAudioCodecs` = aac/ac3/eac3, the same container-safe set as remux) *and* client-decodable; else **encode** AC3 5.1 for a surround-capable client on a multichannel source; else stereo AAC. The decision (`TranscodeAudioCopy`/`Codec`/`Channels`) rides the same plan-store → session path as the video params; `TranscodeProfileBuilder.BuildAudioArgs` emits `-c:a copy` or `-c:a {codec} -ac N -b:a Xk`, replacing the old forced `-c:a aac -ac 2 -b:a 128k`. **Live-verified:** AC3 5.1 MKV → `-c:a copy`; FLAC 5.1 MKV → `-c:a ac3 -ac 6 -b:a 448k`; both produced playable `.ts` segments; neither was stereo AAC.

**Diff-review fixes (2-reviewer pass — both converged on the same HIGH the single-track live test missed).** **(HIGH)** `-c:a copy` with no explicit `-map` let ffmpeg pick the *highest-channel* audio stream, but the plan validated the *first* track — on a multi-track file (AC3 5.1 default + DTS-HD/FLAC 7.1 alternate) it would copy the undecodable alternate → no audio / mux abort. Fix: **pin `-map 0:v:0 -map 0:a:0`** for the default case (guarded on the source actually having audio). **Live-verified** with a 2-track clip (AC3 5.1 + FLAC 8ch): ffmpeg emitted `-map 0:a:0 -c:a copy` (track 0), not the FLAC alternate. **(MEDIUM)** copied audio has no `-b:a` ceiling (E-AC3 Atmos ~1.5 Mbps) → a capped user's total exceeds the video cap; fix: when a bitrate cap is in effect, prefer a **bounded encode** (≤448k) over copy (mirrors the remux gate). **(LOW)** an explicitly-selected non-default track was encoded with the *default* track's negotiated channel count (wrong up/downmix) → a selected track now encodes to neutral AAC preserving its own layout. Tests: `TranscodeProfileBuilderTests` (+7 arg incl. copy-map-pin) + `StreamPlanServiceBitrateTests` (+4 plan-decision incl. capped-encode). Full suite **906/1/0**.

**Remaining LOW follow-up (documented):** the throttle buffer's fixed-6 s assumption also drifts for an audio-copy segment stream (same class as the R-WI-003 note); playback unaffected (hls.js uses real EXTINF).

---

### R-WI-005 — Far-seek preserves negotiated parameters 🔴 P0 *(closes D-4)*

**Motivation.** Seeking past the transcoded range restarts the session without the resolution cap, bitrate cap, codec, or HDR flag — and bypasses the admin per-user bitrate cap.

**Specification.** Two-part. **Server (real fix):** with R-WI-002, a restarted/seek session resolves its parameters from the persisted plan by `sid`, so a seek cannot lose them and a hand-crafted `master.m3u8` cannot bypass `MaxStreamBitrateKbps`. **Client (fast patch, ship first):** rebuild the far-seek URL **the same way the initial URL is constructed** — `plan.url`'s negotiated params (`resolution`/`codec`/`bitrate`/`hdr`/`sid`) **merged with** the live player state the client appends after plan receipt (`sub`, `audio`, `burnSubtitles` — `VideoPlayer.tsx:682-695`; note `plan.url` alone does *not* contain these, and today's seek URL drops `burnSubtitles` too) plus a fresh token and the `seek` offset. Do not silently rely on hls.js `xhrSetup` token rewriting (`VideoPlayer.tsx:782-788`) — request a current token explicitly. See R-WI-002's WS-6 sequencing note (query tokens become media/cast tokens).

**Files affected.** `Controllers/TranscodeController.cs`, `TranscodeSessionManager`; client `src/components/player/VideoPlayer.tsx`.

**Acceptance criteria.** Integration test: a far-seek `master.m3u8` request for an existing `sid` yields ffmpeg args with the same caps/codec/HDR as the initial segment, and the per-user bitrate cap still applies. Client test: the rebuilt seek URL contains **the full param set — `resolution`, `codec`, `bitrate`, `hdr`, `sid`, `sub`, `audio`, `burnSubtitles`** — matching the pre-seek state. Live: seek to the end of a bitrate-capped transcode with a subtitle selected; the cap holds and the subtitle survives.

**Effort.** S (client) + M (server, shared with R-WI-002).
**Dependencies.** R-WI-002 for the server half.
**Risks.** None notable once the plan is authoritative.

**Implementation status (2026-07-16).** ✅ Landed. **Server half** is delivered by R-WI-002's resolver (above) — a far-seek's minimal URL is resolved against the stored plan, so quality/codec/HDR and the per-user bitrate cap survive and cannot be bypassed. **Client half:** `VideoPlayer.tsx` now builds the far-seek URL via a shared `buildTranscodeSeekUrl` helper that additionally carries `burnSubtitles` (previously dropped on seek, resetting burn-in to off); quality params are intentionally omitted since the server restores them. **Live verification uncovered a pre-existing defect and fixed it:** every session-restarting far-seek was returning **HTTP 500** (`ObjectDisposedException` at `TranscodeSessionManager.SessionLock.Dispose` → `SemaphoreSlim.Release`, because a restart's `TryRemoveSession` disposes the per-key semaphore the held lock still references — the L-25 security disposal). Confirmed pre-existing: a sid with **no** stored plan (resolver bypassed) reproduced the 500 identically. After the fix the exact client sequence `DELETE …?sid=… → GET master.m3u8?token+sid&seek=…` returns **200** with the plan restored. Client `tsc` clean.

**Diff-review fix (MEDIUM → root-cause redesign).** The initial fix only swallowed the `ObjectDisposedException` in `SessionLock.Dispose`; review showed that left the *real* hazard: `TryRemoveSession` disposed/removed a per-key semaphore that another same-key request might still hold or await — orphaning a queued waiter (silent hang) or letting a fresh semaphore be created for a still-held key (lost mutual exclusion → two concurrent ffmpeg for one session, which also aggravated the deferred M-5 capacity race). Root-cause fix in `TranscodeSessionManager`: `TryRemoveSession` **no longer disposes** the lock; the table is bounded by `PruneIdleLocks()`, which evicts only provably-idle semaphores (`CurrentCount == 1` = no holder and no waiter) once past a 256 cap; `AcquireLockAsync` retries on the narrow prune-vs-acquire race; the `Dispose` swallow stays as defense-in-depth. Tests: `TranscodeSessionManagerLockTests` (3 — tolerates disposed semaphore, normal release, **mutual exclusion preserved across session removal**) + `TranscodeIntegrityTests.SessionLockTable_IsBounded_ByIdlePruning` (revised from the old dispose-on-remove contract). Full suite 886/1-skip/0.

---

### R-WI-006 — Enforce API-token scopes 🔴 P0 *(closes D-5)*

**Motivation.** "Read-only" tokens can mutate data and — if minted by an admin — hit admin endpoints, because scope policies are attached almost nowhere and the token principal carries the owner's role.

**Specification.**
1. Attach `ReadLibrary`/`ReadState` to read endpoints and require `WriteState` (or a new `WriteLibrary`) on every mutating endpoint currently on plain `[Authorize]` (playlists, watchlist, collections write, user preferences, etc.). Prefer a convention/base policy so new endpoints are secure-by-default rather than per-attribute drift.
2. **Emit the role claim on API-token principals only when the token carries the `admin` scope.** The `admin` scope is a shipped, UI-advertised feature ("Full admin access (admins only)", `ApiTokensCard.tsx:11,65`; `Models/ApiToken.cs:53`) — the defect is that `ApiTokenAuthenticationHandler.cs:60` emits the role claim **unconditionally**, regardless of scopes, making every token as powerful as its owner. Gate the claim on the scope (or, if the maintainer prefers, remove the `admin` scope from `ApiTokenScopes.All` and the UI in the same change — either way, behaviour must match the advertisement).
3. **Add a `write:library` (or `trigger:scan`) scope** to `ApiTokenScopes` + `ScopePolicies` — R-WI-019's scan endpoint needs a least-privilege gate that is neither `Roles=Admin` nor full `admin` scope.
4. Decide token-scope granularity (roadmap open question #2). If the scope model is deemed over-engineered for a home server, the acceptable simplification is a single clearly-labeled full-access token — **not** advertised scopes that don't enforce. Note this option forecloses the least-privilege *arr key in R-WI-019.

**Files affected.** `Services/Identity/ScopeAuthorization.cs`, `Services/Identity/ApiTokenAuthenticationHandler.cs` (role claim, line 60), the auth-scheme wiring in `Extensions/ServiceCollectionExtensions.cs`, all mutating controllers, the My-Account token UI copy.

**Acceptance criteria.** Integration tests: a read-only token is 403 on create-playlist / watchlist-write / preferences-write; an admin-minted read-only token is 403 on an admin endpoint; **a token carrying the `admin` scope still reaches `[Authorize(Roles="Admin")]` endpoints** (or, if the scope is removed instead, it no longer exists anywhere — UI included). Existing full-session flows unaffected. UI scope descriptions match enforced reality.

**Effort.** M.
**Dependencies.** None. Builds on WS-6 media-token work.
**Risks.** Over-tightening could 403 a legitimate first-party call that (incorrectly) runs on a token — audit callers first.

**Implementation status (2026-07-16).** ✅ **Part A (the severe hole) landed:** `ApiTokenAuthenticationHandler` now emits `ClaimTypes.Role` only when the token carries the `admin` scope, so a non-admin-scoped token — even one minted by an admin — can no longer satisfy `[Authorize(Roles="Admin")]` (regression test `AdminMintedReadOnlyToken_Is403_OnAdminEndpoint`; the shipped `admin` scope still reaches admin endpoints, preserved by `Admin_CanMintAdminScope_AndReachAdminEndpoint`). ✅ **Part B (user-write gating) landed:** `[Authorize(Policy = WriteState)]` on every mutating endpoint of `PlaylistsController` (6), `UserPreferencesController` (1), `BookController` bookmark/highlight writes (6), and — after the diff review (below) — `WebhooksController` Create/Delete/Test (3). JWT sessions are unaffected; a read-only token is now 403 (`ReadOnlyToken_Is403_OnPlaylistCreate`, `ReadOnlyToken_Is403_OnWebhookCreate`).

✅ **Part C — token-administration lockdown (added after the diff review found a CRITICAL escalation the original scope missed):** the adversarial review flagged that leaving `AccountController` ungated made Part A/B *illusory* — a read-only token could POST `/api/v1/account/api-tokens` and mint itself a `write:state` (or, for an admin owner, `admin`) token. Fix: a new **`ScopePolicies.FullSession`** policy (requires a JWT/cookie session, rejects any principal on the `ApiToken` scheme) now gates all 8 sensitive `AccountController` mutations — token mint/revoke, account delete, TOTP enroll/confirm/disable, trusted-device revoke. An API token can no longer mint tokens, self-destruct the account, or manipulate 2FA (regression test `ApiToken_CannotMintAnotherToken`). GET endpoints (token list, TOTP status) stay token-readable.

⏳ **Deferred:** the `write:library` (or `trigger:scan`) scope rides with **R-WI-019**. Open question §7 Q2 (scope granularity) still stands but no longer blocks security — token admin is JWT-only regardless of the granularity decision. Server suite green (877/1-skip/0).

---

### R-WI-007 — Register/refresh watchers on library create & edit 🔴 P0 *(closes D-1)*

**Motivation.** Libraries added or path-edited after boot get no real-time watching until restart; edits also leak stale watchers.

**Specification.** Expose a public `RefreshWatchersAsync()` (or `RebuildWatchers`) on the watcher and call it from `LibraryService.CreateLibraryAsync` and `UpdateLibraryAsync`. A full teardown-and-rebuild of all watchers is acceptable at home scale and also fixes the stale-watcher leak on path edits. Two edge cases the code makes real: **(a)** on refresh, purge `_pendingFiles` entries whose path no longer falls under any current library path (today only library *delete* purges them, `LibraryWatcher.cs:89-98` — otherwise files pending under a removed path still get single-file-scanned in); **(b)** when `EnableFileWatcher=false` at boot, `ExecuteAsync` returns and the processing loop is dead (`:128-134`) — `RefreshWatchersAsync` must no-op in that state (registering watchers with no loop would silently black-hole events into `_pendingFiles`); note "restart required to re-enable file watching".

**Files affected.** `Services/Scanning/LibraryWatcher.cs` (public refresh method), `Services/Media/LibraryService.cs`.

**Acceptance criteria.** Integration test: create a library at runtime, drop a file into it, and the watcher enqueues it without restart. Edit a library's paths and confirm no watcher remains on a removed path **and no pending file under a removed path is processed**. `EnableFileWatcher=false` still disables everything (refresh no-ops).

**Effort.** S.
**Dependencies.** None. Pairs with R-WI-008 (schedule as backstop).
**Risks.** Watcher rebuild must be thread-safe against the processing loop; guard shared `_watchers` access.

**Implementation status (2026-07-16).** ✅ Landed. `LibraryWatcher.RefreshWatchersAsync()` (public virtual, serialised by a `SemaphoreSlim`) tears down and rebuilds all watchers and prunes `_pendingFiles` entries no longer under any library root (new `IsPathUnderRoot` helper, trailing-separator-safe so a prefix sibling like `Media2` isn't treated as inside `Media`). Guarded by a new `_isRunning` flag, so the method is a safe no-op when `EnableFileWatcher` was off at boot (and in unit tests that never start the host). Also closed a latent race by moving `_watchers.Add` inside the `_libraryWatchers` lock. `LibraryService` calls it best-effort via `RefreshWatchersSafeAsync` (try/catch — a transient DB error during refresh must not 500 an already-persisted create/edit; diff-review MEDIUM). Startup-window race closed by routing the initial registration through the locked refresh path with `_isRunning` set first (diff-review LOW). Tests: `LibraryWatcherRefreshTests` (5).

**Known follow-ups from the diff review (LOW, not blocking):** (a) a full refresh briefly tears down watchers for *unchanged* libraries too, leaving a sub-second blind window — mitigate later by refreshing only the affected library or enqueuing a reconciliation scan; (b) `RemoveWatchersForLibrary` (delete path) doesn't take `_refreshLock`, so a delete racing a concurrent create/edit could resurrect/leak a watcher — narrow interleaving, resource leak only (no bad scan, guarded by the `FindAsync` check); (c) the real teardown/rebuild body is currently wiring-tested (mock verifies the call) — the risky decision logic (`IsPathUnderRoot`) is unit-tested, but a real running-loop behaviour test is a follow-up (needs host start; inherently integration/flaky).

---

### R-WI-008 — Scheduled periodic library scans 🟠 P1 *(closes a §3 library gap)*

**Motivation.** The only file-discovery triggers are the realtime watcher and manual scans; watchers miss events on network/removable drives and there is no backstop.

**Specification.** A background service that enqueues a full library scan on a configurable interval (setting `LibraryScanIntervalHours`, `0 = off`). Reuse `LibraryScanQueueService` (it already dedups queued/running scans per library and serialises jobs, so overlap handling is free); register the task in the scheduled-tasks registry so it appears on the P1-WI-005 admin page. **Run-now note:** the tasks registry currently supports manual trigger only for Metadata Refresh — this item either generalises that trigger mechanism (preferred; small) or ships a task-specific trigger; state which in the PR. Distinct from `MetadataRefreshService` (which re-enriches existing rows without rediscovering files).

**Files affected.** new `Services/Background/ScheduledScanService.cs`, `Services/Infrastructure/SettingsService.cs` (seed key), the scheduled-tasks registry, `SettingsPage.tsx` (interval field).

**Acceptance criteria.** With the interval set, a new file appears in the library after the interval without a manual trigger (integration test with a short interval). The task lists on the admin tasks page and supports run-now. `0` disables it.

**Effort.** S–M.
**Dependencies.** None. Complements R-WI-007.
**Risks.** Overlapping scans — the queue must coalesce/skip if a scan is already running.

---

### R-WI-009 — Admin write surface for per-user bitrate cap 🟠 P1 *(closes D-6)*

**Motivation.** `MaxStreamBitrateKbps` is enforced but settable only by DB edit.

**Specification.** Add the field to the admin user-edit DTO/endpoint and the admin user UI (a labeled kbps input, `0 = unlimited`). *Decision point (maintainer, §7 Q3):* recommended **admin-only for v1**; a self-service toggle can follow.

**Files affected.** `Controllers/UsersController.cs` (or `AdminController` user-update), the user-update DTO, admin user-edit component in `src/components/admin/`.

**Acceptance criteria.** An admin sets a cap via UI; a subsequent transcode for that user honours it (integration test); `0` = unlimited. Non-admins cannot set it.

**Effort.** S.
**Dependencies.** None (enforcement already exists).
**Risks.** None notable.

**Implementation status (2026-07-16).** ✅ Landed + **live-verified**. New `PUT /api/v1/users/{id}/streaming` (admin-only — the whole `UsersController` is `[Authorize(Roles="Admin")]`) validates `>= 0` (400 otherwise) and clamps the upper bound to 100 000 kbps; `UserDto` gains `MaxStreamBitrateKbps` (exposed at all 3 construction sites; record default dropped so future sites must supply it — diff-review LOW). Client: `StreamingModal` (mirrors `RatingsModal`) opened from a `UserListTable` row ("Edit Streaming Limit", showing the current cap), `userService.updateUserStreaming`, invalidates `['users']`. **Live-verified end-to-end:** admin PUT sets cap=3000 → GET DTO reports 3000 → the plan clamps ("Bitrate limited to 3000 kbps by user policy") → the transcode emits `-maxrate 3000k` → reset to 0 clears it. Tests: `UsersControllerStreamingTests` (6: set/zero/negative-400/404/clamp/GET-exposes) + `StreamingModal.test.tsx` (4). Suite 912 server / +4 client. **Decision (§7 Q3):** admin-only for v1, as recommended. **Known cosmetic (out of scope):** the modal/link reuse the dead Tailwind `primary` utilities (project-wide — `--color-*` live in `:root`, not `@theme`); consistent with all existing modals, focus ring still visible; fix is a repo-wide `@theme` change.

---

### R-WI-010 — Surface & seed the DLNA settings group 🟠 P1 *(closes D-9)*

**Motivation.** A shipped, security-sensitive feature (P4-004) is unconfigurable from the app; `DlnaMaxContentRatings` isn't even seeded, and the `DlnaExposedLibraries` allowlist the enable-toggle references has no UI.

**Specification.** A DLNA settings card (admin, `[Server]` tree): `EnableDlna` toggle, `DlnaServerName`, a library allowlist (`DlnaExposedLibraries`) as a checklist, and `DlnaMaxContentRatings` as **per-media-type rating dropdowns (Movie and TV at minimum)** — the stored value is the per-type JSON shape `{"Movie":"PG-13","TV":"TV-PG"}` consumed via `UserRatingCeilings.From` (`DlnaAccess.cs:17-24`), **not** a bare rating string; `ParseContentRatings` silently fails open (empty dict = no ceiling, `UserRatingCeilings.cs:65-80`) on malformed values, so the UI must serialize the exact shape and a round-trip test must assert the stored value parses. **Seed `DlnaMaxContentRatings` as `""`** (= no cap — preserves current semantics for fresh installs), with the UI making the "no ceiling" state visually loud next to the exposure checklist. Because DLNA is unauthenticated, default the allowlist to empty (expose nothing) and show the LAN-only/no-per-user-ACL caveat inline. Note: `EnableDlna` takes effect on restart (its own seeded description says so, `SettingsService.cs:115`) — show the restart-required note; allowlist/name/ratings are read live per-request.

**Files affected.** `Services/Infrastructure/SettingsService.cs` (seed the missing key), `SettingsPage.tsx` + a new settings card, settings service/types on the client.

**Acceptance criteria.** An admin can configure everything from the UI; allowlist/name/ratings round-trip and take effect **live**; enabling DLNA takes effect **after restart** with the UI saying so. The stored `DlnaMaxContentRatings` value parses via `ParseContentRatings` (round-trip test — guards the silent fail-open). Seeded as `""` on a fresh DB. Security caveat visible.

**Effort.** S.
**Dependencies.** None.
**Risks.** Must not expose libraries by default — verify the empty-allowlist default holds end-to-end.

---

### R-WI-011 — Explicit, visible content-rating choice at user creation 🟠 P1 *(closes D-8)*

**Motivation.** New non-admin users silently default to a PG-13 movie ceiling; titles then 404 with no explanation.

**Specification.** Add a content-rating selector to the admin **create-user** flow, defaulting per the maintainer decision below, with kid-friendly presets one click away. Surface the active ceiling on the user's own account page ("Content limit: PG-13 — set by your administrator"). **Scope note (v1):** the invite flow carries no per-user fields today (`Invite` holds only code/creator/timestamps, `InvitesController.cs:24-57`; `SignupRequest` has no rating field) — extending invites to carry an admin-chosen ceiling needs an `Invite` column + EF migration + threading through the signup acceptance path. v1 scopes to admin create-user + account-page display; invited/self-signup users get the default until edited. If the maintainer wants invite-time ceilings, add the `Invite` migration explicitly and re-cost to M. *Decision point (maintainer, §7 Q2):* default Unrestricted (recommended) vs. keep PG-13 default but make it visible — either is acceptable; an invisible default is not.

**Files affected.** create-user DTOs and endpoints (`UsersController`/`AdminController`), admin user components, `MyAccountPage.tsx` (display), possibly the `User.MaxRating` default. (Invite-time variant additionally: `Models/Invite.cs` + migration + `AuthController` signup path.)

**Acceptance criteria.** Creating a user shows and records the chosen ceiling; the account page displays the current limit; the chosen default matches the maintainer decision. Existing users unchanged unless edited.

**Effort.** S.
**Dependencies.** None. Related to R-WI-009 (same admin user-edit surface).
**Risks.** Changing the model default affects only newly created users; document it.

---

### R-WI-012 — Robust subtitle-path handling 🟠 P1 *(closes D-10)*

**Motivation.** Apostrophe-titled media never gets burned-in subtitles (a logged workaround disables it), and the underlying escape code is incorrect for ffmpeg's two-level filter quoting.

**Specification.** Stop interpolating user file paths into the `subtitles=` filter. Extract the subtitle track to a **fixed-name file inside the session directory** (e.g. `burnin.ass`) via a new `FFmpegService` helper — reuse the `ExtractSubtitleToVttAsync` pattern already invoked from `TranscodeService.cs:453-476`, but extract to `.ass`/`.srt` (WebVTT loses ASS styling under libass burn-in). Reference it in the filter as a **relative filename**: `ProcessStartInfo.WorkingDirectory` is already the session dir (`TranscodeProfileBuilder.cs:408`), so `subtitles=burnin.ass` sidesteps path quoting entirely — note the temp *root* itself can contain apostrophes/colons (`Directory.GetCurrentDirectory()`-derived, `TranscodeService.cs:54`), so an absolute temp path would reintroduce the bug. Then remove both the apostrophe guard (`TranscodeProfileBuilder.cs:98-104`) and the broken escape (`:313`); the builder takes the extracted filename as a new parameter. Add a regression test with an apostrophe (and a bracket/space) in the path.

**Files affected.** `Services/Transcoding/TranscodeProfileBuilder.cs`, `Services/Transcoding/FFmpegService.cs` (extraction helper), `Services/Transcoding/TranscodeService.cs` (pre-pass before `StartFFmpegProcessAsync`), `Services/Transcoding/*` tests.

**Acceptance criteria.** A media file whose path contains `'` produces a valid filter chain and burns subtitles (arg test + live check on an apostrophe-named clip). The old guard no longer trips. Extracted files are cleaned up with the session directory.

**Effort.** S–M (the extraction pre-pass adds startup latency on large files — worth a log line).
**Dependencies.** None. Same-file sequencing note as R-WI-003 (serialize against master-plan P3a/P3b; keep the deinterlace-before-burn filter ordering).
**Risks.** Temp-file lifecycle — the session directory (and janitor) already owns cleanup; ensure the extracted sub outlives the transcode.

---

### R-WI-013 — Play-count & per-play history 🟠 P1 *(closes D-7)*

**Motivation.** `PlayCount`/`MediaItem.LastPlayed` are dead columns; no play history exists for audio/video, blocking watch-history, "most played," and better recommendations.

**Specification.** **Recording mechanism (committed, one path):** record plays inside the existing progress-beat flow — `UserMediaInteractionService.UpdateProgressAsync`, which the video player already feeds every 10s (`VideoPlayer.tsx:354-367`) for **both** direct-play and transcoded playback. Past a threshold (maintainer decision, §7 Q5), open a per-user/per-item history row; dedup with a recency window keyed on user+item (a fresh row only if the last one is older than the window or was completed). Do **not** hook `StreamController` for play counting — it serves every range request with no session concept and never sees transcoded plays; direct-play liveness tracking there belongs to R-WI-016, not to counting. **Guards:** restrict recording to Movie/Episode/Audio types (the book reader posts the same progress endpoint per page-turn, `bookService.ts:62`, and books already have `ReadingSession`), ignore `position <= 0` beats (the player posts a position-0 reset to the *next* episode before navigating, `VideoPlayer.tsx:991`). **Music:** the audio player currently emits *no* progress signal (`PersistentPlayer.tsx` keeps progress in local state and never posts `/interaction/{id}/progress`) — add track progress/start-end reporting to it, or music history stays empty and D-7's music half stays open. Maintain a derived count; either back `PlayCount` with it or drop the dead columns (§7 Q5). Expose a minimal `GET` for a future history page (R-WI-020 / watch-history consume it).

**Files affected.** new `Models/PlaybackSession.cs` (or similar) + migration, `Services/Media/UserMediaInteractionService.cs`, `InteractionController`, client `src/components/player/PersistentPlayer.tsx` (audio progress reporting), a read endpoint.

**Acceptance criteria.** Playing an item creates exactly one history row past the threshold (not on scrub/abort); count increments; a brief click does not; **a book page-turn and the next-episode position-0 reset create no rows** (regression tests); a music track play creates a row. Migration is additive.

**Effort.** M.
**Dependencies.** None. Feeds R-WI-020 and a watch-history page.
**Risks.** Double-counting on resume/seek — the recency-window dedup must be tested against pause/resume and far-seek flows.

---

### R-WI-014 — Local artwork sidecars for movies/TV 🟠 P1 *(closes a §3 library gap)*

**Motivation.** Nothing in movie/TV scanning reads local images; users with `poster.jpg`/`fanart.jpg`/`folder.jpg` next to their media (a Kodi/Plex convention) get none of it — art comes only from remote providers or NFO URLs.

**Specification.** In the movie/TV scan + metadata resolution, look for conventional local image files (`poster.jpg|png`, `folder.jpg`, `fanart.jpg`/`backdrop.jpg`, `<basename>-poster.*`) beside the media/in the series folder (respect `MetadataLocked`). Extend the NFO reader to honour local `<thumb>`/poster file paths, not just http(s) URLs. **Enrichment interaction (must-decide):** the default `Relaxed` policy declares any item with a poster *complete* (`MetadataEnrichmentPolicy.cs:48-68`; `MovieScanner.cs:88` gates on it) — applying a local poster at scan time would therefore permanently block remote description/cast/genre enrichment. Exclude locally-sourced art from the completeness signal (e.g. a distinct local-art field or flag `NeedsEnrichment` ignores), so a `poster.jpg` movie still gets one enrichment pass; add a test asserting exactly that.

**Files affected.** `Services/Scanning/MovieScanner.cs`, `Services/Scanning/TvScanner.cs`, `Services/Metadata/*` image resolution, `Services/Metadata/Nfo/NfoXmlParser.cs`, `ImageController`/path-jail as needed.

**Acceptance criteria.** A movie folder with `poster.jpg` shows that poster without any network call (integration test on a fixture); **the same movie still receives a description from the remote provider** (local art must not satisfy the enrichment-completeness check); locked metadata is not overwritten. Path access stays jailed.

**Effort.** S–M.
**Dependencies.** None.
**Risks.** Path-safety when serving local images — reuse the existing jail; never serve arbitrary sibling files.

---

### R-WI-015 — Media Session API 🟠 P1 *(closes a §3 playback gap)*

**Motivation.** No lock-screen / hardware-media-key control for video or music; a cheap, high-daily-value polish item.

**Specification.** In the video and audio players, populate `navigator.mediaSession.metadata` (title/artist/artwork) and register `setActionHandler` for play/pause/seek/next/previous, wired to existing player controls (and the next-episode/queue logic where present). Progressive enhancement — feature-detect and no-op where unsupported. Two wiring rules: the shared `useMediaSession` hook owns **arbitration** between the persistent audio player and the video player (both can be alive in one tab — one owner at a time); and for HLS video, position state and the `seekto` handler must route through the existing **offset-aware** seek logic (`currentTime` is not the real position after a far seek — client `seekOffset`, `VideoPlayer.tsx:668-675` — so use `handleSeekToTime`, not raw element seeking).

**Files affected.** client `src/components/player/VideoPlayer.tsx`, the audio player hook/components, possibly a shared `useMediaSession` hook.

**Acceptance criteria.** OS media controls show correct metadata and control playback for both video and music; absence of the API doesn't error. Client test for the hook.

**Effort.** S.
**Dependencies.** Artwork available (exists). Nice-to-have alongside R-WI-013 for "next".
**Risks.** None notable.

---

### R-WI-016 — "Now Playing" active-sessions dashboard 🟡 P2 *(closes a §3 admin gap)*

**Motivation.** No admin view of who is watching what; the transcode registry already holds the data.

**Specification.** `GET /api/v1/admin/sessions` enumerating active sessions from `TranscodeSessionManager.GetAllSessions()` (user, title, method, codec, resolution, bitrate, progress, state) + a terminate endpoint. **Terminate is scoped to transcode sessions in v1** (kill the ffmpeg process/session **and release the per-user transcode-cap accounting** — otherwise cap slots leak); direct-play sessions appear **read-only** (stopping a direct play means aborting an in-flight response *and* denylisting re-requests from a client holding a still-valid ~120-min media token — undesigned here, explicit non-goal for v1). **Direct-play tracking design:** a per-request "touch" does not work — `PhysicalFile` with range processing typically means one long-lived request per play (a 2-hour view can be a single request; a fully buffered file makes none) — so track by **response lifetime** (register on `HttpContext.RequestAborted`/response-completed around the `PhysicalFile` result in `StreamController`) and/or use the 10s interaction progress beats as the liveness signal, with an idle-expiry window. Note music direct play has no heartbeat until R-WI-013's audio-player change lands. Admin dashboard card with live polling (reuse the P1-WI-005 15s polling pattern).

**Files affected.** `Controllers/AdminController.cs` (or new `SessionsController`), `Services/Transcoding/TranscodeSessionManager.cs`, `Controllers/StreamController.cs` (response-lifetime registry), admin dashboard component.

**Acceptance criteria.** Starting a transcode and a direct play both appear in the dashboard; **terminating a transcode session** stops the stream and frees its cap slot; direct-play entries are visible read-only and expire on idle; the list clears on end. Admin-only. Integration tests on the endpoints.

**Effort.** M.
**Dependencies.** Benefits from R-WI-013 patterns but independent.
**Risks.** Response-lifetime tracking must not add latency to the hot streaming path; verify the registration is O(1) per request.

---

### R-WI-017 — Multi-field library search 🟡 P2 *(closes a §3 discovery gap)*

**Motivation.** Search matches Title only; cast/genre/description/artist queries return nothing, and tracks/episodes are unsearchable. **Additionally, review of this item uncovered a live defect (report D-12): global search applies the per-user library ACL but NOT the content-rating ceiling** (`MediaController.cs:176-185` uses `ApplyLibraryAccessFilter` only, where every other browse path also applies `ApplyContentRatingFilter`) — a rating-restricted account can surface blocked titles by searching their names, and a multi-field expansion would widen that leak to descriptions and cast.

**Specification.** **First, close D-12** (independent of the enhancement and cherry-pickable ahead of it): add `ApplyContentRatingFilter` to global search alongside the library ACL. Then extend the search query (`MediaController` global + `LibraryRepository` per-library) to match title, original title, cast/person names, genres, album/artist, and description, with sensible ranking (title-prefix first). Include tracks/episodes (revisit `excludedTypes`). **Query mechanism: LIKE-over-joins for v1** — extend the existing `EF.Functions.Like`/`Contains` style with `.Any()` predicates over the normalized Person/Genre tables; adequate at home-server scale, and title-prefix ranking is a trivial `CASE`. SQLite **FTS5 is explicitly a follow-up, not this item**: EF Core has no native FTS support, so it means a hand-written raw-SQL migration, trigger- or app-side index sync across MediaItems/Person/Genre, and `FromSqlRaw` querying — a mini-project to be specced on its own if LIKE proves too slow.

**Files affected.** `Controllers/MediaController.cs`, `Services/Infrastructure/LibraryRepository.cs`.

**Acceptance criteria.** **A rating-blocked title is absent from results for every matched field (D-12 regression test).** Searching an actor, genre, or track title returns the expected items (integration tests per field); ranking puts title matches first; performance acceptable on a large fixture.

**Effort.** M (the D-12 fix alone is S and may ship early).
**Dependencies.** None. Normalized Person/Genre tables already exist.
**Risks.** Parameterised queries only (charter §5). Measure LIKE performance on a large fixture before considering FTS.

---

### R-WI-018 — Subtitle appearance + timing-offset settings 🟡 P2 *(closes a §3 playback gap)*

**Motivation.** Only subtitle-language and burn-in settings exist (`ClientSettings.tsx:56,216`); users can't adjust caption size/color/background or fix out-of-sync subs.

**Specification.** Client subtitle-appearance controls (font size, color, background opacity, edge style) and an in-player timing-offset control (± seconds). **Scope — where text tracks actually exist:** the sidecar-VTT `<track>` element is created only in the HLS setup path (`VideoPlayer.tsx:798-816`), i.e. **remux and transcode-with-text-subtitles**; burned-in output (bitmap subs, or the burn-always preference) is out of scope; **direct play has no subtitle rendering at all today** (`CanDirectPlay` ignores subtitles; the native path just sets `video.src`) — building direct-play subtitle delivery is a separate prerequisite, explicitly not this item. Two design decisions up front: (a) style via the `::cue` pseudo-element (supports size/color/background; limited edge/positioning control) or commit to a custom cue renderer — pick one; (b) the offset must be expressed relative to the **effective** playback position (`currentTime + seekOffset`, `VideoPlayer.tsx:668-675`) so it composes with far-seek timeline resets. Persist via the existing client preferences.

**Files affected.** client player components, `ClientSettings.tsx`, preferences store.

**Acceptance criteria.** Appearance changes reflect live on rendered captions (HLS path); offset shifts sync **and still holds after a far seek** (regression test); settings persist across reloads. Client tests.

**Effort.** M.
**Dependencies.** None.
**Risks.** `::cue` styling limits — if full control is required, the custom-renderer path raises effort; decide before starting.

---

### R-WI-019 — Inbound scan-trigger webhook for *arr 🟡 P2 *(closes a §3 integrations gap)*

**Motivation.** Sonarr/Radarr import a file and SoftMedia won't notice until a scan; there's no inbound trigger to close the loop (pairs with R-WI-008).

**Specification.** A token-authenticated `POST /api/v1/scan` gated by the **`write:library` (or `trigger:scan`) scope policy defined in R-WI-006** — not `Roles=Admin` (today's scan endpoint is admin-role-gated, `LibrariesController.cs:94-95`, which R-WI-006 makes token-unreachable by design; requiring the `admin` scope instead would put a full-admin credential in Sonarr/Radarr config). The endpoint accepts an optional path, **maps it to its owning library, and enqueues a full scan of that library** via `LibraryScanQueueService.EnqueueScan` (the queue has no path-targeted job type — it dedups and serialises whole-library scans, which *arr import volumes tolerate; a path-scoped job type is an optional follow-up, not this item). Authorization: authenticated + posted path validated against configured library roots. Document the Sonarr/Radarr "Connect → Webhook" setup.

**Files affected.** new endpoint (`LibrariesController` or a `ScanController`), `LibraryScanQueueService`, `docs/user-docs/`.

**Acceptance criteria.** An authenticated POST with a valid path enqueues a scan that catalogs a newly-imported file; unauthenticated/invalid-path is rejected. Integration tests.

**Effort.** S.
**Dependencies.** R-WI-006 (scope the token); R-WI-008 (shared queue).
**Risks.** Must be authenticated and path-jailed — no anonymous scan trigger.

---

### R-WI-020 — Personalized home rows 🟢 P3 *(closes a §3 discovery gap)*

**Motivation.** The home screen has no taste-based personalization beyond Continue Watching; `RecommendationService` already exists for post-play.

**Specification.** Extend `RecommendationService` to produce home rows ("Because you watched X", "Top picks", "More from this genre") from watch history (R-WI-013) + existing genre/collection data, ACL/rating-filtered at the join (mirror the Continue Watching row). Add rows to `HomePage`, self-suppressing when empty.

**Files affected.** `Services/Media/RecommendationService.cs` (+ interface), a home-rows endpoint, `src/pages/HomePage.tsx`.

**Acceptance criteria.** A user with history sees relevant rows respecting ACL/ratings; a new user sees none (no errors). Server tests for ordering/filtering.

**Effort.** M.
**Dependencies.** R-WI-013 (history) strongly recommended.
**Risks.** Recommendation quality is iterative — ship simple (genre/collection affinity) first.

---

## 5b. Considered, Not Selected for This Wave

The remaining report-§3 gaps were triaged, not forgotten (this note exists so future readers don't re-litigate — the same purpose the P4 register serves):

- **Parental PIN gate** — natural follow-on to R-WI-011 (same rating surface, same UI area); strong wave-2 candidate.
- **User-facing session/device list with sign-out** — builds directly on the session tracking R-WI-016 introduces; wave-2 candidate.
- **Watch-history page** — explicit follow-on consumer of R-WI-013's read endpoint (named here so it isn't orphaned); wave-2 candidate.
- **Encode-failure → software fallback** — playback reliability; wave-2 candidate, best done after the R-WI-002/003/004 builder rework settles.
- **Admin-observability cluster** (log viewer, restart button, server stats, About/version, folder picker, hardware-probe/test, VACUUM task, generic run-now beyond R-WI-008's note) — valued, none load-bearing; wave-2 backlog.
- **CI / release packaging** — blocked on the maintainer half of P1-WI-004 (no CI exists); not schedulable from this plan.
- **Family/identity cluster** (kid profiles, Quick Connect, avatars, access schedules, audit log, password recovery) — strategic scope; needs maintainer selection before speccing.
- **Strategic features** (Plex/Jellyfin importer, Subsonic/OpenSubsonic layer, OPDS feed, SyncPlay, audiobooks, podcasts, playback webhook events beyond R-WI-019) — each deserves its own plan document; a remediation wave is the wrong vehicle.
- **Long tail** (compilation/VA handling, instant mix, artist images, queue persistence, Chromecast audio, person pages, More-Like-This, genre chips, home-row customization, filter persistence, Ctrl+K, favorites page, missing-episode tracking, extras, NFO write-back, per-library settings, scan cancellation, delete-from-disk, duplicates report, webhook presets/outbox, readiness endpoint, OpenAPI artifact, notification center, systemd docs) — recorded in report §3; wave-2+ backlog.

## 6. Consolidated Task Checklist

### Phase A — P0: security & defects
- [~] R-WI-001 Purge secrets/debris; `.gitignore` — ✅ **untrack + gitignore landed (uncommitted)**; ⏳ rotate JWT key + admin password and history-purge maintainer-gated
- [x] R-WI-002 ✅ **landed + live-verified** — dedicated `sid`-keyed `StreamPlanStore` + resolver; far-seek restores 720p/2000k cap, injected bitrate ignored (4 store tests)
- [ ] R-WI-003 Real remux (`-c copy`) branch + arg tests — not started (R-WI-002 ✓; **next**)
- [x] R-WI-005 ✅ **landed + live-verified** — client keeps `burnSubtitles` on seek; server resolver authoritative; **also fixed a pre-existing far-seek 500** (`SessionLock.Dispose` ObjectDisposedException) (2 tests)
- [~] R-WI-006 Scope enforcement — ✅ **core landed** (admin-scope-conditional role claim + WriteState on Playlists/UserPreferences/Book writes); ⏳ Account/Webhooks gating + `write:library` scope deferred
- [x] R-WI-007 `RefreshWatchersAsync()` called on library create/edit — ✅ **landed** (5 tests)

### Phase B — P1: complete half-built features + high-leverage gaps
- [x] R-WI-004 Surround-preserving audio (copy → plan channels → stereo fallback) ✅ 2026-07-16 (see §8)
- [x] R-WI-008 Scheduled periodic scans (`LibraryScanIntervalHours`) on the tasks page ✅ 2026-07-16 (trigger mechanism **generalised** via `IManuallyTriggerableTask`, per the spec's preferred option)
- [x] R-WI-009 Admin per-user bitrate-cap field ✅ 2026-07-16 (see §8)
- [x] R-WI-010 DLNA settings card + seed `DlnaMaxContentRatings` ✅ 2026-07-16
- [x] R-WI-011 Visible content-rating choice at user creation ✅ 2026-07-17 *(maintainer decided: never restricted by default; admin sets ceilings manually)*
- [x] R-WI-012 Subtitle temp-file extraction; drop the apostrophe guard + broken escape ✅ 2026-07-17
- [x] R-WI-013 Play-count + per-play history table ✅ 2026-07-17 (audio-player beats added; §7 Q5 threshold defaults decided)
- [x] R-WI-014 Local artwork sidecars for movies/TV + NFO local-path support ✅ 2026-07-17
- [x] R-WI-015 Media Session API for video + music ✅ 2026-07-17 (shared `useMediaSession` arbitration hook; offset-aware `seekto`; **Phase B complete**)

### Phase C — P2/P3: valued features
- [x] R-WI-016 Now-Playing admin dashboard + terminate (+ direct-play tracking) ✅ 2026-07-17 (see §9 Checkpoint 8)
- [x] R-WI-017 Multi-field library search (+ tracks/episodes) ✅ 2026-07-17 (incl. the **D-12** rating-ceiling fix; see §9 Checkpoint 9)
- [x] R-WI-018 Subtitle appearance + timing-offset settings ✅ 2026-07-18 (see §9 Checkpoint 10)
- [x] R-WI-019 Inbound *arr scan-trigger webhook ✅ 2026-07-18 (`write:library` scope; see §9 Checkpoint 10)
- [x] R-WI-020 Personalized home rows ✅ 2026-07-18 (see §9 Checkpoint 10)

## 7. Open Questions (maintainer sign-off)

1. **R-WI-006 scope granularity** (roadmap Q2): keep the scopes and enforce them, or collapse to one clearly-labeled full-access token? Either is charter-valid; unenforced-but-advertised is not. Note: the single-token option forecloses the least-privilege *arr key R-WI-019 needs.
2. **R-WI-011 default ceiling**: ✅ **DECIDED (maintainer, 2026-07-17): new users are NEVER content-rating restricted by default — the admin must manually set a ceiling per user.** Default = Unrestricted everywhere a user is created (admin create-user, invite signup, public signup); the create-user UI shows the rating selector defaulting to "No limit". Existing users keep their current values.
3. **R-WI-009 bitrate cap** (roadmap Q3): admin-only for v1 (recommended), or also a self-service user toggle?
4. **R-WI-001 history rewrite timing**: bundle with the ffmpeg-binary purge now, or defer until the public/private repo decision? Force-push invalidates clones.
5. **R-WI-013 play threshold**: ✅ **DECIDED (proposed default, implemented 2026-07-17; maintainer may retune the constants):** a play counts once the position first crosses **min(240s video / 60s audio, 50% of runtime)** — so short clips/songs still count, a quick peek at a long movie doesn't; **completion** reuses `MediaCompletionHelper` (explicit watched → credits marker → 95%); a **6h recency window** dedups (pause/resume is one play, a later return is a new one); a completed row followed by fresh watching counts as a rewatch. The dead `MediaItem.PlayCount`/`LastPlayed` columns are now **backed by the history** (incremented/stamped when a row opens). Constants live in `UserMediaInteractionService` (`VideoPlayThresholdSeconds`, `AudioPlayThresholdSeconds`, `PlaySessionWindow`).
6. **R-WI-002/005 × WS-6 sequencing**: land the wave-2 T6.1 query-token rejection before, alongside, or after the plan-store work? (This plan's tests are written with media tokens either way, but the client's cold-load/hub URLs still ride the access token today.)

## 8. Change Log

| Date | Change | Author |
|------|--------|--------|
| 2026-07-15 | Initial plan derived from `docs/reports/feature-gap-analysis-2026-07-15.md` (8-domain audit + 17-claim adversarial peer review). | Engineering review |
| 2026-07-15 | **Rev. 2 after 5-reviewer design review.** R-WI-002 storage design rewritten (dedicated `sid`-keyed plan store with lifetime independent of `TranscodeSession` teardown — the DELETE-on-seek/param-change restarts were fatal to the original in-registry design); R-WI-005 client patch respecified (merge `plan.url` params with live `sub`/`audio`/`burnSubtitles`); R-WI-003 fMP4 segment decision + mixed-case clause dropped; R-WI-006 role-claim rule corrected to admin-scope-conditional (the `admin` scope is a shipped feature) + `write:library` scope added for R-WI-019; R-WI-013 committed to the progress-beat mechanism + audio-player work added; R-WI-016 terminate scoped to transcode + response-lifetime tracking; R-WI-017 committed to LIKE-over-joins + **D-12** (search rating-filter bypass, discovered by this review) folded in; R-WI-018 scope corrected to the HLS text-track path; same-file sequencing notes vs master-plan P3a/P3b added; §5b triage section and open question 6 (WS-6 timing) added; ~15 further spec corrections. | Engineering review |
| 2026-07-16 | **Phase A implementation (checkpoint 1).** Landed R-WI-007 (watcher refresh, +5 tests), R-WI-006 core (admin-scope-conditional role claim + WriteState on Playlists/UserPreferences/Book writes, +2 tests), and R-WI-001 steps 1–2 (untrack 61 debris files + `.gitignore`, uncommitted). Server suite 875 pass / 1 skip / 0 fail (baseline 869). Playback foundation R-WI-002→005 intentionally deferred — see §9. | Engineering execution |
| 2026-07-16 | **Diff review + fixes.** 3-reviewer adversarial review of the checkpoint diff found: **CRITICAL** — token-mint endpoint (`AccountController`) was ungated, letting a read-only token escalate itself → fixed with new `FullSession` policy on all 8 sensitive account mutations (token admin is now JWT-only); **HIGH** — `WebhooksController` writes ungated (read-only token could register an exfil webhook) → fixed with `WriteState`; **MEDIUM** — watcher refresh could 500 a persisted create/edit on a transient DB error and left watchers torn down → fixed with best-effort `RefreshWatchersSafeAsync` + startup-window fix. +2 tests. Three LOW items documented as follow-ups (R-WI-007 §status). Server suite 877 pass / 1 skip / 0 fail. | Engineering execution |
| 2026-07-16 | **R-WI-002 + R-WI-005 landed + LIVE-verified** (operator provided admin login, enabling end-to-end verification against a real transcode of a synthetic clip). Added the dedicated `sid`-keyed `StreamPlanStore`; controller persists at `/plan`, resolves in `master.m3u8`; DTO carries resolved quality params; client far-seek URL keeps `burnSubtitles`. **Live verification uncovered + fixed a pre-existing bug:** every session-restarting far-seek 500'd (`ObjectDisposedException` in `SessionLock.Dispose`). Proven live: far-seek restores 720p + `-maxrate 2000k`; injected `bitrate=50000` ignored; far-seek returns 200. Server suite 883/1/0. | Engineering execution |
| 2026-07-16 | **Playback diff review + fixes.** 3-reviewer adversarial pass found the live test only covered the happy path. **HIGH:** per-user bitrate cap bypassable via a never-negotiated sid → `GetMasterPlaylist` now clamps against the user cap on every request (live-verified: fabricated sid + `bitrate=50000` → `-maxrate 3000k`). **MEDIUM:** unbounded `StreamPlanStore` growth + unvalidated sid → sid validation + hard 2048 cap. **MEDIUM:** the `SessionLock` swallow left the root cause (disposing in-use semaphores → orphaned waiter / lost mutual exclusion) → redesigned `TranscodeSessionManager` to never dispose in-use locks and prune only idle ones. +3 tests (net); 1 pre-existing test revised to the new lock contract. Server suite 886 pass / 1 skip / 0 fail. Next: R-WI-003 (remux). | Engineering execution |
| 2026-07-16 | **R-WI-009 (per-user bitrate-cap admin UI) landed + LIVE-verified + reviewed.** `PUT /users/{id}/streaming` (admin-only, validated/clamped) + `UserDto.MaxStreamBitrateKbps` + `StreamingModal`/`UserListTable` wiring. Live: admin sets 3000 → DTO shows 3000 → transcode `-maxrate 3000k` → reset clears. Removed the DB-edit workaround for cap testing. Review clean (1 LOW fixed: dropped the record default; 1 LOW documented: dead Tailwind `primary` classes, project-wide). +6 server / +4 client tests. Suite 912/1/0. | Engineering execution |
| 2026-07-16 | **R-WI-004 (surround audio) landed + LIVE-verified + reviewed (Phase B start).** Audio ladder — copy source (fMP4/TS-safe codecs) / encode AC3 5.1 / stereo AAC — threaded through the plan store to `BuildAudioArgs`, replacing forced stereo. Live: AC3 5.1→`-c:a copy`, FLAC 5.1→`-c:a ac3 -ac 6`, both playable. 2-reviewer pass (both found the same HIGH): **HIGH** `-c:a copy` with no `-map` copied the max-channel (possibly undecodable) alternate track → pinned `-map 0:a:0` (live-verified on a 2-track AC3+FLAC clip); **MEDIUM** copied audio uncapped → bounded encode when a cap applies; **LOW** selected-track wrong channels → neutral AAC. +11 tests. Server suite 906 pass / 1 skip / 0 fail. | Engineering execution |
| 2026-07-16 | **R-WI-003 (real remux) landed + LIVE-verified + reviewed → Phase A P0 complete.** `IsRemux` from the plan; `BuildRemuxArguments` (`-c copy` → fMP4); burn-in fallback; live-verified (H.264/AAC MKV → `-c copy`, valid init.mp4 + `.m4s`). 2-reviewer pass: **HIGH** remux bitrate-cap bypass (caught proactively) → `RemuxFitsBitrateCeiling` gate; **HIGH** fMP4-muxability regression (Vorbis would 503) → `RemuxVideoCodecs`/`RemuxAudioCodecs` restrict to fMP4-safe codecs; **LOW** plan-store expiry mid-playback → sliding TTL. +10 tests. 3 LOW follow-ups documented (throttle buffer, debug-panel HDR label, DirectPlay cap bypass). Server suite 896 pass / 1 skip / 0 fail. | Engineering execution |
| 2026-07-17 | **R-WI-016 (Now-Playing dashboard) landed + LIVE-verified + reviewed → Phase C started.** `ActiveStreamRegistry` (response-lifetime + beat-heartbeat direct-play tracking, 60s idle expiry, handle-based release), admin sessions endpoint + terminate (transcode-scoped, cap-freeing, audit-logged), dashboard card with confirm-gated Stop. Live verification found 3 listing gaps (finished-encode vanishing mid-play, preload as phantom "Playing", restart/cached-play invisibility) — fixed. 3-reviewer pass: 2 HIGH (dormant-session suppression + phantom Paused rows), ACL gate on beat-creation, prune-race handle fix, terminate-404 UX, error-state UX + 2 adjacent HIGH/MED bugs fixed (hero album Play-Now → broken player; search play → dead route). Album-card queue bug from Checkpoint 7 also fixed. +19 server/+12 client tests. Server 1020/1/0; client 221. | Engineering execution |
| 2026-07-17 | **R-WI-017 (multi-field search) landed + LIVE-verified + reviewed → D-12 CLOSED.** Rating ceiling now applied to global search (proven live with a G-ceiling account across title/cast/genre queries) + episode hits gated on the parent series passing the filter. Multi-field LIKE-over-joins (title/description/genre/cast/artist; tracks & episodes searchable), prefix-first ranking, LIKE metachars escaped + 100-char cap (review MED), duplicate-library-group bug fixed (found live). Client: tracks play from the dropdown, episode rows route to their series, authed thumbnails, artist/album/series context lines (review HIGH UX). Perf measured: ~430ms @ 25k items worst case → LIKE verdict holds, FTS5 stays follow-up. +10 server/+6 client tests. Server 1030/1/0; client 227. | Engineering execution |
| 2026-07-17 | **R-WI-015 (Media Session API) landed + LIVE-verified + reviewed → Phase B (P1) complete.** Shared `useMediaSession` ownership-arbitration hook (last-to-play wins, paused keeps, fallback on unmount, full clear when empty); both players wired; video `seekto`/position routed through the offset-aware seek logic per spec. 3-reviewer pass → 6 MED fixed (contentId re-claim, fastSeek restart storm, far-seek transient, double-offset raced seek + inflated window bound, resume-preserving episode next/prev from mount, `ratechange` sync) + LOW hardening. Pre-existing bug discovered + logged (album-card play → `/stream/{albumId}` 404 zombie player). +16 hook tests. Server 998/1/0; client 208. | Engineering execution |
| 2026-07-18 | **R-WI-018 + R-WI-019 + R-WI-020 landed + LIVE-verified + reviewed → Phase C & plan engineering scope COMPLETE.** Subtitle appearance (::cue) + per-cue-anchored sync nudge (live verification corrected the model: the server already stream-aligns the VTT — client applies the user offset only; no-store + cache-buster + fail-closed alignment). *arr webhook behind the new `write:library` scope (review HIGH: admin-only sessions + ACL-filtered branches/anti-probe; real nested *arr payloads parsed; Test no-op; one queued follow-up behind running scans; rate-limited). Personalized home rows (video-history genre affinity, visible-seeds-only, watched/seed-excluded, self-suppressing). Out-of-scope finds TRACKED per maintainer instruction in `post-phase-c-bug-backlog.md` (B-13…B-21 added). +25 server/+11 client tests. Server 1044/1/0; client 237. | Engineering execution |

## 9. Implementation Log & Checkpoint Notes

**Checkpoint 1 — 2026-07-16.** Phase A landed its three low-risk, fully unit-testable P0 items (R-WI-001 untrack/gitignore, R-WI-006 core, R-WI-007). The **playback foundation (R-WI-002 → 003/004/005) was deliberately not started in this pass**, for reasons that should hold for whoever picks it up:

1. **It is the highest-risk change in the plan and touches the core streaming hot path** (`StreamPlanService`, `TranscodeController`, `TranscodeService`, `TranscodeSessionManager`, `TranscodeProfileBuilder`, and the client `VideoPlayer`). The Rev.2 design review spent both of its *critical* findings on R-WI-002's lifecycle alone.
2. **Its acceptance criteria require live playback verification** that unit tests cannot substitute for — "seek to the end of a bitrate-capped transcode; the cap holds"; "a 5.1 source transcodes to 5.1"; "an HEVC-in-MKV remux plays via fMP4 on an HEVC-capable client." Shipping a subtle change to the streaming path on unit tests alone would violate the project's verify-end-to-end discipline. This needs a running server + a real client + real media (and ideally `/run` + browser driving), which was not available in the implementation environment.
3. **It is gated on open question §7 Q6** (WS-6 T6.1 query-token sequencing) — the plan-store tests must be written against media/cast tokens, and the client cold-load/hub URLs still ride the access token today; landing R-WI-002/005 without settling that risks colliding with the security follow-up.

**Recommended next unit of work:** R-WI-002 in isolation (the `sid`-keyed plan store + resolver, no consumer behaviour change), verified live, then R-WI-005 (far-seek) → R-WI-003 (remux) → R-WI-004 (surround), each with a live-playback check. Do them serialised against master-plan P3a/P3b per the same-file notes.

**Note on committing.** The 2026-07-16 changes (untracked debris, `.gitignore`, source + test edits) are staged/modified in the working tree but **not committed** — left for maintainer review, consistent with how the plan documents themselves were handled.

**Checkpoint 2 — 2026-07-16.** Phase B advanced through three items, each implemented → tested → **live-verified against a running server** → adversarially diff-reviewed → fixed:

1. **R-WI-004 (surround audio)** and **R-WI-009 (per-user bitrate-cap admin UI)** — landed earlier in the day; see the status line and task table.
2. **R-WI-010 (DLNA settings UI)** — seeded the previously-unseeded `DlnaMaxContentRatings` key (`SettingsService.cs`, default `""` = no cap; `InitializeDefaultsAsync` back-fills it on the next boot of an existing install) and added a self-contained admin **`DlnaSettingsCard`** (enable toggle + restart-required note, server name, exposed-library checklist filtered to Movie/TV/Music, and per-type rating dropdowns serialized to the `{"Movie":..,"TV":..}` JSON `UserRatingCeilings.From` consumes — avoiding the raw-JSON fail-open footgun). **Live-verified:** all four DLNA keys present after seeding; a full PUT→GET round-trip persists enable/name/exposed-CSV/ratings-JSON exactly as the card serializes; the cap is read live per DLNA browse (`DlnaContentDirectory.ApplyContentRatingFilter`); settings GET/PUT are admin-gated (401 unauthenticated, confirmed live).

   **Review finding addressed (state-management, medium).** The card saves and invalidates the shared `['settings']` query, which the settings page also reads. Because section navigation does **not** remount `SettingsPage`, the page's `useEffect` that blindly did `setLocalSettings(server)` on every refetch would silently revert an admin's *unsaved* edits in another settings group when the DLNA card saved. Fixed with a non-destructive **3-way merge** (`mergeSettingsPreservingEdits` in `settingsService.ts`): on refetch, a key adopts the server value only if it actually changed since the last snapshot; otherwise the local (possibly edited) value is kept. This also fixes the symmetric hazard where the page's top-level "Save Changes" could re-PUT a stale DLNA value. Covered by a focused unit test reproducing the exact reported scenario plus edge cases (`settingsService.merge.test.ts`, 5 tests) **and live-verified in a real browser**: edited "Allow User Signup" → Enabled (unsaved) on the Users section, navigated (client-side) to Admin, saved the DLNA card, returned — the unsaved Enabled edit survived (pre-fix it reverted to the server's Disabled). Also visually confirmed the card renders correctly after fixing dead Tailwind-v4 `bg-primary` classes to `bg-[#007AFF]` (the `--color-primary` var lives in `:root`, not `@theme`, so `bg-primary`/`border-primary` utilities generate nothing — computed switch/button background verified as `rgb(0,122,255)`). Server suite 914 pass / 1 skip / 0 fail; client 180 pass.

3. **R-WI-008 (scheduled periodic library scans)** — new `ScheduledScanService` (BackgroundService) drives `LibraryScanIntervalHours` (seeded `"0"` = off, Group Scanning): a poll-loop with 5-minute config granularity (interval changes take effect without a restart), anchored on the task's registry `LastRunUtc`, which `TaskStatusPersistenceService` already persists — so cadence survives reboots and an overdue or first-enabled schedule fires promptly. Enqueues a scan for **every** library via `LibraryScanQueueService` (dedup makes overlapping schedules coalesce). **Trigger mechanism generalised** (the spec's preferred option): new `IManuallyTriggerableTask` interface; `AdminController.TriggerTask` resolves the DI collection by task name instead of hardcoding metadata refresh; `MetadataRefreshService` and `ScheduledScanService` both implement it, so both get working "Run now" buttons with zero client changes. Client: "Scheduled Library Scans" interval field in the Libraries → Scanning section.

   **Live-verified end-to-end (acceptance criteria):** with `EnableFileWatcher=false` (boot-honoured) and a brand-new file on disk, enabling the interval from the UI made the scheduled scan fire **on its own** at the next check tick — all 5 libraries scanned, the new file discovered — no manual trigger, no restart. NextRun stamped exactly +interval; UI Run-now → 202 + re-anchored schedule; unknown task name → 400; setting 0 clears NextRun to "—" within a check period; deleting the fixture + rescanning purged the dead item.

   **Review findings addressed (adversarial diff review; the concurrency lens re-run separately after API-overload failures):**
   - *(medium)* A failed enqueue-all run stamped `LastRunUtc` (Report does so for failures too), silently deferring the backstop a **full interval** — surviving reboots via persistence (e.g. nightly backup locking SQLite at the 04:00 due moment ⇒ scan postponed a whole day, every collision). Fixed: `IsDue` treats a `Failed` last result as still due; the loop paces the retry to the check period; per-library try/catch so a partial failure still enqueues the remaining libraries.
   - *(medium)* Pre-existing check-then-act race in `LibraryScanQueueService`'s dedup became newly reachable (scheduler sweep vs admin Run-now double-enqueueing the same library ⇒ duplicate full scan + duplicate webhooks). Fixed at the root: `_enqueueLock` makes every dedup check+insert atomic (all three enqueue methods), covering all callers; regression test runs 64 parallel enqueues.
   - *(low)* Non-integer interval input (`"2.5"`, `""`) stored verbatim reads as 0/disabled on the server (`int.TryParse` fail-safe) while the UI suffix said "hours". Fixed: `isIntervalHoursEnabled` helper mirrors the server's parse semantics exactly (unit-tested; verified in-browser: `2.5`/``/`0` ⇒ "(Disabled)", `1`/`24` ⇒ "hours").
   - *(accepted, low)* A manual-trigger wake lost in the consumed-TCS window only staleness the dashboard's NextRun ≤5 min (scans enqueue synchronously first) — field made `volatile`, remainder documented in code. Registry `ScheduledTaskStatus` field writes are unsynchronised (pre-existing design shared by all tasks): transient, self-healing telemetry inconsistency only; not worth locking every task's hot path.

   Post-fix re-verification: persisted-anchor NextRun computed correctly across a real reboot with no spurious scan. Suites after all fixes: **server 925 / 1 skip / 0 fail; client 183 pass** (+13 server, +3 client tests for this item).

**Checkpoint 3 — 2026-07-17.** **R-WI-011 (visible content-rating choice)** landed under the maintainer's §7 Q2 decision: **new users are never content-rating restricted by default; the admin sets ceilings manually per user.**

- **Server:** `User.MaxRating` default `"PG-13"` → `""` (all creation paths — admin create, invite, public signup — now produce unrestricted users; no EF migration needed, the column has no baked SQL default). `CreateUserRequest` gained optional `ContentRatings`; new shared write path `UsersController.ApplyContentRatings` strips empty entries, validates type keys + labels against `RatingTables` (unknown labels fail OPEN downstream, so typos are rejected with 400 instead), stores canonical casing, and **syncs legacy `MaxRating` to the map's Movie entry**. That sync kills a live lie: the ratings modal's "None (Unrestricted)" previously left `MaxRating="PG-13"` in place, so the user stayed movie-capped while the admin saw "Unrestricted". New `GET /account/content-limits` returns the caller's EFFECTIVE ceilings (same code path enforcement uses).
- **Client:** Create-User modal gained a visible "Content limits" fieldset (Movie/TV/Game, default "No limit", with the no-restrictions-by-default note); the account page shows "Content limits: Movies: up to PG · … — Set by your administrator" (or "None — you have full access"), fetched fresh per visit.
- **Live-verified end-to-end:** create-without-ratings → `maxRating=""` + full library access (7/7 movies); create-with-Movie=PG → user sees 0/7 (nothing ≤PG exists; unrated titles blocked-by-design under a cap); admin picks "None (Unrestricted)" → `MaxRating` synced to `""` and the same user instantly sees 7/7 — the previously-broken path. Invalid label → 400. Modal + account-page visuals confirmed in-browser; test users deleted and admin session restored afterward.
- **Adversarial review (server + client lenses): no confirmed findings.** Adjacent improvements taken from reviewer notes: `queryClient.clear()` on logout (account-scoped query caches — `['contentLimits']`, API tokens, TOTP — could briefly show the previous user's data after an account switch; pre-existing, now fixed centrally via `lib/queryClient.ts`), `EC` added to both modals' game-rating lists (matches `RatingTables.Game`), label/id a11y pairing in RatingsModal, dead `bg-primary` classes fixed in both modals, and two stale "default PG-13" comments corrected.
- Suites: **server 938 / 1 skip / 0 fail; client 186 pass** (+13 server, +3 client for this item).

**Checkpoint 4 — 2026-07-17.** **R-WI-012 (robust subtitle-path handling)** landed. Text-subtitle burn-in no longer interpolates the media path into ffmpeg's `subtitles=` filter: the chosen track is pre-extracted to a fixed-name `burnin.ass` in the session directory (`SubtitleService.ExtractSubtitleToAssAsync`) and the filter references the bare relative name (`subtitles=burnin.ass:fontsdir=.` — WorkingDirectory is the session dir). The apostrophe guard (which silently skipped burn-in) and the broken two-level escape are gone. Extraction lives inside `TranscodeProfileBuilder` (deviation from the spec's "new parameter" suggestion — the builder already owns `ISubtitleService` and the codec probe, so no interface threading was needed).

**Adversarial review found 8 confirmed findings (3 root causes) — all fixed and re-verified:**
- *(HIGH ×4)* The extraction initially rode `ProcessRunner`'s hard 30s kill with "non-empty file = success": a killed run could burn a silently TRUNCATED subtitle file (subtitles vanish mid-movie), and large remuxes lost burn-in entirely. Fixed: new `IProcessRunner.RunProcessForExitCodeAsync(psi, timeout)`; success now requires **exit code 0 + non-empty output**, partial output is deleted, and the cap is a 10-minute hang backstop (the old inline filter scanned the same data unbounded inside ffmpeg, so parity is preserved).
- *(MEDIUM ×2)* The extracted `.ass` lost the container's embedded FONT attachments (typeset/anime subs rendered in fallback fonts, silently). Fixed: `DumpFontAttachmentsAsync` probes font-mime attachments and dumps them under **sanitized names** (never the file-supplied filename metadata, which could path-traverse) into the session dir; the filter appends `:fontsdir=.`.
- *(MEDIUM)* Re-extraction on every ffmpeg restart. Fixed: an existing non-empty `burnin.ass` in the session dir is reused (strict extraction semantics make existing = valid). Old code re-scanned per restart too, so worst case is parity.
- *(LOW)* Tests enshrined the fragile file-presence convention — rewritten to pin exit-code semantics (nonzero exit / timeout ⇒ false + partial deleted).

**Live-verified twice** (before and after the review fixes): a real transcode of `Don't Stop Clip (2024)/Don't Stop Clip (2024).mkv` (embedded SRT) with `burnSubtitles=true` produced segments whose extracted frame **visibly shows the burned subtitle** ("Apostrophes can't stop us now") — the exact input class the old code refused. `-vf "subtitles=burnin.ass:fontsdir=."` confirmed in args; old skip-warning absent; `burnin.ass` cleaned up with the session dir (dormant-retention janitor lifecycle, pre-existing design). Suites: **server 951 / 1 skip / 0 fail; client 186** (+13 server tests for this item).

**Checkpoint 5 — 2026-07-17.** **R-WI-013 (play counts & per-play history)** landed. New `PlaybackHistory` table (migration `20260717180657_AddPlaybackHistory`, auto-applied on boot — verified live) records one row per play of a Movie/Episode/Audio item, opened inside the existing progress-beat flow (`UserMediaInteractionService.UpdateProgressAsync`). The dead `MediaItem.PlayCount`/`LastPlayed` columns are now backed by it. §7 Q5 threshold decided (see §7): play = position crossing min(240s video / 60s audio, 50% runtime); completion via `MediaCompletionHelper`; 6h dedup window; guards for book page-turns and position-0 resets. **Music now reports listen beats** (`PersistentPlayer` previously posted nothing — added a throttled ~10s beat + a final beat credited at the real end/skip position, so D-7's music half closes). Self-scoped `GET /api/v1/interaction/history`.

**Adversarial review — the first run died mid-way on model credit exhaustion (5/6 agents errored); re-ran fully on the current model (14 agents, 0 errors). It caught issues the first partial run and my own tests missed:**
- *(HIGH — 4 lenses)* **Completion→reopen cascade:** once a row flipped `Completed`, the continue-guard's `!Completed` requirement sent every subsequent ~10s tail beat (the post-95%/credits stretch of one normal viewing) down the new-row path, spawning ~18 phantom completed plays + PlayCount inflation per movie. Fixed: a completed row within the window keeps absorbing tail/near-end beats; only a genuine restart (position dropped below `RewatchRestartFraction`=0.5 of the high-water mark — a rewatch from the top) opens a new play. Open rows still continue on any beat (no double-count from scrubbing). My original "rewatch" test used a below-threshold beat and never exercised the tail path — replaced with `ContinuedBeatsPastCompletion_StayOnePlay_NoPhantomRows` + a scrub-back-near-end test. **Re-verified live**: the exact 18-phantom scenario (watch-through with tail beats) now yields exactly ONE completed row.
- *(MEDIUM, from the first partial run)* **History leaked titles of now-inaccessible media** — the read applied no library-ACL or rating gate. Fixed by threading `ApplyLibraryAccessFilter` + `ApplyContentRatingFilter` through `GetHistoryAsync` (mirrors `WatchlistController` and R-WI-020's spec); +2 tests (revoked-library and rating-cap hiding).
- *(LOW/accepted — 4 lenses)* **Concurrent-beat read-then-add race** can open a duplicate row for the same (user,item) if two devices beat within one ~10s window. A unique index would wrongly reject legitimate multiple abandoned (non-completed) plays; beats are fire-and-forget so a SQLITE_BUSY is harmless; worst case is one cosmetic extra row. Documented in code and accepted.
- *(LOW, api-privacy note)* The history GET inherits the controller's `write:state` scope (all its GETs do — a deliberate, documented controller convention); browser sessions satisfy it. A method-level `read:state` would AND with the controller policy, not override it, so cleanly re-scoping would mean moving the endpoint to another controller — out of scope for this item. Left consistent with the controller.

Suites: **server 974 / 1 skip / 0 fail; client 189** (+25 server, +3 client tests for this item). Live test play-history rows cleaned from the dev DB afterward.

**Checkpoint 5b — 2026-07-17. R-WI-013 privacy follow-up (maintainer-decided).** Users own their history: a per-user **"Record my history" toggle** (`User.RecordPlaybackHistory`, default ON — the scaffolded migration's silent `defaultValue: false` for existing rows was caught and hand-corrected to `true`) and a **"Clear my history"** action, both on the account page (`HistoryPrivacyCard`, with an explicit inline confirm on clear). **Deliberately no anonymous-logging middle mode**: in a 2–5-person household it de-anonymizes trivially (false comfort), and the play-dedup mechanism needs the user key to function. Toggle OFF = the diary stops entirely (no rows, no PlayCount bumps); resume positions and watched flags are a separate system and keep working. Clear = the caller's rows are erased and `MediaItem.PlayCount`/`LastPlayed` are **recomputed from the survivors in one atomic SaveChanges**, preserving the PlayCount==rows invariant. Endpoints: GET/PUT `/account/history-preferences` + DELETE `/account/history` — **all three FullSession-only** (privacy settings can be neither read nor changed by API tokens of any scope).

**Adversarial review (3 lenses + verification, 8 confirmed → 4 root causes, all addressed):**
- *(MEDIUM ×3)* `MarkWatchedAsync`'s open-play completion hook ignored the toggle — the player's automatic end-of-episode "watched" POST stamped `Completed=true + LastBeatAt=now` onto the diary AFTER the user opted out mid-viewing. Fixed: the hook now honours the flag; with recording off the diary is fully frozen (regression test `MarkWatchedWhileHistoryOff_LeavesTheDiaryUntouched`).
- *(MEDIUM/LOW ×3)* `ClearHistoryAsync`'s two-`SaveChanges` shape could strand aggregates on a crash between them. Fixed: survivors are computed up front (excluding the caller's rows) so delete + recompute commit atomically in one SaveChanges. The residual concurrent-beat stale-write race is documented and accepted (cosmetic one-off drift, consistent with the already-accepted beat-vs-beat race).
- *(LOW)* `Set`/`Clear` logged user-linked lines at Information level — privacy-action residue in shipped logs contradicting the card's promise. Demoted to Debug (the file's own convention for per-user playback data).
- *(LOW)* The preferences GET rode bare `[Authorize]`, readable by an API token with any scope. Tightened to FullSession.

**Live-verified end-to-end** (before + after fixes): migration backfilled the existing admin to recording=ON; toggle OFF → threshold-crossing beats wrote nothing while resume kept updating; toggle ON → recorded; DELETE cleared with `{deleted:1}`; the account-page card round-tripped a real browser toggle click and gated clear behind the confirm step. Suites after fixes: **server 979 / 1 skip / 0 fail; client 192** (+9 server, +3 client for this follow-up). Dev-DB state reset (toggle ON, zero rows).

**Checkpoint 6 — 2026-07-17.** **R-WI-014 (local artwork sidecars)** landed. Movie/TV scans now discover `poster.jpg`/`folder.jpg`/`<stem>-poster.*` (+ `fanart`/`backdrop`) beside the media, and the NFO `<thumb>` accepts safe LOCAL relative paths. Local files are **cache-copied into wwwroot** under **source-distinct keys** (`…_poster_local` sidecars, `…_poster_nfo` NFO, provider `…_poster` untouched) — media folders are never served directly, and no source can shadow another. Local art **wins** over provider art; deleting the sidecar reverts (clears art + cached copy, forces re-enrichment). The spec's critical enrichment invariant holds both ways: a local-only poster doesn't satisfy Relaxed completeness until one enrichment pass stamps `MetadataHash` (poster.jpg movies still get descriptions — verified live: OMDb was queried despite the local poster) and satisfies it after (no re-enrichment loop).

**Hardest review cycle of the plan: 23-agent review → 20 confirmed findings (8 root causes) → full rework → 2-verifier fix-verification → 8 residual/new defects → all closed.** Highlights: the sweep clearing NFO-sourced art every scan (infinite clear→re-enrich cycle; fixed via source-distinct keys + owns-check), provider art shadowing the user's sidecar through the shared cache key + naive mtime check (fixed via distinct keys + exact source-stamp freshness ±2s tolerance for coarse-timestamp filesystems), **symlink exfiltration** into the public cache via sidecars or NFO thumbs (fixed: all local ingestion passes the streaming stack's symlink-resolving jail; the NFO jail anchors at the NFO's own folder carried on the MetadataResult, closing the symlinked-subdirectory self-jail), flat multi-movie/TV shared folders getting one bare poster applied to everything (dedicated-folder guards; trailers/samples don't count as sharing), router merge dropping NFO local thumbs, stale-art resurrection after sidecar removal, TvScanner sweep failure-isolation (+ ChangeTracker.Clear on poisoned saves, library-root skip, IsRetryExhausted honoured), delayed provider downloads overwriting local art, admin fix-match/manual-edit clearing the local-art claim, and NFO-claim release when the thumb reference disappears (heals DB-restores).

**Live-verified twice** (before and after the rework): a hand-made magenta `poster.jpg` served **byte-identical** from the cache (md5 match — no network could have produced it), enrichment still queried the provider, and sidecar removal reverted cleanly. Suites: **server 998 / 1 skip / 0 fail; client 192** (+19 server tests for this item across the cycles). Migration `AddLocalArtworkFlags` applies on boot.

**Checkpoint 7 — 2026-07-17.** **R-WI-015 (Media Session API) landed — Phase B COMPLETE.** New shared `src/hooks/useMediaSession.ts` owns the single `navigator.mediaSession` between the persistent audio player and the video player via a module-level ownership registry: **the last player to START PLAYING owns the OS controls**; switching to a *new* track/item while already playing re-claims (`contentId` dependency — an edge-triggered boolean alone missed it, review MED); a paused owner keeps the session (lock-screen resume); on owner unmount/`enabled:false` ownership falls back to the most recent prior claimant, and the last registrant leaving fully clears the session (`metadata=null`, `playbackState='none'`, all handlers unbound). Handlers bind through stable wrappers that read the latest render's closures at call time (VideoPlayer's `handleSeekToTime` is recreated per render); position reporting is drift-throttled (the UA extrapolates; we re-report only on >2s discontinuities, rate/duration/pause changes — and ANY movement while paused, where the UA doesn't extrapolate). Everything is feature-detected + per-call try/caught: no API, no errors.

Wiring: **PersistentPlayer** — track title/artist/album + authed album art, play/pause/queue-next/previous/±30s/seekto (new `seekToTime`, which `handleSeek` now delegates to). **VideoPlayer** — title + `Season X · Episode Y`, poster art (`attachAuthToApiUrl`, a no-op for `/cache/*`), play/pause, `skip(±10)` (same offset-safe relative skip as the keyboard), **`seekto` → `handleSeekToTime`** per the spec's offset-aware constraint, position = `currentTime + seekOffset` against the real `displayDuration`, and episode next/previous → `navigateEpisode` (resume-preserving) available the *whole* episode via the mount-fetched ids.

**3-reviewer adversarial pass (arbitration / browser-API / HLS+tests) → 6 MED + hardening, all fixed:** (1) new-track-while-playing never re-claimed → `contentId`; (2) OS scrubber-drag `fastSeek` intermediates each restarted the transcode → drop `fastSeek` events, honour only the settled seek; (3) far-seek restart left stale `currentTime` added onto the new offset → OS scrubber (and in-app clock) showed garbage for ~1-3s → `setCurrentTime(0)` in both restart branches (mirrors the initial-load guard); (4) the fallback element seek used the ABSOLUTE time (double-offset when a seek raced a pending restart whose duration was still NaN) → seek `targetInStream`, clamped; also fixed the pre-existing inflated beyond-window bound (`seekOffset + transcoded length`, not `+ currentTime` on top); (5) OS "next" was only bound during the last ~2% of an episode AND used `handlePlayNextFromStart` (wipes the next episode's resume + stamps watched) → both next/previous now bind to `navigateEpisode` from mount; (6) element `playbackRate` resets to 1× on every src swap while the speed state kept the old value (OS position sawtoothed at 2×; the speed menu lied too — pre-existing) → new `ratechange` listener syncs state. Plus: fatal-error zombie session (`enabled: !error`), instance-held throttle baseline reset on every ownership gain, paused sub-2s nudges now reported.

**Live-verified in the browser** (real-click gestures, `navigator.mediaSession` inspected at every step): music session populated (title/artist/authed artwork) → pause kept metadata with `'paused'` → starting a video **took over** the session → video end/unmount **fell back** to the still-playing music → closing the player **cleared** to `'none'`; post-fix smoke: video seek via the progress bar landed exactly on target (60% → 18.0s of 30s) with zero console errors. Verification DB state fully reset (history cleared via the app's own endpoint, interaction rows removed, play counts untouched/NULL). Suites: **server 998 / 1 skip / 0 fail; client 208 pass** (16 hook tests: arbitration, StrictMode, fallback dispatch, fastSeek, drift throttle, paused-nudge, clamps, no-API no-op).

**Discovered pre-existing bug (out of scope, logged here for triage):** the home-rail **album** cards' Play button pushes the *album* MediaItem straight into the audio queue (`MediaCard.handlePlay` → `playTrack(item)` for any `isAudio`), producing `GET /api/v1/stream/{albumId}` → **404** — the bar shows "playing" forever with no sound and a desynced store (the element error never syncs back). Playing from the album detail page works. Suggested fix: album-type cards should enqueue the album's tracks (as `Play All` does) instead of `playTrack(album)`. LOW-effort, user-facing; candidate first pick-up alongside Phase C.

**Checkpoint 8 — 2026-07-17.** **R-WI-016 (Now-Playing admin dashboard) landed — Phase C started.** Also folded in the album-card queue bug from Checkpoint 7 and two adjacent bugs the review discovered in the same non-streamable-media class.

**Server.** New `Services/Sessions/ActiveStreamRegistry` (singleton) tracks DIRECT plays (video direct play + all music) exactly as the spec's design note prescribed: **response lifetime** (`StreamController.GetStream` registers on response start/complete — one range response can be a whole movie; HEAD probes excluded) **plus the ~10s progress beats as heartbeat** (`InteractionController.UpdateProgress`), with a 60s idle-expiry. Beats **create-or-refresh** entries — creation is what makes fully browser-cached plays and post-server-restart plays visible (both found live) — gated on (a) no LIVE transcode session for that user+media (else every transcode double-lists as a phantom direct play) and (b) the media being a streamable type **within the caller's library ACL + rating ceiling** (beats accept arbitrary ids; unchecked creation let a user paint the dashboard with content they can't access — review MED). Response release is **handle-based** (the completion callback releases the exact entry-generation it incremented) so prune/evict races can never unbalance a live play's refcount (review MED). `GET /api/v1/admin/sessions` (admin-only) merges transcode sessions + direct plays with resolved titles/usernames, playhead estimates (clamped; transcode = seek offset + client-fetched segments × 6s), quality, and state; **parked sessions (Completed/Dormant) are listed only while the client requested segments in the last 60s** — ffmpeg finishing isn't the viewer finishing (a fully-encoded short file vanished mid-play, live), and a closed player parks its session Dormant for up to 24h (phantom "Paused" rows all day — review HIGH ×2, the second also required the beat-guard state filter). Direct-play rows distinguish **Playing** (has heartbeat) from **Streaming** (open stream, no beat — e.g. the music player's gapless preload, found live as a phantom "Playing" row). `DELETE /api/v1/admin/sessions?mediaId&userId&sub&sid` terminates **transcode sessions only** (kills ffmpeg, deletes segments, removes the session — the concurrency caps are counted from live sessions, so removal IS the slot release), audit-logs the full key, normalizes `sub<0→null`, 404s on unknown keys; direct plays are read-only per the spec's v1 non-goal.

**Client.** `ActiveSessionsCard` on the Admin Dashboard (15s polling): user/title/method badge/quality/progress per row, Stop with inline confirm on transcode rows. Review fixes: terminate-404 reads as "already ended" (not a failure) and refetches on settle; a failed fetch shows an error line instead of masquerading as "nothing is playing" (+ stale-data indicator); a confirm left open for a vanished row can't pre-arm against a future row.

**Adjacent non-streamable-media fixes (same class as the album-card bug):** (1) **hero "Play Now" on an Album** (the hero rotation deliberately includes Albums/Books/Games) navigated to the video player → `/stream/{albumId}` 404 broken player — now only Movies/Episodes go to `/play`, everything else opens its detail page (review HIGH); (2) **global search's play button** navigated to `/player/{id}` — a route that doesn't exist (catch-all dumped users on the home page) — now type-aware `/play` vs `/media` (pre-existing, review MED); (3) album-card **Play** enqueues the album's tracks (double-click-guarded, empty/error falls back to the album page) and **Add to Queue** queues the tracks with success/error toasts; artist cards navigate (nothing streamable to queue).

**Live-verified end-to-end** (browser + API): both session kinds listed with real names/positions; a real transcode terminated from the dashboard UI (audit log line, segment dirs deleted, session gone, cap freed); direct-play rows expired ~48s after the player closed; the three live-found listing gaps above were each re-verified after their fixes. 3-reviewer adversarial pass (concurrency / security+perf / client+albums): **all authz vectors verified clean** (role gating incl. scope-less API tokens, no query-token lift on admin routes, CSRF-immune, no replayable data in the DTO, live-state-only — consistent with R-WI-013's no-history-browse stance). Documented accepted limits: the cap is soft against concurrently-OPEN streams (Kestrel bounds those); playhead leads by the client's prefetch buffer; a quality switch can double-list for ≤60s; `MediaDetailPage`'s music `playTrack(item)` is a latent zombie path guarded only by `MediaDetailLayout` hiding the button (LOW follow-up). Suites: **server 1020 / 1 skip / 0 fail; client 221** (+19 server: 9 registry + 10 endpoint integration; +12 client across the card and MediaCard/hero/search fixes).

**Checkpoint 9 — 2026-07-17.** **R-WI-017 (multi-field search) landed, including the D-12 security fix.**

**D-12 closed:** `GlobalSearch` now applies `ApplyContentRatingFilter` alongside the library ACL — a rating-restricted account can no longer surface blocked titles by name, and (critical with the wider matching) not by cast, genre, or description either. Live-proven with a throwaway G-ceiling account: "Austin Powers" (PG-13) returned zero results by title AND by "Mike Myers", genre searches returned only in-ceiling types, admin unaffected. Review hardening on top: **episode results are additionally gated on the PARENT SERIES passing the rating filter** — an episode stamped TV-G inside a TV-MA series would otherwise make search a side door around the blocked series page (regression-tested with a TV-ceiling user).

**Multi-field matching** (LIKE-over-joins per the plan; FTS5 stays a specced follow-up): top-level items match title/description/genre/cast/artist-name; **tracks** match title/artist/album; **episodes** match title (inherited metadata would flood results); Seasons are newly excluded ("Season 1" matches every show — documented + tested). Ranking: title-prefix → title-contains → other-field, `CASE`-translated server-side before `LIMIT`. The per-library search (`LibraryRepository`) matches the same widened fields (ACL + ceiling already preceded it). **User input is now LITERAL in the patterns** (3-arg `EF.Functions.Like` with an ESCAPE clause + 100-char cap — review MED: raw `%`/`_` were live wildcards that corrupted the new prefix ranking and amplified a superlinear-scan DoS vector; contains-side exposure was pre-existing). Also fixed live: reference-keyed `GroupBy(m.Library)` under `AsNoTracking` put every hit in its own duplicate library group — now keyed by id (regression-tested).

**Client:** tracks in the search dropdown **play directly** in the audio player (and the R-WI-015 media session picks them up); episode ROW clicks route to the series page (episodes have no working detail page — review MED); dropdown thumbnails now attach the query token (`/api/v1/music/...` covers 401'd as raw `<img>` src — review MED); and result rows carry a context line ("artist — album" / "Series — S2 · E5") because live data showed nine identical "Caught In A Mosh" rows with no way to tell them apart (review HIGH UX). Server side of that: the search query Includes Artist/Album/Series and `MediaItemDto` gains a nav-conditional `metadata` name-context block (endpoints that don't Include stay wire-identical — this also fixes the always-"Unknown Artist" player bar for search-played tracks).

**Perf (spec acceptance):** measured on a synthetic 25k-item / 60k-cast-row / 40k-genre-row fixture: ~430ms worst case (no-match full scan), stable across match profiles; typical home libraries (1-5k items) proportionally ~20-90ms behind a 300ms-debounced box. LIKE-over-joins verdict holds; FTS5 remains the documented follow-up for very large libraries.

**Documented residuals (accepted/follow-ups):** global search uses `Like` (metachar-escaped) while per-library uses `Contains`/`instr` (metachars always literal) — semantics now effectively aligned, non-ASCII case-sensitivity is a SQLite-without-ICU limitation on both (pre-existing); per-library TV search still returns series only (episodes are global-search-only — the repo narrows to `Type == Series` before the predicate); "original title" from the spec has no backing column and was dropped; ComicIssues remain globally searchable while hidden from library browse (pre-existing inclusion); with a TV ceiling set, unrated episodes are fail-safe hidden even when their series is allowed (consistent with all browse paths). Suites: **server 1030 / 1 skip / 0 fail; client 227** (+10 server search-integration tests, +6 client dropdown tests). Live-verified end-to-end in the browser (actor/overview/genre/track/artist queries, ranking, grouping, track-play from the dropdown); verification state fully cleaned.

**Checkpoint 10 — 2026-07-18.** **R-WI-018 + R-WI-019 + R-WI-020 landed — Phase C (and the whole R-WI-001..020 plan's engineering scope) COMPLETE.** Per maintainer instruction, out-of-scope bugs discovered along the way were TRACKED, not fixed: see `docs/plans/post-phase-c-bug-backlog.md` (21 product + 1 test-infra entries, each tagged with the review that found it).

**R-WI-018 (subtitle appearance + sync).** Appearance = `::cue` styling (the chosen design; custom renderer rejected for v1): size/color/background-opacity/edge-style selects in Client Settings with a live preview, persisted per device, scoped to the player's video class. Sync = an in-player ±0.5s nudge (clamped ±30s, per-session, reset on item AND track switch — drift belongs to a subtitle file). **The sync model was corrected mid-implementation by live verification:** a labeled-cue fixture (cues announcing their own timestamps) exposed that the server ALREADY aligns the VTT to the stream timeline on far-seek restarts (`OffsetWebVttTimestamps`) — my first model compensated for `seekOffset` client-side and double-shifted (cues at −476s instead of +2s). Final model: server keeps cues stream-aligned; the client applies ONLY the user's nudge, **anchored per-cue** (each cue remembers its served times) so no call pattern — hls.js re-firing MANIFEST_PARSED, track-element recreation, onload+fallback double-apply — can compound the shift. Review fixes: the sync menu no longer renders when subs are off (−1 is the Off sentinel, not null); VTT responses are `Cache-Control: no-store` + the track src carries a per-seek cache-buster (the URL was identical across restarts while content changed — a stale cached VTT desyncs by the whole seek); a failed server-side alignment now DISABLES subtitles for that stream instead of serving them minutes-off (fail-closed). +11 client unit tests.

**R-WI-019 (*arr scan webhook).** `POST /api/v1/scan` gated by the new least-privilege **`write:library`** scope (mintable in the token UI; the R-WI-006 grant reserved for exactly this). Review HIGH hardening: **full sessions must be admin** (the scope-policy model admits every session by design, and scanning was admin-only before this endpoint — a plain user session gains nothing), and **every branch is filtered by the caller's library ACL** — hidden library names/ids never appear in the pathless enumeration, and a path inside a hidden library answers exactly like one outside every root (anti-probe). Review MED: real Sonarr/Radarr payloads carry NO top-level `path` — the endpoint now parses the actual webhook shapes (`series.path`, `episodeFile.path`, `movieFile.path`, `movie.folderPath|path`) case-insensitively, and `eventType: Test` (the connection-test button) succeeds without touching the queue. Review MED: the scan queue now allows ONE queued follow-up behind a RUNNING scan (an import landing mid-scan was silently coalesced into a scan that had already walked past it — the file stayed missing until the next sweep); repeat pings dedup against the queued follow-up. Plus a per-caller `webhook` rate-limit policy (30/min). Path jail: lexical canonicalisation, separator/boundary-guarded, traversal-collapsed (live-verified: 202+correct-library for a real media path; 401/403/404 for anonymous / wrong-scope / outside-roots+traversal). Docs: `docs/user-guide/arr-webhook.md`. +9 integration tests.

**R-WI-020 (personalized home rows).** `RecommendationService.GetHomeRowsAsync` → `GET /api/v1/media/home-rows` → `PersonalizedRows` on the home page below Continue Watching. Ship-simple genre affinity per spec: recent VIDEO plays seed the signal (review MED: music history is excluded — a heavy listener's window emptied the rows or steered them with music genres while candidates are movies/series); episodes roll up to their series; affinity counts DISTINCT seeds per genre (breadth over binge-volume); only ACL/ceiling-VISIBLE seeds steer rows and headings, with fallback to the next visible seed; candidates are visible top-level items excluding seeds/watched (watched = explicit flag OR ≥95% position); rows are deduped, thin rows (<4) and no-history users self-suppress. Live-verified: seeded history → "Because you watched small soldiers" rendered on the home page with genre-correct picks; empty for fresh users. +5 integration tests (incl. the music-crowding regression and ceiling filtering).

**Review residuals accepted/logged:** the backlog gained B-13…B-21 (top items: the HLS master's non-compliant subtitle rendition; iOS/native-HLS has no text subtitles at all; the decorative `read:library` scope; the hero rotation ignoring rating ceilings) — all pre-existing, tracked for the post-Phase-C fix wave. Suites: **server 1044 / 1 skip / 0 fail; client 237 pass**. All verification state cleaned (fixtures retained in the LiveVerify library per convention; test tokens revoked; seeded history/interactions removed).

**Recommended next unit of work:** the post-Phase-C bug backlog, roughly in its listed order (B-01/B-02 playback-hardening first); then the maintainer-gated R-WI-001 remainder and §7 open questions.
