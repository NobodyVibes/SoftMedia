using Microsoft.Extensions.Logging;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using System.Text.Json;

namespace SoftMedia.Server.Services.Media.Strategies;

/// <summary>
/// Analysis strategy for video media types (Movies, Episodes).
/// Uses FFprobe via IMediaProbeService to extract technical metadata.
/// </summary>
public class VideoAnalysisStrategy : IMediaAnalysisStrategy
{
    private readonly IMediaProbeService _mediaProbeService;
    private readonly ILogger<VideoAnalysisStrategy> _logger;

    // Media types handled by this strategy
    private static readonly MediaType[] SupportedTypes = 
    {
        MediaType.Movie,
        MediaType.Episode
    };

    public VideoAnalysisStrategy(
        IMediaProbeService mediaProbeService,
        ILogger<VideoAnalysisStrategy> logger)
    {
        _mediaProbeService = mediaProbeService;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanHandle(MediaType mediaType) => SupportedTypes.Contains(mediaType);

    /// <inheritdoc />
    public async Task<bool> AnalyzeAsync(
        MediaItem item,
        string filePath,
        MetadataRefreshMode mode,
        CancellationToken cancellationToken = default)
    {
        // Smart Probe: Determine if we should actually probe the file
        if (!ShouldAnalyze(item, filePath, mode))
        {
            _logger.LogDebug("[VideoAnalysisStrategy] Skipping analysis for {Title} - up to date", item.Title);
            return false;
        }

        _logger.LogDebug("[VideoAnalysisStrategy] Analyzing {Title} at {Path}", item.Title, filePath);

        var probe = await _mediaProbeService.ProbeMediaAsync(filePath);
        if (probe == null)
        {
            _logger.LogWarning("[VideoAnalysisStrategy] FFprobe returned null for {Path}", filePath);
            return false;
        }

        // Map probe results to MediaItem
        MapProbeToMediaItem(item, probe, filePath);

        _logger.LogDebug("[VideoAnalysisStrategy] Successfully analyzed {Title}: {Resolution}, {VideoCodec}",
            item.Title, item.Resolution, item.VideoCodec);

        return true;
    }

    /// <summary>
    /// Determines if the file should be (re)analyzed based on refresh mode and current state.
    /// </summary>
    private bool ShouldAnalyze(MediaItem item, string filePath, MetadataRefreshMode mode)
    {
        // Full mode always re-analyzes
        if (mode == MetadataRefreshMode.Full)
            return true;

        // Missing mode: only analyze if needed
        if (mode == MetadataRefreshMode.Missing)
        {
            // New item (no existing data)
            if (string.IsNullOrEmpty(item.VideoCodec))
                return true;

            // File has been modified since last analysis
            try
            {
                var fileTime = File.GetLastWriteTimeUtc(filePath);
                if (item.DateModified < fileTime)
                    return true;
            }
            catch
            {
                // If we can't read file time, assume we need to analyze
                return true;
            }

            // Migration: Check for missing Phase 2 fields
            // This ensures existing items get new metadata on next scan
            if (item.BitDepth == null || item.FrameRate == null || item.Width == null)
                return true;

            return false;
        }

        return false;
    }

    /// <summary>
    /// Maps the probe result to the MediaItem properties.
    /// </summary>
    private void MapProbeToMediaItem(MediaItem item, MediaProbeResult probe, string filePath)
    {
        // Core technical metadata
        item.Duration = probe.Duration;
        item.VideoCodec = probe.VideoCodec;
        item.AudioCodec = probe.AudioCodec;
        item.Resolution = probe.Resolution;
        item.Container = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();

        // Phase 2: Extended technical metadata
        item.BitDepth = probe.BitDepth;
        item.HdrFormat = probe.HdrFormat;
        item.AudioChannels = probe.AudioChannels;
        item.Bitrate = probe.Bitrate;
        item.FrameRate = probe.FrameRate;
        item.Width = probe.Width;
        item.Height = probe.Height;

        // Serialize audio and subtitle track lists as JSON
        if (probe.AudioTracks != null && probe.AudioTracks.Count > 0)
        {
            item.AudioTracks = probe.AudioTracks.Select(at => new AudioTrack
            {
                Index = at.Index,
                Codec = at.Codec,
                Language = at.Language,
                Channels = at.Channels,
                ChannelLayout = at.ChannelLayout,
                Title = at.Title,
                IsDefault = at.IsDefault
            }).ToList();
        }
        if (probe.SubtitleTracks != null && probe.SubtitleTracks.Count > 0)
        {
            item.SubtitleTracks = probe.SubtitleTracks.Select(st => new SubtitleTrack
            {
                Index = st.Index,
                Codec = st.Codec,
                Language = st.Language,
                Title = st.Title,
                IsDefault = st.IsDefault,
                IsForced = st.IsForced
            }).ToList();
        }

        // Write credits start to promoted column
        if (probe.CreditsStart.HasValue)
        {
            item.CreditsStart = probe.CreditsStart.Value;
        }

        // Write chapters to relational table
        if (probe.Chapters != null && probe.Chapters.Count > 0)
        {
            item.Chapters.Clear();
            foreach (var ch in probe.Chapters)
            {
                item.Chapters.Add(new Chapter
                {
                    StartTime = ch.StartTime,
                    Title = ch.Title ?? string.Empty
                });
            }
        }
    }
}
