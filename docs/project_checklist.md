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
    - [ ] Configure `appsettings.json` (ConnectionStrings, JWT Settings, RateLimits)
    - [ ] Setup Dependency Injection (DI) container in `Program.cs`
    - [ ] Configure CORS (Allow Frontend URL)
    - [ ] Configure Swagger/OpenAPI (Enable JWT Auth Support)

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
    - [x] Implement `LocalMetadataProvider` (NFO/Sidecar)
- [x] **FFmpeg Integration**
    - [x] Install FFmpeg wrapper or create `Process` helper
    - [x] Implement `MediaProbe` (Extract Codec/Resolution/Duration)
    - [x] Implement `SubtitleExtractor` (Extract SRT/PGS from MKV)

## Phase 2: Frontend Core & Direct Play

### 2.1 Frontend Foundation
- [ ] **Build Setup**
    - [ ] Install NPM dependencies (`axios`, `zustand`, `react-router-dom`)
    - [ ] Configure Vite proxy for API
- [ ] **Styling**
    - [ ] Install Tailwind CSS
    - [ ] Configure `tailwind.config.js` (Blue-Violet Theme Colors)
    - [ ] Create global CSS variables

### 2.2 Authentication UI
- [ ] **State Management**
    - [ ] Create `useAuthStore` (Zustand)
    - [ ] Implement Axios interceptor for Token Refresh
- [ ] **Pages**
    - [ ] Create `LoginPage`
    - [ ] Create `SignupPage`
    - [ ] Create `ProtectedRoute` component

### 2.3 Library Browser
- [ ] **API Integration**
    - [ ] Create `LibraryService` (Frontend)
    - [ ] Implement React Query hooks (`useLibraries`, `useMediaItems`)
- [ ] **Components**
    - [ ] Create `MediaCard` (Poster, Title, Year)
    - [ ] Create `LibraryGrid` (Virtual scrolling/Pagination)
    - [ ] Create `FilterBar` (Genre, Year, Sort)

### 2.4 Media Playback (Direct Play)
- [ ] **Video Player**
    - [ ] Install `vidstack` or similar player library
    - [ ] Create `VideoPlayer` component (Overlay controls, Subtitles)
    - [ ] Connect to Backend Stream Endpoint (Range Requests)
- [ ] **Audio Player**
    - [ ] Create Global Audio Context (Zustand)
    - [ ] Create `PersistentPlayer` component (Bottom bar)
    - [ ] Implement Playlist Queue logic
- [ ] **eReader (Books/Comics)**
    - [ ] Install `react-pdf` and `epubjs` (or similar)
    - [ ] Create `BookReader` component (Canvas/Canvas)
    - [ ] Implement "Save Progress" logic (Page number)
- [ ] **Streaming Backend**
    - [ ] Create `StreamController` (Serve Static Files / Range Requests)
    - [ ] Implement `MimeTypeResolver` (Correct headers for MP4 vs MP3 vs PDF)

## Phase 3: Advanced Features & Polish

### 3.1 Transcoding System
- [ ] **Backend Logic**
    - [ ] Create `TranscodeService`
    - [ ] Implement FFmpeg command builder (HLS/Dash)
    - [ ] Manage temporary transcode segments
    - [ ] Implement `TranscodeController` (M3U8 playlists)
- [ ] **Frontend Logic**
    - [ ] Detect browser capabilities
    - [ ] Request Transcode vs Direct Play

### 3.2 Settings & Administration
- [ ] **Configuration**
    - [ ] Create `SettingsPage` (Tabs: Server, Users, Libraries)
    - [ ] Implement API endpoints for updating `appsettings` or DB config
- [ ] **User Management**
    - [ ] Create Admin User List (Ban/Promote)
    - [ ] Implement Invite System

### 3.3 Final Polish
- [ ] **Performance**
    - [ ] Implement Image Caching (Frontend & Backend)
    - [ ] Optimize Database Indices
- [ ] **Security**
    - [ ] Run Security Audit (LFI, XSS, CSRF)
    - [ ] Verify Remote Access (DuckDNS/Tailscale)
