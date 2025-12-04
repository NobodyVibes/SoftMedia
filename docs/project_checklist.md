# SoftMedia Project Checklist

## Phase 1: Foundation & Backend Core

### 1.1 Project Initialization
- [x] **Repository Setup**
    - [x] Initialize Git repository (`git init`)
    - [x] Create `.gitignore` (Visual Studio, Node, React, macOS)
    - [x] Create `README.md` with basic project info
- [x] **Solution Setup**
    - [x] Create `SoftMedia.sln`
    - [x] Create ASP.NET Core Web API project (`SoftMedia.Server`)
    - [x] Create React + Vite project (`SoftMedia.Client`)
    - [x] Add projects to Solution
- [x] **Backend Configuration**
    - [x] Configure `appsettings.json` (ConnectionStrings, JWT Settings, RateLimits)
    - [x] Setup Dependency Injection (DI) container in `Program.cs`
    - [x] Configure CORS (Allow Frontend URL)
    - [x] Configure Swagger/OpenAPI (Enable JWT Auth Support)

### 1.2 Database & Data Access
- [x] **EF Core Setup**
    - [x] Install NuGet packages (`Microsoft.EntityFrameworkCore.Sqlite`, `Design`)
    - [x] Create `AppDbContext` class
- [x] **Entities**
    - [x] Define `User` entity (Id, Username, PasswordHash, Role, MaxRating)
    - [x] Define `Library` entity (Id, Name, Path, Type)
    - [x] Define `MediaItem` entity (Core Columns + JSON for Type-Specific Metadata)
- [x] **Migrations**
    - [x] Create Initial Migration
    - [x] Update Database (Create `softmedia.db`)

### 1.3 Authentication System
- [x] **Security Utilities**
    - [x] Install `Konscious.Security.Cryptography.Argon2`
    - [x] Create `PasswordHasher` service
- [x] **Token Management**
    - [x] Install `System.IdentityModel.Tokens.Jwt`
    - [x] Create `TokenService` (Generate Access/Refresh Tokens)
- [x] **API Endpoints**
    - [x] Create `AuthRequest` DTOs (Login/Signup)
    - [x] Create `AuthController` (POST /login, POST /signup)
    - [x] Implement Refresh Token rotation logic (HttpOnly Cookie)
- [x] **Testing**
    - [x] Setup `xUnit` Test Project
    - [x] Write Unit Tests for `PasswordHasher` and `TokenService`

### 1.4 Library Management (Backend)
- [x] **File System**
    - [x] Create `FileScannerService` (Recursive directory scan)
    - [x] Implement **Jailed** `FileSystemWatcher` (Prevent path traversal)
- [x] **Metadata Logic**
    - [x] Create `MetadataService` interface
    - [x] **Implement `MetadataRouter` (Selects provider based on `Library.Type`)**
    - [x] Implement `WikidataProvider` (Movies/Games) with **Caching**
    - [x] Implement `TVMazeProvider` (TV Shows) with **Rate Limiting**
    - [x] Implement `MusicBrainzProvider` (Music) with **Rate Limiting**
    - [x] Implement `OpenLibraryProvider` (Books) with **Rate Limiting**
    - [x] Implement `WikidataGameProvider` (Games)
    - [x] Implement `LocalMetadataProvider` (NFO/Sidecar)
- [x] **FFmpeg Integration**
    - [x] Install FFmpeg wrapper or create `Process` helper
    - [x] Implement `MediaProbe` (Extract Codec/Resolution/Duration)
    - [x] Implement `SubtitleExtractor` (Extract SRT/PGS from MKV)

## Phase 2: Frontend Core & Direct Play

### 2.1 Frontend Foundation
- [x] **Build Setup**
    - [x] Install NPM dependencies (`axios`, `zustand`, `react-router-dom`)
    - [x] Configure Vite proxy for API
- [x] **Styling**
    - [x] Install Tailwind CSS
    - [x] Configure `tailwind.config.js` (Blue-Violet Theme Colors)
    - [x] Create global CSS variables

### 2.2 Authentication UI
- [x] **State Management**
    - [x] Create `useAuthStore` (Zustand)
    - [x] Implement Axios interceptor for Token Refresh
- [x] **Pages**
    - [x] Create `LoginPage`
    - [x] Create `SignupPage`
    - [x] Create `ProtectedRoute` component

### 2.3 Library Browser
- [x] **API Integration**
    - [x] Create `LibraryService` (Frontend)
    - [x] Implement React Query hooks (`useLibraries`, `useMediaItems`)
- [x] **Components**
    - [x] Create `MediaCard` (Poster, Title, Year)
    - [x] Create `LibraryGrid` (Virtual scrolling/Pagination)
    - [x] Create `FilterBar` (Genre, Year, Sort)

### 2.4 Media Playback (Direct Play)
- [x] **Video Player**
    - [x] Install `vidstack` or similar player library
    - [x] Create `VideoPlayer` component (Overlay controls, Subtitles)
    - [x] Connect to Backend Stream Endpoint (Range Requests)
- [x] **Audio Player**
    - [x] Create Global Audio Context (Zustand)
    - [x] Create `PersistentPlayer` component (Bottom bar)
    - [x] Implement Playlist Queue logic
- [x] **eReader (Books/Comics)**
    - [x] Install `react-pdf` and `epubjs` (or similar)
    - [x] Create `BookReader` component (Canvas/Canvas)
    - [x] Implement "Save Progress" logic (Page number)
- [x] **Streaming Backend**
    - [x] Create `StreamController` (Serve Static Files / Range Requests)
    - [x] Implement `MimeTypeResolver` (Correct headers for MP4 vs MP3 vs PDF)

## Phase 3: Advanced Features & Polish

### 3.1 Transcoding System
- [x] **Backend Logic**
    - [x] Create `TranscodeService`
    - [x] Implement FFmpeg command builder (HLS/Dash)
    - [x] Manage temporary transcode segments
    - [x] Implement `TranscodeController` (M3U8 playlists)
- [x] **Frontend Logic**
    - [x] Detect browser capabilities
    - [x] Request Transcode vs Direct Play

### 3.2 Settings & Administration
- [x] **Configuration**
    - [x] Create `SettingsPage` (Tabs: Server, Users, Libraries)
    - [x] Update `SettingsPage` with Music, Book, and Game metadata providers
    - [x] Implement API endpoints for updating `appsettings` or DB config
- [x] **User Management Settings**
    - [x] **Backend API**
        - [x] Create `GET /api/v1/users` endpoint (Admin only, returns user list with ID, Username, Role, MaxRating, CreatedAt)
        - [x] Create `PUT /api/v1/users/{id}/role` endpoint (Admin only, update user role: Admin/User)
        - [x] Create `PUT /api/v1/users/{id}/ban` endpoint (Admin only, soft-delete or set IsBanned flag)
        - [x] Create `DELETE /api/v1/users/{id}` endpoint (Admin only, hard delete user)
        - [x] Create `POST /api/v1/invites` endpoint (Admin only, generate invite code with expiration)
        - [x] Create `GET /api/v1/invites` endpoint (Admin only, list all active invites)
        - [x] Create `DELETE /api/v1/invites/{code}` endpoint (Admin only, revoke invite)
        - [x] Update `POST /api/v1/auth/signup` to validate invite code if signup is restricted
    - [x] **Frontend UI**
        - [x] Create `UserListTable` component (Display users with Username, Role, MaxRating, Actions)
        - [x] Implement "Promote to Admin" / "Demote to User" button with confirmation modal
        - [x] Implement "Ban User" button with confirmation modal
        - [x] Implement "Delete User" button with confirmation modal
        - [x] Create `InviteManager` component (Generate invite, copy to clipboard, list active invites)
        - [x] Display invite expiration time and revoke button
        - [x] Integrate `UserListTable` and `InviteManager` into `SettingsPage` Users tab
        - [x] Remove placeholder message from Users tab
- [x] **Library Management Settings**
    - [x] **Backend API**
        - [x] Create `POST /api/v1/libraries` endpoint (Create library with Name, Type, Paths)
        - [x] Create `PUT /api/v1/libraries/{id}` endpoint (Update library Name, Type, Paths)
        - [x] Create `DELETE /api/v1/libraries/{id}` endpoint (Delete library and optionally cascade delete media items)
        - [x] Create `PUT /api/v1/libraries/reorder` endpoint (Update display order for libraries)
        - [x] Add validation to prevent duplicate library paths
        - [x] Implement path validation (ensure paths exist and are accessible)
        - [x] Create `POST /api/v1/libraries/{id}/scan` endpoint (Trigger manual scan)
    - [x] **Frontend UI**
        - [x] Create `LibraryForm` component (Add/Edit library with Name, Type dropdown, Path selector)
        - [x] Implement file/folder picker or text input with validation for Paths
        - [x] Create `LibraryListTable` component (Display libraries with Name, Type, Paths, Actions)
        - [x] Implement "Edit Library" button opening modal with `LibraryForm`
        - [x] Implement "Delete Library" button with confirmation modal (warn about media item deletion)
        - [x] Implement reordering for libraries (Up/Down buttons)
        - [x] Add "Scan Now" button to trigger immediate library scan
        - [x] Integrate `LibraryListTable` and `LibraryForm` into `SettingsPage` Libraries tab
        - [x] Remove placeholder message from Libraries tab
        - [x] Sync library changes with Sidebar component (invalidate queries)

### 3.3 Enhanced Media Experience & Metadata
- [ ] **Metadata Fetching & Storage (Backend)**
    - [ ] **Core Metadata Logic**
        - [ ] Update `MediaItem` entity to support rich metadata (People, Studios, Tags)
        - [ ] Implement `MetadataAggregator` to merge results from multiple sources (Embedded + API)
    - [ ] **Movie Metadata**
        - [ ] Enhance `WikidataProvider` to fetch Director, Cast, Content Rating (MPAA), Production Company
        - [ ] Implement fallback to embedded metadata (Title, Year) if API fails
    - [ ] **TV Show Metadata**
        - [ ] Enhance `TVMazeProvider` to fetch Network, Status, Genre, Content Rating
        - [ ] Implement Episode-level metadata fetching (Summary, Air Date, Guest Stars)
        - [ ] Implement Season-level grouping logic in API responses
    - [ ] **Music Metadata**
        - [ ] Enhance `MusicBrainzProvider` to fetch Artist Bio, Release Date, Record Label
        - [ ] Implement `TagLib#` integration for robust embedded ID3 tag reading (Track, Disk, Album Art)
        - [ ] Implement Artist -> Album -> Disc -> Track hierarchy in API
    - [ ] **Book Metadata**
        - [ ] Enhance `OpenLibraryProvider` to fetch ISBN, Page Count, Publisher, Subjects
        - [ ] Implement EPUB/PDF metadata extraction (Title, Author)
    - [ ] **Game (ROM) Metadata**
        - [ ] Enhance `WikidataGameProvider` to fetch Platform, Developer, Publisher, Game Mode
        - [ ] Implement multi-disc detection and grouping (e.g., "Game (Disc 1).iso", "Game (Disc 2).iso")
    - [ ] **Photo Metadata**
        - [ ] Implement `ExifReader` service to extract Camera, ISO, F-Stop, GPS, Date Taken
        - [ ] Implement reverse geocoding (optional/future) or simple coordinate storage
- [ ] **Frontend Media Display & Detail Pages**
    - [ ] **Routing & Layout**
        - [ ] Create dynamic routes for media details (`/item/{id}`)
        - [ ] Implement `MediaDetailLayout` (Hero image/Backdrop, Poster, Info Column)
        - [ ] Implement **User Star Rating** component (Interactive 1-5 stars)
    - [ ] **Movie Experience**
        - [ ] Update `MediaCard` to show Year and Content Rating badges
        - [ ] Create `MovieDetailPage` (Display Plot, Cast Grid, Director, Studio, Tech Specs)
    - [ ] **TV Experience**
        - [ ] Create `TVShowDetailPage` (Series Info, Season List)
        - [ ] Create `SeasonDetailPage` (Episode List with thumbnails and summaries)
        - [ ] Implement "Next Episode" logic
    - [ ] **Music Experience**
        - [ ] **Implement View Toggle** (Artist [Default] vs Album) for Music Libraries
        - [ ] Create `ArtistDetailPage` (Bio, Album Grid)
        - [ ] Create `AlbumDetailPage` (Tracklist, Disc separation, Release Date)
    - [ ] **Book & Audiobook Experience**
        - [ ] Create `BookDetailPage` (Summary, Author, Page Count, ISBN)
        - [ ] Add "Read" button for E-Books and "Listen" button for Audiobooks
        - [ ] Support "Narrator" metadata field for Audiobooks
    - [ ] **Game Experience**
        - [ ] Create `GameDetailPage` (Platform, Developer, Multiplayer/Singleplayer tags)
        - [ ] Handle multi-disc selection in "Play/Download" action
    - [ ] **Photo Experience**
        - [ ] **Implement Masonry/Justified Grid Layout** for Photo Libraries
        - [ ] Create `PhotoDetailPage` (Large preview, EXIF data sidebar)
        - [ ] Implement "Next/Prev" navigation for photo albums
- [ ] **Search & Filtering**
    - [ ] **Backend Filtering**
        - [ ] Implement `GET /api/v1/libraries/{id}/filter` endpoint
        - [ ] Support filtering by: Genre, Year, Content Rating, Resolution, Video Codec, Audio Codec, **User Rating**
        - [ ] Support sorting by: Date Added, Release Date, Title, IMDB Rating, **User Rating**
    - [ ] **User Rating System (Backend)**
        - [ ] Add `UserRating` field to `MediaItem` (or separate table for per-user ratings)
        - [ ] Create `POST /api/v1/media/{id}/rate` endpoint
    - [ ] **Frontend Filter UI**
        - [ ] Update `FilterBar` to fetch available filters from backend
        - [ ] Implement multi-select for Genres
        - [ ] Implement Range slider for Years or Ratings (optional)
        - [ ] Implement Text Search input (Debounced)

### 3.4 Final Polish
- [x] **Performance**
    - [x] Implement Image Caching (Frontend & Backend)
    - [x] Optimize Database Indices
- [ ] **Security**
    - [ ] Run Security Audit (LFI, XSS, CSRF)
    - [ ] Verify Remote Access (DuckDNS/Tailscale)
