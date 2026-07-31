using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Scanning;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Scanning;

/// <summary>
/// SM-WI-050 — the scan's parallel/save unit is a bounded batch, not a directory. A flat
/// 10k-file folder used to be ONE work item (zero parallelism, one giant save whose
/// failure discarded the whole library's probe results).
/// </summary>
public class ScanBatchingTests
{
    private static (string Dir, List<FileDiscoveryResult> Files) Dir(string name, int count)
        => (name, Enumerable.Range(0, count)
            .Select(i => new FileDiscoveryResult($@"{name}\file{i:D4}.mkv", 1, DateTime.UtcNow))
            .ToList());

    [Fact]
    public void FlatDirectory_SplitsIntoBoundedBatches()
    {
        var batches = BaseMediaScanner.BuildScanBatches(new[] { Dir(@"D:\Movies", 250) });

        Assert.Equal(3, batches.Count);
        Assert.All(batches, b => Assert.True(b.Count <= 100));
        Assert.Equal(250, batches.Sum(b => b.Count));
    }

    [Fact]
    public void SmallDirectories_PackTogether_WithoutInterleaving()
    {
        var batches = BaseMediaScanner.BuildScanBatches(new[]
        {
            Dir(@"D:\TV\Futurama", 40),
            Dir(@"D:\TV\Disenchantment", 30),
            Dir(@"D:\TV\Arcane", 40),
        });

        // 40+30 pack into one batch; adding 40 more would exceed 100 → second batch.
        Assert.Equal(2, batches.Count);
        Assert.Equal(70, batches[0].Count);
        Assert.Equal(40, batches[1].Count);

        // Directory locality: within a batch, a directory's files are contiguous.
        foreach (var batch in batches)
        {
            var dirSequence = batch.Select(f => Path.GetDirectoryName(f.Path)!).ToList();
            var runs = dirSequence.Where((d, i) => i == 0 || d != dirSequence[i - 1]).ToList();
            Assert.Equal(runs.Distinct().Count(), runs.Count); // no dir appears in 2 separate runs
        }
    }

    [Fact]
    public void EveryDiscoveredFile_LandsInExactlyOneBatch()
    {
        var discovered = new[] { Dir(@"D:\A", 150), Dir(@"D:\B", 1), Dir(@"D:\C", 99) };

        var batches = BaseMediaScanner.BuildScanBatches(discovered);

        var all = batches.SelectMany(b => b).Select(f => f.Path).ToList();
        Assert.Equal(250, all.Count);
        Assert.Equal(250, all.Distinct().Count());
    }
}

/// <summary>
/// SM-WI-052 — the lower("Path") expression index must actually serve the
/// case-insensitive path lookups (EXPLAIN QUERY PLAN check against real SQLite; the
/// plain Path index cannot serve `lower("Path") = @p`).
/// </summary>
public class PathLowerIndexTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public PathLowerIndexTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
        // EnsureCreated builds from the model; the expression index lives in raw
        // migration SQL — apply the same statement the migration runs.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """CREATE INDEX IF NOT EXISTS "IX_MediaItems_Path_Lower" ON "MediaItems" (lower("Path"));""";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task LowercasePathLookup_UsesTheExpressionIndex()
    {
        using (var ctx = new AppDbContext(_options))
        {
            // Real SQLite enforces the LibraryId FK (InMemory never did).
            var library = new Library { Id = Guid.NewGuid(), Name = "Movies", Type = LibraryType.Movie };
            ctx.Libraries.Add(library);
            ctx.MediaItems.Add(new MediaItem
            {
                Title = "small soldiers",
                Path = @"C:\Movies\small.soldiers.1998.1080p.bluray.x264-veto.mkv",
                Type = MediaType.Movie,
                LibraryId = library.Id,
            });
            await ctx.SaveChangesAsync();
        }

        // The exact shape the watcher/single-file paths produce.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """EXPLAIN QUERY PLAN SELECT * FROM "MediaItems" WHERE lower("Path") = @p;""";
        cmd.Parameters.AddWithValue("@p", @"c:\movies\small.soldiers.1998.1080p.bluray.x264-veto.mkv");
        using var reader = await cmd.ExecuteReaderAsync();
        var plan = "";
        while (await reader.ReadAsync()) plan += reader.GetString(3) + "\n";

        Assert.Contains("IX_MediaItems_Path_Lower", plan); // index hit, not a table SCAN
    }

    public void Dispose() => _connection.Dispose();
}
