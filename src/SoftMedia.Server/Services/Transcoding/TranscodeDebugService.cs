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
    /// <param name="clientIp">Resolved client IP so the debug plan applies the same LAN/WAN
    /// tier the real plan would (QS-WI-003).</param>
    /// <param name="userPolicy">The caller's per-user streaming limits, for the same reason.</param>
    Task<object> GetDebugInfoAsync(Guid mediaId, Guid userId, ClientCapabilities? clientCaps, int? sub, bool isAdmin, string? sid = null,
        System.Net.IPAddress? clientIp = null, Services.Media.UserStreamingPolicy? userPolicy = null);
}

public class TranscodeDebugService : ITranscodeDebugService
{
    private readonly ITranscodeSessionManager _sessionManager;
    private readonly ISettingsService _settingsService;
    private readonly IStreamPlanService _streamPlanService;
    private readonly IBinaryLocationService _binaryLocationService;
    private readonly IOpenClToneMapProbe _openClProbe;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TranscodeDebugService> _logger;

    public TranscodeDebugService(
        ITranscodeSessionManager sessionManager,
        ISettingsService settingsService,
        IStreamPlanService streamPlanService,
        IBinaryLocationService binaryLocationService,
        IOpenClToneMapProbe openClProbe,
        IServiceScopeFactory scopeFactory,
        ILogger<TranscodeDebugService> logger)
    {
        _sessionManager = sessionManager;
        _settingsService = settingsService;
        _streamPlanService = streamPlanService;
        _binaryLocationService = binaryLocationService;
        _openClProbe = openClProbe;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<object> GetDebugInfoAsync(Guid mediaId, Guid userId, ClientCapabilities? clientCaps, int? sub, bool isAdmin, string? sid = null,
        System.Net.IPAddress? clientIp = null, Services.Media.UserStreamingPolicy? userPolicy = null)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;

        // SR-WI-024: sessions are keyed WITH the client's StreamId (sid) — building the key
        // without it made every sid-keyed lookup miss, so the panel reported "likely direct
        // play" for real transcodes. Pass sid through exactly as StartTranscodeAsync keys it.
        var sessionKey = new TranscodeSessionKey(mediaId, userId, sub, sid);
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
        var repository = scope.ServiceProvider.GetRequiredService<IMediaRepository>();
        var mediaItem = await repository.GetByIdAsync(mediaId);
        
        if (mediaItem == null)
        {
            return new { error = "Media item not found" };
        }

        // Compute Stream Plan (Backend Decision Logic). QS-WI-003: the debug plan runs with
        // the caller's real network class and user policy so the reason-code chain below
        // matches what the play path actually decided.
        var streamPlan = await _streamPlanService.ComputeStreamPlanAsync(
            mediaId, mediaItem, clientCaps ?? new ClientCapabilities(), string.Empty, clientIp, userPolicy);

        if (session == null)
        {
            return new
            {
                playbackMode = "DirectPlay",
                isTranscoding = false,
                message = "No active transcode session - likely direct play",
                reasonCodes = streamPlan.ReasonCodes,
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

        // SR-WI-023/SR-WI-024: report the ACTUAL tone-map decision the profile builder makes,
        // not the old `IsSourceHdr && !PreserveHdr` guess (which lied for remux and, before the
        // software chain existed, for every non-NVIDIA transcode). Mirrors the builder: the
        // pipeline (CUDA on nvidia, software zscale+tonemap otherwise) engages for an HDR source
        // unless HDR passthrough is BOTH requested and carriable (hevc/av1 output — h264 output
        // overrides preserve), and subtitle burn-in forces tone mapping even then. Remux/copy
        // never tone-maps.
        var subtitleBurnIn = session.BurnSubtitles || (session.IsBitmapSubtitle && sub.HasValue);
        var effectiveCodec = session.TargetCodec ?? outputVideoCodec;
        var preserveEngaged = session.PreserveHdr
            && TranscodeProfileBuilder.CodecCanCarryHdr(effectiveCodec) && !subtitleBurnIn;
        // QS-WI-012: report the tone-map decision AND its pipeline through the same single
        // authority the builder and planner consult (remux never tone-maps).
        var openClAvailable = session.IsSourceHdr && !session.IsRemux
            && hwAccel.ToLowerInvariant() is "intel" or "amd"
            && await _openClProbe.IsAvailableAsync();
        var toneMapPipeline = session.IsRemux
            ? ToneMapPipeline.None
            : TranscodeProfileBuilder.SelectToneMapPipeline(
                hwAccel, session.IsSourceHdr, session.PreserveHdr, effectiveCodec,
                subtitleBurnIn, openClAvailable);
        var toneMapped = toneMapPipeline != ToneMapPipeline.None;

        // Build comprehensive debug response
        return new
        {
            playbackMode = "Transcode",
            isTranscoding = true,
            // QS-WI-003: the full decision chain (clamps, codec/HDR causes) for the debug panel.
            reasonCodes = streamPlan.ReasonCodes,

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
                preserveHdr = preserveEngaged,
                toneMapped,
                // QS-WI-012: which pipeline runs the tone-map ("cuda" | "opencl" | "software").
                toneMapPipeline = toneMapped ? toneMapPipeline.ToString().ToLowerInvariant() : null,
                subtitleBurnIn,
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
