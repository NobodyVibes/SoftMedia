using System.Diagnostics;
using System.Text.Json;
using SoftMedia.Server.Services.Infrastructure;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// Outcome of one <see cref="ITrickplayService.GenerateAsync"/> call: success flag plus
/// the ffmpeg CPU/wall cost actually spent (BG-WI-004 — both attempts summed when the
/// keyframe-only pass fell back to a full decode). Zero cost when generation was skipped.
/// </summary>
public sealed record TrickplayGenerationResult(bool Success, double CpuSeconds, double WallSeconds);

/// <summary>
/// Generates and serves pre-baked trickplay sprite sheets (P2-WI-001): a tiled JPEG
/// of evenly-spaced thumbnails plus a JSON manifest, so the scrubber shows instant
/// previews from one cached image instead of spawning an FFmpeg process per scrub.
///
/// Cache layout (NOTE: the repo has no data/ dir — caches live under wwwroot/cache,
/// matching ThumbnailService): wwwroot/cache/trickplay/{itemId}/manifest.json + sheet-N.jpg.
/// </summary>
public interface ITrickplayService
{
    /// True if a manifest already exists for the item.
    bool HasTrickplay(Guid itemId);

    /// Generates the sprite sheet(s) + manifest for the given source video. Failures are
    /// logged and reported via <see cref="TrickplayGenerationResult.Success"/>. Safe to
    /// call repeatedly — skips if already present.
    Task<TrickplayGenerationResult> GenerateAsync(Guid itemId, string sourcePath, CancellationToken ct);

    /// Absolute path to the item's manifest.json, or null if absent.
    string? GetManifestPath(Guid itemId);

    /// Absolute path to a named sheet file inside the item's trickplay dir, with a
    /// path-traversal guard. Null if the name escapes the dir or the file is absent.
    string? GetSheetPath(Guid itemId, string sheetFile);

    /// <summary>
    /// Delete the item's trickplay directory (manifest + sheets). True if anything was
    /// removed. Called when the item (or its whole library) is deleted — before this,
    /// trickplay had no deletion path at all and sheets leaked forever.
    /// </summary>
    bool DeleteForItem(Guid itemId);

    /// <summary>
    /// Delete trickplay directories whose item guid matches no entry in
    /// <paramref name="validIds"/>. Row-existence contract: pass the ids of ALL
    /// MediaItems rows including soft-deleted (IsMissing) ones, whose sheets are
    /// retained so they heal when the drive returns. Also removes ".tmp" staging
    /// directories older than a day (crashed generations). Returns directories deleted.
    /// </summary>
    int CleanupOrphans(HashSet<Guid> validIds);
}

public class TrickplayService : ITrickplayService
{
    private const int Columns = 10;
    private const int Rows = 10;
    private const int TilesPerSheet = Columns * Rows;

    /// SR-WI-028: ceiling on a single FFmpeg generation run — a stuck/unreadable
    /// source must not hang generation forever. Generous because sampling a long
    /// 4K source end-to-end is legitimately slow. Settable so tests can exercise
    /// the timeout path without waiting 30 minutes (project convention; no
    /// InternalsVisibleTo).
    public TimeSpan GenerationTimeout { get; set; } = TimeSpan.FromMinutes(30);

    private readonly IBinaryLocationService _binaryLocation;
    private readonly ISettingsService _settings;
    private readonly ILogger<TrickplayService> _logger;
    private readonly string _root;

    public TrickplayService(
        IWebHostEnvironment env,
        IBinaryLocationService binaryLocation,
        ISettingsService settings,
        ILogger<TrickplayService> logger)
    {
        _binaryLocation = binaryLocation;
        _settings = settings;
        _logger = logger;

        var webRoot = !string.IsNullOrEmpty(env.WebRootPath)
            ? env.WebRootPath
            : Path.Combine(Environment.CurrentDirectory, "wwwroot");
        _root = Path.Combine(webRoot, "cache", "trickplay");
    }

    private string ItemDir(Guid itemId) => Path.Combine(_root, itemId.ToString("N"));

    public bool HasTrickplay(Guid itemId) => File.Exists(Path.Combine(ItemDir(itemId), "manifest.json"));

    public string? GetManifestPath(Guid itemId)
    {
        var p = Path.Combine(ItemDir(itemId), "manifest.json");
        return File.Exists(p) ? p : null;
    }

    public string? GetSheetPath(Guid itemId, string sheetFile)
    {
        // Path-traversal guard: reject anything that isn't a bare filename.
        if (string.IsNullOrWhiteSpace(sheetFile) || sheetFile.Contains('/') || sheetFile.Contains('\\')
            || sheetFile.Contains("..") || sheetFile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;

        var dir = Path.GetFullPath(ItemDir(itemId));
        var candidate = Path.GetFullPath(Path.Combine(dir, sheetFile));
        if (!candidate.StartsWith(dir, StringComparison.Ordinal)) return null;
        return File.Exists(candidate) ? candidate : null;
    }

    public bool DeleteForItem(Guid itemId)
    {
        var dir = ItemDir(itemId);
        try
        {
            if (!Directory.Exists(dir)) return false;
            Directory.Delete(dir, recursive: true);
            _logger.LogDebug("Deleted trickplay for {ItemId}", itemId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete trickplay directory for {ItemId}", itemId);
            return false;
        }
    }

    public int CleanupOrphans(HashSet<Guid> validIds)
    {
        var deleted = 0;
        try
        {
            if (!Directory.Exists(_root)) return 0;

            foreach (var dir in Directory.GetDirectories(_root))
            {
                var name = Path.GetFileName(dir);
                try
                {
                    if (Guid.TryParseExact(name, "N", out var itemId))
                    {
                        if (validIds.Contains(itemId)) continue;
                        Directory.Delete(dir, recursive: true);
                        deleted++;
                        _logger.LogDebug("Deleted orphaned trickplay: {Path}", dir);
                    }
                    else if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                             && Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-1))
                    {
                        // Staging dir from a crashed generation; a LIVE generation's tmp dir
                        // is hours old at most, so the day-old guard can't race it.
                        Directory.Delete(dir, recursive: true);
                        deleted++;
                    }
                    // Anything else unrecognised is left alone — this directory only ever
                    // holds our own layout, so err on the side of not deleting.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process trickplay directory during cleanup: {Path}", dir);
                }
            }

            if (deleted > 0)
            {
                _logger.LogInformation("Trickplay cleanup removed {Count} orphaned director(ies)", deleted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trickplay orphan cleanup failed");
        }
        return deleted;
    }

    /// SR-WI-028: manifest sheet order must be NUMERIC on the index in "sheet-N.jpg" —
    /// the old ordinal string sort put "sheet-10.jpg" before "sheet-2.jpg", scrambling
    /// scrub previews for anything long enough for ≥11 sheets (~2h47m at the default
    /// cadence). On-disk names are untouched (installed servers already have
    /// sheet-N.jpg files); only the listing order is fixed. The client
    /// (useTrickplay.ts) indexes the `sheets` array positionally, so a correctly
    /// ordered list is sufficient. Unparseable names sort last, tie-broken ordinal.
    /// Public so tests can drive it directly (project convention; no InternalsVisibleTo).
    public static List<string> SortSheets(IEnumerable<string> fileNames) =>
        fileNames.OrderBy(SheetIndex).ThenBy(f => f, StringComparer.Ordinal).ToList();

    private static int SheetIndex(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var dash = stem.LastIndexOf('-');
        return dash >= 0 && int.TryParse(stem.AsSpan(dash + 1), out var n) ? n : int.MaxValue;
    }

    public async Task<TrickplayGenerationResult> GenerateAsync(Guid itemId, string sourcePath, CancellationToken ct)
    {
        if (HasTrickplay(itemId)) return new TrickplayGenerationResult(true, 0, 0);
        if (!File.Exists(sourcePath))
        {
            _logger.LogWarning("Trickplay: source missing for {ItemId}: {Path}", itemId, sourcePath);
            return new TrickplayGenerationResult(false, 0, 0);
        }

        var interval = Math.Max(1, await _settings.GetSettingAsync("TrickplayIntervalSeconds", 10));
        var width = Math.Clamp(await _settings.GetSettingAsync("TrickplayThumbnailWidth", 320), 120, 640);
        var height = (int)Math.Round(width * 9.0 / 16.0); // fixed 16:9 tile geometry for deterministic mapping

        var dir = ItemDir(itemId);
        var tmpDir = dir + ".tmp";
        double cpuTotal = 0, wallTotal = 0;
        try
        {
            var ffmpeg = _binaryLocation.ResolveFFmpegPath();
            var sheetPattern = Path.Combine(tmpDir, "sheet-%d.jpg");
            // fps=1/interval samples one frame per `interval` seconds; tile packs them
            // into a Columns x Rows grid per output image; -start_number 0 → sheet-0.jpg.
            var vf = $"fps=1/{interval},scale={width}:{height},tile={Columns}x{Rows}";

            // BG-WI-001: decode keyframes only on the first attempt — measured ~110x
            // cheaper than a full decode on real content, and the fps filter duplicates
            // sparse keyframes to hold the tile cadence, so partial starvation cannot
            // occur. The only real failure modes are a nonzero exit or zero sheets;
            // those fall back to one full-decode attempt. `-threads 2` applies to both:
            // unbounded frame-threading multiplied total CPU ~9x at high decode speed.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var keyframesOnly = attempt == 0;
                if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
                Directory.CreateDirectory(tmpDir);

                var inputFlags = keyframesOnly ? "-threads 2 -skip_frame nokey" : "-threads 2";
                var args = $"-y {inputFlags} -i \"{sourcePath}\" -vf \"{vf}\" -an -start_number 0 -q:v 5 \"{sheetPattern}\"";

                var run = await RunFfmpegAsync(ffmpeg, args, itemId, keyframesOnly, ct);
                cpuTotal += run.CpuSeconds;
                wallTotal += run.WallSeconds;

                if (run.Failed)
                {
                    // A start failure or timeout must not retry: the retry would double
                    // a 30-minute ceiling on a stuck source for no plausible gain.
                    return new TrickplayGenerationResult(false, cpuTotal, wallTotal);
                }

                if (run.ExitCode != 0)
                {
                    if (keyframesOnly)
                    {
                        _logger.LogWarning("Trickplay keyframe-only FFmpeg exit {Code} for {ItemId}; retrying with full decode: {Err}",
                            run.ExitCode, itemId, run.StderrTail);
                        continue;
                    }
                    _logger.LogWarning("Trickplay FFmpeg exit {Code} for {ItemId}: {Err}",
                        run.ExitCode, itemId, run.StderrTail);
                    return new TrickplayGenerationResult(false, cpuTotal, wallTotal);
                }

                var sheets = SortSheets(Directory.GetFiles(tmpDir, "sheet-*.jpg")
                    .Select(Path.GetFileName)
                    .Where(f => f != null)
                    .Select(f => f!));
                if (sheets.Count == 0)
                {
                    if (keyframesOnly)
                    {
                        _logger.LogWarning("Trickplay keyframe-only decode produced no sheets for {ItemId}; retrying with full decode", itemId);
                        continue;
                    }
                    _logger.LogWarning("Trickplay produced no sheets for {ItemId}", itemId);
                    return new TrickplayGenerationResult(false, cpuTotal, wallTotal);
                }

                var manifest = new
                {
                    version = 1,
                    interval,
                    tileWidth = width,
                    tileHeight = height,
                    columns = Columns,
                    rows = Rows,
                    tilesPerSheet = TilesPerSheet,
                    sheets,
                };
                await File.WriteAllTextAsync(Path.Combine(tmpDir, "manifest.json"),
                    JsonSerializer.Serialize(manifest), ct);

                // Atomic publish: swap tmp dir into place.
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                Directory.Move(tmpDir, dir);

                _logger.LogInformation("Trickplay generated for {ItemId}: {Sheets} sheet(s)", itemId, sheets.Count);
                return new TrickplayGenerationResult(true, cpuTotal, wallTotal);
            }

            return new TrickplayGenerationResult(false, cpuTotal, wallTotal); // both attempts exhausted
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trickplay generation failed for {ItemId}", itemId);
            return new TrickplayGenerationResult(false, cpuTotal, wallTotal);
        }
        finally
        {
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    /// <summary>Outcome of a single ffmpeg attempt. <see cref="Failed"/> covers the
    /// non-retryable cases (start failure, timeout); a nonzero <see cref="ExitCode"/>
    /// is retryable by the caller.</summary>
    private sealed record FfmpegRun(bool Failed, int ExitCode, double CpuSeconds, double WallSeconds, string? StderrTail);

    private async Task<FfmpegRun> RunFfmpegAsync(string ffmpeg, string args, Guid itemId, bool keyframesOnly, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi);
        if (proc == null)
        {
            _logger.LogError("Trickplay: failed to start FFmpeg");
            return new FfmpegRun(Failed: true, 0, 0, 0, null);
        }

        // BG-WI-002: background decode must always lose scheduling contests against
        // live playback/transcodes. Best-effort — the process may already have exited.
        try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch { /* exited or not permitted; priority is a safety net, not a contract */ }

        var stderrTask = proc.StandardError.ReadToEndAsync();

        // SR-WI-028: WaitForExitAsync throws on cancellation WITHOUT killing the
        // child (and Dispose doesn't either), which leaked a full-speed FFmpeg;
        // a stuck source also had no ceiling at all. Kill the whole process tree
        // on cancellation or timeout.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(GenerationTimeout);
        var timedOut = false;
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout (not caller cancellation): log and fail this item instead of
            // surfacing a cancellation the caller never requested.
            timedOut = true;
            _logger.LogWarning("Trickplay FFmpeg timed out after {Timeout} for {ItemId}; killing process tree",
                GenerationTimeout, itemId);
        }
        finally
        {
            if (!proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* exited between check and kill */ }
            }
        }

        sw.Stop();
        double cpu = 0;
        int exitCode = -1;
        try { proc.WaitForExit(); cpu = proc.TotalProcessorTime.TotalSeconds; exitCode = proc.ExitCode; }
        catch { /* process handle unusable — keep defaults */ }

        // BG-WI-004: per-spawn cost line so a future "100% CPU" report is attributable
        // from logs alone (the 2026-07-24 investigation had to reconstruct this live).
        _logger.LogInformation("Trickplay ffmpeg: {ItemId} cpu={Cpu:F1}s wall={Wall:F1}s exit={Exit} keyframesOnly={KeyframesOnly}{TimedOut}",
            itemId, cpu, sw.Elapsed.TotalSeconds, exitCode, keyframesOnly, timedOut ? " TIMED OUT" : "");

        if (timedOut) return new FfmpegRun(Failed: true, exitCode, cpu, sw.Elapsed.TotalSeconds, null);

        string? stderrTail = null;
        try { stderrTail = (await stderrTask).Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim(); }
        catch { /* stderr unavailable */ }

        return new FfmpegRun(Failed: false, exitCode, cpu, sw.Elapsed.TotalSeconds, stderrTail);
    }
}
