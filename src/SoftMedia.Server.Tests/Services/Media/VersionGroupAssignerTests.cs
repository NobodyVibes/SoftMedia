using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Background;
using SoftMedia.Server.Services.Media;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// <summary>
/// DV-WI-010 — version-group assignment rules (plan §2.1) on REAL SQLite: episodes get a
/// deterministic (SeriesId, Season, Episode) id so parallel workers converge; movies
/// group by provider-id-then-title+year with the provider VETO for collisions;
/// everything is fill-only so admin splits survive; the boot backfill is idempotent.
/// </summary>
public class VersionGroupAssignerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _libraryId = Guid.NewGuid();

    public VersionGroupAssignerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
        ctx.Libraries.Add(new Library { Id = _libraryId, Name = "Movies", Type = LibraryType.Movie });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private AppDbContext NewContext() => new(_options);

    private MediaItem AddMovie(AppDbContext ctx, string title, int? year, string? imdbId = null,
        Guid? groupId = null, Guid? libraryId = null)
    {
        var m = new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = libraryId ?? _libraryId, Type = MediaType.Movie,
            Title = title, Year = year, ImdbId = imdbId, VersionGroupId = groupId,
        };
        ctx.MediaItems.Add(m);
        ctx.SaveChanges();
        return m;
    }

    // ───────────────────────────── helper determinism ─────────────────────────────

    [Fact]
    public void Episode_group_id_is_deterministic_and_identity_sensitive()
    {
        var seriesId = Guid.NewGuid();
        Assert.Equal(
            VersionGroupHelper.ComputeEpisodeGroupId(seriesId, 1, 3),
            VersionGroupHelper.ComputeEpisodeGroupId(seriesId, 1, 3));
        Assert.NotEqual(
            VersionGroupHelper.ComputeEpisodeGroupId(seriesId, 1, 3),
            VersionGroupHelper.ComputeEpisodeGroupId(seriesId, 1, 4));
        Assert.NotEqual(
            VersionGroupHelper.ComputeEpisodeGroupId(seriesId, 1, 3),
            VersionGroupHelper.ComputeEpisodeGroupId(Guid.NewGuid(), 1, 3));
    }

    [Fact]
    public void Same_movie_rule_provider_id_wins_over_title_and_year()
    {
        var a = new MediaItem { Title = "Dune", Year = 2021, ImdbId = "tt1160419" };
        // Same imdb id, drifted year (one copy enriched) — still the same movie.
        Assert.True(VersionGroupHelper.AreSameMovie(a,
            new MediaItem { Title = "Dune Part One", Year = 2020, ImdbId = "TT1160419" }));
        // Same title+year but a DIFFERENT provider id — never the same movie.
        Assert.False(VersionGroupHelper.AreSameMovie(a,
            new MediaItem { Title = "Dune", Year = 2021, ImdbId = "tt0087182" }));
        // No provider ids: title+year heuristic (punctuation/case-insensitive, but
        // extra WORDS like "Copy" are still distinguishing — admin merge covers those).
        Assert.True(VersionGroupHelper.AreSameMovie(
            new MediaItem { Title = "Se7en!", Year = 1995 },
            new MediaItem { Title = "se7en", Year = 1995 }));
        Assert.False(VersionGroupHelper.AreSameMovie(
            new MediaItem { Title = "Se7en (Copy)", Year = 1995 },
            new MediaItem { Title = "se7en", Year = 1995 }));
        Assert.False(VersionGroupHelper.AreSameMovie(
            new MediaItem { Title = "Dune", Year = 2021 },
            new MediaItem { Title = "Dune", Year = 1984 }));
    }

    // ───────────────────────────── per-file movie assignment ─────────────────────────────

    [Fact]
    public async Task AssignMovieGroup_JoinsAnExistingPersistedCopy()
    {
        using var ctx = NewContext();
        var first = AddMovie(ctx, "Inception", 2010);
        var second = new MediaItem { Id = Guid.NewGuid(), LibraryId = _libraryId, Type = MediaType.Movie, Title = "Inception", Year = 2010 };

        await VersionGroupAssigner.AssignMovieGroupAsync(ctx, second);

        Assert.NotNull(second.VersionGroupId);
        Assert.Equal(second.VersionGroupId, first.VersionGroupId); // sibling was minted into the same group
    }

    [Fact]
    public async Task AssignMovieGroup_IgnoresDifferentYearAndConflictingProviderIds()
    {
        using var ctx = NewContext();
        AddMovie(ctx, "Dune", 1984);
        AddMovie(ctx, "Heat", 1995, imdbId: "tt0113277");
        var newDune = new MediaItem { Id = Guid.NewGuid(), LibraryId = _libraryId, Type = MediaType.Movie, Title = "Dune", Year = 2021 };
        var fakeHeat = new MediaItem { Id = Guid.NewGuid(), LibraryId = _libraryId, Type = MediaType.Movie, Title = "Heat", Year = 1995, ImdbId = "tt9999999" };

        await VersionGroupAssigner.AssignMovieGroupAsync(ctx, newDune);
        await VersionGroupAssigner.AssignMovieGroupAsync(ctx, fakeHeat);

        Assert.Null(newDune.VersionGroupId);  // remake: same title, different year
        Assert.Null(fakeHeat.VersionGroupId); // provider veto: same title+year, different id
    }

    [Fact]
    public async Task AssignMovieGroup_SameProviderId_GroupsAcrossYearDrift()
    {
        using var ctx = NewContext();
        var enriched = AddMovie(ctx, "Dune Part One", 2020, imdbId: "tt1160419");
        var raw = new MediaItem { Id = Guid.NewGuid(), LibraryId = _libraryId, Type = MediaType.Movie, Title = "Dune", Year = 2021, ImdbId = "tt1160419" };

        await VersionGroupAssigner.AssignMovieGroupAsync(ctx, raw);

        Assert.NotNull(raw.VersionGroupId);
        Assert.Equal(enriched.VersionGroupId, raw.VersionGroupId);
    }

    // ───────────────────────────── library-wide grouping pass ─────────────────────────────

    [Fact]
    public async Task GroupMovies_ConvergesUngroupedCopies_AndRespectsLibraryBoundaries()
    {
        var otherLibrary = Guid.NewGuid();
        using (var seed = NewContext())
        {
            seed.Libraries.Add(new Library { Id = otherLibrary, Name = "Movies B", Type = LibraryType.Movie });
            seed.SaveChanges();
            AddMovie(seed, "Tenet", 2020);
            AddMovie(seed, "Tenet", 2020);
            AddMovie(seed, "Tenet", 2020, libraryId: otherLibrary); // other library — never merged
        }

        using var ctx = NewContext();
        var changed = await VersionGroupAssigner.GroupMoviesAsync(ctx, libraryId: null);
        await ctx.SaveChangesAsync();

        Assert.Equal(2, changed);
        using var verify = NewContext();
        var groups = verify.MediaItems.Where(m => m.Title == "Tenet").AsEnumerable()
            .GroupBy(m => (m.LibraryId, m.VersionGroupId)).ToList();
        var mine = verify.MediaItems.Where(m => m.Title == "Tenet" && m.LibraryId == _libraryId).ToList();
        Assert.Equal(2, mine.Count);
        Assert.NotNull(mine[0].VersionGroupId);
        Assert.Equal(mine[0].VersionGroupId, mine[1].VersionGroupId);
        var foreign = verify.MediaItems.Single(m => m.Title == "Tenet" && m.LibraryId == otherLibrary);
        Assert.Null(foreign.VersionGroupId); // alone in its library
    }

    [Fact]
    public async Task GroupMovies_SplitsGroupsWithConflictingProviderIds()
    {
        var wrongGroup = Guid.NewGuid();
        using (var seed = NewContext())
        {
            // Merged before enrichment; providers then disambiguated them.
            AddMovie(seed, "Heat", 1995, imdbId: "tt0113277", groupId: wrongGroup);
            AddMovie(seed, "Heat", 1995, imdbId: "tt9999999", groupId: wrongGroup);
        }

        using var ctx = NewContext();
        var changed = await VersionGroupAssigner.GroupMoviesAsync(ctx, _libraryId);
        await ctx.SaveChangesAsync();

        Assert.True(changed >= 1);
        using var verify = NewContext();
        var rows = verify.MediaItems.Where(m => m.Title == "Heat").ToList();
        Assert.NotEqual(rows[0].VersionGroupId, rows[1].VersionGroupId);
    }

    [Fact]
    public async Task GroupMovies_IsFillOnly_AdminSplitSurvives()
    {
        var splitGroupA = Guid.NewGuid();
        var splitGroupB = Guid.NewGuid();
        using (var seed = NewContext())
        {
            // Admin declared these NOT duplicates (same title+year, no provider ids).
            AddMovie(seed, "Hamlet", 2000, groupId: splitGroupA);
            AddMovie(seed, "Hamlet", 2000, groupId: splitGroupB);
        }

        using var ctx = NewContext();
        var changed = await VersionGroupAssigner.GroupMoviesAsync(ctx, _libraryId);

        Assert.Equal(0, changed);
        using var verify = NewContext();
        var rows = verify.MediaItems.Where(m => m.Title == "Hamlet").OrderBy(m => m.VersionGroupId).ToList();
        Assert.Equal(
            new[] { splitGroupA, splitGroupB }.OrderBy(g => g),
            rows.Select(r => r.VersionGroupId!.Value));
    }

    // ───────────────────────────── episode backfill + boot service ─────────────────────────────

    [Fact]
    public async Task BackfillService_GroupsLegacyRows_AndIsIdempotent()
    {
        var seriesId = Guid.NewGuid();
        using (var seed = NewContext())
        {
            seed.Libraries.Add(new Library { Id = Guid.NewGuid(), Name = "TV", Type = LibraryType.TV });
            seed.MediaItems.Add(new MediaItem { Id = seriesId, LibraryId = _libraryId, Type = MediaType.Series, Title = "Show" });
            MediaItem Ep(int? episode) => new()
            {
                Id = Guid.NewGuid(), LibraryId = _libraryId, Type = MediaType.Episode,
                Title = "Ep", SeriesId = seriesId, SeasonNumber = 1, EpisodeNumber = episode,
            };
            seed.MediaItems.AddRange(Ep(3), Ep(3), Ep(4), Ep(0), Ep(null));
            AddMovie(seed, "Inception", 2010);
            AddMovie(seed, "Inception", 2010);
        }

        var scopeFactory = BuildScopeFactory();
        var service = new VersionGroupBackfillService(scopeFactory, NullLogger<VersionGroupBackfillService>.Instance);

        var (episodes, movies, _) = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(3, episodes); // both E3 copies + E4; unparseable rows untouched
        Assert.Equal(2, movies);

        using (var verify = NewContext())
        {
            var e3 = verify.MediaItems.Where(m => m.EpisodeNumber == 3).ToList();
            Assert.Equal(2, e3.Count);
            Assert.Equal(VersionGroupHelper.ComputeEpisodeGroupId(seriesId, 1, 3), e3[0].VersionGroupId);
            Assert.Equal(e3[0].VersionGroupId, e3[1].VersionGroupId);
            Assert.All(verify.MediaItems.Where(m => m.EpisodeNumber == 0 || (m.Type == MediaType.Episode && m.EpisodeNumber == null)).ToList(),
                m => Assert.Null(m.VersionGroupId));
        }

        // Second run: converged — nothing to do.
        var second = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal((0, 0, 0), second);
    }

    private IServiceScopeFactory BuildScopeFactory()
    {
        var scopeFactory = new Mock<IServiceScopeFactory>();
        var scope = new Mock<IServiceScope>();
        var provider = new Mock<IServiceProvider>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        provider.Setup(p => p.GetService(typeof(AppDbContext))).Returns(() => new AppDbContext(_options));
        return scopeFactory.Object;
    }
}
