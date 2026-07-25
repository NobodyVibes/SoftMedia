using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Media;

public interface IMediaProbeService
{
    Task<MediaProbeResult?> ProbeMediaAsync(string path);
    Task<string?> ProbeSubtitleCodecAsync(string inputPath, int subtitleTrackIndex);
    Task<string?> ProbeSubtitleLanguageAsync(string inputPath, int subtitleTrackIndex);
}

public class MediaProbeService : IMediaProbeService
{
    private readonly ILogger<MediaProbeService> _logger;
    private readonly IProcessRunner _processRunner;
    private readonly IBinaryLocationService _binaryLocationService;

    public MediaProbeService(
        ILogger<MediaProbeService> logger,
        IProcessRunner processRunner,
        IBinaryLocationService binaryLocationService)
    {
        _logger = logger;
        _processRunner = processRunner;
        _binaryLocationService = binaryLocationService;
    }

    public async Task<MediaProbeResult?> ProbeMediaAsync(string path)
    {
        try
        {
            var ffprobePath = _binaryLocationService.ResolveFFprobePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v quiet -print_format json -show_format -show_streams -show_chapters \"{path}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var output = await _processRunner.RunProcessAsync(startInfo);
            if (string.IsNullOrEmpty(output)) return null;

            using var doc = JsonDocument.Parse(output);
            var result = new MediaProbeResult();

            if (doc.RootElement.TryGetProperty("format", out var format))
            {
                if (format.TryGetProperty("duration", out var duration))
                {
                    if (double.TryParse(duration.GetString(), out var d))
                        result.Duration = d;
                }
                
                // Extract overall bitrate
                if (format.TryGetProperty("bit_rate", out var br))
                {
                    if (long.TryParse(br.GetString(), out var bitrate))
                        result.Bitrate = bitrate;
                }
            }

            if (doc.RootElement.TryGetProperty("streams", out var streams))
            {
                result.AudioTracks = new List<AudioTrackInfo>();
                result.SubtitleTracks = new List<SubtitleTrackInfo>();
                int audioIndex = 0;
                int subtitleIndex = 0;

                foreach (var stream in streams.EnumerateArray())
                {
                    if (!stream.TryGetProperty("codec_type", out var codecType)) continue;
                    var type = codecType.GetString();

                    if (type == "video" && result.VideoCodec == null)
                    {
                        if (stream.TryGetProperty("codec_name", out var codec))
                            result.VideoCodec = codec.GetString();
                        if (stream.TryGetProperty("width", out var w) && stream.TryGetProperty("height", out var h))
                        {
                            result.Width = w.GetInt32();
                            result.Height = h.GetInt32();
                            result.Resolution = $"{result.Width}x{result.Height}";
                        }
                        if (stream.TryGetProperty("pix_fmt", out var pixFmt))
                            result.PixelFormat = pixFmt.GetString();
                        if (stream.TryGetProperty("color_transfer", out var transfer))
                            result.ColorTransfer = transfer.GetString();
                        if (stream.TryGetProperty("field_order", out var fieldOrder))
                            result.FieldOrder = fieldOrder.GetString();

                        // Extract bit depth from bits_per_raw_sample or pix_fmt
                        if (stream.TryGetProperty("bits_per_raw_sample", out var bitsRaw))
                        {
                            if (int.TryParse(bitsRaw.GetString(), out var bits))
                                result.BitDepth = bits;
                        }
                        else if (result.PixelFormat != null)
                        {
                            // Infer from pixel format (e.g., yuv420p10le = 10-bit)
                            result.BitDepth = InferBitDepthFromPixelFormat(result.PixelFormat);
                        }

                        // Detect HDR format
                        result.HdrFormat = DetectHdrFormat(stream, result.ColorTransfer);

                        // Parse frame rate
                        if (stream.TryGetProperty("avg_frame_rate", out var avgFr))
                        {
                            var frStr = avgFr.GetString();
                            if (!string.IsNullOrEmpty(frStr))
                            {
                                var parts = frStr.Split('/');
                                if (parts.Length == 2 && 
                                    double.TryParse(parts[0], out var num) && 
                                    double.TryParse(parts[1], out var den) && 
                                    den > 0)
                                {
                                    result.FrameRate = num / den;
                                }
                                else if (double.TryParse(frStr, out var fps))
                                {
                                    result.FrameRate = fps;
                                }
                            }
                        }
                    }
                    else if (type == "audio")
                    {
                        var audioTrack = new AudioTrackInfo { Index = audioIndex++ };
                        if (stream.TryGetProperty("index", out var absIdx) && absIdx.TryGetInt32(out var absIdxVal))
                            audioTrack.StreamIndex = absIdxVal;

                        if (stream.TryGetProperty("codec_name", out var codec))
                            audioTrack.Codec = codec.GetString();
                        if (stream.TryGetProperty("channels", out var channels))
                            audioTrack.Channels = channels.GetInt32();
                        if (stream.TryGetProperty("channel_layout", out var layout))
                            audioTrack.ChannelLayout = layout.GetString();
                        if (stream.TryGetProperty("tags", out var audioTags))
                        {
                            if (audioTags.TryGetProperty("language", out var lang))
                                audioTrack.Language = lang.GetString();
                            if (audioTags.TryGetProperty("title", out var title))
                                audioTrack.Title = title.GetString();
                        }
                        if (stream.TryGetProperty("disposition", out var audioDisp))
                        {
                            if (audioDisp.TryGetProperty("default", out var def))
                                audioTrack.IsDefault = def.GetInt32() == 1;
                        }

                        result.AudioTracks.Add(audioTrack);

                        // Set primary audio codec and channels (first track)
                        if (result.AudioCodec == null)
                        {
                            result.AudioCodec = audioTrack.Codec;
                            result.AudioChannels = audioTrack.Channels;
                        }
                    }
                    else if (type == "subtitle")
                    {
                        var subTrack = new SubtitleTrackInfo { Index = subtitleIndex++ };
                        
                        if (stream.TryGetProperty("codec_name", out var codec))
                            subTrack.Codec = codec.GetString();
                        if (stream.TryGetProperty("tags", out var subTags))
                        {
                            if (subTags.TryGetProperty("language", out var lang))
                                subTrack.Language = lang.GetString();
                            if (subTags.TryGetProperty("title", out var title))
                                subTrack.Title = title.GetString();
                        }
                        if (stream.TryGetProperty("disposition", out var subDisp))
                        {
                            if (subDisp.TryGetProperty("default", out var def))
                                subTrack.IsDefault = def.GetInt32() == 1;
                            if (subDisp.TryGetProperty("forced", out var forced))
                                subTrack.IsForced = forced.GetInt32() == 1;
                        }

                        result.SubtitleTracks.Add(subTrack);
                    }
                }
            }

            // Parse chapters. Marker semantics (which chapter is an intro/credits and the
            // resulting timecodes) live in ChapterMarkerMapper — CM-WI-002 retired the
            // inline credits-title matching that used to happen here so the scan path and
            // the boot-time backfill share one implementation.
            if (doc.RootElement.TryGetProperty("chapters", out var chapters))
            {
                result.Chapters = new List<(double StartTime, string Title)>();

                foreach (var chapter in chapters.EnumerateArray())
                {
                    double startTime = 0;
                    string title = "";

                    // Get start time (in seconds)
                    if (chapter.TryGetProperty("start_time", out var startTimeEl))
                    {
                        double.TryParse(startTimeEl.GetString(), out startTime);
                    }

                    // Get title from tags
                    if (chapter.TryGetProperty("tags", out var tags))
                    {
                        if (tags.TryGetProperty("title", out var titleEl))
                        {
                            title = titleEl.GetString() ?? "";
                        }
                    }

                    result.Chapters.Add((startTime, title));
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error probing media file: {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Probe a file to get the subtitle codec for a specific track.
    /// </summary>
    public async Task<string?> ProbeSubtitleCodecAsync(string inputPath, int subtitleTrackIndex)
    {
        try
        {
            var ffprobePath = _binaryLocationService.ResolveFFprobePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v quiet -print_format json -show_streams -select_streams {subtitleTrackIndex} \"{inputPath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var output = await _processRunner.RunProcessAsync(startInfo);
            if (string.IsNullOrEmpty(output)) return null;

            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (stream.TryGetProperty("codec_name", out var codecProp))
                    {
                        return codecProp.GetString();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to probe subtitle codec for track {Index} in {Path}", subtitleTrackIndex, inputPath);
        }
        
        return null;
    }

    public async Task<string?> ProbeSubtitleLanguageAsync(string inputPath, int subtitleTrackIndex)
    {
        try
        {
            var ffprobePath = _binaryLocationService.ResolveFFprobePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v quiet -print_format json -show_streams -select_streams {subtitleTrackIndex} \"{inputPath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var output = await _processRunner.RunProcessAsync(startInfo);
            if (string.IsNullOrEmpty(output)) return null;

            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (stream.TryGetProperty("tags", out var tags) &&
                        tags.TryGetProperty("language", out var lang))
                    {
                        return lang.GetString();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to probe subtitle language for track {Index} in {Path}", subtitleTrackIndex, inputPath);
        }
        
        return null;
    }

    /// <summary>
    /// Infers bit depth from pixel format string (e.g., yuv420p10le = 10-bit).
    /// </summary>
    private static int? InferBitDepthFromPixelFormat(string pixelFormat)
    {
        if (string.IsNullOrEmpty(pixelFormat))
            return null;

        var pf = pixelFormat.ToLowerInvariant();

        // 12-bit formats
        if (pf.Contains("12le") || pf.Contains("12be") || pf.Contains("p12"))
            return 12;

        // 10-bit formats
        if (pf.Contains("10le") || pf.Contains("10be") || pf.Contains("p10"))
            return 10;

        // Default to 8-bit for common formats
        if (pf.Contains("yuv") || pf.Contains("rgb") || pf.Contains("nv12"))
            return 8;

        return null;
    }

    /// <summary>
    /// Detects HDR format from color transfer and side_data properties.
    /// </summary>
    private static string? DetectHdrFormat(JsonElement stream, string? colorTransfer)
    {
        // Check for Dolby Vision via side_data
        if (stream.TryGetProperty("side_data_list", out var sideData))
        {
            foreach (var data in sideData.EnumerateArray())
            {
                if (data.TryGetProperty("side_data_type", out var sideType))
                {
                    var typeStr = sideType.GetString()?.ToLowerInvariant() ?? "";
                    if (typeStr.Contains("dolby vision") || typeStr.Contains("dovi"))
                        return "Dolby Vision";
                    if (typeStr.Contains("hdr10+") || typeStr.Contains("hdr10 plus"))
                        return "HDR10+";
                }
            }
        }

        // Check color_transfer for HDR indicators
        if (!string.IsNullOrEmpty(colorTransfer))
        {
            var ct = colorTransfer.ToLowerInvariant();

            // SMPTE ST 2084 (PQ) = HDR10 or Dolby Vision (if not already detected)
            if (ct.Contains("smpte2084") || ct.Contains("smpte-st-2084") || ct == "pq")
                return "HDR10";

            // HLG (Hybrid Log-Gamma)
            if (ct.Contains("arib-std-b67") || ct.Contains("hlg"))
                return "HLG";
        }

        // Check for HDR metadata in color_primaries
        if (stream.TryGetProperty("color_primaries", out var primaries))
        {
            var cp = primaries.GetString()?.ToLowerInvariant() ?? "";
            // BT.2020 with wide color gamut often indicates HDR content
            // But only return HDR10 if combined with PQ transfer (already checked above)
        }

        return null; // SDR
    }
}

public class MediaProbeResult
{
    public double Duration { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public string? Resolution { get; set; }
    public string? PixelFormat { get; set; }
    public string? ColorTransfer { get; set; }

    /// <summary>ffprobe <c>field_order</c>: "progressive", or "tt"/"bb"/"tb"/"bt" for interlaced.</summary>
    public string? FieldOrder { get; set; }

    /// <summary>
    /// True when the video stream is interlaced (DVD-era rips, broadcast captures). Browsers do
    /// not deinterlace, so the transcode pipeline must — see TranscodeProfileBuilder.
    /// </summary>
    public bool IsInterlaced => FieldOrder is "tt" or "bb" or "tb" or "bt";

    public double FrameRate { get; set; }
    public List<(double StartTime, string Title)>? Chapters { get; set; }  // All chapters

    // New Phase 1 fields (raw values for database storage)
    public int? BitDepth { get; set; }  // 8, 10, 12 bit
    public string? HdrFormat { get; set; }  // "HDR10", "HDR10+", "Dolby Vision", "HLG", null for SDR
    public int? AudioChannels { get; set; }  // Primary audio channel count (e.g., 2, 6, 8)
    public long? Bitrate { get; set; }  // Overall bitrate in bits/second
    public int? Width { get; set; }  // Video width in pixels
    public int? Height { get; set; }  // Video height in pixels

    // Full track lists for detailed display
    public List<AudioTrackInfo>? AudioTracks { get; set; }
    public List<SubtitleTrackInfo>? SubtitleTracks { get; set; }
}

/// <summary>
/// Represents a single audio track in a media file.
/// </summary>
public class AudioTrackInfo
{
    /// <summary>Audio-RELATIVE index (0,1,2… among audio streams only).</summary>
    public int Index { get; set; }

    /// <summary>
    /// ABSOLUTE ffprobe stream index (the "index" field), i.e. what `-map 0:N` and the client's
    /// `?audio=N` refer to — the tracks endpoint hands the client this number. Kept alongside the
    /// audio-relative <see cref="Index"/> because the two disagree on any file with a video stream,
    /// and matching the wrong one silently resolves the wrong track's channel layout.
    /// </summary>
    public int StreamIndex { get; set; }
    public string? Codec { get; set; }
    public string? Language { get; set; }
    public int Channels { get; set; }
    public string? ChannelLayout { get; set; }  // "stereo", "5.1", "7.1", etc.
    public string? Title { get; set; }
    public bool IsDefault { get; set; }
}

/// <summary>
/// Represents a single subtitle track in a media file.
/// </summary>
public class SubtitleTrackInfo
{
    public int Index { get; set; }
    public string? Codec { get; set; }  // "srt", "ass", "pgs", etc.
    public string? Language { get; set; }
    public string? Title { get; set; }
    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }
}
