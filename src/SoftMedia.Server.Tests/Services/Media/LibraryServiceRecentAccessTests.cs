using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Scanning;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// Audit wave-2 H-1 — the recently-added cache is built with an unfiltered system view, so
/// GetRecentlyAddedAsync MUST gate per-caller before returning: the per-library ACL (deny ->
/// empty) and the content-rating ceiling (over-rating titles stripped). These lock that gate.
public class LibraryServiceRecentAccessTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly Guid _libraryId = Guid.NewGuid();
    private readonly Guid _gMovieId = Guid.NewGuid();
    private readonly Guid _rMovieId = Guid.NewGuid();

    public LibraryServiceRecentAccessTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"libsvc-recent-{Guid.NewGuid()}")
            .Options);

        var lib = new Library { Id = _libraryId, Name = "Movies", Type = LibraryType.Movie, Paths = new() { "/m" } };
        var g = new MediaItem { Id = _gMovieId, LibraryId = _libraryId, Title = "Kids Film", SortTitle = "Kids Film", Path = "/m/kids.mkv", Type = MediaType.Movie, ContentRating = "G" };
        var r = new MediaItem { Id = _rMovieId, LibraryId = _libraryId, Title = "Adult Film", SortTitle = "Adult Film", Path = "/m/adult.mkv", Type = MediaType.Movie, ContentRating = "R" };
        _db.Libraries.Add(lib);
        _db.MediaItems.AddRange(g, r);

        // Cache row holds the unfiltered system view (both movies), exactly as
        // UpdateRecentlyAddedCacheAsync would write it.
        var cachedDtos = new[] { g, r }.Select(m => MediaItemDto.FromMediaItem(m)).ToList();
        _db.LibraryRecentCaches.Add(new LibraryRecentCache
        {
            LibraryId = _libraryId,
            CachedJson = JsonSerializer.Serialize(cachedDtos),
            LastUpdated = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private LibraryService Build(LibraryAccess access, UserRatingCeilings ceilings)
    {
        var libraryRepo = new Mock<ILibraryRepository>();
        var mediaRepo = new Mock<IMediaRepository>();
        var scanQueue = new Mock<ILibraryScanQueueService>();
        var imageCache = new Mock<IImageCacheService>();
        var watcher = new LibraryWatcher(null!, NullLogger<LibraryWatcher>.Instance);

        var accessProvider = new Mock<IUserLibraryAccessProvider>();
        accessProvider.Setup(p => p.GetCurrentAsync()).ReturnsAsync(access);
        var ratingProvider = new Mock<IUserContentRatingProvider>();
        ratingProvider.Setup(p => p.GetCurrentAsync()).ReturnsAsync(ceilings);

        return new LibraryService(
            libraryRepo.Object, mediaRepo.Object, scanQueue.Object, imageCache.Object,
            watcher, _db, accessProvider.Object, ratingProvider.Object, NullLogger<LibraryService>.Instance);
    }

    [Fact]
    public async Task DeniedLibrary_ReturnsEmpty()
    {
        // Caller's ACL allows some OTHER library, not this one.
        var svc = Build(LibraryAccess.AllowOnly(new[] { Guid.NewGuid() }), UserRatingCeilings.Unrestricted);

        var items = await svc.GetRecentlyAddedAsync(_libraryId, Guid.NewGuid());

        Assert.Empty(items);
    }

    [Fact]
    public async Task RatingRestricted_StripsOverRatingTitles()
    {
        // Library is allowed, but the caller is capped at PG-13 — the R movie must not appear.
        var ceilings = UserRatingCeilings.From(new User { MaxRating = "PG-13", ContentRatings = "{}" });
        var svc = Build(LibraryAccess.AllowOnly(new[] { _libraryId }), ceilings);

        var items = (await svc.GetRecentlyAddedAsync(_libraryId, Guid.NewGuid())).ToList();

        Assert.Contains(items, i => i.Id == _gMovieId);
        Assert.DoesNotContain(items, i => i.Id == _rMovieId);
    }

    [Fact]
    public async Task UnrestrictedCaller_SeesAll_AndPathIsFileNameOnly()
    {
        var svc = Build(LibraryAccess.Unrestricted, UserRatingCeilings.Unrestricted);

        var items = (await svc.GetRecentlyAddedAsync(_libraryId, Guid.NewGuid())).ToList();

        Assert.Equal(2, items.Count);
        // Audit wave-2 H-1: the DTO must never carry the absolute on-disk path.
        Assert.All(items, i => Assert.DoesNotContain("/m/", i.Path));
        Assert.Contains(items, i => i.Path == "kids.mkv");
    }
}
