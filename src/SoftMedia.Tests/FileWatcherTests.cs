using System.Text;
using System.Text.Json;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace SoftMedia.Tests;

/// <summary>
/// Integration tests for the file watcher progressive timeout and admin dashboard features.
/// </summary>
public class FileWatcherTests : IDisposable
{
    private readonly string _testDir;
    private readonly Mock<IServiceScopeFactory> _scopeFactoryMock;
    private readonly Mock<ILogger<LibraryWatcher>> _loggerMock;
    
    public FileWatcherTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "SoftMedia_FileWatcher_Test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
        
        _scopeFactoryMock = new Mock<IServiceScopeFactory>();
        _loggerMock = new Mock<ILogger<LibraryWatcher>>();
    }
    
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { /* Ignore cleanup errors */ }
    }
    
    [Fact]
    public void GetFileIssues_ReturnsEmptyInitially()
    {
        // Arrange
        var watcher = new LibraryWatcher(_scopeFactoryMock.Object, _loggerMock.Object);
        
        // Act
        var issues = watcher.GetFileIssues();
        
        // Assert
        Assert.Empty(issues);
    }
    
    [Fact]
    public void ClearIssue_ReturnsFalse_WhenIssueNotFound()
    {
        // Arrange
        var watcher = new LibraryWatcher(_scopeFactoryMock.Object, _loggerMock.Object);
        
        // Act
        var result = watcher.ClearIssue("nonexistent/path.mp4");
        
        // Assert
        Assert.False(result);
    }
    
    [Fact]
    public void RetryFile_ReturnsFalse_WhenIssueNotFound()
    {
        // Arrange
        var watcher = new LibraryWatcher(_scopeFactoryMock.Object, _loggerMock.Object);
        
        // Act
        var result = watcher.RetryFile("nonexistent/path.mp4");
        
        // Assert
        Assert.False(result);
    }
    
    [Fact]
    public void FileWatcherIssue_HasCorrectProperties()
    {
        // Arrange
        var issue = new FileWatcherIssue
        {
            Path = "/media/test/movie.mp4",
            Status = FileWatcherIssueStatus.Locked,
            FirstSeen = DateTime.UtcNow.AddMinutes(-5),
            LastChecked = DateTime.UtcNow,
            LibraryId = Guid.NewGuid(),
            CanRetry = true
        };
        
        // Assert
        Assert.Equal("movie.mp4", issue.FileName);
        Assert.Equal(FileWatcherIssueStatus.Locked, issue.Status);
        Assert.True(issue.CanRetry);
    }
    
    [Fact]
    public void FileWatcherIssueStatus_HasCorrectValues()
    {
        // Assert
        Assert.Equal("File locked - unable to access", FileWatcherIssueStatus.Locked);
        Assert.Equal("Download stalled - no progress", FileWatcherIssueStatus.Stalled);
        Assert.Equal("Maximum wait time exceeded", FileWatcherIssueStatus.Timeout);
    }
}
