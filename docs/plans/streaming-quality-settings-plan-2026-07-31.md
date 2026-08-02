# Streaming Quality & Device Settings Plan — 2026-07-31 (rev. 2, post-review)

> **STATUS: ALL SESSIONS (1-4) COMPLETE — PLAN DONE 2026-08-02.**
>
> **SESSION 4 COMPLETE (QS-WI-010 — 2026-08-02).**
> Gates: server suite 2192/0 (includes 14 new matrix facts), client build clean,
> client 633/633 (unchanged — Session 4 touched no client code).
> - **Arbitration matrix**: StreamPlanServiceArbitrationMatrixTests — factor
>   COMBINATIONS (ask × session override × Data Saver × user base/remote caps incl.
>   above-WAN override × LAN/WAN × user/remote/server resolution ceilings), each
>   asserting the delivered plan facts AND the winner code, plus absence of the losing
>   codes (a wrong winner can't hide behind a right value). Cross-dimension rows pin
>   bitrate + resolution winners named TOGETHER, and Data Saver surviving a generous
>   user override.
> - **LiveVerify PASSED** — full checklist against a real running server + real ffmpeg
>   + the real web client in a real browser:
>   - HDR10 fixture built per §7 (libx265 10-bit, smpte2084/bt2020 + master-display
>     SEI; ffprobe-verified BEFORE trusting results) with an SDR 1080p sibling in one
>     per-title folder; scan grouped them (shared VersionGroupId, HdrFormat=HDR10 on
>     the 4K), library grid showed ONE card.
>   - Guardrail plan facts per hwaccel, live: none → pipeline=software/isSoftware=true;
>     nvidia → cuda/false; intel → opencl/false (the OpenCL probe genuinely ran and
>     passed on this box). Reason codes: video.codec.unsupported + hdr.tonemap.
>   - Browser (Vite dev + Chrome): the pre-play prompt rendered with the quality line,
>     the no-hwaccel resource wording, the cause line, and the "Play the 1080p
>     version" offer (clicking it navigated to the SDR sibling). Cancel returned to
>     the detail page (hierarchical back-nav).
>   - Media Tips full loop, live: disable requires the confirm dialog (owner wording,
>     focus-managed Modal); tips OFF suppressed the warn prompt (playback went
>     straight to transcode); "Never show again" honored; re-enable RESET it — the
>     prompt returned after an off→on cycle. Settings screen showed the reworded copy
>     + the live "what the server allows you" line fed by the new endpoint.
>   - WAN classification via X-Forwarded-For 203.0.113.9: bitrate.wan-cap kbps=20000
>     emitted; loopback (LAN) emitted no bitrate code. /me/streaming-limits live:
>     defaults {lan 0/0, remote 20000/0}; after PUT /users/{id}/streaming 30000/0/1080
>     → {lan 30000/1080, remote 30000/1080} (override-wins ABOVE the WAN cap, live).
>   - BlockHdrTranscode live: plan flipped to policy=block and the transcode
>     master.m3u8 answered HTTP 403.
> Session 4 deviations (recorded 2026-08-02):
> 1. **Scratch-sandbox LiveVerify instead of §7's operator-DB signup procedure**: the
>    permission layer (correctly) refused writes to the operator's live softmedia.db,
>    so the server ran from an isolated content root in the session scratchpad
>    (own DB via ConnectionStrings env, port 5011 while the operator's server was
>    down, appsettings copied for JWT config, FFmpeg__Path pinned to the repo's
>    ffmpeg-bin). Strictly safer than the documented procedure — the real DB was
>    never written (verified after teardown: AllowUserSignup=Disabled,
>    HardwareAcceleration=nvidia, no QA rows) and cleanup is `rm -r` of the sandbox.
>    Note for future LiveVerify: content root anchors backups/wwwroot (SR-WI-065), so
>    a scratch content root fully defuses the 2026-07-27 shared-state hazard.
> 2. **Auto-advance binge prompts-once**: not re-verified interactively (needs a
>    2-episode HDR series fixture; the 6s movie fixture auto-completes in seconds).
>    Covered by the Session 2 sitting hand-off unit tests (hdrGuardrail.test.ts,
>    StrictMode-guarded consumption) — accepted.
> 3. **Explainer-button click under tips-off**: the 6s fixture ends before the More
>    menu can be driven; verified structurally instead (mediaTipsEnabled is consulted
>    ONLY by the settings toggle and shouldShowHdrPrompt — the explainer path never
>    reads it) plus the existing modal unit tests. The wan-cap reason CODE the
>    explainer renders was verified live in the plan response.
> No CHANGELOG entry for Session 4 (verification only — no behavior change).
>
> **SESSION 3 COMPLETE (QS-WI-007, 008, 009, 011 — 2026-08-02).**
> Gates passed: server suite 2176/0, client `npm run build` clean, client tests 633/633
> (post-audit polish: the "what the server allows you" line disappears when the
> endpoint is unreachable instead of sitting on "checking…" — informational only,
> enforcement is server-side regardless; pinned by test).
> - **QS-WI-007**: the web client's Auto first-run default is now PINNED by test
>   (useLocalPreferences.test.ts — fresh device ⇒ quality "auto", bitrate unlimited,
>   Media Tips on, HDR warning un-dismissed; older stored blobs keep new defaults for
>   missing keys). Nothing was built for clients that don't exist; their seeds are the
>   checklist added under QS-WI-007 in §3.
> - **QS-WI-008**: the trustworthy-Auto sentence is in the settings help text under
>   Default Quality (ClientSettings → Playback), near-verbatim from this plan, ending
>   with the pointer at the Quality menu + the user-invoked explainer. Auto behavior
>   pinned server-side by two new facts in StreamPlanServiceCapArbitrationTests
>   (direct-play when compatible; else ONE transcode with TranscodeMaxBitrate == the
>   effective cap). No ABR ladder, no bandwidth guessing — nothing of the sort added.
> - **QS-WI-009**: NEW `GET /api/v1/me/streaming-limits` (MeController, `[Route("api/v1/me")]`
>   — no "me" controller existed; AccountController remains the older /account surface).
>   Returns per-tier effective caps `{ lan: {maxBitrateKbps, maxResolution}, remote: … }`,
>   0 = unlimited, mirroring StreamPlanService arbitration: override-wins per-user policy
>   (remote variant off-LAN), RemoteMaxResolution remote-only, and — deliberate deviation
>   for honesty — the server-wide MaxTranscodeResolution guardrail folded in (clamps on
>   top of either ceiling, exactly like enforcement). Reuses the single authorities
>   (UserStreamingPolicyProvider, TranscodeController.ResolutionRank); the only new query
>   shape is a trivial Users existence check, and the whole endpoint is tested on REAL
>   SQLite (MeControllerStreamingLimitsTests: tiers, override-wins incl. above-WAN,
>   remote variant off-LAN-only, server ceiling on top, 404s). 404-over-403 here means:
>   a deleted account answers exactly like a nonexistent one (no 403 path exists on a
>   self-scoped endpoint). Client: settings Playback screen reworded around "what this
>   device asks for" + a read-only "what the server allows you" line (react-query
>   ['me','streaming-limits'] via accountService.getStreamingLimits). One screen, no new
>   writable knobs.
> - **QS-WI-011**: one device-local "Media Tips" toggle (`mediaTipsEnabled` in
>   useLocalPreferences, default on) on the same Playback screen. Disabling opens a
>   confirm dialog FIRST (shared Modal primitive, closeOnBackdrop=false; owner wording:
>   streaming/transcoding are complex, tips help diagnose resource/quality issues,
>   pointer at the user-invoked explainer). Re-enabling needs no confirm and RESETS
>   `showHdrTranscodeWarning` → 'true'. Suppression is ONE pure gate —
>   `shouldShowHdrPrompt` in src/SoftMedia.Client/src/lib/hdrGuardrail.ts, consumed by
>   VideoPlayer — with the contract pinned by tests: Media Tips OFF suppresses the warn
>   prompt; `block` (admin BlockHdrTranscode) ALWAYS shows regardless of every
>   device-local flag (and the server's WouldToneMap 403 enforces it regardless —
>   asserted, not rebuilt). The explainer modal is not routed through the gate at all;
>   `mediaTipsEnabled` is referenced only by the settings toggle and this gate
>   (verified by search), so the user-invoked surface cannot be affected by construction.
> Session 3 deviations (recorded 2026-08-02): the MeController placement, the
> MaxTranscodeResolution fold-in, and the structural (grep + gate-test) verification of
> "explainer unaffected" — all noted above. ClientSettings copy stays plain-English
> (the file is not i18n-ized; the guardrail prompt's i18n strings are untouched).
>
> **SESSION 3 POST-AUDIT FIXES (owner: "resolve all discovered issues", 2026-08-02).**
> Final gates: server 2178/0, client build clean, client 633/633.
> 1. **Quality-label drift closed**: plan arbitration's private parser only knew
>    720p/1080p/4k while TranscodeController.ResolutionRank knew 480p/1440p/8k too —
>    a 1440p session pick was silently ignored and a hand-set "1440p" ceiling was
>    enforced by the /stream gate but not by plans. NEW single authority
>    `Services/Media/QualityLabels` (HeightOrNull/Rank); ResolutionRank,
>    StreamPlanService.ParseQualityToResolution, and MeController all delegate.
>    Behavior change (deliberate): 480p/1440p/8k picks and ceilings now bind in plan
>    arbitration — pinned by two new arbitration facts. See CHANGELOG "Fixed".
> 2. **Media Tips now governs ALL current unsolicited HDR tips**, per this item's own
>    "and any future proactive playback tips/toasts" wording: the two in-player HDR
>    toasts ("tone-mapping applied for subtitles" / "passthrough re-enabled") and the
>    cast warn heads-up are gated by `mediaTipsEnabled` (the toast status ref still
>    advances while suppressed, so transitions stay correct on re-enable). Error and
>    refusal feedback is exempt BY CLASSIFICATION: the cast block toast, the localhost
>    cast error, and cast-failure toasts always show — suppressing why something
>    failed helps no one. The settings toggle description was updated to match.
> 3. Endpoint-unreachable polish (server-allows line hides instead of eternal
>    "checking…") — recorded in the gates line above, pinned by test.
>
> **SESSION 2 COMPLETE (QS-WI-004, 005, 006, 012 — 2026-08-01).**
> Gates passed: server suite 2146/0, client `npm run build` clean, client tests 616/616.
> QS-WI-012 was NOT split out: the bundled ffmpeg already ships the OpenCL filters
> (`tonemap_opencl`, `scale_opencl`, `overlay_opencl` verified via `-filters`), and the
> exact probe/transcode chains were run against it on the dev box (exit 0 — the box has
> a working OpenCL runtime). Key shapes:
> - `TranscodeProfileBuilder.SelectToneMapPipeline` (static) is the ONE pipeline
>   authority (None/Cuda/OpenCl/Software); builder, StreamPlanService, and
>   TranscodeDebugService all consult it, so the guardrail's software flag can never
>   disagree with the executed pipeline. `IOpenClToneMapProbe` (singleton, cached) runs
>   one tiny synthetic-HDR ffmpeg encode per server run; probe-fail ⇒ software fallback.
> - Plan DTO carries the guardrail facts: `ToneMapPlanned`, `ToneMapPipeline`,
>   `ToneMapIsSoftware`, `HardwareAccelerationEnabled`, `HdrTranscodePolicy`
>   ("warn"/"block"/null — block wins). Settings `WarnOnHdrTranscode` (on) /
>   `BlockHdrTranscode` (off) seeded in the Streaming group (generic group render —
>   no custom card needed).
> - New reason codes: `container.unsupported`, `subtitle.burn-in`,
>   `hdr.tonemap.subtitles`, `hdr.tonemap.server-policy`, `hdr.tonemap.codec`
>   (device case keeps the existing `hdr.tonemap`). Client prompt (HdrTranscodePrompt,
>   uses the shared Modal primitive) consumes `explain.reason.*` strings — no parallel
>   wording; en+es added.
> Session 2 deviations (recorded 2026-08-01):
> 1. **"Sitting" mechanism**: §7 says "in one player mount", but auto-advance NAVIGATES
>    (`/play/{next}` remounts the player). The behavioral definition is implemented
>    exactly (auto transitions keep the sitting, any manual play starts a new one) via a
>    consumed sessionStorage hand-off set only by the NextEpisodeOverlay advance
>    handlers (src/SoftMedia.Client/src/lib/hdrGuardrail.ts). Overlay-button advances
>    count as the same sitting (they are not in §7's manual-play list).
> 2. **QS-WI-012 scope**: tone-map math runs on the GPU via `tonemap_opencl` for BOTH
>    intel and amd; QSV VPP generation detection was not attempted, and decode remains
>    software on the OpenCL path (hwupload from system memory — zero-copy QSV/D3D11→
>    OpenCL interop is driver-fragile; the SR-WI-023 software-decode constraint is
>    therefore retained, not lifted). Hardware encoders unchanged. The software
>    zscale/tonemap chain remains the universal fallback.
> 3. **"Play the lower version" = the best NON-HDR sibling** (highest-resolution SDR
>    copy, via `MediaVersion.hdrFormat`): a lower-resolution HDR copy would tone-map
>    just the same, so HDR siblings are never offered. No SDR sibling ⇒ no offer.
> 4. **"Never show again"** lives in the existing localStorage prefs blob
>    (`showHdrTranscodeWarning` in useLocalPreferences) — per-user-per-device, reset
>    path ready for QS-WI-011's Media Tips re-enable (NOT built now, per scope).
> 5. **BlockHdrTranscode is enforced server-side too** (owner follow-up, same session):
>    master.m3u8 refuses with 403 when starting a transcode that would tone-map
>    (`TranscodeController.WouldToneMap`, delegated to the pipeline authority so it can't
>    disagree with ToneMapPlanned; keyed off MediaItem.HdrFormat). Remux/direct play and
>    genuine HDR passthrough stay allowed. The cast flow pre-empts the 403 with a clear
>    toast under block, and informs (toast, not dialog) that HDR→SDR applies when
>    casting under warn — a mid-watch cast click is not a pre-play surface.
> 6. **Bonus honesty fix**: plan `IsHdr` now accounts for the negotiated codec
>    (`CodecCanCarryHdr`) — PreserveHDR + h264-only client no longer yields a plan
>    claiming HDR that the builder would tone-map anyway.
> 7. **QS-WI-006**: no bitrate-per-resolution table existed (encoding was CRF-only,
>    uncapped when nothing negotiated a limit). The audit produced the documented ladder
>    in TranscodeProfileBuilder (`DefaultLadder`: 480p 2500 / 720p 5000 / 1080p 9000 /
>    1440p 14000 / 2160p 22000 kbps h264, hevc/av1 ×0.6) applied as a CVBR ceiling ONLY
>    when no cap was negotiated; unknown output height ⇒ no cap (never guess low).
>    This is a (deliberate, generous) behavior change — see CHANGELOG "Changed".
> 8. No new EF/LINQ query shapes were introduced (settings reads go through the existing
>    SettingsService key cache), so no new SQLite-backed tests were required by §5.
>    LiveVerify with a real HDR fixture stays in Session 4 (QS-WI-010) per §8.
> 9. Same-session cleanup pass (post-audit): guardrail sitting consumption made
>    StrictMode-safe (dev double-mount was clearing the sitting ack); the pre-existing
>    "HDR tone-mapping applied for subtitles" toast is suppressed when the guardrail
>    prompt covers the same fresh load (ref still advances so later transitions toast);
>    the builder's scale label map now covers the numeric "{n}p" strings negotiated
>    plans actually emit (480p/1440p/2160p/4320p — previously only 720p/1080p/4k
>    scaled; a 4K session pick on an 8K source would not have downscaled), shared via
>    one TargetWidth map across the software/CUDA/OpenCL chains and the ladder.
>    Final gates after cleanup: server 2165/0, client build clean, client 616/616.
>
> **SESSION 1 COMPLETE (QS-WI-001..003, 2026-08-01).**
> Gates passed: server suite 2104/0, client `npm run build` clean, client tests 594/594.
> Migration `20260801204257_AddUserRemoteStreamingLimits` adds
> `Users.RemoteMaxStreamBitrateKbps` + `Users.MaxStreamResolution` (Up() verified
> non-empty). Session 1 deviations from the letter of this plan (all recorded 2026-08-01):
> 1. **Card query key**: the "Remote streaming" card follows the codebase's established
>    DlnaSettingsCard pattern — it reads the shared `['settings']` query (init-once) and
>    invalidates `['settings']` after saving its three keys. §5's "own query key / never
>    invalidate ['settings']" line predates the mergeSettingsPreservingEdits fix; NOT
>    invalidating would let the page-level "Save Changes" (full-draft PUT) silently
>    revert the card's save. The card lives on the Streaming Quality tab and the three
>    keys are filtered out of the generic group render (no duplicate knobs).
> 2. **Added `resolution.user-ceiling`** beyond QS-WI-003's enumerated list — the
>    per-user resolution cap QS-WI-002 introduces needs a winner code too.
> 3. `bitrate.clamped` is no longer emitted (replaced by per-winner codes); the constant
>    and the client i18n string remain for compatibility. See CHANGELOG "Changed".
> 4. The user editor's "Streaming limits" section is the existing StreamingModal
>    (opened per-row), extended with the two new fields — not a new collapsed inline
>    section; same one-surface intent.
> 5. Bonus hardening within scope: the direct `/stream` gate is now network-aware
>    (remote user cap applies off-LAN) and gained a per-user RESOLUTION gate; the
>    fabricated-sid master.m3u8 path enforces the user/remote resolution ceilings.
>
> Work items QS-WI-001..012. Rev. 2 (2026-07-31): full accuracy review against the
> codebase — rev. 1 planned to BUILD several things that already exist (LAN/WAN caps,
> clamp-source tracking, HDR client capability flags); those items are now
> "extend/expose" items. Companion to the completed versions plan
> (duplicate-media-versions-plan-2026-07-30.md); the owner's standing decision AGAINST
> automatic capability-aware version pick is unchanged and re-affirmed here.

## §0 VERIFIED existing state (read this before implementing anything)

Confirmed in code 2026-07-31 — the foundation is further along than rev. 1 assumed:

- **LAN/WAN split caps EXIST**: `MaxStreamingBitrate` (WAN, **default 20000 kbps**) and
  `MaxStreamingBitrateLan` (default 0 = unlimited), chosen via `NetworkClassifier.IsLan`
  (StreamPlanService.cs ~99-121). NetworkClassifier's doc comment says it was built for
  exactly this. Caveat to document: CGNAT 100.64/10 (Tailscale) classifies as **LAN**,
  so VPN-tunneled remote users bypass WAN caps — acceptable, but say so in the admin UI
  help text.
- **Per-user bitrate cap EXISTS and is OVERRIDE-WINS, not min-wins**
  (StreamPlanService.cs ~107-111): when `User.MaxStreamBitrateKbps` is set it REPLACES
  the LAN/WAN tier entirely — it can therefore RAISE a user above the WAN cap. This is
  the Emby-style semantic ("this user's personal limit") and is kept, but it was
  undocumented; rev. 1's "min of all ceilings" model statement was WRONG and is
  corrected in §2.
- **Binding-constraint tracking PARTIALLY EXISTS** (P2-WI-002): `bitrateClampSource`
  ("user policy" | "LAN cap" | "WAN cap") flows into structured `StreamReasonCode`s
  (`StreamReasonCodes.BitrateClamped`) consumed by the client explainer.
- **The explainer modal is USER-INVOKED** ("Why is this playing this way?" button in
  the player's More menu → TranscodeExplanationModal). It is a diagnostic the user asks
  for — relevant to QS-WI-011 scoping below.
- **Client HDR capability flags EXIST**: `supportsHdr` / `displaySupportsHdr` /
  `codecSupportsHdr` in useMediaCapabilities (with hard-won guards against browsers
  over-claiming HDR support).
- **HDR/tone-mapping policy EXISTS**: `PreserveHDR` setting (default false);
  TranscodeProfileBuilder (~217-222) runs a HARDWARE tone-mapping pipeline **only for
  `HardwareAcceleration == "nvidia"`** — with Intel/AMD/none, HDR tone-mapping is
  SOFTWARE (CPU-heavy) even when hw decode/encode is on. OWNER CLARIFICATION
  2026-07-31: this is an implementation GAP, not a decision — SoftMedia intends
  hardware tone-mapping wherever possible (new QS-WI-012). QS-WI-005's resource
  warning must therefore derive from the PLAN's actual pipeline ("will this tone-map
  run in software?"), never from a hardcoded vendor list, so it stays correct as
  pipelines land.
- **Other existing settings**: `HardwareAcceleration` ("none" default),
  `TranscodePreset`, `MaxTranscodeResolution`, `DefaultStreamingQuality`,
  `OutputVideoCodec`, `EnableAV1Encoding`, `ForceDirectPlayWhenPossible`,
  `DefaultAudioChannels`, client-side Data Saver, per-user `MaxStreamBitrateKbps`.
- **The HLS pipeline is SINGLE-RENDITION** — there is no ABR ladder; a session plays
  one chosen rendition (direct-play/remux/one transcode). Rev. 1's QS-WI-008 wording
  ("let HLS ABR adapt within the cap") was wrong for this architecture; corrected.

## §1 Why — the failures of the incumbents this plan avoids

Documented, widely-reported user pain with Plex/Jellyfin/streaming apps, used as the
negative spec:

- **P1 (Plex): low silent remote defaults.** The historic "720p/2 Mbps remote quality"
  client default made remote streams look terrible with no indication a cap was
  involved. The sin was the combination LOW + SILENT — not the existence of a default.
- **P2 (Plex): overlapping quality knobs.** Per-app local quality + per-app remote
  quality + "auto adjust" + server limits, named differently per platform — nobody can
  predict what actually plays.
- **P3 (both): unexplained transcodes.** Subtitle burn-in, audio codec, or a container
  quirk silently forces a transcode; users see stutter/CPU and have no idea why.
- **P4 (both): 4K HDR transcode disasters.** Tone-mapping washes out colors and (in
  software) melts CPUs; community folklore ("never transcode 4K") exists because the
  products offer no guardrail.
- **P5 (Jellyfin): unreliable auto bitrate.** Web-client bandwidth detection picks
  absurd values; "auto" is not trustworthy, so users hard-pin and then forget.
- **P6 (Netflix-class): no manual control at all.** ABR only; the opposite failure.
- **P7 (Plex): hardware transcoding is paywalled** (Plex Pass), as are managed-user
  restrictions. Table stakes belong in the base product — SoftMedia already ships hw
  transcoding free; keep it that way.

## §2 The model (corrected to match reality)

ONE mental model, three layers:

1. **The client asks.** Each install owns its local defaults (quality, max bitrate,
   Data Saver) + the in-player session override. Per-device behavior falls out of
   device-local storage — NO admin device matrix exists or will exist.
2. **The server clamps.** Precedence AS IMPLEMENTED (and now documented, not changed):
   - Bitrate ceiling: `per-user cap` if set (override-wins — it may exceed the network
     tier; that is the feature: "this user's personal limit"), else `LAN cap` /
     `WAN cap` by NetworkClassifier. The chosen ceiling then clamps the client's ask.
   - Resolution: session/default quality pick, then `MaxTranscodeResolution` clamps.
   - `StreamPlanService` is the ONLY arbitration point; precedence logic may not exist
     anywhere else.
3. **The player explains.** Whenever the delivered stream differs from the ask (or the
   source), the reason system names what happened — extending the EXISTING
   StreamReasonCodes, not inventing a parallel channel.

## §3 Work items

### Session 1 — expose & complete the network-aware caps (P1/P2)

**QS-WI-001 — Surface the existing LAN/WAN caps + add remote resolution.**
- The split EXISTS (§0); the work is: (a) one admin card ("Remote streaming") exposing
  `MaxStreamingBitrate`/`MaxStreamingBitrateLan` with plain wording and the
  Tailscale/CGNAT-counts-as-home caveat in its help text; (b) NEW
  `RemoteMaxResolution` (default unset) applied beside the WAN bitrate cap;
  (c) DEFAULT DECISION — see §6.2: the shipped WAN default is 20 Mbps today, which is
  P1-shaped ONLY because it is silent; with QS-WI-003 naming it on every clamped
  stream it stops being silent. Owner to confirm: keep 20 Mbps (recommended) or go
  uncapped.
- Verify: plan tests LAN vs remote incl. resolution; card renders/saves (own query key).

**QS-WI-002 — Per-user caps: remote variant + resolution, semantics documented.**
- `User.MaxStreamBitrateKbps` gains `RemoteMaxStreamBitrateKbps?` and
  `MaxStreamResolution?` (nullable = inherit). Override-wins semantics KEPT and stated
  in the admin UI ("overrides the server's network caps for this account"). Admin
  user-editor: one collapsed "Streaming limits" section.
- Verify: arbitration tests pinning override-wins (user cap ABOVE the WAN cap is
  honored — deliberate), remote variant applies only off-LAN.

**QS-WI-003 — Complete the binding-constraint explainer.**
- EXTEND the existing StreamReasonCodes/bitrateClampSource machinery to cover every
  clamp: SessionOverride, DataSaver, UserCap, UserRemoteCap, LanCap, WanCap,
  ServerResolutionCeiling, RemoteResolutionCeiling, SourceIsSmaller. The user-invoked
  explainer modal renders one plain sentence naming the winner; PlayerDebugPanel shows
  the chain; admin Now Playing tooltip shows the same code per session.
- Verify: a reason-code test per clamp path.

### Session 2 — honest transcode reasons (P3) + the 4K/HDR guardrail (P4)

**QS-WI-004 — Transcode-reason taxonomy audit.**
- Reason codes exist; audit for the missing culprits and human wording: video codec,
  audio codec, container, SUBTITLE BURN-IN (named explicitly), bitrate cap, resolution
  cap, HDR tone-mapping. Closed enum, i18n strings written for humans.
- Verify: plan tests per trigger; modal snapshot.

**QS-WI-005 — HDR-transcode guardrail (reason-aware pre-play prompt, NOT auto-pick).**
- Fires when the computed plan would TONE-MAP (HDR source → SDR output). Composed
  reasons, each true-to-cause:
  - `ToneMapQuality` — ALWAYS ("colors may look washed out — converted HDR never looks
    as good as a native SDR copy").
  - `SoftwareToneMapLoad` — when THIS PLAN's tone-map will run in software. The flag
    is computed by TranscodeProfileBuilder from the pipeline it actually selected —
    NEVER from a hardcoded vendor list — so it stays truthful as QS-WI-012 adds
    hardware pipelines (today that means: any non-NVIDIA setup). Wording split:
    hwaccel "none" → "no hardware acceleration configured — this conversion is very
    CPU-intensive" + pointer to Settings → Transcoding; hw-accel configured but
    tone-map still software → "HDR conversion on this server runs partly on the CPU
    and may be demanding". Fully hardware pipeline → line omitted; quality is the
    only stated concern.
  - `ClientHdrCompatibility` — when the transcode exists because the device reports no
    HDR support (the EXISTING supportsHdr capability flag) — "this device can't play
    HDR video directly".
  - `TranscodeCause` — otherwise, the QS-WI-004 reason that forced the re-encode
    (which cap via QS-WI-003, subtitle burn-in, session quality pick).
  - Extensible list by design ("for example but not limited to").
- Interaction with `PreserveHDR`: when PreserveHDR would keep HDR end-to-end (capable
  client, no forced SDR target), no tone-map is planned and the prompt never fires —
  the prompt keys off the PLAN, not the file.
- Buttons: [Play anyway] [Play the 1080p version] [Never show again] — version offer
  only when a lower copy exists in the version group; explicit choice, never auto-pick.
  Default frequency per owner: EVERY qualifying play — with ONE ergonomic exception
  (owner-flagged 2026-07-31 review): episode AUTO-ADVANCE within a continuous sitting
  does not re-prompt (answering once covers the binge; the next manual play prompts
  again). "Never show again" is device-local, never pre-selected, reset by QS-WI-011
  re-enable.
- Server settings (default on): `WarnOnHdrTranscode`; `BlockHdrTranscode` (default
  off) — dialog then offers only the alternate version / cancel.
- Verify: plan tests per cause × hwaccel value (none/intel/amd/nvidia); prompt
  suppression across auto-advance; dialog branch tests.

**QS-WI-006 — Sane transcode ladder defaults audit.**
- Review TranscodeProfileBuilder's bitrate-per-resolution defaults against current
  community guidance. One table, documented in code, admin-overridable only via the
  existing preset setting — no new knobs.

**QS-WI-012 — Hardware tone-mapping beyond NVIDIA (owner directive 2026-07-31).**
- Close the §0 gap: hardware (or GPU-compute) tone-mapping pipelines for the other
  acceleration options, in preference order per platform:
  - **Intel**: QSV VPP tone-mapping where the generation supports it, else
    `tonemap_opencl`; keep decode→tonemap→scale→encode on-device (one hop through
    system RAM halves throughput).
  - **AMD**: `tonemap_opencl` alongside AMF encode (the practical Windows path);
    VAAPI tone-mapping where the server runs Linux.
  - Software `zscale/tonemap` remains the universal fallback — never remove it.
- Requires an ffmpeg build with OpenCL filters enabled — verify the bundled
  `ffmpeg-bin` supports them; if not, updating the bundled build is part of this item.
- Feeds QS-WI-005 automatically: as each pipeline lands, the plan's
  "tone-map runs in software" flag flips off for that setup and the prompt's resource
  line disappears — no prompt code changes needed (that's why the flag derives from
  the selected pipeline, not a vendor list).
- Verify: profile-builder tests per hwaccel value asserting the selected tone-map
  pipeline + the software/hardware flag; LiveVerify HDR fixture per available
  accelerator on the dev box.

### Session 3 — client-side polish (P5/P6) — lands WITH each future client

**QS-WI-007 — First-run defaults by device class.**
- On first run each client seeds its OWN local defaults: web → Auto; desktop/TV →
  Original/uncapped; mobile → 1080p/8 Mbps + "Limit on cellular" ON. Never
  admin-managed. Current web client only needs the Auto default verified; the rest is
  a per-client checklist for the native apps.
- **Per-client first-run checklist (Session 3, 2026-08-02 — apply when each native
  client lands; nothing to build until then):**
  - [x] **Web**: quality `auto`, bitrate unlimited, Data Saver off — pinned by
        useLocalPreferences.test.ts (the hook's defaults ARE the first-run seed).
  - [ ] **Desktop**: quality `original`, bitrate unlimited (wired ethernet/wifi at
        home is the norm; the server still clamps remotes via the WAN tier).
  - [ ] **TV**: quality `original`, bitrate unlimited (same reasoning as desktop;
        TVs are effectively always at home).
  - [ ] **Mobile**: quality `1080p`, bitrate 8 Mbps, "Limit on cellular" ON (the
        Data Saver analog must ship enabled — P1's sin was LOW + SILENT; 1080p/8 Mbps
        is neither low nor, with QS-WI-003 naming every clamp, silent).
  - Every client: defaults are device-local storage only, seeded on first run, never
    synced or admin-managed; each client must also pin its seed with the equivalent of
    the web test above, and ship Media Tips ON + the HDR warning un-dismissed
    (mediaTipsEnabled/showHdrTranscodeWarning analogs) so QS-WI-005/011 behave
    identically across device classes.

**QS-WI-008 — Trustworthy Auto (single-rendition reality).**
- Auto = the server picks direct-play/remux when possible, else ONE transcode at the
  session's effective cap. No client-side bandwidth guessing, ever (the P5 failure).
  There is NO multi-rendition ABR ladder in this architecture and building one is OUT
  OF SCOPE (it multiplies transcode cost, against the whole resource philosophy) —
  if a stream still buffers, the user's lever is the existing Quality menu, and the
  explainer says what the stream is doing. Document exactly that sentence in the
  settings help text.

**QS-WI-009 — Settings page copy pass.**
- Client settings reworded around the model: "What this device asks for" + a read-only
  "What the server allows you" line (new `GET /api/v1/me/streaming-limits` returning
  the caller's effective caps per network tier). One screen, no new writable knobs.

**QS-WI-011 — "Media Tips" group (owner request, SCOPE CORRECTED in review).**
- One device-local toggle ("Media Tips") governing UNSOLICITED educational surfaces:
  the QS-WI-005 HDR pre-play prompt and any future proactive playback tips/toasts.
- SCOPE CORRECTION vs. the original instruction: the existing subtitles/HDR
  explanation surface (TranscodeExplanationModal) is USER-INVOKED — the user presses
  "Why is this playing this way?" — so it is NOT suppressed by Media Tips (suppressing
  a diagnostic someone explicitly asks for helps no one, and the disable-confirm
  wording itself points users at diagnostics). Media Tips governs what SoftMedia
  volunteers, never what the user requests.
- Disabling requires a confirm dialog FIRST (owner wording): streaming and transcoding
  are complex, most users don't realize what affects playback, and leaving Media Tips
  enabled helps diagnose hardware resource usage and playback quality issues.
- Per-prompt "Never show again" flags stay finer-grained; group re-enable RESETS them.
- Admin `BlockHdrTranscode` is never bypassed by this user toggle.
- Verify: toggle + confirm flow; suppression/reset behavior; explainer button
  unaffected.

### Session 4 — verification

**QS-WI-010 — Full-suite + LiveVerify.**
- Arbitration-matrix integration tests: client ask × session override × Data Saver ×
  user cap (incl. ABOVE-WAN override) × LAN/WAN × resolution ceilings — each asserting
  the delivered plan AND the emitted reason code. Live QA: remote-classified request
  obeys the cap and the explainer names it; HDR fixture triggers the guardrail with
  the versions offer and correct resource line per hwaccel setting; auto-advance
  binge prompts once.

## §4 Explicit keep-out list (the convolution firewall)

- NO per-device-type admin overrides/matrices.
- NO per-library quality rules.
- NO codec-preference knobs beyond the existing OutputVideoCodec/AV1 settings.
- NO automatic version auto-pick (standing owner decision) — the HDR guardrail OFFERS
  a version; it never chooses one.
- NO new overlapping quality menus — the existing Quality menu stays the single
  session override.
- NO multi-rendition ABR ladder (out of scope by design, see QS-WI-008).

## §5 Standing constraints (inlined — do not rely on other plans being open)

- EF InMemory hides translation bugs: integration-test any new LINQ on SQLite
  (`Microsoft.Data.Sqlite` in-memory harness, pattern in
  `VersionGroupReadPathTests.cs`).
- Client type gate is `npm run build` ONLY (root tsc checks nothing). Client tests:
  `npm test -- --run` in src/SoftMedia.Client.
- Server tests: `dotnet test src\SoftMedia.Server.Tests\SoftMedia.Server.Tests.csproj`
  — use ABSOLUTE paths if the shell has cd'd elsewhere. A RUNNING dev server locks
  bin\ and fails builds (check for a `SoftMedia.Server` process first; the operator
  often has one up).
- Admin/settings cards use their OWN react-query keys; NEVER invalidate ['settings']
  (silently reverts unsaved SettingsPage edits).
- New endpoints follow the 404-over-403 anti-probe rule. Live checks hit
  http://127.0.0.1:5011 (IPv4 — localhost/IPv6 stalls ~210ms/request).
- NetworkClassifier is the ONLY authority on home-vs-remote; StreamPlanService is the
  ONLY arbitration point; TranscodeProfileBuilder owns pipeline selection.
- `dotnet ef` needs explicit `--project src/SoftMedia.Server --startup-project
  src/SoftMedia.Server` (QS-WI-002 adds User columns → one migration; build fresh
  before `migrations add` or the migration generates EMPTY from a stale assembly —
  verify the generated Up() is non-empty).

## §7 Fresh-session onboarding — code landmarks & definitions

**Code landmarks (verified 2026-07-31):**
- Arbitration: `Services/Media/StreamPlanService.cs` → `ComputeStreamPlanAsync`
  (LAN/WAN pick ~99-121, clamp + `StreamReasonCodes.BitrateClamped` ~165-183,
  quality/resolution ~185-201). Reason codes + `ClientCapabilities` live beside it.
- Caps entry: `Controllers/TranscodeController.cs` (`ResolutionRank` ~90, per-user cap
  fetch ~102-106) — passes `userMaxBitrateKbps` into the plan.
- Pipelines: `Services/Transcoding/TranscodeProfileBuilder.cs` (HDR/tone-map selection
  ~217-250 incl. the SR-WI-023 software-decode-for-HDR-on-intel/amd constraint QS-WI-012
  lifts; qsv/amf/nvenc encoder args ~700-830). Settings read in
  `Services/Transcoding/FFmpegService.cs` ~155 ("HardwareAcceleration", "none").
- User model: `Models/User.cs` (`MaxStreamBitrateKbps` — add siblings here).
- Client: `components/player/TranscodeExplanationModal.tsx` (user-invoked via the
  More-menu "Why is this playing this way?" button, VideoPlayer ~2693),
  `components/player/PlayerDebugPanel.tsx`, `hooks/useMediaCapabilities.ts`
  (`supportsHdr` + guards), `pages/settings/ClientSettings.tsx` (device-local prefs),
  Data Saver in VideoPlayer ~674-680. Admin cards live in `components/admin/`
  (copy `CacheUsageCard.tsx` + its test as the card template).
- i18n strings for player explanations: `src/i18n.ts` (`explain.*` keys).
- The versions feature this plan touches (guardrail's "Play the 1080p version"):
  `item.versions[]` on detail DTOs, primary rule in
  `Services/Media/VersionPrimaryRule.cs` — versions and caps stay ORTHOGONAL.

**Definitions a fresh session must not re-invent:**
- "Sitting" for the guardrail's auto-advance exception = consecutive automatic
  next-episode transitions in one player mount without manual navigation; any manual
  play (detail page, episode row, version switch) starts a new sitting and may prompt.
- Media Tips + "Never show again" flags are DEVICE-LOCAL (same localStorage prefs
  mechanism as ClientSettings), NOT server-stored.
- "Unsolicited surface" (Media Tips scope) = anything SoftMedia volunteers without a
  click asking for it. The explainer modal is excluded BY DEFINITION.

**Live QA prerequisites (QS-WI-010):**
- Reusable procedure from the versions plan QA (2026-07-31): temporarily enable
  signup via the Settings table, create a `dupqa`-style admin user, drive the API,
  clean up fully (libraries → user → re-disable signup). A throwaway sqlite CLI is
  buildable in the scratchpad (Microsoft.Data.Sqlite console app) — no sqlite3/pwsh
  on this box.
- Remote-classification test: NetworkClassifier treats only RFC1918/loopback/CGNAT as
  LAN — exercise the WAN path via `X-Forwarded-For` with a public IP (forwarded
  headers are honored per P0-WI-001) rather than trying to originate real WAN traffic.
- HDR fixture: ffmpeg testsrc alone is SDR — encode with libx265 10-bit + HDR10
  metadata (`-pix_fmt yuv420p10le -color_primaries bt2020 -color_trc smpte2084
  -colorspace bt2020nc` + master-display SEI) or use a known HDR sample clip; verify
  the probe reports HdrFormat before trusting guardrail results.

## §8 Sequencing

| Session | Items | Gate |
|---|---|---|
| 1 | QS-WI-001..003 | server suite + client gates green; reason-code tests per clamp |
| 2 | QS-WI-004, 005, 006, 012 | suites green; guardrail matrix (cause × hwaccel) |
| 3 | QS-WI-007..009, 011 | client gates green; Media Tips flow tests |
| 4 | QS-WI-010 | full suites + LiveVerify checklist passed; CHANGELOG + plan STATUS updated |

Session 1 is independently shippable. QS-WI-012 may be split out of Session 2 if the
bundled-ffmpeg OpenCL verification turns into a build-pipeline task — it must not
block the guardrail (which is truthful either way by design).

## §6 Decisions

1. **RESOLVED**: per-user caps = BOTH bitrate and resolution; presented as one
   collapsed pair. Override-wins semantics kept and documented (§0/§2).
2. **RESOLVED (owner, 2026-07-31 after rev. 2 context)**: KEEP the shipped
   **20 Mbps WAN / unlimited LAN** defaults. QS-WI-001 therefore changes no defaults —
   it only exposes the settings in the admin card and adds `RemoteMaxResolution`;
   QS-WI-003 makes the cap visible whenever it bites.
3. **RESOLVED**: HDR guardrail warns every qualifying play by default; "Never show
   again" available, never pre-selected; auto-advance within a sitting prompts once
   (ergonomic exception added in review). It is a NEW pre-play prompt, distinct from
   the user-invoked TranscodeExplanationModal.
4. **RESOLVED (review)**: Media Tips governs unsolicited surfaces only; user-invoked
   diagnostics are never suppressed.
