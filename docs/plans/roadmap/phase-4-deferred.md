# Phase 4 — Deferred Features Register

**Roadmap Phase:** 4 of 4 (reference register; not a scheduled work phase)
**Status:** Reference document
**Date:** 2026-05-11
**Parent Document:** [00-roadmap-overview.md](./00-roadmap-overview.md)

## 1. Purpose

This document enumerates features that were considered during the 2026-05 roadmap planning and explicitly **deferred**. Each entry records:

- A brief description of the scope if undertaken.
- The reason for deferral.
- The workaround available to users today.
- The trigger conditions under which re-activation would be reconsidered.

Documenting deferred features serves three goals:

1. **Future maintainers** do not re-litigate previously-considered features without new evidence.
2. **Users** have a clear answer to "why doesn't SoftMedia do X?" with a workaround.
3. **Prospective contributors** who wish to take on a deferred feature have a starting brief.

A feature in this register is **not** a permanent refusal except where explicitly stated. Reclassification from Phase 4 to a scheduled phase requires the procedure in §3 below.

## 2. Deferred Features

### P4-001 — Live TV / DVR Subsystem

**Scope if undertaken.** HDHomeRun protocol client, XMLTV / Schedules-Direct EPG ingestion, channel-list editor, DVR scheduling with conflict resolution, recording cleanup, server-side commercial-break detection.

**Reason for deferral.** Whole subsystem; ~8-12 engineer-weeks for a complete implementation. Audience overlap with hobbyist user base is significant but narrower than the Phase 1-3 work items, and Plex's Live TV remains a feature few users actually adopt despite its prominence in marketing.

**Workaround.** Run Channels DVR or TVHeadend alongside SoftMedia.

**Reassessment trigger.** ≥3 contributor expressions of interest in owning the feature, or sponsorship.

---

### P4-002 — Native Mobile Applications

**Scope if undertaken.** Android (Kotlin / Compose) and iOS (Swift / SwiftUI) native clients with offline media sync.

**Reason for deferral.** Two new toolchains, two app-store relationships, ongoing per-release work. Disproportionate to a small-team project. The Phase 2 PWA captures the majority of the "install on phone" perception.

**Workaround.** PWA after P2-WI-003 ships. Third-party media clients (Findroid for Android, Infuse on iOS) may eventually add SoftMedia support via the API tokens shipped in P1-WI-002.

**Reassessment trigger.** Phase 2 PWA proves insufficient for genuine offline media use cases **and** a contributor commits to owning one of the platforms.

---

### P4-003 — AirPlay Receiver / Sender

**Scope if undertaken.** Either side of AirPlay is significant. Receiver requires implementing Apple's RAOP / AirTunes protocols on .NET; sender requires a first-party iOS app (P4-002 territory).

**Reason for deferral.** Apple platform restrictions; the open-source RAOP implementations (RPiPlay, etc.) are not realistically embeddable in a .NET binary.

**Workaround.** Standalone AirPlay receiver appliance (Apple TV, AirPlay-capable speaker) on the same network as the SoftMedia server.

**Reassessment trigger.** A mature .NET AirPlay implementation appears, **or** Apple changes its policy on third-party AirPlay implementations.

---

### P4-004 — DLNA / UPnP Media Server — **IMPLEMENTED (2026-06-05)**

Triggered by the reassessment condition below (operator with an LG webOS TV, no Chromecast).
Built as a **DLNA Media Server (DMS)** — the TV's media player browses and plays the library —
rather than a renderer/AVTransport "push" target, which is what that use case needs.

**Delivered.** `Services/Dlna/`: `DlnaContentDirectory` (library tree → DIDL-Lite, unit-tested),
`DlnaDescriptions` (device description + SCPDs), `DlnaProtocol`, `SsdpDiscoveryService` (UDP
multicast announce + M-SEARCH replies), `DlnaServerInfo`; `Controllers/DlnaController` (device
description, ContentDirectory + ConnectionManager SOAP, LAN-only file serving with range). Settings
`EnableDlna` (default off) + `DlnaServerName`. Docs: `user-docs/features/dlna.md`.

**Security posture (the footguns, handled).** DLNA is unauthenticated, so the surface is gated
three ways: opt-in (default off), **LAN-only** (non-LAN IPs → 404), and per-file path-jail
validation. Per-user ACL / content ratings do **not** apply (no user). Documented as such.

**Verification.** ContentDirectory/DIDL + the HTTP surface (gate, unauthenticated description,
SOAP Browse) are covered by tests (`DlnaContentDirectoryTests`, `DlnaIntegrationTests`). **SSDP
discovery and real-TV rendering are NOT verifiable in-repo** — they need on-device testing per the
docs. Treat those as "implemented, pending field verification."

**Known limitations.** Direct-play only (no DLNA transcoding); per-restart UDN; first-LAN-IPv4 on
multi-NIC hosts; AV libraries only (no books/games/photos).

**Original deferral rationale (for history).** Niche audience; substantial protocol surface with
security footguns; the Chromecast support in P3-WI-001 covered most "throw to TV" cases. Workaround
was a separate DLNA bridge (ReadyMedia/MiniDLNA). **Reassessment trigger** (now met): demand from
operators with TVs that lack Chromecast support.

---

### P4-005 — Multi-Version / Editions Support

**Scope if undertaken.** Schema change to support N playable files per logical movie (Director's Cut, Theatrical, Extended, 4K vs 1080p). Player UI for "play which version?". Scanner changes to recognise versioning conventions.

**Reason for deferral.** Schema reshuffle ripples through scanner, metadata refresh, playlist materialisation, and the player. Workaround is acceptable for the current user base.

**Workaround.** Keep editions in separate libraries or as separate `MediaItem` rows; cost is duplicate cards.

**Reassessment trigger.** User feedback indicates the duplicate-card workaround is a major adoption blocker.

---

### P4-006 — OIDC / OpenID Connect / SSO

**Scope if undertaken.** Integrate `Microsoft.AspNetCore.Authentication.OpenIdConnect`; admin UI for IdP configuration; user-account linking flow.

**Reason for deferral.** Implementable in approximately one week of focused work, but lower-leverage than P2-WI-005 (TOTP) and P1-WI-002 (API tokens) for the same target user. Operators who need SSO can already front SoftMedia with Authelia or Authentik via Caddy's `forward_auth` directive.

**Workaround.** Authelia / Authentik / Keycloak with reverse-proxy `forward_auth`.

**Reassessment trigger.** Phase 2 TOTP ships and substantial user demand for SSO remains. Likely a Phase 5 candidate.

---

### P4-007 — Trakt / Last.fm / AniList Scrobbling

**Scope if undertaken.** First-party SDK integrations for each service; OAuth flows; scrobble queueing and retry.

**Reason for deferral.** After P2-WI-004 (webhooks) ships, third-party scrobbling becomes a user-side concern: a webhook-to-Trakt translator can be written by anyone. First-party SDK integration introduces unjustified ongoing maintenance burden as service APIs evolve.

**Workaround.** Becomes viable after P2-WI-004 via user-written webhook translators. Community-published translators are the expected delivery model.

**Reassessment trigger.** A widely-adopted webhook-to-scrobbler bridge does not emerge within 12 months of P2-WI-004 shipping, **and** scrobbling remains a top user request.

---

### P4-008 — Music Lyrics, ReplayGain, and Equalizer

**Scope if undertaken.** LRC parser, sidecar lyric serving, ReplayGain analysis on scan, per-track gain application at playback time, Web Audio API equalizer.

**Reason for deferral.** Each item is self-contained and small — these are excellent candidates for community contributions. Not deferred because of size but because the maintainer's limited bandwidth is better spent on items only the maintainer can do.

**Workaround.** None for lyrics. Manual gain adjustment in lieu of ReplayGain. None for EQ.

**Reassessment trigger.** Community contribution offered. May be picked up under the P3-WI-005 reserved slot.

---

### P4-009 — Photos Library Completion

**Scope if undertaken.** Build out the partially-existing `MediaType.Photo` and `ExifMetadataProvider` infrastructure: PhotoController, photo scanner, album / timeline UI, EXIF-based grouping (date, location, camera).

**Reason for deferral.** Photos are explicitly Phase 2 in SDD §4.1. The photo-server audience overlaps strongly with Immich and PhotoPrism, both of which are excellent in this space.

**Workaround.** Immich or PhotoPrism alongside SoftMedia.

**Reassessment trigger.** Audience signal that users want a single-pane media + photos experience. May be picked up under the P3-WI-005 reserved slot.

---

### P4-010 — SAML / LDAP / Enterprise SSO

**Scope if undertaken.** SAML SP implementation; LDAP authentication backend; account-provisioning hooks.

**Reason for permanent deferral.** These are enterprise features. SoftMedia is a home-server project. There is no plausible scenario in which this changes.

**Workaround.** Reverse-proxy SSO (Authelia, Authentik) covers the legitimate use cases.

**Reassessment trigger.** None planned.

---

### P4-011 — Passkey / WebAuthn Authentication

**Scope if undertaken.** Implement WebAuthn registration and authentication ceremonies; support roaming authenticators (security keys) and platform authenticators (Touch ID, Windows Hello).

**Reason for deferral.** The .NET WebAuthn library landscape is less mature than for TOTP. The user-benefit gradient over TOTP in the homelab context is small. Defer until either the library landscape stabilises or strong user demand emerges.

**Workaround.** TOTP from P2-WI-005.

**Reassessment trigger.** A mature .NET WebAuthn implementation is available, **and** ≥10 user requests for passkey support.

### P4-012 — Web Player Shader Upscaler (Anime4K-class)

**Scope if undertaken.** Client-side GPU upscaling in the web player for low-resolution sources: the
`<video>` element keeps owning HLS/audio/seek/subtitles but renders visually hidden; frames are
pulled per `requestVideoFrameCallback` through a WebGL2/WebGPU shader pipeline (Anime4K-class CNN
shaders are MIT-licensed with existing web ports; an FSR-style sharpen+scale is the low-end
fallback) onto a canvas positioned with the player's existing `object-contain` letterbox math.
Control model settled during 2026-07 design discussion: **default Off**; per-user setting plus a
player quick-menu with Off / Auto / On; "Auto" enables only when the source is meaningfully below
display size AND a measured first-seconds shader frame-time stays under budget (~4 ms), silently
falling back to native rendering otherwise. No server-side admin surface — it runs on the viewer's
GPU. Casting, PiP, and fullscreen are unaffected by design.

**Reason for deferral.** Post-release exploration, deliberately. Zero server cost but a large new
client complexity surface (GPU pipelines across browser/driver combinations, per-device
performance variance = support burden). The 2026-07 stream-quality Tier 1 work (deinterlacing,
no-upscale clamp, lanczos) already banked the high-impact/low-cost wins, and viewers on RTX 30/40
GPUs get AI upscaling for free from NVIDIA RTX Video Super Resolution in Chrome/Edge today.

**Workaround.** Browser bilinear upscaling (correct, just soft); RTX VSR for NVIDIA users; TV/box
hardware upscalers when casting.

**Reassessment trigger.** SoftMedia 1.0 released and stable, **and** user feedback identifies
low-resolution playback in the browser as a recurring pain point. Would be the first genuinely
differentiating feature over Plex/Jellyfin web (neither offers in-browser shader upscaling —
Jellyfin's FSRCNNX/Anime4K support exists only in its desktop mpv clients).

---

### P4-013 — AV1 Film-Grain-Synthesis "Optimize" Pipeline

**Scope if undertaken.** Netflix-style grain handling for old/grainy films: a background
"optimize" job (admin-only — never viewer-triggerable) encodes an AV1+FGS version of selected
titles via **software SVT-AV1** (hardware encoders cannot produce FGS), stored alongside the
original; the existing stream-plan capability negotiation direct-plays the AV1 version to
AV1-decoding clients (all modern browsers via dav1d, most 2020+ devices) and falls back to the
original otherwise — no per-playback cost, no viewer decision. Requires a job queue with core
throttling and idle-hours scheduling, storage management for the second version, and
plan-negotiation extension. Interacts with P4-005 (multi-version support) — the "N files per
logical title" schema work would serve both.

**Reason for deferral.** Post-release exploration, deliberately. Real encode costs on typical home
hardware (2 h 1080p movie at SVT-AV1 preset ~6 with FGS: ~2–5 h on an 8-core desktop, ~10–20 h on
an N100-class mini PC; live FGS transcoding is NOT viable on home hardware) plus a whole
background-job subsystem. Benefit is per-title, not universal.

**Workaround.** None in-app; users can pre-encode grainy titles with SVT-AV1 (`--film-grain`)
externally and add the result to the library.

**Reassessment trigger.** SoftMedia 1.0 released and stable, a background-job subsystem exists for
other reasons (or P4-005 lands), **and** grainy-classic playback quality is a demonstrated user
pain point. Like P4-012, this would be a genuine differentiator — neither Plex nor Jellyfin
exposes FGS.

### P4-014 — Dedicated Desktop Client (mpv-based)

**Scope if undertaken.** A first-party desktop app in the Jellyfin Media Player mould: a thin
shell (webview reusing the existing web UI, or native) around an embedded **mpv** for playback.
The payoff is inheriting mpv's mature playback stack as configuration rather than code: flawless
client-side deinterlacing, the enthusiast shader-scaler ecosystem (FSRCNNX, Anime4K, NNEDI3 shader
packs), audio passthrough, and wide codec support. The client declares broad capabilities in the
stream-plan negotiation (see `docs/api/stream-plan-negotiation.md`) and therefore receives Direct
Play — the server ships untouched bytes and mpv owns the pixels; no server-side changes required.

**Reason for deferral.** A whole new client product (packaging for Windows/macOS/Linux, updater,
per-release QA) for an audience the browser already serves. Sibling decision to P4-002 (native
mobile). Note the interaction with P4-012: a desktop mpv client delivers shader upscaling to the
users most likely to want it, reducing the urgency of the in-browser upscaler.

**Workaround.** The web app in any browser; RTX Video Super Resolution provides AI upscaling in
Chrome/Edge for NVIDIA users. Third-party mpv frontends could target the documented stream-plan
API today.

**Reassessment trigger.** SoftMedia 1.0 released and stable, **and** either sustained user demand
for capabilities browsers cannot provide (audio passthrough, advanced scalers, HDR passthrough
beyond browser support) or a contributor commits to owning the client.

## 3. Re-Activation Procedure

To move an item from this register into an active phase:

1. Open a discussion issue citing the item ID and the reassessment trigger that has been met.
2. Draft a new work item under the appropriate phase document with full specification per the conventions in `00-roadmap-overview.md` §6.
3. Update this register: change the item's status to "Reactivated" and reference the new work-item ID.
4. Obtain maintainer sign-off for inclusion in the active roadmap.
5. Add an entry to the roadmap change log in `00-roadmap-overview.md` §9.
