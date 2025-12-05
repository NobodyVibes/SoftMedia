using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;

using Moq;
using SoftMedia.Server.Services;

namespace SoftMedia.Tests;

public class LibraryTests
{
    private AppDbContext GetDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task GetLibraries_ReturnsOrderedLibraries()
    {
        // Arrange
        using var context = GetDbContext();
        context.Libraries.AddRange(
            new Library { Name = "Lib 2", Order = 2 },
            new Library { Name = "Lib 1", Order = 1 }
        );
        await context.SaveChangesAsync();

        var mockScanner = new Mock<IFileScannerService>();
        var controller = new LibrariesController(context, mockScanner.Object);

        // Act
        var result = await controller.GetLibraries();

        // Assert
        var actionResult = Assert.IsType<ActionResult<IEnumerable<Library>>>(result);
        var libraries = Assert.IsAssignableFrom<IEnumerable<Library>>(actionResult.Value);
        Assert.Equal(2, libraries.Count());
        Assert.Equal("Lib 1", libraries.First().Name);
    }

    [Fact]
    public async Task CreateLibrary_AddsLibrary()
    {
        // Arrange
        using var context = GetDbContext();
        var mockScanner = new Mock<IFileScannerService>();
        var controller = new LibrariesController(context, mockScanner.Object);
        var request = new CreateLibraryRequest
        {
            Name = "New Lib",
            Type = LibraryType.Movie,
            Paths = new List<string> { "C:\\Movies" } // Note: Directory.Exists check might fail in unit test environment if not mocked or handled.
            // Since Directory.Exists is a static method, it's hard to mock without a wrapper.
            // For this test, we might need to assume the controller checks Directory.Exists.
            // If the controller checks Directory.Exists, this test will fail if the path doesn't exist.
            // We should use a path that likely exists or modify the controller to use an interface for file system operations.
            // For now, I'll use a path that likely exists like current directory, or I'll skip path validation in test if possible?
            // No, I can't skip it easily.
            // I'll use Directory.GetCurrentDirectory()
        };
        request.Paths = new List<string> { Directory.GetCurrentDirectory() };

        // Act
        var result = await controller.CreateLibrary(request);

        // Assert
        var createdAtActionResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var library = Assert.IsType<Library>(createdAtActionResult.Value);
        Assert.Equal("New Lib", library.Name);
        Assert.Equal(0, library.Order); // First one
    }

    [Fact]
    public async Task ReorderLibraries_UpdatesOrder()
    {
        // Arrange
        using var context = GetDbContext();
        var lib1 = new Library { Name = "Lib 1", Order = 0 };
        var lib2 = new Library { Name = "Lib 2", Order = 1 };
        context.Libraries.AddRange(lib1, lib2);
        await context.SaveChangesAsync();

        var mockScanner = new Mock<IFileScannerService>();
        var controller = new LibrariesController(context, mockScanner.Object);
        var orderedIds = new List<Guid> { lib2.Id, lib1.Id };

        // Act
        await controller.ReorderLibraries(orderedIds);

        // Assert
        var updatedLib1 = await context.Libraries.FindAsync(lib1.Id);
        var updatedLib2 = await context.Libraries.FindAsync(lib2.Id);

        Assert.Equal(1, updatedLib1.Order);
        Assert.Equal(0, updatedLib2.Order);
    }
}
