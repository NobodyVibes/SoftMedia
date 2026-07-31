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
/// SM-WI-040 — end-to-end through a real provider: the first definitive miss records,
/// the second identical lookup costs ZERO network calls. TVMaze is the representative
/// provider (search-fallback shape); the others share the same service.
/// </summary>
public class NegativeCacheProviderIntegrationTests : IDisposable
{
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests;
        public string Body = "[]"; // TVMaze empty search result

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(Body),
            });
        }
    }

    private readonly SqliteConnection _connection;

    public NegativeCacheProviderIntegrationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    [Fact]
    public async Task SecondLookup_AfterDefinitiveMiss_MakesNoNetworkCall()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using (var ctx = new AppDbContext(options)) ctx.Database.EnsureCreated();
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(options));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var cache = new ProviderLookupCacheService(scopeFactory, NullLogger<ProviderLookupCacheService>.Instance);

        var handler = new CountingHandler();
        var provider = new TVMazeProvider(
            new HttpClient(handler),
            NullLogger<TVMazeProvider>.Instance,
            new RateLimiterFactory(),
            cache);

        // Real yearless-parse name from the library; no promoted IDs → search path.
        var item = new MediaItem { Title = "A Christmas Without Snow", Type = MediaType.Series };

        Assert.Null(await provider.FetchMetadataAsync(item));
        Assert.Equal(1, handler.Requests); // searched once, empty → miss recorded

        Assert.Null(await provider.FetchMetadataAsync(item));
        Assert.Equal(1, handler.Requests); // cached miss: zero additional requests
    }

    public void Dispose() => _connection.Dispose();
}
