# SoftMedia Software Design Document (SDD)

**Version:** 1.0.0
**Status:** Draft
**Date:** 2025-11-26

---

## 1. Introduction

SoftMedia is a self-hosted, privacy-focused media server designed for the modern hobbyist. It aims to provide a premium, user-friendly experience on consumer hardware (Windows 11 & Linux) without relying on cloud dependencies.

### 1.1 Core Philosophy
- **Local-First:** All metadata and media are stored locally.
- **Privacy:** No user tracking, no mandatory cloud accounts.
- **Simplicity:** Single-binary deployment where possible; easy setup.
- **Aesthetics:** High-quality, modern "Dark Mode" UI with a signature Bright Blue to Violet gradient.

---

## 2. Technology Stack

### 2.1 Frontend (User Interface)
**Selection:** **React (TypeScript) + Vite + Tailwind CSS**

*   **Justification:**
    *   **React:** The industry standard for building dynamic, component-based interfaces. Its ecosystem allows for rapid development of complex features like the "Card-based" media grid.
    *   **TypeScript:** Ensures type safety, reducing bugs and improving maintainability for a long-term project.
    *   **Vite:** Extremely fast build tool and dev server, providing a superior developer experience compared to Create React App. Lightweight and optimized for modern browsers.
    *   **Tailwind CSS:** A utility-first CSS framework that makes implementing the specific "Blue-Violet" branding and dark theme efficient. It allows for easy responsive design (Mobile/Desktop) without fighting pre-built component styles.
    *   **Framer Motion:** For the "dynamic animations" and "micro-interactions" required to give the app a premium feel.

### 2.2 Backend (Server & Logic)
**Selection:** **C# (.NET 8)**

*   **Justification:**
    *   **Proven Reliability:** C# is the language of choice for major media servers like Jellyfin and Emby, providing a battle-tested foundation for media handling.
    *   **Performance:** .NET 8 offers exceptional performance, rivaling native languages, with significant improvements in memory management and async I/O compared to older versions.
    *   **Ecosystem:** Extensive libraries for media processing, metadata fetching, and system integration.
    *   **Cross-Platform:** .NET 8 runs natively on Windows, Linux, and macOS.

### 2.3 Database
**Selection:** **SQLite**

*   **Justification:**
    *   **Zero Configuration:** Serverless and file-based. The database is just a file (`softmedia.db`) sitting next to the executable.
    *   **Portability:** Easy to backup (just copy the file).
    *   **Performance:** With WAL (Write-Ahead Logging) mode enabled, SQLite is incredibly fast for read-heavy workloads like media browsing.
    *   **Integration:** Entity Framework Core (EF Core) provides excellent support for SQLite.

### 2.4 Media Processing
**Selection:** **FFmpeg**

*   **Justification:** The industry standard for handling video/audio. SoftMedia will shell out to a bundled or system-installed FFmpeg for:
    *   Generating thumbnails/posters.
    *   Extracting technical metadata (resolution, codec, duration).
    *   **Subtitle Extraction:** extracting embedded subtitles (SRT, PGS, ASS) from containers like MKV.
    *   **Transcoding:** Real-time conversion of video/audio to ensure playback on any device (Web, Mobile, TV).
    *   **Logic:** Server checks client capabilities (Codec/Container support) to decide between Direct Play, Direct Stream, or Full Transcode.
    *   **Format Support:** SoftMedia aims for universal compatibility, supporting any container/codec readable by FFmpeg (MKV, AVI, WMV, ISO, HEVC, VP9, AV1, DTS, TrueHD, etc.).

---

## 3. System Architecture

### 3.1 High-Level Overview

```mermaid
graph TD
    User[User (Browser/App)] -->|HTTPS/HTTP| WebServer[ASP.NET Core Web API]
    WebServer -->|Serves| Frontend[React SPA]
    WebServer -->|API Requests| API[REST API Controllers]
    
    subgraph Backend Services
        API --> Auth[Auth Service]
        API --> Library[Library Service]
        API --> Stream[Streaming Service]
        
        Stream --> VideoLogic[Video (Range/Transcode)]
        Stream --> AudioLogic[Audio (Range/Seek)]
        Stream --> BookLogic[Books (Static/Page API)]
        
        Library --> DB[(SQLite Database)]
        Library --> Watcher[File Watcher (FileSystemWatcher)]
        Library --> Meta[Metadata Fetcher]
        
        Meta --> ExtAPI[External APIs (TVMaze/Wikidata)]
        Watcher -->|Scans| Storage[Local Media Storage]
        Stream -->|Reads| Storage
    end
```

### 3.2 Component Breakdown

1.  **Frontend (SPA):**
    *   Communicates with Backend via REST API.
    *   Handles routing (React Router).
    *   Manages state (TanStack Query for server state, Zustand for UI state).
    *   **Theme Engine:** Uses CSS variables to apply the "Bright Blue (#007AFF) to Violet (#8A2BE2)" gradient across headers, active states, and accents.

2.  **Backend API:**
    *   **Framework:** `ASP.NET Core Web API`.
    *   **Auth:** JWT (JSON Web Tokens) stored in HTTP-only cookies for secure session management.
    *   **API Structure:** RESTful Controllers.
        *   `GET /api/v1/movies`
        *   `GET /api/v1/stream/{id}`
        *   `POST /api/v1/auth/login`

3.  **Metadata System:**
    *   **Trigger:** File Watcher detects new file -> Adds to Queue -> Background Service processes file.
    *   **Sources (Keyless):**
        *   **TV Shows:** TV Maze API (Public).
        *   **Movies/General:** Wikidata (SPARQL queries) or OpenMovieDatabase (if available).
        *   **Music:** MusicBrainz API.
    *   **Routing Strategy:** Strictly routes requests based on `Library.Type`.
        *   *Movies* -> Wikidata / OMDb
        *   *TV Shows* -> TVMaze
        *   *Music* -> MusicBrainz
        *   *Books* -> Open Library
        *   *Games* -> Wikidata
    *   **Fallback:** Parse filename (Regex) and read embedded ID3/MKV tags.

4.  **File Watcher:**
    *   Uses `.NET FileSystemWatcher` to listen for OS file system events (Create, Remove, Rename) in real-time.

---

## 4. Detailed Implementation Requirements

### 4.1 Database Schema (Simplified)

*   **Users**
    *   `ID` (Guid)
    *   `Username` (String, Unique)
    *   `PasswordHash` (String, Argon2id)
    *   `Role` (Enum: Admin, User)
    *   `ParentID` (Guid, Nullable - for Child accounts)
    *   `MaxRating` (String - e.g., "PG-13")

*   **Libraries**
    *   `ID` (Guid)
    *   `Name` (String - e.g., "Dad's Movies")
    *   `Type` (Enum: Movie, TV, Music, etc.)
    *   `Paths` (JSON Array - list of folders)

*   **MediaItems**
    *   **Core Fields (All Types):**
        *   `ID` (Guid)
        *   `LibraryID` (FK)
        *   `Title` (String)
        *   `SortTitle` (String - e.g., "Matrix, The")
        *   `Path` (String)
        *   `Size` (Int64)
        *   `DateAdded` (DateTime)
        *   `DateModified` (DateTime)
        *   `IsFavorite` (Boolean)
        *   `PlayCount` (Int)
        *   `LastPlayed` (DateTime)

    *   **Technical Metadata (File Info):**
        *   `Container` (MKV, MP4, AVI, MOV, WMV, FLV, ISO, MP3, FLAC, ALAC, OGG, M4A, CBZ, CBR, PDF, etc.)
        *   `VideoCodec` (H.264, HEVC/H.265, AV1, VP9, MPEG-2, VC-1, ProRes, DNxHR)
        *   `AudioCodec` (AAC, AC3, E-AC3, DTS, DTS-HD MA, TrueHD, FLAC, MP3, OPUS, PCM)
        *   `Resolution` (4K, 1080p, 720p)
        *   `Bitrate` (Kbps)
        *   `Duration` (Seconds)
        *   `FrameRate` (FPS)
        *   `AudioChannels` (2.0, 5.1, 7.1, Atmos)
        *   `HDR` (None, HDR10, Dolby Vision)

    *   **Type-Specific Metadata (Source-Verified):**
        *   **Movies (Source: Wikidata):**
            *   `OriginalTitle` (P1476)
            *   `Overview/Plot` (P973 - Summary)
            *   `ProductionCompany` (P272)
            *   `IMDB_ID` (P345), `RottenTomatoes_ID` (P1258)
            *   `ContentRating` (P1657 - MPAA)
            *   `Director` (P57), `Cast` (P161 - Top billed)
            *   `ReleaseDate` (P577)
            *   `Genre` (P136)

        *   **TV Shows (Source: TVMaze Public API):**
            *   `Network`, `Status` (Running, Ended)
            *   `Premiered` (AirDate)
            *   `Summary` (HTML/Text)
            *   `Type` (Scripted, Reality)
            *   `Language`
            *   **Episode Level:** `Season`, `Number`, `Name`, `Airdate`, `Summary`, `GuestCast`

        *   **Music (Source: MusicBrainz):**
            *   `Artist`, `AlbumArtist`
            *   `ReleaseGroup` (Album Title)
            *   `Date` (Release Date)
            *   `Tags` (Used for Genre)
            *   `TrackTitle`, `Duration`

        *   **Books (Source: Open Library):**
            *   `Title`, `Subtitle`
            *   `Authors`
            *   `PublishDate`
            *   `Publishers`
            *   `ISBN_10`, `ISBN_13`
            *   `PageCount`
            *   `Subjects` (Genres)

        *   **Games (Source: Wikidata):**
            *   `Platform` (P400)
            *   `Developer` (P178), `Publisher` (P123)
            *   `PublicationDate` (P577)
            *   `Genre` (P136)
            *   `GameMode` (P404 - Single/Multiplayer)

        *   **Photos (Source: Local EXIF) — *Phase 2 (post-1.0)*:**
            *   `CameraModel`, `FNumber`, `ISO`, `ExposureTime`
            *   `DateTimeOriginal`
            *   `GPSLatitude`, `GPSLongitude`

### 4.2 Authentication & Roles
*   **Hashing:** Use **Argon2id** for password hashing (via `Konscious.Security.Cryptography` or similar).
*   **Flow:**
    1.  User POSTs credentials.
    2.  Server validates and issues a short-lived **Access Token** (JWT, default ≤15 min) and a long-lived **Refresh Token** (HttpOnly, SameSite=Lax, Path-scoped to `/api/v1/auth/`).
    3.  Refresh tokens rotate on every `/auth/refresh-token` call; replay of a previously-rotated token is treated as chain compromise and revokes every active token for the user.
    *   *Note:* `SameSite=Lax` (rather than `Strict`) is sufficient against CSRF for this design because mutating requests carry the JWT in the `Authorization` header (browsers do not auto-attach `Authorization` cross-origin), and the refresh cookie's `Path=/api/v1/auth/` scope means it is not delivered on cross-site sub-resource POSTs to other API routes.
*   **Parental Control:**
    *   Middleware checks `User.Role` and `User.MaxRating` before serving media metadata or streams.
    *   Child accounts cannot see content above their rating.

### 4.3 Data Sources & Compliance
To ensure ethical usage and prevent incorrect API calls, the system **MUST** enforce strict type mapping:

| Library Type | Allowed Provider |
| :--- | :--- |
| **TV Shows** | TVMaze |
| **Movies** | Wikidata, OMDb |
| **Music** | MusicBrainz |
| **Books** | Open Library |
| **Games** | Wikidata |

**Compliance Rules:**
1.  **TVMaze:**
    *   **Rate Limit:** Adhere to "20 calls every 10 seconds" (or current policy).
    *   **Attribution:** Display "Metadata provided by TVMaze" in the UI.
2.  **Wikidata:**
    *   **User-Agent:** Must send a descriptive User-Agent header (e.g., `SoftMedia/1.0 (https://github.com/NobodyVibes/SoftMedia)`).
    *   **Caching:** Aggressively cache results locally to minimize SPARQL endpoint load.
3.  **MusicBrainz:**
    *   **Rate Limit:** Strictly 1 request per second.
    *   **User-Agent:** Required.
4.  **Open Library:**
    *   **Rate Limit:** < 100 requests/5 minutes.
    *   **Identification:** Send proper User-Agent.

### 4.4 Frontend UI/UX
*   **Navigation:** Sidebar (Desktop) / Bottom Bar (Mobile).
*   **Views:**
    *   **Home:** "Continue Watching", "Recently Added".
    *   **Library:** Grid view of posters. Infinite scroll.
    *   **Details:** Large backdrop image, poster, cast list, "Play" button with gradient background.
*   **Player:**
    *   **Video Player:** Custom HTML5 video player built directly on the native `<video>` element with `hls.js` for HLS-segment playback. Implementation lives at `src/SoftMedia.Client/src/components/player/VideoPlayer.tsx` and supports subtitles, quality selection, chapter seeking, scrubber frame previews, intro / credits skip pills, next-episode overlay, debug pipeline overlay, and HDR toasts. Custom UI was chosen over wrapper libraries (e.g. `vidstack`) so the project keeps full control over these features without fighting wrapper-imposed abstractions.
    *   **Audio Player:** Persistent bottom-bar player (global state) allowing playback while browsing. Supports playlists, shuffle, and gapless playback.
    *   **eReader:** Dedicated viewer for PDF (via `react-pdf`) and EPUB/CBZ (via `react-reader` or canvas). Supports "Remember Page" and "Double Page" view.

### 4.5 Streaming & Playback Technical Specs
To ensure a premium experience, the backend must implement specific serving strategies for each media type:

1.  **Video Streaming:**
    *   **Direct Play:** For supported containers (MP4/WebM) and codecs (H.264/AAC), serve via **HTTP Range Requests** (206 Partial Content) to allow instant seeking.
    *   **Transcoding (HLS):** For unsupported formats (MKV/HEVC), use FFmpeg to transcode into **HLS (HTTP Live Streaming)** segments (`.ts`) and serve an `.m3u8` playlist.
        *   *Latency:* Use short segment duration (e.g., 3-6s) for faster seeking.
        *   *Cleanup:* Background service must aggressively clean up old segments to save disk space.

2.  **Audio Streaming:**
    *   **Strategy:** Strictly **HTTP Range Requests**.
    *   **Transcoding:** If the client doesn't support the format (e.g., FLAC on some iOS versions), transcode on-the-fly to **AAC/MP3** pipe.
    *   **Metadata:** Serve embedded cover art via a separate `/api/v1/audio/{id}/cover` endpoint to avoid extracting it during the stream.

3.  **Book/Comic Serving:**
    *   **PDF:** Serve via **HTTP Range Requests** (Byte-Serving) so the browser/viewer can render pages without downloading the full 100MB+ file.
    *   **EPUB:** Unzip into memory or temporary cache and serve individual resource files (HTML/CSS/Images) to the reader.
    *   **CBZ/CBR:** Treat as a ZIP/RAR. API must provide an endpoint to extract and serve a specific image page on demand: `GET /api/v1/books/{id}/page/{pageNumber}`.

4.  **Auth on media stream endpoints:** Stream, transcode, audio, image, book, and SignalR endpoints accept the JWT either via the standard `Authorization` header or as a `?token=`/`?access_token=` query parameter (lifted by `JwtBearerEvents.OnMessageReceived`). Query-string tokens are required for `<video src>`/`<audio src>`/`<img src>` elements but their operational risk (proxy logs, browser history) is documented in §6.2. Endpoints MUST carry `[Authorize]` so the standard bearer middleware fully validates the signature, lifetime, issuer, and audience — bespoke `JwtSecurityTokenHandler.ReadJwtToken` checks are forbidden because they only decode and do not verify.

---

## 5. Prerequisites & Installation

### 5.1 For the End User (Hobbyist)
To run SoftMedia, the user needs:
1.  **OS:** Windows 10/11 or Linux (Ubuntu/Mint/Debian).
    *   *Note (Linux):* Large libraries may require increasing `fs.inotify.max_user_watches`.
2.  **Hardware:** 
    *   CPU: Intel Core i3 (8th Gen+) or equivalent (for transcoding).
    *   RAM: 4GB minimum.
3.  **Software:**
    *   **SoftMedia Installer** (provided).
    *   **.NET 8 Runtime** (Installer can check/install this).
    *   **FFmpeg** (Installer should offer to download this or use system version).

### 5.2 For Developers (Building from Source)
1.  **.NET 8 SDK**
2.  **Node.js 18+** (for building the frontend assets).

---

## 6. Security Implementation

### 6.1 Secure Remote Access (No Cloud)
Since we do not use a central cloud relay, we recommend two methods:

**Method A: Tailscale (Recommended for Ease)**
*   **What:** A zero-config VPN based on WireGuard.
*   **How:** User installs Tailscale on the Server and their Phone/Laptop.
*   **Result:** Access via `http://softmedia-server:8080` from anywhere safely. No open ports.
*   **Configuration:** Direct Tailscale connections need no extra configuration. If using Tailscale Funnel (proxied HTTPS), configure `ForwardedHeaders:TrustedProxyNetworks` to include the Tailscale CGNAT range (`100.64.0.0/10`) so the per-IP rate limiter sees real client IPs. See `docs/user-docs/reverse-proxy.md`.

**Method B: Reverse Proxy + DuckDNS (Free & Standard)**
*   **Problem:** Most home ISPs change your IP address (Dynamic IP), and buying a domain costs money.
*   **Solution:** **DuckDNS** (Free Dynamic DNS).
*   **How:**
    1.  User gets a free subdomain (e.g., `my-media.duckdns.org`).
    2.  SoftMedia (or Caddy) updates DuckDNS whenever the ISP changes the home IP.
    3.  **Caddy** automatically gets a Let's Encrypt SSL cert for that subdomain.
*   **Result:** Secure access via `https://my-media.duckdns.org`.
*   **Requirement:** Port forwarding (80/443) on the router.
*   **Configuration:** When the reverse proxy runs on the same host as SoftMedia, no extra configuration is required — loopback is trusted by default. When the proxy runs on a different host or inside a container, add the proxy's IP to `ForwardedHeaders:TrustedProxies` in `appsettings.json` so the per-IP rate limiter sees real client IPs. See `docs/user-docs/reverse-proxy.md`.

**Method C: Direct Connection (Legacy/Advanced)**
*   **What:** Opening a port (e.g., 8080) directly to the internet.
*   **Warning:** **NOT RECOMMENDED** without SSL. SoftMedia will support this but warn the user.
*   **Native Apps:** Future native apps will still require one of the above methods (Tailscale, DuckDNS, or Static IP) to locate the server, as SoftMedia does not provide a central "Cloud Relay" service.

### 6.2 Application Security
*   **CSRF Protection:** All mutating requests carry the JWT access token in the `Authorization` header. Browsers do not auto-attach `Authorization` to cross-origin requests, which closes the classic CSRF surface. The refresh-token cookie is `SameSite=Lax` and `Path=/api/v1/auth/`-scoped, so it is not delivered on cross-site sub-resource POSTs. A double-submit cookie would add no enforcement here and is intentionally not implemented.
*   **Rate Limiting:** Login and signup endpoints are per-IP rate limited to defeat credential stuffing.
*   **Sanitization:** All third-party metadata inputs are sanitized before rendering to prevent XSS.
*   **File Access:** The File Watcher and stream layer are strictly jailed to the directories added by the Admin. Path canonicalisation MUST resolve symlinks (e.g., via `FileInfo.ResolveLinkTarget(returnFinalTarget: true)`) in addition to collapsing `..` segments — `Path.GetFullPath` alone is insufficient on Linux, where a symlink inside an admin-declared library root would otherwise re-introduce LFI.
*   **Image Proxy:** Outbound fetches are restricted to a host allow-list, validate `Content-Type` against an image-only set, enforce a streaming size cap, and use a negative-cache sentinel for upstream 404s. All outbound HTTP requests carry a descriptive `User-Agent` per §4.3.
*   **Tokens-in-query operational note:** `?access_token=…` URLs used for `<video>`, `<audio>`, `<img>` and image elements appear in reverse-proxy access logs. Operators running SoftMedia behind nginx/Caddy SHOULD configure log scrubbing of the `access_token`/`token` query parameters. The default access-token TTL is set short (15 min) to bound exposure.
*   **Signup Protection:** Rate limiting and optional CAPTCHA on the signup endpoint to prevent bot account creation.
*   **Parental Controls (enforcement layer):** The middleware/repository filter MUST strip media items above the caller's `User.MaxRating` / `User.ContentRatings` ceiling from list responses *and* return 404 (not 403) for direct stream-by-ID requests targeting blocked items. Items with a null `ContentRating` are treated as restricted (fail-safe) when any ceiling is set; admins bypass the filter entirely.

## 7. Configuration & Settings (Browser Interface)

This section outlines the settings available to the Admin user via the Web UI. Settings are grouped by category as they appear in the navigation tree.

### 7.1 Settings Tree Map
*   **[Server]**
    *   General
    *   Network
*   **[Media Management]**
    *   Libraries
    *   Scanning & Watcher
*   **[Playback]**
    *   Transcoding
    *   Subtitles
*   **[Metadata]**
    *   Data Sources
    *   Images
*   **[Users]**
    *   User List
    *   Invites

### 7.2 Detailed Settings & Defaults

#### **[Server] > General**
*   **Server Name:** Friendly name displayed to clients.
    *   *Default:* "SoftMedia Server"
*   **Language:** UI language preference.
    *   *Default:* "English (US)"
*   **Log Level:** Verbosity of server logs (Error, Info, Debug).
    *   *Default:* "Info"

#### **[Server] > Network**
*   **Local Port:** TCP port for the web interface.
    *   *Default:* `8080`
*   **Enable Remote Access:** Toggle to allow connections outside the local subnet.
    *   *Default:* `False` (Security First)
*   **Secure Connections (HTTPS):** Require SSL for all connections.
    *   *Default:* `True`

#### **[Media Management] > Libraries**
*   **Manage Libraries:** List of active libraries with "Edit" and "Delete" actions.
*   **Add Library:** Wizard to create a new library (Select Type -> Select Folders).

#### **[Media Management] > Scanning & Watcher**
*   **Real-time Monitoring:** Use FileSystemWatcher to detect changes instantly.
    *   *Default:* `True`
*   **Daily Rescan:** Time to perform a full integrity check.
    *   *Default:* `03:00 AM`
*   **Ignore Patterns:** List of file extensions or folder names to skip (e.g., `sample`, `.nfo`).
    *   *Default:* `[]`

#### **[Playback] > Transcoding**
*   **Hardware Acceleration:** Use GPU (NVENC, QuickSync, VAAPI) if available.
    *   *Default:* `False` (Safest for compatibility)
*   **Transcode Thread Count:** Number of CPU threads to dedicate to FFmpeg.
    *   *Default:* `Auto` (Uses all available - 1)
*   **Temporary Path:** Directory for transcoding chunks.
    *   *Default:* `./transcode-temp`

#### **[Playback] > Subtitles**
*   **Auto-Select Audio/Subtitle:** Automatically select tracks based on user language.
    *   *Default:* `True`
*   **Import Embedded Subtitles:** Extract and serve subtitles found inside media files (MKV/MP4).
    *   *Default:* `True`
*   **Import Local Subtitles:** Scan for sidecar files (e.g., `movie.srt`, `movie.en.vtt`) in the media directory.
    *   *Default:* `True`

#### **[Metadata] > Data Sources**
*   **Movie Provider:** Primary API for Movie metadata.
    *   *Default:* `Wikidata`
*   **TV Provider:** Primary API for TV metadata.
    *   *Default:* `TVMaze`
*   **Music Provider:** Primary API for Music metadata.
    *   *Default:* `MusicBrainz`
*   **Books Provider:** Primary API for Book metadata.
    *   *Default:* `Open Library`
*   **Games Provider:** Primary API for Game metadata.
    *   *Default:* `Wikidata`
*   **Auto-Refresh Metadata:** Fetch new data when files are updated.
    *   *Default:* `True`

#### **[Users] > User List**
*   **Allow User Signup:** Allow public registration (requires Admin approval).
    *   *Default:* `False`
*   **Default User Role:** Role assigned to new users.
    *   *Default:* `User` (Restricted)

---

## 8. Universal Client Architecture

SoftMedia adopts a "Universal Client" approach: a single React codebase that adapts to Desktop Browsers, WebOS/TV, and Mobile devices through responsive design and platform-aware components.

### 8.1 Platform Strategy

| Platform | Layout Paradigm | Input Method | Priority |
| :--- | :--- | :--- | :--- |
| **Desktop Browser** | Sidebar + Content Area | Mouse/Keyboard | **Primary** (Build First) |
| **WebOS / Android TV** | Sidebar + Content Area | D-Pad / Remote | **Simultaneous** with Desktop |
| **Mobile (Android/iOS)** | Bottom Tab Bar + Compact Grid | Touch / Swipe | **Sequential** (After Desktop Stable) |

### 8.2 Component Architecture

```
/src/components/
    /layout/
        MainLayout.tsx        # Desktop + WebOS (Sidebar)
        MobileLayout.tsx      # Mobile (Bottom Nav) - Future
    /shared/
        MediaCard.tsx         # Universal (accepts 'size' prop)
        VideoPlayer.tsx       # Universal (100% shared)
        FilterBar.tsx         # Universal
    /mobile/
        MobileNavBar.tsx      # Mobile-specific - Future
```

### 8.3 Multi-Platform Readiness Rules

When building components for Desktop, **always** apply these rules to ensure TV/Accessibility compatibility:

1.  **Semantic Elements:** Use `<button>` for all clickable elements. Avoid `<div onClick>` without `role="button"` and `tabIndex`.
2.  **Focus States:** Always pair hover and focus styles:
    *   *Example:* `hover:bg-white/10 focus-visible:bg-white/10 focus-visible:ring-2 focus-visible:ring-white`
3.  **Keyboard Navigation:** All interactive elements must be reachable via Tab key. Complex grids should support Arrow key navigation.
4.  **Touch Targets (Mobile-Ready):** Ensure minimum touch target of 44x44px for buttons in responsive contexts.

### 8.4 PWA & TV Deployment

*   **PWA (Android/Mobile):**
    *   Requires `vite-plugin-pwa` configuration.
    *   Requires `manifest.json` with app icons and "standalone" display mode.
    *   Enables "Add to Home Screen" functionality.
*   **WebOS (LG TVs):**
    *   Package the built SPA using WebOS CLI tools.
    *   Requires spatial navigation library (e.g., `react-spatial-navigation`).
    *   Map remote keys (Back, Play/Pause, Color buttons) to app actions.

### 8.5 Implementation Sequence

```mermaid
gantt
    title SoftMedia Client Roadmap
    dateFormat  YYYY-MM-DD
    section Phase 1: Desktop + WebOS
    Core Features (Library, Player, Settings) :a1, 2025-01-01, 30d
    Add Focus/Spatial Nav (TV Polish)        :a2, after a1, 5d
    section Phase 2: Mobile
    Mobile Layout Shell                      :b1, after a2, 3d
    Mobile-Specific Features                 :b2, after b1, 7d
```
