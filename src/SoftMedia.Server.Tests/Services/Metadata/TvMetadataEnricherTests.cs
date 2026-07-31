using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

public class TvMetadataEnricherTests : IDisposable
{
    private readonly AppDbContext _dbContext;
    private readonly TvMetadataEnricher _enricher;

    public TvMetadataEnricherTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);
        _enricher = new TvMetadataEnricher(_dbContext, Mock.Of<ILogger<TvMetadataEnricher>>());
    }

    public void Dispose() => _dbContext.Dispose();

    private async Task<MediaItem> SeedSeriesAsync()
    {
        var series = new MediaItem { Id = Guid.NewGuid(), Title = "Test Series", Type = MediaType.Series };
        _dbContext.MediaItems.Add(series);
        await _dbContext.SaveChangesAsync();
        return series;
    }

    private MediaItem AddEpisode(Guid seriesId, int season, int episode, string? backdropUrl)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(), SeriesId = seriesId, Type = MediaType.Episode,
            Title = $"Ep {episode}", SeasonNumber = season, EpisodeNumber = episode,
            BackdropUrl = backdropUrl
        };
        _dbContext.MediaItems.Add(item);
        return item;
    }

    private static MetadataResult ResultWithStill(int season, int episode, string stillUrl) => new()
    {
        Episodes = new List<EpisodeMetadata>
        {
            new() { SeasonNumber = season, EpisodeNumber = episode, StillUrl = stillUrl }
        }
    };

    [Fact]
    public async Task PropagateEpisodeMetadata_DoesNotOverwriteCachedLocalStill()
    {
        // Regression: re-enrichment stamped the remote TVMaze URL over an
        // already-cached "/cache/images/…" still, flipping the episode back onto
        // the image proxy until the download queue caught up.
        var series = await SeedSeriesAsync();
        var cachedPath = "/cache/images/tv/still_s01e01.jpg";
        var ep = AddEpisode(series.Id, 1, 1, cachedPath);
        await _dbContext.SaveChangesAsync();

        await _enricher.PropagateEpisodeMetadataAsync(
            series, ResultWithStill(1, 1, "https://static.tvmaze.com/still.jpg"));

        Assert.Equal(cachedPath, ep.BackdropUrl);
    }

    [Fact]
    public async Task PropagateEpisodeMetadata_SetsRemoteStill_OnAllUncachedDuplicateRows()
    {
        // Duplicate files of the same episode → two rows for (S1, E1); both start
        // without a cached still, so both must receive the remote URL (the download
        // queue later rewrites both to the shared local path).
        var series = await SeedSeriesAsync();
        var copy1 = AddEpisode(series.Id, 1, 1, backdropUrl: null);
        var copy2 = AddEpisode(series.Id, 1, 1, backdropUrl: null);
        await _dbContext.SaveChangesAsync();

        var remoteUrl = "https://static.tvmaze.com/still.jpg";
        await _enricher.PropagateEpisodeMetadataAsync(series, ResultWithStill(1, 1, remoteUrl));

        Assert.Equal(remoteUrl, copy1.BackdropUrl);
        Assert.Equal(remoteUrl, copy2.BackdropUrl);
    }
}
