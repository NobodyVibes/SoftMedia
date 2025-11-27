# SoftMedia Project Checklist

## Phase 1: Foundation & Backend Core

### 1.1 Project Initialization
- [ ] **Repository Setup**
    - [ ] Initialize Git repository (`git init`)
    - [ ] Create `.gitignore` (Visual Studio, Node, React, macOS)
    - [ ] Create `README.md` with basic project info
- [ ] **Solution Setup**
    - [ ] Create `SoftMedia.sln`
    - [ ] Create ASP.NET Core Web API project (`SoftMedia.Server`)
    - [ ] Create React + Vite project (`SoftMedia.Client`)
    - [ ] Add projects to Solution
- [ ] **Backend Configuration**
    - [ ] Configure `appsettings.json` (ConnectionStrings, JWT Settings, RateLimits)
    - [ ] Setup Dependency Injection (DI) container in `Program.cs`
    - [ ] Configure CORS (Allow Frontend URL)
    - [ ] Configure Swagger/OpenAPI (Enable JWT Auth Support)

### 1.2 Database & Data Access
- [ ] **EF Core Setup**
    - [ ] Install NuGet packages (`Microsoft.EntityFrameworkCore.Sqlite`, `Design`)
    - [ ] Create `AppDbContext` class
- [ ] **Entities**
    - [ ] Define `User` entity (Id, Username, PasswordHash, Role, MaxRating)
    - [ ] Define `Library` entity (Id, Name, Path, Type)
    - [ ] Define `MediaItem` entity (Core Columns + JSON for Type-Specific Metadata)
- [ ] **Migrations**
    - [ ] Create Initial Migration
    - [ ] Update Database (Create `softmedia.db`)

### 1.3 Authentication System
- [ ] **Security Utilities**
    - [ ] Install `Konscious.Security.Cryptography.Argon2`
    - [ ] Create `PasswordHasher` service
- [ ] **Token Management**
    - [ ] Install `System.IdentityModel.Tokens.Jwt`
    - [ ] Create `TokenService` (Generate Access/Refresh Tokens)
- [ ] **API Endpoints**
    - [ ] Create `AuthRequest` DTOs (Login/Signup)
    - [ ] Create `AuthController` (POST /login, POST /signup)
    - [ ] Implement Refresh Token rotation logic (HttpOnly Cookie)
- [ ] **Testing**
    - [ ] Setup `xUnit` Test Project
    - [ ] Write Unit Tests for `PasswordHasher` and `TokenService`

### 1.4 Library Management (Backend)
- [ ] **File System**
    - [ ] Create `FileScannerService` (Recursive directory scan)
    - [ ] Implement **Jailed** `FileSystemWatcher` (Prevent path traversal)
- [ ] **Metadata Logic**
    - [ ] Create `MetadataService` interface
    - [ ] **Implement `MetadataRouter` (Selects provider based on `Library.Type`)**
    - [ ] Implement `WikidataProvider` (Movies/Games) with **Caching**
    - [ ] Implement `TVMazeProvider` (TV Shows) with **Rate Limiting**
    - [ ] Implement `LocalMetadataProvider` (NFO/Sidecar)
- [ ] **FFmpeg Integration**
    - [ ] Install FFmpeg wrapper or create `Process` helper
    - [ ] Implement `MediaProbe` (Extract Codec/Resolution/Duration)
    - [ ] Implement `SubtitleExtractor` (Extract SRT/PGS from MKV)

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
- [ ] **Player Component**
    - [ ] Install `vidstack` or similar player library
    - [ ] Create `VideoPlayer` component
- [ ] **Streaming**
    - [ ] Create Backend `StreamController` (Serve Static Files / Range Requests)
    - [ ] Connect Player to Stream Endpoint
    - [ ] **Note:** Only browser-supported formats (MP4/WebM) will play until Phase 3.
- [ ] **Subtitles**
    - [ ] Implement Subtitle Selector UI
    - [ ] Fetch/Parse subtitle tracks from Backend (Embedded & Local)

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
