using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Abstractions;

public interface IUserMediaInteractionService
{
    Task RateMediaAsync(Guid userId, Guid mediaId, int? rating);
    Task ToggleFavoriteAsync(Guid userId, Guid mediaId, bool isFavorite);
    Task MarkWatchedAsync(Guid userId, Guid mediaId, bool watched);
    Task UpdateProgressAsync(Guid userId, Guid mediaId, double position);
    Task<UserMediaInteraction?> GetInteractionAsync(Guid userId, Guid mediaId);
    Task<IEnumerable<UserMediaInteraction>> GetInteractionsAsync(Guid userId, IEnumerable<Guid> mediaIds);
}
