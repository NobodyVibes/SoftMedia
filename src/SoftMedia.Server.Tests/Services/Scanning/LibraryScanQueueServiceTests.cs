using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Scanning; // For LibraryScanQueueService and IScannerOrchestrator
using Xunit;

namespace SoftMedia.Server.Tests.Services.Scanning;

public class LibraryScanQueueServiceTests
{
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IServiceScope> _mockScope;
    private readonly Mock<IServiceProvider> _mockServiceProvider;
    private readonly Mock<ILogger<LibraryScanQueueService>> _mockLogger;
    private readonly Mock<IScannerOrchestrator> _mockOrchestrator;
    private readonly Mock<ILibraryService> _mockLibraryService;

    public LibraryScanQueueServiceTests()
    {
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockScope = new Mock<IServiceScope>();
        _mockServiceProvider = new Mock<IServiceProvider>();

        _mockScopeFactory.Setup(x => x.CreateScope()).Returns(_mockScope.Object);
        _mockScope.Setup(x => x.ServiceProvider).Returns(_mockServiceProvider.Object);

        _mockOrchestrator = new Mock<IScannerOrchestrator>();
        _mockLibraryService = new Mock<ILibraryService>();

        _mockServiceProvider.Setup(x => x.GetService(typeof(IScannerOrchestrator))).Returns(_mockOrchestrator.Object);
        _mockServiceProvider.Setup(x => x.GetService(typeof(ILibraryService))).Returns(_mockLibraryService.Object);
        
        // Mock required IServiceScopeFactory inside the scope if needed?
        // No, LibraryScanQueueService uses the injected one.

        _mockLogger = new Mock<ILogger<LibraryScanQueueService>>();
    }

    private LibraryScanQueueService CreateService()
    {
        return new LibraryScanQueueService(_mockScopeFactory.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task EnqueueScan_RunsScanViaOrchestrator()
    {
        // Arrange
        var service = CreateService();
        var libId = Guid.NewGuid();
        
        using var cts = new CancellationTokenSource();
        var task = service.StartAsync(cts.Token);

        // Act
        service.EnqueueScan(libId, "Test Library");

        // Assert
        // Wait for execution
        int retries = 0;
        bool called = false;
        while (retries < 20)
        {
            try
            {
                _mockOrchestrator.Verify(x => x.ExecuteScanAsync(libId, It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
                called = true;
                break;
            }
            catch (MockException) 
            {
                await Task.Delay(50);
                retries++;
            }
        }

        cts.Cancel();
        
        Assert.True(called, "Orchestrator was not called.");
    }
    
    [Fact]
    public async Task EnqueueScan_SerializesConcurrentRequests()
    {
        // Arrange
        var service = CreateService();
        var libId1 = Guid.NewGuid();
        var libId2 = Guid.NewGuid();
        
        // Setup Orchestrator to block on first call
        var tcs1 = new TaskCompletionSource<bool>();
        _mockOrchestrator.Setup(x => x.ExecuteScanAsync(libId1, It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(async () => {
                await tcs1.Task;
            });
            
        _mockOrchestrator.Setup(x => x.ExecuteScanAsync(libId2, It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        var task = service.StartAsync(cts.Token);

        // Act
        service.EnqueueScan(libId1, "Lib 1");
        service.EnqueueScan(libId2, "Lib 2");

        // Assert
        // 1. Wait for Lib 1 to start
         int retries = 0;
        while (retries < 20)
        {
            try
            {
                _mockOrchestrator.Verify(x => x.ExecuteScanAsync(libId1, It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
                break;
            }
            catch (MockException) 
            {
                await Task.Delay(50);
                retries++;
            }
        }
        
        // 2. Verify Lib 2 has NOT started yet
         _mockOrchestrator.Verify(x => x.ExecuteScanAsync(libId2, It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()), Times.Never);

        // 3. Release Lib 1
        tcs1.SetResult(true);

        // 4. Wait for Lib 2 to start
        retries = 0;
        bool lib2Called = false;
        while (retries < 20)
        {
            try
            {
                _mockOrchestrator.Verify(x => x.ExecuteScanAsync(libId2, It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
                lib2Called = true;
                break;
            }
            catch (MockException) 
            {
                await Task.Delay(50);
                retries++;
            }
        }
        
        cts.Cancel();
        
        Assert.True(lib2Called, "Second library scan was not processed after first completed.");
    }
}
