using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http; // For DefaultHttpContext

namespace SoftMedia.Tests;

public class InteractionLogicTests
{
    private AppDbContext GetContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()) // Unique DB per test
            .Options;
        return new AppDbContext(options);
    }

    private InteractionController GetController(AppDbContext context, Guid userId)
    {
        var controller = new InteractionController(context);
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
        var mediaId = Guid.NewGuid();
        context.MediaItems.Add(new MediaItem { Id = mediaId, Title = "Test Movie" });
        await context.SaveChangesAsync();

        var user1Id = Guid.NewGuid();
        var user2Id = Guid.NewGuid();

        var controller1 = GetController(context, user1Id);
        var controller2 = GetController(context, user2Id);

        // User 1 rates 5
        await controller1.RateMedia(mediaId, new RateRequest { Rating = 5 });
        
        var item = await context.MediaItems.FindAsync(mediaId);
        Assert.Equal(5.0, item!.CommunityRating);

        // User 2 rates 1
        await controller2.RateMedia(mediaId, new RateRequest { Rating = 1 });

        item = await context.MediaItems.FindAsync(mediaId); // Reload
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
        await context.SaveChangesAsync();

        var userId = Guid.NewGuid();
        context.UserMediaInteractions.Add(new UserMediaInteraction 
        { 
            UserId = userId, 
            MediaItemId = mediaId, 
            Rating = 4, 
            IsFavorite = true 
        });
        await context.SaveChangesAsync();

        // Simulate Controller Logic
        var mediaController = new MediaController(context);
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
