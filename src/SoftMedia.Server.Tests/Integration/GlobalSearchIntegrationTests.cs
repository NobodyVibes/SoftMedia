using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// R-WI-017 — multi-field search + the D-12 rating-ceiling fix. Pins:
/// (1) D-12: a rating-blocked title is absent from global search for EVERY matched
///     field (title, cast, description) — search was the one browse path without
///     the ceiling;
/// (2) per-field matching: cast, genre, description, track title, artist name,
///     episode title;
/// (3) ranking: title-prefix matches come before matches on other fields;
/// (4) the per-library search matches the same widened fields.
public class GlobalSearchIntegrationTests : IntegrationTestBase
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

    private Guid _movieLibId;
    private Guid _musicLibId;

    /// Seeds: an R-rated movie (title "Edge of Tomorrow", cast "Tom Cruise",
    /// genre "Action", overview contains "relives"), a PG movie whose title
    /// STARTS with the shared word ("Tomorrow Never Dies"), a music
    /// artist→album→track chain, and a series→episode.
    private async Task SeedCatalogAsync()
    {
        await Factory.WithDbAsync(async db =>
        {
            var movieLib = new Library { Id = Guid.NewGuid(), Name = "Search-Movies", Type = LibraryType.Movie, Paths = new() { "/sm" } };
            var musicLib = new Library { Id = Guid.NewGuid(), Name = "Search-Music", Type = LibraryType.Music, Paths = new() { "/smu" } };
            var tvLib = new Library { Id = Guid.NewGuid(), Name = "Search-TV", Type = LibraryType.TV, Paths = new() { "/stv" } };
            db.Libraries.AddRange(movieLib, musicLib, tvLib);
            _movieLibId = movieLib.Id;
            _musicLibId = musicLib.Id;

            var cruise = new Person { Name = "Tom Cruise" };
            var action = new Genre { Name = "Action" };
            db.Persons.Add(cruise);
            db.Genres.Add(action);

            var edge = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = movieLib.Id,
                Title = "Edge of Tomorrow",
                SortTitle = "Edge of Tomorrow",
                Path = "/sm/edge.mkv",
                Type = MediaType.Movie,
                ContentRating = "R",
                Overview = "A soldier relives the same brutal day in a time loop.",
            };
            var dies = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = movieLib.Id,
                Title = "Tomorrow Never Dies",
                SortTitle = "Tomorrow Never Dies",
                Path = "/sm/dies.mkv",
                Type = MediaType.Movie,
                ContentRating = "PG-13",
                Overview = "A spy stops a media mogul.",
            };
            db.MediaItems.AddRange(edge, dies);
            await db.SaveChangesAsync(); // materialize Person/Genre ids

            db.MediaItemCasts.Add(new MediaItemCast { MediaItemId = edge.Id, PersonId = cruise.Id, Character = "Cage" });
            db.MediaItemGenres.Add(new MediaItemGenre { MediaItemId = edge.Id, GenreId = action.Id });

            var artist = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = musicLib.Id,
                Title = "Arch Enemy",
                SortTitle = "Arch Enemy",
                Path = "/smu/arch",
                Type = MediaType.Artist,
            };
            var album = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = musicLib.Id,
                Title = "War Eternal",
                SortTitle = "War Eternal",
                Path = "/smu/arch/war",
                Type = MediaType.Album,
                ArtistId = artist.Id,
            };
            var track = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = musicLib.Id,
                Title = "No More Regrets",
                SortTitle = "No More Regrets",
                Path = "/smu/arch/war/03.flac",
                Type = MediaType.Audio,
                ArtistId = artist.Id,
                AlbumId = album.Id,
            };

            var series = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = tvLib.Id,
                Title = "Some Show",
                SortTitle = "Some Show",
                Path = "/stv/show",
                Type = MediaType.Series,
            };
            var episode = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = tvLib.Id,
                Title = "The Quantum Gambit",
                SortTitle = "The Quantum Gambit",
                Path = "/stv/show/s01e04.mkv",
                Type = MediaType.Episode,
                SeriesId = series.Id,
                SeasonNumber = 1,
                EpisodeNumber = 4,
            };
            db.MediaItems.AddRange(artist, album, track, series, episode);
            await db.SaveChangesAsync();
        });
    }

    private static List<string> TitlesOf(JsonDocument doc) =>
        doc.RootElement.EnumerateArray()
            .SelectMany(lib => lib.GetProperty("items").EnumerateArray())
            .Select(i => i.GetProperty("title").GetString()!)
            .ToList();

    private async Task<List<string>> SearchTitlesAsync(HttpClient client, string query)
    {
        var json = await client.GetStringAsync($"/api/v1/media/search?query={Uri.EscapeDataString(query)}&limit=10");
        using var doc = JsonDocument.Parse(json);
        return TitlesOf(doc);
    }

    [Fact]
    public async Task D12_RatingBlockedTitle_IsAbsent_ForEveryMatchedField()
    {
        await SeedCatalogAsync();
        var restricted = await Factory.SeedUserAsync("search-kid");
        await Factory.WithDbAsync(async db =>
        {
            (await db.Users.FindAsync(restricted.Id))!.MaxRating = "G";
            await db.SaveChangesAsync();
        });
        restricted.MaxRating = "G"; // the ceiling rides the JWT claim
        var client = ClientFor(restricted);

        Assert.DoesNotContain("Edge of Tomorrow", await SearchTitlesAsync(client, "Edge of Tomorrow")); // by title
        Assert.DoesNotContain("Edge of Tomorrow", await SearchTitlesAsync(client, "Tom Cruise"));       // by cast
        Assert.DoesNotContain("Edge of Tomorrow", await SearchTitlesAsync(client, "relives"));          // by description
        Assert.DoesNotContain("Edge of Tomorrow", await SearchTitlesAsync(client, "Action"));           // by genre

        // An unrestricted user still finds it — the filter is the ceiling, not the query.
        var adult = await Factory.SeedUserAsync("search-adult");
        Assert.Contains("Edge of Tomorrow", await SearchTitlesAsync(ClientFor(adult), "Edge of Tomorrow"));
    }

    [Fact]
    public async Task MultiField_Cast_Genre_Description_AllMatch()
    {
        await SeedCatalogAsync();
        var user = await Factory.SeedUserAsync("search-user");
        var client = ClientFor(user);

        Assert.Contains("Edge of Tomorrow", await SearchTitlesAsync(client, "Tom Cruise"));
        Assert.Contains("Edge of Tomorrow", await SearchTitlesAsync(client, "Action"));
        Assert.Contains("Edge of Tomorrow", await SearchTitlesAsync(client, "relives"));
    }

    [Fact]
    public async Task Tracks_AndEpisodes_AreSearchable()
    {
        await SeedCatalogAsync();
        var user = await Factory.SeedUserAsync("search-user2");
        var client = ClientFor(user);

        Assert.Contains("No More Regrets", await SearchTitlesAsync(client, "No More Regrets")); // track by title
        var byArtist = await SearchTitlesAsync(client, "Arch Enemy");
        Assert.Contains("Arch Enemy", byArtist);       // the artist itself
        Assert.Contains("War Eternal", byArtist);      // album via artist name
        Assert.Contains("No More Regrets", byArtist);  // track via artist name
        Assert.Contains("The Quantum Gambit", await SearchTitlesAsync(client, "Quantum Gambit")); // episode by title
    }

    [Fact]
    public async Task Results_AreGroupedIntoOneEntryPerLibrary()
    {
        // Found live: reference-keyed GroupBy over AsNoTracking materialization made
        // every item its own group — the dropdown showed "Music" repeated 25 times.
        await SeedCatalogAsync();
        var user = await Factory.SeedUserAsync("search-user5");

        // "Arch Enemy" matches three music items (artist + album + track) — they must
        // arrive as ONE Search-Music group.
        var json = await ClientFor(user).GetStringAsync("/api/v1/media/search?query=Arch%20Enemy&limit=10");
        using var doc = JsonDocument.Parse(json);
        var groups = doc.RootElement.EnumerateArray()
            .Select(lib => lib.GetProperty("libraryName").GetString()!)
            .ToList();

        Assert.Equal(groups.Distinct().Count(), groups.Count); // no duplicate library groups
        var music = doc.RootElement.EnumerateArray()
            .Single(lib => lib.GetProperty("libraryName").GetString() == "Search-Music");
        Assert.Equal(3, music.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Ranking_TitlePrefix_BeatsOtherFieldMatches()
    {
        await SeedCatalogAsync();
        var user = await Factory.SeedUserAsync("search-user3");
        var titles = await SearchTitlesAsync(ClientFor(user), "Tomorrow");

        // Prefix match first, containing-title second.
        Assert.Equal("Tomorrow Never Dies", titles[0]);
        Assert.Contains("Edge of Tomorrow", titles);
        Assert.True(titles.IndexOf("Tomorrow Never Dies") < titles.IndexOf("Edge of Tomorrow"));
    }

    [Fact]
    public async Task LikeMetacharacters_AreLiteral_NotWildcards()
    {
        // Review MED: raw %/_ were live LIKE wildcards — "Edge_of" matched
        // "Edge of Tomorrow", and a leading % made every contains-match rank as
        // a "prefix" hit. Escaped now: metachars must match only themselves.
        await SeedCatalogAsync();
        var user = await Factory.SeedUserAsync("search-meta");
        var client = ClientFor(user);

        Assert.DoesNotContain("Edge of Tomorrow", await SearchTitlesAsync(client, "Edge_of"));
        Assert.DoesNotContain("Edge of Tomorrow", await SearchTitlesAsync(client, "%dge"));
        // Plain matching is unaffected.
        Assert.Contains("Edge of Tomorrow", await SearchTitlesAsync(client, "Edge of"));
    }

    [Fact]
    public async Task Seasons_AreExcludedFromSearch()
    {
        // "Season 1" would match every show and adds nothing over the series hit.
        await SeedCatalogAsync();
        await Factory.WithDbAsync(async db =>
        {
            var tvLib = db.Libraries.First(l => l.Name == "Search-TV");
            db.MediaItems.Add(new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = tvLib.Id,
                Title = "Season 1",
                SortTitle = "Season 1",
                Path = "/stv/show/s1",
                Type = MediaType.Season,
            });
            await db.SaveChangesAsync();
        });
        var user = await Factory.SeedUserAsync("search-season");

        Assert.DoesNotContain("Season 1", await SearchTitlesAsync(ClientFor(user), "Season 1"));
    }

    [Fact]
    public async Task Episode_OfASeriesAboveTheTvCeiling_IsHidden_EvenWithItsOwnPermissiveRating()
    {
        // Review MED-LOW: an episode stamped TV-G inside a TV-MA series passed the
        // row-level rating filter — search must not become a side door around the
        // blocked series page.
        await SeedCatalogAsync();
        await Factory.WithDbAsync(async db =>
        {
            var series = db.MediaItems.First(m => m.Title == "Some Show");
            series.ContentRating = "TV-MA";
            var episode = db.MediaItems.First(m => m.Title == "The Quantum Gambit");
            episode.ContentRating = "TV-G";
            await db.SaveChangesAsync();
        });

        var kid = await Factory.SeedUserAsync("search-tvkid");
        await Factory.WithDbAsync(async db =>
        {
            (await db.Users.FindAsync(kid.Id))!.ContentRatings = "{\"TV\":\"TV-G\"}";
            await db.SaveChangesAsync();
        });

        Assert.DoesNotContain("The Quantum Gambit", await SearchTitlesAsync(ClientFor(kid), "Quantum Gambit"));

        // Unrestricted users still find it.
        var adult = await Factory.SeedUserAsync("search-tvadult");
        Assert.Contains("The Quantum Gambit", await SearchTitlesAsync(ClientFor(adult), "Quantum Gambit"));
    }

    [Fact]
    public async Task TrackAndEpisodeResults_CarryNameContext()
    {
        // The dropdown disambiguates duplicate titles via metadata.artist/album/
        // seriesTitle — populated only when the search query includes the navs.
        await SeedCatalogAsync();
        var user = await Factory.SeedUserAsync("search-ctx");

        var json = await ClientFor(user).GetStringAsync("/api/v1/media/search?query=No%20More%20Regrets&limit=5");
        using var doc = JsonDocument.Parse(json);
        var track = doc.RootElement.EnumerateArray()
            .SelectMany(lib => lib.GetProperty("items").EnumerateArray())
            .Single(i => i.GetProperty("title").GetString() == "No More Regrets");
        Assert.Equal("Arch Enemy", track.GetProperty("metadata").GetProperty("artist").GetString());
        Assert.Equal("War Eternal", track.GetProperty("metadata").GetProperty("album").GetString());

        var json2 = await ClientFor(user).GetStringAsync("/api/v1/media/search?query=Quantum%20Gambit&limit=5");
        using var doc2 = JsonDocument.Parse(json2);
        var episode = doc2.RootElement.EnumerateArray()
            .SelectMany(lib => lib.GetProperty("items").EnumerateArray())
            .Single(i => i.GetProperty("title").GetString() == "The Quantum Gambit");
        Assert.Equal("Some Show", episode.GetProperty("metadata").GetProperty("seriesTitle").GetString());
    }

    [Fact]
    public async Task PerLibrarySearch_MatchesCast_AndDescription()
    {
        await SeedCatalogAsync();
        var user = await Factory.SeedUserAsync("search-user4");
        var client = ClientFor(user);

        foreach (var q in new[] { "Tom Cruise", "relives" })
        {
            var json = await client.GetStringAsync($"/api/v1/libraries/{_movieLibId}/items?search={Uri.EscapeDataString(q)}");
            using var doc = JsonDocument.Parse(json);
            var titles = doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("title").GetString()).ToList();
            Assert.Contains("Edge of Tomorrow", titles);
        }
    }
}
