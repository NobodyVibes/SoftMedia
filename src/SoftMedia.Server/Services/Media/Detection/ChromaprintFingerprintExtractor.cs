using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Services.Infrastructure;

namespace SoftMedia.Server.Services.Media.Detection;

/// <summary>
/// Shells FFmpeg's chromaprint muxer to produce raw fingerprint hashes for a single
/// audio window of a media file. Uses Process directly (not IProcessRunner) because
/// the chromaprint muxer writes binary uint32 big-endian output to stdout, which the
/// existing string-returning IProcessRunner cannot capture without corruption.
/// </summary>
public class ChromaprintFingerprintExtractor : IFingerprintExtractor
{
    // FFmpeg's chromaprint muxer feeds audio at 11025 Hz mono into Chromaprint's
    // default algorithm (TEST2). That algorithm has frame_size=4096 and
    // frame_overlap=4096-1365=2731, so the hop between hashes is 1365 samples.
    // At 11025 Hz that's 11025/1365 ≈ 8.08 hashes per second — verified against
    // both the Chromaprint source (fingerprinter_configuration.h) and the
    // Jellyfin intro-skipper plugin's hard-coded value of 8 hashes/sec. An
    // earlier guess of 11025/1024 ≈ 10.77 was wrong by ~33% and corrupted every
    // computed timestamp; do NOT revert without confirming the upstream constant.
    public double HashesPerSecond => 11025.0 / 1365.0;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly IBinaryLocationService _binaryLocationService;
    private readonly ILogger<ChromaprintFingerprintExtractor> _logger;

    public ChromaprintFingerprintExtractor(
        IBinaryLocationService binaryLocationService,
        ILogger<ChromaprintFingerprintExtractor> logger)
    {
        _binaryLocationService = binaryLocationService;
        _logger = logger;
    }

    public Task<uint[]?> ExtractHeadAsync(string filePath, double durationSeconds, CancellationToken cancellationToken = default)
    {
        // Head: seek to 0, read durationSeconds.
        var args = $"-hide_banner -loglevel error -ss 0 -t {FormatSeconds(durationSeconds)} " +
                   $"-i \"{filePath}\" -ac 1 -ar 11025 -vn -f chromaprint -fp_format raw -";
        return ExtractAsync(filePath, args, cancellationToken);
    }

    public Task<uint[]?> ExtractTailAsync(string filePath, double durationSeconds, CancellationToken cancellationToken = default)
    {
        // Tail: -sseof seeks relative to end-of-file. Negative values mean "this many seconds before EOF".
        var args = $"-hide_banner -loglevel error -sseof -{FormatSeconds(durationSeconds)} " +
                   $"-i \"{filePath}\" -ac 1 -ar 11025 -vn -f chromaprint -fp_format raw -";
        return ExtractAsync(filePath, args, cancellationToken);
    }

    private async Task<uint[]?> ExtractAsync(string filePath, string arguments, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("[Fingerprint] File not found: {Path}", filePath);
            return null;
        }

        string ffmpegPath;
        try
        {
            ffmpegPath = _binaryLocationService.ResolveFFmpegPath();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Fingerprint] FFmpeg binary not found");
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(DefaultTimeout);

        try
        {
            if (!process.Start())
            {
                _logger.LogError("[Fingerprint] Failed to start FFmpeg for {Path}", filePath);
                return null;
            }

            // Drain both pipes WITHOUT a cancellation token: their lifetime is tied to the
            // process. Cancelling the drains directly stopped the reads while ffmpeg kept
            // writing — it then blocked forever on the full stdout pipe and never exited,
            // and since the kill lived in a catch we could only reach after the process
            // exited, the whole scan queue wedged on every preemption/timeout.
            using var stdoutBuffer = new MemoryStream();
            var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdoutBuffer);
            var stderrTask = process.StandardError.ReadToEndAsync();

            var interrupted = false;
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                interrupted = true;
                // Kill FIRST so the pipes close and the drain tasks below can complete.
                TryKill(process);
            }

            // Completes promptly: the pipes close when the process exits or dies. The
            // 30s guard is a last resort for an unkillable process — it surfaces as the
            // generic failure path below instead of a permanent hang.
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(30));

            // Genuine cancellation (shutdown, or detection preempted by a scan) must
            // PROPAGATE so the caller stops instead of moving on to the next episode.
            cancellationToken.ThrowIfCancellationRequested();

            if (interrupted)
            {
                _logger.LogWarning("[Fingerprint] FFmpeg timed out after {Timeout} for {Path}", DefaultTimeout, filePath);
                return null;
            }

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("[Fingerprint] FFmpeg exited {Code} for {Path}: {Err}",
                    process.ExitCode, filePath, stderrTask.Result);
                return null;
            }

            return BytesToHashes(stdoutBuffer.ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Fingerprint] FFmpeg failed for {Path}", filePath);
            TryKill(process);
            return null;
        }
    }

    private static uint[] BytesToHashes(byte[] bytes)
    {
        // The chromaprint muxer emits raw fingerprint as big-endian uint32 values, no header.
        // A trailing partial 4-byte chunk indicates a truncated stream — drop it rather than
        // synthesizing a hash from incomplete data.
        var count = bytes.Length / 4;
        var hashes = new uint[count];
        for (int i = 0; i < count; i++)
        {
            hashes[i] = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i * 4, 4));
        }
        return hashes;
    }

    private static string FormatSeconds(double seconds)
        => seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* process may have already exited */ }
    }
}
