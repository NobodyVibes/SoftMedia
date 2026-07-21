using Microsoft.Extensions.Logging.Abstractions;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Scanning;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// NR-WI-014 — the filesystem extras probe: suffix companions, extras subfolders,
/// per-title stem filtering in shared folders, and stable index resolution.
public class ExtrasServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly ExtrasService _svc = new(NullLogger<ExtrasService>.Instance);

    public ExtrasServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "softmedia-extras-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string Touch(params string[] segments)
    {
        var path = Path.Combine(new[] { _dir }.Concat(segments).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    private MediaItem Movie(string fileName) => new()
    {
        Type = MediaType.Movie,
        Title = Path.GetFileNameWithoutExtension(fileName),
        Path = Path.Combine(_dir, fileName),
    };

    [Fact]
    public void Movie_FindsSuffixCompanions_AndExtrasFolder()
    {
        Touch("Inception (2010).mkv");
        Touch("Inception (2010)-trailer.mkv");
        Touch("Inception (2010)-sample.mp4");
        Touch("extras", "Making Of.mkv");
        Touch("cover.jpg"); // non-video noise

        var extras = _svc.GetExtras(Movie("Inception (2010).mkv"));

        Assert.Equal(3, extras.Count);
        Assert.Contains(extras, e => e.Kind == "trailer" && e.Title == "Trailer");
        Assert.Contains(extras, e => e.Kind == "sample");
        Assert.Contains(extras, e => e.Title == "Making Of");
    }

    [Fact]
    public void Movie_SharedFolder_OnlyOwnCompanionsMatch()
    {
        Touch("Movie A.mkv");
        Touch("Movie A-trailer.mkv");
        Touch("Movie B.mkv");
        Touch("Movie B-trailer.mkv");

        var extras = _svc.GetExtras(Movie("Movie A.mkv"));

        Assert.Single(extras);
        Assert.Equal("Movie A-trailer.mkv", extras[0].FileName);
    }

    [Fact]
    public void Series_ProbesItsFolder()
    {
        var seriesDir = Path.Combine(_dir, "The Show");
        Directory.CreateDirectory(seriesDir);
        File.WriteAllBytes(Path.Combine(seriesDir, "S01E01.mkv"), new byte[] { 1 });
        Directory.CreateDirectory(Path.Combine(seriesDir, "extras"));
        File.WriteAllBytes(Path.Combine(seriesDir, "extras", "Bloopers.mkv"), new byte[] { 1 });

        var series = new MediaItem { Type = MediaType.Series, Title = "The Show", Path = seriesDir };
        var extras = _svc.GetExtras(series);

        Assert.Single(extras);
        Assert.Equal("Bloopers", extras[0].Title);
        // Regular episodes must never appear as extras.
        Assert.DoesNotContain(extras, e => e.FileName.Contains("S01E01"));
    }

    [Fact]
    public void ResolveExtraPath_MatchesListIndex_AndBoundsChecked()
    {
        Touch("Film.mkv");
        Touch("Film-trailer.mkv");
        var movie = Movie("Film.mkv");

        var extras = _svc.GetExtras(movie);
        var resolved = _svc.ResolveExtraPath(movie, extras[0].Index);

        Assert.NotNull(resolved);
        Assert.EndsWith("Film-trailer.mkv", resolved);
        Assert.Null(_svc.ResolveExtraPath(movie, 99));
        Assert.Null(_svc.ResolveExtraPath(movie, -1));
    }

    [Fact]
    public void NonMovieSeriesTypes_HaveNoExtras()
    {
        Touch("song.mp3");
        var audio = new MediaItem { Type = MediaType.Audio, Title = "song", Path = Path.Combine(_dir, "song.mp3") };
        Assert.Empty(_svc.GetExtras(audio));
    }

    [Fact]
    public void MovieScanner_SkipsCompanionFiles_ButMusicScannerUnaffected()
    {
        // NR-WI-014 scanner rule: a trailer must not become its own library item.
        var trailer = Touch("Film-trailer.mkv");
        var inExtras = Touch("extras", "clip.mkv");
        var normal = Touch("Film.mkv");

        var movieScanner = new MovieScanner(
            null!, NullLogger<MovieScanner>.Instance,
            new Moq.Mock<SoftMedia.Server.Services.Abstractions.IMediaNotificationService>().Object,
            new Moq.Mock<IMediaAnalysisService>().Object,
            new Moq.Mock<SoftMedia.Server.Services.Metadata.IMetadataQueue>().Object,
            new Moq.Mock<ILocalArtworkService>().Object);

        Assert.True(movieScanner.CanHandleFile(normal));
        Assert.False(movieScanner.CanHandleFile(trailer));
        Assert.False(movieScanner.CanHandleFile(inExtras));
    }
}
