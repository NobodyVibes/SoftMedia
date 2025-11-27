using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services;
using SoftMedia.Server.Services.Abstractions;
using Xunit;

namespace SoftMedia.Tests.Services;

public class FileScannerServiceTests
{
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<IServiceScopeFactory> _scopeFactoryMock;
    private readonly Mock<IServiceScope> _scopeMock;
    private readonly Mock<IServiceProvider> _serviceProviderMock;
    private readonly AppDbContext _dbContext;
    private readonly FileScannerService _service;

    public FileScannerServiceTests()
    {
        _fileSystemMock = new Mock<IFileSystem>();
        _scopeFactoryMock = new Mock<IServiceScopeFactory>();
        _scopeMock = new Mock<IServiceScope>();
        _serviceProviderMock = new Mock<IServiceProvider>();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);

        _scopeFactoryMock.Setup(s => s.CreateScope()).Returns(_scopeMock.Object);
        _scopeMock.Setup(s => s.ServiceProvider).Returns(_serviceProviderMock.Object);
        _serviceProviderMock.Setup(s => s.GetService(typeof(AppDbContext))).Returns(_dbContext);

        _service = new FileScannerService(_scopeFactoryMock.Object, Mock.Of<ILogger<FileScannerService>>(), _fileSystemMock.Object);
    }

    [Fact]
    public async Task ScanLibraryAsync_AddsNewMediaItems()
    {
        // Arrange
        var libraryId = Guid.NewGuid();
        var library = new Library { Id = libraryId, Name = "Movies", Type = LibraryType.Movie, Paths = new List<string> { "/movies" } };
        _dbContext.Libraries.Add(library);
        await _dbContext.SaveChangesAsync();

        _fileSystemMock.Setup(fs => fs.DirectoryExists("/movies")).Returns(true);
        _fileSystemMock.Setup(fs => fs.GetFiles("/movies", "*.*", SearchOption.AllDirectories))
            .Returns(new[] { "/movies/Matrix.mkv" });
        _fileSystemMock.Setup(fs => fs.GetExtension("/movies/Matrix.mkv")).Returns(".mkv");
        _fileSystemMock.Setup(fs => fs.GetFileNameWithoutExtension("/movies/Matrix.mkv")).Returns("Matrix");
        _fileSystemMock.Setup(fs => fs.GetFileLength("/movies/Matrix.mkv")).Returns(1024);
        _fileSystemMock.Setup(fs => fs.GetLastWriteTimeUtc("/movies/Matrix.mkv")).Returns(DateTime.UtcNow);

        // Act
        await _service.ScanLibraryAsync(libraryId);

        // Assert
        var mediaItem = await _dbContext.MediaItems.FirstOrDefaultAsync();
        Assert.NotNull(mediaItem);
        Assert.Equal("Matrix", mediaItem.Title);
        Assert.Equal("/movies/Matrix.mkv", mediaItem.Path);
    }

    [Fact]
    public async Task ScanLibraryAsync_IgnoresNonMediaFiles()
    {
        // Arrange
        var libraryId = Guid.NewGuid();
        var library = new Library { Id = libraryId, Name = "Movies", Type = LibraryType.Movie, Paths = new List<string> { "/movies" } };
        _dbContext.Libraries.Add(library);
        await _dbContext.SaveChangesAsync();

        _fileSystemMock.Setup(fs => fs.DirectoryExists("/movies")).Returns(true);
        _fileSystemMock.Setup(fs => fs.GetFiles("/movies", "*.*", SearchOption.AllDirectories))
            .Returns(new[] { "/movies/readme.txt" });
        _fileSystemMock.Setup(fs => fs.GetExtension("/movies/readme.txt")).Returns(".txt");

        // Act
        await _service.ScanLibraryAsync(libraryId);

        // Assert
        var count = await _dbContext.MediaItems.CountAsync();
        Assert.Equal(0, count);
    }
}
