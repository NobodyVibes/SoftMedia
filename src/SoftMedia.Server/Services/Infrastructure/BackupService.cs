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

    private readonly AppDbContext _db;
    private readonly ISettingsService _settings;
    private readonly ILogger<BackupService> _logger;

    public BackupService(AppDbContext db, ISettingsService settings, ILogger<BackupService> logger)
    {
        _db = db;
        _settings = settings;
        _logger = logger;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public async Task<BackupInfo> CreateBackupAsync(CancellationToken ct)
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

            // 2. appsettings.json (best-effort; may be absent in some deployments).
            byte[]? appSettingsBytes = null;
            var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(appSettingsPath))
                appSettingsBytes = await File.ReadAllBytesAsync(appSettingsPath, ct);

            var lastMigration = (await _db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault();

            // 3. Assemble the zip with a per-file SHA-256 manifest.
            await using (var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                var files = new List<object>();

                await AddEntryAsync(zip, DbEntryName, dbBytes, files, ct);
                if (appSettingsBytes != null)
                    await AddEntryAsync(zip, "appsettings.json", appSettingsBytes, files, ct);

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

        var info = new FileInfo(zipPath);
        _logger.LogInformation("Created backup {Id} ({Size} bytes)", id, info.Length);
        return new BackupInfo(id, info.CreationTimeUtc, info.Length, IsPinned(dir, id));
    }

    public async Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(CancellationToken ct)
    {
        var dir = await GetBackupDirectoryAsync();
        if (!Directory.Exists(dir)) return Array.Empty<BackupInfo>();

        return Directory.EnumerateFiles(dir, "*.zip")
            .Select(p => new FileInfo(p))
            .Select(f => new BackupInfo(
                Path.GetFileNameWithoutExtension(f.Name),
                f.CreationTimeUtc,
                f.Length,
                IsPinned(dir, Path.GetFileNameWithoutExtension(f.Name))))
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

                dbEntry.ExtractToFile(tempDb, overwrite: true);
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
                var marker = path + PinMarkerSuffix;
                if (File.Exists(marker)) File.Delete(marker);
                deleted++;
            }
        }
        if (deleted > 0) _logger.LogInformation("Backup rotation pruned {Count} backups.", deleted);
        return deleted;
    }

    // --- helpers ---

    private async Task<string> GetBackupDirectoryAsync()
    {
        var configured = await _settings.GetSettingAsync("Maintenance.BackupDirectory", "./data/backups");
        return Path.GetFullPath(configured, Directory.GetCurrentDirectory());
    }

    /// <summary>
    /// Resolves the live SQLite database path from the open connection's DataSource,
    /// NOT from appsettings — dev/prod may override the connection string via env or
    /// user-secrets (the dev DB is actually app.db, not the configured softmedia.db).
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
