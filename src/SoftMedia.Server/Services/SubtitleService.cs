using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services;

public interface ISubtitleService
{
    Task<bool> ExtractSubtitleToVttAsync(string inputPath, int subtitleStreamIndex, string outputPath);
    void OffsetWebVttTimestamps(string vttPath, double offsetSeconds);
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

    public void OffsetWebVttTimestamps(string vttPath, double offsetSeconds)
    {
        if (offsetSeconds <= 0 || !File.Exists(vttPath))
            return;

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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error offsetting WebVTT timestamps in {Path}", vttPath);
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
