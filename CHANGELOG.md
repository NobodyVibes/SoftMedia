# Changelog

All notable changes to SoftMedia are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/), and the project aims to follow semantic versioning
from 1.0 onward.

## [Unreleased]

### Added
- **"Media Tips" toggle (per device).** One switch under Settings → Client → Playback
  governs the proactive playback tips SoftMedia volunteers on its own: the pre-play
  "HDR will be converted" warning, the in-player HDR notices ("HDR tone-mapping applied
  for subtitles" / "HDR passthrough re-enabled"), and the HDR heads-up when starting a
  cast; future unsolicited tips will join it.
  Turning it off asks for confirmation first (streaming and transcoding are complex,
  and the tips exist to help diagnose hardware load and quality issues); turning it
  back on also restores any per-prompt "Never show again" dismissals so the tips
  actually return. Deliberately *never* affected: the user-invoked "Why is this
  playing this way?" explainer (you asked for it, so it always answers), the admin's
  `BlockHdrTranscode` dialog (an admin rule — also enforced server-side, so no client
  toggle could bypass it anyway), and any error or refusal feedback such as the
  "server doesn't allow HDR conversion" cast message — suppressing the explanation of
  why something failed helps no one.
- **"What the server allows you" on the client settings page.** The Streaming Quality
  screen is now worded around the actual model — *what this device asks for* — and
  shows a read-only line with your account's effective server ceilings at home and
  away (bitrate + resolution), backed by a new `GET /api/v1/me/streaming-limits`
  endpoint that mirrors the plan arbitration exactly (per-user override-wins limits,
  the remote-only network ceilings, and the server-wide conversion ceiling on top), so
  the display can never drift from what enforcement does. The Auto quality option now
  documents its honest contract in place: the server picks direct play or remux when
  possible, else one transcode at your effective cap — no client-side bandwidth
  guessing, ever; if a stream buffers, the Quality menu is the lever and the explainer
  says what the stream is doing.
- **Pre-play HDR warning ("HDR will be converted").** When a playback plan would convert
  HDR to SDR (tone-mapping), the player now asks before starting instead of silently
  serving washed-out colors: the prompt explains the quality cost, adds an honest
  resource line only when the conversion will actually run on the CPU (worded
  differently when the server has no hardware acceleration at all), and names the
  cause — your device, the server's HDR setting, subtitle burn-in, or an 8-bit output
  format — using the same localized reasons as the "Why is this playing this way?"
  panel. When the title has a non-HDR copy in its version group, the prompt *offers*
  "Play the ⟨1080p⟩ version" (it never switches automatically). Buttons: Play anyway /
  Play the SDR version / Never show again (device-local, never pre-selected). Binge
  sessions stay pleasant: episode auto-advance re-uses your answer for the rest of the
  sitting; the next manual play asks again. Two new server settings govern it:
  `WarnOnHdrTranscode` (default on) and `BlockHdrTranscode` (default off — when on, the
  prompt only offers the SDR version or cancel, and the per-device dismissal does not
  bypass it). `BlockHdrTranscode` is also enforced server-side: a transcode session that
  would convert HDR to SDR is refused (403) even if a client ignores the plan's policy —
  repackaging (remux), direct play, and genuine HDR passthrough are unaffected. Casting
  is covered too: the Chromecast receiver is SDR-only, so casting HDR shows a clear
  message under block (instead of a cryptic receiver stall) and a heads-up toast under
  warn.
- **GPU tone-mapping for Intel and AMD (OpenCL).** HDR→SDR conversion previously ran on
  the GPU only with NVIDIA (CUDA); with Intel or AMD acceleration the tone-map ran
  entirely on the CPU. The transcoder now runs it through OpenCL (`tonemap_opencl`) on
  Intel/AMD setups — decode stays software (frames hop through system memory once), but
  the expensive tone-map math moves onto the GPU, with the hardware encoder unchanged.
  A one-time startup probe verifies the machine's OpenCL runtime actually works; when it
  doesn't, the universal software `zscale/tonemap` fallback engages exactly as before.
  The pre-play HDR warning derives its "runs on the CPU" line from the pipeline actually
  selected, so it reflects this automatically.
- **Transcode-cause taxonomy completed.** Two culprits that previously hid behind the
  generic "requires conversion" line are now named explicitly in the explainer: the
  container ("the ⟨mkv⟩ file format can't be streamed to this device as-is") and
  subtitle burn-in. HDR tone-mapping now always names its actual cause: device
  incapability, server policy (`PreserveHDR` off), subtitle burn-in, or an output codec
  that can't carry HDR. All strings localized (en + es).
- **"Remote streaming" card on the admin Settings page (Streaming Quality tab)** — one
  plain-language surface for the network streaming limits: the remote (WAN) bitrate cap,
  the optional home (LAN) bitrate cap, and a new **remote resolution limit**
  (`RemoteMaxResolution`, default: no limit) that applies only to streams from outside
  the home network. The raw settings no longer appear as generic entries in the Streaming
  group, so each knob exists exactly once. The card's help text spells out the caveat
  that VPN/Tailscale (CGNAT) clients count as home-network. Shipped defaults are
  unchanged (20 Mbps remote / unlimited LAN).
- **Per-user remote bitrate and resolution limits.** The user editor's streaming modal now
  sets three limits per account: max bitrate, max *remote* bitrate (applies only off-LAN
  and beats the base cap there), and a max resolution. Semantics are override-wins and now
  stated in the UI: a set limit *replaces* the server's network caps for that account —
  including allowing more than the server-wide remote cap ("this user's personal limit").
  Enforced at plan time, on plan-less transcode requests, and on the direct `/stream`
  endpoint (which also gained a resolution gate).
- **Every quality clamp now names its winner.** The player's "Why is this playing this
  way?" panel reports exactly which limit bound a stream, in plain localized language:
  your account's limit, your account's remote limit, the server's home/remote network
  limits, your own Data Saver mode, your session quality pick, the server's conversion
  ceiling — or "the file is smaller than what you asked for" when nothing limited it.
  The player debug panel shows the full structured decision chain, and the admin
  "Now Playing" card marks capped sessions with the same reason code as a tooltip.
- **Duplicate Versions card on the admin Settings page** — lists every movie and episode
  that exists as more than one file (quality/language variants or accidental copies) with
  per-copy quality label, size, and watched state. Copies are now tracked as *versions of
  one title*: the server groups them automatically at scan time (and back-fills existing
  libraries at startup), detail responses list each version with its quality label, and a
  "Not a duplicate" action permanently separates false matches — the correction survives
  rescans.
- **One card per title, everywhere.** Library grids, the episode list, search results,
  Continue Watching, the Most Watched row and post-play suggestions now show a single
  entry per movie/episode even when several files exist — fronted by the best copy
  (your "preferred version" pick wins, else highest resolution → HDR → bitrate →
  newest). Watched state, ratings, favorites and watchlist entries apply to the title:
  set on any copy, they cover every copy, and existing libraries are reconciled
  automatically at startup.
- **Versions on the detail page.** When a title exists as multiple files, the Play
  button becomes a split control: Play starts the default copy, and a chevron beside it
  opens a menu listing every copy with quality label, container, size, and per-copy
  watched state — pick one to play that exact file. Admins can pin a "preferred
  version" from the same menu, which becomes the default everywhere. The quality badge
  in the header stays a simple indicator of the best available copy.
- **Compare versions before playing.** In the technical-specs strip under the genres,
  the "Video:" value is now a version dropdown: choose a copy and the whole panel —
  codec, color depth, frame rate, audio format (including Atmos), bitrate, and the
  audio/subtitle track lists — shows that file's real probed metadata. Pressing Play
  then plays the version you're looking at; the chevron on the Play button remains a
  one-off override. Quality badges across the app now use one server-derived label
  (goodbye FHD/HD/1080p inconsistency), stay visible on cards that have multiple
  versions, and a TV show's header now reports its honest best quality instead of
  whichever episode file the server happened to sample.
- **Switch versions from inside the player.** A new "Version" menu (next to Quality)
  lists the title's copies — pick the 4K file mid-movie and playback continues from the
  same position with the new source. Editions whose runtime differs meaningfully (a
  Director's Cut vs. theatrical) start from the beginning instead of landing mid-scene.
  Play history stays honest across a switch: continuing the same sitting on another copy
  counts as one play, not two. (The menu is hidden while casting — cast sessions keep
  the version they started with.)
- **Cache Usage card on the admin Settings page** — per-area file counts and sizes for
  artwork, cast headshots, thumbnails, the image proxy, trickplay previews, and extracted
  subtitles, so cache growth is visible at a glance (a multi-gigabyte orphaned-trickplay
  pile previously accumulated invisibly). Cleanup runs daily and can be triggered from the
  Background Tasks card.
- **Device column in the admin "Now Playing" card** — each session shows an icon for the client's
  form factor (phone, tablet, TV, cast device, or browser) alongside the address it is streaming
  from. Derived from the User-Agent the client already sends; nothing is stored and no lookup
  leaves your server.
- **"Your most watched" home row** — ranks your full play history by play count (ties broken by
  recency), with binged episodes rolled up to one series card. Honors the library ACL and
  content-rating ceiling like every other row, and self-suppresses below four titles (or when
  history recording is off).
- **"Most Played" and "Recently Played" library sorts** for Movie and TV grids. TV grids
  aggregate episode plays up to the series; never-played items sort last.

### Changed
- **Uncapped transcodes now get a sane bitrate ceiling.** When nothing negotiated a
  bitrate limit (no server/network/per-user cap, no client ask), transcodes previously
  ran CRF-only and could spike far past what any player needs (grainy 4K HDR sources
  especially). A documented per-resolution ladder now supplies a generous CVBR ceiling
  (h264: 2.5/5/9/14/22 Mbps for 480p/720p/1080p/1440p/4K; hevc/av1 at 60%) — CRF remains
  the quality driver and any negotiated cap replaces these defaults outright. Audited
  against Apple HLS authoring guidance and Jellyfin/Plex community practice; not a new
  knob (quality/speed still steer via `TranscodeCRF`/`TranscodePreset`).
- **Stream plans no longer promise HDR they can't deliver.** With `PreserveHDR` on but an
  8-bit h264 output negotiated, the plan used to claim HDR while the encoder tone-mapped
  anyway; the plan now reports SDR up front (and the new pre-play warning names the
  codec as the cause).
- **Stream-plan API additions:** plans now carry the HDR-guardrail facts
  (`toneMapPlanned`, `toneMapPipeline` = `cuda`/`opencl`/`software`, `toneMapIsSoftware`,
  `hardwareAccelerationEnabled`, `hdrTranscodePolicy` = `warn`/`block`), new reason codes
  (`container.unsupported`, `subtitle.burn-in`, `hdr.tonemap.subtitles`,
  `hdr.tonemap.server-policy`, `hdr.tonemap.codec`), and the player debug panel's
  decision block gained `toneMapPipeline`.
- **Stream-plan reason codes (API):** the generic `bitrate.clamped` code is no longer
  emitted; plans now carry one code per clamp winner (`bitrate.user-cap`,
  `bitrate.user-remote-cap`, `bitrate.lan-cap`, `bitrate.wan-cap`, `bitrate.data-saver`)
  plus new resolution/quality codes (`resolution.user-ceiling`, `resolution.remote-ceiling`,
  `resolution.server-ceiling`, `quality.session-override`, `source.is-smaller`). Clients
  rendering unknown codes fall back to the plan's free-form `reason` string, and the web
  app keeps its `bitrate.clamped` translation for older servers.

### Security
- **Artwork now requires authentication.** Posters, backdrops, episode stills, cast
  headshots and playlist covers under `/cache/images` were served to anyone on the
  network; they now require the same reduced-privilege media token the player already
  uses (attached automatically by the web app — nothing changes visually). Casting
  keeps working: the cast screen's poster carries the media token current at load time,
  while the cast token stays locked to its single media item's stream routes. Banning
  or deleting a user now cuts off their artwork access immediately, and the per-request
  eligibility recheck behind all media/cast tokens gained a short-lived cache with
  instant invalidation — removing a database query from every HLS segment fetch.
- **Extracted subtitles and trickplay previews are no longer reachable without signing in.**
  Both were quietly served as static files alongside the images cache; they now answer only
  through the authenticated player endpoints. Artwork stays public — the app loads posters in
  plain image tags.
- **The image proxy's archive.org allowlist matches the downloader's audited one again** —
  the proxy still accepted any `*.archive.org` subdomain, including the Wayback Machine, a
  fetch proxy that could relay arbitrary upstream URLs through an allowlisted host. One shared
  fetch policy now backs both code paths, so they cannot drift apart in the future.
- **The main login token no longer travels in URLs** (WS-6). Query-string authentication on
  media routes now accepts only the reduced-privilege media/cast tokens — a full access token
  in a `?token=`/`?access_token=` query string is rejected (query strings leak into logs,
  proxies, and browser history). Media tokens are additionally restricted to GET/HEAD, so a
  leaked media URL can read content but never mutate anything. The web app hard-depends on
  the media token (brief "Connecting…" gate on cold load), the real-time hub rides it too,
  and all mutating player calls moved to Authorization headers.
- **API-token scopes are now enforced** (previously the `read:library`/`read:state` checkboxes were
  decorative — any valid token could read all catalog metadata and user state). `read:library` gates
  every catalog/content surface (browse, search, images, book pages, streaming/transcoding);
  `read:state` gates playlists, watchlist, continue-watching, preferences, bookmarks and highlights.
  Browser sessions are unaffected (scopes only constrain API tokens). **Breaking for API tokens
  minted before this change:** re-mint with the scopes the integration needs (Settings → API tokens).
- **Per-user bitrate caps now bind direct play and remux**, not just transcodes, with a serve-time
  403 backstop on the raw stream endpoint; a hand-crafted transcode session id can no longer bypass
  the server-wide resolution/codec ceilings; the hero rotation now honours content-rating ceilings.

### Fixed
- **Quality labels now mean the same thing everywhere.** Two label parsers had drifted:
  the plan arbitration only recognized 720p/1080p/4k, while the direct-stream gate also
  knew 480p/1440p/8k — so a 1440p session pick was silently ignored (played uncapped),
  and a resolution ceiling hand-set to "1440p" was enforced on one path but not the
  other. One shared label→height authority (`QualityLabels`) now backs plan
  arbitration, the stream gates, and the new streaming-limits display, so 480p, 1440p
  and 8K picks/ceilings are honored consistently.
- **Downscaling now works for every negotiated resolution.** The transcoder's scale
  filters only recognized the admin-setting labels (720p/1080p/4k); the resolutions
  negotiated per session are numeric strings ("1440p", "2160p"…), so e.g. a 4K quality
  pick on an 8K source produced no downscale at all. One shared label→width map now
  backs the software, CUDA, and OpenCL scale paths (480p through 8K), and the new
  transcode bitrate ladder resolves the same labels.
- **"More from this collection" no longer lists a duplicate copy of the movie you're
  viewing as a separate entry.** Collection strips, collection pages, and the collection
  list all count and show logical films — one entry per title however many files it has,
  and the "now viewing" highlight always points at the copy you actually opened.
- **Widescreen movies no longer show the wrong resolution in the Versions list.**
  Cinemascope encodes store a cropped pixel height (a 2.35:1 film at 1080p is
  ~1920×816), and the version label only looked at height — so a 1080p copy could read
  "720p" while the quality panel above it correctly said 1080p. Version labels (detail
  page, cards, admin duplicates report, DLNA) now weigh width like the quality panel
  does, so the two can't disagree.
- **Duplicate copies of the same movie or episode no longer confuse playback and
  progress.** When two files map to one title (quality/language variants, accidental
  copies): autoplay now advances to the next episode instead of replaying the one just
  finished via its other copy; marking an episode watched (or unwatched) applies to every
  copy; a series with a duplicated episode can actually reach "fully watched" and leave
  Continue Watching; two half-watched copies of one movie occupy a single Continue
  Watching slot (the most recently played wins — finishing either copy retires the
  movie); season episode counts count episodes, not files; intro/credits detection
  analyzes each episode once and shares the result between same-length copies; and DLNA
  listings label duplicate episodes with their quality ("E2 [4K]") instead of showing two
  identical titles.
- **Duplicate copies of the same episode now share the cached still.** When two files map to
  the same episode (quality/language variants, accidental copies), the artwork write-back only
  updated one of the rows — every other copy kept pointing at the provider URL and loaded
  through the image proxy on every view. All rows for an episode now receive the local cached
  path, and re-enrichment no longer briefly flips already-cached episode stills back onto the
  proxy.
- **Deleting a library now removes everything derived from it.** Cast headshots (previously a
  silent no-op — files are keyed by the provider's external person id, but deletion looked up
  internal ids), trickplay previews (which had no deletion path at all), thumbnails, and cached
  subtitle extractions are all cleaned up immediately; a person also credited in another library
  keeps their headshot. Items removed by scans (offline past the retention window) get the same
  immediate cleanup, and a daily sweep reclaims anything left over — including orphaned
  people/genre rows — while media on temporarily disconnected drives keeps its artwork so
  everything heals when the drive returns.
- **Artwork viewed before its download finished no longer leaves a permanent duplicate.** The
  on-demand image proxy's copy is deleted the moment the permanent item-keyed copy takes over,
  and any strays now expire after 30 days of non-use (previously they sat in `cache/images/proxy`
  forever with no way to attribute them).
- **Comic series covers actually download now** — they were queued and then silently dropped,
  leaving the row hot-linking the provider through the image proxy on every view.
- **The track-menu subtitle endpoint reuses cached extractions** instead of re-demuxing the whole
  file with a fresh ffmpeg run on every request (a multi-minute cost on large remuxes).
- **Trickplay generation no longer retries items on disconnected drives every sweep.**
- **"Stop" in the admin dashboard now actually stops the stream.** Killing the session was not
  enough: the player reacted to its segments failing by reloading the playlist, which started a
  brand-new transcode under the same session id — so ffmpeg respawned and playback carried on.
  A stopped session is now refused for a couple of minutes (HTTP 410) and the player halts with
  "Playback was stopped by an administrator" instead of retrying. Pressing Play again still
  works immediately — it is not a lockout.
- **A stopped stream no longer leaves a phantom "Direct Play" row behind**, which made the same
  movie appear twice in "Now Playing" (once as Direct Play, once as Transcode) after the viewer
  pressed Play again. The stopped player keeps sending progress beats while it drains its buffer,
  and those beats were registering the title as a direct play — the check that normally prevents
  that looks for a live transcode, which is exactly what Stop had just removed. For a short window
  after a stop, those beats can no longer conjure a new row, and any stale one is cleared. Genuine
  playback is unaffected: a real direct play announces itself with a stream request, still shows up
  immediately, and keeps its live position.
- **Movies with a non-default audio track selected could never play.** Choosing a specific audio
  track dropped the client's channel limit, so a 5.1/TrueHD track was re-encoded as 6-channel AAC
  even for a stereo-only browser. Chrome cannot decode that, so the player downloaded video
  segments forever without ever starting (and flooded the console with `resume` 404s while it
  retried). The selected track is now capped at what the client can actually play — and never
  upmixed above what the track really contains.
- The player's `pause`/`resume` calls now identify their own session, so they stop 404-ing.
- A movie you had already finished no longer "resumes" in its closing seconds: any position past
  the completion threshold (95%, matching the server) restarts from the beginning.
- The HLS master playlist's subtitle rendition now points at a spec-compliant WebVTT media playlist
  (`subtitles.m3u8`), giving native/iOS HLS players working text subtitles and stopping hls.js from
  mis-parsing the raw `.vtt`; far-seek subtitle alignment no longer emits orphan cue identifiers,
  and switching subtitles off actually removes the previous track's cues.
- Per-library TV search finds episodes by title; comic issues no longer flood genre/description
  search results (title-only, and they open in the reader); the player bar shows the real artist
  for album playback; playing a single track no longer resumes a stale queue when it ends; the
  search dropdown shows a "No results found" state; the brand palette (Tailwind v4 `@theme`) renders
  again — `bg-primary` and friends had been silently dead.

### Licensing & repository hygiene
- **In-app AGPL §13 source link.** The login page now offers "Source code (AGPL-3.0)" to every
  remote user, pre-auth; fork operators serving a modified instance repoint `SOURCE_CODE_URL`
  (`src/SoftMedia.Client/src/constants/source.ts`) at their own repository.
- **Relicensed under AGPL-3.0-or-later.** Added the `LICENSE` file, SPDX metadata to the server
  `.csproj` and client `package.json`, and a `THIRD-PARTY-NOTICES.md` covering all dependencies.
- **De-vendored ffmpeg.** Removed the ~346 MB of committed `ffmpeg.exe`/`ffprobe.exe` (and `.bak`)
  binaries from version control. SoftMedia now **fetches jellyfin-ffmpeg** (the build with the
  `chromaprint` muxer required for intro/credits detection) at setup time via `install_ffmpeg.ps1`
  (Windows) / `install_ffmpeg.sh` (macOS), or `jellyfin-ffmpeg7` (Linux/Docker).
- **Hardened ffmpeg resolution.** `BinaryLocationService` now also checks assembly-relative and
  `/usr/lib/jellyfin-ffmpeg` locations, and warns when falling back to a bare-PATH `ffmpeg` (which
  may lack `chromaprint`). `setup.ps1` verifies the `chromaprint` muxer, not just ffmpeg presence.
- **Contributor terms.** Added `CONTRIBUTING.md`, a relicensing `CLA.md`, the CLA-assistant workflow,
  and `SECURITY.md`.
- **Privacy posture documented.** README now states the no-telemetry stance and the exact set of
  opt-in metadata/cover-art hosts, plus an AGPL §13 source-availability notice.

### Notes / follow-ups
- A full git-history purge of the previously committed binaries is deferred pending the repo's
  public/private status (the binaries remain in history until then).
- Docker packaging (bundling `jellyfin-ffmpeg7` + GPL §6(d) source pointer) is deferred to the
  feature roadmap.
