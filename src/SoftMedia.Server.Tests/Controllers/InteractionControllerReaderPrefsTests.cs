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
/// Coverage for the ER-012 reader-preferences endpoints on <see cref="InteractionController"/>.
/// Uses EF Core InMemory so the controller can hit a real DbContext without a SQLite fixture —
/// the code under test reads/writes via EF Core primitives and doesn't depend on SQLite-specific
/// behaviour.
/// </summary>
public class InteractionControllerReaderPrefsTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _mediaId = Guid.NewGuid();

    public InteractionControllerReaderPrefsTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"readerprefs-{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(options);

        // Seed a media item so PUT validation succeeds.
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

    private InteractionController NewController()
    {
        var controller = new InteractionController(
            _context,
            NullLogger<InteractionController>.Instance,
            Mock.Of<IRecommendationService>(),
            Mock.Of<IUserMediaInteractionService>(),
            Mock.Of<SoftMedia.Server.Services.Security.LibraryAccess.IUserLibraryAccessProvider>(),
            Mock.Of<SoftMedia.Server.Services.Security.ContentRating.IUserContentRatingProvider>(),
            new SoftMedia.Server.Services.Sessions.ActiveStreamRegistry(),
            new SoftMedia.Server.Services.Sessions.TerminatedSessionRegistry(),
            Mock.Of<SoftMedia.Server.Services.Transcoding.ITranscodeService>(s =>
                s.GetAllSessions() == Enumerable.Empty<SoftMedia.Server.Services.Transcoding.Models.TranscodeSession>()));

        // Populate ClaimsPrincipal so GetUserId() returns our test id.
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, _userId.ToString()) });
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    [Fact]
    public async Task GetReaderPreferences_ReturnsSchemaVersionZeroWhenNoRowExists()
    {
        var controller = NewController();

        var result = await controller.GetReaderPreferences(_mediaId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ReaderPreferencesResponse>(ok.Value);
        Assert.Equal(0, dto.SchemaVersion);
        Assert.Null(dto.PreferencesJson);
    }

    [Fact]
    public async Task PutReaderPreferences_InsertsRowWhenMissing()
    {
        var controller = NewController();
        var result = await controller.PutReaderPreferences(_mediaId, new ReaderPreferencesRequest
        {
            SchemaVersion = 1,
            PreferencesJson = "{\"fontSize\":140}",
        });

        Assert.IsType<NoContentResult>(result);
        var row = _context.UserReaderPreferences.Single(p => p.UserId == _userId && p.MediaItemId == _mediaId);
        Assert.Contains("fontSize", row.PreferencesJson);
        Assert.Equal(1, row.SchemaVersion);
    }

    [Fact]
    public async Task PutReaderPreferences_UpdatesExistingRowAndBumpsUpdatedAt()
    {
        _context.UserReaderPreferences.Add(new UserReaderPreferences
        {
            UserId = _userId,
            MediaItemId = _mediaId,
            PreferencesJson = "{\"fontSize\":100}",
            SchemaVersion = 1,
            UpdatedAt = DateTime.UtcNow.AddDays(-1),
        });
        _context.SaveChanges();

        var controller = NewController();
        var result = await controller.PutReaderPreferences(_mediaId, new ReaderPreferencesRequest
        {
            SchemaVersion = 1,
            PreferencesJson = "{\"fontSize\":140}",
        });

        Assert.IsType<NoContentResult>(result);
        var row = _context.UserReaderPreferences.Single(p => p.UserId == _userId && p.MediaItemId == _mediaId);
        Assert.Contains("140", row.PreferencesJson);
        Assert.True(row.UpdatedAt > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task PutReaderPreferences_EmptyPayloadDeletesExistingRow()
    {
        _context.UserReaderPreferences.Add(new UserReaderPreferences
        {
            UserId = _userId,
            MediaItemId = _mediaId,
            PreferencesJson = "{\"fontSize\":140}",
        });
        _context.SaveChanges();

        var controller = NewController();
        var result = await controller.PutReaderPreferences(_mediaId, new ReaderPreferencesRequest
        {
            PreferencesJson = null,
        });

        Assert.IsType<NoContentResult>(result);
        Assert.False(_context.UserReaderPreferences.Any(p => p.UserId == _userId && p.MediaItemId == _mediaId));
    }

    [Fact]
    public async Task PutReaderPreferences_EmptyObjectPayloadAlsoDeletes()
    {
        _context.UserReaderPreferences.Add(new UserReaderPreferences
        {
            UserId = _userId,
            MediaItemId = _mediaId,
            PreferencesJson = "{\"fontSize\":140}",
        });
        _context.SaveChanges();

        var controller = NewController();
        var result = await controller.PutReaderPreferences(_mediaId, new ReaderPreferencesRequest
        {
            PreferencesJson = "{}",
        });

        Assert.IsType<NoContentResult>(result);
        Assert.False(_context.UserReaderPreferences.Any());
    }

    [Fact]
    public async Task PutReaderPreferences_RejectsOversizedPayload()
    {
        var controller = NewController();
        var big = "{\"k\":\"" + new string('x', 9000) + "\"}";

        var result = await controller.PutReaderPreferences(_mediaId, new ReaderPreferencesRequest
        {
            PreferencesJson = big,
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PutReaderPreferences_ReturnsNotFoundForUnknownMediaItem()
    {
        var controller = NewController();
        var result = await controller.PutReaderPreferences(Guid.NewGuid(), new ReaderPreferencesRequest
        {
            SchemaVersion = 1,
            PreferencesJson = "{\"a\":1}",
        });

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetReaderPreferences_ReturnsStoredRowAfterPut()
    {
        var controller = NewController();
        await controller.PutReaderPreferences(_mediaId, new ReaderPreferencesRequest
        {
            SchemaVersion = 2,
            PreferencesJson = "{\"theme\":\"sepia\"}",
        });

        var result = await controller.GetReaderPreferences(_mediaId);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ReaderPreferencesResponse>(ok.Value);

        Assert.Equal(2, dto.SchemaVersion);
        Assert.Contains("sepia", dto.PreferencesJson);
        Assert.NotNull(dto.UpdatedAt);
    }
}
