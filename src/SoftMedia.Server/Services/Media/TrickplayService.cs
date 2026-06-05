using System.Diagnostics;
using System.Text.Json;
using SoftMedia.Server.Services.Infrastructure;

namespace SoftMedia.Server.Services.Media;

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

    /// Generates the sprite sheet(s) + manifest for the given source video. Returns
    /// false on failure (logged). Safe to call repeatedly — skips if already present.
    Task<bool> GenerateAsync(Guid itemId, string sourcePath, CancellationToken ct);

    /// Absolute path to the item's manifest.json, or null if absent.
    string? GetManifestPath(Guid itemId);

    /// Absolute path to a named sheet file inside the item's trickplay dir, with a
    /// path-traversal guard. Null if the name escapes the dir or the file is absent.
    string? GetSheetPath(Guid itemId, string sheetFile);
}

public class TrickplayService : ITrickplayService
{
    private const int Columns = 10;
    private const int Rows = 10;
    private const int TilesPerSheet = Columns * Rows;

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

    public async Task<bool> GenerateAsync(Guid itemId, string sourcePath, CancellationToken ct)
    {
        if (HasTrickplay(itemId)) return true;
        if (!File.Exists(sourcePath))
        {
            _logger.LogWarning("Trickplay: source missing for {ItemId}: {Path}", itemId, sourcePath);
            return false;
        }

        var interval = Math.Max(1, await _settings.GetSettingAsync("TrickplayIntervalSeconds", 10));
        var width = Math.Clamp(await _settings.GetSettingAsync("TrickplayThumbnailWidth", 320), 120, 640);
        var height = (int)Math.Round(width * 9.0 / 16.0); // fixed 16:9 tile geometry for deterministic mapping

        var dir = ItemDir(itemId);
        var tmpDir = dir + ".tmp";
        try
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
            Directory.CreateDirectory(tmpDir);

            var ffmpeg = _binaryLocation.ResolveFFmpegPath();
            var sheetPattern = Path.Combine(tmpDir, "sheet-%d.jpg");
            // fps=1/interval samples one frame per `interval` seconds; tile packs them
            // into a Columns x Rows grid per output image; -start_number 0 → sheet-0.jpg.
            var vf = $"fps=1/{interval},scale={width}:{height},tile={Columns}x{Rows}";
            var args = $"-y -i \"{sourcePath}\" -vf \"{vf}\" -an -start_number 0 -q:v 5 \"{sheetPattern}\"";

            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) { _logger.LogError("Trickplay: failed to start FFmpeg"); return false; }
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
            {
                _logger.LogWarning("Trickplay FFmpeg exit {Code} for {ItemId}: {Err}",
                    proc.ExitCode, itemId, (await stderrTask).Split('\n').LastOrDefault());
                return false;
            }

            var sheets = Directory.GetFiles(tmpDir, "sheet-*.jpg")
                .Select(Path.GetFileName)
                .Where(f => f != null)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
            if (sheets.Count == 0)
            {
                _logger.LogWarning("Trickplay produced no sheets for {ItemId}", itemId);
                return false;
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
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trickplay generation failed for {ItemId}", itemId);
            return false;
        }
        finally
        {
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }
}
