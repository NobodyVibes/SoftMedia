using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Security.LibraryAccess;

/// <summary>
/// Translates a <see cref="LibraryAccess"/> into an <c>IQueryable&lt;T&gt;.Where</c>
/// clause that EF Core compiles to a SQL <c>WHERE column IN (...)</c> predicate.
/// Mirrors <see cref="ContentRating.RatingFilterExtensions"/> in design — paginated
/// listings, COUNT(*) queries, and direct-by-ID lookups all share one shape and
/// translate identically.
///
/// EF translates <c>list.Contains(column)</c> to <c>WHERE column IN (...)</c>
/// when <c>list</c> is captured as a local; we follow that idiom verbatim.
/// </summary>
public static class LibraryAccessFilterExtensions
{
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
}
