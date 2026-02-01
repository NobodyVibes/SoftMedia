using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Abstractions;

public interface IUserMediaInteractionRepository
{
    Task<UserMediaInteraction?> GetAsync(Guid userId, Guid mediaItemId);
    Task<IEnumerable<UserMediaInteraction>> GetManyAsync(Guid userId, IEnumerable<Guid> mediaItemIds);
    Task AddOrUpdateAsync(UserMediaInteraction interaction);
}
