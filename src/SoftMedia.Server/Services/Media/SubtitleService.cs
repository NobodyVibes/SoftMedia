using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Media;

public interface ISubtitleService
{
    /// <summary>
    /// SR-WI-022 — extract a text subtitle track to a sidecar WebVTT file. Success requires
    /// ffmpeg exit code 0 AND a non-empty output (parity with the burn-in extractor): a
    /// timeout-killed run leaves a PARTIAL .vtt that makes subtitles silently vanish mid-movie,
    /// so partials are deleted, never served. Extractions are cached persistently under
    /// wwwroot/cache/subtitles keyed by (source path, track, source mtime) — the cache holds the
    /// UNSHIFTED extraction and each caller gets its own COPY at <paramref name="outputPath"/>,
    /// because the transcode session seek-shifts its copy in place via OffsetWebVttTimestamps.
    /// </summary>
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

    /// <summary>
    /// Delete every cached VTT extraction (all tracks, all mtime variants) for the given
    /// source files. The cache is keyed by a hash of the source path — not by media id —
    /// so callers that delete media items must pass the items' file paths BEFORE the DB
    /// rows go away. Returns files deleted.
    /// </summary>
    int DeleteCachedVttForSourcePaths(IEnumerable<string> sourcePaths);

    /// <summary>
    /// Delete cached VTT files whose source-path hash matches none of
    /// <paramref name="validSourcePaths"/>. Row-existence contract: pass the paths of ALL
    /// MediaItems rows including soft-deleted (IsMissing) ones — their extractions are
    /// retained so playback works instantly when the drive returns. Also removes stale
    /// ".tmp.vtt" leftovers from crashed extractions. Returns files deleted.
    /// </summary>
    int CleanupOrphanedVtt(IReadOnlyCollection<string> validSourcePaths);
}

public class SubtitleService : ISubtitleService
{
    private readonly ILogger<SubtitleService> _logger;
    private readonly IProcessRunner _processRunner;
    private readonly IBinaryLocationService _binaryLocationService;
    private readonly string _vttCacheRoot;

    // SR-WI-022: per-(source, track) gate so two sessions far-seeking the same title don't
    // both demux a 40GB remux — the second waits, then hits the cache. Static because the
    // service is scoped (one instance per request scope) but the cache is process-wide.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> VttExtractionGates = new();

    public SubtitleService(
        ILogger<SubtitleService> logger,
        IProcessRunner processRunner,
        IBinaryLocationService binaryLocationService,
        IWebHostEnvironment env)
    {
        _logger = logger;
        _processRunner = processRunner;
        _binaryLocationService = binaryLocationService;

        // Same cache-root convention as TrickplayService/ImageCacheService: the repo has no
        // data/ dir — persistent caches live under wwwroot/cache/<area>.
        var webRoot = !string.IsNullOrEmpty(env.WebRootPath)
            ? env.WebRootPath
            : Path.Combine(Environment.CurrentDirectory, "wwwroot");
        _vttCacheRoot = Path.Combine(webRoot, "cache", "subtitles");
    }

    public async Task<bool> ExtractSubtitleToVttAsync(string inputPath, int subtitleStreamIndex, string outputPath)
    {
        // Cache key: (source identity, track, source mtime). The extraction method has no
        // mediaId in its contract, so source identity is a hash of the full input path —
        // stable across sessions for the same library file. An mtime change (re-mux, upgrade)
        // produces a new variant and evicts the stale one.
        var sourceKey = BuildVttCacheKey(inputPath, subtitleStreamIndex);
        var mtimeTicks = GetLastWriteTicks(inputPath);
        var gate = VttExtractionGates.GetOrAdd(sourceKey, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_vttCacheRoot);
            var cachePath = Path.Combine(_vttCacheRoot, $"{sourceKey}_{mtimeTicks}.vtt");

            // Cache hit: hand the caller its OWN copy. The cached file stays UNSHIFTED —
            // TranscodeService seek-shifts the session copy in place (OffsetWebVttTimestamps),
            // so the cache must never be mutated or shared by reference.
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
            {
                File.Copy(cachePath, outputPath, overwrite: true);
                _logger.LogInformation("Subtitle VTT cache hit for track {Index} of {Input}: {Cache}",
                    subtitleStreamIndex, inputPath, cachePath);
                return true;
            }

            // Extract into a temp file inside the cache dir (same volume → atomic move), then
            // promote on verified success. The caller's outputPath is never written partially.
            var tempPath = Path.Combine(_vttCacheRoot, $"{sourceKey}_{mtimeTicks}.{Guid.NewGuid():N}.tmp.vtt");
            try
            {
                var ffmpegPath = _binaryLocationService.ResolveFFmpegPath();

                // -map 0:s:{index}: the chosen subtitle stream (subtitle-relative index)
                // -c:s webvtt: convert to WebVTT for sidecar delivery
                var arguments = $"-i \"{inputPath}\" -map 0:s:{subtitleStreamIndex} -c:s webvtt -y \"{tempPath}\"";

                _logger.LogInformation("Extracting subtitle track {Index} to WebVTT: {Path}", subtitleStreamIndex, outputPath);

                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = arguments,
                };

                // SR-WI-022 — parity with the burn-in extractor (R-WI-012): exit-code-strict with
                // the 10-minute hung-source backstop, NOT the 30s RunProcessAsync kill that
                // truncated large-remux extractions and made subtitles vanish mid-movie.
                var exitCode = await _processRunner.RunProcessForExitCodeAsync(startInfo, ExtractionTimeout);

                // Exit code 0 AND a non-empty file — a timeout-killed or failed ffmpeg leaves a
                // PARTIAL .vtt that looks plausible. Never trust the file alone, never keep the partial.
                if (exitCode != 0 || !File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
                {
                    _logger.LogWarning(
                        "Subtitle extraction failed (exit {Code}) for track {Index} of {Input}; deleting any partial output.",
                        exitCode, subtitleStreamIndex, inputPath);
                    TryDelete(tempPath);
                    return false;
                }

                // Verified success: evict stale mtime variants for this (source, track), promote
                // the fresh extraction into the cache, then copy to the caller's target.
                DeleteStaleVttVariants(sourceKey, cachePath, tempPath);
                File.Move(tempPath, cachePath, overwrite: true);
                File.Copy(cachePath, outputPath, overwrite: true);

                _logger.LogInformation("Subtitle extracted successfully: {Path} ({Size} bytes)",
                    outputPath, new FileInfo(outputPath).Length);
                return true;
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting subtitle track {Index} from {Path}", subtitleStreamIndex, inputPath);
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string BuildVttCacheKey(string inputPath, int subtitleStreamIndex)
        => $"{HashSourcePath(inputPath)}_s{subtitleStreamIndex}";

    /// <summary>16-hex-char source identity — the prefix of every cache filename for a path.</summary>
    private static string HashSourcePath(string inputPath)
    {
        string canonical;
        try { canonical = Path.GetFullPath(inputPath); }
        catch { canonical = inputPath; }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    public int DeleteCachedVttForSourcePaths(IEnumerable<string> sourcePaths)
    {
        var deleted = 0;
        try
        {
            if (!Directory.Exists(_vttCacheRoot)) return 0;
            foreach (var sourcePath in sourcePaths)
            {
                if (string.IsNullOrEmpty(sourcePath)) continue;
                // "{hash}_*" catches every track index, mtime variant, and tmp leftover.
                foreach (var file in Directory.GetFiles(_vttCacheRoot, $"{HashSourcePath(sourcePath)}_*"))
                {
                    TryDelete(file);
                    deleted++;
                }
            }
            if (deleted > 0)
            {
                _logger.LogInformation("Deleted {Count} cached subtitle extraction(s) for removed media", deleted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete cached subtitle extractions");
        }
        return deleted;
    }

    public int CleanupOrphanedVtt(IReadOnlyCollection<string> validSourcePaths)
    {
        var deleted = 0;
        try
        {
            if (!Directory.Exists(_vttCacheRoot)) return 0;

            var validPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in validSourcePaths)
            {
                if (!string.IsNullOrEmpty(path)) validPrefixes.Add(HashSourcePath(path));
            }

            foreach (var file in Directory.GetFiles(_vttCacheRoot))
            {
                try
                {
                    var name = Path.GetFileName(file);
                    var underscore = name.IndexOf('_');
                    var prefix = underscore > 0 ? name[..underscore] : name;

                    if (validPrefixes.Contains(prefix))
                    {
                        // Live source — only reap crashed-extraction temp files. A LIVE
                        // extraction holds its per-key gate for minutes at most, so the
                        // day-old guard cannot race it.
                        if (name.EndsWith(".tmp.vtt", StringComparison.OrdinalIgnoreCase)
                            && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1))
                        {
                            File.Delete(file);
                            deleted++;
                        }
                        continue;
                    }

                    File.Delete(file);
                    deleted++;
                    _logger.LogDebug("Deleted orphaned subtitle cache file: {Path}", file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process subtitle cache file during cleanup: {Path}", file);
                }
            }

            if (deleted > 0)
            {
                _logger.LogInformation("Subtitle cache cleanup removed {Count} orphaned file(s)", deleted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Subtitle cache orphan cleanup failed");
        }
        return deleted;
    }

    private static long GetLastWriteTicks(string inputPath)
    {
        // Missing/unreadable sources return the 1601 sentinel rather than throwing; the
        // extraction itself will fail cleanly, so the key just needs to be deterministic.
        try { return File.GetLastWriteTimeUtc(inputPath).Ticks; }
        catch { return 0; }
    }

    private void DeleteStaleVttVariants(string sourceKey, string cachePath, string tempPath)
    {
        try
        {
            foreach (var candidate in Directory.EnumerateFiles(_vttCacheRoot, $"{sourceKey}_*.vtt"))
            {
                if (string.Equals(candidate, cachePath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(candidate, tempPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                TryDelete(candidate);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clean stale subtitle cache variants for {Key}", sourceKey);
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
            // B-17: a cue's identifier line precedes its timestamp line, and the
            // keep/drop decision only lands AT the timestamp — buffer the pending
            // identifier so dropped cues don't leave orphan identifiers behind.
            string? pendingIdentifier = null;

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
                                pendingIdentifier = null; // the whole cue is dropped
                                skipCue = true;
                                continue;
                            }

                            if (newStart < TimeSpan.Zero)
                                newStart = TimeSpan.Zero;

                            if (pendingIdentifier != null)
                            {
                                result.Add(pendingIdentifier);
                                pendingIdentifier = null;
                            }
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

                // A non-blank, non-timestamp line directly before a timestamp is a cue
                // identifier — hold it until the cue's fate is known. Header/comment
                // lines never precede a timestamp line, so they flush immediately.
                var isIdentifierCandidate = !string.IsNullOrWhiteSpace(line)
                    && i + 1 < lines.Length && lines[i + 1].Contains(" --> ");
                if (isIdentifierCandidate)
                {
                    if (pendingIdentifier != null) result.Add(pendingIdentifier);
                    pendingIdentifier = line;
                    continue;
                }

                if (pendingIdentifier != null)
                {
                    result.Add(pendingIdentifier);
                    pendingIdentifier = null;
                }
                result.Add(line);
            }
            if (pendingIdentifier != null) result.Add(pendingIdentifier);
            
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
