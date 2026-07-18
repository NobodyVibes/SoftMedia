using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public async Task UserWithHistory_GetsGenreAffinityRows_ExcludingSeedsAndWatched()
    {
        var (user, _, _, _, _) = await SeedAsync();

        var json = await ClientFor(user).GetStringAsync("/api/v1/media/home-rows");
        using var doc = JsonDocument.Parse(json);
        var (rowTitles, itemTitles) = Parse(doc);

        Assert.NotEmpty(rowTitles);
        Assert.Contains(rowTitles, t => t == "Because you watched Seed Movie");
        Assert.Contains("Action Pick 1", itemTitles);
        Assert.DoesNotContain("Seed Movie", itemTitles);      // the seed itself never recommends itself
        Assert.DoesNotContain("Watched Action", itemTitles);  // finished items excluded
        Assert.DoesNotContain("Unrelated Drama", itemTitles); // no shared genres → not recommended
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

    [Fact]
    public async Task NewUser_WithNoHistory_GetsAnEmptyList()
    {
        await SeedAsync(); // catalog exists, but this fresh user never played anything
        var fresh = await Factory.SeedUserAsync($"rows-new-{Guid.NewGuid():N}"[..20]);

        var json = await ClientFor(fresh).GetStringAsync("/api/v1/media/home-rows");
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(0, doc.RootElement.GetArrayLength());
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
        Assert.Contains(rowTitles, t => t == "Because you watched Seed Movie");
        Assert.DoesNotContain(rowTitles, t => t.Contains("Track "));
        Assert.Contains("Action Pick 1", itemTitles);
    }

    [Fact]
    public async Task RowsNeverRepeatAnItem()
    {
        var (user, _, _, _, _) = await SeedAsync();

        var json = await ClientFor(user).GetStringAsync("/api/v1/media/home-rows");
        using var doc = JsonDocument.Parse(json);
        var (_, itemTitles) = Parse(doc);

        Assert.Equal(itemTitles.Distinct().Count(), itemTitles.Count);
    }
}
