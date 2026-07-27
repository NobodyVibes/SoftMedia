using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Security.LibraryAccess;

namespace SoftMedia.Server.Services.Media;

public interface ISmartPlaylistEvaluator
{
    /// <summary>Tracks matching <paramref name="rules"/>, ordered and capped.</summary>
    Task<List<MediaItem>> EvaluateAsync(
        SmartPlaylistRules rules, Guid ownerUserId, LibraryAccess access, CancellationToken ct = default);

    /// <summary>
    /// How many tracks the playlist currently yields, without materialising them.
    /// Used by the index, where a dozen playlists each loading their tracks just
    /// to print a count would be wasteful.
    /// </summary>
    Task<int> CountAsync(
        SmartPlaylistRules rules, Guid ownerUserId, LibraryAccess access, CancellationToken ct = default);

    /// <summary>First <paramref name="take"/> matches — the cover mosaic's source.</summary>
    Task<List<MediaItem>> PreviewAsync(
        SmartPlaylistRules rules, Guid ownerUserId, LibraryAccess access, int take, CancellationToken ct = default);
}

/// <summary>
/// Turns a <see cref="SmartPlaylistRules"/> into the tracks it describes.
///
/// Two rules govern everything here:
///
/// 1. Play signals are the OWNER's. "Most played" counts the owner's
///    <see cref="PlaybackHistory"/> rows; "unplayed" means the owner has no such
///    rows. <see cref="MediaItem.PlayCount"/> and <see cref="MediaItem.LastPlayed"/>
///    are all-user aggregates and are deliberately never touched — on a shared
///    server they would rank a private playlist by the household's listening, and
///    they also silently exclude anyone who turned off
///    <see cref="User.RecordPlaybackHistory"/>.
///
/// 2. Evaluation always runs through the VIEWER's library ACL and always caps at
///    <see cref="SmartPlaylistRules.Limit"/>, so a smart playlist can neither
///    surface a library the caller is denied nor pull an unbounded result set
///    into memory.
/// </summary>
public class SmartPlaylistEvaluator : ISmartPlaylistEvaluator
{
    private readonly AppDbContext _db;

    public SmartPlaylistEvaluator(AppDbContext db) => _db = db;

    public async Task<List<MediaItem>> EvaluateAsync(
        SmartPlaylistRules rules, Guid ownerUserId, LibraryAccess access, CancellationToken ct = default)
        => await OrderedQuery(rules, ownerUserId, access)
            .Take(Math.Clamp(rules.Limit, 1, SmartPlaylistRules.MaxLimit))
            .ToListAsync(ct);

    public async Task<int> CountAsync(
        SmartPlaylistRules rules, Guid ownerUserId, LibraryAccess access, CancellationToken ct = default)
    {
        // The limit is part of the playlist's definition, so the count has to
        // respect it: a rule matching 900 tracks with a limit of 100 IS a
        // 100-track playlist, and saying "900 tracks" on the card would not match
        // what opening it shows.
        var matched = await FilteredQuery(rules, ownerUserId, access).CountAsync(ct);
        return Math.Min(matched, Math.Clamp(rules.Limit, 1, SmartPlaylistRules.MaxLimit));
    }

    public async Task<List<MediaItem>> PreviewAsync(
        SmartPlaylistRules rules, Guid ownerUserId, LibraryAccess access, int take, CancellationToken ct = default)
        => await OrderedQuery(rules, ownerUserId, access)
            .Take(Math.Clamp(Math.Min(take, rules.Limit), 1, SmartPlaylistRules.MaxLimit))
            .ToListAsync(ct);

    /// <summary>Membership before ordering — shared by the count and the fetch.</summary>
    private IQueryable<MediaItem> FilteredQuery(
        SmartPlaylistRules rules, Guid ownerUserId, LibraryAccess access)
    {
        // v1 playlists hold audio only, exactly as the manual path enforces on add.
        var query = _db.MediaItems
            .AsNoTracking()
            .ApplyLibraryAccessFilter(access)
            .ExcludeMissing()
            .Where(m => m.Type == MediaType.Audio);

        if (rules.FavoritesOnly)
        {
            query = query.Where(m => _db.UserMediaInteractions
                .Any(i => i.UserId == ownerUserId && i.MediaItemId == m.Id && i.IsFavorite));
        }

        if (rules.UnplayedOnly)
        {
            query = query.Where(m => !_db.PlaybackHistory
                .Any(h => h.UserId == ownerUserId && h.MediaItemId == m.Id));
        }

        if (rules.AddedWithinDays is { } days)
        {
            // Computed here rather than inlined so EF parameterises one value
            // instead of translating date arithmetic per row.
            var cutoff = DateTime.UtcNow.AddDays(-days);
            query = query.Where(m => m.DateAdded >= cutoff);
        }

        if (!string.IsNullOrWhiteSpace(rules.Genre))
        {
            var genre = rules.Genre.Trim();
            query = query.Where(m => m.MediaItemGenres
                .Any(mg => mg.Genre != null && mg.Genre.Name == genre));
        }

        if (rules.ArtistId is { } artistId)
        {
            query = query.Where(m => m.ArtistId == artistId);
        }

        return query;
    }

    private IQueryable<MediaItem> OrderedQuery(
        SmartPlaylistRules rules, Guid ownerUserId, LibraryAccess access)
    {
        var query = FilteredQuery(rules, ownerUserId, access)
            .Include(m => m.Album)
            .Include(m => m.MediaItemGenres).ThenInclude(mg => mg.Genre);

        // Ties break on Title so paging and re-reads are stable; without it two
        // tracks added in the same scan second could swap places between reads
        // and a reordering playlist looks broken.
        return rules.Sort switch
        {
            SmartPlaylistSort.MostPlayed => query
                .OrderByDescending(m => _db.PlaybackHistory
                    .Count(h => h.UserId == ownerUserId && h.MediaItemId == m.Id))
                .ThenBy(m => m.Title),

            SmartPlaylistSort.RecentlyPlayed => query
                .OrderByDescending(m => _db.PlaybackHistory
                    .Where(h => h.UserId == ownerUserId && h.MediaItemId == m.Id)
                    .Max(h => (DateTime?)h.LastBeatAt))
                .ThenBy(m => m.Title),

            SmartPlaylistSort.Title => query.OrderBy(m => m.Title),

            SmartPlaylistSort.Artist => query
                .OrderBy(m => m.Artist != null ? m.Artist.Title : string.Empty)
                .ThenBy(m => m.Title),

            _ => query.OrderByDescending(m => m.DateAdded).ThenBy(m => m.Title),
        };
    }
}
