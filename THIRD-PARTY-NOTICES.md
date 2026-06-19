# Third-Party Notices

SoftMedia is licensed under **AGPL-3.0-or-later** (see [`LICENSE`](LICENSE)). It uses the
third-party components below. This file has two parts:

1. **Managed & npm dependencies** — should be regenerated with `scripts/gen-licenses.ps1`
   (NuGet via `nuget-license`, npm via `license-checker-rseidelsohn`). The list here is the
   verified starting point; the scanner is authoritative for transitive packages.
2. **External binaries & bundled native engines** — hand-maintained, because license scanners
   read package metadata only and cannot see native binaries (FFmpeg, native Skia, the SQLite
   engine).

All listed licenses are compatible with AGPL-3.0-or-later.

---

## 1. Managed dependencies (server, .NET / NuGet)

| Package | License (SPDX) |
|---|---|
| Konscious.Security.Cryptography.Argon2 | MIT |
| MetadataExtractor | Apache-2.0 |
| Microsoft.AspNetCore.Authentication.JwtBearer | MIT |
| Microsoft.AspNetCore.OpenApi | MIT |
| Microsoft.Data.Sqlite | MIT |
| Microsoft.EntityFrameworkCore.Sqlite | MIT |
| Microsoft.EntityFrameworkCore.Design *(build-time only, `PrivateAssets=all`; not redistributed)* | MIT |
| Microsoft.Extensions.Caching.Memory | MIT |
| Otp.NET | MIT |
| PdfPig | Apache-2.0 |
| SharpCompress | MIT |
| SkiaSharp | MIT |
| Swashbuckle.AspNetCore | MIT |
| System.IdentityModel.Tokens.Jwt | MIT |
| System.Text.Json | MIT |
| TagLibSharp | **LGPL-2.1-only** |

**TagLibSharp (LGPL-2.1-only):** referenced dynamically as an unmodified NuGet assembly,
satisfying LGPL-2.1 §6. Combination with AGPL-3.0 is permitted because LGPL-2.1 §3 allows the
library to be taken under GPL-2.0-or-later, which is one-way compatible with (A)GPL-3.0. If
SoftMedia ever ships single-file/AOT-trimmed (static relinking), revisit this notice.

## 1b. Bundled native engines (transitive — verify with `dotnet list package --include-transitive`)

- **SQLite engine** — shipped via `SQLitePCLRaw.bundle_e_sqlite3` / `SQLitePCLRaw.lib.e_sqlite3`
  (**Apache-2.0**). The SQLite C source itself is **public domain**.
- **Skia (native)** — shipped via `SkiaSharp.NativeAssets.Win32` and `SkiaSharp.NativeAssets.macOS`
  (**BSD-3-Clause**). *No `SkiaSharp.NativeAssets.Linux` is currently in the dependency graph; add it
  (and its notice) when Linux/Docker image work lands.* The BSD-3-Clause non-endorsement clause
  applies.

## 2. Client dependencies (React / npm, runtime/bundled only)

| Package | License (SPDX) |
|---|---|
| react, react-dom | MIT |
| react-router-dom | MIT |
| react-i18next, i18next, i18next-browser-languagedetector | MIT |
| react-intersection-observer | MIT |
| @tanstack/react-query, @tanstack/react-virtual | MIT |
| @dnd-kit/core, @dnd-kit/sortable, @dnd-kit/utilities | MIT |
| @microsoft/signalr | MIT |
| axios | MIT |
| clsx | MIT |
| framer-motion | MIT |
| sonner | MIT |
| tailwind-merge | MIT |
| zustand | MIT |
| @xmldom/xmldom *(override 0.8.13)* | MIT |
| react-pdf | MIT |
| pdfjs-dist *(dependency of react-pdf)* | Apache-2.0 |
| hls.js | Apache-2.0 |
| epubjs | BSD-2-Clause |
| lucide-react | ISC |
| qrcode.react | ISC |

---

## 3. External binaries (NOT redistributed by SoftMedia)

### FFmpeg — **jellyfin-ffmpeg**, GPL-3.0-or-later

SoftMedia **requires the [jellyfin-ffmpeg](https://github.com/jellyfin/jellyfin-ffmpeg) build**
(it carries the `chromaprint` muxer used by intro/credits detection, plus the NVENC/QSV/AMF/VAAPI
paths). It is built `--enable-gpl --enable-version3` and is therefore **GPL-3.0-or-later**. It links
the patent-stripped, redistributable **`fdk-aac-free`** fork (Fedora's MPEG-2 AAC-LC-only build) —
**not** the non-free Fraunhofer FDK-AAC. SoftMedia (AGPL-3.0) invokes ffmpeg only as a separate
process (mere aggregation), so SoftMedia's own code is not a derivative work of FFmpeg.

- **This repository ships no ffmpeg binary.** It is fetched at install time from Jellyfin's official
  servers (`repo.jellyfin.org`) by `setup.ps1`/`install_ffmpeg.ps1` (Windows) and `install_ffmpeg.sh`
  (macOS), or apt-installed (`jellyfin-ffmpeg7`) inside the Docker image. Operators obtain it directly
  from upstream; the GPLv3 conveyance/source obligation is Jellyfin's.
- **If a distribution channel ever conveys the binary** (e.g. an official SoftMedia Docker image that
  bundles `jellyfin-ffmpeg7`), that channel must satisfy GPLv3 §6(d): include the full GPL-3.0 (and the
  GPL-2.0 / LGPL-2.1 / LGPL-3.0 texts FFmpeg's `LICENSE.md` references) plus a corresponding-source
  pointer to the exact `github.com/jellyfin/jellyfin-ffmpeg` tag matching the shipped version.

> **Build policy:** SoftMedia only ever distributes/points at the official jellyfin-ffmpeg
> `-gpl` / `fdk-aac-free` build. A stock ffmpeg built with `--enable-nonfree --enable-libfdk-aac`
> (full Fraunhofer FDK-AAC) is **non-redistributable** and must never be hosted or bundled.

---

_Regenerate part 1 with `scripts/gen-licenses.ps1`. Keep part 2 and section 3 in sync by hand when
dependencies or the ffmpeg acquisition strategy change._
