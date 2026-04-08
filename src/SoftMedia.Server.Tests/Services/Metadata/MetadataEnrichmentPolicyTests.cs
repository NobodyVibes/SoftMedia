using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

public class MetadataEnrichmentPolicyTests
{
    [Fact]
    public void NeedsEnrichment_ReturnsFalse_WhenRetryExhausted()
    {
        // Arrange — IsRetryExhausted column is set by MetadataRetryService after max retries
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            IsRetryExhausted = true,
            MetadataJson = """{"title":"Test"}"""
        };

        // Act
        var result = MetadataEnrichmentPolicy.NeedsEnrichment(item);

        // Assert — permanently failed items should never be re-queued
        Assert.False(result);
    }

    [Fact]
    public void NeedsEnrichment_ReturnsTrue_WhenMetadataJsonIsNull()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            MetadataJson = null
        };

        Assert.True(MetadataEnrichmentPolicy.NeedsEnrichment(item));
    }

    [Fact]
    public void NeedsEnrichment_ReturnsTrue_WhenMetadataJsonIsEmpty()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            MetadataJson = ""
        };

        Assert.True(MetadataEnrichmentPolicy.NeedsEnrichment(item));
    }

    [Fact]
    public void NeedsEnrichment_ReturnsTrue_WhenPosterIsNull()
    {
        // Arrange — poster key exists but value is null (provider returned null)
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            MetadataJson = """{"title":"Test","poster":null}"""
        };

        // Act & Assert — null poster means enrichment still needed
        Assert.True(MetadataEnrichmentPolicy.NeedsEnrichment(item));
    }

    [Fact]
    public void NeedsEnrichment_ReturnsFalse_WhenPosterHasValidUrl()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            MetadataJson = """{"title":"Test","poster":"http://example.com/poster.jpg"}"""
        };

        Assert.False(MetadataEnrichmentPolicy.NeedsEnrichment(item));
    }

    [Fact]
    public void NeedsEnrichment_ReturnsTrue_WhenNoPosterKey()
    {
        // Arrange — has metadata but no poster key at all
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            MetadataJson = """{"title":"Test","year":2024}"""
        };

        Assert.True(MetadataEnrichmentPolicy.NeedsEnrichment(item));
    }

    [Fact]
    public void NeedsEnrichment_ReturnsFalse_WhenRetryExhaustedAndNoPoster()
    {
        // Arrange — IsRetryExhausted takes priority even without poster
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            IsRetryExhausted = true,
            MetadataJson = """{}"""
        };

        Assert.False(MetadataEnrichmentPolicy.NeedsEnrichment(item));
    }

    [Fact]
    public void NeedsEnrichment_ReturnsTrue_WhenNotExhaustedAndIncomplete()
    {
        // Arrange — IsRetryExhausted is false, metadata is incomplete
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            IsRetryExhausted = false,
            MetadataJson = """{"title":"Test"}"""
        };

        Assert.True(MetadataEnrichmentPolicy.NeedsEnrichment(item));
    }

    // ---- Strict Mode Tests ----

    [Fact]
    public void NeedsEnrichment_Relaxed_ReturnsFalse_ForMovieWithPosterButNoDescription()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Type = MediaType.Movie,
            MetadataJson = """{"poster":"http://example.com/poster.jpg"}"""
        };

        // Relaxed: poster alone is sufficient
        Assert.False(MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode: false));
    }

    [Fact]
    public void NeedsEnrichment_Strict_ReturnsTrue_ForMovieWithPosterButNoDescription()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Type = MediaType.Movie,
            MetadataJson = """{"poster":"http://example.com/poster.jpg"}"""
        };

        // Strict: movie needs poster AND description
        Assert.True(MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode: true));
    }

    [Fact]
    public void NeedsEnrichment_Strict_ReturnsFalse_ForMovieWithPosterAndDescription()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Type = MediaType.Movie,
            MetadataJson = """{"poster":"http://example.com/poster.jpg","description":"A great movie."}"""
        };

        Assert.False(MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode: true));
    }

    [Fact]
    public void NeedsEnrichment_Strict_ReturnsFalse_ForAlbumWithCoverArtPath()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Type = MediaType.Album,
            CoverArtPath = "/music/artist/album/cover.jpg",
            MetadataJson = """{"title":"Album"}"""
        };

        // Album: poster OR CoverArtPath on disk is sufficient
        Assert.False(MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode: true));
    }

    [Fact]
    public void NeedsEnrichment_Strict_ReturnsTrue_ForAlbumWithNoPosterNoCoverArt()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Type = MediaType.Album,
            MetadataJson = """{"title":"Album"}"""
        };

        Assert.True(MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode: true));
    }

    [Fact]
    public void NeedsEnrichment_Strict_ReturnsFalse_ForArtistWithTitle()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Type = MediaType.Artist,
            MetadataJson = """{"title":"Artist Name"}"""
        };

        Assert.False(MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode: true));
    }

    [Fact]
    public void NeedsEnrichment_Strict_ReturnsTrue_ForBookWithPosterButNoAuthor()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Type = MediaType.Book,
            MetadataJson = """{"poster":"http://example.com/cover.jpg"}"""
        };

        // Book: needs poster AND (cast or publisher)
        Assert.True(MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode: true));
    }

    [Fact]
    public void NeedsEnrichment_Strict_ReturnsFalse_ForBookWithPosterAndAuthor()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Type = MediaType.Book,
            MetadataJson = """{"poster":"http://example.com/cover.jpg","cast":[{"name":"J.R.R. Tolkien"}]}"""
        };

        Assert.False(MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode: true));
    }

    [Fact]
    public void NeedsEnrichment_StrictMode_Book_ReturnsFalse_WhenPosterAndPublisherPresent()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Type = MediaType.Book,
            MetadataJson = """{"poster":"http://example.com/cover.jpg","publisher":"Penguin Random House"}"""
        };

        Assert.False(MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode: true));
    }

    [Fact]
    public void NeedsEnrichment_RelaxedMode_ReturnsTrue_WhenNoPoster()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Type = MediaType.Movie,
            MetadataJson = """{"title":"Movie without poster"}"""
        };

        Assert.True(MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode: false));
    }

    [Fact]
    public void NeedsEnrichment_StillReturnsFalse_WhenRetryExhausted_InStrictMode()
    {
        // IsRetryExhausted takes priority over strict mode requirements
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Type = MediaType.Movie,
            IsRetryExhausted = true,
            MetadataJson = """{"poster":"http://example.com/poster.jpg"}"""
        };

        Assert.False(MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode: true));
    }
}
