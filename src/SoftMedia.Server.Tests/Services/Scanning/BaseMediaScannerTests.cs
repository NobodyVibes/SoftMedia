using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Scanning;
using SoftMedia.Server.Services.Metadata;
using SoftMedia.Server.Services.Infrastructure;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Scanning;

public class BaseMediaScannerTests
{
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<ILogger<TestMediaScanner>> _mockLogger;
    private readonly Mock<IMediaNotificationService> _mockNotificationService;
    private readonly Mock<IMetadataQueue> _mockMetadataQueue;
    private readonly Mock<ISettingsService> _mockSettingsService;
    private readonly Mock<INotificationService> _mockSystemNotifications;
    private readonly string _dbName;

    public BaseMediaScannerTests()
    {
        _dbName = Guid.NewGuid().ToString();

        // Setup Service Scope Mocking
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockLogger = new Mock<ILogger<TestMediaScanner>>();
        _mockNotificationService = new Mock<IMediaNotificationService>();
        _mockMetadataQueue = new Mock<IMetadataQueue>();
        _mockSettingsService = new Mock<ISettingsService>();
        _mockSystemNotifications = new Mock<INotificationService>();

        _mockSettingsService.Setup(x => x.GetSettingAsync("MetadataEnrichmentMode", "Relaxed"))
                            .ReturnsAsync("Relaxed");

        // Setup CreateScope to return a NEW scope with a NEW context each time
        _mockScopeFactory.Setup(x => x.CreateScope()).Returns(() => {
            var scope = new Mock<IServiceScope>();
            var serviceProvider = new Mock<IServiceProvider>();
            
            // Create a NEW context instance for this scope
            var context = CreateNewContext();
            
            serviceProvider.Setup(x => x.GetService(typeof(AppDbContext))).Returns(context);
            serviceProvider.Setup(x => x.GetService(typeof(ISettingsService))).Returns(_mockSettingsService.Object);
            serviceProvider.Setup(x => x.GetService(typeof(INotificationService))).Returns(_mockSystemNotifications.Object);
            scope.Setup(x => x.ServiceProvider).Returns(serviceProvider.Object);
            
            return scope.Object;
        });
    }

    private AppDbContext CreateNewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: _dbName)
            .Options;
        return new AppDbContext(options);
    }

    private TestMediaScanner CreateScanner()
    {
        return new TestMediaScanner(
            _mockScopeFactory.Object,
            _mockLogger.Object,
            _mockNotificationService.Object,
            _mockMetadataQueue.Object);
    }

    /// <summary>SR-WI-011: retention 0 = legacy immediate hard delete; default (30) = soft delete.</summary>
    private void SetMissingRetentionDays(string value) =>
        _mockSettingsService.Setup(x => x.GetSettingAsync("MissingItemRetentionDays", "30"))
                            .ReturnsAsync(value);

    [Fact]
    public async Task ScanLibraryAsync_ProcessesAllFiles_InSingleDirectory()
    {
        // Arrange
        var scanner = CreateScanner();
        var libId = Guid.NewGuid();
        var library = new Library { Id = libId, Name = "TestLib", Paths = new List<string> { "/root" } };
        
        scanner.VirtualFileSystem.Add("/root", new List<string> { "/root/file1.mkv", "/root/file2.mp4" });

        // Act
        await scanner.ScanLibraryAsync(library);

        // Assert
        Assert.Equal(2, scanner.ProcessedFiles.Count);
        Assert.Contains("/root/file1.mkv", scanner.ProcessedFiles);
        Assert.Contains("/root/file2.mp4", scanner.ProcessedFiles);
    }

    [Fact]
    public async Task ScanLibraryAsync_ProcessesDirectoriesInParallel()
    {
        // Arrange
        var scanner = CreateScanner();
        var libId = Guid.NewGuid();
        var library = new Library { Id = libId, Name = "TestLib", Paths = new List<string> { "/root" } };
        
        // Create 10 directories with 1 file each
        for (int i = 0; i < 10; i++)
        {
            scanner.VirtualFileSystem.Add($"/root/dir{i}", new List<string> { $"/root/dir{i}/file.mkv" });
        }

        scanner.SimulateWorkDelayMs = 50; // Add delay to force potential overlap

        // Act
        await scanner.ScanLibraryAsync(library);

        // Assert
        Assert.Equal(10, scanner.ProcessedFiles.Count);

        // SM-WI-050: the parallel unit is a bounded BATCH, not a directory — these 10
        // one-file directories pack into a single batch (one scope), plus the settings,
        // bulk-load and cleanup scopes. Parallelism across many batches is asserted by
        // ScanBatchingTests; here we only require every file processed and ≥3 scopes.
        _mockScopeFactory.Verify(x => x.CreateScope(), Times.AtLeast(3));
    }

    [Fact]
    public async Task ScanContext_ReadsStrictEnrichment_FromSettingsOnce()
    {
        // Arrange
        _mockSettingsService.Setup(x => x.GetSettingAsync("MetadataEnrichmentMode", "Relaxed"))
                            .ReturnsAsync("Strict");

        var scanner = CreateScanner();
        var library = new Library { Id = Guid.NewGuid(), Name = "TestLib", Paths = new List<string> { "/root" } };
        scanner.VirtualFileSystem.Add("/root", new List<string> { "/root/file1.mkv", "/root/file2.mp4", "/root/file3.mkv" });

        // Act
        await scanner.ScanLibraryAsync(library);

        // Assert
        Assert.True(scanner.IsStrictEnrichment);
        
        // Ensure it's read exactly once, regardless of how many directories/files
        _mockSettingsService.Verify(x => x.GetSettingAsync("MetadataEnrichmentMode", "Relaxed"), Times.Once);
    }

    /// <summary>Synchronous progress capture (Progress&lt;T&gt; posts async and can miss reports).</summary>
    private sealed class CapturingProgress : IProgress<ScanProgress>
    {
        public readonly List<ScanProgress> Reports = new();
        public void Report(ScanProgress value) { lock (Reports) Reports.Add(value); }
    }

    [Fact]
    public async Task ScanLibraryAsync_ReportsExactTotals_AndDiscoveryStage()
    {
        var scanner = CreateScanner();
        var library = new Library { Id = Guid.NewGuid(), Name = "TestLib", Paths = new List<string> { "/root" } };
        for (int i = 0; i < 3; i++)
            scanner.VirtualFileSystem.Add($"/root/dir{i}", new List<string> { $"/root/dir{i}/a.mkv", $"/root/dir{i}/b.mp4" });

        var progress = new CapturingProgress();
        await scanner.ScanLibraryAsync(library, progress);

        Assert.Equal(LibraryScanStage.Discovery, progress.Reports.First().Stage);

        var final = progress.Reports.Last();
        Assert.Equal("Complete", final.CurrentPhase);
        Assert.Equal(6, final.ProcessedCount);
        Assert.Equal(6, final.TotalCount);
        Assert.Equal(6, final.NewCount);
        Assert.Equal(0, final.ErrorCount);
        Assert.Equal(LibraryScanStage.Finishing, final.Stage);
    }

    [Fact]
    public async Task ScanLibraryAsync_CountsPerFileErrors()
    {
        var scanner = CreateScanner();
        var library = new Library { Id = Guid.NewGuid(), Name = "TestLib", Paths = new List<string> { "/root" } };
        scanner.VirtualFileSystem.Add("/root", new List<string>
        {
            "/root/good1.mkv", "/root/bad.mkv", "/root/good2.mkv"
        });
        scanner.ThrowOnFile = path => path.Contains("bad");

        var progress = new CapturingProgress();
        await scanner.ScanLibraryAsync(library, progress);

        var final = progress.Reports.Last();
        Assert.Equal(3, final.ProcessedCount); // errored file still counts as processed
        Assert.Equal(1, final.ErrorCount);
        Assert.Equal(2, final.NewCount);
    }

    [Fact]
    public async Task ScanLibraryAsync_RemovesOrphans_ByPathSetDifference_PreservingContainers()
    {
        SetMissingRetentionDays("0"); // legacy immediate-delete semantics under test
        var scanner = CreateScanner();
        var libId = Guid.NewGuid();
        var library = new Library { Id = libId, Name = "TestLib", Paths = new List<string> { "/root" } };

        // Seed: one file still on disk, one whose file vanished, and a folder-pathed
        // container (ComicSeries) that must never be treated as an orphan.
        using (var seed = CreateNewContext())
        {
            seed.MediaItems.AddRange(
                new MediaItem { Id = Guid.NewGuid(), LibraryId = libId, Title = "Stays", Type = MediaType.Movie, Path = "/root/stays.mkv" },
                new MediaItem { Id = Guid.NewGuid(), LibraryId = libId, Title = "Gone", Type = MediaType.Movie, Path = "/root/gone.mkv" },
                new MediaItem { Id = Guid.NewGuid(), LibraryId = libId, Title = "Comics", Type = MediaType.ComicSeries, Path = "/root" });
            await seed.SaveChangesAsync();
        }

        scanner.VirtualFileSystem.Add("/root", new List<string> { "/root/stays.mkv" });

        await scanner.ScanLibraryAsync(library);

        using var verify = CreateNewContext();
        var remaining = await verify.MediaItems.Select(m => m.Title).ToListAsync();
        Assert.Contains("Stays", remaining);
        Assert.Contains("Comics", remaining);
        Assert.DoesNotContain("Gone", remaining);
    }

    [Fact]
    public async Task ScanLibraryAsync_AllRootsMissing_FailsWithoutPurging()
    {
        var scanner = CreateScanner();
        var libId = Guid.NewGuid();
        var library = new Library { Id = libId, Name = "TestLib", Paths = new List<string> { "/root" } };
        scanner.MissingRoots.Add("/root");

        using (var seed = CreateNewContext())
        {
            seed.MediaItems.Add(new MediaItem
            {
                Id = Guid.NewGuid(), LibraryId = libId, Title = "Survivor",
                Type = MediaType.Movie, Path = "/root/survivor.mkv"
            });
            await seed.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => scanner.ScanLibraryAsync(library));

        using var verify = CreateNewContext();
        Assert.Equal(1, await verify.MediaItems.CountAsync());
    }

    [Fact]
    public async Task ScanLibraryAsync_PartialRootMissing_PreservesItemsUnderIt_StillPurgesLiveRoots()
    {
        SetMissingRetentionDays("0"); // legacy immediate-delete semantics under test
        var scanner = CreateScanner();
        var libId = Guid.NewGuid();
        var library = new Library { Id = libId, Name = "TestLib", Paths = new List<string> { "/root", "/offline" } };
        scanner.MissingRoots.Add("/offline");

        using (var seed = CreateNewContext())
        {
            seed.MediaItems.AddRange(
                new MediaItem { Id = Guid.NewGuid(), LibraryId = libId, Title = "Stays", Type = MediaType.Movie, Path = "/root/stays.mkv" },
                new MediaItem { Id = Guid.NewGuid(), LibraryId = libId, Title = "Gone", Type = MediaType.Movie, Path = "/root/gone.mkv" },
                new MediaItem { Id = Guid.NewGuid(), LibraryId = libId, Title = "Offline", Type = MediaType.Movie, Path = "/offline/unreachable.mkv" });
            await seed.SaveChangesAsync();
        }

        scanner.VirtualFileSystem.Add("/root", new List<string> { "/root/stays.mkv" });

        await scanner.ScanLibraryAsync(library);

        using var verify = CreateNewContext();
        var remaining = await verify.MediaItems.Select(m => m.Title).ToListAsync();
        Assert.Contains("Stays", remaining);
        Assert.Contains("Offline", remaining);   // unreachable root: preserved, not purged
        Assert.DoesNotContain("Gone", remaining); // live root: normal orphan purge
    }

    [Fact]
    public async Task ScanLibraryAsync_UnreadableDirectory_PreservesItsItems_StillPurgesReadableDirs()
    {
        SetMissingRetentionDays("0"); // legacy immediate-delete semantics under test
        var scanner = CreateScanner();
        var libId = Guid.NewGuid();
        var library = new Library { Id = libId, Name = "TestLib", Paths = new List<string> { "/root" } };

        using (var seed = CreateNewContext())
        {
            seed.MediaItems.AddRange(
                new MediaItem { Id = Guid.NewGuid(), LibraryId = libId, Title = "Stays", Type = MediaType.Movie, Path = "/root/stays.mkv" },
                new MediaItem { Id = Guid.NewGuid(), LibraryId = libId, Title = "Gone", Type = MediaType.Movie, Path = "/root/gone.mkv" },
                new MediaItem { Id = Guid.NewGuid(), LibraryId = libId, Title = "Hidden", Type = MediaType.Movie, Path = "/root/locked/hidden.mkv" });
            await seed.SaveChangesAsync();
        }

        scanner.VirtualFileSystem.Add("/root", new List<string> { "/root/stays.mkv" });
        scanner.VirtualFileSystem.Add("/root/locked", new List<string> { "/root/locked/hidden.mkv" });
        scanner.UnreadableDirs.Add("/root/locked"); // simulated permission failure

        await scanner.ScanLibraryAsync(library);

        using var verify = CreateNewContext();
        var remaining = await verify.MediaItems.Select(m => m.Title).ToListAsync();
        Assert.Contains("Stays", remaining);
        Assert.Contains("Hidden", remaining);     // unlistable dir: preserved, not purged
        Assert.DoesNotContain("Gone", remaining); // readable dir: normal orphan purge
    }

    // ---------------------------------------------------------------------------
    // SR-WI-010/011/012 — data-safety suite: purge brake, soft delete, heal,
    // retention, and moved-file reconciliation.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Scan_OrphanedFile_IsSoftDeleted_NotRemoved_ByDefault()
    {
        var scanner = CreateScanner();
        var libId = Guid.NewGuid();
        var library = new Library { Id = libId, Name = "TestLib", Paths = new List<string> { "/root" } };

        using (var seed = CreateNewContext())
        {
            seed.MediaItems.AddRange(
                new MediaItem { Id = Guid.NewGuid(), LibraryId = libId, Title = "Stays", Type = MediaType.Movie, Path = "/root/stays.mkv" },
                new MediaItem { Id = Guid.NewGuid(), LibraryId = libId, Title = "Gone", Type = MediaType.Movie, Path = "/root/gone.mkv" });
            await seed.SaveChangesAsync();
        }

        scanner.VirtualFileSystem.Add("/root", new List<string> { "/root/stays.mkv" });

        await scanner.ScanLibraryAsync(library);

        using var verify = CreateNewContext();
        var gone = await verify.MediaItems.SingleAsync(m => m.Title == "Gone");
        Assert.True(gone.IsMissing);
        Assert.NotNull(gone.MissingSinceUtc);
        var stays = await verify.MediaItems.SingleAsync(m => m.Title == "Stays");
        Assert.False(stays.IsMissing);
    }

    [Fact]
    public async Task Scan_MissingItemWhoseFileReturned_IsHealed()
    {
        var scanner = CreateScanner();
        var libId = Guid.NewGuid();
        var library = new Library { Id = libId, Name = "TestLib", Paths = new List<string> { "/root" } };

        using (var seed = CreateNewContext())
        {
            seed.MediaItems.Add(new MediaItem
            {
                Id = Guid.NewGuid(), LibraryId = libId, Title = "Back", Type = MediaType.Movie,
                Path = "/root/back.mkv", IsMissing = true, MissingSinceUtc = DateTime.UtcNow.AddDays(-3)
            });
            await seed.SaveChangesAsync();
        }

        scanner.VirtualFileSystem.Add("/root", new List<string> { "/root/back.mkv" });

        await scanner.ScanLibraryAsync(library);

        using var verify = CreateNewContext();
        var back = await verify.MediaItems.SingleAsync(m => m.Title == "Back");
        Assert.False(back.IsMissing);
        Assert.Null(back.MissingSinceUtc);
    }

    [Fact]
    public async Task Scan_MissingItemPastRetention_IsHardDeleted_RecentOneKept()
    {
        var scanner = CreateScanner();
        var libId = Guid.NewGuid();
        var library = new Library { Id = libId, Name = "TestLib", Paths = new List<string> { "/root" } };

        using (var seed = CreateNewContext())
        {
            seed.MediaItems.AddRange(
                new MediaItem { Id = Guid.NewGuid(), LibraryId = libId, Title = "Stays", Type = MediaType.Movie, Path = "/root/stays.mkv" },
                new MediaItem
                {
                    Id = Guid.NewGuid(), LibraryId = libId, Title = "Expired", Type = MediaType.Movie,
                    Path = "/root/expired.mkv", IsMissing = true, MissingSinceUtc = DateTime.UtcNow.AddDays(-31)
                },
                new MediaItem
                {
                    Id = Guid.NewGuid(), LibraryId = libId, Title = "RecentlyMissing", Type = MediaType.Movie,
                    Path = "/root/recent.mkv", IsMissing = true, MissingSinceUtc = DateTime.UtcNow.AddDays(-5)
                });
            await seed.SaveChangesAsync();
        }

        scanner.VirtualFileSystem.Add("/root", new List<string> { "/root/stays.mkv" });

        await scanner.ScanLibraryAsync(library);

        using var verify = CreateNewContext();
        var titles = await verify.MediaItems.Select(m => m.Title).ToListAsync();
        Assert.Contains("Stays", titles);
        Assert.Contains("RecentlyMissing", titles); // still inside the 30-day window
        Assert.DoesNotContain("Expired", titles);   // aged out -> hard delete
    }

    [Fact]
    public async Task PurgeBrake_EmptyDiscoveryOfNonEmptyLibrary_MarksNothing_AndNotifiesAdmin()
    {
        var scanner = CreateScanner();
        var libId = Guid.NewGuid();
        var library = new Library { Id = libId, Name = "NasLib", Paths = new List<string> { "/root" } };

        using (var seed = CreateNewContext())
        {
            seed.MediaItems.AddRange(
                new MediaItem { Id = Guid.NewGuid(), LibraryId = libId, Title = "A", Type = MediaType.Movie, Path = "/root/a.mkv" },
                new MediaItem { Id = Guid.NewGuid(), LibraryId = libId, Title = "B", Type = MediaType.Movie, Path = "/root/b.mkv" });
            await seed.SaveChangesAsync();
        }

        // Root "exists" (not in MissingRoots) but the share reconnected empty:
        // the virtual filesystem has no files at all.
        await scanner.ScanLibraryAsync(library);

        using var verify = CreateNewContext();
        Assert.Equal(2, await verify.MediaItems.CountAsync());
        Assert.Equal(0, await verify.MediaItems.CountAsync(m => m.IsMissing));
        _mockSystemNotifications.Verify(
            x => x.CreateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), "error", It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task PurgeBrake_MassDisappearanceOverThreshold_MarksNothing()
    {
        var scanner = CreateScanner();
        var libId = Guid.NewGuid();
        var library = new Library { Id = libId, Name = "TestLib", Paths = new List<string> { "/root" } };

        // 30 known items; only 5 still discovered -> 25 newly missing (83%, >= 20-item floor).
        var stillPresent = new List<string>();
        using (var seed = CreateNewContext())
        {
            for (int i = 0; i < 30; i++)
            {
                var path = $"/root/m{i}.mkv";
                seed.MediaItems.Add(new MediaItem { Id = Guid.NewGuid(), LibraryId = libId, Title = $"M{i}", Type = MediaType.Movie, Path = path });
                if (i < 5) stillPresent.Add(path);
            }
            await seed.SaveChangesAsync();
        }
        scanner.VirtualFileSystem.Add("/root", stillPresent);

        await scanner.ScanLibraryAsync(library);

        using var verify = CreateNewContext();
        Assert.Equal(0, await verify.MediaItems.CountAsync(m => m.IsMissing));
        Assert.Equal(30, await verify.MediaItems.CountAsync());
        _mockSystemNotifications.Verify(
            x => x.CreateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), "error", It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task PurgeBrake_UnderItemFloor_ProceedsEvenAtHighFraction()
    {
        var scanner = CreateScanner();
        var libId = Guid.NewGuid();
        var library = new Library { Id = libId, Name = "TestLib", Paths = new List<string> { "/root" } };

        // 12 known, 10 vanish (83% but under the 20-item floor) -> normal soft delete.
        var stillPresent = new List<string>();
        using (var seed = CreateNewContext())
        {
            for (int i = 0; i < 12; i++)
            {
                var path = $"/root/m{i}.mkv";
                seed.MediaItems.Add(new MediaItem { Id = Guid.NewGuid(), LibraryId = libId, Title = $"M{i}", Type = MediaType.Movie, Path = path });
                if (i < 2) stillPresent.Add(path);
            }
            await seed.SaveChangesAsync();
        }
        scanner.VirtualFileSystem.Add("/root", stillPresent);

        await scanner.ScanLibraryAsync(library);

        using var verify = CreateNewContext();
        Assert.Equal(10, await verify.MediaItems.CountAsync(m => m.IsMissing));
    }

    [Fact]
    public async Task PurgeBrake_OverrideAt100Percent_AllowsMassRemoval()
    {
        _mockSettingsService.Setup(x => x.GetSettingAsync("MaxScanPurgePercent", "25"))
                            .ReturnsAsync("100");
        SetMissingRetentionDays("0"); // combined with override: intentional full cleanup
        var scanner = CreateScanner();
        var libId = Guid.NewGuid();
        var library = new Library { Id = libId, Name = "TestLib", Paths = new List<string> { "/root" } };

        using (var seed = CreateNewContext())
        {
            for (int i = 0; i < 25; i++)
                seed.MediaItems.Add(new MediaItem { Id = Guid.NewGuid(), LibraryId = libId, Title = $"M{i}", Type = MediaType.Movie, Path = $"/root/m{i}.mkv" });
            await seed.SaveChangesAsync();
        }
        // Library intentionally emptied on disk; brake overridden.
        await scanner.ScanLibraryAsync(library);

        using var verify = CreateNewContext();
        Assert.Equal(0, await verify.MediaItems.CountAsync());
    }

    [Fact]
    public async Task Reconcile_RenamedFile_KeepsIdentity_BySizeAndMtime()
    {
        var scanner = CreateScanner();
        var libId = Guid.NewGuid();
        var library = new Library { Id = libId, Name = "TestLib", Paths = new List<string> { "/root" } };
        var itemId = Guid.NewGuid();
        var mtime = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

        using (var seed = CreateNewContext())
        {
            seed.MediaItems.Add(new MediaItem
            {
                Id = itemId, LibraryId = libId, Title = "Film", Type = MediaType.Movie,
                Path = "/root/Film.1080p.mkv", Size = 12345, DateModified = mtime
            });
            await seed.SaveChangesAsync();
        }

        // Same bytes, new name and folder.
        scanner.VirtualFileSystem.Add("/root/renamed", new List<string> { "/root/renamed/Film (2020).mkv" });
        scanner.FileMetadata["/root/renamed/Film (2020).mkv"] = (12345, mtime);

        await scanner.ScanLibraryAsync(library);

        using var verify = CreateNewContext();
        var item = await verify.MediaItems.SingleAsync();
        Assert.Equal(itemId, item.Id); // identity preserved
        Assert.Equal("/root/renamed/Film (2020).mkv", item.Path);
        Assert.False(item.IsMissing);
    }

    [Fact]
    public async Task Reconcile_MovedFile_KeepsIdentity_ByUniqueFilename_WhenSizeUnknown()
    {
        var scanner = CreateScanner();
        var libId = Guid.NewGuid();
        var library = new Library { Id = libId, Name = "TestLib", Paths = new List<string> { "/root" } };
        var itemId = Guid.NewGuid();

        using (var seed = CreateNewContext())
        {
            seed.MediaItems.Add(new MediaItem
            {
                Id = itemId, LibraryId = libId, Title = "Film", Type = MediaType.Movie,
                Path = "/root/a/Film.mkv", Size = 0
            });
            await seed.SaveChangesAsync();
        }

        // Size 0 on both sides (no size signal) -> unique-filename fallback binds it.
        scanner.VirtualFileSystem.Add("/root/b", new List<string> { "/root/b/Film.mkv" });

        await scanner.ScanLibraryAsync(library);

        using var verify = CreateNewContext();
        var item = await verify.MediaItems.SingleAsync();
        Assert.Equal(itemId, item.Id);
        Assert.Equal("/root/b/Film.mkv", item.Path);
        Assert.False(item.IsMissing);
    }

    [Fact]
    public async Task Reconcile_AmbiguousCandidates_BindNothing_AndSoftDeleteApplies()
    {
        var scanner = CreateScanner();
        var libId = Guid.NewGuid();
        var library = new Library { Id = libId, Name = "TestLib", Paths = new List<string> { "/root" } };
        var mtime = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

        using (var seed = CreateNewContext())
        {
            // Two vanished items with identical size+mtime AND identical filenames in
            // different folders — no unique match on either pass.
            seed.MediaItems.AddRange(
                new MediaItem { Id = Guid.NewGuid(), LibraryId = libId, Title = "One", Type = MediaType.Movie, Path = "/root/a/Twin.mkv", Size = 500, DateModified = mtime },
                new MediaItem { Id = Guid.NewGuid(), LibraryId = libId, Title = "Two", Type = MediaType.Movie, Path = "/root/b/Twin.mkv", Size = 500, DateModified = mtime },
                new MediaItem { Id = Guid.NewGuid(), LibraryId = libId, Title = "Anchor", Type = MediaType.Movie, Path = "/root/anchor.mkv" });
            await seed.SaveChangesAsync();
        }

        scanner.VirtualFileSystem.Add("/root", new List<string> { "/root/anchor.mkv" });
        scanner.VirtualFileSystem.Add("/root/c", new List<string> { "/root/c/Twin.mkv" });
        scanner.FileMetadata["/root/c/Twin.mkv"] = (500, mtime);

        await scanner.ScanLibraryAsync(library);

        using var verify = CreateNewContext();
        // Neither ambiguous orphan bound to the new file; both soft-deleted, anchor intact.
        Assert.Equal(2, await verify.MediaItems.CountAsync(m => m.IsMissing));
        Assert.False((await verify.MediaItems.SingleAsync(m => m.Title == "Anchor")).IsMissing);
    }

    [Fact]
    public async Task ScanContext_DefaultsToRelaxed_WhenSettingMissing()
    {
        // Arrange
        _mockSettingsService.Setup(x => x.GetSettingAsync("MetadataEnrichmentMode", "Relaxed"))
                            .ReturnsAsync("Relaxed"); // default value returned by service when missing

        var scanner = CreateScanner();
        var library = new Library { Id = Guid.NewGuid(), Name = "TestLib", Paths = new List<string> { "/root" } };
        scanner.VirtualFileSystem.Add("/root", new List<string> { "/root/file1.mkv" });

        // Act
        await scanner.ScanLibraryAsync(library);

        // Assert
        Assert.False(scanner.IsStrictEnrichment);
    }
}
