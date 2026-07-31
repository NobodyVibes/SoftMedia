using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Infrastructure;

/// <summary>
/// DV-WI-003 — GetEpisodeCountAsync counts EPISODES, not files: duplicate copies of one
/// episode share an EpisodeNumber and must not inflate the season count, while files
/// whose episode number could not be parsed (null/0) each keep counting individually.
/// Runs on real SQLite so the query shape is proven translatable.
/// </summary>
public class MediaRepositoryEpisodeCountTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _seriesId = Guid.NewGuid();
    private readonly Guid _libraryId = Guid.NewGuid();

    public MediaRepositoryEpisodeCountTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
        ctx.Libraries.Add(new Library { Id = _libraryId, Name = "TV", Type = LibraryType.TV });
        ctx.MediaItems.Add(new MediaItem { Id = _seriesId, LibraryId = _libraryId, Type = MediaType.Series, Title = "Show" });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private void AddEpisode(int season, int? episode)
    {
        using var ctx = new AppDbContext(_options);
        ctx.MediaItems.Add(new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = _libraryId, Type = MediaType.Episode,
            Title = $"S{season}E{episode}", SeriesId = _seriesId, SeasonNumber = season, EpisodeNumber = episode,
        });
        ctx.SaveChanges();
    }

    private MediaRepository Build()
    {
        var ratings = new Mock<IUserContentRatingProvider>();
        ratings.Setup(r => r.GetCurrentAsync()).ReturnsAsync(UserRatingCeilings.Unrestricted);
        var access = new Mock<IUserLibraryAccessProvider>();
        access.Setup(a => a.GetCurrentAsync()).ReturnsAsync(LibraryAccess.Unrestricted);
        return new MediaRepository(new AppDbContext(_options), ratings.Object, access.Object);
    }

    [Fact]
    public async Task Duplicate_copies_of_an_episode_count_once()
    {
        for (var n = 1; n <= 10; n++) AddEpisode(1, n);
        AddEpisode(1, 3); // second file of E3

        Assert.Equal(10, await Build().GetEpisodeCountAsync(_seriesId, 1));
    }

    [Fact]
    public async Task Unnumbered_files_each_count_individually()
    {
        // Three distinct files whose episode number could not be parsed (two land at 0,
        // one at null) — they are NOT duplicates of each other and must all count.
        AddEpisode(1, 1);
        AddEpisode(1, 0);
        AddEpisode(1, 0);
        AddEpisode(1, null);

        Assert.Equal(4, await Build().GetEpisodeCountAsync(_seriesId, 1));
    }

    [Fact]
    public async Task Other_seasons_do_not_leak_into_the_count()
    {
        AddEpisode(1, 1);
        AddEpisode(1, 2);
        AddEpisode(2, 1);
        AddEpisode(2, 1); // duplicate in season 2

        Assert.Equal(2, await Build().GetEpisodeCountAsync(_seriesId, 1));
        Assert.Equal(1, await Build().GetEpisodeCountAsync(_seriesId, 2));
    }
}
