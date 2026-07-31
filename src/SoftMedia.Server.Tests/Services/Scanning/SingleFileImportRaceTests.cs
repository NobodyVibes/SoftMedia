using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Metadata;
using SoftMedia.Server.Services.Scanning;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Scanning;

/// <summary>
/// SM-WI-061 — two concurrent single-file imports of the SAME new path (the watcher's
/// 3-permit semaphore allows this, and a running scan is a third possible writer) must
/// yield exactly ONE row. Before the in-lock re-check, both passed the existence lookup
/// and both inserted; the next scan purged one twin along with its user data.
/// Whatever the interleaving, the invariant is: one row survives.
/// </summary>
public class SingleFileImportRaceTests
{
    [Fact]
    public async Task ConcurrentImportsOfSameNewFile_ProduceExactlyOneRow()
    {
        var dbName = $"race-{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var scanner = new TestMediaScanner(
            scopeFactory,
            NullLogger<TestMediaScanner>.Instance,
            new Mock<IMediaNotificationService>().Object,
            new Mock<IMetadataQueue>().Object)
        {
            CreateRealItems = true,
            SimulateWorkDelayMs = 100, // widen the check→insert window so both usually pass the lookup
        };

        var library = new Library { Id = Guid.NewGuid(), Name = "Movies", Type = LibraryType.Movie };
        const string path = @"X:\watched\small.soldiers.1998.1080p.bluray.x264-veto.mkv";

        await Task.WhenAll(
            Task.Run(() => scanner.ProcessSingleFileAsync(path, library)),
            Task.Run(() => scanner.ProcessSingleFileAsync(path, library)));

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.MediaItems.Where(m => m.LibraryId == library.Id).ToListAsync();
        Assert.Single(rows); // one import won; the loser was discarded, not inserted
    }
}
