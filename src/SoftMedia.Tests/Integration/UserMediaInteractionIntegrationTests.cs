using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Media;
using Xunit;

namespace SoftMedia.Tests.Integration;

public class UserMediaInteractionIntegrationTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly UserMediaInteractionService _service;
    private readonly User _testUser;
    private readonly MediaItem _testMedia;

    public UserMediaInteractionIntegrationTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new AppDbContext(options);

        // Seed basic data
        _testUser = new User { Username = "TestUser", Role = UserRole.User };
        _testMedia = new MediaItem 
        { 
            Title = "Test Movie", 
            Type = MediaType.Movie, 
            Path = "/path/to/movie.mkv",
            LibraryId = Guid.NewGuid()
        };
        
        _context.Users.Add(_testUser);
        _context.MediaItems.Add(_testMedia);
        _context.SaveChanges();

        _service = new UserMediaInteractionService(
            _context,
            new Mock<ILogger<UserMediaInteractionService>>().Object
        );
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Fact]
    public async Task RateMediaAsync_CreatesInteractionAndUpdatesAverage()
    {
        // Act
        await _service.RateMediaAsync(_testUser.Id, _testMedia.Id, 5);

        // Assert
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(i => i.UserId == _testUser.Id && i.MediaItemId == _testMedia.Id);
        
        Assert.NotNull(interaction);
        Assert.Equal(5, interaction.Rating);

        var updatedMedia = await _context.MediaItems.FindAsync(_testMedia.Id);
        Assert.Equal(5, updatedMedia.InternalRating);
        Assert.Equal(1, updatedMedia.InternalRatingCount);
    }

    [Fact]
    public async Task RateMediaAsync_UpdatesExistingRating()
    {
        // Arrange
        var interaction = new UserMediaInteraction 
        { 
            UserId = _testUser.Id, 
            MediaItemId = _testMedia.Id, 
            Rating = 3 
        };
        _context.UserMediaInteractions.Add(interaction);
        _context.SaveChanges();

        // Act
        await _service.RateMediaAsync(_testUser.Id, _testMedia.Id, 5);

        // Assert
        var updated = await _context.UserMediaInteractions.FindAsync(interaction.UserId, interaction.MediaItemId);
        Assert.Equal(5, updated.Rating);

        var updatedMedia = await _context.MediaItems.FindAsync(_testMedia.Id);
        Assert.Equal(5, updatedMedia.InternalRating);
    }

    [Fact]
    public async Task RateMediaAsync_RemovesInteraction_IfNullRatingAndNoOtherState()
    {
        // Arrange
        await _service.RateMediaAsync(_testUser.Id, _testMedia.Id, 4);

        // Act
        await _service.RateMediaAsync(_testUser.Id, _testMedia.Id, null);

        // Assert
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(i => i.UserId == _testUser.Id && i.MediaItemId == _testMedia.Id);
        Assert.Null(interaction);
        
        var updatedMedia = await _context.MediaItems.FindAsync(_testMedia.Id);
        Assert.Null(updatedMedia.InternalRating);
    }

    [Fact]
    public async Task ToggleFavoriteAsync_CreatesInteraction()
    {
        // Act
        await _service.ToggleFavoriteAsync(_testUser.Id, _testMedia.Id, true);

        // Assert
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(i => i.UserId == _testUser.Id && i.MediaItemId == _testMedia.Id);
        Assert.NotNull(interaction);
        Assert.True(interaction.IsFavorite);
    }

    [Fact]
    public async Task ToggleFavoriteAsync_UpdatesExisting()
    {
        // Arrange
        await _service.ToggleFavoriteAsync(_testUser.Id, _testMedia.Id, true);

        // Act
        await _service.ToggleFavoriteAsync(_testUser.Id, _testMedia.Id, false);

        // Assert
        var interaction = await _context.UserMediaInteractions
             .FirstOrDefaultAsync(i => i.UserId == _testUser.Id && i.MediaItemId == _testMedia.Id);
        Assert.NotNull(interaction); // Current logic implies it keeps record unless Rating logic removes it? 
        // Logic check: ToggleFavorite only sets property. It does not check like RateMediaAsync to remove if empty.
        Assert.False(interaction.IsFavorite);
    }
    
    [Fact]
    public async Task MarkWatchedAsync_UpdatesStatusAndResetProgress()
    {
        // Arrange
        var interaction = new UserMediaInteraction
        {
            UserId = _testUser.Id,
            MediaItemId = _testMedia.Id,
            PlaybackPosition = 500,
            IsWatched = false
        };
        _context.UserMediaInteractions.Add(interaction);
        _context.SaveChanges();

        // Act
        await _service.MarkWatchedAsync(_testUser.Id, _testMedia.Id, true);

        // Assert
        var updated = await _context.UserMediaInteractions.FindAsync(interaction.UserId, interaction.MediaItemId);
        Assert.True(updated.IsWatched);
        Assert.Equal(0, updated.PlaybackPosition);
        Assert.NotNull(updated.LastPlayed);
    }

    [Fact]
    public async Task UpdateProgressAsync_UpdatesPosition()
    {
        // Act
        await _service.UpdateProgressAsync(_testUser.Id, _testMedia.Id, 123.45);

        // Assert
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(i => i.UserId == _testUser.Id && i.MediaItemId == _testMedia.Id);
        Assert.NotNull(interaction);
        Assert.Equal(123.45, interaction.PlaybackPosition);
        Assert.NotNull(interaction.LastPlayed);
    }
}
