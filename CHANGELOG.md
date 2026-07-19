# Changelog

All notable changes to SoftMedia are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/), and the project aims to follow semantic versioning
from 1.0 onward.

## [Unreleased]

### Added
- **"Your most watched" home row** — ranks your full play history by play count (ties broken by
  recency), with binged episodes rolled up to one series card. Honors the library ACL and
  content-rating ceiling like every other row, and self-suppresses below four titles (or when
  history recording is off).
- **"Most Played" and "Recently Played" library sorts** for Movie and TV grids. TV grids
  aggregate episode plays up to the series; never-played items sort last.

### Security
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
