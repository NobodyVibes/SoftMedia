using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using Xunit;

namespace SoftMedia.Server.Tests.Services;

/// <summary>
/// Unit tests for AudioStreamPlanService.
/// Tests codec matching, bitrate limiting, and direct play detection.
/// </summary>
public class AudioStreamPlanServiceTests
{
    private readonly Mock<ISettingsService> _settingsServiceMock;
    private readonly Mock<ILogger<AudioStreamPlanService>> _loggerMock;
    
    public AudioStreamPlanServiceTests()
    {
        _settingsServiceMock = new Mock<ISettingsService>();
        _loggerMock = new Mock<ILogger<AudioStreamPlanService>>();
        
        // Default setup for MaxAudioStreamingBitrate - unlimited
        _settingsServiceMock.Setup(s => s.GetSettingAsync("MaxAudioStreamingBitrate", 0))
            .ReturnsAsync(0);
    }
    
    private AppDbContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
    
    [Fact]
    public async Task ComputePlanAsync_DirectPlay_WhenClientSupportsCodec()
    {
        // Arrange
        var mediaId = Guid.NewGuid();
        using var context = CreateInMemoryContext();
        
        context.MediaItems.Add(new MediaItem
        {
            Id = mediaId,
            Title = "Test Track",
            Path = "/music/song.mp3",
            Type = MediaType.Track,
            AudioCodec = "mp3",
            Duration = 180
        });
        await context.SaveChangesAsync();
        
        var service = new AudioStreamPlanService(context, _settingsServiceMock.Object, _loggerMock.Object);
        
        // Act
        var plan = await service.ComputePlanAsync(mediaId, new[] { "aac", "mp3", "flac" }, 0);
        
        // Assert
        Assert.True(plan.CanDirectPlay);
        Assert.Equal("mp3", plan.SourceCodec);
        Assert.Null(plan.TargetCodec);
        Assert.Null(plan.TargetBitrate);
        Assert.Contains("/api/v1/audio/stream/", plan.Url);
        Assert.Equal("audio/mpeg", plan.ContentType);
    }
    
    [Fact]
    public async Task ComputePlanAsync_Transcode_WhenClientDoesNotSupportCodec()
    {
        // Arrange
        var mediaId = Guid.NewGuid();
        using var context = CreateInMemoryContext();
        
        context.MediaItems.Add(new MediaItem
        {
            Id = mediaId,
            Title = "Test Track",
            Path = "/music/song.flac",
            Type = MediaType.Track,
            AudioCodec = "flac",
            Duration = 180
        });
        await context.SaveChangesAsync();
        
        var service = new AudioStreamPlanService(context, _settingsServiceMock.Object, _loggerMock.Object);
        
        // Act - Client only supports AAC, not FLAC
        var plan = await service.ComputePlanAsync(mediaId, new[] { "aac" }, 0);
        
        // Assert
        Assert.False(plan.CanDirectPlay);
        Assert.Equal("flac", plan.SourceCodec);
        Assert.Equal("aac", plan.TargetCodec);
        Assert.NotNull(plan.TargetBitrate);
        Assert.Contains("/api/v1/audio/transcode/", plan.Url);
        Assert.Equal("audio/aac", plan.ContentType);
    }
    
    [Fact]
    public async Task ComputePlanAsync_UsesServerBitrateLimit_WhenClientUnlimited()
    {
        // Arrange
        var mediaId = Guid.NewGuid();
        using var context = CreateInMemoryContext();
        
        // Override default: Server limits to 192kbps
        _settingsServiceMock.Setup(s => s.GetSettingAsync("MaxAudioStreamingBitrate", 0))
            .ReturnsAsync(192);
        
        context.MediaItems.Add(new MediaItem
        {
            Id = mediaId,
            Title = "Test Track",
            Path = "/music/song.flac",
            Type = MediaType.Track,
            AudioCodec = "flac",
            Duration = 180
        });
        await context.SaveChangesAsync();
        
        var service = new AudioStreamPlanService(context, _settingsServiceMock.Object, _loggerMock.Object);
        
        // Act - Client is unlimited (0), but server is limited
        var plan = await service.ComputePlanAsync(mediaId, new[] { "aac" }, 0);
        
        // Assert
        Assert.False(plan.CanDirectPlay);
        Assert.Equal(192, plan.TargetBitrate);
    }
    
    [Fact]
    public async Task ComputePlanAsync_UsesClientBitrateLimit_WhenLowerThanServer()
    {
        // Arrange
        var mediaId = Guid.NewGuid();
        using var context = CreateInMemoryContext();
        
        // Server allows up to 256kbps
        _settingsServiceMock.Setup(s => s.GetSettingAsync("MaxAudioStreamingBitrate", 0))
            .ReturnsAsync(256);
        
        context.MediaItems.Add(new MediaItem
        {
            Id = mediaId,
            Title = "Test Track",
            Path = "/music/song.flac",
            Type = MediaType.Track,
            AudioCodec = "flac",
            Duration = 180
        });
        await context.SaveChangesAsync();
        
        var service = new AudioStreamPlanService(context, _settingsServiceMock.Object, _loggerMock.Object);
        
        // Act - Client wants only 128kbps
        var plan = await service.ComputePlanAsync(mediaId, new[] { "aac" }, 128);
        
        // Assert
        Assert.Equal(128, plan.TargetBitrate);
    }
    
    [Fact]
    public async Task ComputePlanAsync_ClampsBitrate_ToValidRange()
    {
        // Arrange
        var mediaId = Guid.NewGuid();
        using var context = CreateInMemoryContext();
        
        // Below minimum (64)
        _settingsServiceMock.Setup(s => s.GetSettingAsync("MaxAudioStreamingBitrate", 0))
            .ReturnsAsync(32);
        
        context.MediaItems.Add(new MediaItem
        {
            Id = mediaId,
            Title = "Test Track",
            Path = "/music/song.flac",
            Type = MediaType.Track,
            AudioCodec = "flac",
            Duration = 180
        });
        await context.SaveChangesAsync();
        
        var service = new AudioStreamPlanService(context, _settingsServiceMock.Object, _loggerMock.Object);
        
        // Act
        var plan = await service.ComputePlanAsync(mediaId, new[] { "aac" }, 0);
        
        // Assert - Should clamp to minimum 64
        Assert.Equal(64, plan.TargetBitrate);
    }
    
    [Fact]
    public async Task ComputePlanAsync_ThrowsException_WhenMediaNotFound()
    {
        // Arrange
        using var context = CreateInMemoryContext();
        var service = new AudioStreamPlanService(context, _settingsServiceMock.Object, _loggerMock.Object);
        
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ComputePlanAsync(Guid.NewGuid(), new[] { "aac" }, 0));
    }
    
    [Fact]
    public async Task ComputePlanAsync_ThrowsException_WhenMediaIsNotAudioTrack()
    {
        // Arrange
        var mediaId = Guid.NewGuid();
        using var context = CreateInMemoryContext();
        
        context.MediaItems.Add(new MediaItem
        {
            Id = mediaId,
            Title = "Test Movie",
            Path = "/movies/movie.mp4",
            Type = MediaType.Movie, // Not an audio track
            Duration = 7200
        });
        await context.SaveChangesAsync();
        
        var service = new AudioStreamPlanService(context, _settingsServiceMock.Object, _loggerMock.Object);
        
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ComputePlanAsync(mediaId, new[] { "aac" }, 0));
    }
    
    [Fact]
    public async Task ComputePlanAsync_NormalizesCodecNames()
    {
        // Arrange
        var mediaId = Guid.NewGuid();
        using var context = CreateInMemoryContext();
        
        // Source uses "mp4a" which should normalize to "aac"
        context.MediaItems.Add(new MediaItem
        {
            Id = mediaId,
            Title = "Test Track",
            Path = "/music/song.m4a",
            Type = MediaType.Track,
            AudioCodec = "mp4a",
            Duration = 180
        });
        await context.SaveChangesAsync();
        
        var service = new AudioStreamPlanService(context, _settingsServiceMock.Object, _loggerMock.Object);
        
        // Act - Client supports "aac" (normalized form of "mp4a")
        var plan = await service.ComputePlanAsync(mediaId, new[] { "aac" }, 0);
        
        // Assert - Should direct play since mp4a == aac
        Assert.True(plan.CanDirectPlay);
        Assert.Equal("aac", plan.SourceCodec);
    }
}
