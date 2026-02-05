using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Scanning;
using Xunit;

namespace SoftMedia.Tests.Integration;

public class LibraryIntegrationTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly LibraryService _libraryService;
    private readonly LibrariesController _controller;

    public LibraryIntegrationTests()
    {
        // 1. Setup In-Memory Database
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new AppDbContext(options);

        // 2. Setup Repositories
        var libraryRepo = new LibraryRepository(_context);
        var mediaRepo = new MediaRepository(_context);

        // 3. Setup Mocks for LibraryService dependencies
        var mockScanQueue = new Mock<ILibraryScanQueueService>();
        
        // Setup ImageCacheService with temp path
        var tempCachePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SoftMediaTest_Cache_" + Guid.NewGuid());
        System.IO.Directory.CreateDirectory(tempCachePath);
        
        var mockEnv = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        mockEnv.Setup(e => e.WebRootPath).Returns(tempCachePath);
        
        // Setup Mock HttpClient
        var handlerMock = new Mock<System.Net.Http.HttpMessageHandler>();
        var httpClient = new System.Net.Http.HttpClient(handlerMock.Object);
        
        var imageCacheService = new ImageCacheService(
            httpClient,
            new Mock<ILogger<ImageCacheService>>().Object,
            mockEnv.Object
        );

        var mockWatcher = new Mock<LibraryWatcher>(
            new Mock<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>().Object, 
            new Mock<ILogger<LibraryWatcher>>().Object);

        // 4. Instantiate LibraryService with real repositories
        _libraryService = new LibraryService(
            libraryRepo,
            mediaRepo,
            mockScanQueue.Object,
            imageCacheService,
            mockWatcher.Object,
            _context,
            new Mock<ILogger<LibraryService>>().Object
        );

        // 5. Instantiate Controller with authenticated context
        _controller = new LibrariesController(_libraryService);
        
        // Setup User Context
        var user = new ClaimsPrincipal(new ClaimsIdentity(new Claim[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Admin")
        }, "mock"));

        _controller.ControllerContext = new ControllerContext()
        {
            HttpContext = new DefaultHttpContext() { User = user }
        };
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Fact]
    public async Task CreateLibrary_PersistsToDatabase()
    {
        // Arrange
        var request = new CreateLibraryRequest
        {
            Name = "My Movies",
            Type = LibraryType.Movie,
            Paths = new List<string> { @"C:\Fake\Path" } // Note: Service checks Directory.Exists
        };

        // Service check Directory.Exists(path).
        // This is a problem for Integration tests running on a machine where C:\Fake\Path doesn't exist.
        // We either need to mock Directory.Exists (which requires System.IO abstraction) 
        // OR use a real temp directory.
        
        // Let's use a real temporary directory.
        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SoftMediaTest_" + Guid.NewGuid());
        System.IO.Directory.CreateDirectory(tempPath);
        request.Paths = new List<string> { tempPath };

        try 
        {
            // Act
            var result = await _controller.CreateLibrary(request);

            // Assert
            var actionResult = Assert.IsType<CreatedAtActionResult>(result.Result);
            var library = Assert.IsType<Library>(actionResult.Value);
            Assert.Equal("My Movies", library.Name);
            Assert.Single(_context.Libraries);
            Assert.Equal(library.Id, _context.Libraries.First().Id);
        }
        finally
        {
            // Cleanup
            if (System.IO.Directory.Exists(tempPath))
                System.IO.Directory.Delete(tempPath);
        }
    }

    [Fact]
    public async Task GetLibraries_ReturnsAllLibraries()
    {
        // Arrange
        _context.Libraries.Add(new Library { Name = "Lib 1", Type = LibraryType.Movie, Paths = new List<string>(), Order = 0 });
        _context.Libraries.Add(new Library { Name = "Lib 2", Type = LibraryType.TV, Paths = new List<string>(), Order = 1 });
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetLibraries();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var libraries = Assert.IsAssignableFrom<IEnumerable<Library>>(okResult.Value);
        Assert.Equal(2, libraries.Count());
    }

    [Fact]
    public async Task DeleteLibrary_RemovesFromDatabase()
    {
        // Arrange
        var libId = Guid.NewGuid();
        _context.Libraries.Add(new Library { Id = libId, Name = "To Delete", Type = LibraryType.Movie, Paths = new List<string>() });
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.DeleteLibrary(libId);

        // Assert
        Assert.IsType<NoContentResult>(result);
        Assert.Empty(_context.Libraries);
    }
}
