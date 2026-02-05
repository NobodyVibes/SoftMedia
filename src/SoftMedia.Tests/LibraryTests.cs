using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;

using Moq;
using SoftMedia.Server.Services;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Tests;

public class LibraryTests
{
    private readonly Mock<ILibraryService> _mockLibraryService;
    private readonly LibrariesController _controller;

    public LibraryTests()
    {
        _mockLibraryService = new Mock<ILibraryService>();
        _controller = new LibrariesController(_mockLibraryService.Object);
    }

    [Fact]
    public async Task GetLibraries_ReturnsOrderedLibraries()
    {
        // Arrange
        var libraries = new List<Library>
        {
            new Library { Name = "Lib 1", Order = 1 },
            new Library { Name = "Lib 2", Order = 2 }
        };
        _mockLibraryService.Setup(s => s.GetLibrariesAsync()).ReturnsAsync(libraries);

        // Act
        var result = await _controller.GetLibraries();

        // Assert
        var actionResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedLibraries = Assert.IsAssignableFrom<IEnumerable<Library>>(actionResult.Value);
        Assert.Equal(2, returnedLibraries.Count());
        Assert.Equal("Lib 1", returnedLibraries.First().Name);
    }

    [Fact]
    public async Task CreateLibrary_AddsLibrary()
    {
        // Arrange
        var request = new CreateLibraryRequest
        {
            Name = "New Lib",
            Type = LibraryType.Movie,
            Paths = new List<string> { "C:\\Movies" }
        };
        var createdLibrary = new Library { Name = "New Lib", Order = 0, Id = Guid.NewGuid() };
        
        _mockLibraryService.Setup(s => s.CreateLibraryAsync(request)).ReturnsAsync(createdLibrary);

        // Act
        var result = await _controller.CreateLibrary(request);

        // Assert
        var createdAtActionResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var library = Assert.IsType<Library>(createdAtActionResult.Value);
        Assert.Equal("New Lib", library.Name);
    }

    [Fact]
    public async Task ReorderLibraries_UpdatesOrder()
    {
        // Arrange
        var orderedIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        _mockLibraryService.Setup(s => s.ReorderLibrariesAsync(orderedIds)).Returns(Task.CompletedTask);

        // Act
        var result = await _controller.ReorderLibraries(orderedIds);

        // Assert
        Assert.IsType<NoContentResult>(result);
        _mockLibraryService.Verify(s => s.ReorderLibrariesAsync(orderedIds), Times.Once);
    }
}
