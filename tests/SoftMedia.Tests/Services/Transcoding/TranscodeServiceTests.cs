using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Transcoding;
using SoftMedia.Server.Services.Transcoding.Models;
using Xunit;

namespace SoftMedia.Tests.Services.Transcoding;

public class TranscodeServiceTests
{
    private readonly Mock<IServiceScopeFactory> _scopeFactoryMock;
    private readonly Mock<IServiceScope> _scopeMock;
    private readonly Mock<IServiceProvider> _serviceProviderMock;
    private readonly Mock<ILogger<TranscodeService>> _loggerMock;
    private readonly Mock<IProcessController> _processControllerMock;
    private readonly Mock<ITranscodeSessionManager> _sessionManagerMock;
    private readonly Mock<IHlsService> _hlsServiceMock;
    private readonly Mock<IConfiguration> _configMock;
    private readonly Mock<IFFmpegService> _ffmpegServiceMock;
    private readonly Mock<ISettingsService> _settingsServiceMock;
    private readonly TranscodeService _service;

    public TranscodeServiceTests()
    {
        _scopeFactoryMock = new Mock<IServiceScopeFactory>();
        _scopeMock = new Mock<IServiceScope>();
        _serviceProviderMock = new Mock<IServiceProvider>();
        _loggerMock = new Mock<ILogger<TranscodeService>>();
        _processControllerMock = new Mock<IProcessController>();
        _sessionManagerMock = new Mock<ITranscodeSessionManager>();
        _hlsServiceMock = new Mock<IHlsService>();
        _configMock = new Mock<IConfiguration>();
        _ffmpegServiceMock = new Mock<IFFmpegService>();
        _settingsServiceMock = new Mock<ISettingsService>();

        // Setup Scope Factory
        _scopeFactoryMock.Setup(s => s.CreateScope()).Returns(_scopeMock.Object);
        _scopeMock.Setup(s => s.ServiceProvider).Returns(_serviceProviderMock.Object);

        // Setup Service Provider to return mocked services
        _serviceProviderMock.Setup(sp => sp.GetService(typeof(IFFmpegService)))
            .Returns(_ffmpegServiceMock.Object);
        _serviceProviderMock.Setup(sp => sp.GetService(typeof(ISettingsService)))
            .Returns(_settingsServiceMock.Object);

        _service = new TranscodeService(
            _scopeFactoryMock.Object,
            _loggerMock.Object,
            _processControllerMock.Object,
            _sessionManagerMock.Object,
            _hlsServiceMock.Object,
            _configMock.Object
        );
    }

    [Fact]
    public async Task StartTranscodeAsync_DifferentResolution_RestartsSession()
    {
        // Arrange
        var mediaId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var sessionKey = new TranscodeSessionKey(mediaId, userId, null);
        
        // Mock existing session with 1080p
        var existingSession = new TranscodeSession
        {
            Key = sessionKey,
            UserId = userId,
            TargetResolution = "1080p",
            SessionDirectory = "test_dir",
            State = TranscodeState.Transcoding
        };

        // Setup SessionManager to simulate existing active session
        _sessionManagerMock.Setup(m => m.AcquireLockAsync(It.IsAny<TranscodeSessionKey>()))
            .ReturnsAsync(new Mock<IDisposable>().Object);
            
        _sessionManagerMock.Setup(m => m.GetSession(It.IsAny<TranscodeSessionKey>()))
            .Returns(existingSession);

        // Ensure directory check passes (simulate existing dir)
        Directory.CreateDirectory("test_dir"); // Real FS interaction (mocking Directory class is hard)
        
        // Act
        // Request transcode with DIFFERENT resolution (720p)
        await _service.StartTranscodeAsync(
            mediaId, userId, "input.mp4", 
            resolution: "720p"
        );

        // Assert
        // Verify TryRemoveSession was called, indicating the old session was stopped
        _sessionManagerMock.Verify(m => m.TryRemoveSession(sessionKey, out It.Ref<TranscodeSession>.IsAny), Times.Once, 
            "Session should be removed/restarted when resolution changes");
            
        // Cleanup
        if (Directory.Exists("test_dir")) Directory.Delete("test_dir", true);
    }
}
