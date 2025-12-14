using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using System.Security.Claims;

namespace SoftMedia.Server.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/interaction")]
public class InteractionController : ControllerBase
{
    private readonly AppDbContext _context;

    public InteractionController(AppDbContext context)
    {
        _context = context;
    }

    private Guid GetUserId()
    {
        var idClaim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (idClaim == null || !Guid.TryParse(idClaim.Value, out var userId))
        {
            throw new UnauthorizedAccessException("Invalid user ID");
        }
        return userId;
    }

    [HttpPost("{mediaId}/rate")]
    public async Task<IActionResult> RateMedia(Guid mediaId, [FromBody] RateRequest request)
    {
        var userId = GetUserId();
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(x => x.UserId == userId && x.MediaItemId == mediaId);

        if (interaction == null)
        {
            interaction = new UserMediaInteraction
            {
                UserId = userId,
                MediaItemId = mediaId
            };
            _context.UserMediaInteractions.Add(interaction);
        }

        interaction.Rating = request.Rating;
        await _context.SaveChangesAsync();

        // Recalculate average rating
        var ratings = await _context.UserMediaInteractions
            .Where(x => x.MediaItemId == mediaId && x.Rating != null)
            .Select(x => x.Rating)
            .ToListAsync();

        double? communityRating = null;
        if (ratings.Any())
        {
            communityRating = ratings.Average(r => r.Value);
        }

        var mediaItem = await _context.MediaItems.FindAsync(mediaId);
        if (mediaItem != null)
        {
            mediaItem.CommunityRating = communityRating;
            await _context.SaveChangesAsync();
        }

        return Ok();
    }

    [HttpPost("{mediaId}/favorite")]
    public async Task<IActionResult> ToggleFavorite(Guid mediaId, [FromBody] FavoriteRequest request)
    {
        var userId = GetUserId();
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(x => x.UserId == userId && x.MediaItemId == mediaId);

        if (interaction == null)
        {
            interaction = new UserMediaInteraction
            {
                UserId = userId,
                MediaItemId = mediaId
            };
            _context.UserMediaInteractions.Add(interaction);
        }

        interaction.IsFavorite = request.IsFavorite;
        await _context.SaveChangesAsync();

        return Ok();
    }

    [HttpPost("{mediaId}/watched")]
    public async Task<IActionResult> MarkWatched(Guid mediaId, [FromBody] WatchedRequest request)
    {
        var userId = GetUserId();
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(x => x.UserId == userId && x.MediaItemId == mediaId);

        if (interaction == null)
        {
            interaction = new UserMediaInteraction
            {
                UserId = userId,
                MediaItemId = mediaId
            };
            _context.UserMediaInteractions.Add(interaction);
        }

        interaction.IsWatched = request.Watched;
        if (request.Watched)
        {
            interaction.LastPlayed = DateTime.UtcNow;
        }
        
        await _context.SaveChangesAsync();

        return Ok();
    }

    // Maintenance endpoint removed after execution
}

public class RateRequest
{
    public int? Rating { get; set; }
}

public class FavoriteRequest
{
    public bool IsFavorite { get; set; }
}

public class WatchedRequest
{
    public bool Watched { get; set; }
}
