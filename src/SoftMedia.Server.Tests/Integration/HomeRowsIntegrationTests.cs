using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// R-WI-020 — personalized home rows. Pins: history-seeded rows with genre
/// affinity, watched/seed exclusion, ACL + rating filtering at the query, and
/// the no-history empty response (client self-suppresses).
public class HomeRowsIntegrationTests : IntegrationTestBase
{
    private HttpClient ClientFor(User user)
    {
        var client = Factory.CreateClient();
        using var scope = Factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenService.GenerateAccessToken(user));
        return client;
    }

    /// Seeds an Action-heavy catalog: the user's history holds "Seed Movie"
    /// (Action); five unwatched Action movies (one R-rated), one already-watched
    /// Action movie, and one Drama movie that shares no genres with the history.
    private async Task<(User user, Guid seedId, Guid watchedId, Guid rRatedId, Guid dramaId)> SeedAsync()
    {
        var user = await Factory.SeedUserAsync($"rows-{Guid.NewGuid():N}"[..20]);
        Guid seedId = Guid.Empty, watchedId = Guid.Empty, rRatedId = Guid.Empty, dramaId = Guid.Empty;

        await Factory.WithDbAsync(async db =>
        {
            var lib = new Library { Id = Guid.NewGuid(), Name = $"Rows-{Guid.NewGuid():N}"[..12], Type = LibraryType.Movie, Paths = new() { "/rw" } };
            db.Libraries.Add(lib);
            var action = new Genre { Name = $"RowsAction{Guid.NewGuid():N}"[..16] };
            var drama = new Genre { Name = $"RowsDrama{Guid.NewGuid():N}"[..16] };
            db.Genres.AddRange(action, drama);
            await db.SaveChangesAsync();

            MediaItem Movie(string title, string? rating = null, double communityRating = 7) => new()
            {
                Id = Guid.NewGuid(),
                LibraryId = lib.Id,
                Title = title,
                SortTitle = title,
                Path = $"/rw/{title}.mkv",
                Type = MediaType.Movie,
                ContentRating = rating,
                CommunityRating = communityRating,
            };

            // Candidates carry explicit PG ratings: null-rated items are fail-safe
            // HIDDEN whenever a ceiling is set (consistent with all browse paths),
            // which would empty the rows in the ceiling test below.
            var seed = Movie("Seed Movie", rating: "PG");
            var watched = Movie("Watched Action", rating: "PG");
            var rRated = Movie("Blocked Action", rating: "R", communityRating: 9);
            var dramaMovie = Movie("Unrelated Drama", rating: "PG");
            var candidates = Enumerable.Range(1, 5).Select(i => Movie($"Action Pick {i}", rating: "PG", communityRating: 8 - i * 0.1)).ToList();

            db.MediaItems.Add(seed);
            db.MediaItems.Add(watched);
            db.MediaItems.Add(rRated);
            db.MediaItems.Add(dramaMovie);
            db.MediaItems.AddRange(candidates);
            await db.SaveChangesAsync();

            foreach (var m in candidates.Concat(new[] { seed, watched, rRated }))
                db.MediaItemGenres.Add(new MediaItemGenre { MediaItemId = m.Id, GenreId = action.Id });
            db.MediaItemGenres.Add(new MediaItemGenre { MediaItemId = dramaMovie.Id, GenreId = drama.Id });

            db.PlaybackHistory.Add(new PlaybackHistory
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                MediaItemId = seed.Id,
                MediaType = MediaType.Movie,
                StartedAt = DateTime.UtcNow.AddHours(-2),
                LastBeatAt = DateTime.UtcNow.AddHours(-1),
                MaxPosition = 5000,
                Completed = true,
            });
            db.UserMediaInteractions.Add(new UserMediaInteraction
            {
                UserId = user.Id,
                MediaItemId = watched.Id,
                IsWatched = true,
            });
            await db.SaveChangesAsync();

            seedId = seed.Id; watchedId = watched.Id; rRatedId = rRated.Id; dramaId = dramaMovie.Id;
        });
        return (user, seedId, watchedId, rRatedId, dramaId);
    }

    private static (List<string> titles, List<string> itemTitles) Parse(JsonDocument doc)
    {
        var rowTitles = doc.RootElement.EnumerateArray()
            .Select(r => r.GetProperty("title").GetString()!)
            .ToList();
        var itemTitles = doc.RootElement.EnumerateArray()
            .SelectMany(r => r.GetProperty("items").EnumerateArray())
            .Select(i => i.GetProperty("title").GetString()!)
            .ToList();
        return (rowTitles, itemTitles);
    }

    /// <summary>
    /// Rows derived from the caller's play history. Their contract — no seeds, no
    /// finished items, and never the same item twice — belongs to THEM, not to the
    /// response as a whole. The catalog rows below deliberately break all three:
    /// "Never Played" must list an item regardless of whether a taste row already
    /// showed it, or the row would be lying about its own criterion.
    /// </summary>
    private static readonly string[] TasteKinds = { "most-watched", "top-picks", "genre" };

    private static readonly string[] CatalogKinds = { "genre-spotlight", "never-played" };

    private static List<string> ItemTitlesOfKinds(JsonDocument doc, params string[] kinds) =>
        doc.RootElement.EnumerateArray()
            .Where(r => kinds.Contains(r.GetProperty("kind").GetString()))
            .SelectMany(r => r.GetProperty("items").EnumerateArray())
            .Select(i => i.GetProperty("title").GetString()!)
            .ToList();

    private static List<string> RowKinds(JsonDocument doc) =>
        doc.RootElement.EnumerateArray()
            .Select(r => r.GetProperty("kind").GetString()!)
            .ToList();

    [Fact]
    public async Task UserWithHistory_GetsGenreAffinityRows_ExcludingSeedsAndWatched()
    {
        var (user, _, _, _, _) = await SeedAsync();

        var json = await ClientFor(user).GetStringAsync("/api/v1/media/home-rows");
        using var doc = JsonDocument.Parse(json);
        var (rowTitles, _) = Parse(doc);
        // Scoped to the taste rows: catalog rows are catalog-wide by design and would
        // legitimately surface the seed and the watched item.
        var tasteItems = ItemTitlesOfKinds(doc, TasteKinds);

        Assert.NotEmpty(rowTitles);
        Assert.Contains(rowTitles, t => t == "Top picks for you");
        Assert.Contains("Action Pick 1", tasteItems);
        Assert.DoesNotContain("Seed Movie", tasteItems);      // the seed itself never recommends itself
        Assert.DoesNotContain("Watched Action", tasteItems);  // finished items excluded
        Assert.DoesNotContain("Unrelated Drama", tasteItems); // no shared genres → not recommended

        // The seed-named row was retired in favour of the scope-toggled Most
        // Watched row; nothing should title itself after a history entry again.
        Assert.DoesNotContain(rowTitles, t => t.StartsWith("Because you watched"));

        // A single played title is far below MinRowItems — Most Watched must
        // self-suppress rather than render a one-card row.
        Assert.DoesNotContain(rowTitles, t => t == "Most Watched");
    }

    [Fact]
    public async Task MostWatched_RanksByPlays_RollsEpisodesToSeries_AndHonorsCeiling()
    {
        var user = await Factory.SeedUserAsync($"mw-{Guid.NewGuid():N}"[..20]);
        await Factory.WithDbAsync(async db =>
        {
            var movieLib = new Library { Id = Guid.NewGuid(), Name = $"MWm-{Guid.NewGuid():N}"[..12], Type = LibraryType.Movie, Paths = new() { "/mwm" } };
            var tvLib = new Library { Id = Guid.NewGuid(), Name = $"MWt-{Guid.NewGuid():N}"[..12], Type = LibraryType.TV, Paths = new() { "/mwt" } };
            db.Libraries.AddRange(movieLib, tvLib);

            MediaItem Item(string title, MediaType type, Library lib, string rating, Guid? seriesId = null) => new()
            {
                Id = Guid.NewGuid(),
                LibraryId = lib.Id,
                Title = title,
                SortTitle = title,
                Path = $"/{lib.Name}/{title}",
                Type = type,
                ContentRating = rating,
                SeriesId = seriesId,
            };

            var blockedR = Item("MW Blocked R", MediaType.Movie, movieLib, "R");   // 5 plays — most played overall
            var movieA = Item("MW Movie A", MediaType.Movie, movieLib, "PG");      // 4 plays
            var series = Item("MW Series", MediaType.Series, tvLib, "PG");         // 3 plays via episodes
            var movieB = Item("MW Movie B", MediaType.Movie, movieLib, "PG");      // 2 plays
            var movieC = Item("MW Movie C", MediaType.Movie, movieLib, "PG");      // 1 play
            db.MediaItems.AddRange(blockedR, movieA, series, movieB, movieC);
            await db.SaveChangesAsync();

            var ep1 = Item("MW S01E01", MediaType.Episode, tvLib, "PG", series.Id);
            var ep2 = Item("MW S01E02", MediaType.Episode, tvLib, "PG", series.Id);
            db.MediaItems.AddRange(ep1, ep2);
            await db.SaveChangesAsync();

            void Plays(MediaItem m, MediaType type, int count)
            {
                for (var i = 0; i < count; i++)
                {
                    db.PlaybackHistory.Add(new PlaybackHistory
                    {
                        Id = Guid.NewGuid(),
                        UserId = user.Id,
                        MediaItemId = m.Id,
                        MediaType = type,
                        StartedAt = DateTime.UtcNow.AddDays(-i - 1),
                        LastBeatAt = DateTime.UtcNow.AddDays(-i - 1).AddMinutes(30),
                        MaxPosition = 1000,
                        Completed = true,
                    });
                }
            }
            Plays(blockedR, MediaType.Movie, 5);
            Plays(movieA, MediaType.Movie, 4);
            Plays(ep1, MediaType.Episode, 2); // series total = 3
            Plays(ep2, MediaType.Episode, 1);
            Plays(movieB, MediaType.Movie, 2);
            Plays(movieC, MediaType.Movie, 1);
            await db.SaveChangesAsync();
        });

        // Unrestricted: strict play-count order, episodes rolled up to ONE series card.
        // Pinned to scope=me so this stays a test of single-user play ranking; the
        // cross-user aggregate has its own test below.
        var json = await ClientFor(user).GetStringAsync("/api/v1/media/home-rows?scope=me");
        using (var doc = JsonDocument.Parse(json))
        {
            var row = doc.RootElement.EnumerateArray()
                .FirstOrDefault(r => r.GetProperty("title").GetString() == "Your Most Watched");
            Assert.NotEqual(JsonValueKind.Undefined, row.ValueKind);
            var titles = row.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("title").GetString()!)
                .ToList();
            Assert.Equal(new[] { "MW Blocked R", "MW Movie A", "MW Series", "MW Movie B", "MW Movie C" }, titles);
            Assert.DoesNotContain("MW S01E01", titles); // episode cards never appear raw
        }

        // Ceiling: the most-played title is R-rated — a PG-capped caller must not
        // see it, and the row survives on the remaining four visible titles.
        await Factory.WithDbAsync(async db =>
        {
            (await db.Users.FindAsync(user.Id))!.MaxRating = "PG";
            await db.SaveChangesAsync();
        });
        user.MaxRating = "PG"; // the ceiling rides the JWT claim
        var jsonCapped = await ClientFor(user).GetStringAsync("/api/v1/media/home-rows?scope=me");
        using (var doc = JsonDocument.Parse(jsonCapped))
        {
            var row = doc.RootElement.EnumerateArray()
                .FirstOrDefault(r => r.GetProperty("title").GetString() == "Your Most Watched");
            Assert.NotEqual(JsonValueKind.Undefined, row.ValueKind);
            var titles = row.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("title").GetString()!)
                .ToList();
            Assert.Equal(new[] { "MW Movie A", "MW Series", "MW Movie B", "MW Movie C" }, titles);
        }
    }

    [Fact]
    public async Task RatingCeiling_FiltersRowCandidates()
    {
        var (user, _, _, _, _) = await SeedAsync();
        await Factory.WithDbAsync(async db =>
        {
            (await db.Users.FindAsync(user.Id))!.MaxRating = "PG-13";
            await db.SaveChangesAsync();
        });
        user.MaxRating = "PG-13";

        var json = await ClientFor(user).GetStringAsync("/api/v1/media/home-rows");
        using var doc = JsonDocument.Parse(json);
        var (_, itemTitles) = Parse(doc);

        Assert.NotEmpty(itemTitles);                        // rows still render
        Assert.DoesNotContain("Blocked Action", itemTitles); // the R-rated candidate is gone
    }

    /// <summary>
    /// A user with no history gets no TASTE rows — there is no signal to build them
    /// from. They do still get catalog rows, which is the point of adding them: a fresh
    /// account used to land on a home page with nothing between the hero and Recently
    /// Added.
    /// </summary>
    [Fact]
    public async Task NewUser_WithNoHistory_GetsCatalogRowsButNoTasteRows()
    {
        await SeedAsync(); // catalog exists, but this fresh user never played anything
        var fresh = await Factory.SeedUserAsync($"rows-new-{Guid.NewGuid():N}"[..20]);

        var json = await ClientFor(fresh).GetStringAsync("/api/v1/media/home-rows");
        using var doc = JsonDocument.Parse(json);
        var kinds = RowKinds(doc);

        Assert.DoesNotContain(kinds, k => TasteKinds.Contains(k));
        Assert.Contains(kinds, k => CatalogKinds.Contains(k));

        // Every catalog row must carry a filter, or its "See more" link cannot exist.
        foreach (var row in doc.RootElement.EnumerateArray()
                     .Where(r => CatalogKinds.Contains(r.GetProperty("kind").GetString())))
        {
            Assert.True(row.TryGetProperty("filter", out var filter)
                        && filter.ValueKind != JsonValueKind.Null,
                $"catalog row '{row.GetProperty("title").GetString()}' has no filter");
        }
    }

    /// <summary>
    /// "Top picks for you" is ranked against a rolling history window and a mutable
    /// cross-row dedup set, so no fixed filter reproduces it. It must therefore NOT
    /// advertise a "See more" link that would land on a different set of items.
    /// </summary>
    [Fact]
    public async Task TopPicksCarriesNoFilterBecauseItIsNotReproducibleFromAUrl()
    {
        var (user, _, _, _, _) = await SeedAsync();

        var json = await ClientFor(user).GetStringAsync("/api/v1/media/home-rows");
        using var doc = JsonDocument.Parse(json);

        var topPicks = doc.RootElement.EnumerateArray()
            .FirstOrDefault(r => r.GetProperty("kind").GetString() == "top-picks");
        Assert.NotEqual(JsonValueKind.Undefined, topPicks.ValueKind);

        var hasFilter = topPicks.TryGetProperty("filter", out var filter)
                        && filter.ValueKind != JsonValueKind.Null;
        Assert.False(hasFilter, "Top picks must not claim to be reproducible as a browse filter");
    }

    [Fact]
    public async Task MusicHistory_DoesNotCrowdOutOrSteerVideoRows()
    {
        // Review MED: a heavy music listener's history window filled with tracks —
        // emptying the rows (tracks without genres) or steering "Top picks" with
        // music genres while candidates are movies/series only.
        var (user, _, _, _, _) = await SeedAsync();
        await Factory.WithDbAsync(async db =>
        {
            var musicLib = new Library { Id = Guid.NewGuid(), Name = $"RowsMusic{Guid.NewGuid():N}"[..14], Type = LibraryType.Music, Paths = new() { "/rm" } };
            db.Libraries.Add(musicLib);
            for (var i = 0; i < 70; i++)
            {
                var track = new MediaItem
                {
                    Id = Guid.NewGuid(),
                    LibraryId = musicLib.Id,
                    Title = $"Track {i}",
                    SortTitle = $"Track {i}",
                    Path = $"/rm/t{i}.flac",
                    Type = MediaType.Audio,
                };
                db.MediaItems.Add(track);
                db.PlaybackHistory.Add(new PlaybackHistory
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    MediaItemId = track.Id,
                    MediaType = MediaType.Audio,
                    StartedAt = DateTime.UtcNow.AddMinutes(-i - 1),
                    LastBeatAt = DateTime.UtcNow.AddMinutes(-i), // all newer than the movie seed
                    MaxPosition = 200,
                    Completed = true,
                });
            }
            await db.SaveChangesAsync();
        });

        var json = await ClientFor(user).GetStringAsync("/api/v1/media/home-rows");
        using var doc = JsonDocument.Parse(json);
        var (rowTitles, itemTitles) = Parse(doc);

        Assert.NotEmpty(rowTitles); // 70 fresher track plays must not evict the movie signal
        Assert.Contains(rowTitles, t => t == "Top picks for you");
        Assert.DoesNotContain(rowTitles, t => t.Contains("Track "));
        Assert.Contains("Action Pick 1", itemTitles);
    }

    /// <summary>
    /// The Everyone scope aggregates every user's plays, ranking breadth of
    /// audience above raw replay count, and must NOT become a way to see titles
    /// the caller's own library ACL hides. Both halves are asserted here because
    /// the second is the security-relevant one: cross-user counts are the only
    /// place a title another user played can reach this caller's home page.
    /// </summary>
    [Fact]
    public async Task MostWatched_EveryoneScope_RanksByAudienceBreadth_AndStillHonorsCallerAcl()
    {
        var alice = await Factory.SeedUserAsync($"mwe-a-{Guid.NewGuid():N}"[..20]);
        var bob = await Factory.SeedUserAsync($"mwe-b-{Guid.NewGuid():N}"[..20]);
        var carol = await Factory.SeedUserAsync($"mwe-c-{Guid.NewGuid():N}"[..20]);

        await Factory.WithDbAsync(async db =>
        {
            var sharedLib = new Library { Id = Guid.NewGuid(), Name = $"MWEs-{Guid.NewGuid():N}"[..12], Type = LibraryType.Movie, Paths = new() { "/mwes" } };
            var privateLib = new Library { Id = Guid.NewGuid(), Name = $"MWEp-{Guid.NewGuid():N}"[..12], Type = LibraryType.Movie, Paths = new() { "/mwep" } };
            db.Libraries.AddRange(sharedLib, privateLib);

            MediaItem Movie(string title, Library lib) => new()
            {
                Id = Guid.NewGuid(),
                LibraryId = lib.Id,
                Title = title,
                SortTitle = title,
                Path = $"/{lib.Name}/{title}",
                Type = MediaType.Movie,
                ContentRating = "PG",
            };

            // Crowd pleaser: 1 play each from three users  → 3 viewers, 3 plays.
            // Obsession:    9 plays from Bob alone         → 1 viewer,  9 plays.
            // Breadth wins, so Crowd Pleaser must outrank Obsession.
            var crowdPleaser = Movie("MWE Crowd Pleaser", sharedLib);
            var obsession = Movie("MWE Obsession", sharedLib);
            var filler1 = Movie("MWE Filler 1", sharedLib);
            var filler2 = Movie("MWE Filler 2", sharedLib);
            // Lives in a library Alice cannot see; only Bob ever plays it.
            var hidden = Movie("MWE Hidden", privateLib);
            db.MediaItems.AddRange(crowdPleaser, obsession, filler1, filler2, hidden);

            // Alice is restricted to the shared library; Bob and Carol stay
            // unrestricted (no UserLibraryAccess rows = full access).
            db.UserLibraryAccess.Add(new UserLibraryAccess { UserId = alice.Id, LibraryId = sharedLib.Id });
            await db.SaveChangesAsync();

            void Play(MediaItem m, User who, int count)
            {
                for (var i = 0; i < count; i++)
                {
                    db.PlaybackHistory.Add(new PlaybackHistory
                    {
                        Id = Guid.NewGuid(),
                        UserId = who.Id,
                        MediaItemId = m.Id,
                        MediaType = MediaType.Movie,
                        StartedAt = DateTime.UtcNow.AddDays(-i - 1),
                        LastBeatAt = DateTime.UtcNow.AddDays(-i - 1).AddMinutes(30),
                        MaxPosition = 1000,
                        Completed = true,
                    });
                }
            }
            Play(crowdPleaser, alice, 1);
            Play(crowdPleaser, bob, 1);
            Play(crowdPleaser, carol, 1);
            Play(obsession, bob, 9);
            Play(filler1, bob, 2);
            Play(filler2, carol, 1);
            Play(hidden, bob, 20); // would top the chart if the ACL were skipped
            await db.SaveChangesAsync();
        });

        var json = await ClientFor(alice).GetStringAsync("/api/v1/media/home-rows?scope=everyone");
        using var doc = JsonDocument.Parse(json);
        var row = doc.RootElement.EnumerateArray()
            .FirstOrDefault(r => r.GetProperty("kind").GetString() == "most-watched");
        Assert.NotEqual(JsonValueKind.Undefined, row.ValueKind);
        Assert.Equal("Most Watched", row.GetProperty("title").GetString());

        var titles = row.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("title").GetString()!)
            .ToList();

        // Alice never played Obsession or Filler 1 — they reach her only via other
        // users' plays, which is exactly what the Everyone scope is for.
        Assert.Equal(
            new[] { "MWE Crowd Pleaser", "MWE Obsession", "MWE Filler 1", "MWE Filler 2" },
            titles);

        // The 20-play title in a library Alice lacks access to must never surface,
        // no matter how heavily someone else played it.
        Assert.DoesNotContain("MWE Hidden", titles);

        // And the personal scope shows only what Alice herself played — one title,
        // which is below MinRowItems, so the row self-suppresses entirely.
        var personalJson = await ClientFor(alice).GetStringAsync("/api/v1/media/home-rows?scope=me");
        using var personalDoc = JsonDocument.Parse(personalJson);
        Assert.DoesNotContain(
            personalDoc.RootElement.EnumerateArray(),
            r => r.GetProperty("kind").GetString() == "most-watched");
    }

    /// <summary>
    /// The no-repeat rule is a property of the TASTE rows, which share one `used` set:
    /// showing the same recommendation twice under two headings looks broken.
    ///
    /// It is explicitly NOT a property of the response as a whole. A catalog row
    /// answers a different question — "what is in this genre", "what have I never
    /// played" — and filtering out items a taste row happened to show would make it
    /// misreport its own criterion and thin it arbitrarily.
    /// </summary>
    [Fact]
    public async Task TasteRowsNeverRepeatAnItemAmongThemselves()
    {
        var (user, _, _, _, _) = await SeedAsync();

        var json = await ClientFor(user).GetStringAsync("/api/v1/media/home-rows");
        using var doc = JsonDocument.Parse(json);
        var tasteItems = ItemTitlesOfKinds(doc, TasteKinds);

        Assert.Equal(tasteItems.Distinct().Count(), tasteItems.Count);
    }

    /// <summary>
    /// The genre spotlight is movies and TV only — genre means something different for
    /// music and books, and those tags dominate by volume, so a mixed ranking never
    /// surfaced a film genre. Critically, the row's Filter must carry that restriction:
    /// a "See more" that opened a grid of albums would contradict the row above it.
    /// </summary>
    [Fact]
    public async Task GenreSpotlightIsVideoOnlyAndItsFilterSaysSo()
    {
        var user = await Factory.SeedUserAsync($"gs-{Guid.NewGuid():N}"[..20]);
        await Factory.WithDbAsync(async db =>
        {
            var movieLib = new Library { Id = Guid.NewGuid(), Name = $"GsM-{Guid.NewGuid():N}"[..12], Type = LibraryType.Movie, Paths = new() { "/gsm" } };
            var musicLib = new Library { Id = Guid.NewGuid(), Name = $"GsA-{Guid.NewGuid():N}"[..12], Type = LibraryType.Music, Paths = new() { "/gsa" } };
            db.Libraries.AddRange(movieLib, musicLib);

            var filmGenre = new Genre { Name = $"GsComedy{Guid.NewGuid():N}"[..14] };
            // Deliberately far more numerous — under a mixed ranking this would win and
            // the film genre would never be shown.
            var musicGenre = new Genre { Name = $"GsMetal{Guid.NewGuid():N}"[..13] };
            db.Genres.AddRange(filmGenre, musicGenre);
            await db.SaveChangesAsync();

            MediaItem Item(string title, Library lib, MediaType type) => new()
            {
                Id = Guid.NewGuid(),
                LibraryId = lib.Id,
                Title = title,
                SortTitle = title,
                Path = $"/{lib.Name}/{title}",
                Type = type,
            };

            for (var i = 0; i < 5; i++)
            {
                var movie = Item($"Gs Movie {i}", movieLib, MediaType.Movie);
                db.MediaItems.Add(movie);
                db.MediaItemGenres.Add(new MediaItemGenre { MediaItemId = movie.Id, GenreId = filmGenre.Id });
            }
            for (var i = 0; i < 20; i++)
            {
                var album = Item($"Gs Album {i}", musicLib, MediaType.Album);
                db.MediaItems.Add(album);
                db.MediaItemGenres.Add(new MediaItemGenre { MediaItemId = album.Id, GenreId = musicGenre.Id });
            }
            await db.SaveChangesAsync();
        });

        var json = await ClientFor(user).GetStringAsync("/api/v1/media/home-rows");
        using var doc = JsonDocument.Parse(json);

        var row = doc.RootElement.EnumerateArray()
            .FirstOrDefault(r => r.GetProperty("kind").GetString() == "genre-spotlight");
        Assert.NotEqual(JsonValueKind.Undefined, row.ValueKind);

        // The 20 albums must NOT have won the ranking.
        Assert.StartsWith("GsComedy", row.GetProperty("title").GetString());

        var titles = row.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("title").GetString()!)
            .ToList();
        Assert.All(titles, t => Assert.StartsWith("Gs Movie", t));

        // And the link must reproduce the row, not widen it back out to everything.
        var types = row.GetProperty("filter").GetProperty("types").EnumerateArray()
            .Select(t => t.GetString()!)
            .ToList();
        Assert.Equal(new[] { "Movie", "Series" }, types);
    }

    /// <summary>
    /// The spotlight rotates daily rather than showing one frozen genre forever.
    ///
    /// GenreSpotlightRotationTests covers the calendar arithmetic; this pins the wiring
    /// — that the SERVICE actually defers to the rotation rather than picking the
    /// highest-count genre, which is what it used to do. Seeds two eligible genres with
    /// DIFFERENT counts so a regression to "always the biggest" is visible on any day
    /// the rotation selects the smaller one.
    ///
    /// Tolerates a UTC midnight crossing between the request and the assertion by
    /// accepting either day's pick, so it cannot flake.
    /// </summary>
    [Fact]
    public async Task GenreSpotlightRotatesDailyRatherThanPinningTheBiggestGenre()
    {
        var user = await Factory.SeedUserAsync($"rot-{Guid.NewGuid():N}"[..20]);
        var bigGenre = $"RotBig{Guid.NewGuid():N}"[..12];
        var smallGenre = $"RotSml{Guid.NewGuid():N}"[..12];

        await Factory.WithDbAsync(async db =>
        {
            var lib = new Library { Id = Guid.NewGuid(), Name = $"Rot-{Guid.NewGuid():N}"[..12], Type = LibraryType.Movie, Paths = new() { "/rot" } };
            db.Libraries.Add(lib);
            var big = new Genre { Name = bigGenre };
            var small = new Genre { Name = smallGenre };
            db.Genres.AddRange(big, small);
            await db.SaveChangesAsync();

            // Both clear MinRowItems (4), so both are eligible for the rotation pool.
            void Seed(Genre genre, int count, string prefix)
            {
                for (var i = 0; i < count; i++)
                {
                    var movie = new MediaItem
                    {
                        Id = Guid.NewGuid(),
                        LibraryId = lib.Id,
                        Title = $"{prefix} {i}",
                        SortTitle = $"{prefix} {i}",
                        Path = $"/rot/{prefix}{i}.mkv",
                        Type = MediaType.Movie,
                    };
                    db.MediaItems.Add(movie);
                    db.MediaItemGenres.Add(new MediaItemGenre { MediaItemId = movie.Id, GenreId = genre.Id });
                }
            }
            Seed(big, 8, "Rot Big");
            Seed(small, 5, "Rot Small");
            await db.SaveChangesAsync();
        });

        var json = await ClientFor(user).GetStringAsync("/api/v1/media/home-rows");
        using var doc = JsonDocument.Parse(json);

        var row = doc.RootElement.EnumerateArray()
            .FirstOrDefault(r => r.GetProperty("kind").GetString() == "genre-spotlight");
        Assert.NotEqual(JsonValueKind.Undefined, row.ValueKind);
        var actual = row.GetProperty("title").GetString();

        // The pool as the service orders it: count desc, then name.
        var pool = new[] { bigGenre, smallGenre };
        var now = DateTimeOffset.UtcNow;
        var expectedToday = GenreSpotlightRotation.Pick(pool, now);
        var expectedTomorrow = GenreSpotlightRotation.Pick(pool, now.AddDays(1));

        Assert.True(actual == expectedToday || actual == expectedTomorrow,
            $"expected the rotation's pick ('{expectedToday}', or '{expectedTomorrow}' across a "
            + $"midnight crossing) but the row showed '{actual}'");

        // And whichever it picked, the row must be filled from that genre.
        var itemPrefix = actual == bigGenre ? "Rot Big" : "Rot Small";
        var titles = row.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("title").GetString()!)
            .ToList();
        Assert.All(titles, t => Assert.StartsWith(itemPrefix, t));
    }

    /// <summary>
    /// Both catalog rows are watch-next shelves: movies and TV only, never books or
    /// albums. On a music- and book-heavy library the non-video items otherwise swamp
    /// them — a wall of unplayed books with the watchable titles pushed off the end.
    ///
    /// Seeds far MORE books than videos, and leaves everything unplayed, so a
    /// regression to "all browsable types" is unmissable: the books are newer, and both
    /// rows sort by DateAdded descending, so they would take every slot.
    /// </summary>
    [Fact]
    public async Task CatalogRowsShowOnlyMoviesAndTvEvenWhenBooksDominate()
    {
        var user = await Factory.SeedUserAsync($"cat-{Guid.NewGuid():N}"[..20]);
        var sharedGenre = $"CatGenre{Guid.NewGuid():N}"[..14];

        await Factory.WithDbAsync(async db =>
        {
            var movieLib = new Library { Id = Guid.NewGuid(), Name = $"CatM-{Guid.NewGuid():N}"[..12], Type = LibraryType.Movie, Paths = new() { "/catm" } };
            var bookLib = new Library { Id = Guid.NewGuid(), Name = $"CatB-{Guid.NewGuid():N}"[..12], Type = LibraryType.Book, Paths = new() { "/catb" } };
            db.Libraries.AddRange(movieLib, bookLib);
            var genre = new Genre { Name = sharedGenre };
            db.Genres.Add(genre);
            await db.SaveChangesAsync();

            var added = DateTime.UtcNow.AddDays(-30);

            // 5 movies, added FIRST (so they are the older, lower-priority items).
            for (var i = 0; i < 5; i++)
            {
                var movie = new MediaItem
                {
                    Id = Guid.NewGuid(),
                    LibraryId = movieLib.Id,
                    Title = $"Cat Movie {i}",
                    SortTitle = $"Cat Movie {i}",
                    Path = $"/catm/{i}.mkv",
                    Type = MediaType.Movie,
                    DateAdded = added.AddMinutes(i),
                };
                db.MediaItems.Add(movie);
                db.MediaItemGenres.Add(new MediaItemGenre { MediaItemId = movie.Id, GenreId = genre.Id });
            }

            // 20 books sharing the SAME genre and added LATER — they would win both the
            // genre ranking and the newest-first ordering if the type filter regressed.
            for (var i = 0; i < 20; i++)
            {
                var book = new MediaItem
                {
                    Id = Guid.NewGuid(),
                    LibraryId = bookLib.Id,
                    Title = $"Cat Book {i}",
                    SortTitle = $"Cat Book {i}",
                    Path = $"/catb/{i}.epub",
                    Type = MediaType.Book,
                    DateAdded = added.AddDays(10).AddMinutes(i),
                };
                db.MediaItems.Add(book);
                db.MediaItemGenres.Add(new MediaItemGenre { MediaItemId = book.Id, GenreId = genre.Id });
            }
            await db.SaveChangesAsync();
        });

        var json = await ClientFor(user).GetStringAsync("/api/v1/media/home-rows");
        using var doc = JsonDocument.Parse(json);

        var catalogItems = ItemTitlesOfKinds(doc, CatalogKinds);
        Assert.NotEmpty(catalogItems);
        Assert.DoesNotContain(catalogItems, t => t.StartsWith("Cat Book"));
        Assert.Contains(catalogItems, t => t.StartsWith("Cat Movie"));

        // Every catalog row must also DECLARE the restriction, or its "See more" would
        // open a grid full of the books the row excluded.
        foreach (var row in doc.RootElement.EnumerateArray()
                     .Where(r => CatalogKinds.Contains(r.GetProperty("kind").GetString())))
        {
            var types = row.GetProperty("filter").GetProperty("types").EnumerateArray()
                .Select(t => t.GetString()!)
                .ToList();
            Assert.Equal(new[] { "Movie", "Series" }, types);
        }
    }
}
