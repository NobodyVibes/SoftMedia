using SoftMedia.Server.Controllers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Security.ContentRating;
using Xunit;

namespace SoftMedia.Server.Tests.Controllers;

/// R-WI-011 — maintainer decision 2026-07-17: new users are NEVER content-rating restricted by
/// default; the admin sets ceilings explicitly. These tests pin the new-user default, the single
/// write path (strip empties, validate labels, sync the legacy MaxRating fallback), and the
/// killed "None (Unrestricted) still capped movies at PG-13" lie.
public class UserContentRatingsTests
{
    [Fact]
    public void NewUser_HasNoContentRestrictions()
    {
        var user = new User();

        Assert.Equal("", user.MaxRating); // the old silent "PG-13" default is gone
        var ceilings = UserRatingCeilings.From(user);
        Assert.Null(ceilings.Movie);
        Assert.Null(ceilings.Tv);
        Assert.Null(ceilings.Game);
    }

    [Fact]
    public void ApplyContentRatings_Null_ClearsToUnrestricted()
    {
        // An existing user carrying the legacy PG-13 default gets fully cleared on edit.
        var user = new User { MaxRating = "PG-13", ContentRatings = "{\"Movie\":\"PG-13\"}" };

        var error = UsersController.ApplyContentRatings(user, null);

        Assert.Null(error);
        Assert.Equal("{}", user.ContentRatings);
        Assert.Equal("", user.MaxRating);
        Assert.Null(UserRatingCeilings.From(user).Movie);
    }

    [Fact]
    public void ApplyContentRatings_ClearingMovie_KillsTheLegacyFallbackLie()
    {
        // THE reported bug: the ratings modal posts {"Movie":""} for "None (Unrestricted)",
        // but the old write path left MaxRating="PG-13" — so the user stayed movie-capped
        // while the admin saw "Unrestricted". The write path must sync the legacy field.
        var user = new User { MaxRating = "PG-13", ContentRatings = "{}" };

        var error = UsersController.ApplyContentRatings(user, new Dictionary<string, string> { ["Movie"] = "" });

        Assert.Null(error);
        Assert.Equal("", user.MaxRating);
        Assert.Null(UserRatingCeilings.From(user).Movie); // truly unrestricted now
    }

    [Fact]
    public void ApplyContentRatings_StripsEmpties_AndSyncsLegacyMovie()
    {
        var user = new User();

        var error = UsersController.ApplyContentRatings(user, new Dictionary<string, string>
        {
            ["Movie"] = "PG",
            ["TV"] = "",       // "None" — must not be stored
            ["Game"] = "  ",   // whitespace — must not be stored
        });

        Assert.Null(error);
        Assert.Equal("PG", user.MaxRating); // legacy fallback kept truthful
        var ceilings = UserRatingCeilings.From(user);
        Assert.Equal("PG", ceilings.Movie);
        Assert.Null(ceilings.Tv);
        Assert.Null(ceilings.Game);
        Assert.DoesNotContain("TV", user.ContentRatings);
    }

    [Theory]
    [InlineData("Movie", "BANANA")]  // unknown label
    [InlineData("Movie", "TV-14")]   // wrong table
    [InlineData("Anime", "R")]       // unknown type key
    public void ApplyContentRatings_UnknownTypeOrLabel_RejectedWithoutMutating(string type, string label)
    {
        // RatingTables fails OPEN on unknown ceilings, so a typo'd cap would silently
        // unrestrict — the write path must reject instead. The user row must be untouched.
        var user = new User { MaxRating = "R", ContentRatings = "{\"Movie\":\"R\"}" };

        var error = UsersController.ApplyContentRatings(user, new Dictionary<string, string> { [type] = label });

        Assert.NotNull(error);
        Assert.Equal("R", user.MaxRating);
        Assert.Equal("{\"Movie\":\"R\"}", user.ContentRatings);
    }

    [Fact]
    public void ApplyContentRatings_NormalizesLabelCasing()
    {
        var user = new User();
        var error = UsersController.ApplyContentRatings(user, new Dictionary<string, string> { ["TV"] = "tv-14" });
        Assert.Null(error);
        Assert.Equal("TV-14", UserRatingCeilings.From(user).Tv); // canonical casing stored
    }
}
