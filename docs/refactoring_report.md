# SoftMedia Refactoring Report

**Date:** 2026-01-28
**Status:** Draft / Analysis

## 1. Executive Summary
The SoftMedia codebase is generally well-structured and follows modern .NET and React patterns. However, as features have been added, certain areas have accumulated technical debt. Key issues include monolithic configuration in `Program.cs`, "God Class" tendencies in core services like `TranscodeService`, and large, complex frontend components. Refactoring these areas will improve maintainability, testability, and scalability.

## 2. Backend Refactoring Opportunities (`SoftMedia.Server`)

### 2.1 Project Structure & Startup (`Program.cs`)
*   **Issue:** `Program.cs` is over 230 lines long and contains dense blocks of Service Registration, JWT configuration, and CORS logic.
*   **Recommendation:** Extract logic into `IServiceCollection` extension methods.
    *   `builder.Services.AddApplicationServices(config)`
    *   `builder.Services.AddIdentityServices(config)` (Auth + JWT)
    *   `builder.Services.AddMediaServices(config)` (Transcoding, Metadata, Scanning)
*   **Benefit:** Improves readability and isolates configuration logic.

### 2.2 Service Organization
*   **Issue:** The `Services` directory contains 52 files, many of which are in the root folder.
*   **Recommendation:** Group services by domain into subdirectories:
    *   `Services/Media` (StreamPlan, MediaProbe, FFmpeg)
    *   `Services/Transcoding` (TranscodeService, HlsManifest, Profiles)
    *   `Services/System` (Settings, UserPreferences, BinaryLocation)
    *   `Services/Identity` (Token, PasswordHasher)

### 2.3 `TranscodeService.cs` (High Priority)
*   **Issue:** This is a ~820 line "God Class" handling too many responsibilities: session state management, process lifecycle, file I/O, HLS playlist parsing, and logging.
*   **Refactoring Plan:**
    1.  **Extract `TranscodeSessionManager`:** Move `_activeSessions` and locking logic to a dedicated manager.
    2.  **Extract `HlsPlaylistParser`:** Move `GetActualPlaylistDuration` and playlist parsing logic to a stateless helper/service.
    3.  **Extract `TranscodeProcessManager`:** Encapsulate the start/stop/suspend logic for FFmpeg processes.
*   **Benefit:** drastically improves testability (can test playlist parsing in isolation) and reduces risk of concurrency bugs.

### 2.4 `LibraryWatcher.cs`
*   **Issue:** Mixes file system events, file locking/stability heuristics, and scanning triggers. Contains hardcoded file extensions.
*   **Refactoring Plan:**
    1.  **Extract `FileStabilityMonitor`:** A service dedicated to tracking file sizes and timeouts.
    2.  **Centralize Constants:** Move the list of extensions in `IsMediaFile` to a shared `MediaConstants` or `IMediaTypeResolver`.
*   **Benefit:** Reusability of file stability logic and single source of truth for supported file types.

### 2.5 Controller Logic
*   **Issue:** `MediaController.GetRecentMedia` contains imperative logic for deduplicating Series/Albums and fetching interactions.
*   **Recommendation:** Move this logic to a `MediaRetrievalService` or `MediaRecommendationService`.
*   **Issue:** `GetUserId()` logic is duplicated.
*   **Recommendation:** Create a `BaseApiController` or an extension method `User.GetId()`.

## 3. Frontend Refactoring Opportunities (`SoftMedia.Client`)

### 3.1 Component Structure
*   **Issue:** `UserListTable.tsx` is ~560 lines and includes UI, API calls, state management, filtering, and sorting.
*   **Refactoring Plan:**
    1.  **Custom Hook `useUserManagement`:** Move `useQuery`, `useMutation`, and filter state into a hook.
    2.  **Sub-Components:** Extract `UserFilters`, `UserListHeader`, and `UserRow`.
    3.  **Generic Table:** Consider using a headless table library (TanStack Table) or creating a generic `SortableTable` component to remove boilerplate.

### 3.2 Directory Structure
*   **Issue:** `components` folder is becoming flat and crowded (40+ files).
*   **Recommendation:** Group by feature or domain, similar to the backend:
    *   `components/admin` (User management, Library forms)
    *   `components/media` (Cards, Grids, Player)
    *   `components/common` (Modals, Buttons, Inputs)

## 4. General Improvements
*   **Hardcoded Values:**
    *   Remove hardcoded Extension lists (Backend `LibraryWatcher`).
    *   Standardize "Magic Strings" in `Program.cs` (e.g., CORS policy names) into Constants.
*   **Testing:**
    *   The complex logic in `TranscodeService` (throttling state machine) is prime for Unit Testing once extracted.

## 5. Proposed Action Plan
1.  **Backend Structure:** Create folders in `Services` and move files (Safe, low risk).
2.  **Program.cs:** Extract DI setup (Safe, high value).
3.  **Frontend:** Refactor `UserListTable` (Moderate effort, high UI maintainability value).
4.  **Complex Refactor:** Tackle `TranscodeService` decomposition (High effort, high risk, needs extensive testing).
