using SoftMedia.Server.Services.Transcoding;

namespace SoftMedia.Server.Services.Infrastructure;

/// <summary>
/// SR-WI-021 — reaps every live ffmpeg when the host stops. Without this, transcode child
/// processes survived server shutdown/restart on Windows and Linux, kept burning CPU and
/// disk indefinitely, and their open handles made the next boot's temp-directory purge
/// fail silently. Registered as a hosted service so StopAsync runs during graceful
/// shutdown, before the process exits.
/// </summary>
public class TranscodeShutdownService : IHostedService
{
    private readonly TranscodeService _transcodeService;
    private readonly ILogger<TranscodeShutdownService> _logger;

    public TranscodeShutdownService(TranscodeService transcodeService, ILogger<TranscodeShutdownService> logger)
    {
        _transcodeService = transcodeService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Host stopping — killing all live transcode processes");
        _transcodeService.KillAllSessionProcesses();
        return Task.CompletedTask;
    }
}
