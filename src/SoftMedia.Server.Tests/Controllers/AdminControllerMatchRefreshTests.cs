using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Metadata;
using SoftMedia.Server.Services.Scanning;
using Xunit;

namespace SoftMedia.Server.Tests.Controllers;

/// <summary>
/// SR-WI-036 — POST match/{id}/refresh (per-item metadata refresh) and the exhaustion-clear
/// on fix-match apply. The refresh endpoint must clear IsRetryExhausted + pending retry rows
/// and enqueue through the central metadata queue; locked items get a 409, not a silent skip.
/// </summary>
public class AdminControllerMatchRefreshTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly Mock<IMetadataQueue> _queue = new();
    private readonly Mock<IImageCacheService> _imageCache = new();
    private readonly List<IMetadataProvider> _providers = new();

    public AdminControllerMatchRefreshTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"admin-refresh-{Guid.NewGuid()}").Options);
    }

    private AdminController BuildController()
    {
        var controller = new AdminController(
            new LibraryWatcher(new Mock<IServiceScopeFactory>().Object, NullLogger<LibraryWatcher>.Instance),
            NullLogger<AdminController>.Instance,
            _providers,
            Mock.Of<IRecommendationService>(),
            Mock.Of<IBackupService>(),
            new ScheduledTaskRegistry(),
            Array.Empty<IManuallyTriggerableTask>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return controller;
    }

    private async Task<MediaItem> SeedItemAsync(
        LibraryType libraryType = LibraryType.Music,
        MediaType mediaType = MediaType.Album,
        bool exhausted = true,
        bool locked = false,
        bool withPendingRetry = true)
    {
        var library = new Library { Id = Guid.NewGuid(), Name = "Lib", Type = libraryType };
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Title = "Item",
            Type = mediaType,
            LibraryId = library.Id,
            Library = library,
            IsRetryExhausted = exhausted,
            MetadataLocked = locked,
            MetadataLockedAt = locked ? DateTime.UtcNow : null,
        };
        _db.Libraries.Add(library);
        _db.MediaItems.Add(item);
        if (withPendingRetry)
        {
            _db.MetadataRetries.Add(new MetadataRetry
            {
                MediaItemId = item.Id,
                LibraryType = libraryType,
                RetryCount = 2,
                NextAttempt = DateTime.UtcNow.AddMinutes(30),
                CreatedAt = DateTime.UtcNow,
            });
        }
        await _db.SaveChangesAsync();
        return item;
    }

    [Fact]
    public async Task Refresh_ClearsExhaustion_RemovesRetryRows_AndEnqueuesWithLibraryType()
    {
        var item = await SeedItemAsync(LibraryType.Music, MediaType.Album);
        var controller = BuildController();

        var result = await controller.RefreshMetadata(item.Id, _db, _queue.Object, _imageCache.Object, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var saved = await _db.MediaItems.SingleAsync(m => m.Id == item.Id);
        Assert.False(saved.IsRetryExhausted);
        Assert.Empty(await _db.MetadataRetries.ToListAsync());
        // Enqueued through the central queue with the OWNING LIBRARY's type (Music, not TV).
        _queue.Verify(q => q.EnqueueMetadataRefreshAsync(item.Id, LibraryType.Music, true, 0, null), Times.Once);
        // SR-WI-037: cached provider artwork invalidated so the refresh re-downloads images.
        _imageCache.Verify(c => c.InvalidateCachedImagesAsync(item.Id), Times.Once);
    }

    [Fact]
    public async Task Refresh_LockedItem_Returns409_AndDoesNotEnqueueOrClear()
    {
        var item = await SeedItemAsync(locked: true);
        var controller = BuildController();

        var result = await controller.RefreshMetadata(item.Id, _db, _queue.Object, _imageCache.Object, CancellationToken.None);

        // SR-WI-061: 409 now ships as an RFC 7807 ProblemDetails body.
        var conflict = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(conflict.Value);
        Assert.Equal("Metadata is locked for this item. Unlock it first to refresh.", problem.Detail);
        _imageCache.Verify(c => c.InvalidateCachedImagesAsync(It.IsAny<Guid>()), Times.Never);
        var saved = await _db.MediaItems.SingleAsync(m => m.Id == item.Id);
        Assert.True(saved.IsRetryExhausted);                        // untouched
        Assert.Single(await _db.MetadataRetries.ToListAsync());     // bookkeeping untouched
        _queue.Verify(q => q.EnqueueMetadataRefreshAsync(
            It.IsAny<Guid>(), It.IsAny<LibraryType>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task Refresh_UnknownItem_Returns404()
    {
        var controller = BuildController();

        var result = await controller.RefreshMetadata(Guid.NewGuid(), _db, _queue.Object, _imageCache.Object, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        _queue.Verify(q => q.EnqueueMetadataRefreshAsync(
            It.IsAny<Guid>(), It.IsAny<LibraryType>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task ApplyMatch_ClearsExhaustionAndRetryRows_SoUnlockCanRefreshLater()
    {
        var item = await SeedItemAsync(LibraryType.Movie, MediaType.Movie);
        var provider = new Mock<ISearchableMetadataProvider>();
        provider.SetupGet(p => p.ProviderName).Returns("TestProv");
        provider.SetupGet(p => p.SupportedType).Returns(LibraryType.Movie);
        provider.Setup(p => p.FetchByCandidateAsync("cand-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataResult { Title = "Fixed Title" });
        _providers.Add(provider.Object);
        var controller = BuildController();

        var result = await controller.ApplyMatch(
            item.Id, new ApplyMatchRequest("TestProv", "cand-1"), _db, CancellationToken.None);

        Assert.IsType<OkResult>(result);
        var saved = await _db.MediaItems.SingleAsync(m => m.Id == item.Id);
        Assert.True(saved.MetadataLocked);              // apply still locks
        Assert.False(saved.IsRetryExhausted);           // exhaustion superseded by the admin match
        Assert.Empty(await _db.MetadataRetries.ToListAsync());
        Assert.Equal("Fixed Title", saved.Title);
    }

    public void Dispose() => _db.Dispose();
}
