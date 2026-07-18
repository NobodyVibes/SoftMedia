using System.Diagnostics;
using SoftMedia.Server.Services.Abstractions;
using Microsoft.Extensions.Logging;

namespace SoftMedia.Server.Services.Transcoding;

public interface IFFmpegService
{
    Task<MediaProbeResult?> ProbeMediaAsync(string path);
    Task<ProcessStartInfo> GetTranscodeArgumentsAsync(string inputPath, string outputDir, string segmentPrefix);
    
    /// <summary>
    /// Get transcode arguments with optional subtitle burn-in and seek position.
    /// </summary>
    Task<ProcessStartInfo> GetTranscodeArgumentsAsync(string inputPath, string outputDir, string segmentPrefix, int? subtitleTrackIndex, double? seekPosition);
    
    /// <summary>
    /// Get transcode arguments with subtitle, seek, read rate, target resolution, codec, HDR, and audio track settings.
    /// </summary>
    Task<ProcessStartInfo> GetTranscodeArgumentsAsync(string inputPath, string outputDir, string segmentPrefix, int? subtitleTrackIndex, double? seekPosition, double? readRate, string? targetResolution = null, string? targetCodec = null, bool preserveHdr = false, int? audioTrackIndex = null, int? maxBitrate = null, bool audioCopy = false, string? audioCodec = null, int audioChannels = 0);

    /// <summary>
    /// Get REMUX (stream-copy) arguments — copy compatible A/V into fMP4 HLS, no re-encode (R-WI-003).
    /// </summary>
    ProcessStartInfo GetRemuxArguments(string inputPath, string outputDir, string segmentPrefix, double? seekPosition = null, int? audioTrackIndex = null);

    Task<bool> ExtractSubtitleToVttAsync(string inputPath, int subtitleStreamIndex, string outputPath);
    
    /// <summary>
    /// Convert an absolute stream index to a subtitle-relative index.
    /// </summary>
    Task<int> GetSubtitleStreamIndexAsync(string inputPath, int absoluteStreamIndex);
    
    bool OffsetWebVttTimestamps(string vttPath, double offsetSeconds);
    
    Task<string?> ProbeSubtitleCodecAsync(string inputPath, int subtitleTrackIndex);
    
    Task<string?> ProbeSubtitleLanguageAsync(string inputPath, int subtitleTrackIndex);

    Task<bool> IsTranscodingDisabledAsync();
}

public class FFmpegService : IFFmpegService
{
    private readonly ILogger<FFmpegService> _logger;
    private readonly ISettingsService _settingsService;
    private readonly IMediaProbeService _mediaProbeService;
    private readonly ISubtitleService _subtitleService;
    private readonly ITranscodeProfileBuilder _transcodeProfileBuilder;

    public FFmpegService(
        ILogger<FFmpegService> logger, 
        ISettingsService settingsService,
        IMediaProbeService mediaProbeService,
        ISubtitleService subtitleService,
        ITranscodeProfileBuilder transcodeProfileBuilder)
    {
        _logger = logger;
        _settingsService = settingsService;
        _mediaProbeService = mediaProbeService;
        _subtitleService = subtitleService;
        _transcodeProfileBuilder = transcodeProfileBuilder;
    }

    public async Task<MediaProbeResult?> ProbeMediaAsync(string path)
    {
        return await _mediaProbeService.ProbeMediaAsync(path);
    }

    public async Task<ProcessStartInfo> GetTranscodeArgumentsAsync(string inputPath, string outputDir, string segmentPrefix)
    {
        var settings = await LoadSettingsAsync();
        return await _transcodeProfileBuilder.BuildTranscodeArgumentsAsync(inputPath, outputDir, segmentPrefix, settings);
    }

    public async Task<ProcessStartInfo> GetTranscodeArgumentsAsync(string inputPath, string outputDir, string segmentPrefix, int? subtitleTrackIndex, double? seekPosition)
    {
        var settings = await LoadSettingsAsync();
        return await _transcodeProfileBuilder.BuildTranscodeArgumentsAsync(inputPath, outputDir, segmentPrefix, settings, subtitleTrackIndex, seekPosition);
    }

    public ProcessStartInfo GetRemuxArguments(string inputPath, string outputDir, string segmentPrefix, double? seekPosition = null, int? audioTrackIndex = null)
        => _transcodeProfileBuilder.BuildRemuxArguments(inputPath, outputDir, segmentPrefix, seekPosition, audioTrackIndex);

    public async Task<ProcessStartInfo> GetTranscodeArgumentsAsync(string inputPath, string outputDir, string segmentPrefix, int? subtitleTrackIndex, double? seekPosition, double? readRate, string? targetResolution = null, string? targetCodec = null, bool preserveHdr = false, int? audioTrackIndex = null, int? maxBitrate = null, bool audioCopy = false, string? audioCodec = null, int audioChannels = 0)
    {
        var settings = await LoadSettingsAsync();

        // Override settings with URL parameters if explicitly specified
        if (!string.IsNullOrEmpty(targetResolution))
        {
            settings.MaxResolution = targetResolution;
        }
        if (!string.IsNullOrEmpty(targetCodec))
        {
            settings.OutputVideoCodec = targetCodec;
            _logger.LogDebug("Using URL-specified codec: {Codec}", targetCodec);
        }
        settings.PreserveHDR = preserveHdr;

        return await _transcodeProfileBuilder.BuildTranscodeArgumentsAsync(inputPath, outputDir, segmentPrefix, settings, subtitleTrackIndex, seekPosition, readRate, audioTrackIndex, maxBitrate, audioCopy, audioCodec, audioChannels);
    }

    public async Task<bool> ExtractSubtitleToVttAsync(string inputPath, int subtitleStreamIndex, string outputPath)
    {
        return await _subtitleService.ExtractSubtitleToVttAsync(inputPath, subtitleStreamIndex, outputPath);
    }

    public async Task<int> GetSubtitleStreamIndexAsync(string inputPath, int absoluteStreamIndex)
    {
        return await _subtitleService.GetSubtitleStreamIndexAsync(inputPath, absoluteStreamIndex);
    }

    public bool OffsetWebVttTimestamps(string vttPath, double offsetSeconds)
    {
        return _subtitleService.OffsetWebVttTimestamps(vttPath, offsetSeconds);
    }

    public async Task<string?> ProbeSubtitleCodecAsync(string inputPath, int subtitleTrackIndex)
    {
        return await _mediaProbeService.ProbeSubtitleCodecAsync(inputPath, subtitleTrackIndex);
    }

    public async Task<string?> ProbeSubtitleLanguageAsync(string inputPath, int subtitleTrackIndex)
    {
        return await _mediaProbeService.ProbeSubtitleLanguageAsync(inputPath, subtitleTrackIndex);
    }

    public async Task<bool> IsTranscodingDisabledAsync()
    {
        var enableStr = await _settingsService.GetSettingAsync("EnableTranscoding", "true");
        // Returns true (Disabled) only if parsing succeeds AND value is false.
        // If parsing fails (invalid), it defaults to Enabled (returns false).
        return bool.TryParse(enableStr, out var enable) && !enable;
    }

    public static bool IsBitmapSubtitleCodec(string? codec)
    {
        if (string.IsNullOrEmpty(codec)) return false;
        
        var bitmapCodecs = new[] 
        { 
            "hdmv_pgs_subtitle", "pgs", 
            "dvd_subtitle", "dvdsub", 
            "xsub",
            "dvb_subtitle"
        };
        
        return bitmapCodecs.Contains(codec.ToLowerInvariant());
    }

    private async Task<TranscodeSettings> LoadSettingsAsync()
    {
        var enableStr = await _settingsService.GetSettingAsync("EnableTranscoding", "true");
        var hwAccel = await _settingsService.GetSettingAsync("HardwareAcceleration", "none");
        var preset = await _settingsService.GetSettingAsync("TranscodePreset", "veryfast");
        var threadCountStr = await _settingsService.GetSettingAsync("TranscodeThreadCount", "0");
        var maxRes = await _settingsService.GetSettingAsync("MaxTranscodeResolution", "original");
        var crfStr = await _settingsService.GetSettingAsync("TranscodeCRF", "23");
        var outputCodec = await _settingsService.GetSettingAsync("OutputVideoCodec", "auto");
        var toneMapAlgo = await _settingsService.GetSettingAsync("ToneMappingAlgorithm", "hable");
        var preserveHdrStr = await _settingsService.GetSettingAsync("PreserveHDR", "false");
        
        return new TranscodeSettings
        {
            EnableTranscoding = !bool.TryParse(enableStr, out var enable) || enable,
            HardwareAcceleration = hwAccel,
            Preset = preset,
            ThreadCount = int.TryParse(threadCountStr, out var tc) ? tc : 0,
            MaxResolution = maxRes,
            CRF = int.TryParse(crfStr, out var crf) ? crf : 23,
            OutputVideoCodec = outputCodec,
            ToneMappingAlgorithm = toneMapAlgo,
            PreserveHDR = bool.TryParse(preserveHdrStr, out var pHdr) && pHdr
        };
    }
}
