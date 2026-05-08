# Task 01 — Hide the Photo library type until Phase 2

**Wave:** A
**Plan:** [feature-shortlist-plan-2026-04-30.md](../../plans/feature-shortlist-plan-2026-04-30.md#wave-a--hide-the-photo-library-type-until-phase-2)
**Severity:** Trivial UX bug — a discoverable broken state in the admin UI.
**Estimated effort:** 0.5 day. Single PR.
**Branch:** `feat/hide-photo-library-type`

---

## Background

`LibraryType.Photo` exists in [src/SoftMedia.Server/Models/Library.cs:13](../../../src/SoftMedia.Server/Models/Library.cs#L13). `ExifMetadataProvider` is registered. `MediaType.Photo` (=6) exists in the enum. But `Services/Scanning/` has no `PhotoScanner` and the `ScannerOrchestrator.GetScannerForLibrary` lookup at [ScannerOrchestrator.cs:33-43](../../../src/SoftMedia.Server/Services/Scanning/ScannerOrchestrator.cs#L33-L43) returns `null` for Photo libraries, logs a warning, and the scan completes with zero items.

SDD §4.1 already labels Photos "Phase 2 (post-1.0)." The admin UI lying about an option that scans empty is a minor data-integrity problem (admins waste effort) and an obvious bad first impression.

## Behavior after this task

- The Add Library / Edit Library form no longer offers `Photo` in the type picker.
- The backend rejects `LibraryType.Photo` on `POST /api/v1/libraries` and `PUT /api/v1/libraries/{id}` with `400 Bad Request` and a message naming Phase 2.
- Existing Photo libraries (if any) are not deleted. They remain visible to admins as orphan rows; the admin can delete them via the existing `DELETE /api/v1/libraries/{id}` flow. We do **not** auto-clean — the admin owns library lifecycle.
- The `LibraryType.Photo` enum value, `ExifMetadataProvider`, and the `PhotoProvider` setting all stay in place so re-enabling later is a one-line revert plus the `PhotoScanner` Phase 2 work.

## Files to change

### Backend

1. **[src/SoftMedia.Server/Services/Media/LibraryService.cs](../../../src/SoftMedia.Server/Services/Media/LibraryService.cs#L51)** — add an early guard in `CreateLibraryAsync` (after the method opening, before `CanonicaliseAll`):
   ```csharp
   if (request.Type == LibraryType.Photo)
       throw new ArgumentException(
           "Photo libraries are not yet supported (planned for Phase 2).");
   ```
   Add the same guard in `UpdateLibraryAsync` (line 86) right after the null check.

   Rationale: the controller at [LibrariesController.cs:53-56](../../../src/SoftMedia.Server/Controllers/LibrariesController.cs#L53-L56) already maps `ArgumentException` to `BadRequest(ex.Message)`, so this gives a clean 400 with the human-readable reason.

### Frontend

2. **[src/SoftMedia.Client/src/components/library/LibraryForm.tsx:19](../../../src/SoftMedia.Client/src/components/library/LibraryForm.tsx#L19)** — change:
   ```tsx
   const libraryTypes = ['Movie', 'TV', 'Music', 'Book', 'Game', 'Photo'];
   ```
   to:
   ```tsx
   const libraryTypes = ['Movie', 'TV', 'Music', 'Book', 'Game'];
   ```

   Add a one-line comment immediately above explaining this is a UI-only hide and that Photo support returns when a `PhotoScanner` lands. This earns its keep because a future contributor adding type pickers elsewhere needs to know this list isn't authoritative.

### Tests

3. **`src/SoftMedia.Server.Tests/Services/Media/LibraryServiceCreatePhotoTests.cs`** (new file) — xUnit:
   - `CreateLibraryAsync_PhotoType_ThrowsArgumentException` — passes a `CreateLibraryRequest { Type = LibraryType.Photo, ... }` and asserts the exception message contains `"Phase 2"`.
   - `UpdateLibraryAsync_PhotoType_ThrowsArgumentException` — pre-seed a Movie library, attempt to update its type to Photo, assert the same.
   - `CreateLibraryAsync_NonPhotoType_Succeeds` — sanity check that a Movie library still creates (regression guard).

   Follow the existing `LibraryServiceTests` setup pattern (in-memory SQLite via `Microsoft.EntityFrameworkCore.Sqlite` with `Data Source=:memory:`). If no existing test exists for this service, create the fixture from scratch — it is small.

## Acceptance criteria

- `POST /api/v1/libraries` with body `{ "name": "Vacation Photos", "type": "Photo", "paths": ["C:\\Photos"] }` returns `400 Bad Request` with body `"Photo libraries are not yet supported (planned for Phase 2)."`.
- The Add Library modal in the running app does not list `Photo` in the type dropdown.
- `dotnet test` passes; the three new test cases are present and green.
- No EF migration is required for this task (verified by running `dotnet ef migrations list` before and after — same output).

## Re-enabling for Phase 2

When the `PhotoScanner` lands:
1. Remove the two `ArgumentException` guards in `LibraryService.cs`.
2. Add `'Photo'` back to `libraryTypes` in `LibraryForm.tsx`.
3. Delete this test file (or convert it to assert the opposite — that creating a Photo library now succeeds).

That is the entire revert surface, by design.

## Out of scope

- Implementing `PhotoScanner` itself — that's the Phase 2 work, in its own plan.
- Cleaning up orphan `LibraryType.Photo` rows — admins own library lifecycle.
- Hiding `LibraryType.Photo` from the database enum — it stays, so existing rows render their type label correctly.
