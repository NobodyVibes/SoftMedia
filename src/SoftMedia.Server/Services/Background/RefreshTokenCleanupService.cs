using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;

namespace SoftMedia.Server.Services.Background;

/// Prunes expired/revoked refresh-token rows from the database once a day.
/// Retains rows for 30 days past their expiry so recent reuse-detection scans
/// still have the chain links available; anything older is safe to drop.
public class RefreshTokenCleanupService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Period = TimeSpan.FromDays(1);
    public static readonly TimeSpan RetainAfterExpiry = TimeSpan.FromDays(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RefreshTokenCleanupService> _logger;

    public RefreshTokenCleanupService(
        IServiceScopeFactory scopeFactory,
        ILogger<RefreshTokenCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "RefreshTokenCleanupService started. Initial delay {InitialDelay}, period {Period}.",
            InitialDelay, Period);

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PruneOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RefreshTokenCleanupService iteration failed.");
            }

            try
            {
                await Task.Delay(Period, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PruneOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cutoff = DateTime.UtcNow - RetainAfterExpiry;
        var deleted = await PruneExpiredAsync(db, cutoff, ct);

        if (deleted > 0)
        {
            _logger.LogInformation(
                "RefreshTokenCleanupService pruned {Count} rows with ExpiresAt < {Cutoff:O}.",
                deleted, cutoff);
        }
    }

    /// Extracted so tests can drive the pruning logic with a known cutoff
    /// against a real relational DbContext (InMemory does not support
    /// <c>ExecuteDeleteAsync</c>).
    public static Task<int> PruneExpiredAsync(AppDbContext db, DateTime cutoff, CancellationToken ct = default)
    {
        return db.RefreshTokens
            .Where(rt => rt.ExpiresAt < cutoff)
            .ExecuteDeleteAsync(ct);
    }
}
