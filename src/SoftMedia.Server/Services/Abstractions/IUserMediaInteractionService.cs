using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;

namespace SoftMedia.Server.Services.Abstractions;

public interface IUserMediaInteractionService
{
    Task RateMediaAsync(Guid userId, Guid mediaId, int? rating);
    Task ToggleFavoriteAsync(Guid userId, Guid mediaId, bool isFavorite);
    Task MarkWatchedAsync(Guid userId, Guid mediaId, bool watched);
    Task UpdateProgressAsync(Guid userId, Guid mediaId, double position, string? bookLocation = null);
    Task<UserMediaInteraction?> GetInteractionAsync(Guid userId, Guid mediaId);
    Task<IEnumerable<UserMediaInteraction>> GetInteractionsAsync(Guid userId, IEnumerable<Guid> mediaIds);

    // Wave E3 — watchlist toggle. Stamps WatchlistedAt on add, clears on remove.
    Task ToggleWatchlistAsync(Guid userId, Guid mediaId, bool isWatchlisted);

    // R-WI-013 — self-scoped per-play history, newest first. Gated by the caller's CURRENT
    // library access + rating ceiling so revoked/now-hidden titles don't leak into history.
    Task<IReadOnlyList<PlaybackHistory>> GetHistoryAsync(
        Guid userId, int page, int pageSize, LibraryAccess access, UserRatingCeilings ceilings);

    // R-WI-013 privacy follow-up — user-owned recording toggle + clear-my-history.
    Task<bool> GetRecordHistoryAsync(Guid userId);
    Task SetRecordHistoryAsync(Guid userId, bool record);
    Task<int> ClearHistoryAsync(Guid userId);
}
