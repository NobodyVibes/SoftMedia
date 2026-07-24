using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Metadata;
using SoftMedia.Server.Services.Scanning;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Scanning;

/// <summary>
/// SR-WI-036 — global Metadata Refresh mode coverage. "All" previously meant Movies+Series
/// only; it must now span music artists/albums, books, comics, and games, routed through the
/// central metadata queue with the correct library type per item. "Running" stays TV-only by
/// design, and locked/missing items are never enqueued.
/// </summary>
public class MetadataRefreshServiceModeTests
{
    private static (MetadataRefreshService svc, Mock<IMetadataQueue> queue, ServiceProvider provider)
        Build(string mode, params MediaItem[] items)
    {
        var queue = new Mock<IMetadataQueue>();
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetSettingAsync("MetadataRefreshMode"))
            .ReturnsAsync(new AppSetting { Key = "MetadataRefreshMode", Value = mode });
        var scanQueue = new Mock<ILibraryScanQueueService>();

        var services = new ServiceCollection();
        var dbName = $"refresh-mode-{Guid.NewGuid()}";
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton(queue.Object);
        services.AddSingleton(settings.Object);
        services.AddSingleton(scanQueue.Object);
        services.AddSingleton<IScheduledTaskRegistry>(new ScheduledTaskRegistry());
        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.MediaItems.AddRange(items);
            db.SaveChanges();
        }

        var svc = new MetadataRefreshService(provider, NullLogger<MetadataRefreshService>.Instance);
        return (svc, queue, provider);
    }

    private static MediaItem Item(MediaType type, bool locked = false, bool missing = false)
        => new()
        {
            Id = Guid.NewGuid(),
            Title = type.ToString(),
            Type = type,
            LibraryId = Guid.NewGuid(),
            MetadataLocked = locked,
            IsMissing = missing,
        };

    [Fact]
    public async Task AllMode_CoversMusicBooksComicsAndGames_WithCorrectLibraryTypes()
    {
        var movie = Item(MediaType.Movie);
        var series = Item(MediaType.Series);
        var artist = Item(MediaType.Artist);
        var album = Item(MediaType.Album);
        var book = Item(MediaType.Book);
        var comicSeries = Item(MediaType.ComicSeries);
        var comicIssue = Item(MediaType.ComicIssue);
        var game = Item(MediaType.Game);
        var episode = Item(MediaType.Episode);  // covered via its series fetch — never enqueued alone
        var track = Item(MediaType.Audio);      // covered via its album/artist
        var (svc, queue, _) = Build("All",
            movie, series, artist, album, book, comicSeries, comicIssue, game, episode, track);

        await svc.RunRefreshJobAsync(new LibraryScanJob(), CancellationToken.None);

        queue.Verify(q => q.EnqueueMetadataRefreshAsync(movie.Id, LibraryType.Movie, true, 0, null), Times.Once);
        queue.Verify(q => q.EnqueueMetadataRefreshAsync(series.Id, LibraryType.TV, true, 0, null), Times.Once);
        queue.Verify(q => q.EnqueueMetadataRefreshAsync(artist.Id, LibraryType.Music, true, 0, null), Times.Once);
        queue.Verify(q => q.EnqueueMetadataRefreshAsync(album.Id, LibraryType.Music, true, 0, null), Times.Once);
        queue.Verify(q => q.EnqueueMetadataRefreshAsync(book.Id, LibraryType.Book, true, 0, null), Times.Once);
        queue.Verify(q => q.EnqueueMetadataRefreshAsync(comicSeries.Id, LibraryType.Book, true, 0, null), Times.Once);
        queue.Verify(q => q.EnqueueMetadataRefreshAsync(comicIssue.Id, LibraryType.Book, true, 0, null), Times.Once);
        queue.Verify(q => q.EnqueueMetadataRefreshAsync(game.Id, LibraryType.Game, true, 0, null), Times.Once);
        // Exactly the eight above — episodes/tracks ride along with their parents.
        queue.Verify(q => q.EnqueueMetadataRefreshAsync(
            It.IsAny<Guid>(), It.IsAny<LibraryType>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<Guid?>()), Times.Exactly(8));
    }

    [Fact]
    public async Task AllMode_SkipsLockedAndMissingItems()
    {
        var locked = Item(MediaType.Movie, locked: true);
        var missing = Item(MediaType.Book, missing: true);
        var normal = Item(MediaType.Game);
        var (svc, queue, _) = Build("All", locked, missing, normal);

        await svc.RunRefreshJobAsync(new LibraryScanJob(), CancellationToken.None);

        queue.Verify(q => q.EnqueueMetadataRefreshAsync(normal.Id, LibraryType.Game, true, 0, null), Times.Once);
        queue.Verify(q => q.EnqueueMetadataRefreshAsync(
            It.IsAny<Guid>(), It.IsAny<LibraryType>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<Guid?>()), Times.Exactly(1));
    }

    [Fact]
    public async Task RunningMode_StaysSeriesOnly()
    {
        var series = Item(MediaType.Series);
        var movie = Item(MediaType.Movie);
        var album = Item(MediaType.Album);
        var (svc, queue, _) = Build("Running", series, movie, album);

        await svc.RunRefreshJobAsync(new LibraryScanJob(), CancellationToken.None);

        queue.Verify(q => q.EnqueueMetadataRefreshAsync(series.Id, LibraryType.TV, true, 0, null), Times.Once);
        queue.Verify(q => q.EnqueueMetadataRefreshAsync(
            It.IsAny<Guid>(), It.IsAny<LibraryType>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<Guid?>()), Times.Exactly(1));
    }

    [Fact]
    public async Task VariableMode_StaysMoviesAndSeries_WithoutImageRefresh()
    {
        var series = Item(MediaType.Series);
        var movie = Item(MediaType.Movie);
        var album = Item(MediaType.Album);
        var (svc, queue, _) = Build("Variable", series, movie, album);

        await svc.RunRefreshJobAsync(new LibraryScanJob(), CancellationToken.None);

        // Variable mode's contract: metadata only, no image refresh (refreshImages: false).
        queue.Verify(q => q.EnqueueMetadataRefreshAsync(series.Id, LibraryType.TV, false, 0, null), Times.Once);
        queue.Verify(q => q.EnqueueMetadataRefreshAsync(movie.Id, LibraryType.Movie, false, 0, null), Times.Once);
        queue.Verify(q => q.EnqueueMetadataRefreshAsync(
            It.IsAny<Guid>(), It.IsAny<LibraryType>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<Guid?>()), Times.Exactly(2));
    }
}
