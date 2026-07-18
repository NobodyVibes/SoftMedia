using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// Library-grid sorts backed by the R-WI-013 play aggregates: "playcount" and
/// "lastplayed". Movie grids sort on the item's own counters; TV grids show
/// SERIES rows, so their sort aggregates the episodes' counters up to the
/// series via a correlated subquery. Never-played items trail either way.
public class LibrarySortIntegrationTests : IntegrationTestBase
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

    private static List<string> Titles(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("title").GetString()!)
            .ToList();

    [Fact]
    public async Task MovieLibrary_SortsByPlayCount_AndLastPlayed()
    {
        var user = await Factory.SeedUserAsync($"sort-{Guid.NewGuid():N}"[..20]);
        Guid libId = Guid.Empty;
        await Factory.WithDbAsync(async db =>
        {
            var lib = new Library { Id = Guid.NewGuid(), Name = $"Sort-{Guid.NewGuid():N}"[..12], Type = LibraryType.Movie, Paths = new() { "/srt" } };
            db.Libraries.Add(lib);
            libId = lib.Id;

            MediaItem Movie(string title, int plays, DateTime? last) => new()
            {
                Id = Guid.NewGuid(),
                LibraryId = lib.Id,
                Title = title,
                SortTitle = title,
                Path = $"/srt/{title}.mkv",
                Type = MediaType.Movie,
                PlayCount = plays,
                LastPlayed = last,
            };

            db.MediaItems.AddRange(
                Movie("Sort Heavy", 5, DateTime.UtcNow.AddDays(-3)),
                Movie("Sort Light", 1, DateTime.UtcNow.AddDays(-1)),
                Movie("Sort Never", 0, null));
            await db.SaveChangesAsync();
        });
        var client = ClientFor(user);

        var byPlays = Titles(await client.GetStringAsync($"/api/v1/libraries/{libId}/items?sortBy=playcount"));
        Assert.Equal(new[] { "Sort Heavy", "Sort Light", "Sort Never" }, byPlays);

        // lastplayed: most recent first; never-played (NULL) trails.
        var byRecency = Titles(await client.GetStringAsync($"/api/v1/libraries/{libId}/items?sortBy=lastplayed"));
        Assert.Equal(new[] { "Sort Light", "Sort Heavy", "Sort Never" }, byRecency);
    }

    [Fact]
    public async Task TvLibrary_PlaySorts_AggregateEpisodesUpToTheSeries()
    {
        var user = await Factory.SeedUserAsync($"sortv-{Guid.NewGuid():N}"[..20]);
        Guid libId = Guid.Empty;
        await Factory.WithDbAsync(async db =>
        {
            var lib = new Library { Id = Guid.NewGuid(), Name = $"SortT-{Guid.NewGuid():N}"[..12], Type = LibraryType.TV, Paths = new() { "/stv" } };
            db.Libraries.Add(lib);
            libId = lib.Id;

            MediaItem Item(string title, MediaType type, Guid? seriesId = null, int plays = 0, DateTime? last = null) => new()
            {
                Id = Guid.NewGuid(),
                LibraryId = lib.Id,
                Title = title,
                SortTitle = title,
                Path = $"/stv/{title}",
                Type = type,
                SeriesId = seriesId,
                PlayCount = plays,
                LastPlayed = last,
            };

            // Series rows themselves carry NO counters — only their episodes do.
            var binged = Item("Sort Binged Show", MediaType.Series);
            var sampled = Item("Sort Sampled Show", MediaType.Series);
            var untouched = Item("Sort Untouched Show", MediaType.Series);
            db.MediaItems.AddRange(binged, sampled, untouched);
            await db.SaveChangesAsync();

            db.MediaItems.AddRange(
                Item("BS E1", MediaType.Episode, binged.Id, plays: 3, last: DateTime.UtcNow.AddDays(-5)),
                Item("BS E2", MediaType.Episode, binged.Id, plays: 2, last: DateTime.UtcNow.AddDays(-4)),
                Item("SS E1", MediaType.Episode, sampled.Id, plays: 1, last: DateTime.UtcNow.AddDays(-1)),
                Item("US E1", MediaType.Episode, untouched.Id));
            await db.SaveChangesAsync();
        });
        var client = ClientFor(user);

        // playcount: 5 aggregated plays beat 1; the browse still shows series only.
        var byPlays = Titles(await client.GetStringAsync($"/api/v1/libraries/{libId}/items?sortBy=playcount"));
        Assert.Equal(new[] { "Sort Binged Show", "Sort Sampled Show", "Sort Untouched Show" }, byPlays);
        Assert.DoesNotContain(byPlays, t => t.Contains("E1"));

        // lastplayed: the sampled show was watched most recently despite fewer plays.
        var byRecency = Titles(await client.GetStringAsync($"/api/v1/libraries/{libId}/items?sortBy=lastplayed"));
        Assert.Equal(new[] { "Sort Sampled Show", "Sort Binged Show", "Sort Untouched Show" }, byRecency);
    }
}
