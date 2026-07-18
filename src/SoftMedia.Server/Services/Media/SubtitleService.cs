using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Media;

public interface ISubtitleService
{
    Task<bool> ExtractSubtitleToVttAsync(string inputPath, int subtitleStreamIndex, string outputPath);

    /// <summary>
    /// R-WI-012 — extract a text subtitle track to an .ass file for libass burn-in.
    /// ASS (not WebVTT) so styling survives; SRT converts losslessly. The output goes to a
    /// FIXED-NAME file inside the transcode session directory so the ffmpeg subtitles= filter
    /// can reference it as a bare relative filename — user media paths (apostrophes, brackets,
    /// colons) never enter a filter string again. Success requires ffmpeg exit code 0 AND a
    /// non-empty output; partial output from a failed/killed run is deleted, never burned.
    /// </summary>
    Task<bool> ExtractSubtitleToAssAsync(string inputPath, int subtitleStreamIndex, string outputPath);

    /// <summary>
    /// R-WI-012 review — dump the input's embedded FONT attachments (typeset ASS subs depend on
    /// them) into a directory, so the burn-in filter can point libass at it via :fontsdir=.
    /// Attachments are written under SANITIZED names (never the file-supplied filename metadata,
    /// which could path-traverse). Best-effort: returns the number of fonts dumped, 0 on any
    /// failure — burn-in proceeds with fallback fonts rather than failing.
    /// </summary>
    Task<int> DumpFontAttachmentsAsync(string inputPath, string outputDir);

    /// <summary>Shift cue times for a seek-restarted stream. False = the file must not be served.</summary>
    bool OffsetWebVttTimestamps(string vttPath, double offsetSeconds);
    Task<int> GetSubtitleStreamIndexAsync(string inputPath, int absoluteStreamIndex);
}

public class SubtitleService : ISubtitleService
{
    private readonly ILogger<SubtitleService> _logger;
    private readonly IProcessRunner _processRunner;
    private readonly IBinaryLocationService _binaryLocationService;

    public SubtitleService(
        ILogger<SubtitleService> logger,
        IProcessRunner processRunner,
        IBinaryLocationService binaryLocationService)
    {
        _logger = logger;
        _processRunner = processRunner;
        _binaryLocationService = binaryLocationService;
    }

    public async Task<bool> ExtractSubtitleToVttAsync(string inputPath, int subtitleStreamIndex, string outputPath)
    {
        try
        {
            var ffmpegPath = _binaryLocationService.ResolveFFmpegPath();
            
            // FFmpeg command to extract subtitle track and convert to WebVTT
            // -i input: input file
            // -map 0:s:{index}: select specific subtitle stream
            // -c:s webvtt: convert to WebVTT format
            // -y: overwrite output file
            var arguments = $"-i \"{inputPath}\" -map 0:s:{subtitleStreamIndex} -c:s webvtt -y \"{outputPath}\"";
            
            _logger.LogInformation("Extracting subtitle track {Index} to WebVTT: {Path}", subtitleStreamIndex, outputPath);
            
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var output = await _processRunner.RunProcessAsync(startInfo);
            
            // Note: ProcessRunner returns output but doesn't easily expose ExitCode if we just use the interface.
            // For simple extraction checks, file existence is key.
            // But verify: ProcessRunner implementation captures stdout. FFmpeg logs to stderr.
            // We might need to check if we trust existing ProcessRunner for this.
            // The original code used Process directly to check ExitCode. 
            // My Interface definition: Task<string> RunProcessAsync(ProcessStartInfo startInfo);
            // It swallows ExitCode. 
            // However, we verify file existence.
            
            if (!File.Exists(outputPath))
            {
                _logger.LogWarning("Subtitle extraction did not create output file: {Path}", outputPath);
                return false;
            }

            var fileInfo = new FileInfo(outputPath);
            // Basic check if file is empty
            if (fileInfo.Length == 0)
            {
                 _logger.LogWarning("Subtitle extraction created empty file: {Path}", outputPath);
                 return false;
            }

            _logger.LogInformation("Subtitle extracted successfully: {Path} ({Size} bytes)", outputPath, fileInfo.Length);
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting subtitle track {Index} from {Path}", subtitleStreamIndex, inputPath);
            return false;
        }
    }

    // Backstop against a truly hung source (dead NAS), NOT a working bound: extraction must
    // demux the whole container, which on big remuxes takes minutes — the old inline
    // subtitles= filter paid the same scan inside ffmpeg with no bound at all. A 30s-style
    // cap here silently truncated or dropped burn-in on large files (R-WI-012 review, HIGH).
    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FontDumpTimeout = TimeSpan.FromSeconds(60);

    public async Task<bool> ExtractSubtitleToAssAsync(string inputPath, int subtitleStreamIndex, string outputPath)
    {
        try
        {
            var ffmpegPath = _binaryLocationService.ResolveFFmpegPath();

            // -map 0:s:{index}: the chosen subtitle stream (subtitle-relative index)
            // -c:s ass: convert to ASS so libass burn-in keeps styling (WebVTT would lose it)
            // The INPUT path is quoted-argument interpolation (the established, MediaPathSafety-
            // guarded pattern) — never filter-string interpolation.
            var arguments = $"-i \"{inputPath}\" -map 0:s:{subtitleStreamIndex} -c:s ass -y \"{outputPath}\"";

            _logger.LogInformation("Extracting subtitle track {Index} to ASS for burn-in: {Path}", subtitleStreamIndex, outputPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
            };

            var exitCode = await _processRunner.RunProcessForExitCodeAsync(startInfo, ExtractionTimeout);

            // Exit code 0 AND a non-empty file — a timeout-killed or failed ffmpeg can leave a
            // PARTIAL .ass that looks plausible; burning it would make subtitles silently vanish
            // mid-movie. Never trust the file alone, and never leave the partial behind.
            if (exitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                _logger.LogWarning(
                    "Burn-in subtitle extraction failed (exit {Code}) for {Input}; deleting any partial output.",
                    exitCode, inputPath);
                TryDelete(outputPath);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting subtitle track {Index} from {Path} for burn-in", subtitleStreamIndex, inputPath);
            TryDelete(outputPath);
            return false;
        }
    }

    public async Task<int> DumpFontAttachmentsAsync(string inputPath, string outputDir)
    {
        try
        {
            // Probe attachment streams for font mimetypes. We deliberately do NOT dump by the
            // attachment's own filename metadata (-dump_attachment:t "") — a crafted file could
            // carry a path-traversing name. Instead each font is dumped to OUR sanitized name.
            var ffprobePath = _binaryLocationService.ResolveFFprobePath();
            var probeInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v quiet -print_format json -show_streams \"{inputPath}\"",
            };
            var json = await _processRunner.RunProcessAsync(probeInfo);
            if (string.IsNullOrWhiteSpace(json)) return 0;

            using var doc = JsonDocument.Parse(json);
            var fonts = new List<(int AbsoluteIndex, string Extension)>();
            foreach (var stream in doc.RootElement.GetProperty("streams").EnumerateArray())
            {
                if (stream.GetProperty("codec_type").GetString() != "attachment") continue;
                var mime = stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("mimetype", out var m)
                    ? m.GetString() ?? "" : "";
                var isFont = mime.Contains("font", StringComparison.OrdinalIgnoreCase)
                    || mime.Contains("truetype", StringComparison.OrdinalIgnoreCase)
                    || mime.Contains("opentype", StringComparison.OrdinalIgnoreCase);
                if (!isFont) continue;
                var ext = mime.Contains("opentype", StringComparison.OrdinalIgnoreCase) ? ".otf" : ".ttf";
                fonts.Add((stream.GetProperty("index").GetInt32(), ext));
            }
            if (fonts.Count == 0) return 0;

            var ffmpegPath = _binaryLocationService.ResolveFFmpegPath();
            var dumped = 0;
            for (var i = 0; i < fonts.Count; i++)
            {
                var target = Path.Combine(outputDir, $"font{i}{fonts[i].Extension}");
                // -dump_attachment writes during input open; ffmpeg then exits non-zero because
                // no output was mapped — that's expected, so judge by the dumped file instead.
                var dumpInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-y -dump_attachment:{fonts[i].AbsoluteIndex} \"{target}\" -i \"{inputPath}\"",
                };
                await _processRunner.RunProcessForExitCodeAsync(dumpInfo, FontDumpTimeout);
                if (File.Exists(target) && new FileInfo(target).Length > 0) dumped++;
            }

            if (dumped > 0)
            {
                _logger.LogInformation("Dumped {Count} embedded font(s) for subtitle burn-in into {Dir}", dumped, outputDir);
            }
            return dumped;
        }
        catch (Exception ex)
        {
            // Fonts are an enhancement — burn-in proceeds with fallback fonts.
            _logger.LogWarning(ex, "Font attachment dump failed for {Path}; burn-in will use fallback fonts.", inputPath);
            return 0;
        }
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not delete partial file {Path}", path); }
    }

    public bool OffsetWebVttTimestamps(string vttPath, double offsetSeconds)
    {
        // No offset needed counts as success; a missing file does not.
        if (offsetSeconds <= 0)
            return true;
        if (!File.Exists(vttPath))
            return false;

        try
        {
            var lines = File.ReadAllLines(vttPath);
            var offsetTimeSpan = TimeSpan.FromSeconds(offsetSeconds);
            var result = new List<string>();
            var skipCue = false;
            
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                
                if (line.Contains(" --> "))
                {
                    var parts = line.Split(new[] { " --> " }, StringSplitOptions.None);
                    if (parts.Length == 2)
                    {
                        if (TryParseVttTimestamp(parts[0].Trim(), out var startTime) && 
                            TryParseVttTimestamp(parts[1].Trim(), out var endTime))
                        {
                            var newStart = startTime - offsetTimeSpan;
                            var newEnd = endTime - offsetTimeSpan;
                            
                            if (newEnd < TimeSpan.Zero)
                            {
                                skipCue = true;
                                continue;
                            }
                            
                            if (newStart < TimeSpan.Zero)
                                newStart = TimeSpan.Zero;
                            
                            result.Add($"{FormatVttTimestamp(newStart)} --> {FormatVttTimestamp(newEnd)}");
                            skipCue = false;
                            continue;
                        }
                    }
                }
                
                if (skipCue)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        skipCue = false;
                        result.Add(line);
                    }
                    continue;
                }
                
                result.Add(line);
            }
            
            File.WriteAllLines(vttPath, result);
            _logger.LogInformation("Offset WebVTT timestamps by {Offset}s: {Path}", offsetSeconds, vttPath);
            return true;
        }
        catch (Exception ex)
        {
            // R-WI-018 review: the caller must NOT serve this file — absolute-time
            // cues on a seek-restarted stream are off by the whole seek (unrecoverable
            // by the client's ±30s sync control). Failure is reported so the session
            // drops the VTT instead.
            _logger.LogError(ex, "Error offsetting WebVTT timestamps in {Path}", vttPath);
            return false;
        }
    }

    public async Task<int> GetSubtitleStreamIndexAsync(string inputPath, int absoluteStreamIndex)
    {
        try
        {
            var ffprobePath = _binaryLocationService.ResolveFFprobePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v quiet -print_format json -show_streams \"{inputPath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var output = await _processRunner.RunProcessAsync(startInfo);
            if (string.IsNullOrEmpty(output)) return 0;

            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("streams", out var streams))
            {
                int subtitleIndex = 0;
                foreach (var stream in streams.EnumerateArray())
                {
                    var index = stream.GetProperty("index").GetInt32();
                    var codecType = stream.TryGetProperty("codec_type", out var ct) ? ct.GetString() : null;
                    
                    if (codecType == "subtitle")
                    {
                        if (index == absoluteStreamIndex)
                        {
                            return subtitleIndex;
                        }
                        subtitleIndex++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to calculate subtitle stream index, using 0");
        }
        
        return 0;
    }

    private bool TryParseVttTimestamp(string timestamp, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        try
        {
            var parts = timestamp.Split(':');
            if (parts.Length == 3)
            {
                var hours = int.Parse(parts[0]);
                var minutes = int.Parse(parts[1]);
                var secondsParts = parts[2].Split('.');
                var seconds = int.Parse(secondsParts[0]);
                var milliseconds = secondsParts.Length > 1 ? int.Parse(secondsParts[1].PadRight(3, '0').Substring(0, 3)) : 0;
                result = new TimeSpan(0, hours, minutes, seconds, milliseconds);
                return true;
            }
            else if (parts.Length == 2)
            {
                var minutes = int.Parse(parts[0]);
                var secondsParts = parts[1].Split('.');
                var seconds = int.Parse(secondsParts[0]);
                var milliseconds = secondsParts.Length > 1 ? int.Parse(secondsParts[1].PadRight(3, '0').Substring(0, 3)) : 0;
                result = new TimeSpan(0, 0, minutes, seconds, milliseconds);
                return true;
            }
        }
        catch { }
        return false;
    }
    
    private string FormatVttTimestamp(TimeSpan ts)
    {
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }
}
