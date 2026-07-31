using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Metadata;
using System.Text.Json;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

/// <summary>
/// SM-WI-012 — titles with quotes/backslashes must produce well-formed SPARQL from
/// every construction site (BuildEntitySearchSelector was unescaped; only the comic
/// provider escaped). A malformed query is an HTTP 400 that rides the whole retry
/// ladder and burns the WDQS error budget.
/// </summary>
public class WikidataSparqlEscapingTests
{
    private sealed class ProbeSparqlClient : WikidataSparqlClient
    {
        public ProbeSparqlClient()
            : base(new HttpClient(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, new RateLimiterFactory())
        {
        }

        public override LibraryType SupportedType => LibraryType.Movie;
        public override string ProviderName => "Probe";

        public string BuildSelector(string title) => BuildEntitySearchSelector(title, "Q11424");

        protected override string BuildSparqlQuery(MediaItem item) => BuildSelector(item.Title);
        protected override MetadataResult ExtractMetadata(JsonElement result, MediaItem item) => new();
    }

    [Theory]
    [InlineData("plain title", "plain title")]
    [InlineData("He Said \"Maybe\"", "He Said \\\"Maybe\\\"")]
    [InlineData(@"back\slash", @"back\\slash")]
    [InlineData("both \\ and \"", "both \\\\ and \\\"")]
    // Real library name (apostrophes are legal in double-quoted SPARQL literals):
    [InlineData("Nell'immenso verde profondo", "Nell'immenso verde profondo")]
    public void EscapeForSparql_EscapesQuotesAndBackslashes(string input, string expected)
    {
        Assert.Equal(expected, WikidataSparqlClient.EscapeForSparql(input));
    }

    [Fact]
    public void BuildEntitySearchSelector_EmbedsEscapedTitle()
    {
        var selector = new ProbeSparqlClient().BuildSelector("He Said \"Maybe\"");

        Assert.Contains("mwapi:search \"He Said \\\"Maybe\\\"\"", selector);
        // The raw quoted form must not appear (it would close the literal early).
        Assert.DoesNotContain("mwapi:search \"He Said \"Maybe\"\"", selector);
    }
}
