# Task 03 — Per-library access control (per-user ACL)

**Wave:** C
**Plan:** [feature-shortlist-plan-2026-04-30.md](../../plans/feature-shortlist-plan-2026-04-30.md#wave-c--per-library-access-control-per-user-acl)
**Severity:** Medium — intersects every list endpoint and the parental-control filter. Architecturally the largest single PR in this shortlist.
**Estimated effort:** 3–4 days. Single PR.
**Branch:** `feat/per-library-acl`

---

## Background

Today, [User.cs](../../../src/SoftMedia.Server/Models/User.cs) has a `Role` (`User`/`Admin`) and a per-type `ContentRatings` JSON dict, but no concept of "this user can see Movies but not Audiobooks." Family setups commonly want a kid account that sees a curated kids-movies library only, or a roommate who shouldn't see the home-photos library when that lands in Phase 2.

The codebase already has the right *shape* for per-user filtering — the parental-control system. We mirror it exactly:

- **Backend reader pattern:** [UserContentRatingProvider.cs](../../../src/SoftMedia.Server/Services/Security/ContentRating/UserContentRatingProvider.cs) reads `HttpContext.User`, looks up the user row, caches the result on `HttpContext.Items` so repeated repository calls in the same request pay one DB lookup.
- **Backend filter pattern:** [RatingFilterExtensions.cs](../../../src/SoftMedia.Server/Services/Security/ContentRating/RatingFilterExtensions.cs) turns the cached object into an `IQueryable<MediaItem>.Where(...)` clause that EF translates to SQL — no client-side evaluation, paginated counts stay correct.
- **Admin UI pattern:** the user list at [src/SoftMedia.Client/src/components/admin/UserListTable.tsx](../../../src/SoftMedia.Client/src/components/admin/UserListTable.tsx) shows an "Edit Ratings" button per row that opens [RatingsModal.tsx](../../../src/SoftMedia.Client/src/components/modals/RatingsModal.tsx) — a per-user settings modal scoped to a single concern.

This task replicates that triple. Per the maintainer's instruction: the existing content-rating control on the user-management page is the model — the new ACL UI is a sibling button on the same row, opening a sibling modal. **Do not put this on `MyAccountPage.tsx`** — ACL is an admin-only setting *about* a user, exactly like content ratings.

## Behavior after this task

### Default semantics (mandatory)

- **No `UserLibraryAccess` rows for a user = unrestricted.** That user sees every library on the server, exactly as today. This is the default for every existing user post-migration.
- **At least one row = allow-list.** Once any row exists for a user, the user sees only the libraries listed in their rows.
- **Admins always bypass**, regardless of rows. Mirrors `UserContentRatingProvider.ResolveAsync` at [UserContentRatingProvider.cs:54-57](../../../src/SoftMedia.Server/Services/Security/ContentRating/UserContentRatingProvider.cs#L54-L57).
- **Saving an empty selection in the UI clears all rows** (= unrestricted). It does **not** mean "no libraries." This is mandatory — the modal copy must make this explicit so an admin doesn't accidentally lock out a user.

### Filtering scope (every read that returns a library or media item)

- `GET /api/v1/libraries` — filtered.
- `GET /api/v1/libraries/{id}` — `404` if the library is not in the user's allowed set.
- `GET /api/v1/libraries/{id}/items` — `404` if blocked, otherwise filtered.
- `GET /api/v1/libraries/{id}/genres` — `404` if blocked.
- `GET /api/v1/media/{id}` — `404` if the item's `LibraryId` is not allowed.
- `GET /api/v1/media/recent` — items from blocked libraries silently stripped.
- `GET /api/v1/media/hero` — items from blocked libraries silently stripped (impacts `RecommendationService.GetHeroItemsAsync`).
- `GET /api/v1/media/search` — items from blocked libraries silently stripped per group.
- `GET /api/v1/stream/{id}`, `/api/v1/audio/{id}`, `/api/v1/audio/{id}/cover`, `/api/v1/books/{id}/...`, `/api/v1/transcode/{id}/...` — all return `404` for blocked libraries (matches the existing jail behavior at [StreamController.cs:50](../../../src/SoftMedia.Server/Controllers/StreamController.cs#L50)).
- SignalR `MediaHub.JoinLibrary` / `JoinMedia` — silently drops the join if the library is blocked. Mirrors the existing pattern at [MediaHub.cs:42-48](../../../src/SoftMedia.Server/Hubs/MediaHub.cs#L42-L48) where invalid libraries are silently dropped.

### Cascade & lifecycle

- **Library hard-delete** (the only delete flow today, via `LibraryService.DeleteLibraryAsync`) cascades to delete its `UserLibraryAccess` rows automatically via EF `OnDelete(DeleteBehavior.Cascade)`. **No code change is needed in `LibraryService.DeleteLibraryAsync`** — the cascade fires when EF emits the `DELETE FROM Libraries` statement on `SaveChangesAsync`. Reviewers should not add explicit `_context.UserLibraryAccess.Where(a => a.LibraryId == id).ExecuteDelete()` calls; the cascade handles it.
- **User soft-delete** (the current user-deletion flow at [UsersController.cs:243-265](../../../src/SoftMedia.Server/Controllers/UsersController.cs#L243-L265)) sets `IsDeleted=true` and renames the user row but **does not remove the `Users` row**. The cascade therefore does not fire and `UserLibraryAccess` rows are retained. This is correct: the rows are dormant (no requests authenticate as the deleted user), and if the user is ever undeleted their access state is preserved.
- **User hard-delete** (does not currently exist) would cascade — the FK is configured that way for forward-compatibility.

## Schema

### New entity

**`src/SoftMedia.Server/Models/UserLibraryAccess.cs`** (new file):

```csharp
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace SoftMedia.Server.Models;

/// <summary>
/// Per-user allow-list for libraries. Presence semantics:
///   - Zero rows for a user => unrestricted (sees every library).
///   - At least one row    => allow-list (sees only those libraries).
/// Admins always bypass this filter regardless of rows.
/// </summary>
[PrimaryKey(nameof(UserId), nameof(LibraryId))]
public class UserLibraryAccess
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid LibraryId { get; set; }
    public Library Library { get; set; } = null!;
}
```

### DbContext registration

**[src/SoftMedia.Server/Data/AppDbContext.cs](../../../src/SoftMedia.Server/Data/AppDbContext.cs)** — add:
```csharp
public DbSet<UserLibraryAccess> UserLibraryAccess { get; set; }
```

In `OnModelCreating` (alongside the other `MediaItemCast` / `MediaItemGenre` config blocks), configure cascade delete on `Library`:
```csharp
modelBuilder.Entity<UserLibraryAccess>()
    .HasOne(a => a.Library)
    .WithMany()
    .HasForeignKey(a => a.LibraryId)
    .OnDelete(DeleteBehavior.Cascade);

modelBuilder.Entity<UserLibraryAccess>()
    .HasOne(a => a.User)
    .WithMany()
    .HasForeignKey(a => a.UserId)
    .OnDelete(DeleteBehavior.Cascade);
```

### Migration

```
dotnet ef migrations add AddUserLibraryAccess --project src/SoftMedia.Server
```

Migration name: `yyyyMMddHHmmss_AddUserLibraryAccess` per the existing convention.

## Backend changes — files to add

### 1. `LibraryAccess` value object

**`src/SoftMedia.Server/Services/Security/LibraryAccess/LibraryAccess.cs`** (new folder + file):

```csharp
namespace SoftMedia.Server.Services.Security.LibraryAccess;

/// <summary>
/// Resolved library-access policy for the current request.
/// Mirrors UserRatingCeilings in design — an immutable struct that
/// repositories use to apply a single Where clause.
/// </summary>
public readonly struct LibraryAccess
{
    public bool IsUnrestricted { get; }
    public IReadOnlyList<Guid> AllowedLibraryIds { get; }

    private LibraryAccess(bool unrestricted, IReadOnlyList<Guid> ids)
    {
        IsUnrestricted = unrestricted;
        AllowedLibraryIds = ids;
    }

    public static LibraryAccess Unrestricted => new(true, Array.Empty<Guid>());
    public static LibraryAccess AllowOnly(IEnumerable<Guid> ids) =>
        new(false, ids.Distinct().ToArray());
}
```

**Why `IReadOnlyList<Guid>` and not `IReadOnlySet<Guid>`:** the existing parental-control filter at [RatingTables.cs:19-20](../../../src/SoftMedia.Server/Services/Security/ContentRating/RatingTables.cs#L19-L20) explicitly notes that EF Core translates `List<T>.Contains(column)` to `WHERE column IN (...)`. That translation is well-tested and reliable. `IReadOnlySet<T>.Contains` is a less-trodden EF translation path. We match the established pattern.

### 2. Provider with HttpContext caching

**`src/SoftMedia.Server/Services/Security/LibraryAccess/IUserLibraryAccessProvider.cs`** + implementation. Direct copy-paste of [UserContentRatingProvider.cs](../../../src/SoftMedia.Server/Services/Security/ContentRating/UserContentRatingProvider.cs) structure with the cache key changed to `softmedia.userLibraryAccess` and the lookup changed to:

```csharp
var allowed = await _db.UserLibraryAccess
    .AsNoTracking()
    .Where(a => a.UserId == userId)
    .Select(a => a.LibraryId)
    .ToListAsync();

return allowed.Count == 0
    ? LibraryAccess.Unrestricted
    : LibraryAccess.AllowOnly(allowed);
```

**Same admin-bypass logic and same fail-open-on-malformed-claim logic as the rating provider.** Comment explicitly references SDD §6.2 and the rating-provider precedent.

### 3. Filter extensions

**`src/SoftMedia.Server/Services/Security/LibraryAccess/LibraryAccessFilterExtensions.cs`** — two extension methods, one for `IQueryable<Library>`, one for `IQueryable<MediaItem>`:

```csharp
public static IQueryable<Library> ApplyLibraryAccessFilter(
    this IQueryable<Library> query, LibraryAccess access)
{
    if (access.IsUnrestricted) return query;
    var allowed = access.AllowedLibraryIds;
    return query.Where(l => allowed.Contains(l.Id));
}

public static IQueryable<MediaItem> ApplyLibraryAccessFilter(
    this IQueryable<MediaItem> query, LibraryAccess access)
{
    if (access.IsUnrestricted) return query;
    var allowed = access.AllowedLibraryIds;
    return query.Where(m => allowed.Contains(m.LibraryId));
}
```

EF translates `IReadOnlyList<Guid>.Contains(...)` into a SQL `WHERE column IN (...)` clause when the list is captured as a local — same trick the rating filter uses (see the comment at [RatingTables.cs:19-20](../../../src/SoftMedia.Server/Services/Security/ContentRating/RatingTables.cs#L19-L20)).

### 4. DI

**[src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs](../../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs)** — inside `AddSecurityServices` (alongside `IUserContentRatingProvider`):
```csharp
services.AddScoped<IUserLibraryAccessProvider, UserLibraryAccessProvider>();
```

## Backend changes — files to modify

### 5. Repositories — apply the filter

**[src/SoftMedia.Server/Services/Infrastructure/LibraryRepository.cs](../../../src/SoftMedia.Server/Services/Infrastructure/LibraryRepository.cs)** — inject `IUserLibraryAccessProvider` alongside the existing `IUserContentRatingProvider` (lines 13-19). Apply the filter:

- `GetAllAsync` — apply to the `Libraries` IQueryable before `ToListAsync`.
- `GetByIdAsync` — after `FindAsync`, return `null` if the library's `Id` is not in the user's allowed set.
- `ExistsAsync` — re-route through `GetByIdAsync` semantics so existence checks respect ACL.
- `IsPathUsedAsync` — **do not** apply the filter here. This is an admin-only uniqueness check called from `LibraryService.CreateLibraryAsync` / `UpdateLibraryAsync`. The calling endpoints already require `[Authorize(Roles = "Admin")]` and admins always have `LibraryAccess.Unrestricted`, so applying the filter would be redundant. Skipping it also keeps the path-collision check honest in the unlikely future case where a non-admin code path ever calls it.
- `GetLibraryItemsAsync` — gate at the top: if `library == null` (after the filtered `GetByIdAsync` lookup) return the empty `PagedResult<>` (this is the existing pattern at line 70-77; it stays). The pagination math then operates on the post-filter data, which is correct.

**[src/SoftMedia.Server/Services/Infrastructure/MediaRepository.cs](../../../src/SoftMedia.Server/Services/Infrastructure/MediaRepository.cs)** — inject `IUserLibraryAccessProvider`. Apply the filter at the top of every read:
- `GetByIdAsync` / `GetByIdWithLibraryAsync` — return `null` if the item's `LibraryId` is blocked.
- `GetRecentMediaAsync` (powers `GET /api/v1/media/recent`).
- All `GetSeriesEpisodes...`, `GetArtistAlbums...`, `GetAlbumTracks...`, `GetComicIssues...` methods — apply the filter so a user with access to Library A but not Library B doesn't see episodes that escaped via a series ID match.

### 6. RecommendationService — hero items

**[src/SoftMedia.Server/Services/Media/RecommendationService.cs](../../../src/SoftMedia.Server/Services/Media/RecommendationService.cs)** — `GetHeroItemsAsync` reads from a cache (`HeroCaches` table) populated by [HeroCacheWorker](../../../src/SoftMedia.Server/Services/Background/HeroCacheWorker.cs). The worker runs without an HTTP context; the cache is server-wide. Apply the filter **at read time**, not at cache-build time, so the cache stays user-agnostic:

```csharp
var allItems = /* deserialize cache */;
var access = await _libraryAccessProvider.GetCurrentAsync();
return allItems.Where(i => access.IsUnrestricted || access.AllowedLibraryIds.Contains(i.LibraryId));
```

### 7. MediaController.GlobalSearch

**[src/SoftMedia.Server/Controllers/MediaController.cs:138-176](../../../src/SoftMedia.Server/Controllers/MediaController.cs#L138-L176)** — the search query directly hits `_context.MediaItems`. Add `.ApplyLibraryAccessFilter(access)` before the `.Where(m => EF.Functions.Like(...))` clause. Inject `IUserLibraryAccessProvider` into the controller (or move the search into `MediaRetrievalService` to keep DI consistent).

### 8. Stream / Audio / Book / Transcode controllers

These already gate on file-existence and library-jail. Add a **library-access pre-check** that returns `404` (matches existing jail behavior). The cleanest place is `IStreamSecurityService.ValidateMediaAccess` at [StreamSecurityService.cs](../../../src/SoftMedia.Server/Services/Security/StreamSecurityService.cs).

**Async signature change.** The current method is sync. Change it to `Task<MediaAccessResult> ValidateMediaAccessAsync(MediaItem item)` because the new check awaits `_libraryAccessProvider.GetCurrentAsync()`. Do not use `.GetAwaiter().GetResult()` — it's a sync-over-async deadlock risk and contradicts the project's async-first posture. Update the interface and every call site in one commit:

- [TranscodeController.cs:82](../../../src/SoftMedia.Server/Controllers/TranscodeController.cs#L82) — already in an `async` method; add `await`.
- [TranscodeController.cs:113](../../../src/SoftMedia.Server/Controllers/TranscodeController.cs#L113) — same.
- Any other call site found by `grep -rn "ValidateMediaAccess(" src/SoftMedia.Server/` — convert.

```csharp
public async Task<MediaAccessResult> ValidateMediaAccessAsync(MediaItem item)
{
    // existing jail check ...

    // NEW: library-access check
    var access = await _libraryAccessProvider.GetCurrentAsync();
    if (!access.IsUnrestricted && !access.AllowedLibraryIds.Contains(item.LibraryId))
        return MediaAccessResult.Unauthorized;  // controller maps to 404, not 403

    return MediaAccessResult.Ok;
}
```

### 9. SignalR hub

**[src/SoftMedia.Server/Hubs/MediaHub.cs:29-52](../../../src/SoftMedia.Server/Hubs/MediaHub.cs#L29-L52)** — `JoinLibrary` already silently drops invalid GUIDs (lines 31-36) and non-existent libraries (lines 43-48), each with a warning log. Add a third silent-drop branch after the existence check: resolve the user via `Context.User`, look up `UserLibraryAccess`, and drop if not allowed. Same pattern in `JoinMedia` — drop if the media's library is blocked. Use `Context.User.GetUserId()` from [ClaimsPrincipalExtensions.cs](../../../src/SoftMedia.Server/Extensions/ClaimsPrincipalExtensions.cs); the class-level `[Authorize]` at line 13 ensures `Context.User.Identity.IsAuthenticated` is true here.

### 10. New admin endpoints

**[src/SoftMedia.Server/Controllers/UsersController.cs](../../../src/SoftMedia.Server/Controllers/UsersController.cs)** — add two endpoints (admin-only via class-level `[Authorize(Roles = "Admin")]`):

```csharp
/// <summary>
/// Returns the explicit allow-list for a user. Empty array => unrestricted (default).
/// </summary>
[HttpGet("{id}/library-access")]
public async Task<ActionResult<List<Guid>>> GetUserLibraryAccess(Guid id)
{
    var ids = await _context.UserLibraryAccess
        .AsNoTracking()
        .Where(a => a.UserId == id)
        .Select(a => a.LibraryId)
        .ToListAsync();
    return Ok(ids);
}

/// <summary>
/// Replaces the user's library allow-list. Empty array clears all rows
/// (= unrestricted). The admin cannot lock themselves out: trying to
/// pass a non-empty list when targeting an admin user is a no-op.
/// </summary>
[HttpPut("{id}/library-access")]
public async Task<IActionResult> SetUserLibraryAccess(
    Guid id, [FromBody] SetLibraryAccessRequest request)
{
    var user = await _context.Users.FindAsync(id);
    if (user == null) return NotFound();
    if (user.Role == UserRole.Admin) return BadRequest("Admins always have access to all libraries.");

    var existing = await _context.UserLibraryAccess.Where(a => a.UserId == id).ToListAsync();
    _context.UserLibraryAccess.RemoveRange(existing);

    if (request.LibraryIds.Count > 0)
    {
        // Validate all library IDs exist before inserting any.
        var validLibraryIds = await _context.Libraries
            .Where(l => request.LibraryIds.Contains(l.Id))
            .Select(l => l.Id)
            .ToHashSetAsync();
        var unknown = request.LibraryIds.Except(validLibraryIds).ToList();
        if (unknown.Count > 0)
            return BadRequest($"Unknown library IDs: {string.Join(", ", unknown)}");

        foreach (var libraryId in validLibraryIds)
        {
            _context.UserLibraryAccess.Add(new UserLibraryAccess
            {
                UserId = id,
                LibraryId = libraryId
            });
        }
    }
    await _context.SaveChangesAsync();
    return Ok();
}

public record SetLibraryAccessRequest(List<Guid> LibraryIds);
```

## Frontend changes

### 11. Service layer

**`src/SoftMedia.Client/src/services/userService.ts`** — add:
```ts
getUserLibraryAccess: (userId: string) =>
    api.get<string[]>(`/users/${userId}/library-access`).then(r => r.data),

setUserLibraryAccess: (userId: string, libraryIds: string[]) =>
    api.put(`/users/${userId}/library-access`, { libraryIds }),
```

### 12. New modal — sibling to `RatingsModal`

**`src/SoftMedia.Client/src/components/modals/LibraryAccessModal.tsx`** (new file) — direct sibling of [RatingsModal.tsx](../../../src/SoftMedia.Client/src/components/modals/RatingsModal.tsx). Same shape: `isOpen`, `onClose`, `user` props; `useQuery` to fetch current access + all libraries, `useMutation` to save.

UI layout:
- Header: `"Edit Library Access for {user.username}"`.
- Body: `<p>` describing the semantics — "**No selection means this user can see every library** (default). To restrict, tick only the libraries this user should have access to."
- A `<ul>` of libraries (from `libraryService.getLibraries()`), each row a `<button type="button">` toggle with checkbox icon (lucide `Check` / empty box). Pair `hover:bg-white/10` with `focus-visible:ring-2 focus-visible:ring-blue-400`. Min hit area 44×44.
- Below the list, a "Clear all (unrestricted)" link that resets selection to `[]`.
- Footer: Cancel + Save buttons. Save calls `setUserLibraryAccess(user.id, selectedIds)`. On success, `toast.success('Library access updated')`, invalidate `['users']`, close.

For the user.role === 'Admin' case, render a disabled placeholder with copy: "Admins always have access to all libraries."

### 13. UserListTable — wire up the button

**[src/SoftMedia.Client/src/components/admin/UserListTable.tsx](../../../src/SoftMedia.Client/src/components/admin/UserListTable.tsx)** — add a sibling button next to the existing "Edit Ratings" button (line 458). Same styling. Add `const [accessModalUser, setAccessModalUser] = useState<UserDto | null>(null);` at the top of the component, render `<LibraryAccessModal isOpen={!!accessModalUser} ... />` at the bottom alongside `<RatingsModal ...>`.

## Tests

### Backend

14. **`src/SoftMedia.Server.Tests/Services/Security/LibraryAccess/UserLibraryAccessProviderTests.cs`**:
    - No HttpContext → `LibraryAccess.Unrestricted` (background scanner safety).
    - Anonymous principal → `LibraryAccess.Unrestricted`.
    - Admin role → `LibraryAccess.Unrestricted` regardless of rows.
    - User with zero rows → `LibraryAccess.Unrestricted`.
    - User with N rows → `LibraryAccess.AllowOnly(...)` containing exactly those IDs.
    - Cache hit on repeated calls within the same `HttpContext` (assert DB query count is 1).

15. **`src/SoftMedia.Server.Tests/Services/Security/LibraryAccess/LibraryAccessFilterTests.cs`**:
    - `Unrestricted` access leaves the query unmodified (assert SQL doesn't gain a `WHERE LibraryId IN (...)` clause — easiest via in-memory and asserting result equality).
    - Restricted access strips items from disallowed libraries.

16. **`src/SoftMedia.Server.Tests/Repositories/LibraryRepositoryAclTests.cs`**:
    - `GetAllAsync` returns only allowed libraries for restricted user.
    - `GetByIdAsync` returns `null` for blocked library ID.
    - `GetLibraryItemsAsync` returns empty page + `TotalCount = 0` for blocked library.

17. **`src/SoftMedia.Server.Tests/Controllers/MediaAclTests.cs`**:
    - `GET /api/v1/media/{id}` → 404 for blocked library.
    - `GET /api/v1/stream/{id}` → 404 for blocked library.
    - `GET /api/v1/media/recent` → response excludes blocked-library items.
    - `GET /api/v1/media/search?query=...` → response groups exclude blocked libraries.

18. **`src/SoftMedia.Server.Tests/Controllers/UsersAclEndpointTests.cs`**:
    - Anonymous → 401 on both endpoints.
    - Non-admin → 403.
    - Admin GET on user with no rows → empty array.
    - Admin PUT with `{ libraryIds: [validId] }` then GET returns `[validId]`.
    - Admin PUT with `{ libraryIds: [] }` clears existing rows.
    - Admin PUT targeting an Admin user → 400.
    - Admin PUT with unknown library ID → 400, no rows mutated.

### Frontend

19. **`src/SoftMedia.Client/src/components/modals/LibraryAccessModal.test.tsx`** — Vitest + RTL:
    - Renders all libraries fetched from the service.
    - Pre-checks libraries that are in the user's current access list.
    - Saving with empty selection sends `{ libraryIds: [] }` and shows success toast.
    - Saving with N selections sends `{ libraryIds: [...] }`.
    - For an admin user, shows disabled state and the explanatory copy.

## Acceptance criteria

- A non-admin user with `UserLibraryAccess` rows for Library A but not Library B:
  - sees only Library A in `GET /api/v1/libraries`.
  - gets `404` on `GET /api/v1/libraries/B-id`.
  - gets `404` on `GET /api/v1/media/{B-item-id}`.
  - cannot stream a B item via any of the streaming controllers (404).
  - sees zero B items in `/api/v1/media/recent`, `/api/v1/media/hero`, `/api/v1/media/search`.
- A non-admin user with **zero** `UserLibraryAccess` rows behaves identically to today's behavior (sees all libraries).
- An admin user behaves identically to today regardless of any `UserLibraryAccess` rows that may exist for their account.
- The Admin → Users page shows an "Edit Library Access" button next to "Edit Ratings". Opening it for a user with no restrictions shows zero checkboxes ticked. Saving an empty selection clears any existing rows.
- Deleting a library cascades the corresponding `UserLibraryAccess` rows.
- `dotnet test` passes; all six new test files are present and green.
- A migration `yyyyMMddHHmmss_AddUserLibraryAccess` is the only new migration.

## Out of scope

- **Per-user library *write* permissions** ("user X can mark items as favorite in this library only"). Today, write surfaces are admin-only or interaction-only (favorite/rate/watched), and interaction is gated by library access automatically — `UserMediaInteraction` rows can't reference items the user can't see, because they can't request the action. No additional gating needed.
- **Per-folder ACL within a library.** A library is the smallest ACL unit. Splitting libraries by folder is the existing answer.
- **A self-service ACL view on `MyAccountPage.tsx`.** ACL is admin-set; users do not configure their own restrictions.
