using System.Text.Json;
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

/// <summary>
/// SM-WI-030 — Wikidata movie candidates are disambiguated by the file's parsed year.
/// The canonical failure this kills: "Dune (1984)" resolving to the more-notable 2021
/// film, whose poster then made the item look enrichment-complete (self-sealing wrong
/// match). Rule 4: when every candidate carries a contradicting year, prefer NO match.
/// </summary>
public class WikidataYearDisambiguationTests
{
    /// <summary>Builds a SPARQL-shaped bindings array; null year = binding without ?year.</summary>
    private static JsonElement Bindings(params (string Label, int? Year)[] candidates)
    {
        var rows = candidates.Select(c => c.Year.HasValue
            ? "{\"itemLabel\":{\"value\":\"" + c.Label + "\"},\"year\":{\"value\":\"" + c.Year + "\"}}"
            : "{\"itemLabel\":{\"value\":\"" + c.Label + "\"}}");
        var doc = JsonDocument.Parse("[" + string.Join(",", rows) + "]");
        return doc.RootElement.Clone();
    }

    private static string? LabelOf(JsonElement? binding)
        => binding?.GetProperty("itemLabel").GetProperty("value").GetString();

    [Fact]
    public void FileYear_PicksTheMatchingRemake_NotTheMostNotable()
    {
        // Dune case: 2021 film ranks first by notability, but the file says 1984.
        var bindings = Bindings(("Dune 2021", 2021), ("Dune 1984", 1984));

        var selected = WikidataProvider.SelectMovieBinding(bindings, fileYear: 1984);

        Assert.Equal("Dune 1984", LabelOf(selected));
    }

    [Fact]
    public void AdjacentYear_WithinOne_IsAccepted()
    {
        // Release-year vs premiere-year off-by-one is common (festival releases).
        var bindings = Bindings(("Wrong Film", 2005), ("Right Film", 1998));

        var selected = WikidataProvider.SelectMovieBinding(bindings, fileYear: 1999);

        Assert.Equal("Right Film", LabelOf(selected));
    }

    [Fact]
    public void NoFileYear_KeepsMostNotableCandidate()
    {
        var bindings = Bindings(("Most Notable", 2021), ("Other", 1984));

        var selected = WikidataProvider.SelectMovieBinding(bindings, fileYear: null);

        Assert.Equal("Most Notable", LabelOf(selected));
    }

    [Fact]
    public void AllCandidatesContradictFileYear_ReturnsNoMatch()
    {
        var bindings = Bindings(("A", 2021), ("B", 2010), ("C", 1955));

        var selected = WikidataProvider.SelectMovieBinding(bindings, fileYear: 1984);

        Assert.Null(selected); // prefer nothing over wrong
    }

    [Fact]
    public void YearlessCandidate_BeatsContradictingOnes()
    {
        // Wikidata items missing P577 can't be contradicted by the file year.
        var bindings = Bindings(("Dated Wrong", 2021), ("Undated", null));

        var selected = WikidataProvider.SelectMovieBinding(bindings, fileYear: 1984);

        Assert.Equal("Undated", LabelOf(selected));
    }

    [Fact]
    public void SingleCandidate_IsKept_EvenWithoutYearAgreementCheck()
    {
        // One candidate = no disambiguation signal between candidates; keep legacy
        // behavior rather than dropping the only hit.
        var bindings = Bindings(("Only Hit", 2021));

        var selected = WikidataProvider.SelectMovieBinding(bindings, fileYear: 1984);

        Assert.Equal("Only Hit", LabelOf(selected));
    }
}
