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

        // SM-WI-043: stamp the ATTEMPT (success or fail) — the Missing-mode backfill
        // gate below keys off this, so a file ffprobe can't fill stops re-probing on
        // every scan of a stable library.
        item.LastProbeAttemptUtc = DateTime.UtcNow;

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

            // SM-WI-043: the old File.GetLastWriteTimeUtc comparison here was dead on
            // the scan path — scanners stamp DateModified from the discovery result
            // BEFORE calling AnalyzeAsync and route genuinely changed files through
            // Full mode, so the check was one wasted filesystem stat per unchanged
            // file per scan (brutal over SMB). Change detection is the scanner's job.

            // Migration backfill for Phase 2 fields — but only ONCE per file version
            // (SM-WI-043): some files legitimately probe without BitDepth/FrameRate/
            // Width, and re-probing them every scan, forever, was a few hundred ffmpeg
            // spawns per scan of a stable library. LastProbeAttemptUtc is stamped on
            // every attempt; a replaced file re-enters via Full mode.
            if ((item.BitDepth == null || item.FrameRate == null || item.Width == null)
                && item.LastProbeAttemptUtc == null)
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

        // CM-WI-002: map chapter markers onto intro/credits timecodes. Invariant:
        // Chapter-sourced columns MIRROR the file's current chapters (written on match,
        // cleared when a previously chapter-sourced value no longer has a matching
        // chapter — e.g. the file was replaced with a different cut). Detected-sourced
        // columns belong to the fingerprint pipeline and are never touched here; the
        // TryWrite* guards in IntroCreditsDetectionService enforce the reverse
        // precedence (detection never overwrites Chapter). Chapter beats Detected:
        // the file's own authoring is ground truth.
        var markers = Detection.ChapterMarkerMapper.Map(
            probe.Chapters ?? new List<(double StartTime, string Title)>(), probe.Duration);

        if (markers.Intro is { } intro)
        {
            item.IntroStart = intro.Start;
            item.IntroEnd = intro.End;
            item.IntroSource = DetectionSource.Chapter;
        }
        else if (item.IntroSource == DetectionSource.Chapter)
        {
            item.IntroStart = null;
            item.IntroEnd = null;
            item.IntroSource = null;
        }

        if (markers.Credits is { } credits)
        {
            item.CreditsStart = credits.Start;
            item.CreditsEnd = credits.End;
            item.CreditsSource = DetectionSource.Chapter;
        }
        else if (item.CreditsSource == DetectionSource.Chapter)
        {
            item.CreditsStart = null;
            item.CreditsEnd = null;
            item.CreditsSource = null;
        }
    }
}
