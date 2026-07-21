using SoftMedia.Server.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Helpers;

/// The decision half of genre normalisation. These pin the properties that make the
/// pass safe to run against a live library: no item loses its whole taxonomy, a row
/// that splits survives as nothing, and a second run is a no-op.
public class GenreNormalizationPlanTests
{
    private static readonly Guid ItemA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ItemB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void MergesCaseVariantsOntoOneRowAndRetiresTheRest()
    {
        var genres = new[] { (1, "Science Fiction"), (2, "science fiction"), (3, "Science fiction") };
        var links = new[] { (ItemA, 1), (ItemA, 2), (ItemB, 3) };

        var plan = GenreNormalizationPlan.Build(genres, links);

        Assert.Equal(3, plan.GenresBefore);
        Assert.Equal(1, plan.GenresAfter);
        // The already-canonical spelling wins, so nothing needs renaming.
        Assert.Equal(1, plan.TargetIdByName["Science Fiction"]);
        Assert.Equal(new[] { 2, 3 }, plan.RetiredGenreIds.OrderBy(x => x));
        // Item A held the same genre under two spellings — it collapses to one link.
        Assert.Equal(new[] { "Science Fiction" }, plan.DesiredLinksByItem[ItemA]);
        Assert.Equal(3, plan.LinksBefore);
        Assert.Equal(2, plan.LinksAfter);
        Assert.Equal(0, plan.ItemsLeftWithNoGenres);
    }

    [Fact]
    public void ARowThatSplitsSurvivesAsNeitherGenre()
    {
        // "FICTION / Horror" becomes two names, so it cannot remain as either — both
        // are created fresh and the composite row retires.
        var genres = new[] { (1, "FICTION / Horror") };
        var links = new[] { (ItemA, 1) };

        var plan = GenreNormalizationPlan.Build(genres, links);

        Assert.Equal(new[] { 1 }, plan.RetiredGenreIds);
        Assert.Null(plan.TargetIdByName["Fiction"]);
        Assert.Null(plan.TargetIdByName["Horror"]);
        Assert.Equal(2, plan.GenresCreated);
        // The item gains both halves.
        Assert.Equal(new[] { "Fiction", "Horror" }, plan.DesiredLinksByItem[ItemA].OrderBy(x => x));
    }

    [Fact]
    public void ASplitReusesAnExistingRowForTheHalfThatAlreadyExists()
    {
        var genres = new[] { (1, "Horror"), (2, "FICTION / Horror") };
        var links = new[] { (ItemA, 2) };

        var plan = GenreNormalizationPlan.Build(genres, links);

        Assert.Equal(1, plan.TargetIdByName["Horror"]);   // reused, not recreated
        Assert.Null(plan.TargetIdByName["Fiction"]);      // genuinely new
        Assert.Equal(1, plan.GenresCreated);
        Assert.Equal(new[] { 2 }, plan.RetiredGenreIds);
    }

    [Fact]
    public void FlagsItemsThatWouldLoseEveryGenre()
    {
        // The item's only genre is junk that normalises away.
        var genres = new[] { (1, "Dune (Imaginary place)") };
        var links = new[] { (ItemA, 1) };

        var plan = GenreNormalizationPlan.Build(genres, links);

        Assert.Equal(1, plan.GenresDropped);
        Assert.Equal(1, plan.ItemsLeftWithNoGenres); // the service refuses to apply this
    }

    [Fact]
    public void DoesNotFlagAnItemThatKeepsAtLeastOneGenre()
    {
        var genres = new[] { (1, "Dune (Imaginary place)"), (2, "Fiction") };
        var links = new[] { (ItemA, 1), (ItemA, 2) };

        var plan = GenreNormalizationPlan.Build(genres, links);

        Assert.Equal(0, plan.ItemsLeftWithNoGenres);
        Assert.Equal(new[] { "Fiction" }, plan.DesiredLinksByItem[ItemA]);
    }

    [Fact]
    public void IsANoOpOnAlreadyCleanData()
    {
        var genres = new[] { (1, "Comedy"), (2, "Heavy Metal") };
        var links = new[] { (ItemA, 1), (ItemB, 2) };

        var plan = GenreNormalizationPlan.Build(genres, links);

        Assert.True(plan.IsNoOp);
        Assert.Empty(plan.RetiredGenreIds);
        Assert.Equal(plan.LinksBefore, plan.LinksAfter);
    }

    [Fact]
    public void RunningThePlanTwiceConverges()
    {
        // Feed the plan its own output: the second pass must find nothing to do.
        var genres = new[] { (1, "Science Fiction"), (2, "science fiction"), (3, "FICTION / Horror") };
        var links = new[] { (ItemA, 1), (ItemA, 2), (ItemB, 3) };

        var first = GenreNormalizationPlan.Build(genres, links);

        // Rebuild the post-apply state: surviving rows renamed, new rows assigned ids.
        var nextId = 100;
        var idByName = first.TargetIdByName.ToDictionary(
            kv => kv.Key, kv => kv.Value ?? nextId++, StringComparer.OrdinalIgnoreCase);
        var genresAfter = idByName.Select(kv => (kv.Value, kv.Key)).ToList();
        var linksAfter = first.DesiredLinksByItem
            .SelectMany(kv => kv.Value.Select(name => (kv.Key, idByName[name])))
            .ToList();

        var second = GenreNormalizationPlan.Build(genresAfter, linksAfter);

        Assert.True(second.IsNoOp);
        Assert.Equal(0, second.GenresDropped);
        Assert.Equal(0, second.ItemsLeftWithNoGenres);
    }
}
