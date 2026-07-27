using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using Xunit;

namespace SoftMedia.Server.Tests.DTOs;

/// <summary>
/// The book detail page renders author, publisher, ISBN and pages. Every one of those
/// values lived on a promoted column and none of them reached the wire, so the page showed
/// "Unknown" for all four regardless of how much the scanner and OpenLibrary had found.
/// These tests pin the DTO end of that path — the deliberately typed properties, NOT the
/// frozen <see cref="MediaItemDto.Metadata"/> bag (see MediaItemDtoMetadataContractTests).
/// </summary>
public class MediaItemDtoBookFieldsTests
{
    private static MediaItem NewBook() => new()
    {
        Id = Guid.NewGuid(),
        LibraryId = Guid.NewGuid(),
        Title = "The Shining",
        SortTitle = "Shining, The",
        Path = "/books/the-shining.epub",
        DateAdded = DateTime.UtcNow,
        Type = MediaType.Book,
        Director = "Stephen King",
        Studio = "Doubleday",
        Isbn = "9780385121675",
        PageCount = 447,
        Year = 1977
    };

    [Fact]
    public void FromMediaItem_ExposesAuthorPublisherIsbnAndPageCount()
    {
        var dto = MediaItemDto.FromMediaItem(NewBook());

        Assert.Equal("Stephen King", dto.Director);   // author
        Assert.Equal("Doubleday", dto.Studio);        // publisher
        Assert.Equal("9780385121675", dto.Isbn);
        Assert.Equal(447, dto.PageCount);
        Assert.Equal(1977, dto.Year);
    }

    [Fact]
    public void FromMediaItem_DoesNotDuplicateBookFieldsIntoTheFrozenMetadataBag()
    {
        // The bag is a frozen contract. Book fields are typed properties precisely so the
        // same data doesn't gain a second, untyped representation on the wire.
        var dto = MediaItemDto.FromMediaItem(NewBook());

        Assert.Null(dto.Metadata);
    }

    [Fact]
    public void FromMediaItem_LeavesBookFieldsNullWhenNothingWasExtracted()
    {
        // An unenriched book with no embedded metadata must produce nulls, not empty
        // strings or zeros — the client hides absent fields rather than printing "Unknown".
        var bare = NewBook();
        bare.Director = null;
        bare.Studio = null;
        bare.Isbn = null;
        bare.PageCount = null;

        var dto = MediaItemDto.FromMediaItem(bare);

        Assert.Null(dto.Director);
        Assert.Null(dto.Studio);
        Assert.Null(dto.Isbn);
        Assert.Null(dto.PageCount);
    }

    [Fact]
    public void FromMediaItem_CarriesStudioAndDirectorForVideoToo()
    {
        // Books reuse the shared columns rather than owning parallel ones; this guards that
        // exposing them didn't quietly become book-only.
        var movie = NewBook();
        movie.Type = MediaType.Movie;
        movie.Director = "Stanley Kubrick";
        movie.Studio = "Warner Bros.";

        var dto = MediaItemDto.FromMediaItem(movie);

        Assert.Equal("Stanley Kubrick", dto.Director);
        Assert.Equal("Warner Bros.", dto.Studio);
    }
}
