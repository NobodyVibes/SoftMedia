using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// Cross-library browse — the endpoint behind every home row's "See more".
/// Pins the filters, the paging envelope, and (most importantly) that the library ACL
/// and rating ceiling still apply when there is no library id in the route to gate on.
public class BrowseIntegrationTests : IntegrationTestBase
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

    private static (List<string> titles, int total) Parse(JsonDocument doc)
    {
        var titles = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("title").GetString()!)
            .ToList();
        return (titles, doc.RootElement.GetProperty("totalCount").GetInt32());
    }

    /// Seeds two libraries. `open` is visible to everyone; `walled` is restricted.
    private async Task<(User unrestricted, User limited, Guid openLibId)> SeedAsync()
    {
        var unrestricted = await Factory.SeedUserAsync($"br-u-{Guid.NewGuid():N}"[..20]);
        var limited = await Factory.SeedUserAsync($"br-l-{Guid.NewGuid():N}"[..20]);
        Guid openLibId = Guid.Empty;

        await Factory.WithDbAsync(async db =>
        {
            var open = new Library { Id = Guid.NewGuid(), Name = $"BrO-{Guid.NewGuid():N}"[..12], Type = LibraryType.Movie, Paths = new() { "/bro" } };
            var walled = new Library { Id = Guid.NewGuid(), Name = $"BrW-{Guid.NewGuid():N}"[..12], Type = LibraryType.Movie, Paths = new() { "/brw" } };
            db.Libraries.AddRange(open, walled);
            openLibId = open.Id;

            var comedy = new Genre { Name = $"BrComedy{Guid.NewGuid():N}"[..14] };
            var drama = new Genre { Name = $"BrDrama{Guid.NewGuid():N}"[..13] };
            db.Genres.AddRange(comedy, drama);
            await db.SaveChangesAsync();

            MediaItem Item(string title, Library lib, MediaType type, int? year, string? rating = null) => new()
            {
                Id = Guid.NewGuid(),
                LibraryId = lib.Id,
                Title = title,
                SortTitle = title,
                Path = $"/{lib.Name}/{title}",
                Type = type,
                Year = year,
                ContentRating = rating,
            };

            var m1990 = Item("Br Nineties Movie", open, MediaType.Movie, 1995);
            var m2000 = Item("Br Noughties Movie", open, MediaType.Movie, 2003);
            var book = Item("Br Book", open, MediaType.Book, 1998);
            var album = Item("Br Album", open, MediaType.Album, 1992);
            var rRated = Item("Br R Rated", open, MediaType.Movie, 1997, "R");
            var hidden = Item("Br Walled Movie", walled, MediaType.Movie, 1996);
            // Child types must never appear as browse cards.
            var series = Item("Br Series", open, MediaType.Series, 1994);
            db.MediaItems.AddRange(m1990, m2000, book, album, rRated, hidden, series);
            await db.SaveChangesAsync();

            var episode = Item("Br S01E01", open, MediaType.Episode, 1994);
            episode.SeriesId = series.Id;
            var track = Item("Br Track 1", open, MediaType.Audio, 1992);
            track.AlbumId = album.Id;
            db.MediaItems.AddRange(episode, track);

            foreach (var m in new[] { m1990, m2000, book, album, rRated, hidden, series })
                db.MediaItemGenres.Add(new MediaItemGenre { MediaItemId = m.Id, GenreId = comedy.Id });

            // Limited user sees ONLY the open library.
            db.UserLibraryAccess.Add(new UserLibraryAccess { UserId = limited.Id, LibraryId = open.Id });
            await db.SaveChangesAsync();
        });

        return (unrestricted, limited, openLibId);
    }

    [Fact]
    public async Task ReturnsAPagedEnvelopeOfTopLevelItemsAcrossLibraries()
    {
        var (user, _, _) = await SeedAsync();

        var json = await ClientFor(user).GetStringAsync("/api/v1/browse?pageSize=50");
        using var doc = JsonDocument.Parse(json);
        var (titles, total) = Parse(doc);

        // Spans libraries AND media types.
        Assert.Contains("Br Nineties Movie", titles);
        Assert.Contains("Br Book", titles);
        Assert.Contains("Br Album", titles);
        Assert.Contains("Br Walled Movie", titles);   // unrestricted caller sees it

        // Child rows are reached through their parent, never shown as cards.
        Assert.DoesNotContain("Br S01E01", titles);
        Assert.DoesNotContain("Br Track 1", titles);

        Assert.Equal(titles.Count, Math.Min(total, 50));
        Assert.Equal(1, doc.RootElement.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task DecadeFilterSelectsTheWholeTenYearSpan()
    {
        var (user, _, _) = await SeedAsync();

        var json = await ClientFor(user).GetStringAsync("/api/v1/browse?decade=1990&pageSize=50");
        using var doc = JsonDocument.Parse(json);
        var (titles, _) = Parse(doc);

        Assert.Contains("Br Nineties Movie", titles); // 1995
        Assert.Contains("Br Book", titles);           // 1998
        Assert.Contains("Br Album", titles);          // 1992
        Assert.DoesNotContain("Br Noughties Movie", titles); // 2003 — outside the decade
    }

    [Fact]
    public async Task GenreFilterMatchesCaseInsensitively()
    {
        var (user, _, _) = await SeedAsync();

        // The seeded genre name is randomised for test isolation, so read it back off
        // an unfiltered browse rather than hard-coding it.
        var all = await ClientFor(user).GetStringAsync("/api/v1/browse?pageSize=50");
        using var allDoc = JsonDocument.Parse(all);
        var genre = allDoc.RootElement.GetProperty("items").EnumerateArray()
            .SelectMany(i => i.GetProperty("genres").EnumerateArray())
            .Select(g => g.GetString()!)
            .First();

        var json = await ClientFor(user).GetStringAsync($"/api/v1/browse?genre={genre.ToUpperInvariant()}&pageSize=50");
        using var doc = JsonDocument.Parse(json);
        var (titles, _) = Parse(doc);

        Assert.Contains("Br Nineties Movie", titles);
    }

    [Fact]
    public async Task LibraryAclStillAppliesWithNoLibraryIdInTheRoute()
    {
        var (_, limited, _) = await SeedAsync();

        var json = await ClientFor(limited).GetStringAsync("/api/v1/browse?pageSize=50");
        using var doc = JsonDocument.Parse(json);
        var (titles, total) = Parse(doc);

        Assert.Contains("Br Nineties Movie", titles);
        // The whole point: a cross-library endpoint must not become a way around the
        // per-library allow-list.
        Assert.DoesNotContain("Br Walled Movie", titles);
        // And the count must reflect the filtered set, not leak the true total.
        Assert.Equal(titles.Count, total);
    }

    [Fact]
    public async Task RatingCeilingAppliesAndIsReflectedInTheTotal()
    {
        var (user, _, _) = await SeedAsync();

        var before = await ClientFor(user).GetStringAsync("/api/v1/browse?pageSize=50");
        using var beforeDoc = JsonDocument.Parse(before);
        var (beforeTitles, beforeTotal) = Parse(beforeDoc);
        Assert.Contains("Br R Rated", beforeTitles);

        await Factory.WithDbAsync(async db =>
        {
            (await db.Users.FindAsync(user.Id))!.MaxRating = "PG";
            await db.SaveChangesAsync();
        });
        user.MaxRating = "PG"; // the ceiling rides the JWT claim

        var after = await ClientFor(user).GetStringAsync("/api/v1/browse?pageSize=50");
        using var afterDoc = JsonDocument.Parse(after);
        var (afterTitles, afterTotal) = Parse(afterDoc);

        Assert.DoesNotContain("Br R Rated", afterTitles);
        Assert.True(afterTotal < beforeTotal,
            "the R-rated item must drop out of the count, not just the page");
    }

    [Fact]
    public async Task UnplayedRollsUpChildPlaysToTheParent()
    {
        var (user, _, _) = await SeedAsync();

        // Play one EPISODE and one TRACK. Their parents (Series/Album) must then stop
        // counting as unplayed even though the parent rows were never played directly.
        await Factory.WithDbAsync(async db =>
        {
            var episode = db.MediaItems.First(m => m.Title == "Br S01E01");
            var track = db.MediaItems.First(m => m.Title == "Br Track 1");
            foreach (var (item, type) in new[] { (episode, MediaType.Episode), (track, MediaType.Audio) })
            {
                db.PlaybackHistory.Add(new PlaybackHistory
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    MediaItemId = item.Id,
                    MediaType = type,
                    StartedAt = DateTime.UtcNow.AddHours(-1),
                    LastBeatAt = DateTime.UtcNow.AddMinutes(-30),
                    MaxPosition = 500,
                    Completed = true,
                });
            }
            await db.SaveChangesAsync();
        });

        var json = await ClientFor(user).GetStringAsync("/api/v1/browse?unplayed=true&pageSize=50");
        using var doc = JsonDocument.Parse(json);
        var (titles, _) = Parse(doc);

        Assert.DoesNotContain("Br Series", titles); // an episode was played
        Assert.DoesNotContain("Br Album", titles);  // a track was played
        Assert.Contains("Br Nineties Movie", titles); // genuinely untouched
    }

    [Fact]
    public async Task TypesNarrowsTheGridToTheRequestedMediaTypes()
    {
        var (user, _, _) = await SeedAsync();

        var json = await ClientFor(user).GetStringAsync("/api/v1/browse?types=Movie,Series&pageSize=50");
        using var doc = JsonDocument.Parse(json);
        var (titles, _) = Parse(doc);

        Assert.Contains("Br Nineties Movie", titles);
        Assert.Contains("Br Series", titles);
        // The whole point of the video-only genre row: books and albums stay out.
        Assert.DoesNotContain("Br Book", titles);
        Assert.DoesNotContain("Br Album", titles);
    }

    [Fact]
    public async Task TypesCannotBeUsedToPullChildRowsIntoTheGrid()
    {
        var (user, _, _) = await SeedAsync();

        // Episode/Audio are reachable only through their parent. Asking for them
        // explicitly must not surface them as cards.
        var json = await ClientFor(user).GetStringAsync("/api/v1/browse?types=Episode,Audio&pageSize=50");
        using var doc = JsonDocument.Parse(json);
        var (titles, _) = Parse(doc);

        Assert.DoesNotContain("Br S01E01", titles);
        Assert.DoesNotContain("Br Track 1", titles);
        // Nothing valid was requested, so it falls back to the full browsable set
        // rather than returning an empty grid.
        Assert.Contains("Br Nineties Movie", titles);
    }

    [Fact]
    public async Task UnknownTypeNamesAreIgnoredRatherThanErroring()
    {
        var (user, _, _) = await SeedAsync();

        var json = await ClientFor(user).GetStringAsync("/api/v1/browse?types=Movie,Nonsense&pageSize=50");
        using var doc = JsonDocument.Parse(json);
        var (titles, _) = Parse(doc);

        Assert.Contains("Br Nineties Movie", titles);
        Assert.DoesNotContain("Br Book", titles);   // the valid half still narrowed
        Assert.DoesNotContain("Br Series", titles);
    }

    /// <summary>
    /// The Most Watched row's "See more" ranks by plays. The scope toggle must survive
    /// the trip: "playcount" is the all-user aggregate, "myplaycount" the caller's own
    /// history — linking to the wrong one silently swaps a personal ranking for the
    /// household's while the heading still says "Your Most Watched".
    /// </summary>
    [Fact]
    public async Task PlayCountSortsDifferBetweenEveryoneAndTheCaller()
    {
        var alice = await Factory.SeedUserAsync($"pc-a-{Guid.NewGuid():N}"[..20]);
        var bob = await Factory.SeedUserAsync($"pc-b-{Guid.NewGuid():N}"[..20]);

        await Factory.WithDbAsync(async db =>
        {
            var lib = new Library { Id = Guid.NewGuid(), Name = $"Pc-{Guid.NewGuid():N}"[..12], Type = LibraryType.Movie, Paths = new() { "/pc" } };
            db.Libraries.Add(lib);
            await db.SaveChangesAsync();

            MediaItem Movie(string title, int playCount) => new()
            {
                Id = Guid.NewGuid(),
                LibraryId = lib.Id,
                Title = title,
                SortTitle = title,
                Path = $"/pc/{title}.mkv",
                Type = MediaType.Movie,
                PlayCount = playCount,
            };

            // Bob hammered "Bobs Favourite"; Alice watched "Alices Pick" a few times.
            // The all-user aggregate ranks Bob's on top, Alice's personal count does not.
            var bobs = Movie("PC Bobs Favourite", 50);
            var alices = Movie("PC Alices Pick", 3);
            db.MediaItems.AddRange(bobs, alices);
            await db.SaveChangesAsync();

            void Plays(MediaItem m, User who, int count)
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
            Plays(bobs, bob, 50);
            Plays(alices, alice, 3);
            await db.SaveChangesAsync();
        });

        var everyone = await ClientFor(alice).GetStringAsync("/api/v1/browse?sortBy=playcount&types=Movie,Series&pageSize=50");
        using (var doc = JsonDocument.Parse(everyone))
        {
            var (titles, _) = Parse(doc);
            Assert.Equal("PC Bobs Favourite", titles.First());
        }

        var mine = await ClientFor(alice).GetStringAsync("/api/v1/browse?sortBy=myplaycount&types=Movie,Series&pageSize=50");
        using (var doc = JsonDocument.Parse(mine))
        {
            var (titles, _) = Parse(doc);
            // Alice never played Bob's, so hers ranks first under the personal sort.
            Assert.Equal("PC Alices Pick", titles.First());
        }
    }

    [Fact]
    public async Task SearchMatchesTitleAndIsCaseInsensitive()
    {
        var (user, _, _) = await SeedAsync();

        var json = await ClientFor(user).GetStringAsync("/api/v1/browse?search=NINETIES&pageSize=50");
        using var doc = JsonDocument.Parse(json);
        var (titles, _) = Parse(doc);

        Assert.Contains("Br Nineties Movie", titles);
        Assert.DoesNotContain("Br Noughties Movie", titles);
    }

    [Fact]
    public async Task YearFilterIsExactUnlikeDecade()
    {
        var (user, _, _) = await SeedAsync();

        var json = await ClientFor(user).GetStringAsync("/api/v1/browse?year=1995&pageSize=50");
        using var doc = JsonDocument.Parse(json);
        var (titles, _) = Parse(doc);

        Assert.Equal(new[] { "Br Nineties Movie" }, titles);
    }

    [Fact]
    public async Task WatchedFilterSplitsTheGridBothWays()
    {
        var (user, _, _) = await SeedAsync();
        await Factory.WithDbAsync(async db =>
        {
            var movie = db.MediaItems.First(m => m.Title == "Br Nineties Movie");
            db.UserMediaInteractions.Add(new UserMediaInteraction
            {
                UserId = user.Id,
                MediaItemId = movie.Id,
                IsWatched = true,
            });
            await db.SaveChangesAsync();
        });

        var watched = await ClientFor(user).GetStringAsync("/api/v1/browse?watched=true&pageSize=50");
        using (var doc = JsonDocument.Parse(watched))
        {
            var (titles, _) = Parse(doc);
            Assert.Equal(new[] { "Br Nineties Movie" }, titles);
        }

        var unwatched = await ClientFor(user).GetStringAsync("/api/v1/browse?watched=false&pageSize=50");
        using (var doc = JsonDocument.Parse(unwatched))
        {
            var (titles, _) = Parse(doc);
            Assert.DoesNotContain("Br Nineties Movie", titles);
            Assert.Contains("Br Book", titles); // never interacted with — still unwatched
        }
    }

    /// <summary>
    /// Continue Watching's "See more". A Movie is in progress when started and not
    /// flagged watched; a Series when any EPISODE has been started, since the show row
    /// is never played directly.
    /// </summary>
    [Fact]
    public async Task InProgressCoversStartedMoviesAndSeriesWithStartedEpisodes()
    {
        var (user, _, _) = await SeedAsync();
        await Factory.WithDbAsync(async db =>
        {
            var movie = db.MediaItems.First(m => m.Title == "Br Nineties Movie");
            var finished = db.MediaItems.First(m => m.Title == "Br Noughties Movie");
            var episode = db.MediaItems.First(m => m.Title == "Br S01E01");

            // Started, not finished -> in progress.
            db.UserMediaInteractions.Add(new UserMediaInteraction
            { UserId = user.Id, MediaItemId = movie.Id, PlaybackPosition = 300, IsWatched = false });
            // Started but flagged watched -> NOT in progress.
            db.UserMediaInteractions.Add(new UserMediaInteraction
            { UserId = user.Id, MediaItemId = finished.Id, PlaybackPosition = 4000, IsWatched = true });
            // An episode started -> its SERIES is in progress.
            db.UserMediaInteractions.Add(new UserMediaInteraction
            { UserId = user.Id, MediaItemId = episode.Id, PlaybackPosition = 120, IsWatched = false });
            await db.SaveChangesAsync();
        });

        var json = await ClientFor(user).GetStringAsync("/api/v1/browse?inProgress=true&pageSize=50");
        using var doc = JsonDocument.Parse(json);
        var (titles, _) = Parse(doc);

        Assert.Contains("Br Nineties Movie", titles);
        Assert.Contains("Br Series", titles);              // via its started episode
        Assert.DoesNotContain("Br Noughties Movie", titles); // finished
        Assert.DoesNotContain("Br Book", titles);            // never started
        Assert.DoesNotContain("Br S01E01", titles);          // episodes are never cards
    }

    [Fact]
    public async Task GenresEndpointListsOnlyVisibleGenresAndRespectsTypeNarrowing()
    {
        var (_, limited, _) = await SeedAsync();

        var all = await ClientFor(limited).GetStringAsync("/api/v1/browse/genres");
        using var allDoc = JsonDocument.Parse(all);
        var genres = allDoc.RootElement.EnumerateArray().Select(g => g.GetString()!).ToList();

        Assert.NotEmpty(genres);
        Assert.Equal(genres.OrderBy(g => g), genres); // sorted for a stable picker
        Assert.Equal(genres.Distinct().Count(), genres.Count);

        // Narrowing to video must not offer genres that only book/album items carry.
        var video = await ClientFor(limited).GetStringAsync("/api/v1/browse/genres?types=Movie,Series");
        using var videoDoc = JsonDocument.Parse(video);
        var videoGenres = videoDoc.RootElement.EnumerateArray().Select(g => g.GetString()!).ToList();
        Assert.All(videoGenres, g => Assert.Contains(g, genres));
    }

    /// <summary>
    /// Every sort key must run both ways. Title is checked explicitly because it is the
    /// one key whose NATURAL direction is ascending — the others default to descending,
    /// so a bug that ignored sortDir entirely would still look right for them.
    /// </summary>
    [Theory]
    [InlineData("title")]
    [InlineData("dateadded")]
    [InlineData("year")]
    public async Task EverySortKeyRunsBothDirections(string sortBy)
    {
        var (user, _, _) = await SeedAsync();

        var asc = await ClientFor(user).GetStringAsync($"/api/v1/browse?sortBy={sortBy}&sortDir=asc&pageSize=50");
        var desc = await ClientFor(user).GetStringAsync($"/api/v1/browse?sortBy={sortBy}&sortDir=desc&pageSize=50");

        using var ascDoc = JsonDocument.Parse(asc);
        using var descDoc = JsonDocument.Parse(desc);
        var (ascTitles, ascTotal) = Parse(ascDoc);
        var (descTitles, descTotal) = Parse(descDoc);

        // Same set, opposite ends. Comparing full sequences would be brittle for keys
        // with ties (several items share a year), so pin the endpoints instead.
        Assert.Equal(ascTotal, descTotal);
        Assert.Equal(ascTitles.OrderBy(t => t), descTitles.OrderBy(t => t));
        Assert.NotEqual(ascTitles.First(), descTitles.First());
        Assert.Equal(ascTitles.First(), descTitles.Last());
    }

    /// <summary>
    /// Omitting sortDir must behave EXACTLY as before it existed. Every "See more" link
    /// already shipped leaves it off, so a default of "ascending" would silently invert
    /// Most Watched, Recently Added and Never Played the moment this deployed.
    /// </summary>
    [Fact]
    public async Task OmittingSortDirKeepsEachKeysNaturalDirection()
    {
        var (user, _, _) = await SeedAsync();

        async Task<List<string>> TitlesFor(string query)
        {
            var json = await ClientFor(user).GetStringAsync($"/api/v1/browse?{query}&pageSize=50");
            using var doc = JsonDocument.Parse(json);
            return Parse(doc).titles;
        }

        // Title reads A-Z by nature.
        Assert.Equal(await TitlesFor("sortBy=title"), await TitlesFor("sortBy=title&sortDir=asc"));
        // Dates and years read newest-first by nature.
        Assert.Equal(await TitlesFor("sortBy=dateadded"), await TitlesFor("sortBy=dateadded&sortDir=desc"));
        Assert.Equal(await TitlesFor("sortBy=year"), await TitlesFor("sortBy=year&sortDir=desc"));
    }

    [Fact]
    public async Task UnrecognisedSortDirFallsBackToTheNaturalDirection()
    {
        var (user, _, _) = await SeedAsync();

        // A hand-edited or stale URL should still render a sensible page, not error.
        var junk = await ClientFor(user).GetStringAsync("/api/v1/browse?sortBy=title&sortDir=sideways&pageSize=50");
        var natural = await ClientFor(user).GetStringAsync("/api/v1/browse?sortBy=title&pageSize=50");

        using var junkDoc = JsonDocument.Parse(junk);
        using var naturalDoc = JsonDocument.Parse(natural);
        Assert.Equal(Parse(naturalDoc).titles, Parse(junkDoc).titles);
    }

    [Fact]
    public async Task PageSizeIsClampedSoAClientCannotRequestTheWholeLibrary()
    {
        var (user, _, _) = await SeedAsync();

        var json = await ClientFor(user).GetStringAsync("/api/v1/browse?pageSize=5000");
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(100, doc.RootElement.GetProperty("pageSize").GetInt32());
    }
}
