using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Metadata;

public class MetadataEnrichmentPolicyTests
{
    [Fact]
    public void NeedsEnrichment_ReturnsFalse_WhenRetryExhausted()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            IsRetryExhausted = true,
            Title = "Test"
        };

        var result = MetadataEnrichmentPolicy.NeedsEnrichment(item);
        Assert.False(result);
    }

    [Fact]
    public void NeedsEnrichment_ReturnsTrue_WhenNoPosterAndNoHash()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            MetadataHash = null,
            PosterUrl = null
        };

        Assert.True(MetadataEnrichmentPolicy.NeedsEnrichment(item));
    }

    [Fact]
    public void NeedsEnrichment_ReturnsFalse_WhenPosterHasValidUrl()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            PosterUrl = "http://example.com/poster.jpg"
        };

        Assert.False(MetadataEnrichmentPolicy.NeedsEnrichment(item));
    }

    [Fact]
    public void NeedsEnrichment_ReturnsFalse_WhenNoPosterButAttemptStamped()
    {
        // SM-WI-041: an enrichment pass RAN (hash stamped) and the provider had no
        // image — relaxed mode treats that as complete. The old `!hasPoster` contract
        // re-enqueued such items on every scan forever (identical imageless answer).
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            MetadataHash = "hash123",
            PosterUrl = null
        };

        Assert.False(MetadataEnrichmentPolicy.NeedsEnrichment(item));
    }

    [Fact]
    public void NeedsEnrichment_ReturnsTrue_WhenNoPosterAndNeverAttempted()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            MetadataHash = null,
            PosterUrl = null
        };

        Assert.True(MetadataEnrichmentPolicy.NeedsEnrichment(item));
    }

    [Fact]
    public void NeedsEnrichment_ReturnsFalse_WhenRetryExhaustedAndNoPoster()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            IsRetryExhausted = true,
            PosterUrl = null
        };

        Assert.False(MetadataEnrichmentPolicy.NeedsEnrichment(item));
    }

    // ---- Strict Mode Tests ----

    [Fact]
    public void NeedsEnrichment_Relaxed_ReturnsFalse_ForMovieWithPosterButNoDescription()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Type = MediaType.Movie,
            PosterUrl = "http://example.com/poster.jpg"
        };

        Assert.False(MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode: false));
    }

    [Fact]
    public void NeedsEnrichment_Strict_ReturnsTrue_ForMovieWithPosterButNoDescription()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Type = MediaType.Movie,
            PosterUrl = "http://example.com/poster.jpg",
            Overview = null
        };

        Assert.True(MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode: true));
    }

    [Fact]
    public void NeedsEnrichment_Strict_ReturnsFalse_ForMovieWithPosterAndDescription()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Type = MediaType.Movie,
            PosterUrl = "http://example.com/poster.jpg",
            Overview = "A great movie."
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
            Title = "Album"
        };

        Assert.False(MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode: true));
    }

    [Fact]
    public void NeedsEnrichment_Strict_ReturnsTrue_ForAlbumWithNoPosterNoCoverArt()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Type = MediaType.Album,
            Title = "Album"
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
            Title = "Artist Name"
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
            PosterUrl = "http://example.com/cover.jpg",
            Director = null // Author
        };

        Assert.True(MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode: true));
    }

    [Fact]
    public void NeedsEnrichment_Strict_ReturnsFalse_ForBookWithPosterAndAuthor()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Type = MediaType.Book,
            PosterUrl = "http://example.com/cover.jpg",
            Director = "J.R.R. Tolkien"
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
            PosterUrl = "http://example.com/cover.jpg",
            Studio = "Penguin Random House"
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
            Title = "Movie without poster"
        };

        Assert.True(MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode: false));
    }

    [Fact]
    public void NeedsEnrichment_StillReturnsFalse_WhenRetryExhausted_InStrictMode()
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            Type = MediaType.Movie,
            IsRetryExhausted = true,
            PosterUrl = "http://example.com/poster.jpg"
        };

        Assert.False(MetadataEnrichmentPolicy.NeedsEnrichment(item, strictMode: true));
    }
}
