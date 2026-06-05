using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

/// Unit coverage for TVMazeProvider.SearchAsync (P3-WI-003): hits the real /search/shows
/// shape (parses ranked candidates, year, poster, network) but with a canned response
/// so the test doesn't talk to the network.
public class TVMazeProviderSearchTests
{
    private const string CannedResponse = """
    [
      {
        "score": 18.5,
        "show": {
          "id": 1,
          "name": "Under the Dome",
          "premiered": "2013-06-24",
          "image": { "medium": "https://example.com/p1m.jpg", "original": "https://example.com/p1.jpg" },
          "network": { "name": "CBS" }
        }
      },
      {
        "score": 17.0,
        "show": {
          "id": 2,
          "name": "Other Dome",
          "premiered": "2005-01-01",
          "image": null,
          "network": null
        }
      }
    ]
    """;

    private class CannedHandler : HttpMessageHandler
    {
        public string? LastRequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CannedResponse, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static TVMazeProvider BuildProvider(out CannedHandler handler)
    {
        handler = new CannedHandler();
        var http = new HttpClient(handler);
        return new TVMazeProvider(http, NullLogger<TVMazeProvider>.Instance, new RateLimiterFactory());
    }

    [Fact]
    public async Task SearchAsync_ParsesCandidatesWithYearAndPoster()
    {
        var provider = BuildProvider(out _);

        var results = await provider.SearchAsync("under the dome", year: null, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal("Under the Dome", results[0].Title);
        Assert.Equal(2013, results[0].Year);
        Assert.Equal("https://example.com/p1m.jpg", results[0].PosterUrl);
        Assert.Equal("CBS", results[0].Subtitle);
        Assert.Equal("TVMaze", results[0].ProviderName);
        Assert.Equal("1", results[0].ProviderItemId);
    }

    [Fact]
    public async Task SearchAsync_AppliesYearFilter()
    {
        var provider = BuildProvider(out _);
        // year=2013 ± 1 keeps only the 2013-premiered show, not 2005.
        var results = await provider.SearchAsync("dome", year: 2013, CancellationToken.None);
        Assert.Single(results);
        Assert.Equal(2013, results[0].Year);
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsEmpty_WithoutHittingHttp()
    {
        var provider = BuildProvider(out var handler);
        var results = await provider.SearchAsync("  ", year: null, CancellationToken.None);
        Assert.Empty(results);
        Assert.Null(handler.LastRequestUri); // early-return before HTTP
    }

    [Fact]
    public async Task SearchAsync_UrlEscapesQuery()
    {
        var provider = BuildProvider(out var handler);
        await provider.SearchAsync("rick & morty", year: null, CancellationToken.None);
        Assert.NotNull(handler.LastRequestUri);
        // Uri.EscapeDataString escapes '&' as %26. Spaces are encoded as %20 in .NET 8.
        // The point is the raw '&' must NOT appear in the query — it would break URI parsing.
        Assert.Contains("%26", handler.LastRequestUri!);
        Assert.DoesNotContain("rick & morty", handler.LastRequestUri!);
    }
}
