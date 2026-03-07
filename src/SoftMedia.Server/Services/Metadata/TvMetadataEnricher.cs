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

            // Still URL 
            if (!string.IsNullOrEmpty(epData.StillUrl))
            {
                var epMeta = MetadataJsonHelper.Parse(child.MetadataJson ?? "{}");
                epMeta["still"] = epData.StillUrl;
                child.MetadataJson = JsonSerializer.Serialize(epMeta);
            }

            updated++;
        }

        if (updated > 0)
        {
            _logger.LogInformation("[TvMetadataEnricher] Propagated metadata to {Count} episodes for '{Series}'", 
                updated, series.Title);
            await _context.SaveChangesAsync();
        }
    }
}
