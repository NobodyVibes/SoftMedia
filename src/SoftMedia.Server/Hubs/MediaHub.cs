using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;

namespace SoftMedia.Server.Hubs;

/// <summary>
/// SignalR hub for real-time media library updates.
/// Clients join groups based on library or media IDs to receive targeted notifications.
/// Validates that libraries/media exist before allowing group subscription.
/// </summary>
[Authorize]
public class MediaHub : Hub
{
    private readonly ILogger<MediaHub> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public MediaHub(ILogger<MediaHub> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Join a library group to receive updates for that library (new items, scan progress).
    /// Validates that the library exists before allowing subscription.
    /// </summary>
    public async Task JoinLibrary(string libraryId)
    {
        if (!Guid.TryParse(libraryId, out var libGuid))
        {
            _logger.LogWarning("Client {ConnectionId} attempted to join invalid library ID: {LibraryId}", 
                Context.ConnectionId, libraryId);
            return;
        }

        // Verify library exists
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var libraryExists = await context.Libraries.AnyAsync(l => l.Id == libGuid);
        
        if (!libraryExists)
        {
            _logger.LogWarning("Client {ConnectionId} attempted to join non-existent library: {LibraryId}", 
                Context.ConnectionId, libraryId);
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"library-{libraryId}");
        _logger.LogDebug("Client {ConnectionId} joined library group {LibraryId}", Context.ConnectionId, libraryId);
    }

    /// <summary>
    /// Leave a library group.
    /// </summary>
    public async Task LeaveLibrary(string libraryId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"library-{libraryId}");
        _logger.LogDebug("Client {ConnectionId} left library group {LibraryId}", Context.ConnectionId, libraryId);
    }

    /// <summary>
    /// Join a media item group to receive updates when that item's metadata/images change.
    /// Validates that the media item exists before allowing subscription.
    /// </summary>
    public async Task JoinMedia(string mediaId)
    {
        if (!Guid.TryParse(mediaId, out var mediaGuid))
        {
            _logger.LogWarning("Client {ConnectionId} attempted to join invalid media ID: {MediaId}", 
                Context.ConnectionId, mediaId);
            return;
        }

        // Verify media item exists
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mediaExists = await context.MediaItems.AnyAsync(m => m.Id == mediaGuid);
        
        if (!mediaExists)
        {
            _logger.LogWarning("Client {ConnectionId} attempted to join non-existent media: {MediaId}", 
                Context.ConnectionId, mediaId);
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"media-{mediaId}");
        _logger.LogDebug("Client {ConnectionId} joined media group {MediaId}", Context.ConnectionId, mediaId);
    }

    /// <summary>
    /// Leave a media item group.
    /// </summary>
    public async Task LeaveMedia(string mediaId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"media-{mediaId}");
        _logger.LogDebug("Client {ConnectionId} left media group {MediaId}", Context.ConnectionId, mediaId);
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}

