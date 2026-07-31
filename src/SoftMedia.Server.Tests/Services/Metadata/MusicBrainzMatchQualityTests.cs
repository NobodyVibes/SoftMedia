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
/// SM-WI-031 — MusicBrainz match quality: MBID-first refresh (one direct request, no
/// search under the strict 1/s budget), score threshold (result[0] is no longer taken
/// blindly), and artist-credit agreement for release-groups. Names are real ones from
/// the operator's library (Anthrax / "Among The Living").
/// </summary>
public class MusicBrainzMatchQualityTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public List<string> Requests { get; } = new();

        public StubHandler Enqueue(string body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK)
        {
            _responses.Enqueue(new HttpResponseMessage(status) { Content = new StringContent(body) });
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add($"{request.Method} {request.RequestUri}");
            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        }
    }

    private static (MusicBrainzProvider Provider, StubHandler Handler) CreateProvider(
        StubHandler handler, Guid? artistId = null, string? artistTitle = null)
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        if (artistId.HasValue)
        {
            using var seedCtx = new AppDbContext(dbOptions);
            seedCtx.MediaItems.Add(new MediaItem
            {
                Id = artistId.Value,
                Title = artistTitle ?? "Artist",
                Type = MediaType.Artist,
                Path = @"C:\music\" + (artistTitle ?? "Artist"),
                LibraryId = Guid.NewGuid(),
            });
            seedCtx.SaveChanges();
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(dbOptions));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var provider = new MusicBrainzProvider(
            new HttpClient(handler),
            NullLogger<MusicBrainzProvider>.Instance,
            new RateLimiterFactory(),
            scopeFactory);
        return (provider, handler);
    }

    [Fact]
    public async Task Artist_WithPromotedMbid_FetchesDirectly_NoSearch()
    {
        var handler = new StubHandler().Enqueue(
            """{"id":"mbid-anthrax","name":"Anthrax","disambiguation":"US thrash metal band","type":"Group"}""");
        var (provider, h) = CreateProvider(handler);

        var item = new MediaItem { Title = "Anthrax", Type = MediaType.Artist, MusicBrainzId = "mbid-anthrax" };
        var result = await provider.FetchMetadataAsync(item);

        Assert.NotNull(result);
        Assert.Equal("Anthrax", result!.Title);
        Assert.Equal("mbid-anthrax", result.MusicBrainzId);
        Assert.Single(h.Requests);
        Assert.Contains("/ws/2/artist/mbid-anthrax", h.Requests[0]);
        Assert.DoesNotContain("query=", h.Requests[0]);
    }

    [Fact]
    public async Task Artist_SearchHit_BelowScoreThreshold_IsRejected()
    {
        // The "Nirvana" case: a notable but wrong entity with a weak score must not win.
        var handler = new StubHandler().Enqueue(
            """{"artists":[{"id":"wrong","name":"Somebody Else","score":60}]}""");
        var (provider, _) = CreateProvider(handler);

        var item = new MediaItem { Title = "Anthrax", Type = MediaType.Artist };
        var result = await provider.FetchMetadataAsync(item);

        Assert.Null(result); // prefer nothing over wrong
    }

    [Fact]
    public async Task Artist_ConfidentSearchHit_CarriesMbidForPromotion()
    {
        var handler = new StubHandler().Enqueue(
            """{"artists":[{"id":"mbid-anthrax","name":"Anthrax","score":100,"type":"Group"}]}""");
        var (provider, _) = CreateProvider(handler);

        var item = new MediaItem { Title = "Anthrax", Type = MediaType.Artist };
        var result = await provider.FetchMetadataAsync(item);

        Assert.NotNull(result);
        Assert.Equal("mbid-anthrax", result!.MusicBrainzId); // aggregator promotes → next refresh is ID-direct
    }

    [Fact]
    public async Task ReleaseGroup_SkipsWrongArtistCandidate_PicksAgreeingOne()
    {
        var artistId = Guid.NewGuid();
        var searchBody = """
            {"release-groups":[
              {"id":"wrong-rg","title":"Among The Living","score":100,
               "artist-credit":[{"name":"Some Cover Band"}]},
              {"id":"right-rg","title":"Among The Living","score":92,
               "first-release-date":"1987-03-22",
               "artist-credit":[{"name":"Anthrax"}]}
            ]}
            """;
        var handler = new StubHandler()
            .Enqueue(searchBody)
            .Enqueue("", System.Net.HttpStatusCode.TemporaryRedirect); // CAA HEAD: art exists
        var (provider, h) = CreateProvider(handler, artistId, "Anthrax");

        var item = new MediaItem
        {
            Title = "1987 - Among The Living",
            Type = MediaType.Album,
            ArtistId = artistId,
        };
        var result = await provider.FetchMetadataAsync(item);

        Assert.NotNull(result);
        Assert.Equal("right-rg", result!.MusicBrainzId);
        Assert.Equal(1987, result.Year);
        Assert.Contains("coverartarchive.org/release-group/right-rg", result.PosterUrl);
        // Exactly one search + one CAA probe — the wrong candidate cost zero requests.
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task ReleaseGroup_WithPromotedMbid_FetchesDirectly()
    {
        var handler = new StubHandler()
            .Enqueue("""{"id":"rg-1","title":"Spreading The Disease","first-release-date":"1985-10-30"}""")
            .Enqueue("", System.Net.HttpStatusCode.NotFound); // CAA: no art
        var (provider, h) = CreateProvider(handler);

        var item = new MediaItem { Title = "1985 - Spreading The Disease", Type = MediaType.Album, MusicBrainzId = "rg-1" };
        var result = await provider.FetchMetadataAsync(item);

        Assert.NotNull(result);
        Assert.Equal(1985, result!.Year);
        Assert.Null(result.PosterUrl); // 404 = no art, nothing stored
        Assert.Contains("/ws/2/release-group/rg-1", h.Requests[0]);
        Assert.DoesNotContain("query=", h.Requests[0]);
    }
}
