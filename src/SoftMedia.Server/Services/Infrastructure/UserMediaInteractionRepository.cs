using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Infrastructure;

public class UserMediaInteractionRepository : IUserMediaInteractionRepository
{
    private readonly AppDbContext _context;

    public UserMediaInteractionRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<UserMediaInteraction?> GetAsync(Guid userId, Guid mediaItemId)
    {
        return await _context.UserMediaInteractions.AsNoTracking()
            .FirstOrDefaultAsync(i => i.UserId == userId && i.MediaItemId == mediaItemId);
    }

    public async Task<IEnumerable<UserMediaInteraction>> GetManyAsync(Guid userId, IEnumerable<Guid> mediaItemIds)
    {
        return await _context.UserMediaInteractions.AsNoTracking()
            .Where(i => i.UserId == userId && mediaItemIds.Contains(i.MediaItemId))
            .ToListAsync();
    }

    public async Task AddOrUpdateAsync(UserMediaInteraction interaction)
    {
        var existing = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(i => i.UserId == interaction.UserId && i.MediaItemId == interaction.MediaItemId);

        if (existing == null)
        {
            _context.UserMediaInteractions.Add(interaction);
        }
        else
        {
            _context.Entry(existing).CurrentValues.SetValues(interaction);
        }

        await _context.SaveChangesAsync();
    }
}
