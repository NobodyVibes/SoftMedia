using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Infrastructure;

/// Exercises the real BackupService against a file-backed SQLite database (the
/// SQLite online-backup API requires a real connection, not :memory:). Each test
/// owns an isolated temp DB file and temp backup directory, both cleaned up on dispose.
public class BackupServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _dbPath;
    private readonly string _backupDir;
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public BackupServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sm-backup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _dbPath = Path.Combine(_tempRoot, "softmedia.db");
        _backupDir = Path.Combine(_tempRoot, "backups");

        // A single open connection keeps the file-backed DB alive for the test.
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private (BackupService svc, AppDbContext db) Build()
    {
        var db = new AppDbContext(_options);
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetSettingAsync("Maintenance.BackupDirectory", It.IsAny<string>()))
                .ReturnsAsync(_backupDir);
        var svc = new BackupService(db, settings.Object, NullLogger<BackupService>.Instance);
        return (svc, db);
    }

    private void SeedUsers(int count)
    {
        using var ctx = new AppDbContext(_options);
        for (var i = 0; i < count; i++)
        {
            ctx.Users.Add(new User { Username = $"user{i}", PasswordHash = "x", FirstName = "F", LastName = "L" });
        }
        ctx.SaveChanges();
    }

    [Fact]
    public async Task CreateBackup_WritesZipWithDbAndManifest()
    {
        SeedUsers(3);
        var (svc, _) = Build();

        var info = await svc.CreateBackupAsync(CancellationToken.None);

        var zipPath = Path.Combine(_backupDir, info.Id + ".zip");
        Assert.True(File.Exists(zipPath));

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.NotNull(zip.GetEntry("softmedia.db"));
        Assert.NotNull(zip.GetEntry("manifest.json"));
    }

    [Fact]
    public async Task CreateBackup_ManifestSha256_MatchesDbEntryBytes()
    {
        SeedUsers(1);
        var (svc, _) = Build();
        var info = await svc.CreateBackupAsync(CancellationToken.None);

        using var zip = ZipFile.OpenRead(Path.Combine(_backupDir, info.Id + ".zip"));

        byte[] dbBytes;
        using (var s = zip.GetEntry("softmedia.db")!.Open())
        using (var ms = new MemoryStream())
        {
            await s.CopyToAsync(ms);
            dbBytes = ms.ToArray();
        }
        var expectedSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(dbBytes)).ToLowerInvariant();

        using var manifestStream = zip.GetEntry("manifest.json")!.Open();
        using var doc = await JsonDocument.ParseAsync(manifestStream);
        var dbFile = doc.RootElement.GetProperty("files").EnumerateArray()
            .First(f => f.GetProperty("path").GetString() == "softmedia.db");
        Assert.Equal(expectedSha, dbFile.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task BackupDb_IsValidSqlite_AndPreservesRowCount()
    {
        SeedUsers(5);
        var (svc, _) = Build();
        var info = await svc.CreateBackupAsync(CancellationToken.None);

        var extracted = Path.Combine(_tempRoot, "extracted.db");
        using (var zip = ZipFile.OpenRead(Path.Combine(_backupDir, info.Id + ".zip")))
            zip.GetEntry("softmedia.db")!.ExtractToFile(extracted, overwrite: true);

        await using var conn = new SqliteConnection($"Data Source={extracted};Mode=ReadOnly;Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Users;";
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        Assert.Equal(5, count);
    }

    [Fact]
    public async Task ListBackups_ReturnsCreatedBackups_NewestFirst()
    {
        var (svc, _) = Build();
        var first = await svc.CreateBackupAsync(CancellationToken.None);
        // Force a distinct second-resolution id (ids are HHmmss-stamped).
        await Task.Delay(1100);
        var second = await svc.CreateBackupAsync(CancellationToken.None);

        var list = await svc.ListBackupsAsync(CancellationToken.None);
        Assert.Equal(2, list.Count);
        Assert.Equal(second.Id, list[0].Id);
        Assert.Equal(first.Id, list[1].Id);
    }

    [Fact]
    public async Task Pin_MarksBackupPinned_AndProtectsFromList()
    {
        var (svc, _) = Build();
        var info = await svc.CreateBackupAsync(CancellationToken.None);

        Assert.True(await svc.SetPinnedAsync(info.Id, true, CancellationToken.None));
        var list = await svc.ListBackupsAsync(CancellationToken.None);
        Assert.True(list.Single().IsPinned);

        Assert.True(await svc.SetPinnedAsync(info.Id, false, CancellationToken.None));
        list = await svc.ListBackupsAsync(CancellationToken.None);
        Assert.False(list.Single().IsPinned);
    }

    [Fact]
    public async Task OpenBackup_RejectsPathTraversalIds()
    {
        var (svc, _) = Build();
        await svc.CreateBackupAsync(CancellationToken.None);

        Assert.Null(await svc.OpenBackupAsync("../../etc/passwd", CancellationToken.None));
        Assert.Null(await svc.OpenBackupAsync("..\\..\\windows\\system32\\config", CancellationToken.None));
    }

    [Fact]
    public async Task StageRestore_RoundTrips_StagesPendingFile()
    {
        SeedUsers(4);
        var (svc, _) = Build();
        var info = await svc.CreateBackupAsync(CancellationToken.None);

        await using var zipStream = (await svc.OpenBackupAsync(info.Id, CancellationToken.None))!;
        var result = await svc.StageRestoreAsync(zipStream, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.RestartRequired);
        // The pending file is staged next to the live DB resolved from the connection.
        Assert.True(File.Exists(_dbPath + ".restore-pending"));
    }

    [Fact]
    public async Task StageRestore_RejectsZipWithoutDatabase()
    {
        var (svc, _) = Build();

        var bogus = new MemoryStream();
        using (var zip = new ZipArchive(bogus, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = zip.CreateEntry("manifest.json");
            await using var s = e.Open();
            await JsonSerializer.SerializeAsync(s, new { schemaVersion = 1 });
        }
        bogus.Position = 0;

        var result = await svc.StageRestoreAsync(bogus, CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task StageRestore_RejectsNewerSchemaVersion()
    {
        var (svc, _) = Build();

        var future = new MemoryStream();
        using (var zip = new ZipArchive(future, ZipArchiveMode.Create, leaveOpen: true))
        {
            var m = zip.CreateEntry("manifest.json");
            await using (var s = m.Open())
                await JsonSerializer.SerializeAsync(s, new { schemaVersion = 999 });
            // include a db entry so the missing-file guard is not what trips
            var d = zip.CreateEntry("softmedia.db");
            await using var ds = d.Open();
            await ds.WriteAsync(new byte[] { 1, 2, 3 });
        }
        future.Position = 0;

        var result = await svc.StageRestoreAsync(future, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("newer", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Prune_DeletesUnpinnedBeyondRetention_KeepsPinned()
    {
        var (svc, _) = Build();

        // Create several backups with distinct ids.
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add((await svc.CreateBackupAsync(CancellationToken.None)).Id);
            await Task.Delay(1100);
        }

        // Pin the oldest, then prune to retain only 1 daily + 0 weekly.
        await svc.SetPinnedAsync(ids[0], true, CancellationToken.None);
        var deleted = await svc.PruneAsync(retentionDaily: 1, retentionWeekly: 0, CancellationToken.None);

        var remaining = await svc.ListBackupsAsync(CancellationToken.None);
        // Pinned oldest is always kept; newest is kept by retentionDaily=1.
        Assert.Contains(remaining, b => b.Id == ids[0] && b.IsPinned);
        Assert.Contains(remaining, b => b.Id == ids[2]);
        Assert.True(deleted >= 1);
    }
}
