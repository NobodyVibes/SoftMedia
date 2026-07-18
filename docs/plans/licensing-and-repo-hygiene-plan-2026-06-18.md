# SoftMedia — Licensing, IP & Repo-Hygiene Remediation Plan

**Date:** 2026-06-18 (rev. 3 — adds the jellyfin-ffmpeg acquisition design; rev. 2 added the 3-lens verification review)
**Status:** Proposal — awaiting maintainer sign-off (do NOT execute yet)
**Scope:** The licensing / intellectual-property / repo-hygiene issues raised during the charter review (see `docs/plans/feature-implementation-plan-2026-06-16.md` §7), narrowed to what is **actionable against the code that exists today**.
**Verification:** Facts re-confirmed against the working tree and web-verified this session (`git ls-files`, file sizes, `install_ffmpeg.ps1`, jellyfin-ffmpeg distribution channels, GPL/AGPL specifics).

> **Firm requirement (maintainer):** SoftMedia MUST run **jellyfin-ffmpeg** at runtime — it is the only build that carries the `chromaprint` muxer the IntroCredits feature needs (`ffmpeg -f chromaprint`), plus the NVENC/QSV/AMF/VAAPI paths and codecs the transcoder relies on. Generic Gyan.FFmpeg / distro ffmpeg are **disqualified** (no chromaprint). The goal is to STOP committing the binary to git while GUARANTEEING the user ends up running jellyfin-ffmpeg.

---

## 1. What this plan does (and does not) cover

The charter review flagged four "must-fix" items. Verified against the codebase, **only the licensing/IP cluster touches present-day code**; the other three are guardrails for features that do not exist yet.

**IN SCOPE (present-day, actionable now):**
1. Add a project **LICENSE** (no license = "all rights reserved" by default, which contradicts the open-source charter).
2. Add **THIRD-PARTY-NOTICES.md** (required attribution for redistributed dependencies).
3. **De-vendor ffmpeg** — four tracked binaries totaling ~346 MB are committed to git with no license: `ffmpeg-bin/ffmpeg.exe` (82.6 MB), `ffmpeg-bin/ffprobe.exe` (82.5 MB), `ffmpeg.exe.bak` (99.3 MB), `ffprobe.exe.bak` (99.1 MB). They are **jellyfin-ffmpeg 7.1.3, GPL-3.0-or-later** — so SoftMedia is committing a **live GPLv3 redistribution violation today** (no license text, no corresponding-source offer). Replace vendoring with a **fetch** of the official jellyfin-ffmpeg build (see §3.C).
4. Add a **CLA + CONTRIBUTING.md** to preserve the option of a future *optional, proprietary media store* (open-core) without being blocked by third-party contributions.
5. Add the **AGPL §13 in-app source-availability offer** (a network-served app must offer its corresponding source to remote users; none exists today).

**EXPLICITLY OUT OF SCOPE (deferred-by-design — no code exists to fix):**
- OpenSubtitles shared-key concern → no OpenSubtitles/external-subtitle code today (only embedded subtitles work). Guardrail in feature-plan §7.2 for *if/when* Phase 5 is built.
- HEIC default-on / Magick.NET / libheif / libde265 → photos are hard-blocked; no decoder ships. Guardrail in §7.1 for Phase 4.
- HEVC/x265 patent worries on the metadata side → not relevant here.
- Full **git-history purge** of the binaries → see Task H4 (necessity depends on the public/private answer, because the GPLv3 binary lives in history).

## 2. Assumed decisions (CONFIRM before execution)

| # | Decision | Assumed value | Why |
|---|---|---|---|
| D1 | Project license | **AGPL-3.0-or-later** | Matches "anyone may fork, but forks must stay free & open source," and — because SoftMedia is a *server* — closes the hosted-service loophole plain GPL leaves open (AGPL §13). `-or-later` keeps GPL-2.0-or-later deps (x264/x265) compatible. **Note:** the media-server norm is GPL-2.0 (Jellyfin/Kodi); AGPL is a deliberate divergence for the network-loophole reason. AGPLv3 ↔ jellyfin-ffmpeg GPLv3 are mutually compatible, and shelling out to ffmpeg is mere aggregation regardless. |
| D2 | ffmpeg handling | **Remove all 4 binaries from git; FETCH official jellyfin-ffmpeg at install (Win/macOS) or apt-install `jellyfin-ffmpeg7` at build (Docker)** | jellyfin-ffmpeg is officially published as portable builds for **Windows** (`.zip`) **and macOS** (`.tar.xz`, arm64 + x86_64) on `repo.jellyfin.org`, plus the **`jellyfin-ffmpeg7`** apt package for Debian/Ubuntu — all carry `--enable-chromaprint`. **Correction (rev.3):** the earlier "libfdk-aac is non-redistributable" claim was **WRONG** — the official build is `--enable-gpl --enable-version3 --enable-libfdk-aac` **without `--enable-nonfree`**, because it links Fedora's patent-stripped `fdk-aac-free` fork (jellyfin-ffmpeg PR #61), so the whole build is **fully GPLv3-redistributable**. Gyan.FFmpeg is disqualified (no chromaprint), so the install gate must verify the **muxer**, not bare presence. |
| D3 | Contributor terms | **CLA (relicensing + patent grant) + CLA-Assistant bot** | A DCO certifies right-to-submit but does **not** grant relicensing rights; a CLA (Apache ICLA/CCLA model) does. Core stays AGPL for all in perpetuity; contributors **retain copyright** (license grant, not assignment); the relicense right is **asymmetric (maintainer-only)** purely to enable an optional separate proprietary component. **Alternative:** DCO-only (drop C2/C3) — friendlier, but forecloses the store. Conscious fork in the road. |
| D4 | History purge | **Tie to public/private (open input)** | The GPLv3 binary is in history; a public repo keeps redistributing it via every historical clone until rewritten (`git filter-repo`/BFG). Private until after de-vendor → H1/H2 suffice. Either way, GPLv3 corresponding-source is owed for any release/Docker image that ever baked the binary in. |

**Open inputs needed from maintainer:** (a) AGPL-3.0 vs GPL-3.0 final call; (b) copyright-holder string for notices; (c) repo currently **private or public** (sets urgency + whether H4 is required); (d) require a CLA, or prefer DCO-only? (e) *(nice)* add a `SECURITY.md` now given the active `security/hardening-wave-2` work, or defer? (f) confirm the **pinned jellyfin-ffmpeg version** — default **7.1.4-3** (current latest 7.x, live-verified 2026-06-18); stay on the **7.x** channel (8.x is pre-release for unreleased Jellyfin 12 — do not auto-track); want the **`-TrackLatest`** pointer-file opt-in in addition to the pin? (g) is a **Dockerfile in scope now** or deferred to feature-plan P1 — and if now, approve **Mode B** (bundle `jellyfin-ffmpeg7` via apt, confirmed redistributable, requires the §6(d) source-pointer work)? (h) since upstream publishes no SHA-256 next to the Windows zip and the rolling channel prunes old point releases (the 7.1.3-1 pin is already 404), OK to (i) record a maintainer-captured SHA-256 in the install script and (ii) optionally mirror a known-good copy in CI to guard against pruning?

---

## 3. Per-item plans

### 3.A LICENSE (AGPL-3.0)
- Create repo-root `LICENSE` with the **verbatim** GNU AGPL-3.0 text (FSF canonical) + a copyright line.
- Add a **License** section to `README.md`. **Encoding caution:** `README.md` is **UTF-16LE with BOM** — append with matching encoding (or convert the whole file to UTF-8) to avoid mojibake.
- Set SPDX metadata: `<PackageLicenseExpression>AGPL-3.0-or-later</PackageLicenseExpression>` in `SoftMedia.Server.csproj` and `"license": "AGPL-3.0-or-later"` in client `package.json` (both currently absent; both forms valid).
- **Optional / deferred:** per-file SPDX headers.
- **Acceptance:** root `LICENSE` exists; README declares it (no corruption); both manifests carry the SPDX expression.

### 3.B THIRD-PARTY-NOTICES.md
- Create repo-root `THIRD-PARTY-NOTICES.md` in **two parts**:
  1. **Managed/npm dependencies — scanner-generated** (authoritative, transitive): NuGet via `nuget-license`/`dotnet-project-licenses --include-transitive`; npm via `license-checker-rseidelsohn --production`. Commit a `scripts/gen-licenses.*` regenerator.
  2. **External binaries & bundled native engines — hand-maintained** (scanners cannot see these).
- Verified inventory:
  - **Server NuGet (MIT unless noted):** Konscious.Security.Cryptography.Argon2; Microsoft.AspNetCore.Authentication.JwtBearer / OpenApi; Microsoft.Data.Sqlite; Microsoft.EntityFrameworkCore.Sqlite (Design = build-time `PrivateAssets=all`, not redistributed); System.Text.Json; Microsoft.Extensions.Caching.Memory; Otp.NET; SharpCompress; Swashbuckle.AspNetCore; System.IdentityModel.Tokens.Jwt; SkiaSharp (**MIT**). **Apache-2.0:** MetadataExtractor; PdfPig. **LGPL-2.1-only:** TagLibSharp (see N3).
  - **Bundled native engines (transitive — MANUAL):** native **SQLite** via `SQLitePCLRaw.bundle_e_sqlite3`/`.lib.e_sqlite3` 2.1.6 (**Apache-2.0**; SQLite C source itself **public domain**). Native **Skia** (**BSD-3-Clause**) via `SkiaSharp.NativeAssets.Win32` + `.macOS` 3.119.2 — **no `SkiaSharp.NativeAssets.Linux` in the graph today** (relevant for the future Linux/Docker + Photos work). Reproduce the BSD-3-Clause non-endorsement notice + SQLite public-domain text.
  - **Client npm (MIT unless noted):** react, react-dom, react-router-dom, react-i18next, react-intersection-observer, @tanstack/react-query + react-virtual, @dnd-kit/*, @microsoft/signalr, axios, clsx, framer-motion, i18next + browser-languagedetector, sonner, tailwind-merge, zustand, @xmldom/xmldom (override 0.8.13), **react-pdf** (MIT — *depends on* **`pdfjs-dist` = Apache-2.0**, not bundled). **Apache-2.0:** hls.js. **BSD-2-Clause:** epubjs. **ISC:** lucide-react; qrcode.react.
  - **External binary — jellyfin-ffmpeg (MANUAL):** **GPL-3.0-or-later** (`--enable-gpl --enable-version3`). It bundles FFmpeg (GPL/LGPL) and the **redistributable `fdk-aac-free`** (Fedora's MPEG-2 AAC-LC-only, patent-expired fork — **NOT** the non-free Fraunhofer FDK-AAC; correct the earlier "non-redistributable" wording). For **Mode A/C** (Win/macOS / operator-installed) this is a *courtesy* entry — the operator fetches direct from upstream, so the §6 conveyance duty is Jellyfin's. For **Mode B** (a Docker image we ship) it is a **hard GPLv3 §5/§6 requirement**: include full GPLv3 (+ the GPLv2/LGPL-2.1/LGPL-3 texts FFmpeg's `LICENSE.md` references) and a **§6(d) corresponding-source pointer** to the exact `github.com/jellyfin/jellyfin-ffmpeg` tag matching the pinned version. **Build-policy line:** only ever distribute the official jellyfin-ffmpeg `-gpl`/`fdk-aac-free` build; a stock `--enable-nonfree --enable-libfdk-aac` build must **never** be hosted or bundled (that one is genuinely unredistributable).
- **N3 — TagLibSharp (LGPL-2.1-only) basis:** "LGPL-2.1-**only**; dynamically referenced and unmodified, satisfying LGPLv2.1 §6. Combining with AGPLv3 is permitted because LGPLv2.1 §3 allows the library to be taken under GPLv2-or-later, one-way compatible with (A)GPLv3." (Do not write `-or-later`.)
- **Acceptance:** part 1 regenerable via the committed script; part 2 (jellyfin-ffmpeg + native Skia BSD-3 + SQLite public-domain) present and hand-curated; **compatibility gate** (N2) green.

### 3.C De-vendor ffmpeg → fetch jellyfin-ffmpeg
- **Untrack all four binaries:** `git rm --cached src/SoftMedia.Server/ffmpeg-bin/ffmpeg.exe src/SoftMedia.Server/ffmpeg-bin/ffprobe.exe src/SoftMedia.Server/ffmpeg.exe.bak src/SoftMedia.Server/ffprobe.exe.bak` (keeps local copies).
- **Remove the build wiring:** delete the **entire `<ItemGroup>` (csproj lines 46-50)**, not just the inner `<Content>`.
- **Ignore going forward** (anchored): add `/src/SoftMedia.Server/ffmpeg-bin/`, `/src/SoftMedia.Server/ffmpeg.exe.bak`, `/src/SoftMedia.Server/ffprobe.exe.bak`, and a defensive `*.exe.bak` rule.
- **Rewrite `install_ffmpeg.ps1` into the sanctioned Windows fetch (Mode A):** make it `$PSScriptRoot`-relative (`$TargetDir = Join-Path $PSScriptRoot 'ffmpeg-bin'`; drop the hardcoded `C:\Users\Admin\…` path); version-pinned (`$Version = '7.1.4-3'`, URL `https://repo.jellyfin.org/files/ffmpeg/windows/latest-7.x/win64/jellyfin-ffmpeg_${Version}_portable_win64-clang-gpl.zip`); add a **`-TrackLatest`** switch that reads `…/win64/win64-clang-gpl.txt` (a ~53-byte pointer file) to discover the current filename (future-proof against the rolling channel); record a maintainer-captured **SHA-256** and verify post-download; idempotent (skip if installed at the pinned version). Stay on the 7.x channel (no 8.x).
- **Add `install_ffmpeg.sh` for macOS (Mode A):** detect arch (`uname -m` → arm64/x86_64), download `…/macos/latest-7.x/<arm64|x86_64>/jellyfin-ffmpeg_${Version}_portable_mac<arm64|x86_64>-gpl.tar.xz` (note `.tar.xz`, arch token `macarm64`/`macx86_64`), `tar -xJf`, move into `ffmpeg-bin/`, `chmod +x`, same chromaprint verification. **Forbid `brew install ffmpeg`** (core Homebrew lacks chromaprint). macOS jellyfin-ffmpeg disables NVENC/AMF (VideoToolbox is the path) but chromaprint IS present → IntroCredits works.
- **Fix `setup.ps1` (chromaprint-critical):** call `& (Join-Path $ServerPath 'install_ffmpeg.ps1')` instead of `Install-WithWinget 'Gyan.FFmpeg'`. Replace the `Test-FFmpeg` gate (`Test-CommandExists "ffmpeg"`, presence-only) with: `ffmpeg-bin/ffmpeg.exe` exists **AND** `ffmpeg -version` contains `--enable-chromaprint` **AND** `ffmpeg -hide_banner -muxers` lists `chromaprint` (PowerShell: `Select-String`).
- **Harden runtime resolution (do BOTH):** (1) set config via env so the **first** `BinaryLocationService` branch hits — Docker `FFmpeg__Path=/usr/lib/jellyfin-ffmpeg/ffmpeg` + `FFmpeg__ProbePath=/usr/lib/jellyfin-ffmpeg/ffprobe`; Windows `FFmpeg:Path` → the `ffmpeg-bin` target (double-underscore env binds to `FFmpeg:Path`). (2) Harden `BinaryLocationService`: add `/usr/lib/jellyfin-ffmpeg/ffmpeg(+ffprobe)` to the Linux candidate list, add `AppContext.BaseDirectory`-anchored candidates beside the CWD ones, and **downgrade the bare-`"ffmpeg"` PATH fallback to a last resort that logs a clear WARNING** (it can silently resolve a chromaprint-less distro ffmpeg and break IntroCredits with a non-obvious error).
- **Docker (forward-looking, feature-plan P1; Mode B is clean):** do **not** `apt-get` a generic ffmpeg. Add Jellyfin's apt repo (`jellyfin_team.gpg.key` → keyring; deb822 `jellyfin.sources` with `URIs https://repo.jellyfin.org/debian`, `Suites=` **derived from the base-image codename** (e.g. trixie — don't hardcode), `Components main`, `Architectures=$(dpkg --print-architecture)`, `Signed-By`), then `apt-get install --no-install-recommends jellyfin-ffmpeg7` (pin `7.1.*` or exact via `apt-cache madison` + a renovate bump, since the repo prunes old point releases). Set the `FFmpeg__Path`/`FFmpeg__ProbePath` env above. **Skip `mesa-va-drivers`/`intel-media-va-driver`** — `jellyfin-ffmpeg7` bundles its own VAAPI/QSV/Mesa drivers under `/usr/lib/jellyfin-ffmpeg/lib/dri`. arm64 first-class via `buildx --platform linux/amd64,linux/arm64`. Intel OpenCL tonemapping OPTIONAL for v1. Add §6(d) compliance: ship the GPL/LGPL license texts + a `CORRESPONDING_SOURCE` pointer (and `org.opencontainers.image.source` label) to the exact jellyfin-ffmpeg tag.
- **Acceptance:** `git ls-files | grep -iE 'ffmpeg|ffprobe'` returns **only `.cs` source**; a fresh framework-dependent `dotnet publish` succeeds; a clean-box `setup.ps1` yields a **chromaprint-enabled** ffmpeg and a working transcode **and** a real `ffmpeg -f chromaprint` fingerprint; new-clone working tree drops ~346 MB (history unchanged until H4).

### 3.D CLA + CONTRIBUTING.md (open-core enablement)
- `CONTRIBUTING.md`: build/test, AGPL-3.0, the back-to-front + layering + a11y conventions, and a **transparent** statement (core stays AGPL for all in perpetuity; contributors retain copyright; the CLA's relicense right is asymmetric, purely to enable an optional separate proprietary component).
- `CLA.md` (Individual + Entity, Apache ICLA/CCLA model) — perpetual, irrevocable copyright **and patent** license **incl. the right to relicense/sublicense**.
- Wire **CLA Assistant**; test on a throwaway PR.
- **Acceptance:** test PR triggers the CLA check; CONTRIBUTING explains the why plainly; the CLA grants relicensing rights (not just DCO).

### 3.E AGPL §13 source offer + README/charter consistency
- **AGPL §13:** add a "Source code (AGPL §13)" link in the running app (`/about`/Settings link, or a small `/api/source-offer` endpoint); document that fork operators serving a **modified** networked version must repoint it to **their** corresponding source. (Unmodified self-host has no §13 duty, but offering the link unconditionally is the safe convention.)
- **README network-egress note:** SoftMedia makes **no** telemetry/analytics/phone-home calls; the only outbound traffic is to the configured, opt-in providers — **Wikidata/Wikimedia SPARQL (`query.wikidata.org/sparql`), TVMaze, OMDb, MusicBrainz + Cover Art Archive (`coverartarchive.org`), Open Library + its covers CDN (`covers.openlibrary.org`)**.
- **Charter housekeeping:** append a "Licensing & Repo Hygiene (June 2026)" entry to `.docs/project_checklist.md`; create root `CHANGELOG.md` (relicense = first entry); banner `docs/reports/feature-gap-analysis-2026-05-07.md` superseded.
- **Acceptance:** in-app §13 link present; README egress note accurate (correct encoding); checklist + CHANGELOG updated; stale report bannered.

---

## 4. Task checklist (grouped; dependency-ordered)

### Group L — License foundation (gates N/D/E wording)
- [ ] **L1** Confirm AGPL-3.0-or-later vs GPL-3.0 + copyright-holder string. _(maintainer input)_
- [ ] **L2** Add root `LICENSE` (verbatim AGPL-3.0). _AC: present, unmodified._
- [ ] **L3** SPDX expression in `SoftMedia.Server.csproj` + client `package.json`. _AC: both declare it._
- [ ] **L4** README **License** section (preserve UTF-16LE/BOM). _AC: states license + meaning; no mojibake._

### Group N — Third-party notices
- [ ] **N1** `scripts/gen-licenses.*` (nuget-license `--include-transitive` + license-checker `--production`). _AC: regenerates part 1._
- [ ] **N2** Generate & commit `THIRD-PARTY-NOTICES.md` part 1; **build gate** fails on any license not in an AGPL-3.0-compatible allowlist (MIT, BSD-2/3, ISC, Apache-2.0, LGPL-2.1/3.0 any, (A)GPL-3.0-or-later, GPL-2.0-or-later, public-domain/Unlicense). _AC: valid SPDX ids; gate green._
- [ ] **N3** Hand-author part 2: jellyfin-ffmpeg (GPL-3.0-or-later, redistributable `fdk-aac-free`), Skia BSD-3 non-endorsement, SQLite public-domain, TagLibSharp **LGPL-2.1-only** basis, SQLitePCLRaw/SkiaSharp.NativeAssets provenance, + the "never ship `--enable-nonfree`" build-policy line. _AC: part 2 present and accurate._

### Group H — De-vendor ffmpeg → fetch jellyfin-ffmpeg
- [ ] **H1** `git rm --cached` **all four** binaries; add the four anchored `.gitignore` rules + `*.exe.bak`. _AC: `git ls-files | grep -iE 'ffmpeg|ffprobe'` → only `.cs`._
- [ ] **H2** Remove the **entire `<ItemGroup>` (csproj lines 46-50)**. _AC: publish no longer references ffmpeg-bin; no empty ItemGroup._
- [ ] **H3** Rewrite `install_ffmpeg.ps1` (`$PSScriptRoot`, pin 7.1.4-3, `-TrackLatest` pointer-file, SHA-256, idempotent); add macOS `install_ffmpeg.sh`; repoint `setup.ps1` to it and replace the `Test-FFmpeg` gate with the chromaprint-muxer check; harden `BinaryLocationService` (jellyfin path + `AppContext.BaseDirectory` candidates + warn-on-bare-PATH); set `FFmpeg__Path`/`FFmpeg__ProbePath` env. _AC: clean-box Win **and** macOS yield `ffmpeg` whose `-version` shows `--enable-chromaprint`, `-muxers` lists `chromaprint`, and a real `ffmpeg -f chromaprint` fingerprint succeeds; gate fails loudly if the muxer is absent; Docker resolves via `/usr/lib/jellyfin-ffmpeg/ffmpeg`, never bare-PATH._
- [ ] **H4** _(conditional on public/private)_ If public/pre-public: history purge (`git filter-repo`/BFG) + coordinated force-push. If private until after de-vendor: skip. _AC: history clean OR documented unnecessary; corresponding-source obligation for any shipped release noted._

### Group C — Contributor terms
- [ ] **C1** `CONTRIBUTING.md` (build/test, conventions, transparent CLA rationale). _AC: explains the why plainly._
- [ ] **C2** `CLA.md` (ICLA + CCLA, relicensing + patent grant). _AC: grants relicensing, not just DCO._
- [ ] **C3** Wire CLA Assistant; test PR. _AC: PR blocks until signed._

### Group R — §13 offer + README/charter housekeeping
- [x] **R1** ✅ DONE 2026-07-18 — "Source code (AGPL-3.0)" link on the LOGIN page (pre-auth, so the offer reaches every remote user — stronger than the `/about` option). URL lives in `src/SoftMedia.Client/src/constants/source.ts` (`SOURCE_CODE_URL`) with the fork-operator repoint note in its doc comment. _AC met: running app exposes a source link._
- [ ] **R2** README egress note with the corrected host list. _AC: present + accurate._
- [ ] **R3** `.docs/project_checklist.md` entry; root `CHANGELOG.md` (relicense first); banner the May-2026 report superseded. _AC: all three updated._

---

## 5. Risks & sequencing notes
- **Live GPLv3 violation now:** the tracked jellyfin-ffmpeg binary makes this a present, not hypothetical, issue — H1/H2 are the priority. Any release/Docker image that ships the binary owes the full GPLv3 text + a §6(d) corresponding-source pointer.
- **License irreversibility:** once published under AGPL, that release stays AGPL forever (you keep the right to relicense *future* work via the CLA, but cannot un-publish). Make L1 deliberate. *(Not legal advice — a one-time lawyer pass on LICENSE + CLA is worth it before any revenue.)*
- **Open-core trust tradeoff:** AGPL + a maintainer-only relicensing CLA is the "reserving the right to go proprietary on community work" pattern; mitigate with transparent CONTRIBUTING framing, or choose DCO-only (D3 alternative).
- **History purge (H4) is disruptive and correctness-relevant:** force-push invalidates clones/forks, but the GPL binary is *in history*, so a public repo keeps redistributing it until rewritten. Decide via the public/private input.
- **Rolling-channel drift vs reproducibility:** `latest-7.x` keeps only the current build (the committed script's 7.1.3-1 pin is already 404). Keep the pin current here, record its SHA-256, and rely on the `win64-clang-gpl.txt` pointer (`-TrackLatest`) as the future-proof fallback; consider mirroring a known-good copy in CI. Do NOT auto-grab the newest version (8.x is pre-release for unreleased Jellyfin 12).
- **Bare-PATH fallback is a silent trap on Linux/Docker** (jellyfin-ffmpeg is not on PATH); set `FFmpeg__Path` and harden the resolver — otherwise it can pick up a chromaprint-less distro ffmpeg and break IntroCredits.
- **chromaprint regression risk:** de-vendoring to the wrong build silently breaks intro/credits detection — H3's chromaprint check is not optional.
- **AGPL ↔ dependency compatibility:** all current deps are permissive/Apache-2.0/LGPL/`-or-later` GPL — all AGPL-3.0-compatible. N2's gate must catch any future incompatible license (EPL/MPL-incompatible/CDDL/CC-BY-SA/BUSL/SSPL/wrong-version `-only` GPL).
- **macOS hw-accel:** the macOS jellyfin-ffmpeg disables NVENC/AMF (VideoToolbox only); confirm no code assumes NVENC/QSV/AMF cross-platform (chromaprint is present, so IntroCredits is unaffected).
- **Independent of the feature plan:** Groups L/N/H/C/R can land as their own small PR before Phase 0; nothing here blocks or is blocked by the 6-feature plan.

---

_Companion to `docs/plans/feature-implementation-plan-2026-06-16.md` (§7). Rev. 3 folds in a web-verified jellyfin-ffmpeg acquisition design (Windows/macOS portable fetch + Docker `jellyfin-ffmpeg7` apt) and corrects the rev.2 libfdk-aac error (the official `fdk-aac-free` build is GPLv3-redistributable). Rev. 2 incorporated a 3-lens verification review (binary count 2→4, ~165→~346 MB; ffmpeg provenance jellyfin-ffmpeg GPL-3.0; TagLibSharp LGPL-2.1-only; egress host list; AGPL §13; scanner blind-spots)._
