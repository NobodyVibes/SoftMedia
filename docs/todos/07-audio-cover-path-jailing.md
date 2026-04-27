# 07 · Port `AudioController.GetCoverArt` onto `StreamSecurityService`

**Severity:** P1 · **Layer:** Backend · **Est. size:** S (< 1 day)

## Problem

`src/SoftMedia.Server/Controllers/AudioController.cs:39` builds the cover-art file path like this:

```csharp
var fullPath = Path.Combine(
    Directory.GetCurrentDirectory(),
    "wwwroot",
    item.CoverArtPath.TrimStart('/'));
if (System.IO.File.Exists(fullPath))
{
    var stream = System.IO.File.OpenRead(fullPath);
    return File(stream, mimeType);
}
```

This is the only file-serving path in the backend that does **not** go through `StreamSecurityService`. Issues:

1. `TrimStart('/')` does not stop `..\..\..\etc\passwd` or `..\..\..\Users\Admin\Documents\secrets.txt`.
2. No canonicalisation, no verify-inside-jail check.
3. Real-world risk is low today because `CoverArtPath` is populated by SoftMedia's own scanner — not user input. But defense-in-depth is the whole point of `StreamSecurityService`, and this endpoint breaks the invariant that every file-open in this codebase goes through one validator.

It also gives future maintainers the wrong example: "look, `AudioController` just does `Path.Combine` — that must be fine."

## Target state

- `AudioController.GetCoverArt` reads cover art only from a single, canonicalised, jailed directory.
- The jail check reuses existing `StreamSecurityService` (or, if that doesn't fit the "inside-wwwroot" model, an analogous new helper with the same shape).
- Attempts to escape the jail (malformed DB rows, traversal payloads) return `404` and log a warning.
- A test covers the traversal-payload case.

## Scope

**In scope:**
- Refactor `GetCoverArt` to route through a canonicalisation-then-jail check.
- Decide whether to reuse `IStreamSecurityService` or add a tiny `IWwwRootSafety` helper (recommendation: reuse — pass the wwwroot path as the "library path" parameter).
- One negative test.

**Out of scope:**
- Changing where cover art is stored (still under `wwwroot/covers` or wherever the scanner puts it).
- Touching the scanner that populates `CoverArtPath`.
- Any work in `ImageController` — its proxy cache uses SHA-256 hashed filenames, not user-supplied paths.

## Recommended approach

`StreamSecurityService.IsPathAuthorized(string filePath, IEnumerable<string> libraryPaths)` already takes a list of "allowed roots." Pass `new[] { Path.Combine(Directory.GetCurrentDirectory(), "wwwroot") }` as the jail. No new service needed.

Pseudocode:

```
var wwwroot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"));
var candidate = Path.Combine(wwwroot, item.CoverArtPath.TrimStart('/', '\\'));

if (!_streamSecurity.IsPathAuthorized(candidate, new[] { wwwroot }))
{
    _logger.LogWarning("Cover art path outside wwwroot blocked: {Path}", item.CoverArtPath);
    return NotFound();
}

if (!System.IO.File.Exists(candidate)) return NotFound();
```

Then the existing MIME-type switch and `File(stream, mimeType)` call stays.

## Implementation steps

1. Inject `IStreamSecurityService` into `AudioController` (it is already registered in DI).
2. Replace the path-building block with the pattern above.
3. As a drive-by fix (in the same PR because it is on the same line of thinking): also strip `\` from the start, not just `/` — `.TrimStart('/', '\\')`.
4. Add a unit/integration test.

## Files to touch

- `src/SoftMedia.Server/Controllers/AudioController.cs`
- `src/SoftMedia.Server.Tests/Controllers/AudioControllerTests.cs` (from todo [01](01-controller-authorization.md); extend)

## Tests required

- `GetCoverArt_WithPathTraversalPayload_Returns404`
  - Create a `MediaItem` with `CoverArtPath = "../../../../etc/passwd"`.
  - Expect 404 (not 200, not 500).
- `GetCoverArt_WithValidPath_Returns200`
  - Place a real test image under `wwwroot/covers/` in the test fixture.
  - Expect 200 and the correct MIME type.
- `GetCoverArt_WithBackslashTraversalOnWindows_Returns404`
  - Windows-specific: `CoverArtPath = @"..\..\..\..\Windows\win.ini"`.
  - Expect 404.

## Acceptance criteria

- [ ] `AudioController.GetCoverArt` no longer contains a raw `Path.Combine` that can be escaped.
- [ ] All file reads go through `IStreamSecurityService.IsPathAuthorized` (or a jail-equivalent helper).
- [ ] The three tests above pass.
- [ ] Logging records the file path on block events.
- [ ] Grep for `Directory.GetCurrentDirectory()` in `src/SoftMedia.Server/Controllers/` returns zero non-test hits.

## Risk / rollback

Very low. The change is a few lines inside one method. Rollback is a revert. Watch out for: DI ordering — `IStreamSecurityService` registration must happen before `AudioController` construction (it does, because it's already registered for other controllers).

## Related

- Todo [01](01-controller-authorization.md) — adds `[Authorize]` to this controller. That PR and this one can merge in either order, but both must land before shipping.
- Todo [08](08-library-path-canonicalization.md) — adjacent path-safety work.
