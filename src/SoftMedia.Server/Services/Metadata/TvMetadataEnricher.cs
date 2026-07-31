using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using System.Text.Json;
using SoftMedia.Server.Helpers;

namespace SoftMedia.Server.Services.Metadata;

public interface ITvMetadataEnricher
{
    Task FilterToLocalEpisodesAsync(MediaItem series, MetadataResult metadata);
    Task PropagateEpisodeMetadataAsync(MediaItem series, MetadataResult metadata);
    Task PropagateSeasonMetadataAsync(MediaItem series, MetadataResult metadata);
}

public class TvMetadataEnricher : ITvMetadataEnricher
{
    private readonly AppDbContext _context;
    private readonly ILogger<TvMetadataEnricher> _logger;

    public TvMetadataEnricher(AppDbContext context, ILogger<TvMetadataEnricher> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task FilterToLocalEpisodesAsync(MediaItem series, MetadataResult metadata)
    {
        // 1. Fetch existing Seasons and Episodes
        var existingSeasons = await _context.MediaItems
            .Where(m => m.SeriesId == series.Id && m.Type == MediaType.Season)
            .Select(m => m.SeasonNumber ?? -1)
            .ToListAsync();

        var existingEpisodes = await _context.MediaItems
            .Where(m => m.SeriesId == series.Id && m.Type == MediaType.Episode)
            .Select(m => new { S = m.SeasonNumber ?? 0, E = m.EpisodeNumber ?? 0 })
            .ToListAsync();

        var seasonSet = new HashSet<int>(existingSeasons);
        var episodeSet = new HashSet<(int, int)>(existingEpisodes.Select(x => (x.S, x.E)));

        // 2. Filter Seasons
        if (metadata.Seasons != null)
        {
            metadata.Seasons = metadata.Seasons.Where(s => seasonSet.Contains(s.Number)).ToList();
        }

        // 3. Filter Episodes
        if (metadata.Episodes != null)
        {
            metadata.Episodes = metadata.Episodes.Where(e => episodeSet.Contains((e.SeasonNumber, e.EpisodeNumber))).ToList();
        }
    }

    public async Task PropagateEpisodeMetadataAsync(MediaItem series, MetadataResult metadata)
    {
        if (metadata.Episodes == null || metadata.Episodes.Count == 0) return;

        // Build a lookup: (season, episode) -> episode metadata
        var episodeLookup = metadata.Episodes.ToDictionary(e => (e.SeasonNumber, e.EpisodeNumber));

        // Fetch child episodes from DB
        var childEpisodes = await _context.MediaItems
            .Where(m => m.SeriesId == series.Id && m.Type == MediaType.Episode)
            .ToListAsync();

        int updated = 0;
        foreach (var child in childEpisodes)
        {
            var sn = child.SeasonNumber ?? 0;
            var en = child.EpisodeNumber ?? 0;
            
            if (!episodeLookup.TryGetValue((sn, en), out var epData))
                continue;

            // Title
            if (!string.IsNullOrEmpty(epData.Name)) child.Title = epData.Name;

            // ReleaseDate
            if (epData.AirDate.HasValue) child.ReleaseDate = epData.AirDate.Value;

            // Overview
            if (!string.IsNullOrEmpty(epData.Summary)) child.Overview = epData.Summary;

            // Still URL -> BackdropUrl (promoted column, episode stills are backdrop-like images).
            // Never overwrite an already-cached local still ("/cache/images/…" written by
            // ImageDownloadQueueService) with the remote URL — that flipped every episode back
            // onto the image proxy on each enrichment pass until the download queue caught up
            // (same rule as MetadataAggregator's poster promotion and TvScanner's scan path).
            // A missing cache FILE still heals: the extractor reads epData.StillUrl, not this
            // column, so the download is queued regardless and re-caches under the same key.
            var hasLocalStill = !string.IsNullOrEmpty(child.BackdropUrl)
                && child.BackdropUrl.StartsWith("/cache/", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(epData.StillUrl) && !hasLocalStill)
            {
                child.BackdropUrl = epData.StillUrl;
            }

            updated++;
        }

        if (updated > 0)
        {
            _logger.LogInformation("[TvMetadataEnricher] Propagated metadata to {Count} episodes for '{Series}'",
                updated, series.Title);
            // NOTE: SaveChangesAsync is NOT called here. The caller (MetadataQueueService.ProcessItemAsync)
            // performs a single SaveChangesAsync after all enrichment steps complete, ensuring atomicity.
        }
    }

    public async Task PropagateSeasonMetadataAsync(MediaItem series, MetadataResult metadata)
    {
        if (metadata.Seasons == null || metadata.Seasons.Count == 0) return;

        var seasonEntities = await _context.MediaItems
            .Where(m => m.SeriesId == series.Id && m.Type == MediaType.Season)
            .ToListAsync();

        var seasonMap = seasonEntities.ToDictionary(s => s.SeasonNumber ?? -1);

        foreach (var seasonMeta in metadata.Seasons)
        {
            if (!seasonMap.TryGetValue(seasonMeta.Number, out var seasonEntity)) continue;

            // Set premiere date if not yet populated
            if (seasonMeta.PremiereDate.HasValue && !seasonEntity.ReleaseDate.HasValue)
                seasonEntity.ReleaseDate = seasonMeta.PremiereDate.Value;

            // NOTE: PosterUrl is intentionally NOT set here.
            // Setting a remote URL that the image proxy might fail to serve (e.g. TVMaze
            // seasons with no dedicated art) causes the frontend to show a broken image
            // instead of gracefully falling back to the series poster or season number.
            // PosterUrl is set exclusively by ImageDownloadQueueService after a successful
            // background download, ensuring only verified local paths are stored.
        }
        // NOTE: SaveChangesAsync is NOT called here — caller handles atomicity.
    }
}
