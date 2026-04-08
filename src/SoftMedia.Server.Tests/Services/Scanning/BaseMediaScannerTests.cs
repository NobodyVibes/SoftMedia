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
        
        // Verify scope creation - should be called at least 10 times (once per dir) + 1 (initial existing paths) + 1 (cleanup)
        // Actually, Parallel.ForEachAsync might reuse threads/tasks but the code does `using var scope = _scopeFactory.CreateScope()` inside the loop body.
        // So Scopes created >= 12.
        _mockScopeFactory.Verify(x => x.CreateScope(), Times.AtLeast(12));
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
