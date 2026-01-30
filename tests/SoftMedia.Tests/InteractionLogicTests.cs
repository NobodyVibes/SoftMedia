using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http; // For DefaultHttpContext
using Moq;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using Microsoft.Data.Sqlite;

namespace SoftMedia.Tests;

public class InteractionLogicTests : IDisposable
{
    private SqliteConnection _connection;

    public InteractionLogicTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    private AppDbContext GetContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    private InteractionController GetController(AppDbContext context, Guid userId)
    {
        var mockRecommendationService = new Mock<IRecommendationService>();
        var controller = new InteractionController(context, new Microsoft.Extensions.Logging.Abstractions.NullLogger<InteractionController>(), mockRecommendationService.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString())
                }, "TestAuth"))
            }
        };
        return controller;
    }

    [Fact]
    public async Task RateMedia_UpdatesCommunityRating()
    {
        using var context = GetContext();
        
        // Setup dependencies for FK constraints
        var libraryId = Guid.NewGuid();
        context.Libraries.Add(new Library { Id = libraryId, Name = "Test Lib", Type = LibraryType.Movie });
        
        var mediaId = Guid.NewGuid();
        context.MediaItems.Add(new MediaItem { Id = mediaId, LibraryId = libraryId, Title = "Test Movie" });
        
        var user1Id = Guid.NewGuid();
        var user2Id = Guid.NewGuid();
        // Assuming Users table exists and FK is enforced. If no Users table, this might be fine, but if UserMediaInteraction has FK to Users, we need users.
        // Checking if AppDbContext has Users. Likely yes.
        context.Users.Add(new User { Id = user1Id, Username = "u1", PasswordHash = "hash" });
        context.Users.Add(new User { Id = user2Id, Username = "u2", PasswordHash = "hash" });
        
        await context.SaveChangesAsync();

        var controller1 = GetController(context, user1Id);
        var controller2 = GetController(context, user2Id);

        // User 1 rates 5
        await controller1.RateMedia(mediaId, new RateRequest { Rating = 5 });
        
        var item = await context.MediaItems.FindAsync(mediaId);
        Assert.Equal(5.0, item!.CommunityRating);

        // User 2 rates 1
        await controller2.RateMedia(mediaId, new RateRequest { Rating = 1 });

        item = await context.MediaItems.FindAsync(mediaId); // Reload
        context.Entry(item).Reload(); 
        Assert.Equal(3.0, item!.CommunityRating); // (5+1)/2 = 3
    }

    [Fact]
    public async Task MediaItemDto_IncludesUserInteraction()
    {
        using var context = GetContext();
        var mediaId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        context.Libraries.Add(new Library { Id = libraryId, Name = "Movies", Type = LibraryType.Movie });
        context.MediaItems.Add(new MediaItem { Id = mediaId, LibraryId = libraryId, Title = "My Movie" });
        
        var userId = Guid.NewGuid();
        context.Users.Add(new User { Id = userId, Username = "testuser", PasswordHash = "hash" });
        
        await context.SaveChangesAsync();

        context.UserMediaInteractions.Add(new UserMediaInteraction 
        { 
            UserId = userId, 
            MediaItemId = mediaId, 
            Rating = 4, 
            IsFavorite = true 
        });
        await context.SaveChangesAsync();

        // Simulate Controller Logic
        var mockMediaRetrieval = new Mock<IMediaRetrievalService>();
        var mediaController = new MediaController(context, mockMediaRetrieval.Object);
        mediaController.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString())
                }, "TestAuth"))
            }
        };

        var result = await mediaController.GetMediaItem(mediaId);
        var dto = result.Value;

        Assert.NotNull(dto);
        Assert.Equal(4, dto.UserRating);
        Assert.True(dto.IsFavorite);
    }

    [Fact]
    public void MediaItemDto_SeparatesInternalAndExternalRatings()
    {
        // Assemble
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Title = "Rated Movie",
            CommunityRating = 4.2,
            MetadataJson = "{\"rating\": 8.7}"
        };

        // Act
        var dto = MediaItemDto.FromMediaItem(item);

        // Assert
        Assert.Equal(4.2, dto.CommunityRating); // Internal
        Assert.Equal("8.7", dto.Rating);       // External
    }
}
