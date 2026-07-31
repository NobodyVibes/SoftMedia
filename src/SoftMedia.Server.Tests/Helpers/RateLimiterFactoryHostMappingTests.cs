using SoftMedia.Server.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Helpers;

/// <summary>
/// SM-WI-020 — host→limiter mapping. The load-bearing property is the shared-budget
/// invariant: any path (metadata, search, image download) hitting the same provider host
/// must receive the SAME limiter instance, while unrelated hosts must never share one.
/// </summary>
public class RateLimiterFactoryHostMappingTests : IDisposable
{
    private readonly RateLimiterFactory _factory = new();

    [Theory]
    [InlineData("https://covers.openlibrary.org/b/id/12345-L.jpg", "OpenLibraryCovers")]
    [InlineData("https://openlibrary.org/search.json?q=dune", "OpenLibrary")]
    [InlineData("https://coverartarchive.org/release-group/abc/front", "CoverArtArchive")]
    [InlineData("https://upload.wikimedia.org/wikipedia/commons/a/ab/X.jpg", "WikimediaImages")]
    [InlineData("https://commons.wikimedia.org/wiki/File:X.jpg", "WikimediaImages")]
    [InlineData("https://musicbrainz.org/ws/2/artist?query=anthrax", "MusicBrainz")]
    [InlineData("https://api.tvmaze.com/shows/123", "TVMaze")]
    [InlineData("https://www.omdbapi.com/?apikey=x&t=dune", "OMDb")]
    [InlineData("https://query.wikidata.org/sparql?query=x", "Wikidata")]
    [InlineData("https://www.wikidata.org/w/api.php?action=wbsearchentities", "Wikidata")]
    public void KnownHosts_ShareTheNamedProviderLimiterInstance(string url, string limiterName)
    {
        var byHost = _factory.GetLimiterForHost(new Uri(url));
        var byName = _factory.GetLimiter(limiterName);

        Assert.Same(byName, byHost);
    }

    [Fact]
    public void UnknownHosts_GetTheirOwnStableLimiter_NotASharedBucket()
    {
        var amazonA = _factory.GetLimiterForHost(new Uri("https://m.media-amazon.com/images/a.jpg"));
        var amazonB = _factory.GetLimiterForHost(new Uri("https://m.media-amazon.com/images/b.jpg"));
        var tvmazeCdn = _factory.GetLimiterForHost(new Uri("https://static.tvmaze.com/uploads/x.jpg"));

        Assert.Same(amazonA, amazonB);       // stable per host
        Assert.NotSame(amazonA, tvmazeCdn);  // never shared across hosts
        // The CDN host must also not steal the API host's budget.
        Assert.NotSame(_factory.GetLimiter("TVMaze"), tvmazeCdn);
    }

    public void Dispose() => _factory.Dispose();
}
