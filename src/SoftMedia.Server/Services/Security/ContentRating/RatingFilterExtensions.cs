using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Security.ContentRating;

/// Translates a <see cref="UserRatingCeilings"/> into an `IQueryable&lt;MediaItem&gt;`
/// `Where` clause that EF Core compiles to a SQL predicate (no client-side
/// evaluation). The whole point of doing this here rather than in a comparator
/// is so that paged listings, COUNT(*) queries, and direct-by-ID lookups all
/// share one shape and translate identically.
///
/// Filtering rules (SDD §6.2 fail-safe semantics):
///   - For each type with a ceiling configured:
///       allowed iff item.ContentRating ∈ allow-list (table[0..ceiling])
///       items with NULL ContentRating are excluded (fail-safe)
///   - For each type WITHOUT a ceiling configured: pass-through.
///   - Types we don't gate at all (Audio/Album/Artist/Photo/Book/Comic in this
///     iteration): pass-through regardless.
///   - <see cref="UserRatingCeilings.IsUnrestricted"/> short-circuits to
///     no-filter (admin / no-context).
public static class RatingFilterExtensions
{
    public static IQueryable<MediaItem> ApplyContentRatingFilter(
        this IQueryable<MediaItem> query,
        UserRatingCeilings ceilings)
    {
        if (ceilings.IsUnrestricted) return query;

        var movieAllowed = RatingTables.AllowedAtOrBelow(RatingTables.Movie, ceilings.Movie);
        var tvAllowed = RatingTables.AllowedAtOrBelow(RatingTables.Tv, ceilings.Tv);
        var gameAllowed = RatingTables.AllowedAtOrBelow(RatingTables.Game, ceilings.Game);

        // Movie + Episode + Series + Season are gated by movieAllowed / tvAllowed.
        // We pre-resolve "no ceiling" -> null and skip the corresponding clause —
        // EF translates `list == null || ...` cleanly when `list` is a captured
        // local, so the COALESCE is a runtime constant.
        if (movieAllowed != null)
        {
            query = query.Where(m =>
                m.Type != MediaType.Movie ||
                (m.ContentRating != null && movieAllowed.Contains(m.ContentRating)));
        }

        if (tvAllowed != null)
        {
            query = query.Where(m =>
                (m.Type != MediaType.Series && m.Type != MediaType.Season && m.Type != MediaType.Episode) ||
                (m.ContentRating != null && tvAllowed.Contains(m.ContentRating)));
        }

        if (gameAllowed != null)
        {
            query = query.Where(m =>
                m.Type != MediaType.Game ||
                (m.ContentRating != null && gameAllowed.Contains(m.ContentRating)));
        }

        return query;
    }
}
