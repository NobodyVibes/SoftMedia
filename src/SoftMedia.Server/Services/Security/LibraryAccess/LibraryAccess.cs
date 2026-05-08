namespace SoftMedia.Server.Services.Security.LibraryAccess;

/// <summary>
/// Resolved library-access policy for the current request. Mirrors
/// <see cref="ContentRating.UserRatingCeilings"/> in design — an immutable
/// value type that repositories use to apply a single Where clause.
///
/// Semantics (Wave C, see docs/todos/feature-shortlist/03-per-library-acl.md):
///   - <see cref="IsUnrestricted"/> = true => see every library (admin or no rows).
///   - Otherwise <see cref="AllowedLibraryIds"/> is the explicit allow-list.
///
/// We use <see cref="IReadOnlyList{T}"/> not <see cref="IReadOnlySet{T}"/> because
/// EF Core reliably translates <c>List&lt;T&gt;.Contains(column)</c> to
/// <c>WHERE column IN (...)</c> — see the comment block in
/// <c>RatingTables.cs</c> on why the existing parental-control filter picked
/// the same shape.
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
