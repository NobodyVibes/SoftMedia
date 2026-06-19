# Changelog

All notable changes to SoftMedia are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/), and the project aims to follow semantic versioning
from 1.0 onward.

## [Unreleased]

### Licensing & repository hygiene
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
- An in-app (UI) AGPL §13 "source code" link is still to be added (the repo-level offer is in the
  README for now).
- A full git-history purge of the previously committed binaries is deferred pending the repo's
  public/private status (the binaries remain in history until then).
- Docker packaging (bundling `jellyfin-ffmpeg7` + GPL §6(d) source pointer) is deferred to the
  feature roadmap.
