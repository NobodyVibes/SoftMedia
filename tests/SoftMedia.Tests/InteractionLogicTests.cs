using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Moq;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

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

    [Fact]
    public async Task RateMedia_UpdatesInternalRating_ViaService()
    {
        using var context = GetContext();
        
        var libraryId = Guid.NewGuid();
        context.Libraries.Add(new Library { Id = libraryId, Name = "Test Lib", Type = LibraryType.Movie });
        
        var mediaId = Guid.NewGuid();
        context.MediaItems.Add(new MediaItem { Id = mediaId, LibraryId = libraryId, Title = "Test Movie" });
        
        var user1Id = Guid.NewGuid();
        var user2Id = Guid.NewGuid();
        context.Users.Add(new User { Id = user1Id, Username = "u1", PasswordHash = "hash" });
        context.Users.Add(new User { Id = user2Id, Username = "u2", PasswordHash = "hash" });
        
        await context.SaveChangesAsync();

        var service = new UserMediaInteractionService(context, NullLogger<UserMediaInteractionService>.Instance);

        // User 1 rates 5
        await service.RateMediaAsync(user1Id, mediaId, 5);
        
        var item = await context.MediaItems.FindAsync(mediaId);
        // Note: Field is 'InternalRating' (CommunityRating might be a computed property or DTO field, checking impl it maps to InternalRating logic)
        // Previous test asserted 5.0 on CommunityRating. Looking at Controller/DTO logic:
        // MediaItem has InternalRating. DTO likely maps CommunityRating to InternalRating (or Average).
        // Service updates InternalRating.
        Assert.Equal(5.0, item!.InternalRating);

        // User 2 rates 1
        await service.RateMediaAsync(user2Id, mediaId, 1);

        item = await context.MediaItems.FindAsync(mediaId); 
        context.Entry(item!).Reload(); 
        Assert.Equal(3.0, item!.InternalRating); // (5+1)/2 = 3
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
        var mockRecommendation = new Mock<IRecommendationService>();
        
        var mediaController = new MediaController(context, mockMediaRetrieval.Object, mockRecommendation.Object);
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
        Assert.Equal(4, dto.PersonalRating);
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
            InternalRating = 4.2,
            ContentRating = "PG-13"
        };

        // Act
        // Note: FromMediaItem takes 3 args: item, imageProxyUrlBase, interaction
        // Tests usually use the static method. Checking MediaController usage: MediaItemDto.FromMediaItem(item, "/api/v1/image/proxy", interaction)
        // I need to match the signature.
        var dto = MediaItemDto.FromMediaItem(item, "/proxy", null);

        // Assert
        Assert.Equal(4.2, dto.UserRating); // Internal Average
        Assert.Equal("PG-13", dto.Rating);       // External Content Rating
    }
}
