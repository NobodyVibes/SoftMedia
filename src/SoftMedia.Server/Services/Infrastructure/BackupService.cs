using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Infrastructure;

/// <summary>
/// Produces and restores consistent SoftMedia backups.
///
/// The database snapshot uses SQLite's online-backup API
/// (<see cref="SqliteConnection.BackupDatabase(SqliteConnection)"/>), which copies
/// pages atomically and re-reads any pages dirtied during the copy. It is safe under
/// concurrent writers and under any journal mode (DELETE, WAL, MEMORY) — no
/// maintenance-mode flag is needed and a future reader should not add one.
///
/// Restore is NON-destructive in-process: the validated database is written to
/// "&lt;db&gt;.restore-pending" and swapped in on the next process start, before EF
/// opens the connection (<see cref="PendingRestore.Apply"/>). This avoids the
/// unreliable open-file replacement problem on Windows.
/// </summary>
public class BackupService : IBackupService
{
    private const int CurrentSchemaVersion = 1;
    private const string DbEntryName = "softmedia.db";
    private const string ManifestEntryName = "manifest.json";
    private const string PinMarkerSuffix = ".pinned";
    // Sidecar holding the editable display name, next to the archive (like the pin marker).
    // Keeps the archive id/filename immutable while the label can change freely.
    private const string NameMarkerSuffix = ".label";
    private const int MaxNameLength = 120;
    // Security (audit wave-2 L-20): upper bound on a restored database to stop a decompression-bomb
    // backup from filling the disk. Generous for a large home library; far below a bomb.
    private const long MaxRestoreDbBytes = 4L * 1024 * 1024 * 1024;

    private readonly AppDbContext _db;
    private readonly ISettingsService _settings;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<BackupService> _logger;

    public BackupService(AppDbContext db, ISettingsService settings, IWebHostEnvironment env, ILogger<BackupService> logger)
    {
        _db = db;
        _settings = settings;
        _env = env;
        _logger = logger;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public Task<BackupInfo> CreateBackupAsync(CancellationToken ct) => CreateBackupAsync(null, ct);

    public async Task<BackupInfo> CreateBackupAsync(string? name, CancellationToken ct)
    {
        var dir = await GetBackupDirectoryAsync();
        Directory.CreateDirectory(dir);

        var id = $"softmedia-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var zipPath = Path.Combine(dir, id + ".zip");

        // 1. Snapshot the live DB to a temp file via the online-backup API.
        var tempDb = Path.Combine(Path.GetTempPath(), $"sm-bak-{Guid.NewGuid():N}.db");
        try
        {
            var src = (SqliteConnection)_db.Database.GetDbConnection();
            if (src.State != System.Data.ConnectionState.Open)
                await src.OpenAsync(ct);

            // Pooling=False so the temp file handle is released on dispose — otherwise
            // Microsoft.Data.Sqlite's connection pool keeps it open and File.Delete fails.
            await using (var dst = new SqliteConnection($"Data Source={tempDb};Pooling=False"))
            {
                await dst.OpenAsync(ct);
                src.BackupDatabase(dst);
            }

            var dbBytes = await File.ReadAllBytesAsync(tempDb, ct);

            // Security (audit wave-2 M-8): do NOT bundle appsettings.json. It is never consumed on
            // restore (StageRestoreAsync only extracts the database), but it typically holds the JWT
            // signing secret — which is ALSO the root of the TOTP-secret AES key — and metadata
            // provider API keys, turning any leaked backup into a complete authentication-system
            // compromise on top of the password hashes already in the DB. Operators restore their
            // own config; the database is the only thing a backup needs to carry.

            var lastMigration = (await _db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault();

            // 3. Assemble the zip with a per-file SHA-256 manifest.
            await using (var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                var files = new List<object>();

                await AddEntryAsync(zip, DbEntryName, dbBytes, files, ct);

                var manifest = new
                {
                    softMediaVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
                    schemaVersion = CurrentSchemaVersion,
                    lastAppliedMigration = lastMigration,
                    takenAtUtc = DateTime.UtcNow,
                    files
                };
                var manifestEntry = zip.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                await using var ms = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(ms, manifest, JsonOpts, ct);
            }
        }
        finally
        {
            if (File.Exists(tempDb)) File.Delete(tempDb);
        }

        var label = NormalizeName(name);
        if (label != null)
            await File.WriteAllTextAsync(zipPath + NameMarkerSuffix, label, ct);

        var info = new FileInfo(zipPath);
        _logger.LogInformation("Created backup {Id} ({Size} bytes){Named}", id, info.Length,
            label != null ? $" named '{label}'" : "");
        return new BackupInfo(id, info.CreationTimeUtc, info.Length, IsPinned(dir, id), label ?? id);
    }

    public async Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(CancellationToken ct)
    {
        var dir = await GetBackupDirectoryAsync();
        if (!Directory.Exists(dir)) return Array.Empty<BackupInfo>();

        return Directory.EnumerateFiles(dir, "*.zip")
            .Select(p => new FileInfo(p))
            .Select(f =>
            {
                var id = Path.GetFileNameWithoutExtension(f.Name);
                return new BackupInfo(id, f.CreationTimeUtc, f.Length, IsPinned(dir, id), GetName(dir, id));
            })
            .OrderByDescending(b => b.CreatedAtUtc)
            .ToList();
    }

    public async Task<Stream?> OpenBackupAsync(string id, CancellationToken ct)
    {
        var dir = await GetBackupDirectoryAsync();
        var path = ResolveBackupPath(dir, id);
        if (path == null || !File.Exists(path)) return null;
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public async Task<bool> SetPinnedAsync(string id, bool pinned, CancellationToken ct)
    {
        var dir = await GetBackupDirectoryAsync();
        var path = ResolveBackupPath(dir, id);
        if (path == null || !File.Exists(path)) return false;

        var marker = path + PinMarkerSuffix;
        if (pinned)
        {
            if (!File.Exists(marker)) await File.WriteAllTextAsync(marker, DateTime.UtcNow.ToString("O"), ct);
        }
        else if (File.Exists(marker))
        {
            File.Delete(marker);
        }
        return true;
    }

    public async Task<bool> SetBackupNameAsync(string id, string? name, CancellationToken ct)
    {
        var dir = await GetBackupDirectoryAsync();
        var path = ResolveBackupPath(dir, id);
        if (path == null || !File.Exists(path)) return false;

        var marker = path + NameMarkerSuffix;
        var label = NormalizeName(name);
        if (label == null)
        {
            if (File.Exists(marker)) File.Delete(marker); // blank name reverts to the id
        }
        else
        {
            await File.WriteAllTextAsync(marker, label, ct);
        }
        _logger.LogInformation("Renamed backup {Id} to '{Name}'", id, label ?? id);
        return true;
    }

    public async Task<bool> DeleteBackupAsync(string id, CancellationToken ct)
    {
        var dir = await GetBackupDirectoryAsync();
        var path = ResolveBackupPath(dir, id);
        if (path == null || !File.Exists(path)) return false;

        File.Delete(path);
        foreach (var suffix in new[] { PinMarkerSuffix, NameMarkerSuffix })
        {
            var marker = path + suffix;
            if (File.Exists(marker)) File.Delete(marker);
        }
        _logger.LogWarning("Backup {Id} deleted.", id);
        return true;
    }

    public async Task<RestoreStageResult> StageRestoreAsync(Stream uploadedZip, CancellationToken ct)
    {
        // Buffer to a temp file so we can both validate and extract from a seekable copy.
        var tempZip = Path.Combine(Path.GetTempPath(), $"sm-restore-{Guid.NewGuid():N}.zip");
        var tempDb = Path.Combine(Path.GetTempPath(), $"sm-restore-{Guid.NewGuid():N}.db");
        try
        {
            await using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write))
                await uploadedZip.CopyToAsync(fs, ct);

            using (var zip = ZipFile.OpenRead(tempZip))
            {
                var manifestEntry = zip.GetEntry(ManifestEntryName);
                var dbEntry = zip.GetEntry(DbEntryName);
                if (manifestEntry == null || dbEntry == null)
                    return new RestoreStageResult(false, "Backup is missing manifest.json or the database file.", false);

                // Schema-version guard: refuse a backup newer than this server understands.
                using (var ms = manifestEntry.Open())
                {
                    using var doc = await JsonDocument.ParseAsync(ms, default, ct);
                    if (doc.RootElement.TryGetProperty("schemaVersion", out var sv) &&
                        sv.TryGetInt32(out var schemaVersion) &&
                        schemaVersion > CurrentSchemaVersion)
                    {
                        return new RestoreStageResult(false,
                            $"Backup schema version {schemaVersion} is newer than this server supports ({CurrentSchemaVersion}). Upgrade SoftMedia first.",
                            false);
                    }
                }

                // Security (audit wave-2 L-20): bound the extraction so a malicious/oversized
                // backup (decompression bomb) can't fill the disk during an admin restore. Guard
                // on the declared size AND on the actual decompressed bytes (a ratio bomb can lie
                // about Length), aborting past the cap.
                if (dbEntry.Length > MaxRestoreDbBytes)
                    return new RestoreStageResult(false,
                        $"Backup database is implausibly large ({dbEntry.Length / (1024 * 1024)} MB).", false);

                await using (var entryStream = dbEntry.Open())
                await using (var outStream = new FileStream(tempDb, FileMode.Create, FileAccess.Write))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await entryStream.ReadAsync(buffer, ct)) > 0)
                    {
                        total += read;
                        if (total > MaxRestoreDbBytes)
                            return new RestoreStageResult(false,
                                "Backup database exceeds the maximum restore size.", false);
                        await outStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                }
            }

            // Validate the extracted DB is a real SQLite database before staging.
            // Pooling=False so the temp file can be deleted in the finally block.
            await using (var probe = new SqliteConnection($"Data Source={tempDb};Mode=ReadOnly;Pooling=False"))
            {
                await probe.OpenAsync(ct);
                await using var cmd = probe.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
                var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
                if (count <= 0)
                    return new RestoreStageResult(false, "Backup database is empty or corrupt.", false);
            }

            // Stage: copy into place next to the live DB. Swap happens on next boot.
            var livePath = GetLiveDbPath();
            var pending = livePath + PendingRestore.PendingSuffix;
            File.Copy(tempDb, pending, overwrite: true);

            _logger.LogWarning("Restore staged to {Pending}; will apply on next server start.", pending);
            return new RestoreStageResult(true,
                "Restore staged. Restart the server to apply — the database will be swapped in before it opens.",
                true);
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
            if (File.Exists(tempDb)) File.Delete(tempDb);
        }
    }

    public async Task<int> PruneAsync(int retentionDaily, int retentionWeekly, CancellationToken ct)
    {
        var dir = await GetBackupDirectoryAsync();
        if (!Directory.Exists(dir)) return 0;

        var all = (await ListBackupsAsync(ct)).Where(b => !b.IsPinned).ToList();

        // Keep the newest `retentionDaily` overall, plus one per ISO week for the
        // newest `retentionWeekly` weeks. Everything else (unpinned) is pruned.
        var keep = new HashSet<string>(all.OrderByDescending(b => b.CreatedAtUtc)
            .Take(retentionDaily).Select(b => b.Id));

        foreach (var weekly in all
                     .GroupBy(b => System.Globalization.ISOWeek.GetWeekOfYear(b.CreatedAtUtc) + b.CreatedAtUtc.Year * 100)
                     .OrderByDescending(g => g.Key)
                     .Take(retentionWeekly)
                     .Select(g => g.OrderByDescending(b => b.CreatedAtUtc).First()))
        {
            keep.Add(weekly.Id);
        }

        var deleted = 0;
        foreach (var b in all.Where(b => !keep.Contains(b.Id)))
        {
            var path = ResolveBackupPath(dir, b.Id);
            if (path != null && File.Exists(path))
            {
                File.Delete(path);
                foreach (var suffix in new[] { PinMarkerSuffix, NameMarkerSuffix })
                {
                    var marker = path + suffix;
                    if (File.Exists(marker)) File.Delete(marker);
                }
                deleted++;
            }
        }
        if (deleted > 0) _logger.LogInformation("Backup rotation pruned {Count} backups.", deleted);
        return deleted;
    }

    // --- helpers ---

    private const string DefaultBackupDir = "./data/backups";

    /// <summary>
    /// SR-WI-065: anchors a relative path to the application CONTENT ROOT rather
    /// than the process CWD — a server launched from another directory (service
    /// manager, scheduled task) must not scatter backups or miss the live DB.
    /// Absolute paths pass through unchanged (Path.GetFullPath ignores the base).
    /// </summary>
    private string ResolveAgainstContentRoot(string path)
    {
        var root = !string.IsNullOrEmpty(_env.ContentRootPath)
            ? _env.ContentRootPath
            : Directory.GetCurrentDirectory();
        return Path.GetFullPath(path, root);
    }

    private async Task<string> GetBackupDirectoryAsync()
    {
        var configured = await _settings.GetSettingAsync("Maintenance.BackupDirectory", DefaultBackupDir);
        var resolved = ResolveAgainstContentRoot(configured);

        // Security (audit wave-2 L-20): refuse a backup directory that lives inside the web root.
        // Backups carry the full database (password hashes, encrypted TOTP secrets, recovery-code
        // hashes); a dir under wwwroot would make them anonymously web-downloadable via the static
        // file middleware. Fall back to the safe default rather than honour an unsafe location.
        var webRoot = !string.IsNullOrEmpty(_env.WebRootPath)
            ? _env.WebRootPath
            : ResolveAgainstContentRoot("wwwroot");
        var webRootFull = Path.GetFullPath(webRoot);
        if (!webRootFull.EndsWith(Path.DirectorySeparatorChar)) webRootFull += Path.DirectorySeparatorChar;
        if ((resolved + Path.DirectorySeparatorChar).StartsWith(webRootFull, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "Configured backup directory {Configured} is inside the web root — it would expose secret-bearing " +
                "backups for anonymous download. Falling back to {Default}.", resolved, DefaultBackupDir);
            return ResolveAgainstContentRoot(DefaultBackupDir);
        }

        return resolved;
    }

    /// <summary>
    /// Resolves the live SQLite database path from the open connection's DataSource,
    /// NOT from appsettings — dev/prod may override the connection string via env or
    /// user-secrets (the dev DB is actually app.db, not the configured softmedia.db).
    /// Deliberately CWD-anchored, NOT content-root-anchored (SR-WI-065 reviewed
    /// this): SQLite itself opens a relative Data Source against the process CWD,
    /// and Program.cs's boot-time PendingRestore.Apply resolves the same way — the
    /// staged ".restore-pending" file must land next to the file BOTH of them use,
    /// or a staged restore would silently never apply.
    /// </summary>
    private string GetLiveDbPath()
    {
        var conn = (SqliteConnection)_db.Database.GetDbConnection();
        var dataSource = conn.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource))
            throw new InvalidOperationException("Could not resolve the live database path from the connection.");
        return Path.GetFullPath(dataSource, Directory.GetCurrentDirectory());
    }

    private static bool IsPinned(string dir, string id)
        => File.Exists(Path.Combine(dir, id + ".zip" + PinMarkerSuffix));

    /// <summary>Reads the editable display name from the sidecar, falling back to the id.</summary>
    private static string GetName(string dir, string id)
    {
        var marker = Path.Combine(dir, id + ".zip" + NameMarkerSuffix);
        if (!File.Exists(marker)) return id;
        try
        {
            var label = NormalizeName(File.ReadAllText(marker));
            return label ?? id;
        }
        catch
        {
            return id;
        }
    }

    /// <summary>Trims, collapses to a single line, and caps a user-supplied name. Returns null if blank.</summary>
    private static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var collapsed = name.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        if (collapsed.Length == 0) return null;
        return collapsed.Length > MaxNameLength ? collapsed[..MaxNameLength] : collapsed;
    }

    /// <summary>
    /// Maps a backup id to its on-disk zip path, rejecting any id that would escape
    /// the backup directory (path-traversal guard).
    /// </summary>
    private static string? ResolveBackupPath(string dir, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Contains('/') || id.Contains('\\') || id.Contains("..")
            || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;

        var fullDir = Path.GetFullPath(dir);
        var candidate = Path.GetFullPath(Path.Combine(fullDir, id + ".zip"));
        return candidate.StartsWith(fullDir, StringComparison.Ordinal) ? candidate : null;
    }

    private static async Task AddEntryAsync(
        ZipArchive zip, string name, byte[] bytes, List<object> files, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using (var s = entry.Open())
            await s.WriteAsync(bytes, ct);

        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        files.Add(new { path = name, sha256 = sha, sizeBytes = bytes.LongLength });
    }
}
