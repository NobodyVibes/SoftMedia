using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Metadata;
using SoftMedia.Server.Services.Scanning;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Scanning;

/// <summary>
/// Verifies the unchanged-file fast paths: rescans of files with identical size + mtime
/// must not re-probe (movies) or re-open with TagLib (music), while changed files get a
/// full re-analysis.
/// </summary>
public class ScannerUnchangedFileTests
{
    private static readonly DateTime Mtime = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IServiceScopeFactory> _scopeFactory = new();
    private readonly Mock<IMediaNotificationService> _notification = new();
    private readonly Mock<IMetadataQueue> _queue = new();
    private readonly Mock<IMediaAnalysisService> _analysis = new();
    private readonly AppDbContext _db;

    public ScannerUnchangedFileTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
    }

    private sealed class TestableMovieScanner : MovieScanner
    {
        public TestableMovieScanner(
            IServiceScopeFactory scopeFactory, ILogger<MovieScanner> logger,
            IMediaNotificationService notification, IMediaAnalysisService analysis,
            IMetadataQueue queue, ILocalArtworkService artwork)
            : base(scopeFactory, logger, notification, analysis, queue, artwork) { }

        public Task<ScanOperationResult> ProcessAsync(
            AppDbContext context, FileDiscoveryResult file, MediaItem? existing, Library library)
            => base.ProcessFileAsync(context, file, existing, library, CancellationToken.None);
    }

    private sealed class TestableMusicScanner : MusicScanner
    {
        public TestableMusicScanner(
            IServiceScopeFactory scopeFactory, ILogger<MusicScanner> logger,
            IMediaNotificationService notification, IMediaAnalysisService analysis,
            IMetadataQueue queue, IWebHostEnvironment env)
            : base(scopeFactory, logger, notification, analysis, queue, env) { }

        public Task<ScanOperationResult> ProcessAsync(
            AppDbContext context, FileDiscoveryResult file, MediaItem? existing, Library library)
            => base.ProcessFileAsync(context, file, existing, library, CancellationToken.None);
    }

    private TestableMovieScanner CreateMovieScanner(out List<MetadataRefreshMode> capturedModes)
    {
        var modes = new List<MetadataRefreshMode>();
        _analysis
            .Setup(a => a.AnalyzeAsync(It.IsAny<MediaItem>(), It.IsAny<string>(), It.IsAny<MetadataRefreshMode>(), It.IsAny<CancellationToken>()))
            .Callback<MediaItem, string, MetadataRefreshMode, CancellationToken>((_, _, m, _) => modes.Add(m))
            .ReturnsAsync(false);

        var artwork = new Mock<ILocalArtworkService>();
        artwork
            .Setup(a => a.ApplyLocalArtworkAsync(It.IsAny<MediaItem>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(new LocalArtworkResult(false, false));

        capturedModes = modes;
        return new TestableMovieScanner(
            _scopeFactory.Object, new Mock<ILogger<MovieScanner>>().Object,
            _notification.Object, _analysis.Object, _queue.Object, artwork.Object);
    }

    private static MediaItem ExistingMovie(Library library, string path) => new()
    {
        Id = Guid.NewGuid(),
        LibraryId = library.Id,
        Title = "Dune",
        Type = MediaType.Movie,
        Path = path,
        Size = 100,
        DateModified = Mtime,
        PosterUrl = "http://example.com/poster.jpg" // relaxed policy: complete, no enrichment
    };

    [Fact]
    public async Task Movie_UnchangedFile_UsesMissingMode_AndReportsSkipped()
    {
        var scanner = CreateMovieScanner(out var modes);
        var library = new Library { Id = Guid.NewGuid(), Name = "Movies", Type = LibraryType.Movie };
        var path = @"C:\movies\Dune (2021)\Dune.2021.mkv";
        var existing = ExistingMovie(library, path);

        var result = await scanner.ProcessAsync(_db, new FileDiscoveryResult(path, 100, Mtime), existing, library);

        Assert.Equal(ScanResult.Skipped, result.Result);
        Assert.False(result.EnqueueMetadata);
        Assert.Equal(new[] { MetadataRefreshMode.Missing }, modes);
    }

    [Fact]
    public async Task Movie_ChangedFile_ForcesFullReprobe_AndReportsUpdated()
    {
        var scanner = CreateMovieScanner(out var modes);
        var library = new Library { Id = Guid.NewGuid(), Name = "Movies", Type = LibraryType.Movie };
        var path = @"C:\movies\Dune (2021)\Dune.2021.mkv";
        var existing = ExistingMovie(library, path);

        var result = await scanner.ProcessAsync(_db, new FileDiscoveryResult(path, 999, Mtime), existing, library);

        Assert.Equal(ScanResult.Updated, result.Result);
        Assert.Equal(new[] { MetadataRefreshMode.Full }, modes);
    }

    [Fact]
    public async Task Movie_NewFile_UsesFullMode()
    {
        var scanner = CreateMovieScanner(out var modes);
        var library = new Library { Id = Guid.NewGuid(), Name = "Movies", Type = LibraryType.Movie };
        var path = @"C:\movies\Dune (2021)\Dune.2021.mkv";

        var result = await scanner.ProcessAsync(_db, new FileDiscoveryResult(path, 100, Mtime), null, library);

        Assert.Equal(ScanResult.New, result.Result);
        Assert.Equal(new[] { MetadataRefreshMode.Full }, modes);
    }

    [Fact]
    public async Task Music_UnchangedFile_SkipsWithoutOpeningFile()
    {
        var env = new Mock<IWebHostEnvironment>();
        var scanner = new TestableMusicScanner(
            _scopeFactory.Object, new Mock<ILogger<MusicScanner>>().Object,
            _notification.Object, _analysis.Object, _queue.Object, env.Object);

        var library = new Library { Id = Guid.NewGuid(), Name = "Music", Type = LibraryType.Music };
        // Path deliberately does not exist: if the fast path regressed and TagLib ran,
        // the catch fallback would return ItemId == Guid.Empty instead of the item's id.
        var path = @"C:\music\does-not-exist\track.mp3";
        var existing = new MediaItem
        {
            Id = Guid.NewGuid(),
            LibraryId = library.Id,
            Title = "Track",
            Type = MediaType.Audio,
            Path = path,
            Size = 100,
            DateModified = Mtime
        };

        var result = await scanner.ProcessAsync(_db, new FileDiscoveryResult(path, 100, Mtime), existing, library);

        Assert.Equal(ScanResult.Skipped, result.Result);
        Assert.Equal(existing.Id, result.ItemId);
    }
}
