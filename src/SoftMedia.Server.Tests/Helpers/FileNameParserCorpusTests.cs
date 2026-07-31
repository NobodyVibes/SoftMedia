using System.Text.Json;
using SoftMedia.Server.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Helpers;

/// <summary>
/// SM-WI-002 — parser regression corpus over REAL file names from the operator's
/// libraries (Fixtures/real-library-manifest.json, harvested by tools/FixtureHarvester).
/// The manifest's "expected" blocks are snapshots of the production parser at harvest
/// time: these tests are a tripwire for unintended parser behavior changes, not a spec.
/// If a parser change is INTENTIONAL, regenerate or hand-edit the manifest in the same
/// commit and say so; a hand-corrected expectation carries a "_corrected" marker and must
/// never be blindly regenerated over.
/// </summary>
public class FileNameParserCorpusTests
{
    private static readonly Lazy<JsonDocument> Manifest = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "real-library-manifest.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    });

    public static TheoryData<string, string, int?> MovieCases()
    {
        var data = new TheoryData<string, string, int?>();
        foreach (var lib in Manifest.Value.RootElement.GetProperty("libraries").EnumerateArray())
        {
            if (lib.GetProperty("type").GetString() != "Movie") continue;
            foreach (var entry in lib.GetProperty("entries").EnumerateArray())
            {
                var expected = entry.GetProperty("expected");
                data.Add(
                    entry.GetProperty("relativePath").GetString()!,
                    expected.GetProperty("title").GetString()!,
                    expected.GetProperty("year").ValueKind == JsonValueKind.Null
                        ? null : expected.GetProperty("year").GetInt32());
            }
        }
        return data;
    }

    public static TheoryData<string, string, int, int, string> TvCases()
    {
        var data = new TheoryData<string, string, int, int, string>();
        foreach (var lib in Manifest.Value.RootElement.GetProperty("libraries").EnumerateArray())
        {
            if (lib.GetProperty("type").GetString() != "TV") continue;
            foreach (var series in lib.GetProperty("series").EnumerateArray())
            {
                foreach (var entry in series.GetProperty("entries").EnumerateArray())
                {
                    var expected = entry.GetProperty("expected");
                    data.Add(
                        entry.GetProperty("relativePath").GetString()!,
                        expected.GetProperty("show").GetString()!,
                        expected.GetProperty("season").GetInt32(),
                        expected.GetProperty("episode").GetInt32(),
                        expected.GetProperty("episodeTitle").GetString()!);
                }
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(MovieCases))]
    public void ParseMovie_matches_manifest_snapshot(string relativePath, string expectedTitle, int? expectedYear)
    {
        var fileName = Path.GetFileName(relativePath.Replace('/', Path.DirectorySeparatorChar));

        var (title, year) = FileNameParser.ParseMovie(fileName);

        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedYear, year);
    }

    [Theory]
    [MemberData(nameof(TvCases))]
    public void ParseTvEpisode_matches_manifest_snapshot(
        string relativePath, string expectedShow, int expectedSeason, int expectedEpisode, string expectedEpisodeTitle)
    {
        var fileName = Path.GetFileName(relativePath.Replace('/', Path.DirectorySeparatorChar));

        var (show, season, episode, episodeTitle) = FileNameParser.ParseTvEpisode(fileName);

        Assert.Equal(expectedShow, show);
        Assert.Equal(expectedSeason, season);
        Assert.Equal(expectedEpisode, episode);
        Assert.Equal(expectedEpisodeTitle, episodeTitle);
    }

    [Fact]
    public void Manifest_covers_all_four_library_types()
    {
        var types = Manifest.Value.RootElement.GetProperty("libraries").EnumerateArray()
            .Select(l => l.GetProperty("type").GetString())
            .ToHashSet();

        Assert.Superset(new HashSet<string?> { "Movie", "TV", "Music", "Book" }, types);
    }
}
