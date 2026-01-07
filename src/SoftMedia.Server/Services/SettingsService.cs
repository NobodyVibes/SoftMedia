using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services;

public interface ISettingsService
{
    Task<List<AppSetting>> GetAllSettingsAsync();
    Task UpdateSettingsAsync(List<AppSetting> settings);
    Task<T> GetSettingAsync<T>(string key, T defaultValue);
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

    public async Task InitializeDefaultsAsync()
    {
        var defaults = new List<AppSetting>
        {
            // Server
            new() { Key = "ServerName", Value = "SoftMedia Server", Group = "Server", Description = "Friendly name displayed to clients." },
            new() { Key = "Language", Value = "en-US", Group = "Server", Description = "UI language preference." },
            new() { Key = "LogLevel", Value = "Info", Group = "Server", Description = "Verbosity of server logs." },
            
            // Network
            new() { Key = "EnableRemoteAccess", Value = "false", Group = "Network", Description = "Toggle to allow connections outside the local subnet." },
            new() { Key = "SecureConnections", Value = "true", Group = "Network", Description = "Require SSL for all connections." },

            // Scanning
            new() { Key = "RealTimeMonitoring", Value = "true", Group = "Scanning", Description = "Use FileSystemWatcher to detect changes instantly." },
            new() { Key = "DailyRescan", Value = "03:00", Group = "Scanning", Description = "Time to perform a full integrity check." },
            
            // Transcoding
            new() { Key = "HardwareAcceleration", Value = "none", Group = "Transcoding", Description = "GPU encoder: none, nvidia (NVENC), amd (AMF), intel (QSV)." },
            new() { Key = "TranscodePreset", Value = "veryfast", Group = "Transcoding", Description = "Encoding speed preset (ultrafast to veryslow)." },
            new() { Key = "TranscodeThreadCount", Value = "0", Group = "Transcoding", Description = "CPU threads for FFmpeg (0 = auto-detect)." },
            new() { Key = "MaxTranscodeResolution", Value = "original", Group = "Transcoding", Description = "Maximum output resolution (720p, 1080p, 4k, original)." },
            new() { Key = "TranscodeCRF", Value = "23", Group = "Transcoding", Description = "Quality level 0-51 (lower = better, 23 = good default)." },
            new() { Key = "DisableTranscoding", Value = "false", Group = "Transcoding", Description = "Force direct play only, no video conversion." },
            new() { Key = "ToneMappingAlgorithm", Value = "hable", Group = "Transcoding", Description = "HDR to SDR conversion: hable, reinhard, mobius." },
            new() { Key = "EnableAV1Encoding", Value = "false", Group = "Transcoding", Description = "Enable AV1 encoding (requires RTX 40+, RX 7000+, or Intel Arc GPU)." },
            
            // Streaming (Jellyfin/Plex-inspired)
            new() { Key = "MaxStreamingBitrate", Value = "20000", Group = "Streaming", Description = "Maximum bitrate (kbps) for remote streaming. 0 = unlimited." },
            new() { Key = "MaxSimultaneousTranscodes", Value = "0", Group = "Streaming", Description = "Maximum concurrent transcode sessions. 0 = unlimited." },
            new() { Key = "DefaultStreamingQuality", Value = "auto", Group = "Streaming", Description = "Default quality for new streams (auto, 720p, 1080p, 4k, original)." },
            new() { Key = "ForceDirectPlayWhenPossible", Value = "true", Group = "Streaming", Description = "Prefer direct play over transcoding when client supports the format." },
            new() { Key = "DefaultAudioChannels", Value = "auto", Group = "Streaming", Description = "Default audio channel preference (auto, stereo, 5.1, 7.1)." },
            new() { Key = "PreserveHDR", Value = "true", Group = "Streaming", Description = "Pass through HDR to compatible clients (skips tone mapping)." },
            new() { Key = "OutputVideoCodec", Value = "auto", Group = "Streaming", Description = "Preferred output codec: auto, h264, hevc, av1. AV1 requires hardware." },
            
            // Subtitles

            new() { Key = "AutoSelectSubtitle", Value = "true", Group = "Subtitles", Description = "Automatically select tracks based on user language." },
            
            // Metadata
            new() { Key = "MovieProvider", Value = "Wikidata", Group = "Metadata", Description = "Primary API for Movie metadata." },
            new() { Key = "TVProvider", Value = "TVMaze", Group = "Metadata", Description = "Primary API for TV metadata." },
            new() { Key = "MusicProviderPrimary", Value = "Embedded", Group = "Metadata", Description = "Primary API for Music metadata." },
            new() { Key = "MusicProviderFallback", Value = "MusicBrainz", Group = "Metadata", Description = "Fallback API for Music metadata." },
            new() { Key = "BookProvider", Value = "Open Library", Group = "Metadata", Description = "Primary API for Book metadata." },
            new() { Key = "GameProvider", Value = "Wikidata", Group = "Metadata", Description = "Primary API for Game metadata." },
            new() { Key = "PhotoProvider", Value = "Exif", Group = "Metadata", Description = "Primary API for Photo metadata." },
            new() { Key = "AutoRefreshMetadata", Value = "true", Group = "Metadata", Description = "Fetch new data when files are updated." },
            
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
        await _context.SaveChangesAsync();
    }
}
