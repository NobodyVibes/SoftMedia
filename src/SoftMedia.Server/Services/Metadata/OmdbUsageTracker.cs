using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Metadata;

/// <summary>
/// Tracks OMDb daily API usage. Registered as a singleton so the count survives
/// the scoped/transient lifetime of <see cref="OMDbProvider"/> and stays accurate
/// when the metadata queue fetches movies concurrently.
/// </summary>
public interface IOmdbUsageTracker
{
    /// <summary>
    /// Atomically reserves one request against the daily limit. Returns false
    /// (without counting) when the limit is already reached. The counter resets
    /// when the UTC date changes.
    /// </summary>
    Task<bool> TryRecordRequestAsync(int limit);

    /// <summary>Number of requests recorded for the current UTC day.</summary>
    Task<int> GetUsedTodayAsync();
}

/// <summary>
/// In-memory counter guarded by a single async gate, persisted to the settings
/// table (OMDbDailyCount / OMDbCountDate) so it survives restarts. The in-memory
/// value is authoritative while the process runs; persistence is best-effort and
/// a failed write never blocks a request or loses the in-memory increment.
/// </summary>
public class OmdbUsageTracker : IOmdbUsageTracker
{
    private const string CountKey = "OMDbDailyCount";
    private const string DateKey = "OMDbCountDate";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OmdbUsageTracker> _logger;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _loaded;
    private int _count;
    private string _date = "";

    public OmdbUsageTracker(
        IServiceScopeFactory scopeFactory,
        ILogger<OmdbUsageTracker> logger,
        Func<DateTime>? utcNow = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<bool> TryRecordRequestAsync(int limit)
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedAsync();
            RollOverIfNewDay();

            if (_count >= limit)
                return false;

            _count++;
            await PersistAsync();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> GetUsedTodayAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedAsync();
            RollOverIfNewDay();
            return _count;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RollOverIfNewDay()
    {
        var today = _utcNow().ToString("yyyy-MM-dd");
        if (_date != today)
        {
            _count = 0;
            _date = today;
        }
    }

    private async Task EnsureLoadedAsync()
    {
        if (_loaded) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

            var countStr = await settings.GetSettingAsync(CountKey, "0");
            _date = await settings.GetSettingAsync(DateKey, "");
            int.TryParse(countStr, out _count);
        }
        catch (Exception ex)
        {
            // Start from zero rather than blocking metadata; worst case we
            // re-count a partial day conservatively low once, on boot only.
            _logger.LogError(ex, "Failed to load persisted OMDb usage; starting counter at 0");
            _count = 0;
            _date = "";
        }

        _loaded = true;
    }

    private async Task PersistAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            await settings.UpdateSettingsAsync(new List<AppSetting>
            {
                new() { Key = CountKey, Value = _count.ToString(), Group = "Internal" },
                new() { Key = DateKey, Value = _date, Group = "Internal" }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist OMDb usage count {Count}; in-memory count remains accurate", _count);
        }
    }
}
