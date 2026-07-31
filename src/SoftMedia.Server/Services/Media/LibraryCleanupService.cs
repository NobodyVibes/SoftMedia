using Microsoft.EntityFrameworkCore;

using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// Deletes every derived on-disk artifact for a library's media items when the library is
/// deleted: cached artwork, cast headshots, trickplay sheets, thumbnails, and cached
/// subtitle extractions. Must run BEFORE the library row is deleted — cast exclusivity and
/// subtitle source paths are answered from rows the EF cascade is about to remove.
/// The daily ImageCacheCleanupService sweep is the backstop for anything this misses
/// (e.g. scan-driven hard deletes), but a manual delete should leave nothing behind now.
/// </summary>
public interface ILibraryCleanupService
{
    Task DeleteArtifactsForLibraryAsync(Guid libraryId, IReadOnlyCollection<(Guid Id, MediaType Type)> mediaItems);

    /// <summary>
    /// MC-WI-004 — per-item artifact cleanup (artwork, trickplay, thumbnails, cached
    /// subtitle VTTs) for rows being hard-deleted outside a library delete (scan
    /// retention expiry / retention-0 purge). No DB access: the caller captures
    /// (Id, Type, Path) BEFORE deleting the rows. Cast headshots are deliberately not
    /// handled here — exclusivity needs live cast rows; the daily sweep covers them.
    /// </summary>
    void DeleteArtifactsForItems(IReadOnlyCollection<(Guid Id, MediaType Type, string? Path)> items);
}

public class LibraryCleanupService : ILibraryCleanupService
{
    private readonly AppDbContext _context;
    private readonly IImageCacheService _imageCache;
    private readonly ITrickplayService _trickplay;
    private readonly IThumbnailService _thumbnails;
    private readonly ISubtitleService _subtitles;
    private readonly ILogger<LibraryCleanupService> _logger;

    public LibraryCleanupService(
        AppDbContext context,
        IImageCacheService imageCache,
        ITrickplayService trickplay,
        IThumbnailService thumbnails,
        ISubtitleService subtitles,
        ILogger<LibraryCleanupService> logger)
    {
        _context = context;
        _imageCache = imageCache;
        _trickplay = trickplay;
        _thumbnails = thumbnails;
        _subtitles = subtitles;
        _logger = logger;
    }

    public async Task DeleteArtifactsForLibraryAsync(Guid libraryId, IReadOnlyCollection<(Guid Id, MediaType Type)> mediaItems)
    {
        // Cast headshots. Files are keyed by Person.ExternalId (NOT the Person PK), and
        // Persons are GLOBAL — shared across libraries — so only delete headshots of
        // persons referenced exclusively by this library's items. A person also credited
        // in another library keeps their image.
        var exclusiveExternalIds = await _context.MediaItemCasts
            .AsNoTracking()
            .Where(c => c.MediaItem!.LibraryId == libraryId && c.Person!.ExternalId != null)
            .Where(c => !_context.MediaItemCasts.Any(o =>
                o.PersonId == c.PersonId && o.MediaItem!.LibraryId != libraryId))
            .Select(c => c.Person!.ExternalId!.Value)
            .Distinct()
            .ToListAsync();
        if (exclusiveExternalIds.Count > 0)
        {
            _imageCache.DeleteCastImagesForExternalIds(exclusiveExternalIds);
        }

        // Subtitle cache keys derive from source paths — capture them while rows exist,
        // then run the shared per-item cleanup.
        var paths = await _context.MediaItems
            .AsNoTracking()
            .Where(m => m.LibraryId == libraryId && m.Path != null)
            .Select(m => new { m.Id, m.Path })
            .ToDictionaryAsync(x => x.Id, x => x.Path);
        DeleteArtifactsForItems(mediaItems
            .Select(m => (m.Id, m.Type, paths.GetValueOrDefault(m.Id)))
            .ToList());

        _logger.LogInformation(
            "Library {LibraryId} artifact cleanup: {Items} item(s), {Cast} exclusive cast image key(s)",
            libraryId, mediaItems.Count, exclusiveExternalIds.Count);
    }

    public void DeleteArtifactsForItems(IReadOnlyCollection<(Guid Id, MediaType Type, string? Path)> items)
    {
        // Posters / backdrops / stills / covers, keyed by item id.
        _imageCache.DeleteImagesForLibrary(items.Select(i => (i.Id, i.Type)));

        // Trickplay sheets + on-demand thumbnails, both keyed by item id.
        var trickplayDeleted = 0;
        foreach (var (id, _, _) in items)
        {
            if (_trickplay.DeleteForItem(id)) trickplayDeleted++;
            _thumbnails.DeleteThumbnails(id);
        }

        var subtitlesDeleted = _subtitles.DeleteCachedVttForSourcePaths(
            items.Where(i => !string.IsNullOrEmpty(i.Path)).Select(i => i.Path!));

        _logger.LogDebug(
            "Per-item artifact cleanup: {Items} item(s), {Trickplay} trickplay dir(s), {Subs} cached subtitle file(s)",
            items.Count, trickplayDeleted, subtitlesDeleted);
    }
}
