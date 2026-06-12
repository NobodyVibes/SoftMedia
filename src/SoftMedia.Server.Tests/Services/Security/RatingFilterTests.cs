using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Security.ContentRating;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Security;

/// Phase 3 / B1 + B3 — verifies the rating filter classifies items correctly
/// across the supported types (Movie, Series, Episode, Game) and respects the
/// fail-safe rules (null ContentRating, unknown ceiling, admin bypass).
///
/// Tests run against a real in-memory SQLite database so the IQueryable
/// extension is exercised end-to-end through EF Core (no client-side eval).
public class RatingFilterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public RatingFilterTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
        SeedFixtures(ctx);
    }

    public void Dispose() => _connection.Dispose();

    private static void SeedFixtures(AppDbContext ctx)
    {
        var lib = new Library { Id = Guid.NewGuid(), Name = "L", Type = LibraryType.Movie, Paths = new() { "/x" } };
        ctx.Libraries.Add(lib);

        // Movies across the rating ladder + one with null rating (fail-safe).
        ctx.MediaItems.AddRange(
            Movie(lib, "G-Movie", "G"),
            Movie(lib, "PG-Movie", "PG"),
            Movie(lib, "PG13-Movie", "PG-13"),
            Movie(lib, "R-Movie", "R"),
            Movie(lib, "NC17-Movie", "NC-17"),
            Movie(lib, "Unrated-Movie", null),

            // TV
            Series(lib, "TV-Y-Show", "TV-Y"),
            Series(lib, "TV-PG-Show", "TV-PG"),
            Series(lib, "TV-MA-Show", "TV-MA"),

            // Game
            Game(lib, "E-Game", "E"),
            Game(lib, "T-Game", "T"),
            Game(lib, "M-Game", "M"));

        ctx.SaveChanges();
    }

    private static MediaItem Movie(Library lib, string title, string? rating) =>
        new() { LibraryId = lib.Id, Title = title, SortTitle = title, Path = $"/x/{title}.mkv", Type = MediaType.Movie, ContentRating = rating };
    private static MediaItem Series(Library lib, string title, string? rating) =>
        new() { LibraryId = lib.Id, Title = title, SortTitle = title, Path = $"/x/{title}", Type = MediaType.Series, ContentRating = rating };
    private static MediaItem Game(Library lib, string title, string? rating) =>
        new() { LibraryId = lib.Id, Title = title, SortTitle = title, Path = $"/x/{title}", Type = MediaType.Game, ContentRating = rating };

    private async Task<List<string>> Run(UserRatingCeilings ceilings)
    {
        await using var ctx = new AppDbContext(_options);
        return await ctx.MediaItems
            .ApplyContentRatingFilter(ceilings)
            .OrderBy(m => m.Title)
            .Select(m => m.Title)
            .ToListAsync();
    }

    [Fact]
    public async Task Unrestricted_ReturnsEverything()
    {
        var titles = await Run(UserRatingCeilings.Unrestricted);
        Assert.Equal(12, titles.Count);
    }

    [Fact]
    public async Task MoviePG13Ceiling_HidesAtAndAboveR()
    {
        var ceilings = UserRatingCeilings.From(new User
        {
            MaxRating = "PG-13",
            ContentRatings = "{}", // legacy single-string fallback
        });
        var titles = await Run(ceilings);

        Assert.Contains("G-Movie", titles);
        Assert.Contains("PG-Movie", titles);
        Assert.Contains("PG13-Movie", titles);
        Assert.DoesNotContain("R-Movie", titles);
        Assert.DoesNotContain("NC17-Movie", titles);
        Assert.DoesNotContain("Unrated-Movie", titles); // fail-safe: null hidden
        // TV and Game are unrestricted because no per-type ceiling was set.
        Assert.Contains("TV-MA-Show", titles);
        Assert.Contains("M-Game", titles);
    }

    [Fact]
    public async Task PerTypeCeilings_GateEachTypeIndependently()
    {
        var ceilings = UserRatingCeilings.From(new User
        {
            MaxRating = "PG-13",
            ContentRatings = """{"Movie":"G","TV":"TV-PG","Game":"E"}"""
        });
        var titles = await Run(ceilings);

        // Movies: only G allowed.
        Assert.Equal(new[] { "G-Movie" }, titles.Where(t => t.EndsWith("-Movie")).ToArray());
        // TV: TV-Y, TV-Y7, TV-G, TV-PG allowed; TV-MA hidden.
        Assert.Contains("TV-Y-Show", titles);
        Assert.Contains("TV-PG-Show", titles);
        Assert.DoesNotContain("TV-MA-Show", titles);
        // Game: only EC, E allowed.
        Assert.Contains("E-Game", titles);
        Assert.DoesNotContain("T-Game", titles);
        Assert.DoesNotContain("M-Game", titles);
    }

    [Fact]
    public async Task PerTypeCeilingsOverrideMaxRatingFallback()
    {
        // MaxRating="G" would normally cap movies at G. The per-type Movie="R"
        // entry must win — that's the SDD §6.2 contract: ContentRatings JSON
        // is authoritative when present.
        var ceilings = UserRatingCeilings.From(new User
        {
            MaxRating = "G",
            ContentRatings = """{"Movie":"R"}"""
        });
        var titles = await Run(ceilings);

        Assert.Contains("R-Movie", titles);
        Assert.DoesNotContain("NC17-Movie", titles);
    }

    [Fact]
    public async Task UnknownCeiling_FailsOpenForThatType()
    {
        // An unknown ceiling string (e.g. "Unrated-XYZ") is treated as no
        // filter for that type — fail-open. Better to show extra content than
        // accidentally hide a child's whole library because of a typo.
        var ceilings = UserRatingCeilings.From(new User
        {
            MaxRating = "GARBAGE",
            ContentRatings = "{}"
        });
        var titles = await Run(ceilings);

        Assert.Equal(12, titles.Count);
    }

    [Fact]
    public async Task NullContentRatingHiddenWhenAnyCeilingSet()
    {
        // Movies have one null-rated row. With a Movie ceiling configured,
        // it must NOT appear (fail-safe).
        var ceilings = UserRatingCeilings.From(new User
        {
            MaxRating = "NC-17", // permissive but still set
            ContentRatings = "{}"
        });
        var titles = await Run(ceilings);

        Assert.DoesNotContain("Unrated-Movie", titles);
        // Sanity: every other movie is visible at NC-17 ceiling.
        Assert.Contains("NC17-Movie", titles);
    }

    // ── In-memory IsRatingAllowed helper (audit wave-2 WS-2) ──────────────────
    // The collections/recent-cache paths can't run the EF predicate, so they use
    // RatingFilterExtensions.IsRatingAllowed. These lock it to the SAME semantics.

    [Fact]
    public void IsRatingAllowed_Unrestricted_AllowsEverythingIncludingNull()
    {
        Assert.True(RatingFilterExtensions.IsRatingAllowed(UserRatingCeilings.Unrestricted, MediaType.Movie, "NC-17"));
        Assert.True(RatingFilterExtensions.IsRatingAllowed(UserRatingCeilings.Unrestricted, MediaType.Movie, null));
    }

    [Fact]
    public void IsRatingAllowed_MovieCeiling_MatchesQueryableSemantics()
    {
        var ceilings = UserRatingCeilings.From(new User { MaxRating = "PG-13", ContentRatings = "{}" });

        Assert.True(RatingFilterExtensions.IsRatingAllowed(ceilings, MediaType.Movie, "G"));
        Assert.True(RatingFilterExtensions.IsRatingAllowed(ceilings, MediaType.Movie, "PG-13"));
        Assert.False(RatingFilterExtensions.IsRatingAllowed(ceilings, MediaType.Movie, "R"));
        Assert.False(RatingFilterExtensions.IsRatingAllowed(ceilings, MediaType.Movie, "NC-17"));
        // Fail-safe: null rating blocked when a ceiling applies to the type.
        Assert.False(RatingFilterExtensions.IsRatingAllowed(ceilings, MediaType.Movie, null));
        // Ungated types pass through regardless of rating.
        Assert.True(RatingFilterExtensions.IsRatingAllowed(ceilings, MediaType.Audio, null));
    }

    [Fact]
    public void IsRatingAllowed_PerTypeCeilings_GateEachTypeIndependently()
    {
        var ceilings = UserRatingCeilings.From(new User
        {
            MaxRating = "PG-13",
            ContentRatings = """{"Movie":"G","TV":"TV-PG","Game":"E"}"""
        });

        Assert.True(RatingFilterExtensions.IsRatingAllowed(ceilings, MediaType.Movie, "G"));
        Assert.False(RatingFilterExtensions.IsRatingAllowed(ceilings, MediaType.Movie, "R"));
        Assert.True(RatingFilterExtensions.IsRatingAllowed(ceilings, MediaType.Series, "TV-PG"));
        Assert.False(RatingFilterExtensions.IsRatingAllowed(ceilings, MediaType.Episode, "TV-MA"));
        Assert.True(RatingFilterExtensions.IsRatingAllowed(ceilings, MediaType.Game, "E"));
        Assert.False(RatingFilterExtensions.IsRatingAllowed(ceilings, MediaType.Game, "M"));
    }

    [Fact]
    public async Task MalformedContentRatingsJson_TreatedAsEmpty()
    {
        // A user row with corrupt JSON must not 500 the request. The provider
        // is the only place that touches the JSON; UserRatingCeilings.From
        // swallows JsonException and falls back to MaxRating only.
        var ceilings = UserRatingCeilings.From(new User
        {
            MaxRating = "PG-13",
            ContentRatings = "{\"Movie\": [malformed"
        });
        var titles = await Run(ceilings);

        Assert.DoesNotContain("R-Movie", titles);
        Assert.Contains("PG13-Movie", titles);
    }
}
