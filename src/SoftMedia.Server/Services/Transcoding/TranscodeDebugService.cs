using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs; // For ClientCapabilities
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Transcoding.Models;
using System.Text.Json;

namespace SoftMedia.Server.Services.Transcoding;

public interface ITranscodeDebugService
{
    Task<object> GetDebugInfoAsync(Guid mediaId, Guid userId, ClientCapabilities? clientCaps, int? sub, bool isAdmin);
}

public class TranscodeDebugService : ITranscodeDebugService
{
    private readonly ITranscodeSessionManager _sessionManager;
    private readonly ISettingsService _settingsService;
    private readonly IStreamPlanService _streamPlanService;
    private readonly IBinaryLocationService _binaryLocationService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TranscodeDebugService> _logger;

    public TranscodeDebugService(
        ITranscodeSessionManager sessionManager,
        ISettingsService settingsService,
        IStreamPlanService streamPlanService,
        IBinaryLocationService binaryLocationService,
        IServiceScopeFactory scopeFactory,
        ILogger<TranscodeDebugService> logger)
    {
        _sessionManager = sessionManager;
        _settingsService = settingsService;
        _streamPlanService = streamPlanService;
        _binaryLocationService = binaryLocationService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<object> GetDebugInfoAsync(Guid mediaId, Guid userId, ClientCapabilities? clientCaps, int? sub, bool isAdmin)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;

        var sessionKey = new TranscodeSessionKey(mediaId, userId, sub);
        var session = _sessionManager.GetSession(sessionKey);
        
        // Fetch individual server settings
        var outputVideoCodec = await _settingsService.GetSettingAsync("OutputVideoCodec", "auto");
        var maxResolution = await _settingsService.GetSettingAsync("MaxTranscodeResolution", "original");
        var preserveHdrStr = await _settingsService.GetSettingAsync("PreserveHDR", "true");
        var preserveHdr = preserveHdrStr == "true";
        var enableAv1Str = await _settingsService.GetSettingAsync("EnableAV1Encoding", "false"); 
        var enableAv1 = enableAv1Str == "true";
        var hwAccel = await _settingsService.GetSettingAsync("HardwareAcceleration", "none");
        var preset = await _settingsService.GetSettingAsync("TranscodePreset", "veryfast");
        var crf = await _settingsService.GetSettingAsync("TranscodeCRF", "23");
        var audioChannels = await _settingsService.GetSettingAsync("DefaultAudioChannels", "auto");
        
        // Get source media info
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mediaItem = await dbContext.MediaItems.FindAsync(mediaId);
        
        // Compute Stream Plan (Backend Decision Logic)
        var streamPlan = await _streamPlanService.ComputeStreamPlanAsync(mediaId, mediaItem, clientCaps ?? new ClientCapabilities(), null);

        if (session == null)
        {
            return new
            {
                playbackMode = "DirectPlay",
                isTranscoding = false,
                message = "No active transcode session - likely direct play",
                clientCapabilities = clientCaps != null ? new
                {
                    videoCodecs = clientCaps.VideoCodecs,
                    audioCodecs = clientCaps.AudioCodecs,
                    supportsHdr = clientCaps.SupportsHdr,
                    maxAudioChannels = clientCaps.MaxAudioChannels,
                    requestedQuality = clientCaps.RequestedQuality,
                    supportedSubtitleFormats = clientCaps.SupportedSubtitleFormats
                } : null,
                serverSettings = isAdmin ? new
                {
                    outputVideoCodec,
                    maxResolution,
                    preserveHdr,
                    enableAv1,
                    hardwareAcceleration = hwAccel,
                    targetAudioChannels = audioChannels
                } : null, // Hide settings from non-admins
                selectedSubtitleTrack = sub
            };
        }
        
        // Get probe info from transcoded output
        var probeInfo = await ProbeTranscodedOutput(session, isAdmin);
        
        // Build comprehensive debug response
        return new
        {
            playbackMode = "Transcode",
            isTranscoding = true,
            
            // 1. Client Capabilities - what the browser/client sent
            clientCapabilities = clientCaps != null ? new
            {
                videoCodecs = clientCaps.VideoCodecs,
                audioCodecs = clientCaps.AudioCodecs,
                supportsHdr = clientCaps.SupportsHdr,
                maxAudioChannels = clientCaps.MaxAudioChannels,
                maxResolution = clientCaps.MaxResolution,
                maxBitrate = clientCaps.MaxBitrate,
                requestedQuality = clientCaps.RequestedQuality,
                supportedContainers = clientCaps.SupportedContainers,
                supportedSubtitleFormats = clientCaps.SupportedSubtitleFormats,
                displaySupportsHdr = clientCaps.DisplaySupportsHdr,
                codecSupportsHdr = clientCaps.CodecSupportsHdr
            } : null,
            
            // 2. Server Settings - admin-configured transcode settings
            serverSettings = isAdmin ? new
            {
                outputVideoCodec,
                maxResolution,
                preserveHdr,
                enableAv1,
                hardwareAcceleration = hwAccel,
                preset,
                crf,
                targetAudioChannels = audioChannels
            } : null,
            
            // 3. Source Media Info - what was detected from the source file
            sourceMedia = mediaItem != null ? new
            {
                videoCodec = mediaItem.VideoCodec,
                audioCodec = mediaItem.AudioCodec,
                resolution = mediaItem.Resolution,
                container = mediaItem.Container,
                duration = mediaItem.Duration,
                isHdr = session.IsSourceHdr
            } : null,
            
            // 4. Session Status - real-time state
            sessionStatus = new
            {
                state = session.State.ToString(),
                bufferSeconds = (session.LatestSegmentIndex - session.ClientSegmentIndex) * 6, // approx
                isSuspended = session.IsSuspended,
                startTime = session.SessionStartTime,
                targetResolution = session.TargetResolution,
                targetCodec = session.TargetCodec,
                isBitmapSubtitle = session.IsBitmapSubtitle
            },
            
            // 5. Decision - logic mapped for frontend
            decision = new 
            {
                targetCodec = session.TargetCodec ?? streamPlan.VideoCodec,
                targetResolution = session.TargetResolution ?? streamPlan.Resolution,
                preserveHdr = session.PreserveHdr,
                toneMapped = session.IsSourceHdr && !session.PreserveHdr,
                subtitleBurnIn = session.BurnSubtitles || (session.IsBitmapSubtitle && sub.HasValue),
                subtitleTrack = sub,
                subtitleLanguage = session.SubtitleLanguage
            },

            // 6. Output Probe - actual file analysis (ground truth)
            probe = probeInfo
        };
    }

    private async Task<object> ProbeTranscodedOutput(TranscodeSession session, bool includeSensitiveData)
    {
        try
        {
            // Try init.mp4 first (fMP4 mode), then fall back to first segment
            var initPath = Path.Combine(session.SessionDirectory, "init.mp4");

            // Look for segment_000.ts or segment_000.m4s (standard formatting usually segment_%03d)
            var probeFile = File.Exists(initPath) 
                ? initPath 
                : Directory.GetFiles(session.SessionDirectory, "segment_000.*").FirstOrDefault() 
                  ?? Directory.GetFiles(session.SessionDirectory, "*0.ts").FirstOrDefault();
                
            if (probeFile == null)
            {
                return new { error = "No transcode output files found yet" };
            }
            
            var ffprobePath = _binaryLocationService.ResolveFFprobePath();
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v quiet -print_format json -show_streams \"{probeFile}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null) return new { error = "Failed to start FFprobe" };
            
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            
            // Parse JSON and extract video and audio stream info
            using var probeData = JsonDocument.Parse(output);
            var streams = probeData.RootElement.GetProperty("streams");
            
            // Find video and audio streams
            string? videoCodec = null, pixelFormat = null, colorSpace = null, colorTransfer = null, colorPrimaries = null, resolution = null;
            bool hasHdrMetadata = false, isHdr = false;
            string? audioCodec = null;
            int? audioChannels = null;
            
            foreach (var stream in streams.EnumerateArray())
            {
                if (stream.TryGetProperty("codec_type", out var codecType))
                {
                    var type = codecType.GetString();
                    
                    if (type == "video" && videoCodec == null)
                    {
                        videoCodec = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                        pixelFormat = stream.TryGetProperty("pix_fmt", out var pf) ? pf.GetString() : null;
                        colorSpace = stream.TryGetProperty("color_space", out var cs) ? cs.GetString() : null;
                        colorTransfer = stream.TryGetProperty("color_transfer", out var ct) ? ct.GetString() : null;
                        colorPrimaries = stream.TryGetProperty("color_primaries", out var cp) ? cp.GetString() : null;
                        var width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                        var height = probeFile.Contains("init.mp4") ? 0 : (stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0);
                        if (height > 0) resolution = $"{width}x{height}";
                        
                        // Check side data for HDR metadata
                        hasHdrMetadata = stream.TryGetProperty("side_data_list", out var sideData) &&
                            sideData.EnumerateArray().Any(sd => 
                                sd.TryGetProperty("side_data_type", out var sdType) &&
                                (sdType.GetString()?.Contains("Mastering") == true || 
                                 sdType.GetString()?.Contains("Content light") == true));
                                 
                        isHdr = colorTransfer == "smpte2084" || colorSpace == "bt2020nc";
                    }
                    else if (type == "audio" && audioCodec == null)
                    {
                        audioCodec = stream.TryGetProperty("codec_name", out var acn) ? acn.GetString() : null;
                        audioChannels = stream.TryGetProperty("channels", out var ch) ? ch.GetInt32() : null;
                    }
                }
            }
            
            if (videoCodec == null)
            {
                return new { error = "No video stream found in probe data" };
            }
            
            return new
            {
                filePath = includeSensitiveData ? probeFile : Path.GetFileName(probeFile),
                videoCodec,
                pixelFormat,
                colorSpace,
                colorTransfer,
                colorPrimaries,
                resolution,
                hasHdrMetadata,
                isHdr,
                audioCodec,
                audioChannels
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to probe transcoded output");
            return new { error = ex.Message };
        }
    }
}
