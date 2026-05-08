using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Security.LibraryAccess;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Security;

/// Wave C — verifies that LibraryAccessFilterExtensions emits SQL Where
/// clauses that EF translates to WHERE column IN (...) without falling back
/// to client-side evaluation. Mirrors the structure of RatingFilterTests.
public class LibraryAccessFilterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Library _libA;
    private readonly Library _libB;
    private readonly Library _libC;

    public LibraryAccessFilterTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _libA = new Library { Id = Guid.NewGuid(), Name = "A", Type = LibraryType.Movie, Paths = new() { "/a" } };
        _libB = new Library { Id = Guid.NewGuid(), Name = "B", Type = LibraryType.Movie, Paths = new() { "/b" } };
        _libC = new Library { Id = Guid.NewGuid(), Name = "C", Type = LibraryType.TV, Paths = new() { "/c" } };

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
        ctx.Libraries.AddRange(_libA, _libB, _libC);

        ctx.MediaItems.AddRange(
            Movie(_libA, "A-1"),
            Movie(_libA, "A-2"),
            Movie(_libB, "B-1"),
            Movie(_libC, "C-1"));

        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private static MediaItem Movie(Library lib, string title) => new()
    {
        Id = Guid.NewGuid(),
        LibraryId = lib.Id,
        Title = title,
        SortTitle = title,
        Path = $"/{title}.mkv",
        Type = MediaType.Movie,
    };

    [Fact]
    public void Unrestricted_LeavesQueryUnchanged_OnLibraries()
    {
        using var ctx = new AppDbContext(_options);
        var all = ctx.Libraries.ApplyLibraryAccessFilter(LibraryAccess.Unrestricted).ToList();
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void Unrestricted_LeavesQueryUnchanged_OnMediaItems()
    {
        using var ctx = new AppDbContext(_options);
        var all = ctx.MediaItems.ApplyLibraryAccessFilter(LibraryAccess.Unrestricted).ToList();
        Assert.Equal(4, all.Count);
    }

    [Fact]
    public void AllowOnly_FiltersLibrariesToAllowList()
    {
        using var ctx = new AppDbContext(_options);
        var access = LibraryAccess.AllowOnly(new[] { _libA.Id });
        var libs = ctx.Libraries.ApplyLibraryAccessFilter(access).ToList();

        Assert.Single(libs);
        Assert.Equal(_libA.Id, libs[0].Id);
    }

    [Fact]
    public void AllowOnly_FiltersMediaItemsByLibraryId()
    {
        using var ctx = new AppDbContext(_options);
        var access = LibraryAccess.AllowOnly(new[] { _libA.Id });
        var items = ctx.MediaItems.ApplyLibraryAccessFilter(access).ToList();

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal(_libA.Id, i.LibraryId));
    }

    [Fact]
    public void AllowOnly_WithMultipleIds_IncludesAllListedLibraries()
    {
        using var ctx = new AppDbContext(_options);
        var access = LibraryAccess.AllowOnly(new[] { _libA.Id, _libC.Id });
        var items = ctx.MediaItems.ApplyLibraryAccessFilter(access).ToList();

        Assert.Equal(3, items.Count);
        Assert.DoesNotContain(items, i => i.LibraryId == _libB.Id);
    }

    [Fact]
    public void AllowOnly_EmptyList_BlocksEverything()
    {
        // An empty AllowOnly is degenerate (the resolver maps zero rows to
        // Unrestricted before reaching this layer), but the extension must
        // still behave deterministically: nothing matches an empty IN clause.
        using var ctx = new AppDbContext(_options);
        var access = LibraryAccess.AllowOnly(Array.Empty<Guid>());
        Assert.Empty(ctx.Libraries.ApplyLibraryAccessFilter(access).ToList());
        Assert.Empty(ctx.MediaItems.ApplyLibraryAccessFilter(access).ToList());
    }

    [Fact]
    public void AllowOnly_DistinctsDuplicateInputIds()
    {
        // The factory deduplicates; even an explicit duplicate still matches
        // the library exactly once.
        var access = LibraryAccess.AllowOnly(new[] { _libA.Id, _libA.Id });
        Assert.Single(access.AllowedLibraryIds);
    }
}
