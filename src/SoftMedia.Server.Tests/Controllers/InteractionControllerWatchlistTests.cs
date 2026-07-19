using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using Xunit;

namespace SoftMedia.Server.Tests.Controllers;

/// <summary>
/// Coverage for the watchlist toggle endpoint on <see cref="InteractionController"/>.
/// The watchlist is intentionally scoped to non-music media — playlists cover
/// "I'll come back to this" for music. The controller rejects Audio/Album/Artist
/// with 400; everything else flows into the service.
/// </summary>
public class InteractionControllerWatchlistTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly Mock<IUserMediaInteractionService> _interactionService = new();
    private readonly Guid _userId = Guid.NewGuid();

    public InteractionControllerWatchlistTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"watchlist-toggle-{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(options);
    }

    public void Dispose() => _context.Dispose();

    private Guid SeedItem(MediaType type)
    {
        var id = Guid.NewGuid();
        _context.MediaItems.Add(new MediaItem
        {
            Id = id,
            Title = $"{type}",
            SortTitle = $"{type}",
            Path = $"/lib/{id}",
            Type = type,
            LibraryId = Guid.NewGuid(),
        });
        _context.SaveChanges();
        return id;
    }

    private InteractionController NewController()
    {
        var controller = new InteractionController(
            _context,
            NullLogger<InteractionController>.Instance,
            Mock.Of<IRecommendationService>(),
            _interactionService.Object,
            Mock.Of<SoftMedia.Server.Services.Security.LibraryAccess.IUserLibraryAccessProvider>(),
            Mock.Of<SoftMedia.Server.Services.Security.ContentRating.IUserContentRatingProvider>(),
            new SoftMedia.Server.Services.Sessions.ActiveStreamRegistry(),
            new SoftMedia.Server.Services.Sessions.TerminatedSessionRegistry(),
            Mock.Of<SoftMedia.Server.Services.Transcoding.ITranscodeService>(s =>
                s.GetAllSessions() == Enumerable.Empty<SoftMedia.Server.Services.Transcoding.Models.TranscodeSession>()));

        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, _userId.ToString()) });
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    [Fact]
    public async Task ToggleWatchlist_MissingMedia_ReturnsNotFound()
    {
        var controller = NewController();

        var result = await controller.ToggleWatchlist(Guid.NewGuid(), new WatchlistRequest { IsWatchlisted = true });

        Assert.IsType<NotFoundResult>(result);
        _interactionService.Verify(
            s => s.ToggleWatchlistAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Theory]
    [InlineData(MediaType.Audio)]
    [InlineData(MediaType.Album)]
    [InlineData(MediaType.Artist)]
    public async Task ToggleWatchlist_MusicTypes_ReturnsBadRequest(MediaType musicType)
    {
        var mediaId = SeedItem(musicType);
        var controller = NewController();

        var result = await controller.ToggleWatchlist(mediaId, new WatchlistRequest { IsWatchlisted = true });

        Assert.IsType<BadRequestObjectResult>(result);
        _interactionService.Verify(
            s => s.ToggleWatchlistAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Theory]
    [InlineData(MediaType.Movie)]
    [InlineData(MediaType.Series)]
    [InlineData(MediaType.Book)]
    [InlineData(MediaType.ComicSeries)]
    [InlineData(MediaType.Game)]
    public async Task ToggleWatchlist_AllowedTypes_DelegatesToService(MediaType allowedType)
    {
        var mediaId = SeedItem(allowedType);
        var controller = NewController();

        var result = await controller.ToggleWatchlist(mediaId, new WatchlistRequest { IsWatchlisted = true });

        Assert.IsType<OkResult>(result);
        _interactionService.Verify(
            s => s.ToggleWatchlistAsync(_userId, mediaId, true),
            Times.Once);
    }
}
