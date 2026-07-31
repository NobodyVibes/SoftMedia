using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// <summary>
/// The end-of-movie post-play recommendations. Load-bearing contracts: collection siblings come
/// FIRST and are rotated so the film released after the finished one leads (the marathon path);
/// finished movies never appear (same completion rule as the Continue Watching row); genre
/// matches fill the remainder and never duplicate collection members; and a movie the caller
/// cannot see answers null exactly like a nonexistent one.
/// </summary>
public class RecommendationServicePostPlayTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _libraryId = Guid.NewGuid();
    private readonly Guid _collectionId = Guid.NewGuid();

    // The Lord of the Rings-style trilogy, plus genre-mates outside the collection.
    private readonly MediaItem _film1;
    private readonly MediaItem _film2;
    private readonly MediaItem _film3;
    private readonly MediaItem _genreMate;
    private readonly MediaItem _unrelated;

    public RecommendationServicePostPlayTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();

        ctx.Users.Add(new User
        {
            Id = _userId, Username = "viewer", PasswordHash = "x", Role = UserRole.User,
            IsApproved = true, CreatedAt = DateTime.UtcNow, FirstName = "T", LastName = "T", ContentRatings = "{}",
        });
        ctx.Libraries.Add(new Library { Id = _libraryId, Name = "Movies", Type = LibraryType.Movie });
        ctx.Collections.Add(new Collection { Id = _collectionId, Name = "The Trilogy" });
        var action = new Genre { Name = "Action" };
        ctx.Genres.Add(action);

        _film1 = Movie("Film One", 2001, _collectionId);
        _film2 = Movie("Film Two", 2002, _collectionId);
        _film3 = Movie("Film Three", 2003, _collectionId);
        _genreMate = Movie("Genre Mate", 2010, collectionId: null);
        _unrelated = Movie("Unrelated", 2011, collectionId: null);
        ctx.MediaItems.AddRange(_film1, _film2, _film3, _genreMate, _unrelated);
        ctx.SaveChanges();

        foreach (var m in new[] { _film1, _film2, _film3, _genreMate })
        {
            ctx.MediaItemGenres.Add(new MediaItemGenre { MediaItemId = m.Id, GenreId = action.Id });
        }

        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private MediaItem Movie(string title, int year, Guid? collectionId)
        => new()
        {
            Id = Guid.NewGuid(), LibraryId = _libraryId, Type = MediaType.Movie,
            Title = title, Year = year, ReleaseDate = new DateTime(year, 6, 1),
            Duration = 6000, CollectionId = collectionId,
        };

    private RecommendationService Build(LibraryAccess? access = null)
    {
        var ctx = new AppDbContext(_options);
        var ratings = new Mock<IUserContentRatingProvider>();
        ratings.Setup(r => r.GetCurrentAsync()).ReturnsAsync(UserRatingCeilings.Unrestricted);
        var accessMock = new Mock<IUserLibraryAccessProvider>();
        accessMock.Setup(a => a.GetCurrentAsync()).ReturnsAsync(access ?? LibraryAccess.Unrestricted);

        return new RecommendationService(
            new MediaRepository(ctx, ratings.Object, accessMock.Object),
            new UserMediaInteractionRepository(ctx),
            ctx,
            accessMock.Object,
            ratings.Object,
            NullLogger<RecommendationService>.Instance);
    }

    private void MarkWatched(MediaItem movie)
    {
        using var ctx = new AppDbContext(_options);
        ctx.UserMediaInteractions.Add(new UserMediaInteraction
        {
            UserId = _userId, MediaItemId = movie.Id, IsWatched = true, LastPlayed = DateTime.UtcNow,
        });
        ctx.SaveChanges();
    }

    [Fact]
    public async Task Collection_siblings_lead_with_the_next_release_first()
    {
        // Finished the first film: the second must lead (marathon order), third follows,
        // and the genre-mate trails in the similar section.
        var result = await Build().GetMoviePostPlayAsync(_userId, _film1.Id);

        Assert.NotNull(result);
        Assert.Equal("The Trilogy", result!.CollectionName);
        Assert.Equal(new[] { _film2.Id, _film3.Id }, result.CollectionItems.Select(i => i.Id).ToArray());
        Assert.Contains(_genreMate.Id, result.SimilarItems.Select(i => i.Id));
        Assert.DoesNotContain(_film1.Id, result.CollectionItems.Concat(result.SimilarItems).Select(i => i.Id));
    }

    [Fact]
    public async Task Duplicate_collection_member_appears_once_in_the_marathon_list()
    {
        // DV-WI-016: a 4K copy of Film Two shares its version group — the marathon list
        // must offer Film Two ONCE, not once per file.
        var group = Guid.NewGuid();
        using (var ctx = new AppDbContext(_options))
        {
            var copy = Movie("Film Two", 2002, _collectionId);
            copy.VersionGroupId = group;
            ctx.MediaItems.Add(copy);
            ctx.MediaItems.Find(_film2.Id)!.VersionGroupId = group;
            ctx.SaveChanges();
        }

        var result = await Build().GetMoviePostPlayAsync(_userId, _film1.Id);

        Assert.NotNull(result);
        Assert.Single(result!.CollectionItems, i => i.Title == "Film Two");
    }

    [Fact]
    public async Task Middle_of_marathon_offers_the_later_film_before_an_unwatched_earlier_one()
    {
        // Finished the SECOND film without having seen the first: the third (next in release
        // order) leads; the skipped first film still appears, after it.
        var result = await Build().GetMoviePostPlayAsync(_userId, _film2.Id);

        Assert.NotNull(result);
        Assert.Equal(new[] { _film3.Id, _film1.Id }, result!.CollectionItems.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task Finished_movies_never_appear_in_either_section()
    {
        MarkWatched(_film2);
        MarkWatched(_genreMate);

        var result = await Build().GetMoviePostPlayAsync(_userId, _film1.Id);

        Assert.NotNull(result);
        Assert.Equal(new[] { _film3.Id }, result!.CollectionItems.Select(i => i.Id).ToArray());
        Assert.Empty(result.SimilarItems);
    }

    [Fact]
    public async Task Genre_section_excludes_collection_members_and_movies_without_shared_genres()
    {
        var result = await Build().GetMoviePostPlayAsync(_userId, _film1.Id);

        Assert.NotNull(result);
        var similarIds = result!.SimilarItems.Select(i => i.Id).ToList();
        Assert.DoesNotContain(_film2.Id, similarIds);
        Assert.DoesNotContain(_film3.Id, similarIds);
        Assert.DoesNotContain(_unrelated.Id, similarIds); // shares no genre
    }

    [Fact]
    public async Task Movie_hidden_by_library_acl_answers_null_like_a_nonexistent_one()
    {
        var noAccess = LibraryAccess.AllowOnly(new[] { Guid.NewGuid() });

        var blocked = await Build(noAccess).GetMoviePostPlayAsync(_userId, _film1.Id);
        var missing = await Build().GetMoviePostPlayAsync(_userId, Guid.NewGuid());

        Assert.Null(blocked);
        Assert.Null(missing);
    }
}
