// SM-WI-001 — real-library fixture manifest harvester.
//
// Reads the live SoftMedia SQLite database READ-ONLY and emits
// src/SoftMedia.Server.Tests/Fixtures/real-library-manifest.json: real file names from
// the operator's Movies / TV / Music / Books libraries, with expected parse results
// snapshotted by calling the production FileNameParser (so the SM-WI-002 corpus locks
// CURRENT behavior — a wrong current parse is corrected in the manifest by hand and
// noted, never silently locked).
//
// Usage: dotnet run --project tools/FixtureHarvester -- <path-to-softmedia.db> <out.json>
// Names only — no media content. Paths are stored relative to the library root so the
// manifest carries no drive letters.

using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SoftMedia.Server.Helpers;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: FixtureHarvester <softmedia.db> <out.json>");
    return 1;
}

var dbPath = Path.GetFullPath(args[0]);
var outPath = Path.GetFullPath(args[1]);
if (!File.Exists(dbPath))
{
    Console.Error.WriteLine($"Database not found: {dbPath}");
    return 1;
}

SQLitePCL.Batteries_V2.Init();

await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
await conn.OpenAsync();

// ── Libraries ────────────────────────────────────────────────────────────────
var libraries = new List<Lib>();
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id, Name, Type, Paths FROM Libraries ORDER BY \"Order\"";
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        var paths = JsonSerializer.Deserialize<List<string>>(r.GetString(3)) ?? new();
        libraries.Add(new Lib(r.GetString(0), r.GetString(1), (LibraryType)r.GetInt32(2), paths));
    }
}

// ── Media items (paths + identity, nothing else) ─────────────────────────────
var items = new List<Item>();
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT LibraryId, Type, Path, Title, Year, SeriesId, SeasonNumber, EpisodeNumber, TrackNumber, Id
        FROM MediaItems WHERE IsMissing = 0
        """;
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        items.Add(new Item(
            LibraryId: r.GetString(0),
            Type: r.GetInt32(1),
            Path: r.GetString(2),
            Title: r.GetString(3),
            Year: r.IsDBNull(4) ? null : r.GetInt32(4),
            SeriesId: r.IsDBNull(5) ? null : r.GetString(5),
            SeasonNumber: r.IsDBNull(6) ? null : r.GetInt32(6),
            EpisodeNumber: r.IsDBNull(7) ? null : r.GetInt32(7),
            TrackNumber: r.IsDBNull(8) ? null : r.GetInt32(8),
            Id: r.GetString(9)));
    }
}

Console.WriteLine($"Loaded {libraries.Count} libraries, {items.Count} media items.");
foreach (var lib in libraries)
{
    // Root paths help tools/New-LiveVerifySandbox.ps1 point at the right sources.
    Console.WriteLine($"  [{lib.Type}] '{lib.Name}' roots: {string.Join("; ", lib.Paths)}");
}

// MediaType enum values (mirror Models/MediaItem.cs — a tool-local copy would drift,
// but the project reference makes the real enum available; ints used in SQL above).
const int TMovie = 0, TSeries = 1, TEpisode = 2, TAudio = 3, TBook = 4;
const int TComicSeries = 10, TComicIssue = 11;

string Rel(Lib lib, string full)
{
    foreach (var root in lib.Paths)
    {
        var normRoot = root.TrimEnd('\\', '/');
        if (full.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
            return full[normRoot.Length..].TrimStart('\\', '/').Replace('\\', '/');
    }
    // Root not matched (moved library?): keep at most the last 3 segments — never a drive.
    var parts = full.Replace('\\', '/').Split('/');
    return string.Join('/', parts.TakeLast(Math.Min(3, parts.Length)));
}

static List<string> FlagsFor(string name, int? parsedYear)
{
    var flags = new List<string>();
    if (parsedYear == null) flags.Add("yearless");
    if (name.Contains('\'') || name.Contains('"')) flags.Add("quote");
    if (name.Any(c => c > 127)) flags.Add("nonAscii");
    return flags;
}

var manifest = new Dictionary<string, object?>
{
    ["_note"] = "SM-WI-001 real-library fixture manifest. Harvested from the operator's live " +
                "libraries; names only. 'expected' blocks are snapshots of the production " +
                "FileNameParser at harvest time — corrections are made by hand with a '_corrected' " +
                "marker, never regenerated blindly.",
    ["generatedUtc"] = DateTime.UtcNow.ToString("O"),
    ["libraries"] = new List<object>(),
};
var outLibs = (List<object>)manifest["libraries"]!;

foreach (var lib in libraries)
{
    var libItems = items.Where(i => i.LibraryId == lib.Id).ToList();
    if (libItems.Count == 0) continue;

    switch (lib.Type)
    {
        case LibraryType.Movie:
        {
            var movies = libItems.Where(i => i.Type == TMovie).OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();
            // Variety first: every yearless/quote/nonAscii parse, then fill to 150 by even sampling.
            var entries = new List<object>();
            var interesting = new List<Item>();
            var plain = new List<Item>();
            foreach (var m in movies)
            {
                var (t, y) = FileNameParser.ParseMovie(Path.GetFileName(m.Path));
                if (y == null || m.Path.Contains('\'') || m.Path.Any(c => c > 127)) interesting.Add(m);
                else plain.Add(m);
            }
            var chosen = interesting.Concat(SampleEvenly(plain, Math.Max(0, 150 - interesting.Count))).ToList();
            foreach (var m in chosen)
            {
                var fileName = Path.GetFileName(m.Path);
                var (title, year) = FileNameParser.ParseMovie(fileName);
                entries.Add(new
                {
                    relativePath = Rel(lib, m.Path),
                    expected = new { title, year },
                    flags = FlagsFor(fileName, year),
                });
            }
            outLibs.Add(new { name = lib.Name, type = "Movie", totalInLibrary = movies.Count, entries });
            Console.WriteLine($"  Movie '{lib.Name}': {entries.Count}/{movies.Count} entries ({interesting.Count} interesting)");
            break;
        }

        case LibraryType.TV:
        {
            var seriesRows = libItems.Where(i => i.Type == TSeries).ToDictionary(s => s.Id);
            var episodes = libItems.Where(i => i.Type == TEpisode && i.SeriesId != null).ToList();
            // Pick up to 3 series: the largest, one with specials (season 0) if any, one more.
            var bySeries = episodes.GroupBy(e => e.SeriesId!).OrderByDescending(g => g.Count()).ToList();
            var picked = new List<IGrouping<string, Item>>();
            if (bySeries.Count > 0) picked.Add(bySeries[0]);
            var withSpecials = bySeries.FirstOrDefault(g => g.Any(e => e.SeasonNumber == 0) && !picked.Contains(g));
            if (withSpecials != null) picked.Add(withSpecials);
            foreach (var g in bySeries)
            {
                if (picked.Count >= 3) break;
                if (!picked.Contains(g)) picked.Add(g);
            }

            var seriesOut = new List<object>();
            foreach (var g in picked)
            {
                var seriesTitle = seriesRows.TryGetValue(g.Key, out var s) ? s.Title : "?";
                var eps = g.OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).Take(100).Select(e =>
                {
                    var fileName = Path.GetFileName(e.Path);
                    var (show, season, episode, epTitle) = FileNameParser.ParseTvEpisode(fileName);
                    return (object)new
                    {
                        relativePath = Rel(lib, e.Path),
                        expected = new { show, season, episode, episodeTitle = epTitle },
                        flags = FlagsFor(fileName, 0),
                    };
                }).ToList();
                seriesOut.Add(new { series = seriesTitle, episodeCount = g.Count(), entries = eps });
            }
            outLibs.Add(new { name = lib.Name, type = "TV", totalSeries = seriesRows.Count, series = seriesOut });
            Console.WriteLine($"  TV '{lib.Name}': {picked.Count} series of {seriesRows.Count}");
            break;
        }

        case LibraryType.Music:
        {
            // Structure matters more than filename parsing for music (tags drive identity):
            // record artist/album/track TREES so scanner tests can rebuild real layouts.
            var tracks = libItems.Where(i => i.Type == TAudio).ToList();
            var artistOut = new List<object>();
            // Structure-from-path: group tracks by album folder, then album folders by
            // artist folder; prefer artists with ≥2 albums (SM-WI-003 sandbox needs them).
            var albumFolders = tracks.GroupBy(t => Path.GetDirectoryName(t.Path) ?? "")
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
            var artistFolders = albumFolders.GroupBy(g => Path.GetDirectoryName(g.Key) ?? "")
                .Where(a => a.Count() >= 2).Take(2)
                .Concat(albumFolders.GroupBy(g => Path.GetDirectoryName(g.Key) ?? "").Take(1))
                .DistinctBy(a => a.Key).Take(3).ToList();
            foreach (var artist in artistFolders)
            {
                var albumsOut = artist.Select(albumGroup => (object)new
                {
                    albumFolder = Rel(lib, albumGroup.Key),
                    tracks = albumGroup.OrderBy(t => t.TrackNumber).Take(12)
                        .Select(t => Rel(lib, t.Path)).ToList(),
                }).ToList();
                artistOut.Add(new { artistFolder = Rel(lib, artist.Key), albums = albumsOut });
            }
            outLibs.Add(new { name = lib.Name, type = "Music", totalTracks = tracks.Count, artists = artistOut });
            Console.WriteLine($"  Music '{lib.Name}': {artistOut.Count} artist trees of {tracks.Count} tracks total");
            break;
        }

        case LibraryType.Book:
        {
            var books = libItems.Where(i => i.Type is TBook or TComicIssue).OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();
            var chosen2 = SampleEvenly(books, 30);
            var entries = chosen2.Select(b => (object)new
            {
                relativePath = Rel(lib, b.Path),
                kind = b.Type == TComicIssue ? "comicIssue" : "book",
                flags = FlagsFor(Path.GetFileName(b.Path), 0),
            }).ToList();
            outLibs.Add(new { name = lib.Name, type = "Book", totalInLibrary = books.Count, entries });
            Console.WriteLine($"  Book '{lib.Name}': {entries.Count}/{books.Count} entries");
            break;
        }

        default:
            Console.WriteLine($"  Skipping '{lib.Name}' ({lib.Type}) — not in the manifest scope.");
            break;
    }
}

Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
});
await File.WriteAllTextAsync(outPath, json);
Console.WriteLine($"Wrote {outPath} ({json.Length:N0} chars).");
return 0;

static List<Item> SampleEvenly(List<Item> source, int count)
{
    if (source.Count <= count) return source;
    var result = new List<Item>(count);
    for (int i = 0; i < count; i++)
        result.Add(source[(int)((long)i * source.Count / count)]);
    return result;
}

enum LibraryType { Movie, TV, Music, Book, Game, Photo }

record Lib(string Id, string Name, LibraryType Type, List<string> Paths);

record Item(string LibraryId, int Type, string Path, string Title, int? Year,
    string? SeriesId, int? SeasonNumber, int? EpisodeNumber, int? TrackNumber, string Id);
