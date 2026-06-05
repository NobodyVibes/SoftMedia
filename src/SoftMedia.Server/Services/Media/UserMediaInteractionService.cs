using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Media;

public class UserMediaInteractionService : IUserMediaInteractionService
{
    private readonly AppDbContext _context;
    private readonly ILogger<UserMediaInteractionService> _logger;

    public UserMediaInteractionService(AppDbContext context, ILogger<UserMediaInteractionService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task RateMediaAsync(Guid userId, Guid mediaId, int? rating)
    {
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(i => i.UserId == userId && i.MediaItemId == mediaId);

        if (interaction == null)
        {
            if (rating == null) return;
            interaction = new UserMediaInteraction
            {
                UserId = userId,
                MediaItemId = mediaId
            };
            _context.UserMediaInteractions.Add(interaction);
        }

        interaction.Rating = rating;
        
        if (interaction.Rating == null && interaction.IsFavorite == false && interaction.IsWatched == false && (interaction.PlaybackPosition ?? 0) <= 0)
        {
            _context.UserMediaInteractions.Remove(interaction);
        }

        await _context.SaveChangesAsync();

        // Recalculate average rating for the media item
        await UpdateMediaInternalRatingAsync(mediaId);
    }

    private async Task UpdateMediaInternalRatingAsync(Guid mediaId)
    {
        var ratings = await _context.UserMediaInteractions
            .Where(i => i.MediaItemId == mediaId && i.Rating != null)
            .Select(i => i.Rating!.Value)
            .ToListAsync();

        var mediaItem = await _context.MediaItems.FindAsync(mediaId);
        if (mediaItem != null)
        {
            if (ratings.Any())
            {
                mediaItem.InternalRating = ratings.Average();
                mediaItem.InternalRatingCount = ratings.Count;
            }
            else
            {
                mediaItem.InternalRating = null;
                mediaItem.InternalRatingCount = 0;
            }

            await _context.SaveChangesAsync();
            _logger.LogInformation("Updated internal rating for media {MediaId}: {Rating} ({Count} votes)", 
                mediaId, mediaItem.InternalRating, mediaItem.InternalRatingCount);
        }
    }

    public async Task ToggleFavoriteAsync(Guid userId, Guid mediaId, bool isFavorite)
    {
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(i => i.UserId == userId && i.MediaItemId == mediaId);

        if (interaction == null)
        {
            if (!isFavorite) return;
            interaction = new UserMediaInteraction
            {
                UserId = userId,
                MediaItemId = mediaId
            };
            _context.UserMediaInteractions.Add(interaction);
        }

        interaction.IsFavorite = isFavorite;
        await _context.SaveChangesAsync();
    }

    public async Task MarkWatchedAsync(Guid userId, Guid mediaId, bool watched)
    {
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(i => i.UserId == userId && i.MediaItemId == mediaId);

        if (interaction == null)
        {
            if (!watched) return;
            interaction = new UserMediaInteraction
            {
                UserId = userId,
                MediaItemId = mediaId
            };
            _context.UserMediaInteractions.Add(interaction);
        }

        interaction.IsWatched = watched;
        if (watched)
        {
            interaction.LastPlayed = DateTime.UtcNow;
            interaction.PlaybackPosition = 0;
        }

        await _context.SaveChangesAsync();
    }

    public async Task UpdateProgressAsync(Guid userId, Guid mediaId, double position, string? bookLocation = null)
    {
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(i => i.UserId == userId && i.MediaItemId == mediaId);

        if (interaction == null)
        {
            interaction = new UserMediaInteraction
            {
                UserId = userId,
                MediaItemId = mediaId
            };
            _context.UserMediaInteractions.Add(interaction);
        }

        interaction.PlaybackPosition = position;
        if (bookLocation != null)
        {
            interaction.BookLocation = bookLocation;
        }
        interaction.LastPlayed = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }

    public async Task<UserMediaInteraction?> GetInteractionAsync(Guid userId, Guid mediaId)
    {
        return await _context.UserMediaInteractions
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.UserId == userId && i.MediaItemId == mediaId);
    }

    public async Task<IEnumerable<UserMediaInteraction>> GetInteractionsAsync(Guid userId, IEnumerable<Guid> mediaIds)
    {
        return await _context.UserMediaInteractions
            .AsNoTracking()
            .Where(i => i.UserId == userId && mediaIds.Contains(i.MediaItemId))
            .ToListAsync();
    }

    public async Task ToggleWatchlistAsync(Guid userId, Guid mediaId, bool isWatchlisted)
    {
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(i => i.UserId == userId && i.MediaItemId == mediaId);

        if (interaction == null)
        {
            // No-op when removing from watchlist for an item the user never watchlisted.
            if (!isWatchlisted) return;
            interaction = new UserMediaInteraction
            {
                UserId = userId,
                MediaItemId = mediaId,
            };
            _context.UserMediaInteractions.Add(interaction);
        }

        interaction.IsWatchlisted = isWatchlisted;
        // Stamp on add (used for sort); clear on remove so re-adds get a fresh timestamp.
        interaction.WatchlistedAt = isWatchlisted ? DateTime.UtcNow : null;

        // GC empty interaction rows so the table doesn't accumulate dead state.
        if (!interaction.IsFavorite
            && !interaction.IsWatched
            && !interaction.IsWatchlisted
            && interaction.Rating == null
            && (interaction.PlaybackPosition ?? 0) <= 0
            && interaction.BookLocation == null)
        {
            _context.UserMediaInteractions.Remove(interaction);
        }

        await _context.SaveChangesAsync();
    }
}
