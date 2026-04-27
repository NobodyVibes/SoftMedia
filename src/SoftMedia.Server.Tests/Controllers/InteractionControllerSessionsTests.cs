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
/// Coverage for ER-052's reading-session endpoints. Shares the InMemory
/// DbContext + ClaimsPrincipal plumbing with sibling tests.
/// </summary>
public class InteractionControllerSessionsTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _otherUserId = Guid.NewGuid();
    private readonly Guid _mediaId = Guid.NewGuid();

    public InteractionControllerSessionsTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"sessions-{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(options);
        _context.MediaItems.Add(new MediaItem
        {
            Id = _mediaId,
            Title = "Test Book",
            Type = MediaType.Book,
            Path = "/lib/test.epub",
            LibraryId = Guid.NewGuid(),
        });
        _context.SaveChanges();
    }

    public void Dispose() => _context.Dispose();

    private InteractionController NewController(Guid? asUser = null)
    {
        var controller = new InteractionController(
            _context,
            NullLogger<InteractionController>.Instance,
            Mock.Of<IRecommendationService>(),
            Mock.Of<IUserMediaInteractionService>());
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, (asUser ?? _userId).ToString()),
        });
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    [Fact]
    public async Task StartSession_InsertsRowAndReturnsId()
    {
        var controller = NewController();
        var result = await controller.StartSession(_mediaId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<StartSessionResponse>(ok.Value);
        Assert.NotEqual(Guid.Empty, dto.SessionId);
        Assert.Single(_context.ReadingSessions);
    }

    [Fact]
    public async Task StartSession_NotFoundForUnknownMedia()
    {
        var controller = NewController();
        var result = await controller.StartSession(Guid.NewGuid());
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task EndSession_PersistsWhenPagesReadPositive()
    {
        var session = new ReadingSession
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            MediaItemId = _mediaId,
            StartedAt = DateTime.UtcNow.AddMinutes(-10),
        };
        _context.ReadingSessions.Add(session);
        _context.SaveChanges();

        var controller = NewController();
        var result = await controller.EndSession(_mediaId, session.Id, new EndSessionRequest
        {
            PagesRead = 12,
        });

        Assert.IsType<NoContentResult>(result);
        var refreshed = _context.ReadingSessions.Single();
        Assert.Equal(12, refreshed.PagesRead);
        Assert.NotNull(refreshed.EndedAt);
    }

    [Fact]
    public async Task EndSession_DeletesRowWhenPagesReadZero()
    {
        var session = new ReadingSession
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            MediaItemId = _mediaId,
            StartedAt = DateTime.UtcNow.AddMinutes(-30),
        };
        _context.ReadingSessions.Add(session);
        _context.SaveChanges();

        var controller = NewController();
        var result = await controller.EndSession(_mediaId, session.Id, new EndSessionRequest
        {
            PagesRead = 0,
        });

        Assert.IsType<NoContentResult>(result);
        Assert.False(_context.ReadingSessions.Any());
    }

    [Fact]
    public async Task EndSession_RefusesOtherUsersSession()
    {
        var session = new ReadingSession
        {
            Id = Guid.NewGuid(),
            UserId = _otherUserId,
            MediaItemId = _mediaId,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        _context.ReadingSessions.Add(session);
        _context.SaveChanges();

        var controller = NewController();
        var result = await controller.EndSession(_mediaId, session.Id, new EndSessionRequest
        {
            PagesRead = 5,
        });
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetSessionSummary_SumsOnlyCompletedSessionsForCaller()
    {
        var now = DateTime.UtcNow;
        _context.ReadingSessions.AddRange(
            new ReadingSession
            {
                Id = Guid.NewGuid(),
                UserId = _userId,
                MediaItemId = _mediaId,
                StartedAt = now.AddMinutes(-20),
                EndedAt = now.AddMinutes(-10),
                PagesRead = 10,
            },
            new ReadingSession
            {
                Id = Guid.NewGuid(),
                UserId = _userId,
                MediaItemId = _mediaId,
                StartedAt = now.AddMinutes(-60),
                EndedAt = now.AddMinutes(-30),
                PagesRead = 20,
            },
            // Unfinished — should not count.
            new ReadingSession
            {
                Id = Guid.NewGuid(),
                UserId = _userId,
                MediaItemId = _mediaId,
                StartedAt = now.AddMinutes(-2),
                EndedAt = null,
                PagesRead = 0,
            },
            // Other user — should not count.
            new ReadingSession
            {
                Id = Guid.NewGuid(),
                UserId = _otherUserId,
                MediaItemId = _mediaId,
                StartedAt = now.AddMinutes(-60),
                EndedAt = now.AddMinutes(-50),
                PagesRead = 99,
            }
        );
        _context.SaveChanges();

        var controller = NewController();
        var result = await controller.GetSessionSummary(_mediaId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ReadingSessionSummary>(ok.Value);
        Assert.Equal(2, dto.SessionCount);
        Assert.Equal(30, dto.TotalPages);
        Assert.True(dto.TotalMinutes >= 39 && dto.TotalMinutes <= 41); // ~40min
        Assert.True(dto.PagesPerMinute > 0);
    }

    [Fact]
    public async Task GetSessionSummary_ReturnsZeroesWhenNothingCompleted()
    {
        var controller = NewController();
        var result = await controller.GetSessionSummary(_mediaId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ReadingSessionSummary>(ok.Value);
        Assert.Equal(0, dto.SessionCount);
        Assert.Equal(0, dto.TotalPages);
        Assert.Equal(0, dto.TotalMinutes);
        Assert.Equal(0, dto.PagesPerMinute);
    }
}
