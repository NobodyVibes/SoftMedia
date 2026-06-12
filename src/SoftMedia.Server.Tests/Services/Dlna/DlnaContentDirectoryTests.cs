using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Dlna;
using SoftMedia.Server.Services.Infrastructure;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Dlna;

/// CC/P4-004 — the DLNA ContentDirectory: maps SoftMedia's library tree onto UPnP containers/items.
/// Verifies the browse hierarchy (root → AV libraries → movies | series→episodes | albums→tracks),
/// that non-AV libraries are excluded, paging counts, res URLs, and DIDL XML escaping.
public class DlnaContentDirectoryTests
{
    private const string Base = "http://192.168.1.50:5011";
    private readonly Guid _movieLib = Guid.NewGuid();
    private readonly Guid _tvLib = Guid.NewGuid();
    private readonly Guid _musicLib = Guid.NewGuid();
    private readonly Guid _bookLib = Guid.NewGuid();
    private readonly Guid _series = Guid.NewGuid();
    private readonly Guid _album = Guid.NewGuid();

    private AppDbContext NewDb()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"dlna-{Guid.NewGuid()}").Options);

        db.Libraries.AddRange(
            new Library { Id = _movieLib, Name = "Movies", Type = LibraryType.Movie, Order = 0 },
            new Library { Id = _tvLib, Name = "Shows", Type = LibraryType.TV, Order = 1 },
            new Library { Id = _musicLib, Name = "Music", Type = LibraryType.Music, Order = 2 },
            new Library { Id = _bookLib, Name = "Books", Type = LibraryType.Book, Order = 3 });

        db.MediaItems.AddRange(
            Item("Blade Runner & Friends <2049>", _movieLib, MediaType.Movie, sort: "blade"),
            Item("Arrival", _movieLib, MediaType.Movie, sort: "arrival"),
            new MediaItem { Id = _series, Title = "The Show", SortTitle = "show", Path = "/tv/show", LibraryId = _tvLib, Type = MediaType.Series },
            Episode("E2", 1, 2), Episode("E1", 1, 1),
            new MediaItem { Id = _album, Title = "The Album", SortTitle = "album", Path = "/music/album", LibraryId = _musicLib, Type = MediaType.Album },
            Track("Track B", 2), Track("Track A", 1),
            // A book — must NOT appear in DLNA.
            Item("A Novel", _bookLib, MediaType.Book, sort: "novel"));
        db.SaveChanges();
        return db;
    }

    private static MediaItem Item(string title, Guid lib, MediaType type, string sort) => new()
    {
        Id = Guid.NewGuid(), Title = title, SortTitle = sort, Path = $"/x/{sort}.mkv",
        LibraryId = lib, Type = type, Size = 1000, Duration = 90 * 60,
    };

    private MediaItem Episode(string title, int season, int ep) => new()
    {
        Id = Guid.NewGuid(), Title = title, SortTitle = title, Path = $"/tv/{title}.mkv",
        LibraryId = _tvLib, Type = MediaType.Episode, SeriesId = _series, SeasonNumber = season, EpisodeNumber = ep,
    };

    private MediaItem Track(string title, int trackNo) => new()
    {
        Id = Guid.NewGuid(), Title = title, SortTitle = title, Path = $"/music/{title}.flac",
        LibraryId = _musicLib, Type = MediaType.Audio, AlbumId = _album, TrackNumber = trackNo,
    };

    // Default: expose all three AV libraries (the pre-allow-set behaviour) so the existing
    // hierarchy assertions hold. The allow-set itself is exercised by the dedicated tests below.
    private IDlnaContentDirectory Cd(AppDbContext db) => CdExposing(db, _movieLib, _tvLib, _musicLib);

    private static IDlnaContentDirectory CdExposing(AppDbContext db, params Guid[] exposed)
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetSettingAsync(DlnaAccess.ExposedLibrariesSetting, ""))
            .ReturnsAsync(string.Join(",", exposed));
        return new DlnaContentDirectory(db, settings.Object);
    }

    // audit wave-2 M-6: expose libraries AND apply a per-type DLNA rating ceiling (JSON).
    private static IDlnaContentDirectory CdExposingWithCeiling(AppDbContext db, string ratingJson, params Guid[] exposed)
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetSettingAsync(DlnaAccess.ExposedLibrariesSetting, ""))
            .ReturnsAsync(string.Join(",", exposed));
        settings.Setup(s => s.GetSettingAsync(DlnaAccess.MaxContentRatingsSetting, ""))
            .ReturnsAsync(ratingJson);
        return new DlnaContentDirectory(db, settings.Object);
    }

    [Fact]
    public async Task DlnaRatingCeiling_HidesOverRatingMovies()
    {
        using var db = NewDb();
        db.MediaItems.First(m => m.Title == "Arrival").ContentRating = "G";
        db.MediaItems.First(m => m.Title.StartsWith("Blade")).ContentRating = "R";
        db.SaveChanges();

        // Ceiling Movie:PG-13 → the R movie must be hidden from Browse.
        var cd = CdExposingWithCeiling(db, """{"Movie":"PG-13"}""", _movieLib);
        var r = await cd.BrowseAsync($"L:{_movieLib}", false, 0, 0, Base, default);
        var titles = Children(r.Didl, "item").Select(Title).ToList();

        Assert.Equal(1, r.TotalMatches);
        Assert.Equal("Arrival", Assert.Single(titles));
    }

    private static List<XElement> Children(string didl, string localName)
        => XDocument.Parse(didl).Root!.Elements().Where(e => e.Name.LocalName == localName).ToList();

    private static string? Title(XElement el) => el.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value;

    [Fact]
    public async Task Root_ListsOnlyAvLibraries()
    {
        using var db = NewDb();
        var r = await Cd(db).BrowseAsync("0", false, 0, 0, Base, default);

        var containers = Children(r.Didl, "container");
        Assert.Equal(3, r.TotalMatches);
        Assert.Equal(3, containers.Count);
        Assert.Equal(new[] { "Movies", "Shows", "Music" }, containers.Select(Title));
        Assert.DoesNotContain("Books", containers.Select(Title));
        Assert.All(containers, c => Assert.StartsWith("L:", c.Attribute("id")!.Value));
    }

    [Fact]
    public async Task MovieLibrary_ListsVideoItems_WithResUrlAndEscapedTitle()
    {
        using var db = NewDb();
        var r = await Cd(db).BrowseAsync($"L:{_movieLib}", false, 0, 0, Base, default);

        var items = Children(r.Didl, "item");
        Assert.Equal(2, items.Count);
        // Sorted by SortTitle: "arrival" before "blade". Title() returns the PARSED value, so a
        // correct match proves the special chars were escaped and round-trip cleanly.
        Assert.Equal(new[] { "Arrival", "Blade Runner & Friends <2049>" }, items.Select(Title));
        // And the raw DIDL is escaped (not literal & / <), so it's well-formed inside SOAP.
        Assert.Contains("&amp;", r.Didl);
        Assert.Contains("&lt;", r.Didl);

        var res = items[0].Elements().First(e => e.Name.LocalName == "res");
        Assert.StartsWith($"{Base}/dlna/media/", res.Value);
        Assert.Contains("http-get:*:", res.Attribute("protocolInfo")!.Value);
        Assert.Contains("videoItem", items[0].Elements().First(e => e.Name.LocalName == "class").Value);
    }

    [Fact]
    public async Task TvLibrary_ListsSeriesContainers_ThenEpisodesInOrder()
    {
        using var db = NewDb();
        var lib = await Cd(db).BrowseAsync($"L:{_tvLib}", false, 0, 0, Base, default);
        var series = Assert.Single(Children(lib.Didl, "container"));
        Assert.Equal($"S:{_series}", series.Attribute("id")!.Value);

        var eps = await Cd(db).BrowseAsync($"S:{_series}", false, 0, 0, Base, default);
        var items = Children(eps.Didl, "item");
        Assert.Equal(new[] { "E1", "E2" }, items.Select(Title)); // ordered by episode number
    }

    [Fact]
    public async Task MusicLibrary_ListsAlbums_ThenTracksInOrder()
    {
        using var db = NewDb();
        var lib = await Cd(db).BrowseAsync($"L:{_musicLib}", false, 0, 0, Base, default);
        var album = Assert.Single(Children(lib.Didl, "container"));
        Assert.Equal($"A:{_album}", album.Attribute("id")!.Value);

        var tracks = await Cd(db).BrowseAsync($"A:{_album}", false, 0, 0, Base, default);
        var items = Children(tracks.Didl, "item");
        Assert.Equal(new[] { "Track A", "Track B" }, items.Select(Title)); // ordered by track number
        Assert.Contains("musicTrack", items[0].Elements().First(e => e.Name.LocalName == "class").Value);
    }

    [Fact]
    public async Task Paging_LimitsReturned_ButReportsTotal()
    {
        using var db = NewDb();
        var r = await Cd(db).BrowseAsync($"L:{_movieLib}", false, startingIndex: 0, requestedCount: 1, Base, default);
        Assert.Equal(1, r.NumberReturned);
        Assert.Equal(2, r.TotalMatches);
        Assert.Single(Children(r.Didl, "item"));
    }

    [Fact]
    public async Task BrowseMetadata_OnRoot_ReturnsSingleRootContainer()
    {
        using var db = NewDb();
        var r = await Cd(db).BrowseAsync("0", metadata: true, 0, 0, Base, default);
        var container = Assert.Single(Children(r.Didl, "container"));
        Assert.Equal("0", container.Attribute("id")!.Value);
        Assert.Equal("SoftMedia", Title(container));
        Assert.Equal(1, r.NumberReturned);
    }

    // --- Audit M7/L9: admin-scoped library allow-set --------------------------

    [Fact]
    public async Task Root_OnlyListsExposedLibraries()
    {
        using var db = NewDb();
        // Expose ONLY the movie library; Shows + Music must not appear.
        var r = await CdExposing(db, _movieLib).BrowseAsync("0", false, 0, 0, Base, default);

        var containers = Children(r.Didl, "container");
        Assert.Equal(1, r.TotalMatches);
        Assert.Equal(new[] { "Movies" }, containers.Select(Title));
    }

    [Fact]
    public async Task EmptyAllowSet_ExposesNothing()
    {
        using var db = NewDb();
        // Default secure posture: nothing exposed even though AV libraries exist.
        var r = await CdExposing(db).BrowseAsync("0", false, 0, 0, Base, default);
        Assert.Equal(0, r.TotalMatches);
        Assert.Empty(Children(r.Didl, "container"));
    }

    [Fact]
    public async Task NonExposedLibrary_BrowsedDirectly_IsEmpty()
    {
        using var db = NewDb();
        // Only the movie library is exposed; browsing the TV library by id yields nothing,
        // and its episodes are not reachable by guessing the series id.
        var lib = await CdExposing(db, _movieLib).BrowseAsync($"L:{_tvLib}", false, 0, 0, Base, default);
        Assert.Empty(Children(lib.Didl, "container"));
        Assert.Empty(Children(lib.Didl, "item"));

        var eps = await CdExposing(db, _movieLib).BrowseAsync($"S:{_series}", false, 0, 0, Base, default);
        Assert.Empty(Children(eps.Didl, "item"));
    }
}
