using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Metadata;
using SoftMedia.Server.Services.Scanning;
using Xunit;

namespace SoftMedia.Server.Tests.Controllers;

/// <summary>
/// DV-WI-011/012 — the version-group admin surface: merge joins items into one group
/// (guarding cross-library and mixed-type requests), split moves one item to a fresh
/// group (clearing its preference claim), and the duplicates report lists exactly the
/// groups with more than one member. Runs on REAL SQLite because the report's
/// GroupBy/HAVING shape must actually translate (EF InMemory would hide a failure).
/// </summary>
public class AdminControllerVersionsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _libraryId = Guid.NewGuid();

    public AdminControllerVersionsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
        ctx.Libraries.Add(new Library { Id = _libraryId, Name = "Movies", Type = LibraryType.Movie });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private AppDbContext NewContext() => new(_options);

    private static AdminController BuildController() => new(
        new LibraryWatcher(new Mock<IServiceScopeFactory>().Object, NullLogger<LibraryWatcher>.Instance),
        NullLogger<AdminController>.Instance,
        new List<IMetadataProvider>(),
        Mock.Of<IRecommendationService>(),
        Mock.Of<IBackupService>(),
        new ScheduledTaskRegistry(),
        Array.Empty<IManuallyTriggerableTask>())
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
    };

    private MediaItem AddItem(AppDbContext ctx, string title, MediaType type = MediaType.Movie,
        Guid? groupId = null, Guid? libraryId = null, int? height = null, bool preferred = false)
    {
        var m = new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = libraryId ?? _libraryId, Type = type, Title = title,
            VersionGroupId = groupId, Height = height, PreferredVersion = preferred, Size = 1000,
            Path = $"/x/{title}-{Guid.NewGuid():N}.mkv",
        };
        ctx.MediaItems.Add(m);
        ctx.SaveChanges();
        return m;
    }

    [Fact]
    public async Task Merge_JoinsItemsIntoOneGroup_PreferringAnExistingGroupId()
    {
        using var ctx = NewContext();
        var existingGroup = Guid.NewGuid();
        var a = AddItem(ctx, "Movie A", groupId: existingGroup);
        var b = AddItem(ctx, "Movie A copy");

        var result = await BuildController().MergeVersions(
            new MergeVersionsRequest(new List<Guid> { a.Id, b.Id }), ctx, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        using var verify = NewContext();
        Assert.Equal(existingGroup, verify.MediaItems.Find(a.Id)!.VersionGroupId);
        Assert.Equal(existingGroup, verify.MediaItems.Find(b.Id)!.VersionGroupId);
    }

    [Fact]
    public async Task Merge_RejectsCrossLibrary_MixedTypes_AndTooFewItems()
    {
        using var ctx = NewContext();
        var otherLib = Guid.NewGuid();
        ctx.Libraries.Add(new Library { Id = otherLib, Name = "B", Type = LibraryType.Movie });
        ctx.SaveChanges();
        var movie = AddItem(ctx, "Movie");
        var foreign = AddItem(ctx, "Movie", libraryId: otherLib);
        var episode = AddItem(ctx, "Ep", MediaType.Episode);
        var controller = BuildController();

        Assert.IsType<BadRequestObjectResult>(await controller.MergeVersions(
            new MergeVersionsRequest(new List<Guid> { movie.Id }), ctx, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.MergeVersions(
            new MergeVersionsRequest(new List<Guid> { movie.Id, foreign.Id }), ctx, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.MergeVersions(
            new MergeVersionsRequest(new List<Guid> { movie.Id, episode.Id }), ctx, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.MergeVersions(
            new MergeVersionsRequest(new List<Guid> { movie.Id, Guid.NewGuid() }), ctx, CancellationToken.None));
    }

    [Fact]
    public async Task Split_MovesItemToFreshGroup_AndClearsPreference()
    {
        using var ctx = NewContext();
        var shared = Guid.NewGuid();
        var keep = AddItem(ctx, "Movie", groupId: shared);
        var split = AddItem(ctx, "Movie", groupId: shared, preferred: true);

        var result = await BuildController().SplitVersion(
            new SplitVersionRequest(split.Id), ctx, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        using var verify = NewContext();
        var splitRow = verify.MediaItems.Find(split.Id)!;
        Assert.NotEqual(shared, splitRow.VersionGroupId);
        Assert.NotNull(splitRow.VersionGroupId);
        Assert.False(splitRow.PreferredVersion);
        Assert.Equal(shared, verify.MediaItems.Find(keep.Id)!.VersionGroupId);
    }

    [Fact]
    public async Task Prefer_PinsOneCopy_AndClearsTheSiblingsClaim()
    {
        using var ctx = NewContext();
        var group = Guid.NewGuid();
        var previous = AddItem(ctx, "Tenet", groupId: group, preferred: true);
        var next = AddItem(ctx, "Tenet", groupId: group);
        var controller = BuildController();

        var result = await controller.SetPreferredVersion(
            new PreferVersionRequest(next.Id, Preferred: true), ctx, CancellationToken.None);

        Assert.IsType<OkResult>(result);
        using var verify = NewContext();
        Assert.True(verify.MediaItems.Find(next.Id)!.PreferredVersion);
        Assert.False(verify.MediaItems.Find(previous.Id)!.PreferredVersion); // at most one claim per group

        // Clearing works too and never touches the sibling.
        await controller.SetPreferredVersion(new PreferVersionRequest(next.Id, Preferred: false), ctx, CancellationToken.None);
        using var verify2 = NewContext();
        Assert.False(verify2.MediaItems.Find(next.Id)!.PreferredVersion);
    }

    [Fact]
    public async Task DuplicatesReport_ListsOnlyMultiMemberGroups_WithLabelsAndWatchedCounts()
    {
        var userId = Guid.NewGuid();
        var dupGroup = Guid.NewGuid();
        using (var seed = NewContext())
        {
            seed.Users.Add(new User
            {
                Id = userId, Username = "u", PasswordHash = "x", Role = UserRole.User,
                IsApproved = true, CreatedAt = DateTime.UtcNow, FirstName = "T", LastName = "T", ContentRatings = "{}",
            });
            var hd = AddItem(seed, "Tenet", groupId: dupGroup, height: 1080);
            AddItem(seed, "Tenet", groupId: dupGroup, height: 2160);
            AddItem(seed, "Alone", groupId: Guid.NewGuid()); // singleton group — not a duplicate
            seed.UserMediaInteractions.Add(new UserMediaInteraction
            {
                UserId = userId, MediaItemId = hd.Id, IsWatched = true,
            });
            seed.SaveChanges();
        }

        using var ctx = NewContext();
        var result = await BuildController().GetDuplicateVersions(ctx, CancellationToken.None);

        var report = Assert.IsType<List<VersionGroupDto>>(((OkObjectResult)result.Result!).Value);
        var group = Assert.Single(report);
        Assert.Equal(dupGroup, group.VersionGroupId);
        Assert.Equal(2, group.Members.Count);
        Assert.Contains(group.Members, m => m.Label == "1080p" && m.WatchedByCount == 1);
        Assert.Contains(group.Members, m => m.Label == "4K" && m.WatchedByCount == 0);
    }
}
