using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

/// <summary>
/// SM-WI-040 — negative-result cache. Runs against REAL SQLite (in-memory connection),
/// not EF InMemory: the upsert is the load-bearing query and InMemory would hide any
/// translation problem (project convention — see plan preamble).
/// </summary>
public class ProviderLookupCacheTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IServiceScopeFactory _scopeFactory;

    public ProviderLookupCacheTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using (var ctx = new AppDbContext(options))
        {
            ctx.Database.EnsureCreated();
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(options));
        _scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private ProviderLookupCacheService CreateService(Func<DateTime> utcNow)
        => new(_scopeFactory, NullLogger<ProviderLookupCacheService>.Instance, utcNow);

    [Fact]
    public async Task RecordedMiss_IsFresh_UntilTtl_ThenExpires()
    {
        var now = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
        var service = CreateService(() => now);
        var key = ProviderLookupCacheService.NormalizeKey("Movie", "A Boy and His Dog", 1975);

        Assert.False(await service.IsFreshMissAsync("Wikidata", key)); // never recorded

        await service.RecordMissAsync("Wikidata", key);
        Assert.True(await service.IsFreshMissAsync("Wikidata", key));

        now = now.AddDays(29);
        Assert.True(await service.IsFreshMissAsync("Wikidata", key)); // still inside 30d

        now = now.AddDays(2);
        Assert.False(await service.IsFreshMissAsync("Wikidata", key)); // TTL passed → retry allowed
    }

    [Fact]
    public async Task RepeatMisses_Upsert_BumpAttemptCount_AndRefreshTtl()
    {
        var now = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
        var service = CreateService(() => now);
        var key = ProviderLookupCacheService.NormalizeKey("artist", "Some Unknown Band");

        await service.RecordMissAsync("MusicBrainz", key);
        now = now.AddDays(31); // expired
        await service.RecordMissAsync("MusicBrainz", key); // re-attempt missed again

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.ProviderLookupCache.SingleAsync();
        Assert.Equal(2, row.AttemptCount);
        Assert.Equal(now, row.LastAttemptUtc);
        Assert.True(await service.IsFreshMissAsync("MusicBrainz", key)); // TTL re-anchored
    }

    [Fact]
    public async Task Providers_DoNotShareMisses()
    {
        var service = CreateService(() => DateTime.UtcNow);
        var key = ProviderLookupCacheService.NormalizeKey("Movie", "A Star is Born");

        await service.RecordMissAsync("Wikidata", key);

        Assert.True(await service.IsFreshMissAsync("Wikidata", key));
        Assert.False(await service.IsFreshMissAsync("OMDb", key));
    }

    [Theory]
    [InlineData(new object?[] { new object?[] { "Movie", "  Dune ", 1984 } })]
    [InlineData(new object?[] { new object?[] { "movie", "dune", "1984" } })]
    public void NormalizeKey_IsCaseAndWhitespaceInsensitive(object?[] parts)
    {
        Assert.Equal("movie|dune|1984", ProviderLookupCacheService.NormalizeKey(parts));
    }

    [Fact]
    public void NormalizeKey_DropsNullParts()
    {
        Assert.Equal("movie|dune", ProviderLookupCacheService.NormalizeKey("Movie", "Dune", null));
    }

    public void Dispose() => _connection.Dispose();
}
