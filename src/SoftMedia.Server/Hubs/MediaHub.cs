using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Services.Security.LibraryAccess;

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

    // Audit wave-2 L-23: bound Join* invocations per connection. Each Join runs 1-2 SQLite queries,
    // so an authenticated client spamming joins is a cheap DB-pressure vector. Fixed-window counter
    // keyed by connection id (evicted on disconnect); excess joins are silently dropped. 30 / 10s
    // is far above any legitimate burst (the SPA joins one library group on navigation).
    private const int MaxJoinsPerWindow = 30;
    private static readonly TimeSpan JoinWindow = TimeSpan.FromSeconds(10);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime WindowStart, int Count)> _joinRates = new();

    public MediaHub(ILogger<MediaHub> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    /// <summary>Fixed-window per-connection throttle for the Join* methods (audit wave-2 L-23).
    /// Returns true when the caller has exceeded the window budget and the join should be dropped.</summary>
    private bool JoinThrottleExceeded()
    {
        var now = DateTime.UtcNow;
        var entry = _joinRates.AddOrUpdate(
            Context.ConnectionId,
            _ => (now, 1),
            (_, s) => (now - s.WindowStart) > JoinWindow ? (now, 1) : (s.WindowStart, s.Count + 1));
        if (entry.Count > MaxJoinsPerWindow)
        {
            _logger.LogWarning("Connection {ConnectionId} exceeded the Join throttle ({Count}/{Window}s)",
                Context.ConnectionId, entry.Count, JoinWindow.TotalSeconds);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Join a library group to receive updates for that library (new items, scan progress).
    /// Validates that the library exists before allowing subscription.
    /// </summary>
    public async Task JoinLibrary(string libraryId)
    {
        if (JoinThrottleExceeded()) return;
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

        // Wave C — per-user library ACL gate. Resolved via the same provider
        // used by repository filters; admins always pass. Class-level [Authorize]
        // guarantees Context.User is authenticated when we reach this point.
        if (!await IsLibraryAllowedForCallerAsync(scope, libGuid))
        {
            _logger.LogInformation(
                "Client {ConnectionId} blocked from library {LibraryId} by per-user ACL",
                Context.ConnectionId, libraryId);
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"library-{libraryId}");
        _logger.LogDebug("Client {ConnectionId} joined library group {LibraryId}", Context.ConnectionId, libraryId);
    }

    /// <summary>
    /// Wave C helper — checks whether the calling user's per-library ACL allows
    /// access to <paramref name="libraryId"/>. Admins and unauthenticated calls
    /// (which shouldn't reach here under [Authorize]) pass-through as allowed.
    /// </summary>
    private async Task<bool> IsLibraryAllowedForCallerAsync(
        IServiceScope scope, Guid libraryId)
    {
        var accessProvider = scope.ServiceProvider.GetRequiredService<IUserLibraryAccessProvider>();
        // The provider reads HttpContext.User; Hub calls don't go through HTTP
        // middleware that fills HttpContextAccessor, so we resolve directly
        // from Context.User here instead.
        var principal = Context.User;
        if (principal?.Identity?.IsAuthenticated != true) return false;
        if (principal.IsInRole(Models.UserRole.Admin.ToString())) return true;

        var sub = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? principal.FindFirst("sub")?.Value;
        if (!Guid.TryParse(sub, out var userId)) return true; // fail-open per provider semantics

        var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var allowed = await ctx.UserLibraryAccess
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .Select(a => a.LibraryId)
            .ToListAsync();

        return allowed.Count == 0 || allowed.Contains(libraryId);
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
        if (JoinThrottleExceeded()) return;
        if (!Guid.TryParse(mediaId, out var mediaGuid))
        {
            _logger.LogWarning("Client {ConnectionId} attempted to join invalid media ID: {MediaId}",
                Context.ConnectionId, mediaId);
            return;
        }

        // Verify media item exists; capture LibraryId for the ACL check
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mediaLibraryId = await context.MediaItems
            .AsNoTracking()
            .Where(m => m.Id == mediaGuid)
            .Select(m => (Guid?)m.LibraryId)
            .FirstOrDefaultAsync();

        if (mediaLibraryId is null)
        {
            _logger.LogWarning("Client {ConnectionId} attempted to join non-existent media: {MediaId}",
                Context.ConnectionId, mediaId);
            return;
        }

        // Wave C — drop the join if the media's library isn't in the caller's
        // ACL allow-list.
        if (!await IsLibraryAllowedForCallerAsync(scope, mediaLibraryId.Value))
        {
            _logger.LogInformation(
                "Client {ConnectionId} blocked from media {MediaId} (library {LibraryId}) by per-user ACL",
                Context.ConnectionId, mediaId, mediaLibraryId);
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
        _joinRates.TryRemove(Context.ConnectionId, out _); // audit wave-2 L-23: evict throttle state
        await base.OnDisconnectedAsync(exception);
    }
}

