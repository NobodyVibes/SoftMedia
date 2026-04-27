# 08 · Canonicalise library paths at creation time

**Severity:** P1 · **Layer:** Backend · **Est. size:** S (< 1 day)

## Problem

`src/SoftMedia.Server/Services/Media/LibraryService.cs` around lines 51–63 (`CreateLibraryAsync` — verify exact lines when editing) stores the raw path string supplied by the admin into the database. It checks only:

1. `Directory.Exists(path)` — does the folder exist?
2. A string-equality duplicate check against other libraries.

Consequences:

- `../../../../secret` and `C:\Users\Admin\secret` are both accepted if the resolved directory exists — the raw string is stored.
- Two libraries can resolve to the same physical folder but pass the string-equality duplicate check: `C:\Media\Movies` and `C:\Media\..\Media\Movies`, or relative vs absolute, or `/mnt/media` vs `/mnt/media/`.
- Symlinks are not detected. An admin could add `~/Media` which symlinks to `/`.

The file watcher and streaming path (`StreamSecurityService`) **do** canonicalise the stored path at access time, so there is no active exploit path today. But:

- The stored path in the DB is whatever was typed, which makes debugging harder and duplicates silent.
- If anyone refactors `StreamSecurityService` to trust the DB value, the jail collapses.
- Duplicate canonical paths cause confused scan behaviour and double-indexed media.

## Target state

- On `CreateLibraryAsync` and `UpdateLibraryAsync`, every path is canonicalised via `Path.GetFullPath` before the existence check, duplicate check, and DB insert.
- Duplicate detection compares canonical forms (case-insensitive on Windows, case-sensitive on Linux — EF Core collation or an explicit compare).
- On Linux, attempting to add a path that is itself a symlink or contains a symlinked ancestor is logged as a warning but still allowed (following symlinks is legitimate for many setups); optionally blocked by a config flag `Libraries:BlockSymlinkedPaths` default `false`.
- A migration back-fills existing rows to canonical form (idempotent).

## Scope

**In scope:**
- `LibraryService.CreateLibraryAsync` and `UpdateLibraryAsync`.
- Duplicate detection against canonical form.
- Migration to canonicalise existing `Libraries.Paths` rows.
- Unit tests.

**Out of scope:**
- UX changes in the admin UI (the backend already returns the canonicalised path on success; the UI will naturally display it).
- Auto-expansion of `~` or environment variables in paths (would be a UX improvement; defer).
- Symlink policy enforcement beyond a warning log.

## Implementation steps

1. Add a private helper `private static string Canonicalise(string path)`:
   - `Path.GetFullPath(path.Trim())`.
   - Trim trailing separator for consistent comparison (except root drives).
2. In `CreateLibraryAsync`, apply `Canonicalise` to every incoming path **before** the existence check and duplicate check.
3. Duplicate check: query `Libraries` where any element of `Paths` equals the canonical form (case-insensitive on Windows). EF-side comparison should match filesystem casing semantics.
4. In `UpdateLibraryAsync` (if it exists — if not, skip), apply the same treatment.
5. Optional warning: if `new FileInfo(path).LinkTarget is not null`, log a warning. Do not block.
6. **Data migration**: add an EF Core migration `CanonicaliseLibraryPaths` that iterates existing rows and rewrites `Paths`. Write the migration's `Up` in C# (not raw SQL) by running a one-off data-fixup inside the migration, or by running a `MigrationRunner` hosted service at startup that is idempotent. Prefer the hosted-service approach for clarity.
7. Reject the path if canonicalisation throws (e.g., on Windows drives that don't exist): return a 400 with a helpful message.

## Files to touch

- `src/SoftMedia.Server/Services/Media/LibraryService.cs`
- `src/SoftMedia.Server/Migrations/*` (new migration, if any schema change; otherwise a `CanonicaliseLibraryPathsService` hosted service for data fixup)
- `src/SoftMedia.Server.Tests/Services/Media/LibraryServiceTests.cs`

## Tests required

- `Create_RelativePath_StoresCanonicalAbsolutePath`
- `Create_PathWithTrailingSlash_StoresWithoutTrailingSlash`
- `Create_DuplicateViaRelativePath_IsRejected` (create `C:\Media\Movies`, then try `C:\Media\..\Media\Movies`)
- `Create_DuplicateViaCaseOnWindows_IsRejected` (`C:\Media\Movies` vs `c:\media\movies`)
- `Create_NonexistentPath_Returns400`
- `Create_SymlinkedPathOnLinux_Succeeds_AndLogsWarning` — may be skipped in CI that doesn't support symlinks
- `Update_PathReverted_StaysCanonical`

## Acceptance criteria

- [ ] `LibraryService` canonicalises every incoming path before DB insert.
- [ ] Duplicate detection compares canonical forms.
- [ ] Existing DB rows are back-filled to canonical form via migration or idempotent hosted service.
- [ ] All tests above pass.
- [ ] `grep -n 'Path.GetFullPath' src/SoftMedia.Server/Services/Media/LibraryService.cs` shows the canonicalisation is in place.
- [ ] Manual smoke: add a library via the admin UI with a path that has a trailing slash; DB stores it without; adding the same path again without the slash is rejected.

## Risk / rollback

Low-to-medium. The data migration is the riskier part. Mitigations:

- Before running in prod, back up `softmedia.db` (it is SQLite — one file).
- The migration is idempotent: running it twice produces the same result.
- If the canonicalisation throws on a path (e.g., admin removed the drive), log and skip that path, do not crash.

Rollback: revert the code change; the DB will have canonical paths but those still work with the old code (it just wasn't enforcing canonicalisation).

## Related

- `StreamSecurityService` already canonicalises at access time — this todo makes that redundant (defence-in-depth, not a replacement). Do **not** remove the access-time check; belt-and-braces is correct for path safety.
