using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Metadata;

/// <summary>
/// SM-WI-040 — see <see cref="ProviderLookupCacheEntry"/> for the semantics. Providers
/// consult <see cref="IsFreshMissAsync"/> before a SEARCH-shaped network call and call
/// <see cref="RecordMissAsync"/> on a definitive no-match. Never used for ID-based
/// lookups, never for transient errors (the retry ladder owns those).
/// </summary>
public interface IProviderLookupCache
{
    /// <summary>True when this exact query missed within the TTL — skip the network call.</summary>
    Task<bool> IsFreshMissAsync(string provider, string queryKey, CancellationToken ct = default);

    /// <summary>Upsert a definitive-miss row (bumps AttemptCount, refreshes the TTL anchor).</summary>
    Task RecordMissAsync(string provider, string queryKey, CancellationToken ct = default);
}

public class ProviderLookupCacheService : IProviderLookupCache
{
    /// <summary>How long a definitive miss suppresses re-searching the same query.</summary>
    public static readonly TimeSpan MissTtl = TimeSpan.FromDays(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProviderLookupCacheService> _logger;
    private readonly Func<DateTime> _utcNow;

    public ProviderLookupCacheService(
        IServiceScopeFactory scopeFactory,
        ILogger<ProviderLookupCacheService> logger,
        Func<DateTime>? utcNow = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Canonical query key: lowercase, trimmed, null/empty parts dropped, '|'-joined.
    /// Deterministic so the same item produces the same key on every tier/rescan/amnesty.
    /// </summary>
    public static string NormalizeKey(params object?[] parts)
        => string.Join('|', parts
            .Select(p => p?.ToString()?.Trim().ToLowerInvariant())
            .Where(s => !string.IsNullOrEmpty(s)));

    public async Task<bool> IsFreshMissAsync(string provider, string queryKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(queryKey)) return false;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.ProviderLookupCache.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Provider == provider && e.QueryKey == queryKey, ct);
            return row != null && _utcNow() - row.LastAttemptUtc < MissTtl;
        }
        catch (Exception ex)
        {
            // Cache failure must never block enrichment — fall through to the network.
            _logger.LogWarning(ex, "Provider lookup cache read failed for {Provider}/{Key}", provider, queryKey);
            return false;
        }
    }

    public async Task RecordMissAsync(string provider, string queryKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(queryKey)) return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.ProviderLookupCache
                .FirstOrDefaultAsync(e => e.Provider == provider && e.QueryKey == queryKey, ct);
            if (row == null)
            {
                db.ProviderLookupCache.Add(new ProviderLookupCacheEntry
                {
                    Provider = provider,
                    QueryKey = queryKey,
                    LastAttemptUtc = _utcNow(),
                    AttemptCount = 1,
                });
            }
            else
            {
                row.LastAttemptUtc = _utcNow();
                row.AttemptCount++;
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Provider lookup cache write failed for {Provider}/{Key}", provider, queryKey);
        }
    }
}
