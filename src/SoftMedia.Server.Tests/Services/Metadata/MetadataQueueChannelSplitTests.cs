using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

/// <summary>
/// SM-WI-070 — Books no longer queue behind the movie backlog (the maintainer-observed
/// "only one library type progresses at a time"): with the Shared channel's workers all
/// saturated by movies, a book must still be processed on its own channel.
/// </summary>
public class MetadataQueueChannelSplitTests
{
    [Fact]
    public async Task Book_IsProcessed_WhileMovieBacklogSaturatesTheSharedChannel()
    {
        var movieGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bookProcessed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var aggregator = new Mock<IMetadataAggregator>();
        aggregator
            .Setup(a => a.EnrichMediaItemAsync(It.IsAny<MediaItem>(), LibraryType.Movie, It.IsAny<bool>()))
            .Returns(() => movieGate.Task); // movies block until released
        aggregator
            .Setup(a => a.EnrichMediaItemAsync(It.IsAny<MediaItem>(), LibraryType.Book, It.IsAny<bool>()))
            .Callback(() => bookProcessed.TrySetResult(true))
            .Returns(Task.CompletedTask);

        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetSettingAsync("MetadataEnrichmentMode", "Relaxed")).ReturnsAsync("Relaxed");

        var notifications = new Mock<IMediaNotificationService>();

        var dbName = $"channels-{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton(aggregator.Object);
        services.AddSingleton(settings.Object);
        services.AddSingleton(notifications.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        // Seed 12 movies (more than the Shared channel's 10 workers) + 1 book.
        // Real names where we have them.
        var movieIds = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToList();
        var bookId = Guid.NewGuid();
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var (id, i) in movieIds.Select((id, i) => (id, i)))
            {
                db.MediaItems.Add(new MediaItem
                {
                    Id = id, Title = $"Movie {i}", Type = MediaType.Movie,
                    Path = $@"X:\movies\movie{i}.mkv", LibraryId = Guid.NewGuid(),
                });
            }
            db.MediaItems.Add(new MediaItem
            {
                Id = bookId, Title = "Dune", Type = MediaType.Book,
                Path = @"X:\books\1 - Dune - Frank Herbert.epub", LibraryId = Guid.NewGuid(),
                PosterUrl = "http://example.com/c.jpg", MetadataHash = "h", // complete → no retry churn
            });
            await db.SaveChangesAsync();
        }

        var service = new MetadataQueueService(
            scopeFactory, notifications.Object, NullLogger<MetadataQueueService>.Instance);

        // Movies first — before the split they filled the one Shared channel FIFO and
        // the book waited behind all of them.
        foreach (var id in movieIds)
        {
            await service.EnqueueMetadataRefreshAsync(id, LibraryType.Movie);
        }
        await service.EnqueueMetadataRefreshAsync(bookId, LibraryType.Book);

        await service.StartAsync(CancellationToken.None);
        try
        {
            var completed = await Task.WhenAny(bookProcessed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(bookProcessed.Task, completed); // book progressed while every movie was still blocked
            Assert.False(movieGate.Task.IsCompleted);   // movies really were parked the whole time
        }
        finally
        {
            movieGate.TrySetResult(true);
            await service.StopAsync(CancellationToken.None);
        }
    }
}
