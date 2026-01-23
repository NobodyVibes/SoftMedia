using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services;
using Xunit;

namespace SoftMedia.Server.Tests.Services;

public class RecommendationServiceTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly Mock<ILogger<RecommendationService>> _loggerMock;
    private readonly RecommendationService _service;

    public RecommendationServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new AppDbContext(options);
        _loggerMock = new Mock<ILogger<RecommendationService>>();
        _service = new RecommendationService(_context, _loggerMock.Object);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Fact]
    public async Task GetNextEpisodeAsync_NoHistory_ReturnsFirstEpisode()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var episode1 = new MediaItem { Id = Guid.NewGuid(), SeriesId = seriesId, Type = MediaType.Episode, SeasonNumber = 1, EpisodeNumber = 1, Title = "Ep 1" };
        var episode2 = new MediaItem { Id = Guid.NewGuid(), SeriesId = seriesId, Type = MediaType.Episode, SeasonNumber = 1, EpisodeNumber = 2, Title = "Ep 2" };

        _context.MediaItems.AddRange(episode1, episode2);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetNextEpisodeAsync(userId, seriesId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(episode1.Id, result.EpisodeId);
    }

    [Fact]
    public async Task GetNextEpisodeAsync_WatchedFirst_ReturnsSecond()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var episode1 = new MediaItem { Id = Guid.NewGuid(), SeriesId = seriesId, Type = MediaType.Episode, SeasonNumber = 1, EpisodeNumber = 1, Title = "Ep 1", Duration = 1000 };
        var episode2 = new MediaItem { Id = Guid.NewGuid(), SeriesId = seriesId, Type = MediaType.Episode, SeasonNumber = 1, EpisodeNumber = 2, Title = "Ep 2", Duration = 1000 };

        _context.MediaItems.AddRange(episode1, episode2);
        
        var interaction = new UserMediaInteraction 
        { 
            UserId = userId, 
            MediaItemId = episode1.Id, 
            LastPlayed = DateTime.UtcNow, 
            PlaybackPosition = 990, // Watched > 95%
            IsWatched = false
        };
        _context.UserMediaInteractions.Add(interaction);
        
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetNextEpisodeAsync(userId, seriesId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(episode2.Id, result.EpisodeId);
    }

    [Fact]
    public async Task GetNextEpisodeAsync_Incomplete_ReturnsResume()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var episode1 = new MediaItem { Id = Guid.NewGuid(), SeriesId = seriesId, Type = MediaType.Episode, SeasonNumber = 1, EpisodeNumber = 1, Title = "Ep 1", Duration = 1000 };
        
        _context.MediaItems.Add(episode1);
        
        var interaction = new UserMediaInteraction 
        { 
            UserId = userId, 
            MediaItemId = episode1.Id, 
            LastPlayed = DateTime.UtcNow, 
            PlaybackPosition = 500, // Watched 50%
            IsWatched = false
        };
        _context.UserMediaInteractions.Add(interaction);
        
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetNextEpisodeAsync(userId, seriesId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(episode1.Id, result.EpisodeId);
        Assert.Equal(500, result.ResumePosition);
    }
}
