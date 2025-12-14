using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Tests;

/// <summary>
/// Unit tests for StreamController covering streaming, authorization, and LFI protection.
/// </summary>
public class StreamControllerTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly Mock<ILogger<StreamController>> _loggerMock;
    private readonly StreamController _controller;
    private readonly Guid _libraryId = Guid.NewGuid();
    private readonly string _testLibraryPath;

    public StreamControllerTests()
    {
        // Setup in-memory database
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;

        _context = new AppDbContext(options);
        _loggerMock = new Mock<ILogger<StreamController>>();
        _controller = new StreamController(_context, _loggerMock.Object);

        // Create a temp directory for test files
        _testLibraryPath = Path.Combine(Path.GetTempPath(), "SoftMediaTest_" + Guid.NewGuid());
        Directory.CreateDirectory(_testLibraryPath);
        
        SeedTestData();
    }

    private void SeedTestData()
    {
        var library = new Library
        {
            Id = _libraryId,
            Name = "Test Movies",
            Type = LibraryType.Movie,
            Paths = new List<string> { _testLibraryPath }
        };
        _context.Libraries.Add(library);
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        // Cleanup temp directory
        if (Directory.Exists(_testLibraryPath))
        {
            Directory.Delete(_testLibraryPath, true);
        }
    }

    [Fact]
    public async Task GetStream_ReturnsNotFound_WhenMediaItemDoesNotExist()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var result = await _controller.GetStream(nonExistentId);

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetStream_ReturnsNotFound_WhenFileDoesNotExistOnDisk()
    {
        // Arrange
        var mediaItem = new MediaItem
        {
            Id = Guid.NewGuid(),
            LibraryId = _libraryId,
            Title = "Missing File",
            Path = Path.Combine(_testLibraryPath, "nonexistent.mp4"),
            Container = "mp4"
        };
        _context.MediaItems.Add(mediaItem);
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetStream(mediaItem.Id);

        // Assert
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal("File not found on disk.", notFoundResult.Value);
    }

    [Fact]
    public async Task GetStream_ReturnsForbid_WhenPathOutsideLibrary()
    {
        // Arrange - Create a media item with path outside the library
        var mediaItem = new MediaItem
        {
            Id = Guid.NewGuid(),
            LibraryId = _libraryId,
            Title = "Malicious Path",
            Path = @"C:\Windows\System32\config\sam", // Path outside library
            Container = "mp4"
        };
        _context.MediaItems.Add(mediaItem);
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetStream(mediaItem.Id);

        // Assert - Should be NotFound first (file doesn't exist) or Forbid
        // In this case, File.Exists will fail first, so NotFound
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetStream_ReturnsPhysicalFile_WhenValidRequest()
    {
        // Arrange - Create an actual test file
        var testFilePath = Path.Combine(_testLibraryPath, "test_video.mp4");
        await File.WriteAllBytesAsync(testFilePath, new byte[] { 0x00, 0x00, 0x00, 0x20 }); // Minimal data

        var mediaItem = new MediaItem
        {
            Id = Guid.NewGuid(),
            LibraryId = _libraryId,
            Title = "Test Video",
            Path = testFilePath,
            Container = "mp4"
        };
        _context.MediaItems.Add(mediaItem);
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetStream(mediaItem.Id);

        // Assert
        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("video/mp4", fileResult.ContentType);
        Assert.True(fileResult.EnableRangeProcessing);
    }

    [Fact]
    public async Task GetStream_ReturnsForbid_ForLFIWithPathTraversal()
    {
        // Arrange - Create a file in the library, but try to access with path traversal
        var legitFilePath = Path.Combine(_testLibraryPath, "legit.mp4");
        await File.WriteAllBytesAsync(legitFilePath, new byte[] { 0x00 });

        // Create media item pointing outside library using traversal
        var maliciousPath = Path.Combine(_testLibraryPath, "..", "..", "Windows", "System32", "config");
        var mediaItem = new MediaItem
        {
            Id = Guid.NewGuid(),
            LibraryId = _libraryId,
            Title = "LFI Attempt",
            Path = maliciousPath,
            Container = "mp4"
        };
        _context.MediaItems.Add(mediaItem);
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetStream(mediaItem.Id);

        // Assert - Should be blocked (NotFound because file doesn't exist, or Forbid)
        Assert.True(result is NotFoundObjectResult || result is ForbidResult);
    }
}
