using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;

namespace SoftMedia.Server.Services.Background;

public class HeroCacheWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HeroCacheWorker> _logger;
    private readonly IScheduledTaskRegistry _taskRegistry;
    private readonly TimeSpan _targetTime = new TimeSpan(0, 1, 0); // 12:01 AM (00:01 in 24-hour format)

    public HeroCacheWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<HeroCacheWorker> logger,
        IScheduledTaskRegistry taskRegistry)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _taskRegistry = taskRegistry;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HeroCacheWorker started. Scheduled to run daily at {TargetTime}", _targetTime);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            var nextRun = CalculateNextRunTime(now);
            var delay = nextRun - now;
            _taskRegistry.SetNextRun(ScheduledTaskNames.HeroCache, nextRun.ToUniversalTime());

            _logger.LogInformation("Next hero cache update scheduled for {NextRun} (in {Delay})",
                nextRun.ToString("yyyy-MM-dd HH:mm:ss"),
                delay);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Execute the cache update
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var recommendationService = scope.ServiceProvider.GetRequiredService<IRecommendationService>();

                _logger.LogInformation("Running scheduled hero cache update at {Time}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                await recommendationService.UpdateHeroCacheAsync();
                _logger.LogInformation("Scheduled hero cache update completed successfully");
                _taskRegistry.Report(ScheduledTaskNames.HeroCache, "Success", sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during scheduled hero cache update");
                _taskRegistry.Report(ScheduledTaskNames.HeroCache, "Failed", sw.ElapsedMilliseconds, ex.Message);
            }
        }
        
        _logger.LogInformation("HeroCacheWorker stopped.");
    }

    private DateTime CalculateNextRunTime(DateTime now)
    {
        // Start with today at the target time (12:01 AM)
        var nextRun = now.Date.Add(_targetTime);

        // If we've already passed 12:01 AM today, schedule for tomorrow
        if (now >= nextRun)
        {
            nextRun = nextRun.AddDays(1);
        }

        return nextRun;
    }
}
