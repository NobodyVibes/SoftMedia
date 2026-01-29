using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Infrastructure;

public interface ISettingsService
{
    Task<List<AppSetting>> GetAllSettingsAsync();
    Task UpdateSettingsAsync(List<AppSetting> settings);
    Task<T> GetSettingAsync<T>(string key, T defaultValue);
    Task<AppSetting?> GetSettingAsync(string key);
    Task InitializeDefaultsAsync();
}

public class SettingsService : ISettingsService
{
    private readonly AppDbContext _context;
    private readonly ILogger<SettingsService> _logger;

    public SettingsService(AppDbContext context, ILogger<SettingsService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<List<AppSetting>> GetAllSettingsAsync()
    {
        return await _context.Settings.ToListAsync();
    }

    public async Task UpdateSettingsAsync(List<AppSetting> settings)
    {
        foreach (var setting in settings)
        {
            var existing = await _context.Settings.FindAsync(setting.Key);
            if (existing != null)
            {
                existing.Value = setting.Value;
            }
            else
            {
                _context.Settings.Add(setting);
            }
        }
        await _context.SaveChangesAsync();
    }

    public async Task<T> GetSettingAsync<T>(string key, T defaultValue)
    {
        var setting = await _context.Settings.FindAsync(key);
        if (setting == null) return defaultValue;

        try
        {
            return (T)Convert.ChangeType(setting.Value, typeof(T));
        }
        catch
        {
            return defaultValue;
        }
    }
    
    public async Task<AppSetting?> GetSettingAsync(string key)
    {
        return await _context.Settings.FindAsync(key);
    }

    public async Task InitializeDefaultsAsync()
    {
        var defaults = new List<AppSetting>
        {
            // Transcoding (server-side encoder configuration)
            new() { Key = "HardwareAcceleration", Value = "none", Group = "Transcoding", Description = "GPU encoder: none, nvidia (NVENC), amd (AMF), intel (QSV)." },
            new() { Key = "TranscodePreset", Value = "veryfast", Group = "Transcoding", Description = "Encoding speed preset (ultrafast to veryslow)." },
            new() { Key = "TranscodeThreadCount", Value = "0", Group = "Transcoding", Description = "CPU threads for FFmpeg (0 = auto-detect)." },
            new() { Key = "OutputVideoCodec", Value = "auto", Group = "Transcoding", Description = "Preferred output codec: auto, h264, hevc, av1. AV1 requires hardware." },
            new() { Key = "EnableAV1Encoding", Value = "false", Group = "Transcoding", Description = "Enable AV1 encoding (requires RTX 40+, RX 7000+, or Intel Arc GPU)." },
            new() { Key = "MaxSimultaneousTranscodes", Value = "0", Group = "Transcoding", Description = "Maximum concurrent transcode sessions. 0 = unlimited." },
            new() { Key = "EnableTranscoding", Value = "true", Group = "Transcoding", Description = "Enable video transcoding. If disabled, files will be served directly." },
            new() { Key = "ForceDirectPlayWhenPossible", Value = "true", Group = "Transcoding", Description = "Prefer direct play over transcoding when client supports the format." },
            
            // Streaming (client-facing playback quality settings)
            new() { Key = "MaxTranscodeResolution", Value = "original", Group = "Streaming", Description = "Maximum output resolution (720p, 1080p, 4k, original)." },
            new() { Key = "TranscodeCRF", Value = "23", Group = "Streaming", Description = "Quality level 0-51 (lower = better, 23 = good default)." },
            new() { Key = "PreserveHDR", Value = "true", Group = "Streaming", Description = "Pass through HDR to compatible clients (skips tone mapping)." },
            new() { Key = "ToneMappingAlgorithm", Value = "hable", Group = "Streaming", Description = "HDR to SDR conversion: hable, reinhard, mobius." },
            new() { Key = "MaxStreamingBitrate", Value = "20000", Group = "Streaming", Description = "Maximum bitrate (kbps) for remote streaming. 0 = unlimited." },
            new() { Key = "DefaultStreamingQuality", Value = "auto", Group = "Streaming", Description = "Default quality for new streams (auto, 720p, 1080p, 4k, original)." },
            new() { Key = "DefaultAudioChannels", Value = "auto", Group = "Streaming", Description = "Default audio channel preference (auto, stereo, 5.1, 7.1)." },
            
            // Scanning
            new() { Key = "EnableFileWatcher", Value = "true", Group = "Scanning", Description = "Automatically detect new files and update library. Disable for manual scanning only." },
            new() { Key = "MetadataRefreshIntervalDays", Value = "30", Group = "Scanning", Description = "Days between automatic refresh of metadata. 0 = disabled." },
            new() { Key = "MetadataRefreshMode", Value = "Running", Group = "Scanning", Description = "Running (active shows only), Variable (all metadata except images), or All (everything)." },
            new() { Key = "MetadataRefreshOnStartup", Value = "false", Group = "Scanning", Description = "Run metadata refresh when server starts." },
            
            // Metadata
            new() { Key = "MovieProvider", Value = "Wikidata", Group = "Metadata", Description = "Primary API for Movie metadata." },
            new() { Key = "OMDbApiKeyMode", Value = "softmedia", Group = "Metadata", Description = "OMDB API key mode: softmedia (default), custom, or disabled." },
            new() { Key = "OMDbApiKeyCustom", Value = "", Group = "Metadata", Description = "Custom OMDB API key when mode is set to custom." },
            new() { Key = "OMDbApiTier", Value = "free", Group = "Metadata", Description = "OMDb API tier: free (1K/day), basic (100K/day), standard (250K/day), pro (unlimited)." },
            new() { Key = "OMDbDailyCount", Value = "0", Group = "Internal", Description = "Internal: Daily OMDb request counter." },
            new() { Key = "OMDbCountDate", Value = "", Group = "Internal", Description = "Internal: Date of last OMDb counter reset (UTC)." },
            new() { Key = "TVProvider", Value = "TVMaze", Group = "Metadata", Description = "Primary API for TV metadata." },
            new() { Key = "MusicProviderPrimary", Value = "Embedded", Group = "Metadata", Description = "Primary API for Music metadata." },
            new() { Key = "MusicProviderFallback", Value = "MusicBrainz", Group = "Metadata", Description = "Fallback API for Music metadata." },
            new() { Key = "BookProvider", Value = "Open Library", Group = "Metadata", Description = "Primary API for Book metadata." },
            new() { Key = "GameProvider", Value = "Wikidata", Group = "Metadata", Description = "Primary API for Game metadata." },
            new() { Key = "PhotoProvider", Value = "Exif", Group = "Metadata", Description = "Primary API for Photo metadata." },
            
            // Users
            new() { Key = "AllowUserSignup", Value = "Disabled", Group = "Users", Description = "Control public registration (Disabled, InviteOnly, Enabled)." },
        };

        foreach (var def in defaults)
        {
            if (!await _context.Settings.AnyAsync(s => s.Key == def.Key))
            {
                _context.Settings.Add(def);
            }
        }
        // Cleanup obsolete settings
        var obsoleteKeys = new[] { "MetadataRefreshIntervalHours" };
        foreach (var key in obsoleteKeys)
        {
            var obsolete = await _context.Settings.FindAsync(key);
            if (obsolete != null)
            {
                _context.Settings.Remove(obsolete);
            }
        }
        
        await _context.SaveChangesAsync();
    }
}
